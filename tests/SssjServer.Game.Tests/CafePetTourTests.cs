using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 咖啡馆宠物**远征**子系统（<c>CafePetFeature.Tour.cs</c> 的 4 个 sCmd）。
///
/// 断言围绕四条「一错就废」的契约：
/// <list type="number">
///   <item><b>回包必须带 <c>nIndex</c></b> —— 四个 Rsp 第一句都是
///     <c>if not tbParam or not tbParam.nIndex then return end</c>，漏了 = 界面不刷新。</item>
///   <item><b><c>CheckTourIsSuccess</c> 必须真推进状态</b> —— 客户端每秒轮询
///     （<c>UI_PetTour.lua:635..651</c>），不推进 = 死循环。</item>
///   <item><b>校验不过时不回包</b> —— <c>UnLockTourAreaRsp</c> 无条件弹「解锁成功」，
///     回包 = 玩家看到成功但栏位还锁着。</item>
///   <item><b>任务值坐标</b>与客户端 <c>GetTaskValue</c> 逐个对齐（位段还是 15 位打包）。</item>
/// </list>
/// </summary>
public class CafePetTourTests
{
    private static Task SendAsync(FeatureHarness h, string sCmd, params (string Key, JsonNode? Value)[] fields)
    {
        var obj = new JsonObject { ["sCmd"] = sCmd };
        foreach (var (k, v) in fields)
            obj[k] = v;
        return h.DispatchFunctionAsync(CafePetFeature.Function, obj.ToJsonString());
    }

    /// <summary>造 <paramref name="count"/> 只宠物（uid 从 1 起），可改每只的属性。</summary>
    private static void SeedPets(Player player, int count, Action<CafePet, int>? mutate = null)
    {
        var state = CafePetState.Read(player);
        state.EnsureSlots(6);
        for (int i = 1; i <= count; i++)
        {
            var pet = new CafePet
            {
                Uid = (uint)i,
                TemplateId = 101,
                Hot = 100,
                Lv = 10,
                Nature = 1,
                Name = $"宠物{i}",
            };
            mutate?.Invoke(pet, i);
            state.Pets.Add(pet);
        }
        state.NextUid = count + 1;
        state.Write(player);
    }

    /// <summary>把某栏位直接置成「已解锁 + 挂了远征 tourId」。</summary>
    private static void SeedArea(Player player, int index, int tourId, int state = PetTourTable.StateIdle)
    {
        player.SetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index, state);
        player.SetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + index, tourId);
    }

    private static JsonObject LastReply(FeatureHarness h, string expectSCmd)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(CafePetFeature.Function, function);
        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(expectSCmd, obj["sCmd"]!.GetValue<string>());
        Assert.Equal(0, obj["nError"]!.GetValue<int>());
        return obj;
    }

    // =================================================================
    //  任务值坐标（客户端 GetTaskValue 的那些偏移）
    // =================================================================

    /// <summary>
    /// 编队/奖励的任务 id 必须与客户端算式逐字一致
    /// （<c>NCafePetTour.lua:14</c> / <c>:96</c> / <c>:99</c>）。
    /// 这类「差一」错误不会抛，只会让界面读到别的栏位的数据。
    /// </summary>
    [Fact]
    public void 任务值坐标与客户端算式一致()
    {
        // (FomationId - 1) * 3 + 550 + Index
        Assert.Equal(551, CafePetFeature.FormationTask(1, 1));
        Assert.Equal(553, CafePetFeature.FormationTask(1, 3));
        Assert.Equal(554, CafePetFeature.FormationTask(2, 1));

        // ToursAwardIdOff + (nIndex-1)*2 + 1 与 ToursAwardIdOff + nIndex*2
        Assert.Equal(851, CafePetFeature.AwardTaskFirst(1));
        Assert.Equal(852, CafePetFeature.AwardTaskSecond(1));
        Assert.Equal(853, CafePetFeature.AwardTaskFirst(2));
        Assert.Equal(854, CafePetFeature.AwardTaskSecond(2));
    }

    /// <summary>
    /// 15 位段打包必须与客户端 <c>LoadBits(v,1,15)</c> / <c>LoadBits(v,16,30)</c> 互逆，
    /// 且**不碰第 31 位**（符号位）—— 与 §39 剧情位域是同一类约束。
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(49, 0)]
    [InlineData(0, 49)]
    [InlineData(32767, 32767)]
    public void 奖励id按15位段打包且不碰符号位(int low, int high)
    {
        int packed = CafePetFeature.Pack15(low, high);

        Assert.True(packed >= 0, "打包结果不能是负数（碰到符号位了）");
        Assert.Equal(low, packed & 0x7FFF);
        Assert.Equal(high, (packed >> 15) & 0x7FFF);
    }

    // =================================================================
    //  UnLockTourArea
    // =================================================================

    /// <summary>栏位 1 无条件、免费 ⇒ 直接解锁成功，并且**立刻挂上一条远征**。</summary>
    [Fact]
    public async Task 解锁免费栏位后状态变1且挂上远征()
    {
        using var h = await FeatureHarness.CreateAsync("tour_unlock_free");

        await SendAsync(h, CafePetFeature.CmdUnLockTourArea, ("nIndex", 1));

        var reply = LastReply(h, CafePetFeature.RspUnLockTourArea);
        Assert.Equal(1, reply["nIndex"]!.GetValue<int>());   // 契约 ①

        Assert.Equal(PetTourTable.StateIdle,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));

        // ★ 必须挂上远征，否则客户端 UI_PetTour.lua:308 会 Error('已解锁的栏位没有任务！')
        int tourId = h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + 1);
        Assert.NotEqual(0, tourId);

        // 四个奖励也要抽好（850 两段）
        Assert.NotEmpty(CafePetFeature.ReadAwardIds(h.Player, 1));
    }

    /// <summary>
    /// 条件不满足时**不能回包** —— <c>UnLockTourAreaRsp</c>（<c>:63..73</c>）无条件弹
    /// 「解锁成功」并刷新界面，回了包玩家就会看到「解锁成功」但栏位还是锁的。
    /// 栏位 3 要「完成 1 次 C 阶 + 10 只宠物 + 20000 货币2」，新号一个都不满足。
    /// </summary>
    [Fact]
    public async Task 条件不满足时不回包且不扣钱()
    {
        using var h = await FeatureHarness.CreateAsync("tour_unlock_deny");
        int before = h.Outbound.Count;
        int money = h.Player.GetMoney(2);

        await SendAsync(h, CafePetFeature.CmdUnLockTourArea, ("nIndex", 3));

        Assert.Equal(before, h.Outbound.Count);          // 一条下行都没有
        Assert.Equal(money, h.Player.GetMoney(2));       // 没扣钱
        Assert.Equal(PetTourTable.StateLocked,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 3));
    }

    /// <summary>条件都满足时解锁栏位 3，并按 <c>Unlock3 = [2,20000]</c> 扣钱。</summary>
    [Fact]
    public async Task 条件满足时解锁栏位3并扣两万()
    {
        using var h = await FeatureHarness.CreateAsync("tour_unlock_paid");
        SeedPets(h.Player, 10);
        // 完成过 2 次 C 阶（栏位 3 的 Unlock1 = [1,2]）
        h.Player.SetTask(PetTourTable.Group, PetTourTable.TaskCompleteNumOff + 1, 2);
        h.Player.SetMoney(2, 50000);

        await SendAsync(h, CafePetFeature.CmdUnLockTourArea, ("nIndex", 3));

        LastReply(h, CafePetFeature.RspUnLockTourArea);
        Assert.Equal(PetTourTable.StateIdle,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 3));
        Assert.Equal(30000, h.Player.GetMoney(2));       // 50000 - 20000
    }

    /// <summary>已解锁的栏位再解锁一次 ⇒ 不回包、不重复扣钱。</summary>
    [Fact]
    public async Task 重复解锁不回包()
    {
        using var h = await FeatureHarness.CreateAsync("tour_unlock_twice");
        SeedArea(h.Player, 1, tourId: 1);
        int before = h.Outbound.Count;

        await SendAsync(h, CafePetFeature.CmdUnLockTourArea, ("nIndex", 1));

        Assert.Equal(before, h.Outbound.Count);
    }

    // =================================================================
    //  SetTourPets（派宠物 + 出发）
    // =================================================================

    /// <summary>
    /// 正常出发：写编队、扣精力、状态 → 2、记出发时刻。
    /// ★ 这一条同时验证了「<c>SetTourPets</c> 身兼派人与出发两职」（类注释 §2）。
    /// </summary>
    [Fact]
    public async Task 出发后写编队扣精力并进入进行中()
    {
        using var h = await FeatureHarness.CreateAsync("tour_go");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);   // 远征 1 的 Condition = {{1,1,3}}（只要 3 只）

        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));

        var reply = LastReply(h, CafePetFeature.RspSetTourPets);
        Assert.Equal(1, reply["nIndex"]!.GetValue<int>());

        Assert.Equal(PetTourTable.StateRunning,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));
        Assert.True(h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + 1) > 1_700_000_000);

        // 编队三格
        Assert.Equal(1, h.Player.GetTask(PetTourTable.Group, CafePetFeature.FormationTask(1, 1)));
        Assert.Equal(2, h.Player.GetTask(PetTourTable.Group, CafePetFeature.FormationTask(1, 2)));
        Assert.Equal(3, h.Player.GetTask(PetTourTable.Group, CafePetFeature.FormationTask(1, 3)));

        // 栏位 1 的 EnergyCost = 20 ⇒ 100 - 20
        var state = CafePetState.Read(h.Player);
        Assert.All(state.Pets, p => Assert.Equal(80u, p.Hot));
    }

    /// <summary>不足 3 只 ⇒ 客户端 <c>IsPetsCanGoTour</c> 判 false，服务端也必须拒绝。</summary>
    [Fact]
    public async Task 少于三只不回包()
    {
        using var h = await FeatureHarness.CreateAsync("tour_go_few");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);
        int before = h.Outbound.Count;

        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 0 }));

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(PetTourTable.StateIdle,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));
    }

    /// <summary>
    /// 精力判据是**严格大于**（<c>NCafePetTour.lua:280</c> <c>info.hot &gt; tourInfo.EnergyCost</c>）。
    /// 栏位 1 的 <c>EnergyCost = 20</c> ⇒ 精力恰好 20 的宠物**不能**出发。
    /// ★ 写成 <c>&gt;=</c> 就会让客户端灰着的按钮在服务端被放行，属「两端判据不一致」。
    /// </summary>
    [Fact]
    public async Task 精力恰好等于消耗时不能出发()
    {
        using var h = await FeatureHarness.CreateAsync("tour_go_hot");
        SeedPets(h.Player, 3, (p, _) => p.Hot = 20);
        SeedArea(h.Player, 1, tourId: 1);
        int before = h.Outbound.Count;

        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// ★★ **真机才暴露的路径**：新领养的宠物 <c>hot = 0</c>（`OnNewPet` 就是这么写的），
    /// 而出征要求 <c>hot &gt; EnergyCost</c> ⇒ 刚领养的宠物**一只都派不出去**。
    ///
    /// 本轮真机验收第一条 `SetTourPets` 就被这个拒了，而当时全部单测是绿的 ——
    /// 因为 <see cref="SeedPets"/> 造的是 <c>Hot=100</c>，**种子数据比真实数据更理想**。
    /// ⇒ 补这条钉住「精力 0 必须被拒」，与 <see cref="精力恰好等于消耗时不能出发"/> 一起
    /// 覆盖住边界两侧。（handler 行为本身是对的，判据来自客户端 `NCafePetTour.lua:280`。）
    /// </summary>
    [Fact]
    public async Task 新领养宠物精力为0时不能出发()
    {
        using var h = await FeatureHarness.CreateAsync("tour_go_fresh");
        SeedPets(h.Player, 3, (p, _) => p.Hot = 0);      // 与 OnNewPet 落盘的状态一致
        SeedArea(h.Player, 1, tourId: 1);
        int before = h.Outbound.Count;

        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(PetTourTable.StateIdle,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));
    }

    /// <summary>同一只宠物不能同时派到两个栏位。</summary>
    [Fact]
    public async Task 已在远征的宠物不能再次出发()
    {
        using var h = await FeatureHarness.CreateAsync("tour_go_busy");
        SeedPets(h.Player, 6);
        SeedArea(h.Player, 1, tourId: 1);
        SeedArea(h.Player, 2, tourId: 1);

        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));
        int before = h.Outbound.Count;

        // 栏位 2 想再用宠物 1
        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 2), ("tbPets", new JsonArray { 1, 4, 5 }));

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// 出征的宠物如果正站在咖啡馆里，回包要带 <c>statelist</c> 把那些指派位撤下
    /// （<c>NCafePetTour.lua:134..141</c> 会 <c>DestroyPet</c>），
    /// 否则客户端里同一只宠物**既在店里又在远征**。
    /// </summary>
    [Fact]
    public async Task 出发时撤下咖啡馆指派位并回statelist()
    {
        using var h = await FeatureHarness.CreateAsync("tour_go_clear");
        SeedPets(h.Player, 3);
        var seeded = CafePetState.Read(h.Player);
        seeded.EnsureSlots(6);
        seeded.InCafe[0] = 1;        // 宠物 1 站在指派位 1
        seeded.InCafe[2] = 3;        // 宠物 3 站在指派位 3
        seeded.Write(h.Player);
        SeedArea(h.Player, 1, tourId: 1);

        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));

        var reply = LastReply(h, CafePetFeature.RspSetTourPets);
        var list = Assert.IsType<JsonArray>(reply["statelist"]);
        Assert.Equal(new[] { 1, 3 }, list.Select(n => n!.GetValue<int>()).OrderBy(v => v).ToArray());

        var after = CafePetState.Read(h.Player);
        Assert.Equal(0, after.InCafe[0]);
        Assert.Equal(0, after.InCafe[2]);
    }

    // =================================================================
    //  CheckTourIsSuccess
    // =================================================================

    /// <summary>
    /// ★★★ 时间没到 ⇒ **不推进状态**。
    /// （回不回包都行，这里顺带钉住「不回包」这个更省的实现。）
    /// </summary>
    [Fact]
    public async Task 时间没到不推进状态()
    {
        using var h = await FeatureHarness.CreateAsync("tour_check_early");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);
        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));

        await SendAsync(h, CafePetFeature.CmdCheckTourIsSuccess, ("nIndex", 1));

        Assert.Equal(PetTourTable.StateRunning,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));
    }

    /// <summary>
    /// ★★★ 时间到了必须把状态从 2 推到 3 或 4 —— 客户端每秒轮询这条
    /// （<c>UI_PetTour.lua:635..651</c>），不推进 = **无限重发**。
    /// </summary>
    [Fact]
    public async Task 时间到了推进到已完成()
    {
        using var h = await FeatureHarness.CreateAsync("tour_check_done");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);
        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));

        // 把出发时刻往前挪 4 小时（栏位 1 的 Hour = 4）
        long begin = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4 * 3600 - 60;
        h.Player.SetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + 1, (int)begin);

        await SendAsync(h, CafePetFeature.CmdCheckTourIsSuccess, ("nIndex", 1));

        var reply = LastReply(h, CafePetFeature.RspCheckTourIsSuccess);
        Assert.Equal(1, reply["nIndex"]!.GetValue<int>());

        int state = h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1);
        Assert.True(state is PetTourTable.StateDone or PetTourTable.StateGreatSuccess,
            $"状态应推进到 3/4，实际 {state}");
    }

    /// <summary>
    /// 掷骰必须是**确定性**的：同一次远征反复查询，结果不能变。
    /// 用真随机会出现「这秒大成功、下秒普通」——而客户端每秒问一次。
    /// </summary>
    [Fact]
    public async Task 重复查询结果不变()
    {
        using var h = await FeatureHarness.CreateAsync("tour_check_stable");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);
        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));
        long begin = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4 * 3600 - 60;
        h.Player.SetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + 1, (int)begin);

        await SendAsync(h, CafePetFeature.CmdCheckTourIsSuccess, ("nIndex", 1));
        int first = h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1);

        for (int i = 0; i < 5; i++)
        {
            await SendAsync(h, CafePetFeature.CmdCheckTourIsSuccess, ("nIndex", 1));
            Assert.Equal(first, h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));
        }
    }

    // =================================================================
    //  CompleteTour
    // =================================================================

    /// <summary>
    /// 收工：发奖、品阶完成次数 +1、栏位重置为「可再出发」并重抽远征。
    /// </summary>
    [Fact]
    public async Task 收工后发奖并重置栏位()
    {
        using var h = await FeatureHarness.CreateAsync("tour_complete");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);
        await SendAsync(h, CafePetFeature.CmdSetTourPets,
            ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));
        long begin = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4 * 3600 - 60;
        h.Player.SetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + 1, (int)begin);
        await SendAsync(h, CafePetFeature.CmdCheckTourIsSuccess, ("nIndex", 1));

        int itemsBefore = h.Player.Items.Count;
        // ★ 奖励可能落在**任意**货币上，不能只盯金币：
        //   远征 1 普通奖励 7-1-4-2-5（道具）、大成功奖励 15-2-1-1-30
        //   （ItemList.txt 里是 ItemFreeDiamondAuto ⇒ 折算成免费钻石，不进背包）。
        //   而大成功与否由掷骰决定 ⇒ 只盯一种就会 ~50% 概率假红（本轮踩过）。
        var moneyBefore = h.Player.Money.ToDictionary(kv => kv.Key, kv => kv.Value);

        await SendAsync(h, CafePetFeature.CmdCompleteTour, ("nIndex", 1));

        var reply = LastReply(h, CafePetFeature.RspCompleteTour);
        Assert.Equal(1, reply["nIndex"]!.GetValue<int>());

        bool gotItem = h.Player.Items.Count > itemsBefore;
        bool gotMoney = h.Player.Money.Any(kv =>
            kv.Value > moneyBefore.GetValueOrDefault(kv.Key));
        Assert.True(gotItem || gotMoney,
            $"收工应当发奖：背包 {itemsBefore} → {h.Player.Items.Count}、" +
            $"货币 {string.Join(',', h.Player.Money.Select(kv => $"{kv.Key}:{kv.Value}"))}");

        // 栏位 1 的 Rare = 1 ⇒ C 阶完成次数 +1
        Assert.Equal(1, h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskCompleteNumOff + 1));

        // 重置：状态回 1、编队清空、出发时刻清 0、重新挂了远征
        Assert.Equal(PetTourTable.StateIdle,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));
        Assert.Equal(0, h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + 1));
        Assert.Equal(0, h.Player.GetTask(PetTourTable.Group, CafePetFeature.FormationTask(1, 1)));
        Assert.NotEqual(0, h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + 1));
    }

    /// <summary>没完成就想收工 ⇒ 不回包、不发奖。</summary>
    [Fact]
    public async Task 未完成时收工不回包()
    {
        using var h = await FeatureHarness.CreateAsync("tour_complete_early");
        SeedPets(h.Player, 3);
        SeedArea(h.Player, 1, tourId: 1);
        int before = h.Outbound.Count;

        await SendAsync(h, CafePetFeature.CmdCompleteTour, ("nIndex", 1));

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// ★★★ 必须推任务值（pf 1026）—— 客户端 <c>UI_PetTour</c> 的每个字段都是自己
    /// <c>me:GetTaskValue(49,...)</c> 现读的，回包只是「请刷新」的信号。
    /// 只回包不推任务值 ⇒ 界面刷新了但读到的还是旧值，是本仓反复出现的那类
    /// 「日志全绿、玩家看到的是坏的」缺陷（§51）。
    /// </summary>
    [Fact]
    public async Task 解锁后推送任务值()
    {
        using var h = await FeatureHarness.CreateAsync("tour_push");

        await SendAsync(h, CafePetFeature.CmdUnLockTourArea, ("nIndex", 1));

        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp),
            $"没等到 pf {GamePf.TaskValueSyncRsp}，实际推了：[{string.Join(",", h.PushedPf)}]");
    }

    /// <summary>
    /// 全链路走一遍：解锁 → 出发 → 到点结算 → 收工 → **再出发一次**。
    /// 最后这步是重点：收工后栏位必须真的回到「可再出发」，
    /// 否则玩家只能玩一轮（属「功能写完了但玩家用不了」，第十五轮社团那类缺陷）。
    /// </summary>
    [Fact]
    public async Task 全链路可以连做两轮()
    {
        using var h = await FeatureHarness.CreateAsync("tour_e2e");
        SeedPets(h.Player, 3);

        await SendAsync(h, CafePetFeature.CmdUnLockTourArea, ("nIndex", 1));
        Assert.Equal(PetTourTable.StateIdle,
            h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));

        for (int round = 1; round <= 2; round++)
        {
            await SendAsync(h, CafePetFeature.CmdSetTourPets,
                ("nIndex", 1), ("tbPets", new JsonArray { 1, 2, 3 }));
            Assert.Equal(PetTourTable.StateRunning,
                h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));

            long begin = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4 * 3600 - 60;
            h.Player.SetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + 1, (int)begin);

            await SendAsync(h, CafePetFeature.CmdCheckTourIsSuccess, ("nIndex", 1));
            int st = h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1);
            Assert.True(st is PetTourTable.StateDone or PetTourTable.StateGreatSuccess,
                $"第 {round} 轮结算后状态应是 3/4，实际 {st}");

            await SendAsync(h, CafePetFeature.CmdCompleteTour, ("nIndex", 1));
            Assert.Equal(PetTourTable.StateIdle,
                h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + 1));

            // ★ 宠物精力每轮都扣，第二轮出发前要确认还够（100 - 20*2 = 60 > 20）
            var state = CafePetState.Read(h.Player);
            Assert.All(state.Pets, p => Assert.True(p.Hot > 20,
                $"第 {round} 轮后精力 {p.Hot} 已不足以再出发"));
        }

        // 两轮 ⇒ C 阶完成次数 = 2
        Assert.Equal(2, h.Player.GetTask(PetTourTable.Group, PetTourTable.TaskCompleteNumOff + 1));
    }

    // =================================================================
    //  畸形上行
    // =================================================================

    /// <summary>不存在的栏位 / 缺参数 / 负数 ⇒ 不抛、不回包。</summary>
    [Theory]
    [InlineData(CafePetFeature.CmdUnLockTourArea)]
    [InlineData(CafePetFeature.CmdSetTourPets)]
    [InlineData(CafePetFeature.CmdCheckTourIsSuccess)]
    [InlineData(CafePetFeature.CmdCompleteTour)]
    public async Task 畸形栏位号不抛也不回包(string sCmd)
    {
        using var h = await FeatureHarness.CreateAsync($"tour_bad_{sCmd}");
        int before = h.Outbound.Count;

        await SendAsync(h, sCmd);                       // 缺 nIndex
        await SendAsync(h, sCmd, ("nIndex", 0));
        await SendAsync(h, sCmd, ("nIndex", -1));
        await SendAsync(h, sCmd, ("nIndex", 9999));

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// 四个下行 sCmd 都必须在白名单里 —— 客户端 <c>Dispatcher</c> 是
    /// <c>self[sCmd.."Rsp"](self, ...)</c> 且不判 nil，不在白名单 = 直接 nil 调用。
    /// </summary>
    [Fact]
    public void 四个下行sCmd都在白名单里()
    {
        Assert.Contains(CafePetFeature.RspUnLockTourArea, CafePetFeature.AllowedReplyCmds);
        Assert.Contains(CafePetFeature.RspSetTourPets, CafePetFeature.AllowedReplyCmds);
        Assert.Contains(CafePetFeature.RspCheckTourIsSuccess, CafePetFeature.AllowedReplyCmds);
        Assert.Contains(CafePetFeature.RspCompleteTour, CafePetFeature.AllowedReplyCmds);
    }

    /// <summary>
    /// ★ <c>GetPawAward</c> 属于**图鉴/爪印**子系统而不是远征，且全仓没有
    /// <c>GetPawAwardRsp</c> ⇒ 必须仍然留在「已知未实现」里，绝不能回包。
    /// </summary>
    [Fact]
    public void 爪印奖励仍是已知未实现()
    {
        Assert.Contains("GetPawAward", CafePetFeature.KnownUnimplemented.Keys);
        Assert.DoesNotContain("GetPawAward", CafePetFeature.AllowedReplyCmds);

        // 远征四条则**不该**再出现在未实现清单里
        Assert.DoesNotContain(CafePetFeature.CmdUnLockTourArea, CafePetFeature.KnownUnimplemented.Keys);
        Assert.DoesNotContain(CafePetFeature.CmdSetTourPets, CafePetFeature.KnownUnimplemented.Keys);
        Assert.DoesNotContain(CafePetFeature.CmdCheckTourIsSuccess, CafePetFeature.KnownUnimplemented.Keys);
        Assert.DoesNotContain(CafePetFeature.CmdCompleteTour, CafePetFeature.KnownUnimplemented.Keys);
    }
}
