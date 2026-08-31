using System.Runtime.CompilerServices;
using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 仓库路径定位：测试要读真实的 <c>extracted/setting/*.txt</c>，但
/// <c>dotnet test</c> 的工作目录与 <c>ArtifactsPath</c> 有关（可能落在 /tmp），
/// 所以用编译期嵌入的源文件路径反推仓库根，而不是靠运行期 CWD。
/// </summary>
internal static class RepoPaths
{
    /// <summary>仓库根目录（本文件在 <c>&lt;root&gt;/server/tests/SssjServer.Game.Tests/</c> 下）。</summary>
    public static string Root { get; } = Resolve();

    /// <summary><c>&lt;root&gt;/extracted/setting</c>。</summary>
    public static string SettingDir => Path.Combine(Root, "extracted", "setting");

    private static string Resolve([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "extracted", "setting")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException($"从 {thisFile} 向上找不到 extracted/setting");
    }
}

/// <summary>
/// 物品模板表（<c>extracted/setting/ItemList.txt</c>）。
///
/// 这张表在 APK 里**没有 txt**：客户端 <c>Game.FileItemBaseInfoReader.LoadFile()</c>
/// （RVA 0x178BEBC）把 <c>FilePath = "Item/ItemList.txt"</c>（dump.cs:559180）改成
/// <c>Assets/_Game/SettingGen/Item/ItemList.asset</c> 后走 <c>Game.Assets.Load&lt;FileItemBaseInfo&gt;</c>，
/// 读的是烘进 setting.u 的 ScriptableObject。本仓库用
/// <c>work/parse_itemlist.py</c> + <c>work/itemlist_to_setting.py</c> 把它解出成同口径文本。
/// 断言的每个数值都能在 dump / lua 里对上。
/// </summary>
public class ItemBaseTableTests
{
    private static ItemBaseTable Load() => ItemBaseTable.Load(RepoPaths.SettingDir);

    /// <summary>
    /// 全表 2379 行，且 (G,D,P,L) 是唯一主键（解析脚本已做过字节精确闭合校验：
    /// 2379 条变长记录刚好消耗完 raw，零剩余）。
    ///
    /// ★★ 第十轮更新：<c>ItemList</c> 从**新版 <c>setting.u</c>** 重解，2120 → **2379**
    /// （genre 13 礼包 24→41、genre 19 皮肤 24→48 等）。
    /// ★ 它是 MonoBehaviour 不是 TextAsset，走 <c>work/parse_itemlist.py</c>；
    /// **<c>path_id</c> 在新包里变了**（现为 <c>-2989788756228539293</c>），
    /// 要按 raw 字节 offset 28 的 <c>m_Name</c> 反查，不能沿用写死常量。见 `docs/05` §37.1。
    /// </summary>
    [Fact]
    public void Load_ReadsWholeTable_WithUniqueKeys()
    {
        var table = Load();
        Assert.Equal(2379, table.Count);
        Assert.Equal(table.Count, table.All.Count());
    }

    /// <summary>
    /// 1-1 奖励里那四个四元组的语义（ItemList.txt + ItemName.txt 的 zh_CN 名）。
    /// 只有 genre 15 的两个是货币；材料/道具不是。
    /// </summary>
    [Theory]
    // (G,D,P,L),          ClassName,               是否货币, 货币id, 中文名
    [InlineData(15, 1, 1, 1, "ItemGoldAuto", true, MoneyType.Gold, "晶币")]
    [InlineData(15, 2, 1, 1, "ItemFreeDiamondAuto", true, MoneyType.FreeDiamond, "地球币")]
    [InlineData(7, 1, 4, 1, "exp_card", false, 0, "1阶通用战场数据库")]
    [InlineData(7, 3, 1, 1, "exp_weapon", false, 0, "1阶武器补强液")]
    public void Get_Chapter11AwardTuples_HaveExpectedSemantics(
        uint g, uint d, uint p, uint l, string className, bool isMoney, int moneyId, string name)
    {
        var table = Load();

        var tpl = table.Get(g, d, p, l);
        Assert.NotNull(tpl);
        Assert.Equal(className, tpl!.ClassName);
        Assert.Equal(name, tpl.DisplayName);

        Assert.Equal(isMoney, table.TryGetMoneyId(g, d, p, l, out int actual));
        Assert.Equal(moneyId, actual);
    }

    /// <summary>
    /// 泛型代币类（<c>ItemLogic.lua:74..79</c> 的三个 ClassName）的货币 id 来自 <c>ExtParam2</c>。
    /// (15,6,1,1) 与 (15,7,1,1) 还能和 <c>UI_Player.lua:796..798</c>
    /// （TowerSmall=5 → (15,6,1,1)、TowerBig=6 → (15,7,1,1)）互相印证。
    /// </summary>
    [Theory]
    [InlineData(15, 6, 1, 1, "ItemNormalCoinAuto", 5)]    // MoneyLogic.eMoneyType_TowerSmall
    [InlineData(15, 7, 1, 1, "ItemNormalCoinAuto", 6)]    // MoneyLogic.eMoneyType_TowerBig
    [InlineData(15, 53, 1, 1, "ItemBattlePassCoinAuto", 31)] // eMoneyType_BattlePassCoin
    [InlineData(15, 54, 1, 1, "ItemMixTowerCoinAuto", 29)]   // eMoneyType_MixTowerCoin1
    [InlineData(15, 58, 1, 1, "ItemMixTowerCoinAuto", 34)]   // eMoneyType_MixTowerCoin5
    public void TryGetMoneyId_CoinAutoClasses_UseExtParam2(
        uint g, uint d, uint p, uint l, string className, int expectedMoneyId)
    {
        var table = Load();
        var tpl = table.Get(g, d, p, l);
        Assert.NotNull(tpl);
        Assert.Equal(className, tpl!.ClassName);
        Assert.Contains(className, ItemBaseTable.CoinAutoClassNames);
        Assert.Equal(expectedMoneyId.ToString(), tpl.ExtParam2);

        Assert.True(table.TryGetMoneyId(g, d, p, l, out int moneyId));
        Assert.Equal(expectedMoneyId, moneyId);
    }

    /// <summary>
    /// 表里凡是 ClassName 属于三个泛型代币类的行，Genre 必须是 15、ExtParam2 必须能解析成正整数，
    /// 且不同行不会撞同一个货币 id —— 这正是 <c>ItemLogic.lua:69..88</c> 建
    /// <c>tbItemCoinItem[ExtParam2]</c> 字典时隐含的前提。
    /// </summary>
    [Fact]
    public void CoinAutoRows_AreGenre15_WithDistinctNumericExtParam2()
    {
        var table = Load();
        var coinRows = table.All
            .Where(t => ItemBaseTable.CoinAutoClassNames.Contains(t.ClassName))
            .ToList();

        Assert.NotEmpty(coinRows);

        var seen = new Dictionary<int, ItemTemplate>();
        foreach (var row in coinRows)
        {
            Assert.Equal(15u, row.G);
            Assert.True(int.TryParse(row.ExtParam2, out int moneyId), $"{row} 的 ExtParam2 不是整数");
            Assert.True(moneyId > 0, $"{row} 的货币 id 非正");
            Assert.False(seen.ContainsKey(moneyId),
                $"货币 id {moneyId} 同时被 {seen.GetValueOrDefault(moneyId)} 与 {row} 占用");
            seen[moneyId] = row;
        }
    }

    /// <summary>
    /// 角色卡 / 武器 / 模块（genre 1..4）不在这张表里 —— 它们的模板在别的表
    /// （卡：CardRefit.txt 等）。所以 <see cref="ItemBaseTable.Get"/> 返回 null 是正常的，
    /// 不能因此把它们当成货币。表里实际存在的 Genre 是 5..22。
    /// </summary>
    [Fact]
    public void Get_InstanceGenres_AreNotInThisTable()
    {
        var table = Load();

        Assert.Null(table.Get(1, 2, 1, 1));   // 角色卡（1-5 的首通奖励）
        Assert.Null(table.Get(3, 1, 1, 1));   // 模块/Rune（随机奖励池的主力）
        Assert.False(table.TryGetMoneyId(1, 2, 1, 1, out _));

        var genres = table.All.Select(t => t.G).Distinct().Order().ToList();
        Assert.Equal(5u, genres.First());
        Assert.DoesNotContain(1u, genres);
        Assert.DoesNotContain(4u, genres);
    }

    /// <summary>
    /// 缺表降级：<see cref="ItemBaseTable.Empty"/> 查不到任何模板，但靠
    /// <see cref="ItemBaseTable.KnownMoneyItems"/>（Lib.lua:1364 GetMoneyGDPL 等）
    /// 仍然认得出主货币 —— 关卡结算只用到 (15,1,1,1) 与 (15,2,1,1)，所以缺表也不会错发。
    /// </summary>
    [Fact]
    public void Empty_DegradesGracefully_ButStillResolvesMainCurrencies()
    {
        var table = ItemBaseTable.Empty;

        Assert.Equal(0, table.Count);
        Assert.Null(table.Get(15, 1, 1, 1));
        Assert.Equal("(15,1,1,1)", table.NameOf(15, 1, 1, 1));

        Assert.True(table.TryGetMoneyId(15, 1, 1, 1, out int gold));
        Assert.Equal(MoneyType.Gold, gold);
        Assert.True(table.TryGetMoneyId(15, 2, 1, 1, out int diamond));
        Assert.Equal(MoneyType.FreeDiamond, diamond);
        Assert.True(table.TryGetMoneyId(15, 4, 1, 1, out int vigour));
        Assert.Equal(MoneyType.Vigour, vigour);

        // 缺表时非货币道具当然也认不出货币（会被当普通物品进背包）
        Assert.False(table.TryGetMoneyId(7, 1, 4, 1, out _));
    }

    /// <summary>
    /// 缺文件时 <see cref="ItemBaseTable.Load"/> 不抛异常，返回空表（打 WARN）。
    /// </summary>
    [Fact]
    public void Load_MissingDirectory_ReturnsEmptyTable()
    {
        var table = ItemBaseTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-setting-dir"));
        Assert.Equal(0, table.Count);
    }

    /// <summary>
    /// 硬映射表里的四元组不能与「ExtParam2 规则」冲突：凡是 KnownMoneyItems 里的行，
    /// 要么表里不存在这一行，要么它不是泛型代币类，要么两条规则给出同一个货币 id。
    /// （这条挡住的是「同一个道具被算成两种货币」这类静默错误。）
    /// </summary>
    [Fact]
    public void KnownMoneyItems_DoNotContradictExtParam2Rule()
    {
        var table = Load();
        foreach (var (key, moneyId) in ItemBaseTable.KnownMoneyItems)
        {
            var tpl = table.Get(key.G, key.D, key.P, key.L);
            if (tpl is null || !ItemBaseTable.CoinAutoClassNames.Contains(tpl.ClassName))
                continue;
            Assert.True(int.TryParse(tpl.ExtParam2, out int fromExt));
            Assert.Equal(moneyId, fromExt);
        }
    }

    /// <summary>
    /// 硬映射覆盖的 genre 15 道具，Genre 必须真的是 15（防手抖写错四元组）。
    /// </summary>
    [Fact]
    public void KnownMoneyItems_AreAllGenre15AndPresentInTable()
    {
        var table = Load();
        foreach (var (key, moneyId) in ItemBaseTable.KnownMoneyItems)
        {
            Assert.Equal(15u, key.G);
            Assert.True(moneyId > 0);
            Assert.NotNull(table.Get(key.G, key.D, key.P, key.L));
        }
    }
}
