using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 每日签到（上行 <c>SignUpMsg</c> → 下行 <c>NormalActivityMsg</c>）。
///
/// 断言的取证锚点：
/// <list type="number">
/// <item>上下行**不同名**：下行必须是 <c>NormalActivityMsg</c>（<c>S2CCall.lua:329</c>），
///   按上行名回会被 <c>S2C.ProcCmd</c> 静默丢弃。</item>
/// <item>下行必须带 <c>nType=3</c> / <c>nSubType</c> / <c>bSuccess=true</c>
///   （<c>NormalActivityConfig.lua:1702..1717</c>：<c>bSuccess</c> 为假时整个分支什么都不做，
///   客户端连失败提示都没有）。</item>
/// <item>状态全在任务值 group 20：11001 今天签没签、11002 本月总次数、11003 补签次数
///   （<c>NormalActivityConfig.lua:11..17</c>），客户端 <c>Refresh</c> 直接读它们
///   （<c>UI_SignActivityPop.lua:107..108</c>）。</item>
/// <item>「每天 4 点」的口径是 <c>GetTime() - 4*3600</c> 的日历日
///   （<c>UI_SignActivityPop.lua:103</c>）。</item>
/// </list>
/// </summary>
public class SignUpFeatureTests
{
    // ================= 配置表 =================

    [Fact]
    public void SignUpTable_LoadsTwelveMonths()
    {
        var table = SignUpTable.Load(TestPaths.SettingDir);
        // 现网表是 366 行（12 个月 × 各月天数）
        Assert.Equal(366, table.Count);
        // 8 月第 1 次签到 = [15-1-1-1-5000]（金币 5000）
        var award = table.Award(8, 1);
        Assert.Single(award);
        Assert.Equal(new[] { 15, 1, 1, 1, 5000 }, award[0]);
        Assert.Equal(31, table.MaxCount(8));
    }

    /// <summary>
    /// 「签到用的今天」= 服务器时间减 4 小时后的 (月,日)。
    /// 03:59 仍算前一天，04:00 才翻页 —— 与客户端 <c>GetTime() - 4*3600</c> 一致。
    /// </summary>
    [Fact]
    public void SignDay_ShiftsByFourHours()
    {
        Assert.Equal((8, 18), SignUpFeature.SignDay(new DateTimeOffset(2025, 8, 19, 3, 59, 0, TimeSpan.Zero)));
        Assert.Equal((8, 19), SignUpFeature.SignDay(new DateTimeOffset(2025, 8, 19, 4, 0, 0, TimeSpan.Zero)));
        // 月初 03:00 会回退到上个月最后一天
        Assert.Equal((7, 31), SignUpFeature.SignDay(new DateTimeOffset(2025, 8, 1, 3, 0, 0, TimeSpan.Zero)));
    }

    // ================= 签到 =================

    [Fact]
    public async Task Sign_RepliesNormalActivityMsg_WithRequiredFields()
    {
        using var h = await FeatureHarness.CreateAsync("sign-ok");

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");

        var (function, json) = h.LastOutbound;
        // ★ 下行名必须是 NormalActivityMsg，不是上行的 SignUpMsg
        Assert.Equal(SignUpFeature.DownFunction, function);

        var tb = JsonNode.Parse(json)!;
        Assert.Equal(SignUpFeature.MsgTypeSign, tb["nType"]!.GetValue<int>());
        Assert.Equal(SignUpFeature.SubTypeSign, tb["nSubType"]!.GetValue<int>());
        Assert.True(tb["bSuccess"]!.GetValue<bool>());
        Assert.NotNull(tb["tbAward"]);
        Assert.NotNull(tb["isRefreshSign"]);
    }

    [Fact]
    public async Task Sign_UpdatesTaskValues()
    {
        using var h = await FeatureHarness.CreateAsync("sign-task");

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");

        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskTodaySigned));
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
    }

    [Fact]
    public async Task Sign_GrantsConfiguredAward()
    {
        using var h = await FeatureHarness.CreateAsync("sign-award");
        int goldBefore = h.Player.GetMoney(MoneyType.Gold);

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");

        // 当月第 1 次签到的奖励必须真的进账（不能只弹窗）
        var table = SignUpTable.Load(TestPaths.SettingDir);
        var (month, _) = SignUpFeature.SignDay(DateTimeOffset.UtcNow);
        var expected = table.Award(month, 1);
        if (expected.Count > 0 && expected[0][0] == 15 && expected[0][1] == 1)
        {
            // (15,1,1,1) 折算成金币（ItemBaseTable.KnownMoneyItems）
            Assert.Equal(goldBefore + expected[0][4], h.Player.GetMoney(MoneyType.Gold));
        }

        var tbAward = (JsonArray)JsonNode.Parse(h.LastOutbound.Json)!["tbAward"]!;
        Assert.Equal(expected.Count, tbAward.Count);
    }

    [Fact]
    public async Task Sign_Twice_SecondIsIgnoredWithoutReply()
    {
        using var h = await FeatureHarness.CreateAsync("sign-twice");

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");
        int outboundAfterFirst = h.Outbound.Count;
        int countAfterFirst = h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount);

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");

        // 重复签到：不加次数、不再回包（正常流程里客户端已隐藏按钮）
        Assert.Equal(countAfterFirst, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
        Assert.Equal(outboundAfterFirst, h.Outbound.Count);
    }

    [Fact]
    public async Task Resign_DoesNotConsumeTodayFlag_ButCountsBoth()
    {
        using var h = await FeatureHarness.CreateAsync("sign-resign");

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeResign}}}");

        // 补签：月累计 +1、补签次数 +1，但不占用「今天已签」
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskResignCount));
        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskTodaySigned));

        var tb = JsonNode.Parse(h.LastOutbound.Json)!;
        Assert.Equal(SignUpFeature.SubTypeResign, tb["nSubType"]!.GetValue<int>());
    }

    // ================= 补签扣费（服务端复算，上行不带支付方式）=================

    /// <summary>
    /// 有补签券 (9,3,1,2) 时优先扣券、**不动钻石** ——
    /// 与客户端 `UI_SignActivity.lua:228` 的 `GetItemCount(...) > 0` 分支同一优先级。
    /// </summary>
    [Fact]
    public async Task Resign_WithTicket_ConsumesTicketNotDiamond()
    {
        using var h = await FeatureHarness.CreateAsync("sign-resign-ticket");
        var (g, d, p, l) = SignUpFeature.ResignItem;
        var ticket = h.Player.AddItem(g, d, p, l, count: 2);
        int diamondBefore = h.Player.GetMoney(MoneyType.FreeDiamond);

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeResign}}}");

        Assert.Equal(1u, ticket.Count);                                        // 券 -1
        Assert.Equal(diamondBefore, h.Player.GetMoney(MoneyType.FreeDiamond)); // 钻不动
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskResignCount));
    }

    /// <summary>最后一张券要从背包移除（推 pf 1031 时 count=0 表示删除）。</summary>
    [Fact]
    public async Task Resign_LastTicket_IsRemovedFromBag()
    {
        using var h = await FeatureHarness.CreateAsync("sign-resign-last-ticket");
        var (g, d, p, l) = SignUpFeature.ResignItem;
        var ticket = h.Player.AddItem(g, d, p, l, count: 1);

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeResign}}}");

        Assert.DoesNotContain(ticket, h.Player.Items);
    }

    /// <summary>没券时扣 50 免费钻（`UI_SignActivity.lua:3 RESIGN_COST = 50`）。</summary>
    [Fact]
    public async Task Resign_WithoutTicket_ConsumesDiamond()
    {
        using var h = await FeatureHarness.CreateAsync("sign-resign-diamond");
        h.Player.SetMoney(MoneyType.FreeDiamond, 200);

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeResign}}}");

        Assert.Equal(200 - SignUpFeature.ResignDiamondCost, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskResignCount));
    }

    /// <summary>
    /// 券和钻都不够 → **拒绝补签**：不记次数、不发奖、不回包。
    /// 客户端没有「补签失败」提示分支（正常流程里它会先本地拦住），所以只能靠服务端日志暴露。
    /// </summary>
    [Fact]
    public async Task Resign_CannotPay_IsRejectedWithoutSideEffect()
    {
        using var h = await FeatureHarness.CreateAsync("sign-resign-poor");
        h.Player.SetMoney(MoneyType.FreeDiamond, SignUpFeature.ResignDiamondCost - 1);
        int goldBefore = h.Player.GetMoney(MoneyType.Gold);

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeResign}}}");

        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskResignCount));
        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
        Assert.Equal(goldBefore, h.Player.GetMoney(MoneyType.Gold));   // 奖励没发
        Assert.Empty(h.Outbound);                                      // 不回包
    }

    /// <summary>普通签到不扣任何费用。</summary>
    [Fact]
    public async Task Sign_IsFree()
    {
        using var h = await FeatureHarness.CreateAsync("sign-free");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10);   // 远低于补签价

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");

        Assert.Equal(10, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskTodaySigned));
    }

    // ================= 跨日 / 跨月重置 =================

    [Fact]
    public async Task ResetIfNeeded_ClearsTodayFlagOnNewDay()
    {
        using var h = await FeatureHarness.CreateAsync("sign-reset-day");
        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskTodaySigned));

        // 手动把「上次签到日」改成别的一天，模拟跨日
        var record = (JsonObject)h.Player.Extra[SignUpFeature.ExtraKey]!;
        int today = record["day"]!.GetValue<int>();
        record["day"] = today == 1 ? 2 : today - 1;

        var feature = new SignUpFeature();
        var changed = feature.ResetIfNeeded(h.Player);

        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskTodaySigned));
        // 月没变 → 月累计不清
        Assert.Equal(1, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
        Assert.Contains(changed, c => Player.TaskIdOf(c.Key) == SignUpFeature.TaskTodaySigned);
    }

    [Fact]
    public async Task ResetIfNeeded_ClearsMonthCountersOnNewMonth()
    {
        using var h = await FeatureHarness.CreateAsync("sign-reset-month");
        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, $"{{\"nType\":{SignUpFeature.TypeSign}}}");

        var record = (JsonObject)h.Player.Extra[SignUpFeature.ExtraKey]!;
        int month = record["month"]!.GetValue<int>();
        record["month"] = month == 1 ? 2 : month - 1;

        new SignUpFeature().ResetIfNeeded(h.Player);

        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskTodaySigned));
        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskResignCount));
    }

    [Fact]
    public async Task UnknownType_IsIgnored()
    {
        using var h = await FeatureHarness.CreateAsync("sign-unknown");

        await h.DispatchFunctionAsync(SignUpFeature.UpFunction, "{\"nType\":77}");

        Assert.Empty(h.Outbound);
        Assert.Equal(0, h.Player.GetTask(SignUpFeature.TaskGroup, SignUpFeature.TaskMonthCount));
    }
}
