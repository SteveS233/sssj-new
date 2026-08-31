using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <c>ServerOptions.EnableBots</c>（命令行 <c>--bots</c>）开关的测试。
///
/// <para><b>★ 为什么需要这个开关</b></para>
/// 单机本地服没有第二个真玩家，好友四页签与社团发现列表会全空 ⇒ 无法验收。
/// 所以本服按账号**确定性**造假数据。但**对外分发时必须关掉**：
/// 多个真玩家同服时，「好友列表里有不存在的人、送电力没人回、假社团永远没活动」
/// 是很明显的违和感。
///
/// <para><b>★★ 本文件同时钉住一个已修的 bug</b></para>
/// 两处开关都必须 <b>判 <c>_ctx</c> 是否为 null</b> —— 单测会直接
/// <c>new ClubFeature()</c> / <c>new FriendFeature()</c> 后调生成方法（不走 <c>Install</c>），
/// 此时 <c>_ctx</c> 是 <c>null!</c>（非空断言，编译器不报警但运行时真的是 null）。
/// 初版漏了社团这一处，导致 5 个既有社团测试 NRE —— 由本文件的
/// <see cref="未装配时不抛_社团"/> 守住。
/// </summary>
public class BotsSwitchTests
{
    // =================================================================
    //  一、开关生效
    // =================================================================

    /// <summary>
    /// ★ 开关生效的端到端判据：**好友列表回包里的条目数**。
    /// 开启时有 16 个机器人 ⇒ 列表非空；关闭时只剩真实玩家 ⇒ 空。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 好友列表随开关变化(bool enableBots)
    {
        using var h = await FeatureHarness.CreateAsync(
            $"bots-{enableBots}", enableBots: enableBots);
        // ★ reqfriendtype=2 是「推荐/FXList」页签 —— 机器人就在这一页
        //   （类型语义见 FriendFeature 类注释与 Friend.lua:77..106）
        await h.DispatchLuaCallAsync(FriendFeature.CmdReqList, "{\"reqfriendtype\":2}");

        var json = h.LastOutbound.Json;
        // 开启时回包里必然出现机器人昵称池里的名字之一；关闭时一个都不该有
        bool anyBotName = FriendFeature.BotNames.Any(n => json.Contains(n, StringComparison.Ordinal));
        Assert.Equal(enableBots, anyBotName);
    }

    /// <summary>★ 社团发现列表随开关变化（<c>fx</c> 数组长度）。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 社团发现列表随开关变化(bool enableBots)
    {
        using var h = await FeatureHarness.CreateAsync(
            $"clubs-{enableBots}", enableBots: enableBots);
        await h.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "{}");

        var json = h.LastOutbound.Json;
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var tb = node["tbParam"] ?? node;
        var fx = tb["fx"] as System.Text.Json.Nodes.JsonArray;
        Assert.NotNull(fx);                                  // ★ 键必须在（族 B 的非空表要求）
        Assert.Equal(enableBots ? ClubFeature.DiscoverClubCount : 0, fx!.Count);
    }

    // =================================================================
    //  二、关掉之后不能崩（回包形状仍合法）
    // =================================================================

    /// <summary>
    /// ★★ 关掉机器人后，社团「发现列表」回包**仍须是非空表**。
    ///
    /// 理由：社团族 B 的成功判据是 <c>if next(tbParam)</c>（**非空表即成功**，
    /// <c>docs/05</c> §20.1）—— 若空列表让我们回了 <c>{}</c>，客户端会判成**失败**，
    /// 进而清空 <c>me.clubid</c> 把玩家踢回主城。
    /// <c>BuildFxListPayload</c> 即使收到空池也会回
    /// <c>{applyedlist: [], fx: []}</c>（两个键都在）⇒ 安全。
    /// </summary>
    [Fact]
    public void 关闭后_发现列表回包仍是非空表()
    {
        var payload = ClubFeature.BuildFxListPayload(Array.Empty<ClubFeature.NpcClub>(),
                                                     Array.Empty<ulong>());
        Assert.NotNull(payload["applyedlist"]);
        Assert.NotNull(payload["fx"]);
        Assert.Empty((System.Text.Json.Nodes.JsonArray)payload["fx"]!);
        // ★ 关键：payload 本身非空（有 2 个键）⇒ Lua 侧 next(tbParam) 为真 ⇒ 判成功
        Assert.Equal(2, payload.Count);
    }

    /// <summary>关掉机器人后，好友域的上行命令不能崩（空池要能正常分页）。</summary>
    [Fact]
    public async Task 关闭后_好友列表命令不崩()
    {
        using var h = await FeatureHarness.CreateAsync("bots-off-list", enableBots: false);
        foreach (int type in new[] { 1, 2, 3, 4 })
            await h.DispatchLuaCallAsync(FriendFeature.CmdReqList, $"{{\"reqfriendtype\":{type}}}");
        // 四个页签都不能抛（空池要能正常分页）；回包内容由好友域自己的测试覆盖
    }

    // =================================================================
    //  三、★ 未装配（_ctx == null）时不能抛 —— 钉住已修的 NRE
    // =================================================================

    /// <summary>
    /// ★ 直接 <c>new ClubFeature()</c>（不走 <c>Install</c>）调 <c>DiscoverClubs</c> 不能抛。
    /// 初版漏了 <c>_ctx</c> 判空，导致 5 个既有社团测试全部 NRE。
    /// </summary>
    [Fact]
    public void 未装配时不抛_社团()
    {
        var feature = new ClubFeature();
        var player = new Player("t", 1);
        // 未装配 ⇒ 按默认（开启）处理 ⇒ 应当拿到假社团而不是抛
        Assert.NotEmpty(feature.DiscoverClubs(player));
    }

    /// <summary>同理：<c>new FriendFeature()</c> 调 <c>Bots</c> 不能抛。</summary>
    [Fact]
    public void 未装配时不抛_好友()
    {
        var feature = new FriendFeature();
        Assert.NotNull(feature.Bots(1));
    }
}
