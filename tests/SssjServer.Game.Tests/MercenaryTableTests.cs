using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <see cref="MercenaryTable"/> 探针。
///
/// ★★★ 本组最重要的作用是**钉住档期公式与真机自报的数字一致**。
/// 本轮踩过的坑：先按本地快照 `MercenaryTower.txt` 的 102 行推出
/// 「档期 2022-04 全部结束 ⇒ 佣兵是死内容」，而真机自报
/// <c>towerCount=891</c>、<c>lastWeek ClearTime=2126833200</c>（2037-05）、
/// <c>GetCurTowerConfig</c> 非 nil、服务端确实收到 320。
/// ⇒ 结论完全相反。所以这里用**真机实测到的时间戳**做断言，而不是用表行数。
/// </summary>
public class MercenaryTableTests
{
    /// <summary>真机自报的 <c>MercenaryTotal.BeginTime</c>。</summary>
    private const long RealBeginTime = 1587942000;

    /// <summary>真机自报的 <c>FirstClearTime</c>。</summary>
    private const long RealFirstClearTime = 1588561200;

    /// <summary>真机自报的当前时间（2026-08）。</summary>
    private const long RealNow = 1787397665;

    /// <summary>真机自报的最后一周 <c>ClearTime</c>（2037-05）。</summary>
    private const long RealLastClearTime = 2126833200;

    private static MercenaryTable Load() => MercenaryTable.Load(TestPaths.SettingDir);

    [Fact]
    public void 基准时间与真机自报一致()
    {
        var t = Load();

        // ★ 客户端自报 BeginTime=1587942000 / FirstClearTime=1588561200
        // 允许时区带来的整小时差：只要落在同一天即可（服务端按 UTC 算、设备按本地时区）
        Assert.True(Math.Abs(t.BeginTime - RealBeginTime) <= 24 * 3600,
            $"BeginTime={t.BeginTime} 与真机 {RealBeginTime} 差超过一天");
        Assert.True(Math.Abs(t.FirstClearTime - RealFirstClearTime) <= 24 * 3600,
            $"FirstClearTime={t.FirstClearTime} 与真机 {RealFirstClearTime} 差超过一天");
    }

    [Fact]
    public void 活动现在是开放的_不是死内容()
    {
        var t = Load();

        // ★★★ 这条是本域「要不要做」的判据。真机已证实：
        // 891 周排到 2037-05，当前时间（2026-08）落在区间内。
        Assert.True(t.IsOpen(RealNow), "按真机时间判定活动未开放 —— 与真机实测矛盾");
        Assert.True(t.CurrentWeekId(RealNow) > 0);
    }

    [Fact]
    public void 当前周与真机的891周量级一致()
    {
        var t = Load();
        int week = t.CurrentWeekId(RealNow);

        // 真机最后一周 ClearTime 落在 2037-05 ⇒ 总周数 891
        // 当前（2026-08）应在中段：远大于本地快照的 102，且小于 891
        Assert.True(week > 102,
            $"当前周 {week} ≤ 102 —— 说明又在按本地快照的表行数推断了");
        Assert.True(week < 891, $"当前周 {week} 已超过真机的总周数 891");
    }

    [Fact]
    public void 反解与正算互为逆运算()
    {
        var t = Load();

        // 对任意周 w：CurrentWeekId(该周区间内的时刻) == w
        foreach (int w in new[] { 1, 2, 50, 102, 300, 890 })
        {
            long begin = t.BeginTimeOf(w);
            long clear = t.ClearTimeOf(w);
            Assert.True(clear > begin, $"第 {w} 周的区间非法（{begin}..{clear}）");
            // 取区间中点
            long mid = begin + (clear - begin) / 2;
            Assert.Equal(w, t.CurrentWeekId(mid));
        }
    }

    [Fact]
    public void 最后一周的结算时间与真机对上()
    {
        var t = Load();

        // 真机 891 周的 ClearTime = 2126833200
        long clear891 = t.ClearTimeOf(891);
        Assert.True(Math.Abs(clear891 - RealLastClearTime) <= 24 * 3600,
            $"第 891 周 ClearTime={clear891} 与真机 {RealLastClearTime} 差超过一天");
    }

    [Fact]
    public void 活动开始前判未开放()
    {
        var t = Load();
        Assert.False(t.IsOpen(t.BeginTime - 1));
        Assert.Equal(0, t.CurrentWeekId(t.BeginTime - 1));
    }

    [Fact]
    public void 每周次数与奖励与表一致()
    {
        var t = Load();

        // MercenaryTotal.txt 数据行：WeekCount=3、ShopId=20009、Rewards=[15-44-1-1-1]
        Assert.Equal(3, t.WeekCount);
        Assert.Equal(20009, t.ShopId);
        var r = Assert.Single(t.WeekRewards);
        Assert.Equal(new[] { 15, 44, 1, 1, 1 }, r);
    }

    [Fact]
    public void 快照已修正为891个WeekId_且判档期不依赖表行数()
    {
        var t = Load();

        // ★★★ 第十轮：配置表快照从**新版 `setting.u`** 重解后，本地
        // `MercenaryTower.txt` 也是 **891** 个 WeekId —— 与真机自报一致
        // （旧快照只有 102，那正是 `AGENTS.md` §31.1 记的「本地表比设备少行」）。
        // ⇒ 那条坑的根因是「解的是 APK 内的旧 setting.u」，现已根治。
        Assert.Equal(891, t.KnownWeekIds.Count);
        // 判档期仍然走公式反解、不依赖表行数（这是当初的正确设计，保持）
        Assert.True(t.IsOpen(RealNow));
    }

    [Fact]
    public void 奖励解析是短横分隔()
    {
        // ★ 与 Friendliness.BanGirl 的逗号风格不同，这里是短横（G-D-P-L-N）
        var one = Assert.Single(MercenaryTable.ParseRewards("[15-44-1-1-1]"));
        Assert.Equal(new[] { 15, 44, 1, 1, 1 }, one);

        var two = MercenaryTable.ParseRewards("[15-44-1-1-1,2-1-1-1-500]");
        Assert.Equal(2, two.Count);

        Assert.Empty(MercenaryTable.ParseRewards(""));
        Assert.Empty(MercenaryTable.ParseRewards("[]"));
    }

    [Fact]
    public void 任务值常量与Lua定义一致()
    {
        // `MercenaryLogic.lua:4..36` 逐条核对
        Assert.Equal(64, MercenaryTable.Group);
        Assert.Equal(1, MercenaryTable.TaskCurWeekId);
        Assert.Equal(6, MercenaryTable.TaskCanGetMoney);
        Assert.Equal(10, MercenaryTable.TaskWeekMoney);
        Assert.Equal(11, MercenaryTable.TaskStep);
        Assert.Equal(12, MercenaryTable.TaskWeekCount);
        Assert.Equal(6, MercenaryTable.TaskWeekClear);
        Assert.Equal(61, MercenaryTable.TaskSettlementClear);
        Assert.Equal(180, MercenaryTable.TaskMax);
    }

    [Fact]
    public void 空表_降级为未开放()
    {
        var t = MercenaryTable.Empty;
        Assert.False(t.IsOpen(RealNow));
        Assert.Equal(0, t.CurrentWeekId(RealNow));
        Assert.Equal(0, t.ClearTimeOf(1));
        Assert.Empty(t.WeekRewards);
    }
}
