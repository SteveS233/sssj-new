using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 「限时领电力」（活动 8，上行 <c>NormalActivityGetAward</c> → 下行 <c>NormalActivityMsg nType=4</c>）。
///
/// 取证锚点（详见 <c>work/activity-award-forensics.md</c>）：
/// <list type="number">
/// <item>上行只有 <c>{nActivityId=8}</c>（<c>UI_EnergyGet.lua:129..131</c>）——
///   **不带时段号**，服务端自己按时间判。</item>
/// <item>状态在 group 20 / taskId <c>10000 + 时段Id</c>，客户端判 <b><c>== 1</c></b>
///   （<c>UI_EnergyGet.lua:80</c>）。</item>
/// <item>下行必须带 <c>nEnergy</c>（两条文案里的 <c>%d</c>，缺了客户端 <c>string.format</c> 抛）
///   与 <c>bFullAdd</c>（<c>NormalActivityConfig.lua:1692..1700</c>）。</item>
/// <item>★ <c>bFullAdd</c> 是「有没有超上限」而非「成功/失败」：
///   <c>true</c> → 「成功收取%d电力」；<c>false</c> → 「拥有电力已达上限，本次获得%d电力」
///   （原文从 <c>work/apk/assets/lang.u</c> 解出）⇒ 体力满时**照发**。</item>
/// <item>★ 本族全链路**无 <c>nError</c> 检查</c> ⇒ 成功不带 <c>nError</c>、失败静默不回。</item>
/// </list>
/// </summary>
public class EnergyGetFeatureTests
{
    // ================= 配置表 =================

    /// <summary>
    /// 现网表就是 2 行：11:00-14:00 与 18:00-20:00，各 30 电力。
    /// ★ 探针型断言：将来配置表变了会立刻红，提醒同步实现。
    /// </summary>
    [Fact]
    public void 配置表_两个时段各30电力()
    {
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);

        Assert.Equal(2, table.Count);

        var s1 = table.SlotById(1);
        Assert.NotNull(s1);
        Assert.Equal(1100, s1!.BeginTime);
        Assert.Equal(1400, s1.EndTime);
        Assert.Equal(30, s1.Energy);

        var s2 = table.SlotById(2);
        Assert.NotNull(s2);
        Assert.Equal(1800, s2!.BeginTime);
        Assert.Equal(2000, s2.EndTime);
        Assert.Equal(30, s2.Energy);
    }

    /// <summary>★ 时段判定左闭右开（与抽卡一致、与商店的两端闭相反）。</summary>
    [Theory]
    [InlineData(10, 59, null)]   // 差 1 分钟
    [InlineData(11, 0, 1)]       // 左端点：含
    [InlineData(13, 59, 1)]
    [InlineData(14, 0, null)]    // ★ 右端点：不含
    [InlineData(17, 30, null)]   // 两个时段中间
    [InlineData(18, 0, 2)]
    [InlineData(19, 59, 2)]
    [InlineData(20, 0, null)]    // ★ 右端点：不含
    [InlineData(23, 30, null)]
    public void 时段判定_左闭右开(int hour, int minute, int? expectedSlotId)
    {
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);
        var now = new DateTimeOffset(2025, 8, 19, hour, minute, 0, TimeSpan.Zero);

        var slot = table.SlotAt(now);

        if (expectedSlotId is null)
            Assert.Null(slot);
        else
            Assert.Equal(expectedSlotId.Value, slot!.Id);
    }

    /// <summary>空表 = 任何时刻都不在时段内（降级到「静默拒绝」而不是「乱发电力」）。</summary>
    [Fact]
    public void 空表_任何时刻都不在时段内()
    {
        Assert.Equal(0, EnergyActivityTable.Empty.Count);
        Assert.Null(EnergyActivityTable.Empty.SlotAt(new DateTimeOffset(2025, 8, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    /// <summary>taskId 的编码：<c>10000 + 时段Id</c>（<c>UI_EnergyGet.lua:80</c>）。</summary>
    [Fact]
    public void taskId编码()
    {
        Assert.Equal(10000, EnergyGetFeature.TaskSlotBase);
        Assert.Equal(10001, EnergyGetFeature.TaskSlotBase + 1);
        Assert.Equal(10002, EnergyGetFeature.TaskSlotBase + 2);
        // ★ 与每日签到同组（group 20）
        Assert.Equal(SignUpFeature.TaskGroup, EnergyGetFeature.TaskGroup);
        // ★ 客户端判 == 1，只能写 1
        Assert.Equal(1, EnergyGetFeature.TaskClaimed);
    }

    /// <summary>
    /// ★★ 时段判定必须用**本地时分**，不是 UTC。
    ///
    /// <para>取证 <c>UI_EnergyGet.lua:39..43</c>：原厂**注释掉了** <c>GetTime()</c> 那版，
    /// 实际用 <c>Game.Utils.DateTime('HHmm')</c>（RVA <c>0x1537064</c>）
    /// → <c>Game.ServerStatus.LocalTime</c>（<c>0x17C1858</c>）= 时间戳 + 本地时区偏移。</para>
    ///
    /// <para>判据：同一个**绝对时刻**、不同时区偏移 ⇒ 判定结果应当不同。
    /// 若实现误用 UTC，这条会红。（实测开发环境设备 CST/UTC+8、宿主 JST/UTC+9、
    /// UTC 相差 8~9 小时 ⇒ 用 UTC 会「客户端显示可领、服务端拒绝」。）</para>
    /// </summary>
    [Fact]
    public void 时段判定_用本地时分而非UTC()
    {
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);

        // 当地 12:00（此处 UTC 也是 12:00）→ 时段 1
        Assert.Equal(1, table.SlotAt(new DateTimeOffset(2025, 8, 19, 12, 0, 0, TimeSpan.Zero))!.Id);
        // 当地 21:00（同一绝对时刻 = UTC 12:00）→ 不在任何时段
        Assert.Null(table.SlotAt(new DateTimeOffset(2025, 8, 19, 21, 0, 0, TimeSpan.FromHours(9))));
        // 当地 12:30（UTC 03:30）→ 仍是时段 1
        Assert.Equal(1, table.SlotAt(new DateTimeOffset(2025, 8, 19, 12, 30, 0, TimeSpan.FromHours(9)))!.Id);
    }

    /// <summary>
    /// 跨午夜时段（<c>EndTime &lt; BeginTime</c>）的判据，与客户端
    /// <c>UI_EnergyGet.lua:56</c> 那条 <c>elseif</c> 同构。
    /// 现网两行都不跨午夜，但配置改了不能错。
    /// </summary>
    [Theory]
    [InlineData(2300, true)]
    [InlineData(2359, true)]
    [InlineData(0, true)]      // 00:00 属于「< EndTime」那半
    [InlineData(159, true)]
    [InlineData(200, false)]   // 右端点不含
    [InlineData(1200, false)]
    [InlineData(2259, false)]
    public void 跨午夜时段判定(int hhmm, bool expected)
    {
        var slot = new EnergySlot(9, BeginTime: 2300, EndTime: 200, Energy: 30);
        Assert.True(slot.WrapsMidnight);
        Assert.Equal(expected, slot.Contains(hhmm));
    }

    /// <summary>
    /// 「领电力用的今天」= **本地时间**减 4 小时的 (月,日)。
    /// 03:59 仍算前一天、04:00 翻页 —— 本作所有「每日刷新」都是凌晨 4 点。
    /// ★ 与 <see cref="SignUpFeature.SignDay"/> 的差别只在时区基准（那个用 UTC），
    /// 理由见 <see cref="EnergyGetFeature.EnergyDay"/> 注释。
    /// </summary>
    [Fact]
    public void EnergyDay_偏移四小时且用本地时间()
    {
        Assert.Equal((8, 18), EnergyGetFeature.EnergyDay(
            new DateTimeOffset(2025, 8, 19, 3, 59, 0, TimeSpan.Zero)));
        Assert.Equal((8, 19), EnergyGetFeature.EnergyDay(
            new DateTimeOffset(2025, 8, 19, 4, 0, 0, TimeSpan.Zero)));
        // 月初 03:00 回退到上个月最后一天
        Assert.Equal((7, 31), EnergyGetFeature.EnergyDay(
            new DateTimeOffset(2025, 8, 1, 3, 0, 0, TimeSpan.Zero)));
        // ★ 用当地日期：+09:00 表示下的 08-19 12:00 就是 8/19
        Assert.Equal((8, 19), EnergyGetFeature.EnergyDay(
            new DateTimeOffset(2025, 8, 19, 12, 0, 0, TimeSpan.FromHours(9))));
    }

    // ================= 上行链路 =================

    /// <summary>
    /// 该 Feature 必须被真实 <c>GameModule</c> 装配（否则真机打 unhandled、点了没反应）。
    /// 判据：装配后 <c>LuaCalls</c> 认识这个 sFunction。
    /// </summary>
    [Fact]
    public async Task 装配_上行已注册()
    {
        using var h = await FeatureHarness.CreateAsync("energy-install");

        Assert.True(h.LuaCalls.HasFunction(EnergyGetFeature.UpFunction));
    }

    /// <summary>非活动 8 的请求不处理（其余活动属另一条链或已过期），且**不回包**。</summary>
    [Fact]
    public async Task 其它活动id_不回包()
    {
        using var h = await FeatureHarness.CreateAsync("energy-other");
        int before = h.Outbound.Count;

        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction, "{\"nActivityId\":20}");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// ★ 真机上行可能是 <c>JsonArray</c>（本项目踩过：<c>JsonNode["key"]</c> 对数组会**抛**）。
    /// 必须不抛、也不回包。
    /// </summary>
    [Fact]
    public async Task 上行是数组_不抛异常()
    {
        using var h = await FeatureHarness.CreateAsync("energy-array");
        int before = h.Outbound.Count;

        // 不抛异常就算过（DispatchFunctionAsync 会把 handler 的异常冒泡出来）
        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction, "[]");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>不在时段内：静默不回（客户端本来就把按钮置灰了）。</summary>
    [Fact]
    public async Task 不在时段内_静默不回()
    {
        using var h = await FeatureHarness.CreateAsync("energy-outside");
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);

        // 只在「当前真的不在任何时段内」时断言 —— 否则这条会随执行时刻漂移
        if (table.SlotAt(DateTimeOffset.Now) is not null)
            return;

        int before = h.Outbound.Count;
        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction,
                                      $"{{\"nActivityId\":{EnergyGetFeature.PowerActivityId}}}");

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(0, h.Player.GetTask(EnergyGetFeature.TaskGroup, EnergyGetFeature.TaskSlotBase + 1));
        Assert.Equal(0, h.Player.GetTask(EnergyGetFeature.TaskGroup, EnergyGetFeature.TaskSlotBase + 2));
    }

    /// <summary>
    /// 在时段内：回包字段齐全 + 电力真的进账 + 任务值置 1。
    /// ★ 与上一条互补 —— 两条合起来覆盖全天任意执行时刻。
    /// </summary>
    [Fact]
    public async Task 在时段内_发电力并置任务值()
    {
        using var h = await FeatureHarness.CreateAsync("energy-inside");
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);
        var slot = table.SlotAt(DateTimeOffset.Now);
        if (slot is null)
            return;   // 不在时段内，交给上一条测试

        int before = h.Player.Vigour;

        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction,
                                      $"{{\"nActivityId\":{EnergyGetFeature.PowerActivityId}}}");

        // 电力真的加了
        Assert.Equal(before + slot.Energy, h.Player.Vigour);
        // 任务值置 1（客户端判 == 1）
        Assert.Equal(EnergyGetFeature.TaskClaimed,
                     h.Player.GetTask(EnergyGetFeature.TaskGroup, EnergyGetFeature.TaskSlotBase + slot.Id));

        // 回包字段
        var (function, json) = h.LastOutbound;
        Assert.Equal(EnergyGetFeature.DownFunction, function);
        var tb = JsonNode.Parse(json)!;
        Assert.Equal(EnergyGetFeature.MsgTypeEnergy, tb["nType"]!.GetValue<int>());
        Assert.Equal(slot.Energy, tb["nEnergy"]!.GetValue<int>());
        Assert.NotNull(tb["bFullAdd"]);
        // ★ 本族无 nError 检查 ⇒ 成功包不带它
        Assert.Null(tb["nError"]);
    }

    /// <summary>同一时段重复领：静默不回、不重复加电力。</summary>
    [Fact]
    public async Task 同一时段重复领_被拒()
    {
        using var h = await FeatureHarness.CreateAsync("energy-twice");
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);
        if (table.SlotAt(DateTimeOffset.Now) is null)
            return;

        string param = $"{{\"nActivityId\":{EnergyGetFeature.PowerActivityId}}}";
        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction, param);
        int vigourAfterFirst = h.Player.Vigour;
        int outboundAfterFirst = h.Outbound.Count;

        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction, param);

        Assert.Equal(vigourAfterFirst, h.Player.Vigour);
        Assert.Equal(outboundAfterFirst, h.Outbound.Count);
    }

    /// <summary>
    /// ★ 体力已满时**照发**，只把 <c>bFullAdd</c> 置 false
    /// （文案「拥有电力已达上限，本次获得%d电力」—— 是发放成功的提示，不是失败）。
    /// </summary>
    [Fact]
    public async Task 体力已满_照发但bFullAdd为false()
    {
        using var h = await FeatureHarness.CreateAsync("energy-full");
        var table = EnergyActivityTable.Load(TestPaths.SettingDir);
        var slot = table.SlotAt(DateTimeOffset.Now);
        if (slot is null)
            return;

        // 先把体力顶到上限
        int max = PlayerExpTable.Load(TestPaths.SettingDir).MaxVigour(h.Player.Level);
        h.Player.AddMoney(MoneyType.Vigour, max);
        int before = h.Player.Vigour;

        await h.DispatchFunctionAsync(EnergyGetFeature.UpFunction,
                                      $"{{\"nActivityId\":{EnergyGetFeature.PowerActivityId}}}");

        // 照发（可超上限）
        Assert.Equal(before + slot.Energy, h.Player.Vigour);
        var tb = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.False(tb["bFullAdd"]!.GetValue<bool>());
        Assert.Equal(slot.Energy, tb["nEnergy"]!.GetValue<int>());
    }

    // ================= 跨日重置 =================

    /// <summary>
    /// ★★ 跨日必须清零，否则第二天永远显示「已领」。
    /// 客户端从不写这两个 taskId（<c>grep 'SetTaskValue.*10000'</c> 零命中）⇒ 责任全在服务端。
    /// </summary>
    [Fact]
    public void 跨日重置_清零已领标记()
    {
        var feature = NewLoadedFeature();
        var player = new Player("energy-reset", 1);

        // 造一个「昨天领过」的状态：置标记 + 把记录日写成一个不可能是今天的值
        player.SetTask(EnergyGetFeature.TaskGroup, EnergyGetFeature.TaskSlotBase + 1,
                       EnergyGetFeature.TaskClaimed);
        player.Extra[EnergyGetFeature.ExtraKey] = new JsonObject { ["month"] = 1, ["day"] = 1 };

        var changed = feature.ResetIfNeeded(player);

        Assert.Contains(changed, c => Player.TaskIdOf(c.Key) == EnergyGetFeature.TaskSlotBase + 1
                                      && c.Value == 0);
        Assert.Equal(0, player.GetTask(EnergyGetFeature.TaskGroup, EnergyGetFeature.TaskSlotBase + 1));
    }

    /// <summary>同一天内重复调重置：无变化（不该反复推 pf 1026）。</summary>
    [Fact]
    public void 同一天重复重置_无变化()
    {
        var feature = NewLoadedFeature();
        var player = new Player("energy-idem", 2);

        feature.ResetIfNeeded(player);                  // 第一次：写下今天
        var second = feature.ResetIfNeeded(player);

        Assert.Empty(second);
    }

    /// <summary>
    /// 造一个「已加载真实配置表」的 Feature 实例。
    /// <see cref="EnergyGetFeature.ResetIfNeeded"/> 只依赖配置表里的时段列表，
    /// 不依赖 <c>Install</c> 里的其余装配，所以这样够用且不必起 harness。
    /// </summary>
    private static EnergyGetFeature NewLoadedFeature()
    {
        var feature = new EnergyGetFeature();
        feature.LoadTableForTest(TestPaths.SettingDir);
        return feature;
    }
}
