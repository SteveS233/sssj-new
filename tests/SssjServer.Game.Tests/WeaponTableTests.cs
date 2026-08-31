using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <see cref="WeaponTable"/> 的配置表探针。
///
/// 存在的理由与 <c>GirlLoveTableTests</c> 同：配置表读错**不抛不报错、只静默返回 0**
/// （AGENTS.md §5 的表头行数坑），必须用「对着真实表文件的具体数值」钉住。
/// ★ 本组第一条就是为了防住取证文档里那个错误结论（武器经验列是 <c>NeedExp3</c> 不是 <c>NeedExp2</c>）。
/// </summary>
public class WeaponTableTests
{
    private static WeaponTable Load() => WeaponTable.Load(TestPaths.SettingDir);

    [Fact]
    public void 经验列_是NeedExp3且非零()
    {
        var t = Load();

        // ★★ `WeaponCommon.lua:58` ITEMEXP_WEAOPN = 3 ⇒ 查的是 NeedExp3。
        // 取证文档 §E.3 写「武器看 NeedExp2」是错的 —— ItemExp.txt 的 NeedExp2 整列为空，
        // 照它实现会让每级所需经验恒 0 ⇒ 武器永远升不了级。
        Assert.Equal(3, WeaponTable.WeaponExpType);
        Assert.True(t.LevelUpExp(1) > 0,
            "1 级升 2 级所需经验为 0 —— 极可能读到了空的 NeedExp2 列");
        Assert.True(t.LevelUpExp(10) > 0);
    }

    [Fact]
    public void 经验列_1级需求与表一致()
    {
        var t = Load();

        // ItemExp.txt 第 4 行（Lv=1）：NeedExp1=133、NeedExp3=133
        Assert.Equal(133, t.LevelUpExp(1));
    }

    [Fact]
    public void 喂料经验_按稀有度取NeedExp3()
    {
        var t = Load();

        // ItemRarityExp.txt Rarity=1：NeedExp3=500
        Assert.Equal(500, t.RarityExp(1));
        Assert.True(t.RarityExp(2) > 0);
    }

    [Fact]
    public void 模板_解析出全部武器()
    {
        var t = Load();

        // Weapon.txt 138 行数据（141 - 3 表头）
        Assert.True(t.WeaponCount >= 130, $"只解析出 {t.WeaponCount} 把武器");

        // 第 4 行：2-1-2-2 Rarity=1
        var w = t.Find(1, 2, 2);
        Assert.NotNull(w);
        Assert.Equal(1, w!.Rarity);
    }

    [Fact]
    public void 模板_被动技能列解析正确()
    {
        var t = Load();

        // 实测 2-1-6-1：Rarity=2、PassiveSkill1=40151、PassiveSkill2=0
        var w = t.Find(1, 6, 1);
        Assert.NotNull(w);
        Assert.Equal(2, w!.Rarity);
        Assert.Equal(40151, w.PassiveSkill1);
        Assert.Equal(0, w.PassiveSkill2);
    }

    [Fact]
    public void 模板_不存在的组合返回null()
    {
        var t = Load();
        Assert.Null(t.Find(999, 999, 999));
    }

    [Fact]
    public void 突破_上限随突破次数递增()
    {
        var t = Load();

        // WeaponBreak.txt Rarity=1：MaxLevel0=40、MaxLevel1=50、MaxLevel2=60、MaxLevel3=70
        Assert.Equal(40, t.MaxLevelFor(1, 0));
        Assert.Equal(50, t.MaxLevelFor(1, 1));
        Assert.Equal(60, t.MaxLevelFor(1, 2));
        Assert.Equal(70, t.MaxLevelFor(1, 3));
        // ★ 可突破 3 次（MaxLevel4 是空列 ⇒ 停在 3）
        Assert.Equal(3, t.BreakNumFor(1));
    }

    [Fact]
    public void 突破_越界取最后一档()
    {
        var t = Load();
        // 已突破 99 次（非法）不该抛，取最后一档
        Assert.Equal(t.MaxLevelFor(1, 3), t.MaxLevelFor(1, 99));
    }

    [Fact]
    public void 突破_消耗按次数取()
    {
        var t = Load();

        // Rarity=1 第 1 次突破：BreakItem1=1 CurrencyId1=2 CurrencyCount1=20000
        var c = t.BreakCostFor(1, 0);
        Assert.NotNull(c);
        Assert.Equal(2, c!.CurrencyId);
        Assert.Equal(20000, c.CurrencyCount);

        // 第 2 次：40000
        Assert.Equal(40000, t.BreakCostFor(1, 1)!.CurrencyCount);
        // 超出可突破次数 ⇒ null（调用方据此拒绝）
        Assert.Null(t.BreakCostFor(1, 3));
    }

    [Fact]
    public void 上限_与经验表取小()
    {
        var t = Load();

        // 经验表最大等级应 > 0 才有意义
        Assert.True(t.ExpMaxLevel > 0);
        // MaxLevel = min(经验表上限, 突破上限)
        int rarity = 1;
        int byBreak = t.MaxLevelFor(rarity, 0);
        int expected = t.ExpMaxLevel > 0 && t.ExpMaxLevel < byBreak ? t.ExpMaxLevel : byBreak;
        Assert.Equal(expected, t.MaxLevel(rarity, 0));
    }

    // ================= DestLevel（1:1 移植 GetItemDestLevel）=================

    [Fact]
    public void 升级推演_零经验原样返回()
    {
        var t = Load();
        Assert.Equal((5, 20), t.DestLevel(5, 20, 0, 40));
    }

    [Fact]
    public void 升级推演_单级跃迁()
    {
        var t = Load();
        int need = t.LevelUpExp(1);

        // 刚好够升一级 ⇒ 2 级、余 0
        Assert.Equal((2, 0), t.DestLevel(1, 0, need, 40));
        // 差 1 点 ⇒ 留在 1 级
        Assert.Equal((1, need - 1), t.DestLevel(1, 0, need - 1, 40));
    }

    [Fact]
    public void 升级推演_多级跃迁()
    {
        var t = Load();
        int need = t.LevelUpExp(1) + t.LevelUpExp(2);

        var (lv, exp) = t.DestLevel(1, 0, need, 40);
        Assert.Equal(3, lv);
        Assert.Equal(0, exp);
    }

    [Fact]
    public void 升级推演_已达上限时经验仍累加()
    {
        var t = Load();

        // ★★ `Lib.lua:689..691`：已达上限时 return maxLevel, srcExp + nAdd
        // —— **不是丢弃**（与少女好感度的截断行为相反，最容易照抄错的一处）
        var (lv, exp) = t.DestLevel(40, 100, 500, 40);
        Assert.Equal(40, lv);
        Assert.Equal(600, exp);
    }

    [Fact]
    public void 升级推演_撞上限时保留余额()
    {
        var t = Load();

        // 给一笔远超上限的经验：应停在 maxLevel，且余额保留（不是清 0）
        var (lv, exp) = t.DestLevel(1, 0, 99_999_999, 40);
        Assert.Equal(40, lv);
        Assert.True(exp > 0, "撞上限时余下经验被丢弃了 —— `Lib.lua:702` 是保留的");
    }

    [Fact]
    public void 累计经验_逐级求和()
    {
        var t = Load();

        Assert.Equal(0, t.SumExpBelow(1));
        Assert.Equal(t.LevelUpExp(1), t.SumExpBelow(2));
        Assert.Equal(t.LevelUpExp(1) + t.LevelUpExp(2), t.SumExpBelow(3));
    }

    [Fact]
    public void 固定技能表_现网为空且查询安全()
    {
        var t = Load();

        // ★ 实测 `FixedSkillList.txt` **只有 3 行表头、零行数据**
        // （第 4 行只有一个 \r）⇒ 现网「打造的固定随机技能」这一档没有配置。
        // 这不是解析错，是配置本身为空 —— 所以打造时 skillinfo 只发
        // Weapon.txt 的 PassiveSkill1/2，不叠加固定技能。
        Assert.Equal(0, t.FixedSkillCount);
        // 查询不存在的组不能抛
        Assert.Null(t.FixedSkill(0));
        Assert.Null(t.FixedSkill(1));
    }

    [Fact]
    public void 空表_降级不抛()
    {
        var t = WeaponTable.Empty;

        Assert.Null(t.Find(1, 2, 2));
        Assert.Equal(0, t.LevelUpExp(1));
        Assert.Equal(0, t.RarityExp(1));
        Assert.Equal(1, t.MaxLevelFor(1, 0));
        Assert.Equal(0, t.BreakNumFor(1));
        Assert.Null(t.BreakCostFor(1, 0));
        Assert.Equal((1, 0), t.DestLevel(1, 0, 0, 0));
    }
}
