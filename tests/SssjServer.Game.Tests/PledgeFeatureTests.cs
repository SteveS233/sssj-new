using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 誓约域（LuaCall 231/232/233/234/235）。台账 `docs/05` §52、取证 `work/pledge-forensics.md`。
///
/// 断言重点（每条都对应一个「写错就静默失效」的点）：
/// <list type="number">
///   <item>★★★ 232 回的是 <b>对象包数组</b> <c>{OpenGirlList:[...]}</c>，不是裸数组
///     （<c>GirlPromise.lua:550</c> 取 <c>.OpenGirlList</c> 键；
///      而 <c>LuaCall.lua:3023</c> 的原服注释写的是裸数组，**与消费点矛盾，以消费点为准**）；</item>
///   <item>★★★ 234 是**第八种错误约定**：失败键 <c>Err</c>（判 nil），成功**绝不能带**；</item>
///   <item>★ 231/233/235 的少女对象必须含 <c>girlid/step/cardguid/taskdatas</c>；</item>
///   <item>★ 235 前置：本阶段任务全完成才允许进阶；</item>
///   <item>★ <c>isopen</c> 是**日期字符串**不是 0/1，解析要按日期判。</item>
/// </list>
/// </summary>
public class PledgeFeatureTests
{
    private static Task CallAsync(FeatureHarness h, int cmd, string tbParam)
        => h.DispatchLuaCallAsync(cmd, tbParam);

    private static JsonNode LastTbParam(FeatureHarness h)
    {
        var (_, json) = h.LastOutbound;
        return JsonNode.Parse(json)!["tbParam"]!;
    }

    // ================= 232 开放少女列表 =================

    /// <summary>
    /// ★★★ 232 必须回「对象包数组」。回成裸数组的话
    /// <c>GirlPromise.lua:550</c> 取 <c>.OpenGirlList</c> 得 nil ⇒ 界面一个少女都没有。
    /// </summary>
    [Fact]
    public async Task 开放少女列表是对象包数组()
    {
        var h = await FeatureHarness.CreateAsync(nameof(开放少女列表是对象包数组));

        await CallAsync(h, PledgeFeature.CmdReqOpenGirls, "[]");

        var tb = LastTbParam(h);
        var obj = Assert.IsType<JsonObject>(tb);           // ★ 不能是 JsonArray
        var arr = Assert.IsType<JsonArray>(obj["OpenGirlList"]);
        Assert.NotEmpty(arr);                              // 真实配置里 16 个少女的开放日期都已过
        foreach (var el in arr)
            Assert.True(el!.GetValue<int>() > 0);
    }

    // ================= 231 已激活列表 =================

    /// <summary>231 新号回空数组（顶层就是数组，<c>GirlPromise.lua:573</c> 整体赋值）。</summary>
    [Fact]
    public async Task 新号的已激活列表是空数组()
    {
        var h = await FeatureHarness.CreateAsync(nameof(新号的已激活列表是空数组));

        await CallAsync(h, PledgeFeature.CmdReqGirls, "[]");

        var arr = Assert.IsType<JsonArray>(LastTbParam(h));
        Assert.Empty(arr);
    }

    // ================= 233 激活 =================

    /// <summary>
    /// 激活成功后回**单个少女对象**，四个字段齐全
    /// （<c>GirlPromise.lua:598</c> 无保护读 <c>girlid</c>）。
    /// </summary>
    [Fact]
    public async Task 激活少女回单个对象且字段齐全()
    {
        var h = await FeatureHarness.CreateAsync(nameof(激活少女回单个对象且字段齐全));
        int girlId = FirstOpenGirl(h);
        GrantActivateCost(h, girlId);

        await CallAsync(h, PledgeFeature.CmdNewGirl, $"{{\"girlid\":{girlId}}}");

        var o = Assert.IsType<JsonObject>(LastTbParam(h));
        Assert.Equal(girlId, o["girlid"]!.GetValue<int>());
        Assert.Equal(1, o["step"]!.GetValue<int>());       // ★ step0 是占位，激活后进 1
        Assert.True(o.ContainsKey("cardguid"));
        Assert.IsType<JsonArray>(o["taskdatas"]);
    }

    /// <summary>★ 重复激活必须被拒（且不回包，避免客户端把旧数据当新的）。</summary>
    [Fact]
    public async Task 重复激活被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(重复激活被拒绝));
        int girlId = FirstOpenGirl(h);
        GrantActivateCost(h, girlId);
        await CallAsync(h, PledgeFeature.CmdNewGirl, $"{{\"girlid\":{girlId}}}");

        int before = h.Outbound.Count;
        await CallAsync(h, PledgeFeature.CmdNewGirl, $"{{\"girlid\":{girlId}}}");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>★ 材料不足不能激活（激活消耗来自 step0 的首个 ReduceItem 任务）。</summary>
    [Fact]
    public async Task 材料不足不能激活()
    {
        var h = await FeatureHarness.CreateAsync(nameof(材料不足不能激活));
        int girlId = FirstOpenGirl(h);
        // 故意不发材料

        int before = h.Outbound.Count;
        await CallAsync(h, PledgeFeature.CmdNewGirl, $"{{\"girlid\":{girlId}}}");

        Assert.Equal(before, h.Outbound.Count);
        Assert.Empty(PledgeFeature.LoadStateStatic(h.Player).Girls);
    }

    // ================= 234 完成任务（★ 第八种错误约定）=================

    /// <summary>
    /// ★★★ 成功回 <c>{id,count,status}</c> 且**绝不能带 <c>Err</c>**
    /// —— <c>GirlPromise.lua:613</c> 是 <c>if tbParam.Err == nil then</c>，
    /// 带了这个键（哪怕值是 0）都会被判成失败、任务勾不上。
    /// </summary>
    [Fact]
    public async Task 完成任务成功时绝不带Err()
    {
        var h = await FeatureHarness.CreateAsync(nameof(完成任务成功时绝不带Err));
        var (girlId, taskId) = ActivateAndPickTask(h);

        await CallAsync(h, PledgeFeature.CmdSetTaskFinish,
            $"{{\"girlid\":{girlId},\"taskid\":{taskId}}}");

        var o = Assert.IsType<JsonObject>(LastTbParam(h));
        Assert.False(o.ContainsKey("Err"), "成功回包带了 Err ⇒ 客户端会判成失败");
        Assert.Equal(taskId, o["id"]!.GetValue<int>());
        Assert.Equal(PledgeFeature.TaskStatusDone, o["status"]!.GetValue<int>());
    }

    /// <summary>★ 失败必须带 <c>Err</c>（非 nil）。</summary>
    [Fact]
    public async Task 完成不存在的任务回Err()
    {
        var h = await FeatureHarness.CreateAsync(nameof(完成不存在的任务回Err));
        var (girlId, _) = ActivateAndPickTask(h);

        await CallAsync(h, PledgeFeature.CmdSetTaskFinish,
            $"{{\"girlid\":{girlId},\"taskid\":99999999}}");

        var o = Assert.IsType<JsonObject>(LastTbParam(h));
        Assert.True(o.ContainsKey("Err"));
    }

    /// <summary>★ 重复完成同一任务要被拒（回 Err），否则可以刷进度。</summary>
    [Fact]
    public async Task 重复完成同一任务回Err()
    {
        var h = await FeatureHarness.CreateAsync(nameof(重复完成同一任务回Err));
        var (girlId, taskId) = ActivateAndPickTask(h);
        string p = $"{{\"girlid\":{girlId},\"taskid\":{taskId}}}";

        await CallAsync(h, PledgeFeature.CmdSetTaskFinish, p);
        await CallAsync(h, PledgeFeature.CmdSetTaskFinish, p);

        var o = Assert.IsType<JsonObject>(LastTbParam(h));
        Assert.True(o.ContainsKey("Err"));
    }

    // ================= 235 进阶 =================

    /// <summary>★ 任务没做完不能进阶（不回包）。</summary>
    [Fact]
    public async Task 任务未完成不能进阶()
    {
        var h = await FeatureHarness.CreateAsync(nameof(任务未完成不能进阶));
        var (girlId, _) = ActivateAndPickTask(h);

        int before = h.Outbound.Count;
        await CallAsync(h, PledgeFeature.CmdStepUpGirl, $"{{\"girlid\":{girlId}}}");

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(1, PledgeFeature.LoadStateStatic(h.Player).Girls[girlId].Step);
    }

    /// <summary>任务全完成后可以进阶，step +1 且任务列表换成新阶段的。</summary>
    [Fact]
    public async Task 任务全完成后可以进阶()
    {
        var h = await FeatureHarness.CreateAsync(nameof(任务全完成后可以进阶));
        int girlId = FirstOpenGirl(h);
        GrantActivateCost(h, girlId);
        await CallAsync(h, PledgeFeature.CmdNewGirl, $"{{\"girlid\":{girlId}}}");

        // 把当前阶段的任务全部做完（材料类的先补足材料）
        var st = PledgeFeature.LoadStateStatic(h.Player);
        foreach (var t in st.Girls[girlId].Tasks.ToList())
        {
            GrantTaskCost(h, t.Id);
            await CallAsync(h, PledgeFeature.CmdSetTaskFinish,
                $"{{\"girlid\":{girlId},\"taskid\":{t.Id}}}");
        }

        await CallAsync(h, PledgeFeature.CmdStepUpGirl, $"{{\"girlid\":{girlId}}}");

        var after = PledgeFeature.LoadStateStatic(h.Player).Girls[girlId];
        Assert.Equal(2, after.Step);
        Assert.All(after.Tasks, t => Assert.Equal(PledgeFeature.TaskStatusDoing, t.Status));
    }

    // ================= 配置表 =================

    /// <summary>
    /// ★★★ <c>isopen</c> 是**开放日期字符串**（例 <c>2019-11-5-10-40</c>），不是 0/1。
    /// 客户端 <c>tonumber</c> 它得 nil ⇒ 本地 <c>GetOpenGirlList</c> 恒空；
    /// 本服按日期判定并由服务端下发名单。
    /// </summary>
    [Theory]
    [InlineData("2019-11-5-10-40", true)]
    [InlineData("2022-06-29-8-0", true)]
    [InlineData("2099-01-01-0-0", false)]
    [InlineData("", true)]                 // 空 ⇒ 视为已开放
    [InlineData("garbage", true)]          // 解析不了 ⇒ 视为已开放（不因表变动而整域静默关闭）
    public void 开放日期解析(string raw, bool expectOpen)
    {
        var row = new PledgeStepRow { OpenTimeRaw = raw };
        Assert.Equal(expectOpen, row.IsOpenAt(new DateTime(2026, 8, 29)));
    }

    /// <summary>★ 真实表探针：16 个少女、每人多个阶段、激活任务是 ReduceItem。</summary>
    [Fact]
    public void 真实配置表结构()
    {
        var t = PledgeTable.Load(TestPaths.SettingDir);

        Assert.True(t.GirlCount >= 16, $"誓约少女数 {t.GirlCount} < 16");
        Assert.True(t.TaskCount > 600, $"誓约任务数 {t.TaskCount} 偏少");

        foreach (int gid in t.AllGirlIds.Where(g => g > 0))
        {
            Assert.NotNull(t.Step(gid, PledgeTable.BeginStep));   // step0 占位必须在
            Assert.True(t.MaxStep(gid) >= 1, $"少女 {gid} 没有可养成阶段");
        }

        // 激活消耗：step0 首个任务是 ReduceItem，四元组齐全
        int first = t.OpenGirlIds(DateTime.Now).First();
        var act = t.ActivateTask(first);
        Assert.NotNull(act);
        Assert.Equal(PledgeFeature.TypeReduceItem, act!.StrType);
        Assert.True(act.ReduceGdpl.Count >= 4, "激活任务的 gdpl 不足 4 段（短横分隔解析失败？）");
    }

    /// <summary>★ 短横分隔解析（<c>Parse2Item(x,"-")</c>，<c>pldgecommon.lua:105</c>）。</summary>
    [Theory]
    [InlineData("9-16-1-1", new[] { 9, 16, 1, 1 })]
    [InlineData("", new int[0])]
    [InlineData("100", new[] { 100 })]
    public void 短横数组解析(string raw, int[] expected)
        => Assert.Equal(expected, PledgeTable.ParseDashInts(raw));

    // ================= helper =================

    private static int FirstOpenGirl(FeatureHarness h)
    {
        var t = PledgeTable.Load(TestPaths.SettingDir);
        return t.OpenGirlIds(DateTime.Now).First(g => g > 0);
    }

    /// <summary>给玩家发够「激活某少女」所需的材料。</summary>
    private static void GrantActivateCost(FeatureHarness h, int girlId)
    {
        var t = PledgeTable.Load(TestPaths.SettingDir);
        Grant(h, t.ActivateTask(girlId));
    }

    /// <summary>给玩家发够某个任务所需的材料（非 ReduceItem 则无操作）。</summary>
    private static void GrantTaskCost(FeatureHarness h, int taskId)
    {
        var t = PledgeTable.Load(TestPaths.SettingDir);
        Grant(h, t.Task(taskId));
    }

    private static void Grant(FeatureHarness h, PledgeTaskRow? cfg)
    {
        if (cfg is null || cfg.StrType != PledgeFeature.TypeReduceItem || cfg.ReduceGdpl.Count < 4)
            return;
        var g = cfg.ReduceGdpl;
        h.Player.Items.Add(new Item
        {
            Guid = (uint)(90000 + h.Player.Items.Count),
            Genre = (uint)g[0],
            Detail = (uint)g[1],
            Particular = (uint)g[2],
            Level = (uint)g[3],
            Count = (uint)Math.Max(1, cfg.Count) * 2,
        });
    }

    /// <summary>激活第一个开放少女，返回 (girlId, 该阶段第一个任务 id)。</summary>
    private static (int GirlId, int TaskId) ActivateAndPickTask(FeatureHarness h)
    {
        int girlId = FirstOpenGirl(h);
        GrantActivateCost(h, girlId);
        CallAsync(h, PledgeFeature.CmdNewGirl, $"{{\"girlid\":{girlId}}}").GetAwaiter().GetResult();

        var st = PledgeFeature.LoadStateStatic(h.Player);
        int taskId = st.Girls[girlId].Tasks[0].Id;
        GrantTaskCost(h, taskId);
        return (girlId, taskId);
    }
}
