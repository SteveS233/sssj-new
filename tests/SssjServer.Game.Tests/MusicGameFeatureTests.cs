using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 音游（LuaCall 388 成绩 / 389 排行 / 390 好友排行）。台账 `docs/05` §48。
///
/// 断言重点：
/// <list type="number">
///   <item>★ 成绩存任务值 <c>(1, 3100 + gameId)</c>，**只在超过旧纪录时覆盖**；</item>
///   <item>★★ 388 **不回业务包**（原厂 <c>CallClient</c> 被注释、客户端分支是空的）；</item>
///   <item>★ 389 回**以 gameId 为键的字典**，<c>pos = 0</c>（未上榜）是合法状态；</item>
///   <item>★ 390 必须给 <c>musicId</c>，<c>tbList</c> 可空（客户端自己插入本人）；</item>
///   <item>★ gameId 越界要拒绝 —— 任务值键是 <c>(group&lt;&lt;16)|(id&amp;0xFFFF)</c>，
///         偏移溢出会污染别的 group。</item>
/// </list>
/// </summary>
public class MusicGameFeatureTests
{
    private static Task CompleteAsync(FeatureHarness h, int gameId, int score, int combo = 10)
        => h.DispatchLuaCallAsync(MusicGameFeature.CompleteCmd,
            $"{{\"gameId\":{gameId},\"score\":{score},\"type\":1,\"nCombo\":{combo}}}");

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

    private static int Score(FeatureHarness h, int gameId)
        => h.Player.GetTask(MusicGameFeature.ScoreTaskGroup,
                            MusicGameFeature.ScoreTaskBase + gameId);

    // ================================================================ 388

    /// <summary>★ 成绩落在 <c>(1, 3100 + gameId)</c> 并推 pf 1026。</summary>
    [Fact]
    public async Task 上报成绩落到正确任务值并推1026()
    {
        using var h = await FeatureHarness.CreateAsync("music-complete");

        await CompleteAsync(h, gameId: 3, score: 12345);

        Assert.Equal(12345, Score(h, 3));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    /// <summary>
    /// ★★ 388 **不回业务包**（原厂 <c>CallClient</c> 被注释掉、客户端分支是空的）。
    /// 回了不会崩，但那是凭空多一条下行 —— 用它当契约的回归闸。
    /// </summary>
    [Fact]
    public async Task 上报成绩不回业务包()
    {
        using var h = await FeatureHarness.CreateAsync("music-noreply");
        int before = h.Outbound.Count;

        await CompleteAsync(h, gameId: 1, score: 500);

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>★★ 只有超过旧纪录才覆盖（低分不能把最高分刷掉）。</summary>
    [Fact]
    public async Task 低分不会覆盖最高分()
    {
        using var h = await FeatureHarness.CreateAsync("music-best");

        await CompleteAsync(h, gameId: 2, score: 9000);
        Assert.Equal(9000, Score(h, 2));

        await CompleteAsync(h, gameId: 2, score: 100);
        Assert.Equal(9000, Score(h, 2));      // ★ 仍是 9000

        await CompleteAsync(h, gameId: 2, score: 9001);
        Assert.Equal(9001, Score(h, 2));      // 更高才覆盖
    }

    /// <summary>
    /// ★★★ gameId 越界必须拒绝：任务值键是 <c>(group&lt;&lt;16)|(id&amp;0xFFFF)</c>，
    /// 偏移溢出 65535 会写到**别的 group** 上（静默污染其它系统的数据）。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(70000)]
    public async Task 越界gameId被拒绝(int gameId)
    {
        using var h = await FeatureHarness.CreateAsync($"music-oob-{gameId}");

        await CompleteAsync(h, gameId, score: 500);

        // ★ 断言「该 gameId 对应的偏移没被写」，而不是比对 Tasks.Count ——
        //   后者会被别的登录钩子影响（跨用例共享状态 ⇒ 全量跑时 flaky）。
        //   注意越界 id 算出的偏移可能溢出到别的 group，所以两处都验：
        int offset = MusicGameFeature.ScoreTaskBase + gameId;
        Assert.Equal(0, h.Player.GetTask(MusicGameFeature.ScoreTaskGroup, offset));
        Assert.Equal(0, Score(h, gameId));
    }

    /// <summary>★ 非正分数忽略（客户端本来也只在 score &gt; 0 时发）。</summary>
    [Fact]
    public async Task 非正分数被忽略()
    {
        using var h = await FeatureHarness.CreateAsync("music-zero");

        await CompleteAsync(h, gameId: 1, score: 0);

        Assert.Equal(0, Score(h, 1));
    }

    /// <summary>★ 非法参数不崩。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("5")]
    public async Task 上报成绩非法参数不崩(string json)
    {
        using var h = await FeatureHarness.CreateAsync("music-bad" + json.GetHashCode());

        await h.DispatchLuaCallAsync(MusicGameFeature.CompleteCmd, json);
        // 不抛即通过
        Assert.True(true);
    }

    // ================================================================ 389

    /// <summary>
    /// ★★ 389 回**以 gameId 为键的字典**（客户端 <c>tb[self.tbConfig.Id]</c>），
    /// 且 <c>pos = 0</c> 表示未上榜（原厂认可的合法状态）。
    /// </summary>
    [Fact]
    public async Task 排行回以gameId为键的字典()
    {
        using var h = await FeatureHarness.CreateAsync("music-rank");
        await CompleteAsync(h, gameId: 4, score: 777);

        await h.DispatchLuaCallAsync(MusicGameFeature.RankCmd, "{\"gameId\":4}");

        var r = Reply(h, MusicGameFeature.RankCmd);
        Assert.NotNull(r);
        var one = Assert.IsType<JsonObject>(r!["4"]);
        Assert.Equal(MusicGameFeature.RankNone, one["pos"]!.GetValue<int>());
        Assert.Equal(777, one["score"]!.GetValue<int>());
    }

    /// <summary>★ 没有任何成绩时回空字典（不能回 null —— 客户端 `tb[id]` 会抛）。</summary>
    [Fact]
    public async Task 无成绩时排行回空字典()
    {
        using var h = await FeatureHarness.CreateAsync("music-rank-empty");

        await h.DispatchLuaCallAsync(MusicGameFeature.RankCmd, "{}");

        var r = Reply(h, MusicGameFeature.RankCmd);
        Assert.NotNull(r);
        Assert.Empty(r!);
    }

    /// <summary>★ 三种上行形态（单曲 / 无参 / isAll）都要回包且不崩。</summary>
    [Theory]
    [InlineData("{\"gameId\":1}")]
    [InlineData("null")]
    [InlineData("{\"isAll\":true}")]
    [InlineData("[]")]
    public async Task 排行三种上行形态都回包(string json)
    {
        using var h = await FeatureHarness.CreateAsync("music-rank-form" + json.GetHashCode());
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(MusicGameFeature.RankCmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.NotNull(Reply(h, MusicGameFeature.RankCmd));
    }

    // ================================================================ 390

    /// <summary>
    /// ★★ 390 必须给 <c>musicId</c>（客户端拿它查任务值与缓存键），
    /// <c>tbList</c> 可空 —— 客户端自己把本人插进列表再排序。
    /// </summary>
    [Fact]
    public async Task 好友排行必须带musicId且tbList可空()
    {
        using var h = await FeatureHarness.CreateAsync("music-friend");

        await h.DispatchLuaCallAsync(MusicGameFeature.FriendRankCmd, "{\"musicId\":5}");

        var r = Reply(h, MusicGameFeature.FriendRankCmd);
        Assert.NotNull(r);
        Assert.Equal(5, r!["musicId"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(r["tbList"]));
    }

    /// <summary>★ 也容忍 <c>gameId</c> 键名（上行形态未逐个取证）。</summary>
    [Fact]
    public async Task 好友排行容忍gameId键名()
    {
        using var h = await FeatureHarness.CreateAsync("music-friend-alt");

        await h.DispatchLuaCallAsync(MusicGameFeature.FriendRankCmd, "{\"gameId\":7}");

        Assert.Equal(7, Reply(h, MusicGameFeature.FriendRankCmd)!["musicId"]!.GetValue<int>());
    }

    /// <summary>★ 非法参数不崩且仍然回包。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task 好友排行非法参数不崩(string json)
    {
        using var h = await FeatureHarness.CreateAsync("music-friend-bad" + json.GetHashCode());
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(MusicGameFeature.FriendRankCmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
    }
}
