using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <c>SystemUnlock.txt</c> 解锁表 + <c>ServerConsoleFeature</c> 的 <c>unlock</c> 依赖。
///
/// 这张表是「主城为什么全是灰锁」的答案：<c>SystemUnlock:CheckLevel</c>
/// （<c>extracted/lua/SystemUnlock.lua:73</c>）只看 <c>me:GetPlayerLevelData(ch,idx,diff)</c>，
/// 也就是服务端下发的 <c>PlayerNtf.levellist</c> / pf 1034 那张关卡字典 ——
/// 与玩家等级无关。详见 <c>docs/04-level-flow.md §4</c>。
/// </summary>
public class SystemUnlockTableTests
{
    [Fact]
    public void Load_ReadsAllEntries()
    {
        var table = SystemUnlockTable.Load(TestPaths.SettingDir);
        // 现网 SystemUnlock.txt 是 77 条（80 行 - 3 行表头）
        Assert.Equal(77, table.Entries.Count);
    }

    [Fact]
    public void KnownKeys_MapToExpectedChapters()
    {
        var table = SystemUnlockTable.Load(TestPaths.SettingDir);

        // 逐条对照 extracted/setting/SystemUnlock.txt 的原文
        Assert.Equal((1, 2, 1), Triple(table.Get("UI_UnlockGacha")));       // 抽奖解锁
        Assert.Equal((2, 1, 1), Triple(table.Get("UI_UnlockShop")));        // 商店解锁
        Assert.Equal((1, 1, 1), Triple(table.Get("UI_UnlockMission")));     // 任务解锁
        Assert.Equal((1, 3, 1), Triple(table.Get("UI_UnlockCoffee")));      // 咖啡馆解锁
        Assert.Equal((1, 6, 1), Triple(table.Get("UI_UnlockAutoFight")));   // 自动战斗解锁
        Assert.Equal((6, 12, 1), Triple(table.Get("UI_UnlockPromise")));    // 誓约解锁
    }

    /// <summary>
    /// 哨兵行必须被识别出来，否则 <c>unlock all</c> 会给玩家记上 30-30-1 这种不存在的关卡。
    ///
    /// ★★★ 第十轮更新：新版 <c>setting.u</c> 把
    /// <c>UI_UnlockChapterChallenge11..20</c> 的 <c>Chapter</c> 从占位值 <b>1000 改成真实章节 10</b>
    /// （故事挑战模式后来实装了 —— <c>Chapter.txt</c> 里 10-12-2「Level10_12(精英)」确实存在）
    /// ⇒ 它**不再是占位行**，现在是一条正常的解锁配置。
    /// 这同时印证 <c>IsPlaceholder</c> 那条「Chapter &gt; 999 才算占位」的判据是对的：
    /// 数据一变判定自动跟着变，不用改代码。
    /// </summary>
    [Fact]
    public void SentinelRows_AreDetected()
    {
        var table = SystemUnlockTable.Load(TestPaths.SettingDir);

        // SystemUnlock.txt:45 —— Key=None、注释写着「无效配置」
        var none = table.Get(SystemUnlockTable.SentinelKey);
        Assert.NotNull(none);
        Assert.True(none!.IsPlaceholder);

        // 故事挑战模式：新版指向真实关卡 10-12-2 ⇒ 不再是占位
        var challenge = table.Get("UI_UnlockChapterChallenge11");
        Assert.NotNull(challenge);
        Assert.Equal(10, challenge!.Chapter);
        Assert.False(challenge.IsPlaceholder);

        // 正常行不能被误判
        Assert.False(table.Get("UI_UnlockShop")!.IsPlaceholder);
    }

    [Fact]
    public void RealMapIds_AreDeduplicatedAndExcludePlaceholders()
    {
        var table = SystemUnlockTable.Load(TestPaths.SettingDir);
        var ids = table.RealMapIds();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        // ★ 第十轮：故事挑战模式转正（1000-12-2 → 10-12-2），但 **总数仍是 22** ——
        // 因为 `UI_UnlockChapterChallenge`/`5..10` 这些 key **本来就指向 10-12-2**，
        // 转正后只是并入了同一关（多个 Key 共用一关是常态）⇒ 去重后不增加。
        // ★★ 这也是「不能按行数推条数」的又一个例子：改了 10 行数据，去重结果一个没变。
        Assert.Equal(22, ids.Count);

        // 哨兵行的 mapId 不在里面
        Assert.DoesNotContain(MapList.MapId(30, 30, 1), ids);
        // 旧的占位 mapId 当然也不在（那个 Chapter 值已经不存在于表里了）
        Assert.DoesNotContain(MapList.MapId(1000, 12, 2), ids);
        // ★ 但它转正后的真身**必须在**
        Assert.Contains(MapList.MapId(10, 12, 2), ids);

        // 商店那一关（2-1-1）必须在
        Assert.Contains(MapList.MapId(2, 1, 1), ids);
    }

    [Fact]
    public void Entry_MapId_MatchesMapListEncoding()
    {
        var table = SystemUnlockTable.Load(TestPaths.SettingDir);
        var shop = table.Get("UI_UnlockShop")!;
        // mapId = (chapter<<16) | (index<<8) | diff（Game.MapList.GetMapId RVA 0x1D4C640）
        Assert.Equal(MapList.MapId(shop.Chapter, shop.Index, shop.Diff), shop.MapId);
        Assert.Equal((2, 1, 1), MapList.Decode(shop.MapId));
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyWithoutThrowing()
    {
        var table = SystemUnlockTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-dir"));
        Assert.Empty(table.Entries);
        Assert.Empty(table.RealMapIds());
        Assert.Null(table.Get("UI_UnlockShop"));
    }

    private static (int, int, int) Triple(SystemUnlockTable.Entry? e)
    {
        Assert.NotNull(e);
        return (e!.Chapter, e.Index, e.Diff);
    }
}
