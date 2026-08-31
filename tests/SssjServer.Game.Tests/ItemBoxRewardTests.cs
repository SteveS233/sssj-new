using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 礼包箱展开（<see cref="ItemBoxTable"/> + <see cref="RewardService.Boxes"/>）与
/// 「皮肤道具入包 ⇒ 解锁服饰」（<see cref="RewardService.Suits"/>）。台账 `docs/05` §37.3。
///
/// <para><b>这组测试钉的是「十面妆造买了皮肤却什么都没得到」的完整链</b></para>
/// <code>
/// PurchaseIBShop.GDPL = [13,1,1,6000,1]        -- 卖的是**礼包**（ClassName=IBGiftAuto）
///   → ItemList[13-1-1-6000].ExtParam1 = 6000   -- boxId
///     → ItemBoxInfo[6000] 的 Fix：[19-1-1-4-1] -- ★ 皮肤本体（genre 19）
///       → 任务值 group 4 的 SuitValue 置 1     -- ★ 换装列表按 >0 过滤
/// </code>
/// 四环缺任何一环，玩家都是「钱花了、界面上什么都没变」。
/// </summary>
public class ItemBoxRewardTests
{
    private static RewardService MakeService()
    {
        var svc = new RewardService(ItemBaseTable.Shared, new Random(12345));
        svc.Boxes = ItemBoxTable.Load(TestPaths.SettingDir);
        svc.Suits = GirlDressTable.Load(TestPaths.SettingDir);
        return svc;
    }

    // ================= 表解析 =================

    [Fact]
    public void 箱表_读到现网数据()
    {
        var t = ItemBoxTable.Load(TestPaths.SettingDir);

        Assert.True(t.BoxCount > 500, $"ItemBoxInfo.txt 只读到 {t.BoxCount} 个箱子，疑似表头行数/列名错");
        Assert.True(t.EntryCount > t.BoxCount);
    }

    [Fact]
    public void 箱表_皮肤礼包6000的Fix含genre19皮肤()
    {
        var t = ItemBoxTable.Load(TestPaths.SettingDir);

        // ★ 真机自报印证：`[Z1] box6000=2`（皮肤本体 + 皮肤背景）
        var fixedItems = t.FixedOf(6000).ToList();
        Assert.Equal(2, fixedItems.Count);
        Assert.Contains(fixedItems, e => e.Gdpl[0] == (int)GirlDressTable.SuitItemGenre);
    }

    [Fact]
    public void 箱表_GDPLN是短横分隔()
    {
        // ★★ 与 Friendliness.BanGirl 的逗号风格不同 —— 混用会静默得到空数组（AGENTS.md §5）
        var v = ItemBoxTable.ParseGdpln("[19-1-1-4-1]");
        Assert.NotNull(v);
        Assert.Equal(new[] { 19, 1, 1, 4, 1 }, v);

        // 第 5 段（数量）缺省 1
        var four = ItemBoxTable.ParseGdpln("[19-1-1-4]");
        Assert.NotNull(four);
        Assert.Equal(1, four![4]);

        Assert.Null(ItemBoxTable.ParseGdpln(""));
        Assert.Null(ItemBoxTable.ParseGdpln("[]"));
        Assert.Null(ItemBoxTable.ParseGdpln("[19-1]"));
    }

    [Fact]
    public void 皮肤表_读到48条且道具能反查()
    {
        var t = GirlDressTable.Load(TestPaths.SettingDir);

        // ★★ 第十轮从新版 setting.u 重解：24 → 48 条（真机自报 `SpineSuit 条数=48`）
        Assert.Equal(48, t.Count);
        // 少女 9 的 7001 皮肤是表里第一行，且带 IsModel
        var suit = t.Get(9, 7001);
        Assert.NotNull(suit);
        Assert.True(suit!.HasModel);
        Assert.Equal((19u, 1u, 1u, 4u), suit.Gdpl);
        // 道具 → 服饰的反查
        Assert.Same(suit, t.SuitForItem(19, 1, 1, 4));
    }

    [Fact]
    public void 皮肤表_IsModel空列要读成false()
    {
        var t = GirlDressTable.Load(TestPaths.SettingDir);

        // ★ IsModel 在无模型的行上是**空字符串**（不是 0）—— 少女 7 的 7002 就是
        Assert.False(t.SuitHasModel(7, 7002));
        Assert.True(t.IsSpineSuit(7, 7002));        // 但它确实是皮肤
        // 非皮肤（角色卡服装）两者都 false
        Assert.False(t.IsSpineSuit(1, 1));
        Assert.False(t.SuitHasModel(1, 1));
    }

    // ================= 礼包展开 =================

    [Fact]
    public void 发礼包_展开成箱内Fix物品而不是礼包本体()
    {
        var svc = MakeService();
        var player = new Player("boxtest", 1);

        // 13-1-1-6000 = 「朱颜藤梦·时无瑕」皮肤礼包（十面妆造 id=1001 卖的就是它）
        var granted = svc.Grant(player, new[] { new[] { 13, 1, 1, 6000, 1 } }, RewardSource.Extra);

        // ★★★ 礼包本体不该进背包
        Assert.DoesNotContain(player.Items, i => i.Genre == 13 && i.Level == 6000);
        // ★ 箱内的皮肤本体（genre 19）必须到手
        Assert.Contains(player.Items, i => i.Genre == GirlDressTable.SuitItemGenre);
        Assert.False(granted.IsEmpty);
    }

    [Fact]
    public void 发礼包_皮肤到手同时解锁服饰标记()
    {
        var svc = MakeService();
        var player = new Player("boxtest2", 1);
        // 箱 6000 的皮肤属少女 9 ⇒ 必须先持有她（否则按设计只警告不置位）
        player.Girls.Add(new Girl { Girlid = 9, Modelid = 1 });

        var granted = svc.Grant(player, new[] { new[] { 13, 1, 1, 6000, 1 } }, RewardSource.Extra);

        // ★★★ 这一步漏掉 ⇒ 皮肤在背包里但换装界面看不到（列表按 SuitValue>0 过滤）
        Assert.Equal(GirlDressFeature.SuitNew,
                     GirlDressFeature.GetSuitValue(player, 9, 7001));
        // ★ 变更要能被推出去（pf 1026）
        Assert.Contains(granted.ChangedTasks,
            kv => kv.Key == Player.ComposeTaskId(GirlDressFeature.SuitGroup,
                                                 GirlDressFeature.SuitTaskId(9, 7001)));
    }

    [Fact]
    public void 发礼包_未持有少女时不置位但仍发道具()
    {
        var svc = MakeService();
        var player = new Player("boxtest3", 1);
        player.Girls.Clear();

        svc.Grant(player, new[] { new[] { 13, 1, 1, 6000, 1 } }, RewardSource.Extra);

        // 道具照发（拿到少女后登录钩子会补标记）
        Assert.Contains(player.Items, i => i.Genre == GirlDressTable.SuitItemGenre);
        Assert.Equal(GirlDressFeature.SuitNone, GirlDressFeature.GetSuitValue(player, 9, 7001));
    }

    [Fact]
    public void 发礼包_买N份则箱内每项乘N()
    {
        var svc = MakeService();
        var player = new Player("boxtest4", 1);
        player.Girls.Add(new Girl { Girlid = 9, Modelid = 1 });

        svc.Grant(player, new[] { new[] { 13, 1, 1, 6000, 3 } }, RewardSource.Extra);

        // 箱内 [6-25-2-3-1]（皮肤背景，genre 6 ⇒ 可叠加）应是 1×3
        var bg = player.Items.FirstOrDefault(i => i.Genre == 6 && i.Detail == 25);
        Assert.NotNull(bg);
        Assert.Equal(3u, bg!.Count);
    }

    [Fact]
    public void 发礼包_没挂箱表时保持旧行为()
    {
        // ★ Boxes 为 null ⇒ 礼包按普通道具进背包（第九轮以前的行为，向后兼容）
        var svc = new RewardService(ItemBaseTable.Shared, new Random(1));
        var player = new Player("boxtest5", 1);

        svc.Grant(player, new[] { new[] { 13, 1, 1, 6000, 1 } }, RewardSource.Extra);

        Assert.Contains(player.Items, i => i.Genre == 13 && i.Level == 6000);
        Assert.DoesNotContain(player.Items, i => i.Genre == GirlDressTable.SuitItemGenre);
    }

    [Fact]
    public void 发皮肤道具_直接给也会解锁服饰()
    {
        var svc = MakeService();
        var player = new Player("boxtest6", 1);
        player.Girls.Add(new Girl { Girlid = 9, Modelid = 1 });

        // 不经礼包，直接发 genre 19（GM 发货 / 邮件都可能走这条路）
        svc.Grant(player, new[] { new[] { 19, 1, 1, 4, 1 } }, RewardSource.Extra);

        Assert.Equal(GirlDressFeature.SuitNew, GirlDressFeature.GetSuitValue(player, 9, 7001));
    }

    [Fact]
    public void 发皮肤道具_已穿过的不被洗回NEW()
    {
        var svc = MakeService();
        var player = new Player("boxtest7", 1);
        player.Girls.Add(new Girl { Girlid = 9, Modelid = 7001 });
        GirlDressFeature.SetSuitValue(player, 9, 7001, GirlDressFeature.SuitWorn, updates: null);

        var granted = svc.Grant(player, new[] { new[] { 19, 1, 1, 4, 1 } }, RewardSource.Extra);

        // ★ 幂等：不动 >0 的，也不产生多余的任务值推送
        Assert.Equal(GirlDressFeature.SuitWorn, GirlDressFeature.GetSuitValue(player, 9, 7001));
        Assert.Empty(granted.ChangedTasks);
    }

    [Fact]
    public void 发普通道具_不受礼包逻辑影响()
    {
        var svc = MakeService();
        var player = new Player("boxtest8", 1);

        // 金币（货币类道具）应照旧折算成货币，不进背包
        svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 5000 } }, RewardSource.Extra);

        Assert.True(player.GetMoney(MoneyType.Gold) >= 5000);
    }
}
