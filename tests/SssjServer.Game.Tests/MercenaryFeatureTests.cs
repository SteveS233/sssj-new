using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 佣兵赌注入口三跳（LuaCall 320 / 330 / 329）。台账 `docs/05` §31。
///
/// 断言重点是这一域**最容易错的三条**：
/// <list type="number">
///   <item>★★ 320 必须回 <c>nWeekId</c>，且要是**按公式算的当前周**（不是本地表的最大值）
///     —— 客户端拿它查 <c>tbMercenaryTower[weekId]</c>，查不到就**静默不开界面**；</item>
///   <item>★★ 330 的失败通道是 <c>bStart = false</c> **不是 `nError`**；</item>
///   <item>★★ 329 没得领时**不回包**（回空 <c>tbReward</c> 会弹空奖励框）。</item>
/// </list>
/// </summary>
public class MercenaryFeatureTests
{
    private static JsonObject? TryReply(FeatureHarness h, int cmd)
    {
        if (h.Outbound.Count == 0)
            return null;
        var (function, json) = h.LastOutbound;
        if (function != "LuaCall")
            return null;
        var root = JsonNode.Parse(json) as JsonObject;
        if (root?["sCmd"]?.GetValue<int>() != cmd)
            return null;
        return root["tbParam"] as JsonObject;
    }

    private static JsonObject Reply(FeatureHarness h, int cmd)
    {
        var r = TryReply(h, cmd);
        Assert.NotNull(r);
        return r!;
    }

    private static int Task(FeatureHarness h, int off)
        => h.Player.GetTask(MercenaryTable.Group, off);

    private static void SetTask(FeatureHarness h, int off, int v)
        => h.Player.SetTask(MercenaryTable.Group, off, v);

    private static MercenaryTable Table() => MercenaryTable.Load(TestPaths.SettingDir);

    // ================= 320 进塔 =================

    [Fact]
    public async Task 进塔_回当前周且非零()
    {
        using var h = await FeatureHarness.CreateAsync("merc-get");

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        var r = Reply(h, MercenaryFeature.CmdGetTower);
        int week = r["nWeekId"]!.GetValue<int>();
        // ★★ 活动现在是开放的（真机已证实）⇒ 必须回非 0，否则客户端不开界面
        Assert.True(week > 0, "回了 nWeekId=0 —— 客户端界面不会打开");
        // ★ 必须与表的公式一致
        Assert.Equal(Table().CurrentWeekId(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), week);
    }

    [Fact]
    public async Task 进塔_首次会初始化次数()
    {
        using var h = await FeatureHarness.CreateAsync("merc-init");
        var t = Table();

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // 跨周重置把次数补满
        Assert.Equal(t.WeekCount, Task(h, MercenaryTable.TaskWeekCount));
        Assert.Equal(MercenaryTable.StepStart, Task(h, MercenaryTable.TaskStep));
        Assert.True(Task(h, MercenaryTable.TaskCurWeekId) > 0);
    }

    [Fact]
    public async Task 进塔_推pf1026让界面能读到次数()
    {
        using var h = await FeatureHarness.CreateAsync("merc-push");

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // ★ 塔界面读 GetWeekMoney()/GetWeekCount()（都是任务值）⇒ 必须先推 pf 1026
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp),
            "进塔没推 pf 1026 —— 界面上的「次数 x/y」会是 0");
    }

    [Fact]
    public async Task 进塔_同一周内不重复重置()
    {
        using var h = await FeatureHarness.CreateAsync("merc-noreset");
        var t = Table();

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        // 手动消耗一次
        SetTask(h, MercenaryTable.TaskWeekCount, t.WeekCount - 1);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // ★ 同一周再进塔不能把次数刷回满（否则可无限刷）
        Assert.Equal(t.WeekCount - 1, Task(h, MercenaryTable.TaskWeekCount));
    }

    // ================= 330 开始 =================

    [Fact]
    public async Task 开始_扣次数并置step1()
    {
        using var h = await FeatureHarness.CreateAsync("merc-start");
        var t = Table();
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdStartTower, "{\"nWeekId\":1}");

        var r = Reply(h, MercenaryFeature.CmdStartTower);
        // ★★ 成功必须带 bStart = true
        Assert.True(r["bStart"]!.GetValue<bool>());
        Assert.Equal(t.WeekCount - 1, Task(h, MercenaryTable.TaskWeekCount));
        Assert.Equal(MercenaryTable.StepSelectBuff, Task(h, MercenaryTable.TaskStep));
    }

    [Fact]
    public async Task 开始_次数用尽时回bStartFalse而不是nError()
    {
        using var h = await FeatureHarness.CreateAsync("merc-nocount");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        SetTask(h, MercenaryTable.TaskWeekCount, 0);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdStartTower, "{\"nWeekId\":1}");

        var r = Reply(h, MercenaryFeature.CmdStartTower);
        // ★★ 客户端判的是 bStart，不是 nError（`MercenaryLogic.lua:550`）
        Assert.False(r["bStart"]!.GetValue<bool>());
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(MercenaryTable.StepStart, Task(h, MercenaryTable.TaskStep));
    }

    [Fact]
    public async Task 开始_续局不扣次数()
    {
        using var h = await FeatureHarness.CreateAsync("merc-resume");
        var t = Table();
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // 第一次：新开一局（扣 1）
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdStartTower, "{\"nWeekId\":1}");
        int after1 = Task(h, MercenaryTable.TaskWeekCount);

        // 第二次：step 已是 1（未打完）⇒ 续局，**不该再扣**
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdStartTower, "{\"nWeekId\":1}");

        Assert.True(Reply(h, MercenaryFeature.CmdStartTower)["bStart"]!.GetValue<bool>());
        Assert.Equal(after1, Task(h, MercenaryTable.TaskWeekCount));
        Assert.Equal(t.WeekCount - 1, after1);
    }

    [Theory]
    [InlineData(MercenaryTable.StepSelectBuff)]
    [InlineData(MercenaryTable.StepFormation)]
    [InlineData(MercenaryTable.StepFighting)]
    [InlineData(MercenaryTable.StepSettlement)]
    public async Task 开始_保留原step让客户端跳对界面(int step)
    {
        using var h = await FeatureHarness.CreateAsync($"merc-step-{step}");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        SetTask(h, MercenaryTable.TaskStep, step);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdStartTower, "{\"nWeekId\":1}");

        // ★ 客户端按 step 决定开 FirstBuff / Formation2 / LevelInfo ⇒ 服务端不能把它重置
        Assert.Equal(step, Task(h, MercenaryTable.TaskStep));
        Assert.True(Reply(h, MercenaryFeature.CmdStartTower)["bStart"]!.GetValue<bool>());
    }

    // ================= 329 领周奖励 =================

    [Fact]
    public async Task 领周奖励_没得领时不回包()
    {
        using var h = await FeatureHarness.CreateAsync("merc-noreward");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetWeekReward, "{}");

        // ★★ 原服是 `if ret then CallClient`（LuaCall.lua:3458）
        // 回空 tbReward 会让客户端弹一个空奖励框
        Assert.Equal(before, h.Outbound.Count);
    }

    [Fact]
    public async Task 领周奖励_按可领数量放大并清零()
    {
        using var h = await FeatureHarness.CreateAsync("merc-reward");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        SetTask(h, MercenaryTable.TaskCanGetMoney, 7);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetWeekReward, "{}");

        var r = Reply(h, MercenaryFeature.CmdGetWeekReward);
        var arr = Assert.IsType<JsonArray>(r["tbReward"]);
        var one = Assert.IsType<JsonArray>(arr[0]);
        // 模板 [15-44-1-1-1] × 7 ⇒ 末位数量 7
        Assert.Equal(15, one[0]!.GetValue<int>());
        Assert.Equal(44, one[1]!.GetValue<int>());
        Assert.Equal(7, one[4]!.GetValue<int>());
        // 领完清零，防重复领
        Assert.Equal(0, Task(h, MercenaryTable.TaskCanGetMoney));
    }

    [Fact]
    public async Task 领周奖励_重复领第二次不回包()
    {
        using var h = await FeatureHarness.CreateAsync("merc-reward-twice");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        SetTask(h, MercenaryTable.TaskCanGetMoney, 3);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetWeekReward, "{}");
        int after1 = h.Outbound.Count;

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetWeekReward, "{}");

        Assert.Equal(after1, h.Outbound.Count);
    }

    [Fact]
    public async Task 领周奖励_真的发道具()
    {
        using var h = await FeatureHarness.CreateAsync("merc-reward-grant");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");
        SetTask(h, MercenaryTable.TaskCanGetMoney, 5);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetWeekReward, "{}");

        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf)
                    || await h.WaitForPfAsync(GamePf.MoneyUpdateNtf),
            "领周奖励后既没推 1031 也没推 1032 —— 代币没真的发");
    }

    // ================= 跨周重置 =================

    [Fact]
    public async Task 跨周_清区段并缓存上周代币()
    {
        using var h = await FeatureHarness.CreateAsync("merc-newweek");
        var t = Table();
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // 造出「上周打过」的状态
        SetTask(h, MercenaryTable.TaskWeekMoney, 4321);
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepFighting);
        SetTask(h, MercenaryTable.TaskWeekCount, 0);
        // 把记录的周改成一个旧周 ⇒ 下次进塔触发跨周
        SetTask(h, MercenaryTable.TaskCurWeekId, 1);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // ★ 每周清空区段被清、次数补满、step 归 0
        Assert.Equal(0, Task(h, MercenaryTable.TaskWeekMoney));
        Assert.Equal(MercenaryTable.StepStart, Task(h, MercenaryTable.TaskStep));
        Assert.Equal(t.WeekCount, Task(h, MercenaryTable.TaskWeekCount));
        // ★ 上周成绩存进缓存（`MercenaryLogic.lua:5` 的语义）
        Assert.Equal(4321, Task(h, MercenaryTable.TaskCacheLastWeekMoney));
    }

    [Fact]
    public async Task 跨周_清到Task_Max覆盖两个区段()
    {
        using var h = await FeatureHarness.CreateAsync("merc-clearrange");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        // 在两个可清区段里各埋一个值
        SetTask(h, MercenaryTable.TaskSettlementClear + 5, 99);   // 结算清空段
        SetTask(h, MercenaryTable.TaskMax, 88);                   // 上界本身也要清
        SetTask(h, MercenaryTable.TaskCurWeekId, 1);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "{}");

        Assert.Equal(0, Task(h, MercenaryTable.TaskSettlementClear + 5));
        Assert.Equal(0, Task(h, MercenaryTable.TaskMax));
    }

    // ================= 健壮性 =================

    [Fact]
    public async Task 上行是数组时不抛()
    {
        using var h = await FeatureHarness.CreateAsync("merc-array");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetTower, "[]");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdStartTower, "[]");
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdGetWeekReward, "[]");
    }

    [Fact]
    public void 步骤名齐全()
    {
        Assert.Equal("开始", MercenaryFeature.StepName(0));
        Assert.Equal("等待选buff", MercenaryFeature.StepName(1));
        Assert.Equal("等待编队", MercenaryFeature.StepName(2));
        Assert.Equal("闯关", MercenaryFeature.StepName(3));
        Assert.Equal("结算", MercenaryFeature.StepName(4));
    }
    // ================================================================ 321 选 buff / 322 选编队（第十三轮）

    private static Task SelectBuffAsync(FeatureHarness h, string buff, string debuff)
        => h.DispatchLuaCallAsync(MercenaryFeature.CmdSelectBuff,
            $"{{\"nWeekId\":1,\"buff\":{buff},\"debuff\":{debuff}}}");

    private static Task SelectFormationAsync(FeatureHarness h)
        => h.DispatchLuaCallAsync(MercenaryFeature.CmdSelectFormation, "{\"nWeekId\":1}");

    /// <summary>
    /// ★★★ buff 存储是位域：bit0 = 激活标志、bit1..31 = id。
    /// 直接写 id（不左移）会让客户端 <c>LoadBits(v,1,31)</c> 解成 id/2 ⇒ 静默错乱。
    /// </summary>
    [Fact]
    public async Task 选buff按位域存储且id左移一位()
    {
        using var h = await FeatureHarness.CreateAsync("merc-buff-bits");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);

        await SelectBuffAsync(h, "[101,102]", "[201]");

        // buff：flag=0 ⇒ 值 = id << 1
        Assert.Equal(101 << 1, Task(h, MercenaryFeature.TaskBuffIdsBase + 1));
        Assert.Equal(102 << 1, Task(h, MercenaryFeature.TaskBuffIdsBase + 2));
        // debuff：flag=1 ⇒ 值 = (id << 1) | 1
        Assert.Equal((201 << 1) | 1, Task(h, MercenaryFeature.TaskDebuffIdsBase + 1));

        // ★ 反解要能还原（模拟客户端 LoadBits）
        int v = Task(h, MercenaryFeature.TaskBuffIdsBase + 1);
        Assert.Equal(0, v & 1);          // flag
        Assert.Equal(101, v >> 1);       // id
        int dv = Task(h, MercenaryFeature.TaskDebuffIdsBase + 1);
        Assert.Equal(1, dv & 1);
        Assert.Equal(201, dv >> 1);
    }

    /// <summary>★ 321 成功要把 step 推进到「等待编队」，并回包（不回 = 卡在选 buff 界面）。</summary>
    [Fact]
    public async Task 选buff推进step到等待编队并回包()
    {
        using var h = await FeatureHarness.CreateAsync("merc-buff-step");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);

        await SelectBuffAsync(h, "[101]", "[]");

        var r = Reply(h, MercenaryFeature.CmdSelectBuff);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(MercenaryTable.StepFormation, Task(h, MercenaryTable.TaskStep));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    /// <summary>★ 重新选 buff 要先清空旧槽（本轮重选，不能累加）。</summary>
    [Fact]
    public async Task 重新选buff会清空旧槽()
    {
        using var h = await FeatureHarness.CreateAsync("merc-buff-clear");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);
        await SelectBuffAsync(h, "[101,102,103]", "[]");
        Assert.Equal(103 << 1, Task(h, MercenaryFeature.TaskBuffIdsBase + 3));

        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);
        await SelectBuffAsync(h, "[201]", "[]");

        Assert.Equal(201 << 1, Task(h, MercenaryFeature.TaskBuffIdsBase + 1));
        Assert.Equal(0, Task(h, MercenaryFeature.TaskBuffIdsBase + 2));
        Assert.Equal(0, Task(h, MercenaryFeature.TaskBuffIdsBase + 3));
    }

    /// <summary>★ 超过选择上限（3）要拒绝，且带 <c>nError</c>（本族判据是 <c>nError &gt; 0</c>）。</summary>
    [Fact]
    public async Task 选buff超过上限被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-buff-toomany");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);

        await SelectBuffAsync(h, "[1,2,3,4,5]", "[]");

        var r = Reply(h, MercenaryFeature.CmdSelectBuff);
        Assert.True(r["nError"]!.GetValue<int>() > 0);
        Assert.Equal(MercenaryTable.StepSelectBuff, Task(h, MercenaryTable.TaskStep));
    }

    /// <summary>★ step 不对时要拒绝（防跳步）。</summary>
    [Fact]
    public async Task step不对时选buff被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-buff-badstep");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepStart);

        await SelectBuffAsync(h, "[101]", "[]");

        Assert.True(Reply(h, MercenaryFeature.CmdSelectBuff)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>★ 322 把 step 推进到「闯关」并回包（不回 = 卡在编队界面）。</summary>
    [Fact]
    public async Task 选编队推进step到闯关并回包()
    {
        using var h = await FeatureHarness.CreateAsync("merc-formation");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepFormation);

        await SelectFormationAsync(h);

        var r = Reply(h, MercenaryFeature.CmdSelectFormation);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(MercenaryTable.StepFighting, Task(h, MercenaryTable.TaskStep));
    }

    /// <summary>
    /// ★★ 322 对「step 不对」采取**宽容**策略：仍然推进 + 回包。
    /// 理由见实现注释 —— 客户端 <c>:1073..1082</c> 只有 ActivityEnd 会 return，
    /// 其它错误码仍会跳界面 ⇒ 拒绝会造成「拒了却又跳过去」的不一致。
    /// </summary>
    [Fact]
    public async Task 选编队对step不对采取宽容策略()
    {
        using var h = await FeatureHarness.CreateAsync("merc-formation-lenient");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepStart);

        await SelectFormationAsync(h);

        var r = Reply(h, MercenaryFeature.CmdSelectFormation);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(MercenaryTable.StepFighting, Task(h, MercenaryTable.TaskStep));
    }

    /// <summary>★ 两条命令对非法参数都不崩且都回包。</summary>
    [Theory]
    [InlineData(MercenaryFeature.CmdSelectBuff, "{}")]
    [InlineData(MercenaryFeature.CmdSelectBuff, "[]")]
    [InlineData(MercenaryFeature.CmdSelectBuff, "null")]
    [InlineData(MercenaryFeature.CmdSelectBuff, "{\"buff\":\"abc\"}")]
    [InlineData(MercenaryFeature.CmdSelectFormation, "{}")]
    [InlineData(MercenaryFeature.CmdSelectFormation, "[]")]
    public async Task 战斗链命令非法参数不崩(int cmd, string json)
    {
        using var h = await FeatureHarness.CreateAsync($"merc-bad-{cmd}-{json.GetHashCode()}");
        SetTask(h, MercenaryTable.TaskStep,
            cmd == MercenaryFeature.CmdSelectBuff
                ? MercenaryTable.StepSelectBuff
                : MercenaryTable.StepFormation);
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(cmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
    }

    /// <summary>★ <c>ReadIdArray</c> 只吃数组，别的形状一律空（不抛）。</summary>
    [Theory]
    [InlineData("[1,2,3]", 3)]
    [InlineData("[\"4\",5]", 2)]
    [InlineData("[0,-1,2]", 1)]
    [InlineData("{}", 0)]
    [InlineData("5", 0)]
    [InlineData("null", 0)]
    public void ReadIdArray只吃正整数数组(string json, int expected)
        => Assert.Equal(expected,
            MercenaryFeature.ReadIdArray(System.Text.Json.Nodes.JsonNode.Parse(json)).Count);
    // ================================================================ 323/324/325/326（第十三轮）

    private static Task EnterAsync(FeatureHarness h, int levelId, int weekId = 1)
        => h.DispatchLuaCallAsync(MercenaryFeature.CmdEnterTower,
            $"{{\"nWeekId\":{weekId},\"nLevelId\":{levelId},\"nFormationId\":1}}");

    private static Task PassAsync(FeatureHarness h, int levelId, int weekId = 1)
        => h.DispatchLuaCallAsync(MercenaryFeature.CmdPassTower,
            $"{{\"nWeekId\":{weekId},\"nLevelId\":{levelId},\"nFormationId\":1,"
            + "\"tbKillMonster\":[],\"nUseTime\":30,\"tbActiveBuff\":[]}");

    /// <summary>从真实表里挑一个有首通奖励的关卡（★ 不写死 id）。</summary>
    private static int PickLevelWithAward()
    {
        var t = Table();
        for (int id = 1; id <= 500; id++)
        {
            var lv = t.LevelOf(id);
            if (lv is not null && lv.FirstAward.Count > 0)
                return id;
        }
        return 0;
    }

    /// <summary>★ 关卡表能载入（表头解错 ⇒ 静默读空 的回归闸）。</summary>
    [Fact]
    public void 佣兵关卡表能载入且非空()
    {
        var t = Table();
        Assert.True(t.LevelCount > 0, "MercenaryLevel.txt 一关都没解出来，疑似表头行数解错");
        Assert.True(t.LevelCount > 200, $"只解出 {t.LevelCount} 关，现网应有 288 关");
    }

    /// <summary>
    /// ★★ 奖励列是 <c>FixAward</c>（不是主线的 <c>FixedAward</c>）——
    /// 抄主线列名会让固定奖励整列读空、静默不发奖。这条断言首通奖励能解出来。
    /// </summary>
    [Fact]
    public void 关卡奖励列能正确解析()
    {
        int id = PickLevelWithAward();
        Assert.True(id > 0, "找不到带首通奖励的关卡，用例前提不成立");
        var lv = Table().LevelOf(id)!;
        Assert.NotEmpty(lv.FirstAward);
        // 每项必须是 5 元组（★ 佣兵这三列是纯 5 位，没有第 6 位）
        Assert.All(lv.FirstAward, a => Assert.Equal(5, a.Length));
    }

    /// <summary>
    /// ★★★ 323 必须回 <c>tbData = {nWeekId, nLevelId}</c> ——
    /// 客户端下一句就取 <c>tbAllLevelConfig[levelId].LevelType</c>，缺了就抛。
    /// </summary>
    [Fact]
    public async Task 进关回tbData含周与关卡id()
    {
        using var h = await FeatureHarness.CreateAsync("merc-enter");
        int id = PickLevelWithAward();

        await EnterAsync(h, id, weekId: 7);

        var r = Reply(h, MercenaryFeature.CmdEnterTower);
        Assert.False(r.ContainsKey("nError"));
        var tbData = Assert.IsType<JsonObject>(r["tbData"]);
        Assert.Equal(7, tbData["nWeekId"]!.GetValue<int>());
        Assert.Equal(id, tbData["nLevelId"]!.GetValue<int>());
    }

    /// <summary>★ 不存在的关卡要回 <c>nError</c>（不能静默 —— 客户端会 nil 解引用）。</summary>
    [Fact]
    public async Task 进关未知关卡被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-enter-bad");

        await EnterAsync(h, 999999);

        Assert.True(Reply(h, MercenaryFeature.CmdEnterTower)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>
    /// ★★★ 324 的 <c>tbData</c> 必须是**数组**（客户端 <c>#tbParam.tbData</c> 取长度）。
    /// 回对象会让剧情关分支的 <c>#</c> 得 0、奖励弹窗不出现。
    /// </summary>
    [Fact]
    public async Task 通关回包的tbData是数组()
    {
        using var h = await FeatureHarness.CreateAsync("merc-pass-arr");
        int id = PickLevelWithAward();

        await PassAsync(h, id);

        var r = Reply(h, MercenaryFeature.CmdPassTower);
        Assert.False(r.ContainsKey("nError"));
        var arr = Assert.IsType<JsonArray>(r["tbData"]);
        Assert.NotEmpty(arr);      // 该关有首通奖励
        // 每项是 GDPLN 五元组
        foreach (var one in arr)
            Assert.True(Assert.IsType<JsonArray>(one).Count >= 5);
    }

    /// <summary>★ 通关要记两处次数（每轮清空的 + 每周累计的）。</summary>
    [Fact]
    public async Task 通关记录两处次数()
    {
        using var h = await FeatureHarness.CreateAsync("merc-pass-count");
        int id = PickLevelWithAward();
        int slot = id % MercenaryFeature.LevelPassSlots + 1;

        await PassAsync(h, id);

        Assert.Equal(1, Task(h, MercenaryFeature.TaskLevelPassBase + slot));
        Assert.Equal(1, Task(h, MercenaryFeature.TaskWeekLevelPassBase + slot));
    }

    /// <summary>★★ 首通奖励只发一次（第二次通关不再给首通那部分）。</summary>
    [Fact]
    public async Task 首通奖励只发一次()
    {
        using var h = await FeatureHarness.CreateAsync("merc-pass-first");
        int id = PickLevelWithAward();

        await PassAsync(h, id);
        int firstCount = Assert.IsType<JsonArray>(
            Reply(h, MercenaryFeature.CmdPassTower)["tbData"]).Count;

        await PassAsync(h, id);
        int secondCount = Assert.IsType<JsonArray>(
            Reply(h, MercenaryFeature.CmdPassTower)["tbData"]).Count;

        Assert.True(secondCount < firstCount,
            $"第二次通关仍发了 {secondCount} 项（首次 {firstCount}）⇒ 首通奖励重复发放");
    }

    /// <summary>
    /// ★★★ 325 **不回包**（客户端 <c>OnFailTower</c> 函数体是空的、
    /// 界面切换由 <c>map:GameEnd(false,…)</c> 自己完成）。
    /// </summary>
    [Fact]
    public async Task 失败不回包()
    {
        using var h = await FeatureHarness.CreateAsync("merc-fail");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdFailTower, "{}");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// ★★★ 326 必须**先把 step 推成 4 再回包** ——
    /// 客户端 <c>OnSettlement</c> 读的是**任务值**（<c>GetStep() == 4</c>），
    /// 不是回包内容；顺序反了结算界面就不打开。
    /// </summary>
    [Fact]
    public async Task 结算把step推成4并清零本轮次数()
    {
        using var h = await FeatureHarness.CreateAsync("merc-settle");
        int id = PickLevelWithAward();
        int slot = id % MercenaryFeature.LevelPassSlots + 1;
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepFighting);
        await PassAsync(h, id);
        Assert.Equal(1, Task(h, MercenaryFeature.TaskLevelPassBase + slot));

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSettlement, "{}");

        var r = Reply(h, MercenaryFeature.CmdSettlement);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(MercenaryTable.StepSettlement, Task(h, MercenaryTable.TaskStep));
        // ★ Task_LevelPass 注释写明「每轮清空」
        Assert.Equal(0, Task(h, MercenaryFeature.TaskLevelPassBase + slot));
        // ★ 每周累计的那个不清
        Assert.Equal(1, Task(h, MercenaryFeature.TaskWeekLevelPassBase + slot));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    /// <summary>★ 未开局（step 0）时结算要拒绝。</summary>
    [Fact]
    public async Task 未开局时结算被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-settle-nostart");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepStart);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSettlement, "{}");

        Assert.True(Reply(h, MercenaryFeature.CmdSettlement)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>★ 四条命令对非法参数都不崩。</summary>
    [Theory]
    [InlineData(MercenaryFeature.CmdEnterTower, "{}")]
    [InlineData(MercenaryFeature.CmdEnterTower, "[]")]
    [InlineData(MercenaryFeature.CmdPassTower, "{}")]
    [InlineData(MercenaryFeature.CmdPassTower, "null")]
    [InlineData(MercenaryFeature.CmdFailTower, "[]")]
    [InlineData(MercenaryFeature.CmdSettlement, "[]")]
    public async Task 战斗链四条命令非法参数不崩(int cmd, string json)
    {
        using var h = await FeatureHarness.CreateAsync($"merc-cl-{cmd}-{json.GetHashCode()}");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepFighting);

        await h.DispatchLuaCallAsync(cmd, json);
        Assert.True(true);      // 不抛即通过
    }
    // ================================================================ 327/328/331/332/333（第十三轮）

    /// <summary>★ 本周关卡清单能解析（<c>LevelIds</c> 是**逗号**分隔）。</summary>
    [Fact]
    public void 周关卡清单能解析()
    {
        var t = Table();
        // 任取一个已知 WeekId，序号应能算出来
        int found = 0;
        for (int lv = 1; lv <= 300 && found == 0; lv++)
            found = t.LevelIndexInWeek(lv);
        Assert.True(found > 0, "LevelIds 一条都没解出来，疑似分隔符用错（应为逗号）");
    }

    /// <summary>
    /// ★★ 327 上行是**裸数组**（不是 <c>{debuff:[…]}</c>），且要真的把槽位清零。
    /// </summary>
    [Fact]
    public async Task 删除debuff按裸数组解析并清零槽位()
    {
        using var h = await FeatureHarness.CreateAsync("merc-deldebuff");
        SetTask(h, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);
        await SelectBuffAsync(h, "[]", "[201,202]");
        Assert.Equal((201 << 1) | 1, Task(h, MercenaryFeature.TaskDebuffIdsBase + 1));

        // ★ 裸数组
        await h.DispatchLuaCallAsync(MercenaryFeature.CmdDeleteDebuff, "[201]");

        var r = Reply(h, MercenaryFeature.CmdDeleteDebuff);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(0, Task(h, MercenaryFeature.TaskDebuffIdsBase + 1));      // 被清掉
        Assert.Equal((202 << 1) | 1, Task(h, MercenaryFeature.TaskDebuffIdsBase + 2)); // 另一个不动
    }

    /// <summary>★ 327 解析不出 id 要拒绝（带 nError）。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task 删除debuff空参数被拒绝(string json)
    {
        using var h = await FeatureHarness.CreateAsync("merc-deldebuff-bad" + json.GetHashCode());

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdDeleteDebuff, json);

        Assert.True(Reply(h, MercenaryFeature.CmdDeleteDebuff)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>
    /// ★ 328 上行的 fate 字段名是 <c>FataId</c>（原厂拼写少个 e），选择结果要落存档。
    /// </summary>
    [Fact]
    public async Task 选fate落存档()
    {
        using var h = await FeatureHarness.CreateAsync("merc-fate");
        int levelId = PickLevelWithAward();
        int slot = levelId % MercenaryFeature.LevelPassSlots + 1;

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSelectFate,
            $"{{\"LevelId\":{levelId},\"FataId\":3,\"Index\":2,\"WeekId\":1}}");

        var r = Reply(h, MercenaryFeature.CmdSelectFate);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(2, Task(h, MercenaryFeature.TaskFateBase + slot));
    }

    /// <summary>★ 328 参数非法要拒绝。</summary>
    [Fact]
    public async Task 选fate非法参数被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-fate-bad");

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSelectFate, "{}");

        Assert.True(Reply(h, MercenaryFeature.CmdSelectFate)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>
    /// ★★★ 331 **回 nil 会弹「无效」提示** —— 必须回一个非空对象才能开历史界面
    /// （原厂把字段填充全注释了，所以内容不重要、非 nil 才重要）。
    /// </summary>
    [Fact]
    public async Task 回溯数据必须回非空对象()
    {
        using var h = await FeatureHarness.CreateAsync("merc-recalldata");

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdRecallLevelData, "{\"nNodeId\":3}");

        var r = Reply(h, MercenaryFeature.CmdRecallLevelData);
        Assert.NotNull(r);
        Assert.Equal(3, r["nNodeId"]!.GetValue<int>());
        // 四个容器都要在（客户端历史界面会遍历）
        foreach (var k in new[] { "blood", "shield", "buff", "debuff" })
            Assert.IsType<JsonArray>(r[k]);
    }

    /// <summary>★ 332 回溯要扣本周次数。</summary>
    [Fact]
    public async Task 回溯扣本周次数()
    {
        using var h = await FeatureHarness.CreateAsync("merc-recall");
        SetTask(h, MercenaryTable.TaskWeekCount, 3);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdRecall, "{\"nNodeId\":2}");

        var r = Reply(h, MercenaryFeature.CmdRecall);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal(2, Task(h, MercenaryTable.TaskWeekCount));
        Assert.Equal(MercenaryTable.StepFighting, Task(h, MercenaryTable.TaskStep));
    }

    /// <summary>★★ 次数用尽时回溯要拒绝，且失败码是 <b>1</b>（客户端弹「次数不足」）。</summary>
    [Fact]
    public async Task 次数用尽时回溯被拒绝且码为1()
    {
        using var h = await FeatureHarness.CreateAsync("merc-recall-none");
        SetTask(h, MercenaryTable.TaskWeekCount, 0);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdRecall, "{\"nNodeId\":2}");

        Assert.Equal(1, Reply(h, MercenaryFeature.CmdRecall)["nError"]!.GetValue<int>());
    }

    /// <summary>
    /// ★★ 333 跳关的积分门槛：<c>maxIdx = max(上周,本周) / 100</c>，
    /// 关卡序号必须 ≤ maxIdx。积分为 0 时任何关都跳不了。
    /// </summary>
    [Fact]
    public async Task 积分不足时跳关被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-skip-poor");
        SetTask(h, MercenaryTable.TaskWeekMoney, 0);
        SetTask(h, MercenaryTable.TaskCacheLastWeekMoney, 0);
        int levelId = PickLevelWithAward();

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSkipLevel,
            $"{{\"nLevelId\":{levelId},\"nWeekId\":1}}");

        Assert.True(Reply(h, MercenaryFeature.CmdSkipLevel)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>
    /// ★ 积分足够时跳关成功，回 <c>tbAward</c>（客户端用 <c>next()</c> 判空 ⇒ 空数组安全）。
    /// </summary>
    [Fact]
    public async Task 积分足够时跳关成功并回tbAward()
    {
        using var h = await FeatureHarness.CreateAsync("merc-skip-ok");
        // 给足积分（100 分 = 可跳 1 关，给 100000 保证覆盖）
        SetTask(h, MercenaryTable.TaskWeekMoney, 100_000);

        // 找一个「在某周清单里」的关卡
        var t = Table();
        int levelId = 0;
        for (int lv = 1; lv <= 300; lv++)
        {
            if (t.LevelIndexInWeek(lv) > 0 && t.LevelOf(lv) is not null)
            {
                levelId = lv;
                break;
            }
        }
        Assert.True(levelId > 0, "找不到在周清单里的关卡，用例前提不成立");

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSkipLevel,
            $"{{\"nLevelId\":{levelId},\"nWeekId\":1}}");

        var r = Reply(h, MercenaryFeature.CmdSkipLevel);
        Assert.False(r.ContainsKey("nError"));
        Assert.IsType<JsonArray>(r["tbAward"]);
        // 通关次数要记上
        int slot = levelId % MercenaryFeature.LevelPassSlots + 1;
        Assert.Equal(1, Task(h, MercenaryFeature.TaskLevelPassBase + slot));
    }

    /// <summary>★ 未知关卡跳关要拒绝。</summary>
    [Fact]
    public async Task 跳关未知关卡被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("merc-skip-bad");
        SetTask(h, MercenaryTable.TaskWeekMoney, 100_000);

        await h.DispatchLuaCallAsync(MercenaryFeature.CmdSkipLevel,
            "{\"nLevelId\":999999,\"nWeekId\":1}");

        Assert.True(Reply(h, MercenaryFeature.CmdSkipLevel)["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>★ 五条命令对非法参数都不崩。</summary>
    [Theory]
    [InlineData(MercenaryFeature.CmdDeleteDebuff, "5")]
    [InlineData(MercenaryFeature.CmdSelectFate, "[]")]
    [InlineData(MercenaryFeature.CmdRecallLevelData, "{}")]
    [InlineData(MercenaryFeature.CmdRecallLevelData, "[]")]
    [InlineData(MercenaryFeature.CmdRecall, "{}")]
    [InlineData(MercenaryFeature.CmdSkipLevel, "[]")]
    public async Task 回溯与跳关命令非法参数不崩(int cmd, string json)
    {
        using var h = await FeatureHarness.CreateAsync($"merc-r-{cmd}-{json.GetHashCode()}");
        SetTask(h, MercenaryTable.TaskWeekCount, 3);

        await h.DispatchLuaCallAsync(cmd, json);
        Assert.True(true);
    }
}
