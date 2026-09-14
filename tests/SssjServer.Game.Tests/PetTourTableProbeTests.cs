using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <see cref="PetTourTable"/> 的探针测试：拿**真实配置表**验解析，
/// 防的是本仓反复踩过的那类「整表静默 0 行」——
/// 表头行数传错 / 列名拼错时不抛不报错，只是所有查询恒返回空
/// （`docs/05` §52.4 誓约那次、AGENTS.md 记的 `ItemExp` 那次）。
///
/// ⇒ 所以这里**断言具体数值**，不只断言「非空」。
/// </summary>
public class PetTourTableProbeTests
{
    private static PetTourTable Load() => PetTourTable.Load(TestPaths.SettingDir);

    [Fact]
    public void 三张表都能读出预期行数()
    {
        var t = Load();

        // 实测：TourArea 5 栏位、TourInfo 5 种远征、TourReward 49 条
        Assert.Equal(5, t.Areas.Count);
        Assert.Equal(5, t.Tours.Count);
        Assert.Equal(49, t.Rewards.Count);
    }

    [Fact]
    public void 栏位1是免费的而栏位3要两万金币()
    {
        var t = Load();

        var a1 = t.Area(1)!;
        Assert.Equal(1, a1.Rare);
        Assert.Equal(4, a1.Hour);
        Assert.Equal(20, a1.EnergyCost);
        Assert.Equal(100, a1.ExNum);
        Assert.Empty(a1.Unlock1);      // 栏位 1/2 无解锁条件
        Assert.Equal(0, a1.Unlock2);
        Assert.Empty(a1.Unlock3);

        // 栏位 3：完成 1 次 C 阶 + 拥有 10 只宠物 + 花 20000 货币2
        var a3 = t.Area(3)!;
        Assert.Equal(6, a3.Hour);
        Assert.Equal(50, a3.EnergyCost);
        Assert.Equal(new[] { 1, 2 }, a3.Unlock1);
        Assert.Equal(10, a3.Unlock2);
        Assert.Equal(new[] { 2, 20000 }, a3.Unlock3);

        var a5 = t.Area(5)!;
        Assert.Equal(new[] { 2, 5 }, a5.Unlock1);
        Assert.Equal(50, a5.Unlock2);
        Assert.Equal(new[] { 2, 50000 }, a5.Unlock3);
    }

    [Fact]
    public void 嵌套组解析出二元插值点与三元条件()
    {
        var t = Load();

        // 远征 1：SpWinNum = {{10,20},{60,100}}，Condition = {{1,1,3}}
        var t1 = t.Tour(1)!;
        Assert.Equal(2, t1.SpWinNum.Count);
        Assert.Equal((10, 20), t1.SpWinNum[0]);
        Assert.Equal((60, 100), t1.SpWinNum[1]);
        Assert.Single(t1.Condition);
        Assert.Equal((1, 1, 3), t1.Condition[0]);

        // 远征 4：Condition = {{2,1,2},{3,3,2}} —— 两条、分别是限定物种与限定性格
        var t4 = t.Tour(4)!;
        Assert.Equal(2, t4.Condition.Count);
        Assert.Equal((2, 1, 2), t4.Condition[0]);
        Assert.Equal((3, 3, 2), t4.Condition[1]);
    }

    [Fact]
    public void 短横五元组奖励能解析()
    {
        var t = Load();

        // 远征 1 AwardNormal = [7-1-4-2-5]
        Assert.Equal(new[] { 7, 1, 4, 2, 5 }, t.Tour(1)!.AwardNormal);
        // AwardSpecial = [15-2-1-1-30]
        Assert.Equal(new[] { 15, 2, 1, 1, 30 }, t.Tour(1)!.AwardSpecial);

        // 奖励 1 = [15-1-1-1-20000]，归属 1（基础奖励）
        var r1 = t.Reward(1)!;
        Assert.Equal(1, r1.Kind);
        Assert.Equal(new[] { 15, 1, 1, 1, 20000 }, r1.Award);
        Assert.Equal(20, r1.Pro);

        // 奖励 7 归属 2（宠物指数奖励）
        Assert.Equal(2, t.Reward(7)!.Kind);
    }

    [Theory]
    // 总等级 0 ⇒ 恒 0（客户端 :243 单独判的那条）
    [InlineData(0, 0)]
    // <= minLv(10) ⇒ minPro(20)
    [InlineData(5, 20)]
    [InlineData(10, 20)]
    // >= maxLv(60) ⇒ maxPro(100)
    [InlineData(60, 100)]
    [InlineData(99, 100)]
    // 中间线性插值：20 + (35-10)*(100-20)/(60-10) = 20 + 25*80/50 = 60
    [InlineData(35, 60)]
    public void 大成功概率与客户端同算法(int totalLv, double expected)
    {
        var tour = Load().Tour(1)!;
        Assert.Equal(expected, PetTourTable.SuccessPercent(tour, totalLv), 3);
    }

    [Fact]
    public void 奖励id都在15位段内()
    {
        // 存进任务值时按 LoadBits(v,1,15) 打包 ⇒ 超过 32767 会被截断成别的奖励
        foreach (var r in Load().Rewards)
            Assert.InRange(r.AwardId, 1, PetTourTable.MaxRewardId);
    }

    [Fact]
    public void 缺表时降级为空而不是抛()
    {
        var t = PetTourTable.Load(Path.Combine(Path.GetTempPath(), "sssj-不存在的目录"));
        Assert.Empty(t.Areas);
        Assert.Null(t.Area(1));
        Assert.Equal(0, PetTourTable.SuccessPercent(new TourInfoRow(), 50));
    }
}
