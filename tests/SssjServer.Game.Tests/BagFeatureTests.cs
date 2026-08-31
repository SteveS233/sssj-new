using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 背包 / 仓库功能域（<c>ItemFlushFlag</c> / <c>LockItem</c> / 分解 25,26,32,349）。
///
/// 断言围绕三条最容易写错、且客户端会**静默失败**的规则展开：
/// <list type="number">
/// <item>★ LuaCall 族**成功回包不能带 <c>nError</c>**（客户端是 Lua 真值判断，
///   `0` 也是真 —— <c>UI_Warehouse.lua:1913/1936/1946</c>）。</item>
/// <item>★ 26 的回包是**数字下标数组** <c>[gold, [道具…]]</c>，
///   <c>tbParam[1]</c> 必填 number（<c>UI_Warehouse.lua:1949</c> 直接 `> 0` 比较）。</item>
/// <item>★ 「已锁定 / 在编队中」必须由**服务端**拦：客户端那两条保护在本版本是坏的
///   （<c>RuneItem.weaponGuid</c> 协议从不写、<c>bSolution</c> 检查被注释掉）。</item>
/// </list>
/// 价格口径来自真实 <c>extracted/setting/ItemList.txt</c> 的 <c>SellingPrice</c> 列
/// （客户端预算镜像在 <c>UI_Warehouse.lua:1409</c>）。
/// </summary>
public class BagFeatureTests
{
    /// <summary>蜂蜜牛奶 (5,1,1,2)：ShowType=3（材料 → 仓库「其他」页签），SellingPrice=900。</summary>
    private const uint HoneyG = 5, HoneyD = 1, HoneyP = 1, HoneyL = 2;
    private const int HoneyPrice = 900;

    [Fact]
    public void ItemList_HoneyMilk_SellingPriceIs900()
    {
        // 这条不是「测实现」，是把测试依赖的配置事实钉住：
        // 若哪天换了 ItemList.txt，下面几条分解断言的期望值需要同步改。
        var table = ItemBaseTable.Load(TestPaths.SettingDir);
        var tpl = table.Get(HoneyG, HoneyD, HoneyP, HoneyL);
        Assert.NotNull(tpl);
        Assert.Equal(HoneyPrice, tpl!.SellingPrice);
        Assert.Equal(3, tpl.ShowType);
    }

    // ================= 分解 25（按四元组）=================

    [Fact]
    public async Task SellItem25_Success_ReplyHasGoldAndNoNError()
    {
        using var h = await FeatureHarness.CreateAsync("bag-25");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 10);
        int goldBefore = h.Player.GetMoney(MoneyType.Gold);

        await h.DispatchLuaCallAsync(BagFeature.CmdSellItem, "[[5,1,1,2,3]]");

        // 扣了 3 个、加了 3×900
        Assert.Equal(7u, item.Count);
        Assert.Equal(goldBefore + 3 * HoneyPrice, h.Player.GetMoney(MoneyType.Gold));

        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var tb = JsonNode.Parse(json)!["tbParam"]!;
        Assert.Equal(3 * HoneyPrice, tb["Gold"]!.GetValue<int>());
        // ★ 成功回包**不能**出现 nError（Lua 里 0 也是真值）
        Assert.Null(tb["nError"]);
    }

    [Fact]
    public async Task SellItem25_TakesAcrossInstances_OldestFirst()
    {
        using var h = await FeatureHarness.CreateAsync("bag-25-multi");
        // 同一四元组两个实例：createtime 小的先被消耗（与 ItemSort 的 CreateTime 口径一致）
        var older = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 2);
        var newer = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 5);
        older.CreateTime = 1000;
        newer.CreateTime = 2000;

        await h.DispatchLuaCallAsync(BagFeature.CmdSellItem, "[[5,1,1,2,4]]");

        // 老的 2 个被清空并移除，新的扣 2 剩 3
        Assert.DoesNotContain(older, h.Player.Items);
        Assert.Equal(3u, newer.Count);
    }

    [Fact]
    public async Task SellItem25_LockedItem_IsRejectedWithError()
    {
        using var h = await FeatureHarness.CreateAsync("bag-locked");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 5);
        item.LockOn = 1;

        await h.DispatchLuaCallAsync(BagFeature.CmdSellItem, "[[5,1,1,2,2]]");

        Assert.Equal(5u, item.Count);                 // 一个都不能扣
        var tb = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.NotNull(tb["nError"]);                 // 失败回包必须带 nError
        Assert.NotNull(tb["sMsg"]);                   // 25/26 的 sMsg 是原文直显
    }

    [Fact]
    public async Task SellItem25_ItemInFormation_IsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("bag-information");
        var card = h.Player.AddItem(genre: 1, detail: 9, particular: 1, level: 3, count: 1);
        // 放进 1 号编队的主卡槽 —— 客户端的 bSolution 检查被注释掉了，只能服务端拦
        var formation = new Formation { Id = 1 };
        formation.Slots.Add(new FormationCardSlot { MainCardGuid = card.Guid, UsedCardGuid = card.Guid });
        h.Player.Formations.Add(formation);

        await h.DispatchLuaCallAsync(BagFeature.CmdSellItem, "[[1,9,1,3,1]]");

        Assert.Contains(card, h.Player.Items);
        Assert.NotNull(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["nError"]);
    }

    // ================= 分解 26（按 guid，回包是数字下标数组）=================

    [Fact]
    public async Task SellStuff26_ReplyIsPositionalArray()
    {
        using var h = await FeatureHarness.CreateAsync("bag-26");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 6);

        await h.DispatchLuaCallAsync(BagFeature.CmdSellStuff, $"[{{\"guid\":{item.Guid},\"num\":2}}]");

        Assert.Equal(4u, item.Count);

        var tb = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        var arr = Assert.IsType<JsonArray>(tb);
        // ★ tbParam[1] 是金币（Lua 下标 1 = JSON 下标 0），必须是 number 否则客户端 nil > 0 抛错
        Assert.Equal(2 * HoneyPrice, arr[0]!.GetValue<int>());
        // tbParam[2] 是返还道具数组（本实现暂不发额外材料 → 空数组，与「省略」等价）
        Assert.IsType<JsonArray>(arr[1]);
    }

    [Fact]
    public async Task SellStuff26_NumBeyondCount_IsClamped()
    {
        using var h = await FeatureHarness.CreateAsync("bag-26-clamp");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 2);

        await h.DispatchLuaCallAsync(BagFeature.CmdSellStuff, $"[{{\"guid\":{item.Guid},\"num\":99}}]");

        // 只扣得到的 2 个，金币按 2 计
        Assert.DoesNotContain(item, h.Player.Items);
        var arr = (JsonArray)JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Equal(2 * HoneyPrice, arr[0]!.GetValue<int>());
    }

    // ================= 分解 32 / 349（按 guid 列表）=================

    [Fact]
    public async Task SellRune32_ReplyHasGoldAndItems()
    {
        using var h = await FeatureHarness.CreateAsync("bag-32");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 1);

        await h.DispatchLuaCallAsync(BagFeature.CmdSellRune, $"[{item.Guid}]");

        Assert.DoesNotContain(item, h.Player.Items);
        var tb = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Equal(HoneyPrice, tb["Gold"]!.GetValue<int>());   // Gold 必填 number
        Assert.NotNull(tb["items"]);                             // items 可省，但给了空数组更省事
        Assert.Null(tb["nError"]);
    }

    [Fact]
    public async Task SellWeaponParts349_ReadsGuidListFromTbGuid()
    {
        using var h = await FeatureHarness.CreateAsync("bag-349");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 1);

        // 349 的上行外壳与 32 不同：{bConvert, tbGuid:[...]}
        await h.DispatchLuaCallAsync(BagFeature.CmdSellWeaponParts,
                                     $"{{\"bConvert\":true,\"tbGuid\":[{item.Guid}]}}");

        Assert.DoesNotContain(item, h.Player.Items);
        Assert.Equal(HoneyPrice, JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["Gold"]!.GetValue<int>());
    }

    // ================= ItemFlushFlag =================

    [Fact]
    public async Task ItemFlushFlag_ClearNew_ClearsBit0_AndSendsNoReply()
    {
        using var h = await FeatureHarness.CreateAsync("bag-flag1");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 1);
        item.Flag = Item.FlagNewBit | Item.FlagStoryReadBit;

        await h.DispatchFunctionAsync(BagFeature.FnItemFlushFlag,
                                     $"{{\"nCmd\":{BagFeature.FlushClearNew},\"nGuid\":{item.Guid}}}");

        Assert.Equal(Item.FlagStoryReadBit, item.Flag);   // 只清 bit0
        Assert.Empty(h.Outbound);                          // nCmd=1 无回包
    }

    [Fact]
    public async Task ItemFlushFlag_StoryRead_SetsBit1()
    {
        using var h = await FeatureHarness.CreateAsync("bag-flag3");
        var card = h.Player.AddItem(genre: 1, detail: 1, particular: 1, level: 3, count: 1);
        card.Flag = 0;

        await h.DispatchFunctionAsync(BagFeature.FnItemFlushFlag,
                                     $"{{\"nCmd\":{BagFeature.FlushStoryRead},\"nGuid\":{card.Guid}}}");

        // bit1 = 1 表示「剧情已读」（客户端 get_bStoryNew 取反用，RVA 0x174EEB0）
        Assert.Equal(Item.FlagStoryReadBit, card.Flag & Item.FlagStoryReadBit);
        Assert.Empty(h.Outbound);
    }

    [Fact]
    public async Task ItemFlushFlag_UseNonMoneyItem_RepliesUseItemMsgWithoutConsuming()
    {
        using var h = await FeatureHarness.CreateAsync("bag-use");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 3);

        await h.DispatchFunctionAsync(BagFeature.FnItemFlushFlag,
            $"{{\"nCmd\":{BagFeature.FlushUseItem},\"nGuid\":{item.Guid},\"Class\":\"UI_Warehouse\"}}");

        // 非货币道具：不扣、不发货币，但**必须回 UseItemMsg** 解 Blocking 遮罩
        Assert.Equal(3u, item.Count);
        Assert.Equal(BagFeature.FnUseItemMsg, h.LastOutbound.Function);
        Assert.Equal("UI_Warehouse", JsonNode.Parse(h.LastOutbound.Json)!["Class"]!.GetValue<string>());
    }

    [Fact]
    public async Task ItemFlushFlag_DropExpired_RefusesWhenNotActuallyExpired()
    {
        using var h = await FeatureHarness.CreateAsync("bag-expire");
        // 蜂蜜牛奶的 LimitTime = 0（永久）→ 服务端复核判定「未过期」，必须拒绝删除
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 1);

        await h.DispatchFunctionAsync(BagFeature.FnItemFlushFlag,
            $"{{\"nCmd\":{BagFeature.FlushDropExpired},\"tbGuid\":[{item.Guid}]}}");

        Assert.Contains(item, h.Player.Items);
        Assert.Empty(h.Outbound);
    }

    // ================= LockItem =================

    [Fact]
    public async Task LockItem_SetsAndClearsLockOn_WithoutReply()
    {
        using var h = await FeatureHarness.CreateAsync("bag-lock");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 1);

        await h.DispatchFunctionAsync(BagFeature.FnLockItem,
                                      $"{{\"nGuid\":{item.Guid},\"nLockOn\":1}}");
        Assert.Equal(1u, item.LockOn);
        Assert.Empty(h.Outbound);   // 无回包（客户端已本地写好）

        await h.DispatchFunctionAsync(BagFeature.FnLockItem,
                                      $"{{\"nGuid\":{item.Guid},\"nLockOn\":0}}");
        Assert.Equal(0u, item.LockOn);
    }

    [Fact]
    public async Task LockItem_ThenSell_IsRejected_ThenUnlock_Succeeds()
    {
        using var h = await FeatureHarness.CreateAsync("bag-lock-sell");
        var item = h.Player.AddItem(HoneyG, HoneyD, HoneyP, HoneyL, count: 4);

        await h.DispatchFunctionAsync(BagFeature.FnLockItem, $"{{\"nGuid\":{item.Guid},\"nLockOn\":1}}");
        await h.DispatchLuaCallAsync(BagFeature.CmdSellItem, "[[5,1,1,2,1]]");
        Assert.Equal(4u, item.Count);
        Assert.NotNull(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["nError"]);

        await h.DispatchFunctionAsync(BagFeature.FnLockItem, $"{{\"nGuid\":{item.Guid},\"nLockOn\":0}}");
        await h.DispatchLuaCallAsync(BagFeature.CmdSellItem, "[[5,1,1,2,1]]");
        Assert.Equal(3u, item.Count);
        Assert.Null(JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["nError"]);
    }

}
