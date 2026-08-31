using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 内购商店购买（<c>LuaCall.DoRecharge = 10000</c>）——
/// 商店界面里的「特殊补给 / 地球币矿池 / 十面妆造」三个入口共用这一条命令。
///
/// 断言重点：
/// <list type="number">
/// <item><b>★★ 第七种错误约定</b>：大写 <c>Error</c> + **数值**比较
///   （<c>IBLogic.lua:798</c> <c>if tbParam.Error &gt; 0</c>）⇒ 成功也必须显式给 0，
///   缺了客户端抛「attempt to compare nil with number」。</item>
/// <item><b>★★★ 真钱哨兵 <c>Currency == -1</c> 必须被拒绝</b> —— 那条路必经
///   <c>Game.Login.XGCharge</c>，而 <c>platform=selftest</c> 下 SDK 从未初始化。
///   判错方向的后果：要么 299 件真钱商品变免费，要么 42 件本可购买的商品全部买不了。</item>
/// <item><b><c>tbItem</c> 必须给（空数组也行）</b> —— <c>IBLogic.lua:882</c> 的
///   <c>ipairs(tbParam.tbItem)</c> 对 nil 会抛。</item>
/// <item><b>货币校验只能由服务端做</b> —— 客户端 <c>DoBuyBox</c> 全程没有 <c>CheckMoney</c>。</item>
/// <item><b>限购要写任务值 <c>(42, id + 10000)</c> 并推 pf 1026</b>，
///   否则客户端不会把商品挪进「售罄」区。</item>
/// </list>
/// </summary>
public class IbShopFeatureTests
{
    /// <summary>
    /// ★ 验收靶子：十面妆造在售、货币 62（造型U型夹）、原价 75、总计限购 1 次。
    /// 本地快照与真机都有这一件（真机在售 8 件里最便宜的那件）。
    /// </summary>
    private const int DressGoodsId = 1002;

    /// <summary>另一件十面妆造在售品（原价 138），用来验「限购只锁自己那一件」。</summary>
    private const int DressGoodsId2 = 1011;

    /// <summary>造型U型夹（<c>MoneyLogic.eMoneyType_Hairpin</c>，<c>MoneyLogic.lua:57</c>）。</summary>
    private const int Hairpin = 62;

    private static Task SendAsync(FeatureHarness h, string json)
        => h.LuaCalls.DispatchAsync(h.Session, "LuaCall",
            $"{{\"sCmd\":{IbShopFeature.CmdDoRecharge},\"tbParam\":{json}}}");

    private static JsonObject LastReply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var outer = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(IbShopFeature.CmdDoRecharge, outer["sCmd"]!.GetValue<int>());
        return Assert.IsType<JsonObject>(outer["tbParam"]);
    }

    private static string Buy(int id, int type = IbShopFeature.TypeIbShop)
        => $"{{\"Type\":{type},\"Id\":{id}}}";

    // ================= ① 配置表探针 =================

    [Fact]
    public void Table_LoadsGoods_AndDressShopIsOnSale()
    {
        var table = IbShopTable.Load(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 本地快照 284 件（★ 真机 341 件 —— 本地表少 55 行，见 AGENTS.md §5）
        // 所以只断言「至少」，避免换快照就红
        Assert.True(table.Count >= 284, $"只载入 {table.Count} 件内购商品");

        var dress = table.OnSaleAt(IbShopTable.LabelDress, now).ToList();
        Assert.NotEmpty(dress);
        // 十面妆造在售品**全部**是非真钱（这是本域能做的根据）
        Assert.All(dress, g => Assert.False(g.IsRealMoney));
        Assert.All(dress, g => Assert.Equal(IbShopTable.LabelDress, g.LabelIdx));

        var goods = table.Get(DressGoodsId);
        Assert.NotNull(goods);
        Assert.Equal(Hairpin, goods!.Currency);
        Assert.Equal(75, goods.Cost);
        Assert.Equal(IbShopTable.LabelDress, goods.LabelIdx);
        Assert.Equal(4, goods.LimitType);        // 4 = 总计限购
        Assert.Equal(1, goods.LimitTimes);
        // ★ GDPL 列是**逗号**分隔（[13,2,1,6001,1]）—— 与 gacha 的短横不同
        Assert.Equal(new[] { 13, 2, 1, 6001, 1 }, goods.Gdpl);
        // EndTime 为空 ⇒ 永久在售
        Assert.Equal(0, goods.EndTime);
        Assert.True(goods.IsOnSaleAt(now));
    }

    /// <summary>
    /// ★★★ 真钱哨兵：<c>Currency == -1</c> 的商品占绝大多数，必须被识别出来。
    /// </summary>
    [Fact]
    public void Table_RealMoneyGoodsAreTheMajority_AndFlaggedCorrectly()
    {
        var table = IbShopTable.Load(TestPaths.SettingDir);

        int real = table.All.Count(g => g.IsRealMoney);
        int payable = table.All.Count(g => !g.IsRealMoney);

        Assert.True(real > payable, $"真钱 {real} 件、非真钱 {payable} 件 —— 与真机分布(299:42)不符？");
        Assert.All(table.All.Where(g => g.Currency == -1), g => Assert.True(g.IsRealMoney));
        Assert.All(table.All.Where(g => g.Currency != -1), g => Assert.False(g.IsRealMoney));
    }

    /// <summary>
    /// ★★ 折扣缺省值必须是原价，**不能是 0** ——
    /// `docs/05` §5.1.1 记过常规商店的同款坑（漏发 discount ⇒ 0 折免费）。
    ///
    /// ★ 顺带钉住一条实测事实（写这条测试时先猜错过一次）：
    /// 商品 1002 **是配了折扣的**（<c>OffFlag=12 / OffCost=66 / 折扣期 2021-01-01~2021-01-21</c>），
    /// 只是那个折扣期早已过去 ⇒ 现在应当按**原价 75** 收费。
    /// 这正好覆盖 <c>PriceAt</c> 最容易写错的一支：
    /// 「配了折扣但不在折扣期」必须回退到原价，而不是无条件用 <c>OffCost</c>。
    /// </summary>
    [Fact]
    public void Table_PriceDefaultsToCost_WhenDiscountWindowHasPassed()
    {
        var table = IbShopTable.Load(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var goods = table.Get(DressGoodsId)!;
        // 配了 1 组折扣，但折扣期已过
        Assert.Single(goods.OffCosts);
        Assert.Equal(66, goods.OffCosts[0]);
        Assert.True(goods.OffEndTime > 0 && now > goods.OffEndTime,
            "商品 1002 的折扣期应当已经结束（若配置改了这条要重算）");

        // ⇒ 实付 = 原价 75，不是折后 66，更不是 0
        Assert.Equal(goods.Cost, goods.PriceAt(now));
        Assert.Equal(75, goods.PriceAt(now));

        // 全表：任何在售商品的实付价都不该是 0（除非原价本身是 0）
        foreach (var g in table.All.Where(g => g.IsOnSaleAt(now) && g.Cost > 0))
            Assert.True(g.PriceAt(now) > 0, $"商品 {g.Id} 的实付价算成了 0（原价 {g.Cost}）");
    }

    /// <summary>
    /// 折扣**在期内**时要用折后价（<c>IBLogic:GetIBShopOffInfo</c> 第 1 组分支）。
    /// 现网数据里在售且折扣在期的商品不一定存在，所以这条用构造出来的商品直接验 <c>PriceAt</c>。
    /// </summary>
    [Fact]
    public void PriceAt_UsesDiscount_WhenInsideDiscountWindow()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var goods = new IbShopGoods
        {
            Id = 1,
            Cost = 100,
            OffFlags = new[] { 12 },
            OffCosts = new[] { 66 },
            OffStartTime = now - 3600,
            OffEndTime = now + 3600,
        };

        Assert.Equal(66, goods.PriceAt(now));
        // 折扣期之外回原价
        Assert.Equal(100, goods.PriceAt(now + 7200));
    }

    /// <summary>货币映射：-2 → 免费钻、-3 → 金币、&gt;0 → 代币 id 本身。</summary>
    [Fact]
    public void ResolveMoneyId_MapsSentinelsAndTokens()
    {
        Assert.Equal(MoneyType.FreeDiamond, IbShopFeature.ResolveMoneyId(-2));
        Assert.Equal(MoneyType.Gold, IbShopFeature.ResolveMoneyId(-3));
        Assert.Equal(Hairpin, IbShopFeature.ResolveMoneyId(Hairpin));
        Assert.Equal(63, IbShopFeature.ResolveMoneyId(63));
    }

    // ================= ② 购买成功 =================

    [Fact]
    public async Task Buy_DressGoods_SpendsHairpin_GrantsItem_AndRecordsPurchase()
    {
        using var h = await FeatureHarness.CreateAsync("ib-buy");
        var goods = IbShopTable.Load(TestPaths.SettingDir).Get(DressGoodsId)!;
        h.Player.SetMoney(Hairpin, 500);
        // 妆造商品 1002 的皮肤属少女 12（SpineSuit.txt：19-1-2-3 → girl 12 / model 7001）
        if (h.Player.GetGirl(12) is null)
            h.Player.Girls.Add(new Girl { Girlid = 12, Modelid = 1 });

        await SendAsync(h, Buy(DressGoodsId));

        var reply = LastReply(h);
        // ★ 第七种约定：大写 Error，成功也必须显式给 0
        Assert.Equal(IbShopFeature.ErrOk, reply["Error"]!.GetValue<int>());
        Assert.Equal(IbShopFeature.TypeIbShop, reply["Type"]!.GetValue<int>());
        Assert.Equal(DressGoodsId, reply["Id"]!.GetValue<int>());

        // ★★★ 第十轮修：商品的 GDPL 是 genre 13 **礼包**（ClassName=IBGiftAuto），
        // 现在会被 RewardService 就地展开成箱内 Fix 物品 ——
        // 所以到手的是 **genre 19 皮肤本体**（+ 皮肤背景），而不是那个用不掉的礼包。
        // 旧断言「tbItem 只有一项且是 13-2-1-6001」正是「买了皮肤什么都没得到」的固化。
        var items = Assert.IsType<JsonArray>(reply["tbItem"]);
        Assert.NotEmpty(items);
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 13 && i.Level == 6001);
        Assert.Contains(h.Player.Items, i => i.Genre == Config.GirlDressTable.SuitItemGenre);

        // ★ 皮肤到手同时要解锁服饰标记（不然换装列表里看不到）
        Assert.Equal(GirlDressFeature.SuitNew, GirlDressFeature.GetSuitValue(h.Player, 12, 7001));

        // 扣了 U型夹、购买记录 +1
        Assert.Equal(500 - goods.Cost, h.Player.GetMoney(Hairpin));
        Assert.Equal(1, h.Player.GetTask(IbShopFeature.TaskGroup,
                                        IbShopFeature.IbShopTaskOffset + DressGoodsId));
    }

    /// <summary>购买要推 pf 1032（货币）+ 1031（背包）+ 1026（购买记录任务值）。</summary>
    [Fact]
    public async Task Buy_PushesMoneyItemAndTaskValue()
    {
        using var h = await FeatureHarness.CreateAsync("ib-push");
        h.Player.SetMoney(Hairpin, 500);

        await SendAsync(h, Buy(DressGoodsId));

        Assert.Equal(IbShopFeature.ErrOk, LastReply(h)["Error"]!.GetValue<int>());
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf), "应推 pf 1032 货币变更");
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf), "应推 pf 1031 背包变更");
        // ★ 不推 1026 的话客户端不会把商品挪进「售罄」区
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp), "应推 pf 1026 购买记录");
    }

    // ================= ③ 失败路径 =================

    /// <summary>
    /// ★★★ 真钱商品必须明确拒绝（不能假装成功）。
    /// </summary>
    [Fact]
    public async Task Buy_RealMoneyGoods_IsRejected_AndNothingGranted()
    {
        using var h = await FeatureHarness.CreateAsync("ib-realmoney");
        var table = IbShopTable.Load(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var real = table.All.First(g => g.IsRealMoney && g.IsOnSaleAt(now));
        int itemsBefore = h.Player.Items.Count;

        await SendAsync(h, Buy(real.Id));

        Assert.Equal(IbShopFeature.ErrRealMoneyUnsupported,
                     LastReply(h)["Error"]!.GetValue<int>());
        Assert.Equal(itemsBefore, h.Player.Items.Count);
        Assert.Equal(0, h.Player.GetTask(IbShopFeature.TaskGroup,
                                        IbShopFeature.IbShopTaskOffset + real.Id));
    }

    /// <summary><c>Type = 2</c>（充值道具）也是真钱通道 ⇒ 拒绝。</summary>
    [Fact]
    public async Task Buy_IbItemType_IsRejectedAsRealMoney()
    {
        using var h = await FeatureHarness.CreateAsync("ib-type2");

        await SendAsync(h, Buy(1, IbShopFeature.TypeIbItem));

        Assert.Equal(IbShopFeature.ErrRealMoneyUnsupported,
                     LastReply(h)["Error"]!.GetValue<int>());
    }

    /// <summary>
    /// 商品不存在 ⇒ 20074。★ 这条在真机上会真的命中
    /// （本地表 284 件 / 真机 341 件，例如 id=1027 本地压根没有）。
    /// </summary>
    [Fact]
    public async Task Buy_UnknownGoods_Replies20074()
    {
        using var h = await FeatureHarness.CreateAsync("ib-unknown");

        await SendAsync(h, Buy(999999));

        Assert.Equal(IbShopFeature.ErrNoGoods, LastReply(h)["Error"]!.GetValue<int>());
        // ★ 失败也要给 tbItem（客户端 ipairs 对 nil 抛）
        Assert.IsType<JsonArray>(LastReply(h)["tbItem"]);
    }

    /// <summary>★ 客户端不查货币 ⇒ 服务端必须拦，否则玩家能负数购买。</summary>
    [Fact]
    public async Task Buy_NotEnoughCurrency_IsRejected_AndBalanceUnchanged()
    {
        using var h = await FeatureHarness.CreateAsync("ib-poor");
        h.Player.SetMoney(Hairpin, 3);          // 远不够 75

        await SendAsync(h, Buy(DressGoodsId));

        Assert.True(LastReply(h)["Error"]!.GetValue<int>() > 0);
        Assert.Equal(3, h.Player.GetMoney(Hairpin));     // 没扣
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 13 && i.Level == 6001);
        Assert.Equal(0, h.Player.GetTask(IbShopFeature.TaskGroup,
                                        IbShopFeature.IbShopTaskOffset + DressGoodsId));
    }

    /// <summary>限购 1 次 ⇒ 第二次买回 20075，且只锁自己那一件。</summary>
    [Fact]
    public async Task Buy_Twice_SecondIsLimited_ButOtherGoodsStillBuyable()
    {
        using var h = await FeatureHarness.CreateAsync("ib-limit");
        h.Player.SetMoney(Hairpin, 1000);

        await SendAsync(h, Buy(DressGoodsId));
        Assert.Equal(IbShopFeature.ErrOk, LastReply(h)["Error"]!.GetValue<int>());

        // 第二次：总计限购 1 次 ⇒ 拒绝
        await SendAsync(h, Buy(DressGoodsId));
        Assert.Equal(IbShopFeature.ErrLimitReached, LastReply(h)["Error"]!.GetValue<int>());
        Assert.Equal(1, h.Player.GetTask(IbShopFeature.TaskGroup,
                                        IbShopFeature.IbShopTaskOffset + DressGoodsId));

        // 另一件不受影响（限购是 per-id 的任务值）
        await SendAsync(h, Buy(DressGoodsId2));
        Assert.Equal(IbShopFeature.ErrOk, LastReply(h)["Error"]!.GetValue<int>());
    }

    /// <summary>等级不足 ⇒ 20079。</summary>
    [Fact]
    public async Task Buy_LevelTooLow_Replies20079()
    {
        using var h = await FeatureHarness.CreateAsync("ib-level");
        var table = IbShopTable.Load(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var gated = table.All.FirstOrDefault(g => !g.IsRealMoney && g.Level > 1 && g.IsOnSaleAt(now));
        if (gated is null)
            return;   // 现网没有这种商品时跳过（不硬编造数据）

        h.Player.Level = 1;
        h.Player.SetMoney(IbShopFeature.ResolveMoneyId(gated.Currency), 999999);

        await SendAsync(h, Buy(gated.Id));

        Assert.Equal(IbShopFeature.ErrLevel, LastReply(h)["Error"]!.GetValue<int>());
    }

    /// <summary>上行是数组时不抛（本项目踩过 <c>JsonNode["key"]</c> 对 JsonArray 抛）。</summary>
    [Fact]
    public async Task Buy_ArrayParam_DoesNotThrow()
    {
        using var h = await FeatureHarness.CreateAsync("ib-array");

        await SendAsync(h, "[]");

        // 认不出来也要回包（这一族客户端会一直等）
        Assert.Equal(IbShopFeature.ErrNoGoods, LastReply(h)["Error"]!.GetValue<int>());
    }
}
