using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 少女换装 / 看板娘域（<c>GirlLogic</c> 的 <c>SetMainGirl</c> / <c>ChangeCloth</c> /
/// <c>SetModelInFight</c>）。台账 `docs/05` §37。
///
/// 断言重点（都是「漏了就整域看起来没实现」的那几条）：
/// <list type="number">
///   <item>三条命令**都必须回包**（本族 <c>Blocking.Hide()</c> 在 Dispatcher 里 ⇒ 不回就卡遮罩）；</item>
///   <item><c>SetMainGirl</c> 要**先推 pf 1026 再回包**（客户端 handler 不读回包、重读任务值）；</item>
///   <item><c>ChangeCloth</c> 改的是 <c>GirlInfo.modelid</c> ⇒ 通道是 **pf 1030**，不是 1026；</item>
///   <item>换衣要校验 <c>SuitValue &gt; 0</c>，并把 NEW(1) 清成已穿(2)；</item>
///   <item><c>ModelId</c> 必须过 <c>FixModelId</c>（7001 → 1711）才能当任务值偏移；</item>
///   <item>登录钩子要把「已持有的皮肤道具/角色卡」对应服饰置 1，否则换装列表是空的。</item>
/// </list>
/// </summary>
public class GirlDressFeatureTests
{
    private static JsonObject Reply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(GirlDressFeature.Function, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    /// <summary>造一个少女（★ 复用已存在的同 id，避免 Girls 里出现两个同 girlid —— 见 GirlLoveFeatureTests.Seed 的说明）。</summary>
    private static Girl Seed(FeatureHarness h, uint girlId = 1, uint modelId = 1)
    {
        var girl = h.Player.GetGirl(girlId);
        if (girl is null)
        {
            girl = new Girl { Girlid = girlId };
            h.Player.Girls.Add(girl);
        }
        girl.Modelid = modelId;
        return girl;
    }

    private static void GiveSuit(FeatureHarness h, uint girlId, int modelId, int value)
        => GirlDressFeature.SetSuitValue(h.Player, girlId, modelId, value, updates: null);

    private static Task MainGirlAsync(FeatureHarness h, uint girlId)
        => h.DispatchFunctionAsync(GirlDressFeature.Function,
            $"{{\"sCmd\":\"{GirlDressFeature.CmdSetMainGirl}\",\"nId\":{girlId}}}");

    private static Task ChangeClothAsync(FeatureHarness h, uint girlId, int modelId)
        => h.DispatchFunctionAsync(GirlDressFeature.Function,
            $"{{\"sCmd\":\"{GirlDressFeature.CmdChangeCloth}\",\"nId\":{girlId},\"nSuit\":{modelId}}}");

    private static Task FightModelAsync(FeatureHarness h, uint girlId, int? use)
        => h.DispatchFunctionAsync(GirlDressFeature.Function,
            use is null
                ? $"{{\"sCmd\":\"{GirlDressFeature.CmdSetModelInFight}\",\"nId\":{girlId}}}"
                : $"{{\"sCmd\":\"{GirlDressFeature.CmdSetModelInFight}\",\"nId\":{girlId},\"nUse\":{use}}}");

    // ================= SetMainGirl =================

    [Fact]
    public async Task 换看板娘_写任务值2_2并回包()
    {
        using var h = await FeatureHarness.CreateAsync("dress-main");
        Seed(h, 3);

        await MainGirlAsync(h, 3);

        Assert.Equal(3, h.Player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId));
        var r = Reply(h);
        Assert.Equal(GirlDressFeature.CmdSetMainGirl, r["sCmd"]!.GetValue<string>());
        // ★ 少女养成族：成功绝不带 nError
        Assert.Null(r["nError"]);
    }

    [Fact]
    public async Task 换看板娘_推pf1026()
    {
        using var h = await FeatureHarness.CreateAsync("dress-main-push");
        Seed(h, 3);

        await MainGirlAsync(h, 3);

        // ★ 看板娘存任务值 ⇒ 通道是 pf 1026（客户端 OnSetMainGirl 不读回包、重读任务值）
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp),
            $"没推 pf 1026，实际推了 {string.Join(',', h.PushedPf)}");
    }

    [Fact]
    public async Task 换看板娘_未持有该少女时回错误码()
    {
        using var h = await FeatureHarness.CreateAsync("dress-main-nogirl");

        await MainGirlAsync(h, 99);

        // ★ 必须有回包（哪怕客户端没有 OnSetMainGirlError）—— 否则遮罩不会解开
        var r = Reply(h);
        Assert.Equal(GirlDressFeature.ErrGeneric, r["nError"]!.GetValue<int>());
        Assert.Equal(0, h.Player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId));
    }

    [Fact]
    public async Task 换看板娘_已经是看板娘时仍回包()
    {
        using var h = await FeatureHarness.CreateAsync("dress-main-same");
        Seed(h, 3);
        h.Player.SetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId, 3);

        await MainGirlAsync(h, 3);

        // 不算失败：原样回执（不带 nError），避免界面卡遮罩
        var r = Reply(h);
        Assert.Equal(GirlDressFeature.CmdSetMainGirl, r["sCmd"]!.GetValue<string>());
        Assert.Null(r["nError"]);
    }

    // ================= ChangeCloth =================

    [Fact]
    public async Task 换服饰_已拥有时改modelid并回显两字段()
    {
        using var h = await FeatureHarness.CreateAsync("dress-change");
        var girl = Seed(h, 1, modelId: 1);
        GiveSuit(h, 1, 5, GirlDressFeature.SuitWorn);

        await ChangeClothAsync(h, 1, 5);

        Assert.Equal(5u, girl.Modelid);
        var r = Reply(h);
        // ★ 回包必须带 nId + nSuit（GirlLogic.lua:614 要用它们调 ui:OnDragCloth）
        Assert.Equal(1, r["nId"]!.GetValue<int>());
        Assert.Equal(5, r["nSuit"]!.GetValue<int>());
        Assert.Null(r["nError"]);
    }

    [Fact]
    public async Task 换服饰_推pf1030而不是1026()
    {
        using var h = await FeatureHarness.CreateAsync("dress-change-push");
        Seed(h, 1, modelId: 1);
        GiveSuit(h, 1, 5, GirlDressFeature.SuitWorn);

        await ChangeClothAsync(h, 1, 5);

        // ★★ modelid 在 protobuf 里 ⇒ 只有 pf 1030 GirlDataNtf 能让客户端看到
        Assert.True(await h.WaitForPfAsync(GamePf.GirlDataNtf),
            $"没推 pf 1030，实际推了 {string.Join(',', h.PushedPf)}");
    }

    [Fact]
    public async Task 换服饰_未拥有时拒绝且不改modelid()
    {
        using var h = await FeatureHarness.CreateAsync("dress-change-locked");
        var girl = Seed(h, 1, modelId: 1);
        // 故意不给 SuitValue

        await ChangeClothAsync(h, 1, 777);

        Assert.Equal(1u, girl.Modelid);
        // ★ 仍要回包（OnChangeClothError 是空函数 ⇒ 玩家零反馈，但遮罩要解开）
        Assert.Equal(GirlDressFeature.ErrGeneric, Reply(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task 换服饰_把NEW标记清成已穿()
    {
        using var h = await FeatureHarness.CreateAsync("dress-change-new");
        Seed(h, 1, modelId: 1);
        GiveSuit(h, 1, 5, GirlDressFeature.SuitNew);

        await ChangeClothAsync(h, 1, 5);

        // ★ UI_GirlClothes.lua:846 IsNew = (SuitValue == 1) ⇒ 穿过一次要清掉红点
        Assert.Equal(GirlDressFeature.SuitWorn,
                     GirlDressFeature.GetSuitValue(h.Player, 1, 5));
    }

    [Fact]
    public async Task 换服饰_穿皮肤时任务值键要过FixModelId()
    {
        using var h = await FeatureHarness.CreateAsync("dress-change-fix");
        Seed(h, 9, modelId: 1);
        // 少女 9 的 7001 皮肤（SpineSuit.txt 第一行）
        GiveSuit(h, 9, 7001, GirlDressFeature.SuitNew);

        await ChangeClothAsync(h, 9, 7001);

        var girl = h.Player.GetGirl(9)!;
        Assert.Equal(7001u, girl.Modelid);
        // ★★★ 键是 FixModelId(7001) = 1711，不是 7001 —— 写错的话「发了却显示未获得」
        Assert.Equal(1711, GmUnlock.FixModelId(7001));
        Assert.Equal(GirlDressFeature.SuitWorn,
                     h.Player.GetTask(GirlDressFeature.SuitGroup,
                                      GmUnlock.GirlTaskId(9, 1711)));
    }

    [Fact]
    public async Task 换服饰_少女未持有时拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("dress-change-nogirl");

        await ChangeClothAsync(h, 99, 5);

        Assert.Equal(GirlDressFeature.ErrGeneric, Reply(h)["nError"]!.GetValue<int>());
    }

    // ================= SetModelInFight =================

    [Fact]
    public async Task 战斗服_开启时写偏移9并回bSuccess()
    {
        using var h = await FeatureHarness.CreateAsync("dress-fight-on");
        Seed(h, 1, modelId: 1);

        await FightModelAsync(h, 1, 1);

        Assert.Equal(1, h.Player.GetTask(GmUnlock.GirlVarGroup,
                                         GmUnlock.GirlTaskId(1, GirlDressFeature.VarIsFightModel)));
        var r = Reply(h);
        // ★ 回包必须有 bSuccess + nUse（GirlLogic.lua:731..732）
        Assert.True(r["bSuccess"]!.GetValue<bool>());
        Assert.Equal(1, r["nUse"]!.GetValue<int>());
    }

    [Fact]
    public async Task 战斗服_只传nId时视为关闭()
    {
        using var h = await FeatureHarness.CreateAsync("dress-fight-implicit");
        Seed(h, 1, modelId: 1);
        h.Player.SetTask(GmUnlock.GirlVarGroup,
                         GmUnlock.GirlTaskId(1, GirlDressFeature.VarIsFightModel), 1);

        // ★ 客户端有三处只传 nId（GirlLogic.lua:622 / UI_GirlClothes.lua:350,583），nUse 缺省 = 0
        await FightModelAsync(h, 1, use: null);

        Assert.Equal(0, h.Player.GetTask(GmUnlock.GirlVarGroup,
                                        GmUnlock.GirlTaskId(1, GirlDressFeature.VarIsFightModel)));
        Assert.Equal(0, Reply(h)["nUse"]!.GetValue<int>());
    }

    [Fact]
    public async Task 战斗服_当前皮肤无3D模型时拒绝开启()
    {
        using var h = await FeatureHarness.CreateAsync("dress-fight-nomodel");
        var dress = GirlDressTable.Load(TestPaths.SettingDir);
        // 从真实表里挑一条「是皮肤但 IsModel 为空」的
        var noModel = dress.All.First(r => !r.HasModel);
        Seed(h, noModel.GirlId, (uint)noModel.ModelId);

        await FightModelAsync(h, noModel.GirlId, 1);

        var r = Reply(h);
        Assert.False(r["bSuccess"]!.GetValue<bool>());
        Assert.Equal(0, r["nUse"]!.GetValue<int>());
        Assert.Equal(0, h.Player.GetTask(
            GmUnlock.GirlVarGroup,
            GmUnlock.GirlTaskId(noModel.GirlId, GirlDressFeature.VarIsFightModel)));
    }

    [Fact]
    public async Task 战斗服_有3D模型的皮肤可以开启()
    {
        using var h = await FeatureHarness.CreateAsync("dress-fight-hasmodel");
        var dress = GirlDressTable.Load(TestPaths.SettingDir);
        var withModel = dress.All.First(r => r.HasModel);
        Seed(h, withModel.GirlId, (uint)withModel.ModelId);

        await FightModelAsync(h, withModel.GirlId, 1);

        Assert.True(Reply(h)["bSuccess"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 战斗服_少女未持有时回bSuccessFalse()
    {
        using var h = await FeatureHarness.CreateAsync("dress-fight-nogirl");

        await FightModelAsync(h, 99, 1);

        // ★ 这条不走 nError 通道（客户端 OnSetModelInFight 读 bSuccess），但必须有回包
        var r = Reply(h);
        Assert.False(r["bSuccess"]!.GetValue<bool>());
    }

    // ================= 协处理器共存 =================

    [Fact]
    public async Task GirlLogic三个域共存_互不吞命令()
    {
        using var h = await FeatureHarness.CreateAsync("dress-coexist");
        Seed(h, 1, modelId: 1);
        GiveSuit(h, 1, 5, GirlDressFeature.SuitWorn);

        // ① 本域（协处理器）
        await ChangeClothAsync(h, 1, 5);
        Assert.Equal(GirlDressFeature.CmdChangeCloth, Reply(h)["sCmd"]!.GetValue<string>());

        // ② 特训域（主 handler）—— 必须仍然响应
        await h.DispatchFunctionAsync(GirlTrainFeature.Function,
            $"{{\"sCmd\":\"{GirlTrainFeature.CmdAddTrainPos}\"}}");
        Assert.Equal(GirlTrainFeature.CmdAddTrainPos, Reply(h)["sCmd"]!.GetValue<string>());

        // ③ 好感度域（另一个协处理器）—— 也必须仍然响应
        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewLinkStory}\",\"nId\":1,\"Level\":1}}");
        Assert.Equal(GirlLoveFeature.CmdViewLinkStory, Reply(h)["sCmd"]!.GetValue<string>());
    }

    // ================= 登录钩子：补服饰标记 =================

    [Fact]
    public async Task 登录_皮肤道具解锁对应服饰()
    {
        using var h = await FeatureHarness.CreateAsync("dress-sync-item");
        var dress = GirlDressTable.Load(TestPaths.SettingDir);
        var suit = dress.All.First(r => r.Gdpl is not null);
        Seed(h, suit.GirlId);
        var q = suit.Gdpl!.Value;
        h.Player.Items.Add(new Item
        {
            Guid = 77001, Genre = q.G, Detail = q.D, Particular = q.P, Level = q.L, Count = 1,
        });
        // 清掉可能已有的标记，确保是本次钩子写的
        GirlDressFeature.SetSuitValue(h.Player, suit.GirlId, suit.ModelId,
                                     GirlDressFeature.SuitNone, updates: null);

        h.RunPrepareHooks();

        // ★★★ 不置位的服饰不会出现在换装列表里（UI_GirlClothes.lua:847）
        Assert.True(GirlDressFeature.GetSuitValue(h.Player, suit.GirlId, suit.ModelId)
                        >= GirlDressFeature.SuitNew,
            $"皮肤道具 {q.G}-{q.D}-{q.P}-{q.L} 没能解锁服饰 {suit}");
    }

    [Fact]
    public async Task 登录_角色卡也解锁对应服饰()
    {
        using var h = await FeatureHarness.CreateAsync("dress-sync-card");
        // 起始卡里就有少女 1 的卡；先把标记清空，看钩子会不会补
        var card = h.Module.CharacterCards.ForGirl(1).First();
        h.Player.Items.Add(new Item
        {
            Guid = 77002, Genre = CharacterCardTable.CardGenre,
            Detail = card.D, Particular = card.P, Level = card.L, Count = 1,
        });
        Seed(h, 1);
        GirlDressFeature.SetSuitValue(h.Player, 1, card.ModelId,
                                     GirlDressFeature.SuitNone, updates: null);

        h.RunPrepareHooks();

        Assert.True(GirlDressFeature.GetSuitValue(h.Player, 1, card.ModelId)
                        >= GirlDressFeature.SuitNew);
    }

    [Fact]
    public async Task 登录_已穿过的服饰不被洗回NEW()
    {
        using var h = await FeatureHarness.CreateAsync("dress-sync-idem");
        var card = h.Module.CharacterCards.ForGirl(1).First();
        Seed(h, 1);
        GirlDressFeature.SetSuitValue(h.Player, 1, card.ModelId,
                                     GirlDressFeature.SuitWorn, updates: null);

        h.RunPrepareHooks();

        // ★ 幂等：只把 0 → 1，不动 >0 的
        Assert.Equal(GirlDressFeature.SuitWorn,
                     GirlDressFeature.GetSuitValue(h.Player, 1, card.ModelId));
    }

    // ================= 任务值编码 =================

    [Fact]
    public void 服饰任务值_普通少女走group4()
    {
        Assert.Equal(GirlDressFeature.SuitGroup, GirlDressFeature.SuitGroupOf(1));
        Assert.Equal(GirlDressFeature.SuitGroup, GirlDressFeature.SuitGroupOf(32));
    }

    [Fact]
    public void 服饰任务值_联动少女走group91且girlId减200()
    {
        // ★ GirlCommon:GetExtGroup 的 4 → 91（GirlCommon.lua:1495..1506）
        Assert.Equal(GirlDressFeature.SuitGroupLink, GirlDressFeature.SuitGroupOf(201));
        // 联动少女 201 的编码等同普通少女 1
        Assert.Equal(GirlDressFeature.SuitTaskId(1, 5), GirlDressFeature.SuitTaskId(201, 5));
    }

    [Fact]
    public void 服饰任务值_ModelId过FixModelId()
    {
        // 7001 → 1711、3017 → 1607、8001 → 1801（GirlCommon.lua:609..629）
        Assert.Equal(GmUnlock.GirlTaskId(9, 1711), GirlDressFeature.SuitTaskId(9, 7001));
        Assert.Equal(GmUnlock.GirlTaskId(2, 1607), GirlDressFeature.SuitTaskId(2, 3017));
        Assert.Equal(GmUnlock.GirlTaskId(1, 1801), GirlDressFeature.SuitTaskId(1, 8001));
        // < 2000 的原样
        Assert.Equal(GmUnlock.GirlTaskId(1, 5), GirlDressFeature.SuitTaskId(1, 5));
    }
}
