using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 零件词条覆写域（LuaCall 351/352）。台账 `docs/05` §53、取证 `work/parts-refit-forensics.md`。
///
/// 断言重点（每条都对应一个「写错就卡死/静默失效」的点）：
/// <list type="number">
///   <item>★★★ 351 回包**必须带 `hasSp`** —— 客户端第一句
///     <c>if tbParam.hasSp == nil then return end</c>，而上行带 300ms Blocking
///     ⇒ 漏了就**永久卡遮罩**；</item>
///   <item>★★★ 回包必须**原样回显 `partGuid`** —— 客户端用它打开下一个界面；</item>
///   <item>★ 前置校验不过时**也要回包**（同样理由：不回就卡遮罩）；</item>
///   <item>★ 词条只能写 <c>Attrs</c> 的**下标 3..5**（0..2 是基础属性，写了会冲掉）；</item>
///   <item>★ 点数是「低 16 位原生 / 高 16 位充能」的复合编码，必须按位读写。</item>
/// </list>
/// </summary>
public class WeaponPartsRefitFeatureTests
{
    private static JsonObject LastTb(FeatureHarness h)
    {
        var (_, json) = h.LastOutbound;
        return Assert.IsType<JsonObject>(JsonNode.Parse(json)!["tbParam"]);
    }

    /// <summary>造一个零件（genre 4）。</summary>
    private static Item AddPart(FeatureHarness h, uint guid, uint detail = 1, uint level = 3)
    {
        var it = new Item
        {
            Guid = guid,
            Genre = WeaponPartsRefitFeature.PartsGenre,
            Detail = detail,
            Particular = 1,
            Level = level,
            Count = 1,
        };
        h.Player.Items.Add(it);
        return it;
    }

    // ================= 351 =================

    /// <summary>
    /// ★★★ 最要命的一条：回包必须带 <c>hasSp</c> 且原样回显 <c>partGuid</c>。
    /// 这条红了 = 玩家点「覆写」后永久卡在 300ms Blocking 遮罩里。
    /// </summary>
    [Fact]
    public async Task 覆写回包必须带hasSp且原样回显partGuid()
    {
        var h = await FeatureHarness.CreateAsync(nameof(覆写回包必须带hasSp且原样回显partGuid));
        AddPart(h, 5001);
        AddPart(h, 5002);
        h.Player.AddMoney(2, 999999);

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite,
            """{"partGuid":5001,"ItemGuids":[5002],"nType":1}""");

        var tb = LastTb(h);
        Assert.True(tb.ContainsKey("hasSp"), "回包缺 hasSp ⇒ 客户端第一句就 return，界面永久卡遮罩");
        // ★ 必须是 JSON 布尔（客户端判的是 `~= nil` 后直接当条件用）
        Assert.IsType<bool>(tb["hasSp"]!.GetValue<bool>());
        Assert.Equal(5001, tb["partGuid"]!.GetValue<int>());     // ★ 原样回显
        Assert.True(tb.ContainsKey("id"));
    }

    /// <summary>★ 前置校验不通过（零件不存在）时也**必须回包**，否则卡遮罩。</summary>
    [Fact]
    public async Task 零件不存在也要回包()
    {
        var h = await FeatureHarness.CreateAsync(nameof(零件不存在也要回包));

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite,
            """{"partGuid":99999,"ItemGuids":[],"nType":1}""");

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.True(LastTb(h).ContainsKey("hasSp"));
    }

    /// <summary>★ 货币不足也要回包（且不能扣钱、不能吃素材）。</summary>
    [Fact]
    public async Task 货币不足也要回包且不结算()
    {
        var h = await FeatureHarness.CreateAsync(nameof(货币不足也要回包且不结算));
        AddPart(h, 5001);
        AddPart(h, 5002);
        // ★ Harness 可能预置了货币，显式清零才能验「不足」这条路径
        h.Player.AddMoney(2, -h.Player.GetMoney(2));

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite,
            """{"partGuid":5001,"ItemGuids":[5002],"nType":1}""");

        Assert.True(LastTb(h).ContainsKey("hasSp"));
        // 素材没被吃掉
        Assert.Contains(h.Player.Items, i => i.Guid == 5002);
    }

    /// <summary>成功路径：扣货币 + 吃素材 + 产生待确认词条。</summary>
    [Fact]
    public async Task 覆写成功会扣钱吃素材并产生待确认词条()
    {
        var h = await FeatureHarness.CreateAsync(nameof(覆写成功会扣钱吃素材并产生待确认词条));
        AddPart(h, 5001);
        AddPart(h, 5002);
        h.Player.AddMoney(2, 999999);
        long before = h.Player.GetMoney(2);

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite,
            """{"partGuid":5001,"ItemGuids":[5002],"nType":1}""");

        Assert.True(h.Player.GetMoney(2) < before, "没有扣货币");
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 5002);   // 素材被吃掉
        var st = WeaponPartsRefitFeature.LoadStateStatic(h.Player);
        Assert.True(st.Pending.ContainsKey(5001), "没有记录待二选一的新词条");
        Assert.True(st.Pending[5001].NewAffixId > 0);
    }

    /// <summary>★ 上行是裸数组时不能抛（<c>JsonNode["k"]</c> 对 JsonArray 会抛）。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task 非法上行不抛(string tbParam)
    {
        var h = await FeatureHarness.CreateAsync("parts-bad-" + tbParam.Length);

        var ex = await Record.ExceptionAsync(() =>
            h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite, tbParam));

        Assert.Null(ex);
    }

    // ================= 352 =================

    /// <summary>★ 352 原样回显（客户端按 <c>nType</c> 分流、用 <c>bNew</c> 选文案）。</summary>
    [Fact]
    public async Task 保存二选一原样回显()
    {
        var h = await FeatureHarness.CreateAsync(nameof(保存二选一原样回显));
        AddPart(h, 5001);

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdSave,
            """{"guid":5001,"bNew":true,"nType":1}""");

        var tb = LastTb(h);
        Assert.Equal(5001, tb["guid"]!.GetValue<int>());
        Assert.True(tb["bNew"]!.GetValue<bool>());
        Assert.Equal(1, tb["nType"]!.GetValue<int>());
    }

    /// <summary>
    /// 选「新的」会把词条写进零件，且**只能写下标 3..5**
    /// （0..2 是基础属性，写了会把攻击/暴击等冲掉）。
    /// </summary>
    [Fact]
    public async Task 保留新词条只写词条槽()
    {
        var h = await FeatureHarness.CreateAsync(nameof(保留新词条只写词条槽));
        var part = AddPart(h, 5001);
        AddPart(h, 5002);
        h.Player.AddMoney(2, 999999);

        // 先覆写产生待确认词条
        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite,
            """{"partGuid":5001,"ItemGuids":[5002],"nType":1}""");
        int affixId = WeaponPartsRefitFeature.LoadStateStatic(h.Player).Pending[5001].NewAffixId;

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdSave,
            """{"guid":5001,"bNew":true,"nType":1}""");

        Assert.Equal(Item.AttrSlotCount, part.Attrs.Count);          // 补齐到 6 槽
        // ★ 前三槽（基础属性）没被动过
        for (int i = 0; i < WeaponPartsRefitFeature.AffixSlotStart; i++)
            Assert.Equal(0u, part.Attrs[i].AType);
        // 词条落在 3..5
        Assert.Contains(part.Attrs.Skip(WeaponPartsRefitFeature.AffixSlotStart),
            a => a.AType == (uint)affixId);
        // 待确认记录被消费
        Assert.False(WeaponPartsRefitFeature.LoadStateStatic(h.Player).Pending.ContainsKey(5001));
    }

    /// <summary>★ 选「旧的」不改零件，但仍要消费掉待确认记录。</summary>
    [Fact]
    public async Task 保留旧词条不改零件()
    {
        var h = await FeatureHarness.CreateAsync(nameof(保留旧词条不改零件));
        var part = AddPart(h, 5001);
        AddPart(h, 5002);
        h.Player.AddMoney(2, 999999);

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdOverwrite,
            """{"partGuid":5001,"ItemGuids":[5002],"nType":1}""");

        await h.DispatchLuaCallAsync(WeaponPartsRefitFeature.CmdSave,
            """{"guid":5001,"bNew":false,"nType":1}""");

        Assert.All(part.Attrs, a => Assert.Equal(0u, a.AType));
        Assert.False(WeaponPartsRefitFeature.LoadStateStatic(h.Player).Pending.ContainsKey(5001));
    }

    // ================= 配置表 =================

    /// <summary>★ 点数是「低 16 位原生 / 高 16 位充能」的复合编码（必须按位读写）。</summary>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(10, 5)]
    [InlineData(0, 7)]
    public void 点数编码往返(int org, int charge)
    {
        int v = PartsRefitTable.MakePoint(org, charge);
        var (o, c) = PartsRefitTable.SplitPoint(v);
        Assert.Equal(org, o);
        Assert.Equal(charge, c);
        Assert.Equal(org + charge, PartsRefitTable.TotalPoint(v));
    }

    /// <summary>
    /// ★ 真实表探针：覆写规则 8 条（4 类型 × 2 星级）、词缀池 22 条。
    /// ★★ 同表两列分隔符不同：<c>ItemNum</c> 逗号、<c>ActiveItem</c> 短横五元组。
    /// </summary>
    [Fact]
    public void 真实配置表结构()
    {
        var t = PartsRefitTable.Load(TestPaths.SettingDir);

        Assert.True(t.OverwriteCount >= 4, $"覆写规则只有 {t.OverwriteCount} 条");
        Assert.True(t.AffixCount >= 20, $"词缀池只有 {t.AffixCount} 条");

        var rule = t.Overwrite(1, 3);
        Assert.NotNull(rule);
        Assert.True(rule!.ItemNum.Count >= 3, "ItemNum 应是 [总,紫,金] 三段（逗号分隔）");
        Assert.True(rule.CurrencyNum > 0);
        // ★ ActiveItem 是短横五元组 G-D-P-L-N
        Assert.True(rule.ActiveItem.Count >= 4,
            $"ActiveItem 解析出 {rule.ActiveItem.Count} 段 —— 分隔符用错了？（应是短横）");

        // 词缀四档激活点数
        var affix = t.AllAffixes.First();
        Assert.True(affix.LevelPoints.Count >= 1);
        Assert.True(affix.LevelOf(999) >= 1);
        Assert.Equal(0, affix.LevelOf(0));
    }

    /// <summary>★ 两种分隔符各自解析正确（混用会静默得到空数组）。</summary>
    [Fact]
    public void 两种分隔符不能混用()
    {
        Assert.Equal(new[] { 30, 10, 20 }, PartsRefitTable.ParseCommaInts("[30,10,20]"));
        Assert.Equal(new[] { 7, 5, 1, 4, 1 }, PartsRefitTable.ParseDashInts("[7-5-1-4-1]"));
        // ★ 用错分隔符 ⇒ 整段解析不出来（int.TryParse("7-5-1-4-1") 失败）⇒ **空列表**。
        //   这正是 AGENTS.md §5 说的「混用解析器不抛不报错、只静默得到空数组」的形状。
        Assert.Empty(PartsRefitTable.ParseCommaInts("7-5-1-4-1"));
    }

    /// <summary>装配后能取到本域，且命令号正确。</summary>
    [Fact]
    public async Task 装配()
    {
        var h = await FeatureHarness.CreateAsync(nameof(装配));

        Assert.NotNull(h.Module.FindFeature<WeaponPartsRefitFeature>());
        Assert.Equal(new[] { 351, 352 }, WeaponPartsRefitFeature.HandledCmds);
    }
}
