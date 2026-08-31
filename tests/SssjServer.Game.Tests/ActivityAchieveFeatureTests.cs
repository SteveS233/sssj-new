using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 福利页活动成就领奖（`NormalActivityGetAward` 的 `nType=1` 分支）。
/// 取证 `work/activity-award-forensics.md` §4、台账 `docs/05` §29。
///
/// 断言重点是这一域**最容易错的三条**：
/// <list type="number">
///   <item>下行是 **`MissionMgrMsg`** 而不是 `NormalActivityMsg`，且 `nError` **必须带**
///     （`MissionManager.lua:663` 是 `nError &gt; 0` 数值比较，缺了 `nil &gt; 0` 抛）；</item>
///   <item>**失败也必须回**（客户端要弹提示 + 解遮罩）；</item>
///   <item>★ 与活动 8（`nType=4` 领电力）**共用同一个上行**，两边不能互相吞
///     —— 靠协处理器机制隔开。</item>
/// </list>
/// </summary>
public class ActivityAchieveFeatureTests
{
    /// <summary>活动 16「新手咖啡馆」的成就 61：`missionMax=[500]`、`assistparam=[1]`。</summary>
    private const int Activity = 16;
    private const int Achieve = 61;

    private static JsonObject Reply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        // ★★ 下行是 MissionMgrMsg，不是 NormalActivityMsg
        Assert.Equal(ActivityAchieveFeature.DownFunction, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    private static Task ClaimAsync(FeatureHarness h, int activityId = Activity, int achieveId = Achieve)
        => h.DispatchFunctionAsync(ActivityAchieveFeature.UpFunction,
            $"{{\"nActivityId\":{activityId},\"nId\":{achieveId},\"nType\":1}}");

    /// <summary>把某成就的进度设到指定值（bit1..31），领取标记按 claimed。</summary>
    private static void SetProgress(FeatureHarness h, int achieveId, int progress, int claimedBit = 0)
        => h.Player.SetTask(ActivityAchieveTable.AchieveGroup, achieveId,
                            MissionFeature.ComposeTaskValue(progress, claimedBit));

    private static int Required(FeatureHarness h, int achieveId)
        => h.Module.Missions.GetAchieveOther(achieveId)!.RequiredCount(achieveKey: true);

    // ================= 回包形状 =================

    [Fact]
    public async Task 成功回包必须带nMission0与nError0()
    {
        using var h = await FeatureHarness.CreateAsync("actach-ok");
        SetProgress(h, Achieve, Required(h, Achieve));

        await ClaimAsync(h);

        var r = Reply(h);
        Assert.Equal(ActivityAchieveFeature.MissionAchieve, r["nMission"]!.GetValue<int>());
        // ★ 必须带 nError = 0（数值比较，缺字段客户端抛）
        Assert.True(r.ContainsKey("nError"), "回包没有 nError —— 客户端 nil > 0 会抛");
        Assert.Equal(ActivityAchieveFeature.ErrOk, r["nError"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(Activity, 999999)]      // 成就不属于该活动
    [InlineData(999999, Achieve)]       // 活动不是 Template=2
    public async Task 失败也必须回包(int activityId, int achieveId)
    {
        using var h = await FeatureHarness.CreateAsync($"actach-fail-{activityId}-{achieveId}");

        await ClaimAsync(h, activityId, achieveId);

        var r = Reply(h);
        Assert.Equal(ActivityAchieveFeature.MissionAchieve, r["nMission"]!.GetValue<int>());
        Assert.True(r["nError"]!.GetValue<int>() > 0, "失败必须回 nError > 0，客户端才会弹提示");
    }

    // ================= 领奖闸门 =================

    [Fact]
    public async Task 进度不足时拒绝且不发奖()
    {
        using var h = await FeatureHarness.CreateAsync("actach-low");
        int required = Required(h, Achieve);
        SetProgress(h, Achieve, required - 1);
        long before = h.Player.GetMoney((int)MoneyType.Gold);

        await ClaimAsync(h);

        Assert.Equal(ActivityAchieveFeature.ErrNotFinished, Reply(h)["nError"]!.GetValue<int>());
        // bit0 没被置
        Assert.Equal(0, MissionFeature.ClaimedOf(
            h.Player.GetTask(ActivityAchieveTable.AchieveGroup, Achieve)));
        Assert.Equal(before, h.Player.GetMoney((int)MoneyType.Gold));
    }

    [Fact]
    public async Task 进度达标时置bit0并保留进度()
    {
        using var h = await FeatureHarness.CreateAsync("actach-claim");
        int required = Required(h, Achieve);
        SetProgress(h, Achieve, required);

        await ClaimAsync(h);

        int v = h.Player.GetTask(ActivityAchieveTable.AchieveGroup, Achieve);
        Assert.Equal(1, MissionFeature.ClaimedOf(v));
        // ★ 进度必须保留（ComposeTaskValue 而不是直接赋 1）
        Assert.Equal(required, MissionFeature.ProgressOf(v));
    }

    [Fact]
    public async Task 重复领取被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("actach-twice");
        SetProgress(h, Achieve, Required(h, Achieve));

        await ClaimAsync(h);
        Assert.Equal(ActivityAchieveFeature.ErrOk, Reply(h)["nError"]!.GetValue<int>());

        await ClaimAsync(h);
        Assert.Equal(ActivityAchieveFeature.ErrAlreadyGot, Reply(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task 真的发奖()
    {
        using var h = await FeatureHarness.CreateAsync("actach-grant");
        SetProgress(h, Achieve, Required(h, Achieve));

        var entry = h.Module.Missions.GetAchieveOther(Achieve)!;
        Assert.NotEmpty(entry.Rewards);      // 成就 61 的 awardsList = [14-1-4-1-300]

        await ClaimAsync(h);

        // ★ 奖励弹窗内容客户端自己拼，但服务端必须真的发 ⇒ 断言背包/货币有变化
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf)
                    || await h.WaitForPfAsync(GamePf.MoneyUpdateNtf),
            "领奖后既没推 pf 1031 也没推 1032 —— 奖励没真的发");
    }

    [Fact]
    public async Task 推送pf1026让按钮变灰()
    {
        using var h = await FeatureHarness.CreateAsync("actach-push");
        SetProgress(h, Achieve, Required(h, Achieve));

        await ClaimAsync(h);

        // ★ 客户端 OnGetAchieveAward() 不收参数、重读 group 18 ⇒ 必须先推 pf 1026
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp),
            "领奖后没推 pf 1026 —— 客户端按钮不会变灰");
    }

    // ================= 与活动 8 的共存（协处理器机制）=================

    [Fact]
    public async Task nType4的领电力不被本域吞掉()
    {
        using var h = await FeatureHarness.CreateAsync("actach-coexist");

        // ★★ 同一个 sFunction：nType=4 必须交回 EnergyGetFeature（活动 8）
        // 那条路的约定是「失败静默不回」⇒ 这里断言**没有** MissionMgrMsg 下行
        await h.DispatchFunctionAsync(ActivityAchieveFeature.UpFunction,
            "{\"nActivityId\":8,\"nType\":4}");

        Assert.DoesNotContain(h.Outbound, o => o.Function == ActivityAchieveFeature.DownFunction);
    }

    [Fact]
    public async Task 上行是数组时不抛()
    {
        using var h = await FeatureHarness.CreateAsync("actach-array");
        // ★ JsonNode["key"] 对 JsonArray 会抛（咖啡馆 115 真机崩过）
        await h.DispatchFunctionAsync(ActivityAchieveFeature.UpFunction, "[]");
    }

    [Fact]
    public async Task 缺nType时不处理()
    {
        using var h = await FeatureHarness.CreateAsync("actach-notype");

        await h.DispatchFunctionAsync(ActivityAchieveFeature.UpFunction,
            $"{{\"nActivityId\":{Activity},\"nId\":{Achieve}}}");

        // nType 缺失 ⇒ 不是本域的 ⇒ 不该回 MissionMgrMsg
        Assert.DoesNotContain(h.Outbound, o => o.Function == ActivityAchieveFeature.DownFunction);
    }

    // ================= 配置表 =================

    [Fact]
    public void 配置表_六个开放活动的成就清单与取证一致()
    {
        var t = ActivityAchieveTable.Load(TestPaths.SettingDir);

        // 取证 §4.2 的表格（逐个核对）
        Assert.Equal(new[] { 4, 5, 6, 7, 8, 9 }, t.AchievesOf(2).ToArray());
        Assert.Equal(new[] { 10, 11, 12, 15, 17, 301, 302, 303, 304, 305 }, t.AchievesOf(3).ToArray());
        Assert.Equal(14, t.AchievesOf(14).Count);
        Assert.Equal(16, t.AchievesOf(15).Count);
        Assert.Equal(new[] { 61, 62, 63, 64, 65, 66, 67, 68, 69 }, t.AchievesOf(16).ToArray());
        Assert.Equal(19, t.AchievesOf(108).Count);
    }

    [Fact]
    public void 配置表_那74个成就在AchievementOther里一个都不缺()
    {
        var t = ActivityAchieveTable.Load(TestPaths.SettingDir);
        var missions = MissionTable.Load(TestPaths.SettingDir);

        foreach (int activityId in new[] { 2, 3, 14, 15, 16, 108 })
        {
            foreach (int id in t.AchievesOf(activityId))
            {
                Assert.NotNull(missions.GetAchieveOther(id));
            }
        }
    }

    [Fact]
    public void 配置表_花括号数组解析()
    {
        // ★ 这张表用 {..} 而不是 [..]（`NormalActivityConfig.lua:135` 是 Eval()）
        Assert.Equal(new[] { 4, 5, 6 }, ActivityAchieveTable.ParseBraceList("{4,5,6}").ToArray());
        Assert.Empty(ActivityAchieveTable.ParseBraceList(""));
        Assert.Empty(ActivityAchieveTable.ParseBraceList("{}"));
        // 兼容中括号写法（防表格式变化）
        Assert.Equal(new[] { 7 }, ActivityAchieveTable.ParseBraceList("[7]").ToArray());
    }

    [Fact]
    public void 配置表_成就group是18()
    {
        Assert.Equal(18, ActivityAchieveTable.AchieveGroup);
        Assert.Equal(MissionTable.OtherGroup, ActivityAchieveTable.AchieveGroup);
    }

    [Fact]
    public void 空表_任何领奖都拒绝()
    {
        var t = ActivityAchieveTable.Empty;
        Assert.Empty(t.AchievesOf(16));
        Assert.False(t.Contains(16, 61));
    }
}
