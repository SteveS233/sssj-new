using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 角色卡养成域的**配置表与公式**测试（业务流程在 <c>CardFeatureTests</c>）。
///
/// 这一批断言的作用是「把从 Lua 逐字翻译过来的公式钉在真实配置数据上」——
/// 取证见 <c>work/card-domain-forensics.md</c> 与 <c>work/gap-analysis.md</c> §2.4。
/// </summary>
public class CardTableTests
{
    private static CardTable Table => CardTable.Load(TestPaths.SettingDir);

    // =================================================================
    //  一、ItemExp.txt 升级经验曲线
    // =================================================================

    /// <summary>
    /// 角色卡走 <c>NeedExp1</c> 这一列，且表里的真实值能读出来。
    /// 取证：<c>ItemExp.txt</c> 第 4 行（Lv=1）的 <c>NeedExp1</c> = <b>133</b>。
    /// </summary>
    [Fact]
    public void 经验曲线_角色卡用NeedExp1_Lv1需要133()
    {
        Assert.Equal(133, Table.ExpToNextLevel(1));
        // Lv=0 那一行是 0（表里首行就是 0）
        Assert.Equal(0, Table.ExpToNextLevel(0));
    }

    /// <summary>
    /// 累计经验 = 逐级相加（复刻 <c>GetPreAllItemExp</c>）。
    /// 2 级的累计 = Lv0 + Lv1 的需求。
    /// </summary>
    [Fact]
    public void 累计经验_等于逐级相加()
    {
        long expect = Table.ExpToNextLevel(0) + Table.ExpToNextLevel(1);
        Assert.Equal(expect, Table.TotalExpBefore(2));
        Assert.Equal(0, Table.TotalExpBefore(0));
    }

    /// <summary>
    /// ★★ 角色卡真实等级上限 = <b>70</b>，由两个**互不相干**的数据源交叉印证：
    /// ① <c>ItemExp.txt</c> 的 <c>NeedExp1</c> 在 71..90 行是空值 ⇒ 有值的最大 Lv = 70；
    /// ② <c>CharacterCardBreak.txt</c> 的 <c>MaxLevel3</c>（三次突破后上限）最大值也是 70。
    /// 这条断言同时盯住「空值行没被当成有效等级」这个易错点。
    /// </summary>
    [Fact]
    public void 最大等级_是70且与突破表上限一致()
    {
        var t = Table;
        Assert.Equal(70, t.MaxLevel());

        // 突破表里所有 MaxLevel 的最大值也应是 70
        int breakMax = 0;
        foreach (int type in new[] { 11, 12, 13, 14, 51, 52, 53, 54 })
        {
            var row = t.BreakOf(type);
            if (row is null)
                continue;
            foreach (var s in row.Steps)
                breakMax = Math.Max(breakMax, s.MaxLevel);
        }
        Assert.Equal(70, breakMax);
    }

    /// <summary>
    /// ★ <c>ApplyExp</c> 是「给定经验能升到几级」，与客户端的 <c>GetItemNeedExp</c> 互为逆运算：
    /// 给正好 <c>TotalExpBefore(target)</c> 的经验，就应该正好到 <c>target</c> 级、残余 0。
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    public void 消化经验_与客户端公式互为逆运算(int target)
    {
        long total = Table.TotalExpBefore(target);
        var (lv, rest) = Table.ApplyExp(0, 0, total, maxLevel: 200);
        Assert.Equal(target, lv);
        Assert.Equal(0, rest);
    }

    /// <summary>
    /// ★★★ 三条边界逐条对齐 <c>GetItemDestLevel</c>（<c>Lib.lua:685..714</c>，升级族共用）。
    ///
    /// ① 撞**突破上限** ⇒ 保留残余（突破后可继续升）；
    /// ② 撞**硬满级**（Lv70，表里 NeedExp 变 0）⇒ ★ 残余**作废归 0**
    ///    （客户端 <c>UI_WeaponStrong.lua:1369</c> 存进 `OOExp` 并提示玩家 = 真的没了）；
    /// ③ **入口就已在上限** ⇒ 经验不截断照加。
    /// </summary>
    [Fact]
    public void 消化经验_撞硬满级时残余作废()
    {
        var t = Table;
        int hard = t.MaxLevel();                       // 70
        long toHard = t.TotalExpBefore(hard);

        // 给「刚好到 70 级」+ 一大笔溢出；maxLevel 放宽到 999 以排除「突破上限」这条边界
        var (lv, rest) = t.ApplyExp(0, 0, toHard + 999999, maxLevel: 999);
        Assert.Equal(hard, lv);
        Assert.Equal(0, rest);                         // ★ 作废，不是保留
    }

    /// <summary>
    /// ③ 入口就已在突破上限 ⇒ 经验**不截断**照加（`enhanceexp` 无限涨，
    /// 留着等下次突破解开上限后一次性转等级）。
    /// </summary>
    [Fact]
    public void 消化经验_入口已满上限时经验不截断()
    {
        var (lv, rest) = Table.ApplyExp(level: 40, exp: 500, gain: 12345, maxLevel: 40);
        Assert.Equal(40, lv);
        Assert.Equal(500 + 12345, rest);
    }

    /// <summary>
    /// 到达 <paramref name="maxLevel"/> 后**不再升级**，多余经验**保留在残余里**
    /// （客户端 <c>UI_CardMain.lua:2073</c> 用 <c>GetItemNeedExp(maxLevel, 0, maxLevel+1, ...)</c>
    /// 显示「满级后还在攒」，说明溢出经验不丢）。
    /// </summary>
    [Fact]
    public void 消化经验_到上限后停住且溢出经验保留()
    {
        long huge = Table.TotalExpBefore(40) + 99999;
        var (lv, rest) = Table.ApplyExp(0, 0, huge, maxLevel: 20);
        Assert.Equal(20, lv);
        Assert.True(rest > 0, "溢出经验必须保留在 enhanceexp 里");
    }

    // =================================================================
    //  二、CharacterCardBreak.txt 突破
    // =================================================================

    /// <summary>
    /// ★★ 编码规则 <c>Type = WeaponType * 10 + Rarity</c> —— 对
    /// <c>CharacterCard.txt</c> 全部 269 行**逐行验证**，必须零未命中。
    /// 这是本域最关键的一条结论（`work/gap-analysis.md` §2.4②）。
    /// </summary>
    [Fact]
    public void 突破类型编码_对269张卡全部命中()
    {
        var cards = CharacterCardTable.Load(TestPaths.SettingDir);
        var t = Table;
        Assert.NotEmpty(cards.All);

        var missing = new List<string>();
        foreach (var c in cards.All)
        {
            int bt = CardTable.BreakTypeOf(c.WeaponType, c.Rarity);
            if (t.BreakOf(bt) is null)
                missing.Add($"({c.G},{c.D},{c.P},{c.L}) wt={c.WeaponType} rar={c.Rarity} → Type={bt}");
        }
        Assert.True(missing.Count == 0,
            $"有 {missing.Count} 张卡算不出突破配置：{string.Join(" / ", missing.Take(5))}");
    }

    /// <summary>
    /// <c>Type=11</c> 那一行的真实数据（表第 4 行）：初始上限 40，三次突破到 50/60/70，
    /// 代币 id=2、数量 20000/40000/60000。
    /// </summary>
    [Fact]
    public void 突破配置_Type11的真实数值()
    {
        var row = Table.BreakOf(11);
        Assert.NotNull(row);
        Assert.Equal(40, row!.MaxLevel0);
        Assert.Equal(3, row.Steps.Count);
        Assert.Equal(new[] { 50, 60, 70 }, row.Steps.Select(s => s.MaxLevel));
        Assert.All(row.Steps, s => Assert.Equal(2, s.CurrencyId));
        Assert.Equal(new[] { 20000, 40000, 60000 }, row.Steps.Select(s => s.CurrencyCount));
        // 第一次突破的材料 [7-2-1-1-3,7-2-1-2-3]
        Assert.Equal(2, row.Steps[0].Items.Count);
        Assert.Contains(row.Steps[0].Items, i => i is { G: 7, D: 2, P: 1, L: 1, N: 3 });
    }

    /// <summary>等级上限随突破次数递增；未配置的突破类型退化成全局上限。</summary>
    [Fact]
    public void 等级上限_随突破次数递增()
    {
        var t = Table;
        int l0 = t.MaxLevelFor(11, 0), l1 = t.MaxLevelFor(11, 1), l3 = t.MaxLevelFor(11, 3);
        Assert.True(l0 < l1 && l1 < l3, $"上限未递增：{l0}/{l1}/{l3}");
        // 不存在的类型：不抛、退化成表驱动上限
        Assert.Equal(t.MaxLevel(), t.MaxLevelFor(999999, 0));
    }

    // =================================================================
    //  三、ItemRarityItem.txt 分解
    // =================================================================

    /// <summary>
    /// 分解金币公式 <c>floor(累计经验 × Param1[0] + rarity × Param1[1])</c>。
    /// <c>Rarity=1</c> 的 <c>Param1 = [0.3, 500]</c>（表第 4 行）⇒ 可手算核对。
    /// </summary>
    [Fact]
    public void 分解金币_复刻GetCardPrice()
    {
        var t = Table;
        var row = t.RarityOf(1);
        Assert.NotNull(row);
        var (c0, c1, _) = row!.For(1);
        Assert.Equal(0.3, c0, 3);
        Assert.Equal(500, c1, 3);

        const int level = 10;
        long expect = (long)Math.Floor(t.TotalExpBefore(level) * c0 + 1 * c1);
        Assert.Equal(expect, t.DecomposeGold(level, rarity: 1));
    }

    /// <summary>等级越高分解金币越多（累计经验单调递增）。</summary>
    [Fact]
    public void 分解金币_随等级单调不减()
    {
        var t = Table;
        long a = t.DecomposeGold(1, 1), b = t.DecomposeGold(20, 1), c = t.DecomposeGold(40, 1);
        Assert.True(a <= b && b <= c, $"非单调：{a}/{b}/{c}");
    }

    /// <summary>未配置的稀有度：金币 0、材料空，且**不抛**。</summary>
    [Fact]
    public void 分解_未配置稀有度时安全降级()
    {
        Assert.Equal(0, Table.DecomposeGold(10, rarity: 99999));
        Assert.Empty(Table.DecomposeItems(99999));
    }

    /// <summary>返还材料能解析出概率位（<c>DisItem1</c> 末位是万分比）。</summary>
    [Fact]
    public void 分解材料_带万分比概率()
    {
        var items = Table.DecomposeItems(1);
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.InRange(i.Probability, 0, 10000));
    }

    // =================================================================
    //  四、经验素材索引 ↔ GDPL（本域最易写错的一处）
    // =================================================================

    /// <summary>
    /// <c>tbExpCard</c> 是 24 项，全部 <c>G=7 D=1</c>，
    /// <c>P ∈ {1,2,3,4,5,6}</c>、<c>L ∈ {1..4}</c>（<c>CardCommon.lua:4..30</c>）。
    /// </summary>
    [Fact]
    public void 经验素材表_24项且GD固定()
    {
        Assert.Equal(24, CardTable.ExpCards.Count);
        Assert.All(CardTable.ExpCards, c =>
        {
            Assert.Equal(7, c.G);
            Assert.Equal(1, c.D);
            Assert.InRange(c.P, 1, 6);
            Assert.InRange(c.L, 1, 4);
        });
    }

    /// <summary>
    /// ★★ 索引解码：<c>1..24</c> 是经验卡、<c>25/26</c> 是技能饼干 <c>(7,4,1,3)/(7,4,1,4)</c>
    /// （<c>GetEnhanceCardGDPL</c> 的 else 分支）。越界返回 null。
    /// </summary>
    [Fact]
    public void 素材索引解码_经验卡与技能饼干()
    {
        Assert.Equal((7, 1, 1, 1), CardTable.DecodeExpItem(1));
        Assert.Equal((7, 1, 4, 4), CardTable.DecodeExpItem(24));
        Assert.Equal((7, 4, 1, 3), CardTable.DecodeExpItem(25));
        Assert.Equal((7, 4, 1, 4), CardTable.DecodeExpItem(26));
        Assert.Null(CardTable.DecodeExpItem(0));
        Assert.Null(CardTable.DecodeExpItem(27));
        Assert.Null(CardTable.DecodeExpItem(-1));
    }

    /// <summary>技能饼干要能被识别出来（它加技能经验、不加等级经验）。</summary>
    [Fact]
    public void 技能饼干_可识别()
    {
        Assert.False(CardTable.IsSkillCookie(24));
        Assert.True(CardTable.IsSkillCookie(25));
        Assert.True(CardTable.IsSkillCookie(26));
        Assert.False(CardTable.IsSkillCookie(27));
    }

    /// <summary>
    /// 经验倍率（<c>GetExpRate</c>）：素材 <c>P=4</c> 通用 ⇒ 1.5；
    /// 属性匹配（<c>tbMapC2I</c>：属性 1 → 素材 P=3）⇒ 1.5；不匹配 ⇒ 1.0。
    /// ★ 映射**不是恒等的**，这条断言专门盯住它。
    /// </summary>
    [Theory]
    [InlineData(0, 4, 1.5)]   // 通用素材
    [InlineData(1, 3, 1.5)]   // 属性 1 ↔ 素材 P=3
    [InlineData(2, 2, 1.5)]   // 属性 2 ↔ 素材 P=2
    [InlineData(3, 5, 1.5)]
    [InlineData(4, 6, 1.5)]
    [InlineData(1, 1, 1.0)]   // 不匹配（注意属性 1 对应的不是 P=1）
    [InlineData(2, 6, 1.0)]
    public void 经验倍率_同属性与通用才翻倍(int attr, int itemP, double expect)
        => Assert.Equal(expect, CardTable.ExpRate(attr, itemP), 3);

    // =================================================================
    //  五、ItemRarityExp.txt（稀有度维度）
    // =================================================================

    /// <summary>
    /// 「该稀有度可突破几次」= <c>NeedExp1</c>，真实数据 1★:0 / 2★:1 / 3★:2 / 4★:3 / 5★:4。
    /// ★ 这张表也只有 <b>2 行表头</b>，本断言顺带验证 <c>LoadAuto</c> 对它生效。
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    public void 稀有度可突破次数_真实数值(int rarity, int expect)
        => Assert.Equal(expect, Table.RarityBreakCount(rarity));

    /// <summary>「喂卡基础返还」= <c>NeedExp2</c>，真实数据 500/2000/5000/10000/80000。</summary>
    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 2000)]
    [InlineData(3, 5000)]
    [InlineData(4, 10000)]
    [InlineData(5, 80000)]
    public void 喂卡基础返还_真实数值(int rarity, long expect)
        => Assert.Equal(expect, Table.RarityFeedBaseExp(rarity));

    /// <summary>
    /// ★★ 可突破次数 = **突破表配了几档**，与稀有度的 <c>ItemRarityExp.NeedExp1</c> **无关**。
    ///
    /// 判据（子 agent 取证 + 主会话复核）：
    /// ① <c>CardCommon:GetMaxBreakCount</c>（<c>CardCommon.lua:809..811</c>）只数
    ///    <c>#tbBreakItem</c>，不读 <c>ItemRarityExp</c>；
    /// ② <c>CardCommon.BreakCountType</c>（<c>:75</c>）**全仓 0 处使用**；
    /// ③ 决定性反证：**每个** <c>Type</c>（含 1★ 的 11..14）都配了 <b>3 档</b>，
    ///    若按 <c>NeedExp1</c>（1★ = 0 次）那 3 档就成了死数据。
    ///
    /// 本断言专门盯住「不要退回 min 口径」这个回归。
    /// </summary>
    [Fact]
    public void 最大突破次数_只看突破表档数而非稀有度()
    {
        var t = Table;
        // 1★（Type=11）的 NeedExp1 是 0，但突破表配了 3 档 ⇒ 必须是 3
        Assert.Equal(0, t.RarityBreakCount(1));
        Assert.Equal(3, t.MaxBreakCount(CardTable.BreakTypeOf(1, 1), rarity: 1));
        Assert.Equal(3, t.MaxBreakCount(CardTable.BreakTypeOf(1, 4), rarity: 4));
        // 不存在的类型 ⇒ 0（不可突破）
        Assert.Equal(0, t.MaxBreakCount(999999));
    }

    /// <summary>
    /// 「吃卡」经验 = <c>floor(min(累计经验 × 0.3, 150000) + 稀有度基础返还)</c>
    /// （<c>CardCommon:GetCardExp</c>）。1 级卡累计经验为 0 ⇒ 只剩基础返还。
    /// </summary>
    [Fact]
    public void 吃卡经验_复刻GetCardExp()
    {
        var t = Table;
        // 1 级卡：TotalExpBefore(1) = ExpToNextLevel(0) = 0 ⇒ 只有基础返还
        Assert.Equal(t.RarityFeedBaseExp(3), t.FeedCardExp(level: 1, rarity: 3));

        // 高等级卡：应包含折算部分
        long high = t.FeedCardExp(level: 50, rarity: 4);
        Assert.True(high > t.RarityFeedBaseExp(4), "高等级卡必须比基础返还更多");

        // 折算部分有 MaxEXPProvide 上限
        long capped = t.FeedCardExp(level: 70, rarity: 5);
        Assert.True(capped <= CardTable.MaxExpProvide + t.RarityFeedBaseExp(5));
    }

    // =================================================================
    //  六、解析工具
    // =================================================================

    /// <summary>GDPL 列表解析：兼容 <c>[a,b]</c> 与 <c>[a];[b]</c> 两种分隔。</summary>
    [Fact]
    public void 解析GDPL_兼容两种分隔()
    {
        var a = CardTable.ParseGdplList("[7-2-1-1-3,7-2-1-2-3]");
        Assert.Equal(2, a.Count);
        Assert.Equal(new GdplN(7, 2, 1, 1, 3), a[0]);

        var b = CardTable.ParseGdplList("[15-11-1-1-1-0]", allowSixth: true);
        Assert.Single(b);
        Assert.Equal(0, b[0].Probability);

        Assert.Empty(CardTable.ParseGdplList(""));
        Assert.Empty(CardTable.ParseGdplList("   "));
        // 段数不足的脏数据要被丢弃而不是抛
        Assert.Empty(CardTable.ParseGdplList("[7-2]"));
    }

    /// <summary>浮点列表解析要用 InvariantCulture（避免某些 locale 下小数点解析失败）。</summary>
    [Fact]
    public void 解析浮点列表_不受locale影响()
    {
        var v = CardTable.ParseDoubleList("[0.3,500]");
        Assert.Equal(2, v.Count);
        Assert.Equal(0.3, v[0], 3);
        Assert.Equal(500, v[1], 3);
    }

    /// <summary>空表（缺表降级）：所有查询都要安全返回，绝不抛。</summary>
    [Fact]
    public void 空表_全部查询安全降级()
    {
        var t = CardTable.Empty;
        Assert.Equal(0, t.ExpToNextLevel(1));
        Assert.Equal(0, t.TotalExpBefore(10));
        Assert.Null(t.BreakOf(11));
        Assert.Equal(0, t.DecomposeGold(10, 1));
        Assert.Empty(t.DecomposeItems(1));
        Assert.Equal(1, t.MaxLevel());
    }
    // ================================================================ 改造配置（第十三轮）

    /// <summary>
    /// ★ 探针：<c>CardRefit.txt</c> 能载入且条数对得上（现网 27 张可改造卡）。
    /// 这是「表头行数解错 ⇒ 整表静默读空」那类错误的回归闸。
    /// ★ 条数随配置更新可能变化 ⇒ 只断言「非空」+ 数量级，不写死。
    /// </summary>
    [Fact]
    public void 改造表能载入且非空()
    {
        var t = CardTable.Load(TestPaths.SettingDir);
        Assert.True(t.RefitCardCount > 0, "CardRefit.txt 一张卡都没解出来，疑似表头行数解错");
        Assert.True(t.RefitCardCount < 200, $"可改造卡 {t.RefitCardCount} 张，明显过多，疑似把说明行当数据");
    }

    /// <summary>
    /// ★★ 逐字段核对第一张可改造卡（对着真实表值的探针）。
    /// 表里第一行是 <c>Genre=1 Detail=2 Particular=1 Level=3</c>（私服·高奈利亚）、
    /// <c>CostType=1</c>，第 1 档 <c>RefitSync1=25000</c>。
    /// </summary>
    [Fact]
    public void 改造表首行字段与真实表一致()
    {
        var t = CardTable.Load(TestPaths.SettingDir);
        var row = t.RefitOf(1, 2, 1, 3);
        Assert.NotNull(row);
        Assert.Equal(1, row!.CostType);
        Assert.NotNull(row.Cost);

        // 第 1 档同步率门槛（★ 单位是千分之一 ⇒ 25000 = 25%）
        Assert.Equal(25000, row.Steps[0].NeedSync);

        // 第 1 档消耗：CardRefitCost.txt Type=1 → RefitItem1=[7-2-7-3-2]、货币 2×30000
        var c0 = row.Cost!.Steps[0];
        Assert.Equal(2, c0.CurrencyId);
        Assert.Equal(30000, c0.CurrencyCount);
        Assert.Single(c0.Items);
        Assert.Equal((7, 2, 7, 3, 2),
            (c0.Items[0].G, c0.Items[0].D, c0.Items[0].P, c0.Items[0].L, c0.Items[0].N));
    }

    /// <summary>
    /// ★★★ <c>RefitItem&lt;n&gt;</c> 的多项用**逗号**分隔、每项内部用**短横**
    /// （样例 <c>[7-2-7-3-5,7-2-7-4-3]</c>）。
    /// 本仓踩过「同表两个 INTARR 列分隔符不同、混用静默得空数组」的坑 ⇒ 这条必须有闸。
    /// </summary>
    [Fact]
    public void 改造消耗的多材料能正确解析()
    {
        var t = CardTable.Load(TestPaths.SettingDir);
        var row = t.RefitOf(1, 2, 1, 3);
        Assert.NotNull(row);
        Assert.NotNull(row!.Cost);

        // Type=1 的第 2 档是 [7-2-7-3-3,7-2-7-4-1] —— 两项
        var c1 = row.Cost!.Steps[1];
        Assert.Equal(2, c1.Items.Count);
        Assert.Equal(60000, c1.CurrencyCount);
    }

    /// <summary>
    /// ★ <c>MaxRefit</c> 要按「配了同步率的档位数」算，不能写死 5 ——
    /// 现网多数卡只配到第 4 档，按 5 判会让第 5 次改造「条件永远不满足却又不被拒绝」。
    /// </summary>
    [Fact]
    public void 改造上限按配了同步率的档位数算()
    {
        var t = CardTable.Load(TestPaths.SettingDir);
        var row = t.RefitOf(1, 2, 1, 3);
        Assert.NotNull(row);
        Assert.InRange(row!.MaxRefit, 1, CardTable.MaxRefitSteps);
        // 现网该卡配到第 4 档
        Assert.Equal(4, row.MaxRefit);
    }

    /// <summary>★ 不可改造的卡返回 null（不能抛）。</summary>
    [Fact]
    public void 不可改造的卡返回null()
        => Assert.Null(CardTable.Load(TestPaths.SettingDir).RefitOf(1, 9999, 9, 9));
}
