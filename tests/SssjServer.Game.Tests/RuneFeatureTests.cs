using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 符文养成（LuaCall 31 强化 / 35 突破）。台账 `docs/05` §46。
///
/// 断言重点（每条对应一个「写错了静默失效」的坑）：
/// <list type="number">
///   <item>★★★ 强化费用列是 <c>NeedExp5_&lt;rarity&gt;</c> —— 用 <c>NeedExp5</c> 会让
///         2/3/4★ 都按 1★ 收费（实测 1★ 500 / 2★ 750 / 3★ 1250 / 4★ 2500）；</item>
///   <item>★★★ 等级上限**先看关卡进度**（5-12 → 10 级、6-12 → 15 级），
///         只有到 15 档突破配置才接管；</item>
///   <item>★★ <c>RuneBreak.CurrencyCount&lt;n&gt;</c> 是 <c>[货币id-数量]</c>，
///         按纯数量解析会得到 2 ⇒ 突破几乎免费；</item>
///   <item>★ 两条都是 LuaCall 数字族：成功不带 <c>nError</c>、失败要带；</item>
///   <item>★ 成功要先推 pf 1031 再回包（客户端 <c>self:Update()</c> 重读本地缓存）。</item>
/// </list>
/// </summary>
public class RuneFeatureTests
{
    private static RuneTable LoadTable() => RuneTable.Load(TestPaths.SettingDir);

    private static JsonObject? Reply(FeatureHarness h, int cmd)
    {
        for (int i = h.Outbound.Count - 1; i >= 0; i--)
        {
            var (fn, json) = h.Outbound[i];
            if (fn != "LuaCall") continue;
            var root = JsonNode.Parse(json);
            if (root?["sCmd"]?.GetValue<int>() == cmd)
                return root["tbParam"] as JsonObject;
        }
        return null;
    }

    /// <summary>造一枚真实存在的符文，返回 guid 与它的配置行。</summary>
    private static (uint Guid, RuneRow Row) SeedRune(
        FeatureHarness h, int rarity = 1, uint level = 1, uint breaklv = 0)
    {
        var table = LoadTable();
        var pick = FindRune(table, rarity);
        Assert.NotNull(pick);

        uint guid = (uint)(h.Player.Items.Count + 7000);
        h.Player.Items.Add(new Item
        {
            Guid = guid,
            Genre = (uint)pick!.G, Detail = (uint)pick.D,
            Particular = (uint)pick.P, Level = (uint)pick.L,
            Count = 1, EnhanceLevel = level, BreakLevel = breaklv,
        });
        return (guid, pick);
    }

    /// <summary>从真实表里挑一枚指定星级的符文（★ 不写死 id，避免配置更新后变红）。</summary>
    private static RuneRow? FindRune(RuneTable table, int rarity)
    {
        for (int d = 1; d <= 60; d++)
            for (int p = 1; p <= 8; p++)
                for (int l = 1; l <= 8; l++)
                {
                    var r = table.RuneOf(3, d, p, l);
                    if (r is not null && r.Rarity == rarity)
                        return r;
                }
        return null;
    }

    /// <summary>把玩家的关卡记录设成「已通关 5-12 与 6-12」（解锁 15 级上限）。</summary>
    private static void ClearGateLevels(FeatureHarness h)
    {
        h.Player.Levels[512] = 1;
        h.Player.Levels[612] = 1;
    }

    private static Task EnhanceAsync(FeatureHarness h, uint guid)
        => h.DispatchLuaCallAsync(RuneFeature.EnhanceCmd, $"{{\"nGuid\":{guid}}}");

    private static Task BreakAsync(FeatureHarness h, uint guid)
        => h.DispatchLuaCallAsync(RuneFeature.BreakCmd, $"{{\"nGuid\":{guid},\"tbGuid\":[]}}");

    // ================================================================ 配置探针

    /// <summary>★ 三张表都能载入且非空（表头解错 ⇒ 静默读空 的回归闸）。</summary>
    [Fact]
    public void 符文三张表都能载入()
    {
        var t = LoadTable();
        Assert.True(t.RuneCount > 0, "Rune.txt 一枚都没解出来，疑似表头行数解错");
        Assert.True(t.BreakCount > 0, "RuneBreak.txt 没解出来");
        Assert.NotEmpty(t.Skills);          // ★ 本轮新发现的纯服务端表
        Assert.True(t.EnhanceCostRows > 0, "ItemExp 的符文列没解出来");
    }

    /// <summary>
    /// ★★★ 强化费用**按星级分档**。这条是本域最容易写错的地方：
    /// 用 <c>NeedExp5</c>（无后缀）会让各星级费用全相同。
    /// 实测 Lv1→2：1★ 500 / 2★ 750 / 3★ 1250 / 4★ 2500。
    /// </summary>
    [Fact]
    public void 强化费用按星级分档且与真实表一致()
    {
        var t = LoadTable();
        Assert.Equal(500, t.EnhanceCost(1, 1));
        Assert.Equal(750, t.EnhanceCost(2, 1));
        Assert.Equal(1250, t.EnhanceCost(3, 1));
        Assert.Equal(2500, t.EnhanceCost(4, 1));

        // ★ 四档必须互不相同 —— 相同就说明列名拼错、全读到同一列了
        var costs = new[] { t.EnhanceCost(1, 1), t.EnhanceCost(2, 1), t.EnhanceCost(3, 1), t.EnhanceCost(4, 1) };
        Assert.Equal(costs.Length, costs.Distinct().Count());
    }

    /// <summary>
    /// ★★ <c>CurrencyCount&lt;n&gt;</c> 是 <c>[货币id-数量]</c>（实测 <c>[2-20000]</c>）。
    /// 按纯数量解析会得到 <b>2</b> ⇒ 突破几乎免费。
    /// </summary>
    [Fact]
    public void 突破消耗的货币列被正确拆成id与数量()
    {
        var t = LoadTable();
        var brk = t.BreakOf(11);            // Rune.txt 首枚符文的 BreakConfigID
        Assert.NotNull(brk);
        Assert.NotEmpty(brk!.Steps);

        var s0 = brk.Steps[0];
        Assert.Equal(2, s0.CurrencyId);      // 金币
        Assert.Equal(20000, s0.CurrencyCount);
        Assert.True(s0.CurrencyCount > 1000, "货币数量疑似被当成 id 读了");
        Assert.NotEmpty(s0.Items);           // BreakItem1 有 4 项
    }

    /// <summary>
    /// ★★★ 等级上限先看关卡进度：没通关 5-12 恒 5、通关 5-12 → 10、
    /// 通关 6-12 → 15；只有到 15 档，突破配置的 MaxLevel 才接管。
    /// </summary>
    [Fact]
    public void 等级上限按关卡进度分三档()
    {
        var t = LoadTable();
        const int breakId = 11;

        Assert.Equal(RuneTable.MaxLevelBase,
            t.MaxLevelFor(breakId, 0, passed512: false, passed612: false));
        Assert.Equal(RuneTable.MaxLevelAfter512,
            t.MaxLevelFor(breakId, 0, passed512: true, passed612: false));

        // 通关 6-12 后突破配置接管：0 次突破 → MaxLevel0（实测 15）
        int at0 = t.MaxLevelFor(breakId, 0, passed512: true, passed612: true);
        Assert.Equal(15, at0);
        // 1 次突破 → MaxLevel1（实测 20）
        Assert.Equal(20, t.MaxLevelFor(breakId, 1, passed512: true, passed612: true));
    }

    // ================================================================ 31 强化

    /// <summary>★ 强化成功：等级 +1、扣金币、不带 <c>nError</c>。</summary>
    [Fact]
    public async Task 强化成功等级加一并扣金币()
    {
        var h = await FeatureHarness.CreateAsync(nameof(强化成功等级加一并扣金币));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 1);
        int cost = LoadTable().EnhanceCost(row.Rarity, 1);
        h.Player.AddMoney(RuneFeature.GoldMoneyId, cost - h.Player.GetMoney(RuneFeature.GoldMoneyId));

        await EnhanceAsync(h, guid);

        var reply = Reply(h, RuneFeature.EnhanceCmd);
        Assert.NotNull(reply);
        Assert.False(reply!.ContainsKey("nError"), "成功回包带了 nError，客户端会弹错误提示");
        Assert.Equal(2u, h.Player.Items.First(i => i.Guid == guid).EnhanceLevel);
        Assert.Equal(0, h.Player.GetMoney(RuneFeature.GoldMoneyId));
    }

    /// <summary>★ 金币不足要拒绝并**带 <c>nError</c>**（本域失败要回包，与角色卡突破不同）。</summary>
    [Fact]
    public async Task 强化金币不足被拒绝并带nError()
    {
        var h = await FeatureHarness.CreateAsync(nameof(强化金币不足被拒绝并带nError));
        ClearGateLevels(h);
        var (guid, _) = SeedRune(h, rarity: 1, level: 1);
        h.Player.AddMoney(RuneFeature.GoldMoneyId, -h.Player.GetMoney(RuneFeature.GoldMoneyId));

        await EnhanceAsync(h, guid);

        var reply = Reply(h, RuneFeature.EnhanceCmd);
        Assert.NotNull(reply);
        Assert.Equal(RuneFeature.ErrNotEnoughGold, reply!["nError"]!.GetValue<int>());
        Assert.Equal(1u, h.Player.Items.First(i => i.Guid == guid).EnhanceLevel);
    }

    /// <summary>
    /// ★★★ 没通关 5-12 的号，符文上限恒 5 —— 到 5 级就该拒绝，
    /// 不能按突破配置的 15/30 放行。
    /// </summary>
    [Fact]
    public async Task 没通关前置关卡时上限是5()
    {
        var h = await FeatureHarness.CreateAsync(nameof(没通关前置关卡时上限是5));
        // ★ 故意不通关 5-12 / 6-12
        var (guid, _) = SeedRune(h, rarity: 1, level: 5);
        h.Player.AddMoney(RuneFeature.GoldMoneyId, 10_000_000);

        await EnhanceAsync(h, guid);

        var reply = Reply(h, RuneFeature.EnhanceCmd);
        Assert.NotNull(reply);
        Assert.True(reply!.ContainsKey("nError"), "上限 5 时还允许强化 ⇒ 关卡门槛没生效");
        Assert.Equal(5u, h.Player.Items.First(i => i.Guid == guid).EnhanceLevel);
    }

    /// <summary>★ 满级要拒绝。</summary>
    [Fact]
    public async Task 强化到上限后被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(强化到上限后被拒绝));
        ClearGateLevels(h);
        var (guid, _) = SeedRune(h, rarity: 1, level: 15);   // 0 次突破上限 15
        h.Player.AddMoney(RuneFeature.GoldMoneyId, 10_000_000);

        await EnhanceAsync(h, guid);

        Assert.True(Reply(h, RuneFeature.EnhanceCmd)!.ContainsKey("nError"));
    }

    /// <summary>★ 非符文 / 不存在的 guid 要拒绝且不崩。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"nGuid\":0}")]
    [InlineData("{\"nGuid\":999999}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task 强化非法参数不崩且有回包(string json)
    {
        var h = await FeatureHarness.CreateAsync(nameof(强化非法参数不崩且有回包) + json.GetHashCode());
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(RuneFeature.EnhanceCmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.True(Reply(h, RuneFeature.EnhanceCmd)!.ContainsKey("nError"));
    }

    // ================================================================ 35 突破

    /// <summary>★ 突破成功：次数 +1、扣材料与货币、不带 <c>nError</c>。</summary>
    [Fact]
    public async Task 突破成功次数加一并扣消耗()
    {
        var h = await FeatureHarness.CreateAsync(nameof(突破成功次数加一并扣消耗));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 15);

        var brk = LoadTable().BreakOf(row.BreakConfigId);
        Assert.NotNull(brk);
        var step = brk!.Steps[0];

        h.Player.AddMoney(step.CurrencyId,
            step.CurrencyCount - h.Player.GetMoney(step.CurrencyId));
        foreach (var need in step.Items)
        {
            h.Player.Items.Add(new Item
            {
                Guid = (uint)(h.Player.Items.Count + 8000),
                Genre = (uint)need.G, Detail = (uint)need.D,
                Particular = (uint)need.P, Level = (uint)need.L,
                Count = (uint)need.N,
            });
        }

        await BreakAsync(h, guid);

        var reply = Reply(h, RuneFeature.BreakCmd);
        Assert.NotNull(reply);
        Assert.False(reply!.ContainsKey("nError"));
        Assert.Equal(1u, h.Player.Items.First(i => i.Guid == guid).BreakLevel);
        Assert.Equal(0, h.Player.GetMoney(step.CurrencyId));
    }

    /// <summary>★★ 材料不足要拒绝（且不能扣钱）。</summary>
    [Fact]
    public async Task 突破材料不足被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(突破材料不足被拒绝));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 15);
        var step = LoadTable().BreakOf(row.BreakConfigId)!.Steps[0];
        // 只给钱、不给材料
        // ★ AddMoney 是**增量**（新号本来就有初始金币）⇒ 断言要比对「操作前后差值」，
        //   不能假设余额恰好等于给的数（第一版这么写、红在 120000 vs 20000）。
        h.Player.AddMoney(step.CurrencyId, step.CurrencyCount);
        int moneyBefore = h.Player.GetMoney(step.CurrencyId);

        await BreakAsync(h, guid);

        Assert.True(Reply(h, RuneFeature.BreakCmd)!.ContainsKey("nError"));
        Assert.Equal(0u, h.Player.Items.First(i => i.Guid == guid).BreakLevel);
        // 拒绝路径不能扣钱
        Assert.Equal(moneyBefore, h.Player.GetMoney(step.CurrencyId));
    }

    /// <summary>★ 超过突破上限要拒绝。</summary>
    [Fact]
    public async Task 突破超上限被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(突破超上限被拒绝));
        ClearGateLevels(h);
        var table = LoadTable();
        var pick = FindRune(table, 1)!;
        var brk = table.BreakOf(pick.BreakConfigId)!;

        uint guid = 7777;
        h.Player.Items.Add(new Item
        {
            Guid = guid, Genre = 3, Detail = (uint)pick.D,
            Particular = (uint)pick.P, Level = (uint)pick.L,
            Count = 1, EnhanceLevel = 30, BreakLevel = (uint)brk.MaxBreak,
        });
        h.Player.AddMoney(RuneFeature.GoldMoneyId, 10_000_000);

        await BreakAsync(h, guid);

        Assert.True(Reply(h, RuneFeature.BreakCmd)!.ContainsKey("nError"));
    }

    /// <summary>★ 突破非法参数不崩且有回包。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"nGuid\":999999}")]
    [InlineData("[]")]
    public async Task 突破非法参数不崩(string json)
    {
        var h = await FeatureHarness.CreateAsync(nameof(突破非法参数不崩) + json.GetHashCode());
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(RuneFeature.BreakCmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
    }
    // ================================================================ 33 重铸 / 34 用新技能（第十三轮）

    private static Task RecastAsync(FeatureHarness h, uint guid)
        => h.DispatchLuaCallAsync(RuneFeature.RecastCmd, $"{{\"rune\":{guid}}}");

    private static Task UseRecastAsync(FeatureHarness h, uint guid, bool useNew)
        => h.DispatchLuaCallAsync(RuneFeature.UseRecastCmd,
            $"{{\"rune\":{guid},\"usenew\":{(useNew ? "true" : "false")}}}");

    /// <summary>把重铸消耗备齐。</summary>
    private static void GiveRecastCost(FeatureHarness h, RuneRow row)
    {
        if (row.RecastMoneyCount > 0)
            h.Player.AddMoney(row.RecastMoneyId, row.RecastMoneyCount);
        foreach (var need in row.RecastItems)
        {
            h.Player.Items.Add(new Item
            {
                Guid = (uint)(h.Player.Items.Count + 9000),
                Genre = (uint)need.G, Detail = (uint)need.D,
                Particular = (uint)need.P, Level = (uint)need.L,
                Count = (uint)need.N,
            });
        }
    }

    /// <summary>
    /// ★★★ 重铸池按星级各 6 条（<c>RuneSkillList.txt</c> 24 条 / 4 档），
    /// 且每条只有一个属性列非空（= 一种词条）。
    /// </summary>
    [Fact]
    public void 重铸池按星级分布均匀()
    {
        var t = LoadTable();
        Assert.Equal(24, t.Skills.Count);
        for (int rarity = 1; rarity <= 4; rarity++)
            Assert.Equal(6, t.Skills.Count(s => s.RuneRarity == rarity));
    }

    /// <summary>
    /// ★★ 抽词条：数量为 3、**不重复**、且都属该星级。
    /// （客户端两处渲染都是 <c>while SubIdx &lt;= 3 and …</c>）
    /// </summary>
    [Fact]
    public void 重铸抽三条不重复词条()
    {
        // 抽取逻辑本身走端到端断言（见「重铸替换子技能并扣消耗」），
        // 这里只验「池本身足够抽 3 条不重复」
        var t = LoadTable();
        for (int rarity = 1; rarity <= 4; rarity++)
        {
            var ids = t.Skills.Where(s => s.RuneRarity == rarity).Select(s => s.Id).ToList();
            Assert.True(ids.Count >= RuneFeature.MaxSubSkills);
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }
    }

    /// <summary>
    /// ★★★ 33 成功：子技能被替换成 3 条、<c>SkillType</c> 是 2、消耗被扣、不带 nError。
    /// ★ 服务端只发 skillId（数值由客户端 C# 侧的 <c>RuneSkillList.txt</c> 自算）。
    /// </summary>
    [Fact]
    public async Task 重铸替换子技能并扣消耗()
    {
        var h = await FeatureHarness.CreateAsync(nameof(重铸替换子技能并扣消耗));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 1);
        GiveRecastCost(h, row);
        int moneyBefore = h.Player.GetMoney(row.RecastMoneyId);

        await RecastAsync(h, guid);

        var reply = Reply(h, RuneFeature.RecastCmd);
        Assert.NotNull(reply);
        Assert.False(reply!.ContainsKey("nError"), "成功回包带了 nError");

        var rune = h.Player.Items.First(i => i.Guid == guid);
        Assert.Equal(RuneFeature.MaxSubSkills, rune.Skills.Count);
        Assert.All(rune.Skills, k => Assert.Equal(RuneFeature.RuneSkillType, k.SkillType));
        // 词条不重复
        Assert.Equal(rune.Skills.Count, rune.Skills.Select(k => k.SkillId).Distinct().Count());
        // 词条都属该星级
        var valid = LoadTable().Skills.Where(s => s.RuneRarity == row.Rarity).Select(s => (uint)s.Id).ToHashSet();
        Assert.All(rune.Skills, k => Assert.Contains(k.SkillId, valid));
        // 货币被扣
        if (row.RecastMoneyCount > 0)
            Assert.Equal(moneyBefore - row.RecastMoneyCount, h.Player.GetMoney(row.RecastMoneyId));
    }

    /// <summary>★★ 重铸会**覆盖**旧词条（不是累加 —— 累加会超过 3 条上限）。</summary>
    [Fact]
    public async Task 重铸覆盖旧词条不累加()
    {
        var h = await FeatureHarness.CreateAsync(nameof(重铸覆盖旧词条不累加));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 1);

        GiveRecastCost(h, row);
        await RecastAsync(h, guid);
        GiveRecastCost(h, row);
        await RecastAsync(h, guid);

        Assert.Equal(RuneFeature.MaxSubSkills,
            h.Player.Items.First(i => i.Guid == guid).Skills.Count);
    }

    /// <summary>★ 消耗不足要拒绝且**不改词条**。</summary>
    [Fact]
    public async Task 重铸消耗不足被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(重铸消耗不足被拒绝));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 1);
        // 清空货币、不给材料
        h.Player.AddMoney(row.RecastMoneyId, -h.Player.GetMoney(row.RecastMoneyId));

        await RecastAsync(h, guid);

        Assert.True(Reply(h, RuneFeature.RecastCmd)!.ContainsKey("nError"));
        Assert.Empty(h.Player.Items.First(i => i.Guid == guid).Skills);
    }

    /// <summary>★ 上行字段名是 <c>rune</c>（不是 <c>nGuid</c>）—— 用错会恒拒绝。</summary>
    [Fact]
    public async Task 重铸上行字段名是rune()
    {
        var h = await FeatureHarness.CreateAsync(nameof(重铸上行字段名是rune));
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 1);
        GiveRecastCost(h, row);

        // 用错字段名 ⇒ 应被拒绝
        await h.DispatchLuaCallAsync(RuneFeature.RecastCmd, $"{{\"nGuid\":{guid}}}");
        Assert.True(Reply(h, RuneFeature.RecastCmd)!.ContainsKey("nError"));

        // 正确字段名 ⇒ 成功
        await RecastAsync(h, guid);
        Assert.False(Reply(h, RuneFeature.RecastCmd)!.ContainsKey("nError"));
    }

    /// <summary>
    /// ★★★ 34 一律回包（成功/放弃都要）——
    /// 客户端成功分支要复位 <c>objUp_HaveClick</c>，不回包会让按钮**永久锁死**。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 用新技能一律回包(bool useNew)
    {
        var h = await FeatureHarness.CreateAsync(nameof(用新技能一律回包) + useNew);
        ClearGateLevels(h);
        var (guid, row) = SeedRune(h, rarity: 1, level: 1);
        GiveRecastCost(h, row);
        await RecastAsync(h, guid);

        int before = h.Outbound.Count;
        await UseRecastAsync(h, guid, useNew);

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.False(Reply(h, RuneFeature.UseRecastCmd)!.ContainsKey("nError"));
    }

    /// <summary>★ 33/34 非法参数不崩且有回包。</summary>
    [Theory]
    [InlineData(RuneFeature.RecastCmd, "{}")]
    [InlineData(RuneFeature.RecastCmd, "[]")]
    [InlineData(RuneFeature.RecastCmd, "null")]
    [InlineData(RuneFeature.UseRecastCmd, "{}")]
    [InlineData(RuneFeature.UseRecastCmd, "[]")]
    public async Task 重铸命令非法参数不崩(int cmd, string json)
    {
        var h = await FeatureHarness.CreateAsync($"rune-rc-{cmd}-{json.GetHashCode()}");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(cmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
    }
}
