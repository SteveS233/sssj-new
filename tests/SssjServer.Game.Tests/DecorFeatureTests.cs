using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 个性化装扮域（LuaCall 70 头像框 / 91 名片 / 92 标识 / 93 气泡 / 95 主题 / 96 背景）。
/// 台账 `docs/05` §41。
///
/// 断言重点（每条都对应一个「写错了会静默失效」的坑，见 <see cref="DecorFeature"/> 类注释）：
/// <list type="number">
///   <item>★ 上行是**裸标量**（<c>91</c> 收到的是 <c>5</c> 而不是 <c>{id:5}</c>）—— 按对象解析会恒拒绝；</item>
///   <item>★ 70 的成功回包必须是**裸数组** <c>[id]</c>（客户端 <c>#tbParam &gt; 0</c> 才刷新）；</item>
///   <item>★ 成功回包**绝不能带 <c>nError</c>**（Lua 真值判断，带 0 也弹错误框）；</item>
///   <item>★ 任务值存的是**获得时间戳**，<c>Type==2</c> 的限时装扮要按 <c>stamp+limit &gt; now</c> 判到期；</item>
///   <item>★ 95/96 要**先推 pf 1026 再回包**（客户端 handler 不收参数、重读任务值）；</item>
///   <item>★ 91/92/93 存的地方必须与已实现的 cmd 94 读的地方一致（否则改了读不出来）。</item>
/// </list>
/// </summary>
public class DecorFeatureTests
{
    private static Task HeadFrameAsync(FeatureHarness h, int id)
        => h.DispatchLuaCallAsync(DecorFeature.SetHeadFrameCmd, id.ToString());

    private static Task VisitingCardAsync(FeatureHarness h, int id)
        => h.DispatchLuaCallAsync(DecorFeature.SetVisitingCardCmd, id.ToString());

    private static Task ListSkinAsync(FeatureHarness h, int id)
        => h.DispatchLuaCallAsync(DecorFeature.SetPlayerListSkinCmd, id.ToString());

    private static Task BubbleAsync(FeatureHarness h, int id)
        => h.DispatchLuaCallAsync(DecorFeature.SetChatBubbleCmd, id.ToString());

    private static Task DecorateAsync(FeatureHarness h, int themeId, int decorateId)
        => h.DispatchLuaCallAsync(DecorFeature.ChangeDecorateCmd,
            $"[{{\"type\":1,\"id\":{themeId}}},{{\"type\":2,\"id\":{decorateId}}}]");

    private static Task BackgroundAsync(FeatureHarness h, int id)
        => h.DispatchLuaCallAsync(DecorFeature.ChangeBackgroundCmd, $"{{\"id\":{id}}}");

    /// <summary>
    /// 取最后一条下行的 <c>tbParam</c>（本域回包有对象/数组两种形状，所以返回 JsonNode）。
    /// ★ <c>LuaCallRouter.CallClient</c> 会把 payload 包成
    /// <c>{"sCmd":N,"tbParam":…}</c>（<c>LuaCallRouter.cs:247</c> 的 <c>BuildFullJson</c>）
    /// ⇒ 断言必须剥掉这层外壳，直接解析整包会永远看不到业务字段。
    /// </summary>
    private static JsonNode LastReply(FeatureHarness h)
    {
        var (_, json) = h.LastOutbound;
        var root = JsonNode.Parse(json)!;
        return root["tbParam"]!;
    }

    /// <summary>把某个装扮标记为「刚刚获得」（任务值 = 现在的时间戳）。</summary>
    private static void Give(FeatureHarness h, DecorKind kind, int id)
    {
        int group = kind switch
        {
            DecorKind.HeadFrame => DecorTable.HeadFrameGroup,
            DecorKind.Skin => DecorTable.SkinGroup,
            DecorKind.Board => DecorTable.BoardGroup,
            DecorKind.Babble => DecorTable.BabbleGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        h.Player.SetTask(group, id, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>从真实配置表里挑一个指定 Type 的装扮 id（避免把测试写死在某个 id 上）。</summary>
    private static int PickId(DecorTable table, DecorKind kind, int type)
    {
        var (rows, _) = table.For(kind);
        return rows.Values.Where(r => r.Type == type).Select(r => r.Id).FirstOrDefault();
    }

    private static DecorTable LoadTable() => DecorTable.Load(TestPaths.SettingDir);

    // ================================================================ 配置表探针

    /// <summary>
    /// ★ 四张表都能载入，且行数与真实文件对得上。
    /// 这是「表头行数猜错 ⇒ 整表列找不到 ⇒ 所有查询静默返回 0」那类错误的回归闸
    /// （AGENTS.md §5）。数值随配置更新可能变化 ⇒ 只断言「有内容」+ 数量级。
    /// </summary>
    [Fact]
    public void 四张装扮表都能载入且非空()
    {
        var table = LoadTable();

        Assert.NotEmpty(table.Heads);
        Assert.NotEmpty(table.Skins);
        Assert.NotEmpty(table.Boards);
        Assert.NotEmpty(table.Babbles);

        // 头像框是四张里最大的一张（真机 121 条），远多于其它三张
        Assert.True(table.Heads.Count > table.Skins.Count);
        Assert.True(table.Heads.Count > 50, $"头像框只有 {table.Heads.Count} 条，疑似表头行数解错");
    }

    /// <summary>
    /// ★ 每张表都应同时存在 <c>Type==1</c>（永久）与 <c>Type==2</c>（限时）之外的合法值域，
    /// 且 <c>Type</c> 不能整列为 0 —— 整列 0 就说明列名没对上（静默失效的典型症状）。
    /// </summary>
    [Fact]
    public void 装扮表的Type列没有整列为空()
    {
        var table = LoadTable();
        foreach (var kind in new[] { DecorKind.HeadFrame, DecorKind.Skin, DecorKind.Board, DecorKind.Babble })
        {
            var (rows, _) = table.For(kind);
            Assert.Contains(rows.Values, r => r.Type != 0);
        }
    }

    /// <summary>
    /// ★★★ 限时装扮的到期判定（<c>UI_SkinSelected.lua:341</c> 的
    /// <c>(nGetTime + btime) &gt; GetTime()</c>）。
    /// 这条是本域最容易写错的语义：任务值存「获得时间」不是「有没有」。
    /// </summary>
    [Fact]
    public void 限时装扮按获得时间加有效期判到期()
    {
        var table = LoadTable();
        int limited = PickId(table, DecorKind.Skin, type: 2);
        if (limited == 0) return;   // 现网若无限时名片则跳过（不硬绑具体配置）

        var row = table.Skins[limited];
        long now = 1_700_000_000;

        // 刚获得 ⇒ 拥有
        Assert.True(table.IsOwned(DecorKind.Skin, limited, (int)now, now));

        // 获得于「有效期之前」⇒ 已过期
        int expired = (int)(now - row.LimitTime - 1);
        Assert.False(table.IsOwned(DecorKind.Skin, limited, expired, now));

        // ★ 任务值 0（= 从未获得）绝不能算拥有
        Assert.False(table.IsOwned(DecorKind.Skin, limited, 0, now));
    }

    /// <summary>★ 永久装扮（<c>Type==1</c>）只看「任务值非 0」，不算到期。</summary>
    [Fact]
    public void 永久装扮只看任务值非零()
    {
        var table = LoadTable();
        int perm = PickId(table, DecorKind.HeadFrame, type: 1);
        if (perm == 0) return;

        long now = 1_700_000_000;
        Assert.False(table.IsOwned(DecorKind.HeadFrame, perm, 0, now));
        // 即便获得时间非常久远，永久装扮依然拥有
        Assert.True(table.IsOwned(DecorKind.HeadFrame, perm, 1, now));
    }

    /// <summary>★ id 0 一律视为拥有（= 脱下，客户端 <c>IsUnlock</c> 首句同款判断）。</summary>
    [Fact]
    public void id0一律视为拥有()
    {
        var table = LoadTable();
        Assert.True(table.IsOwned(DecorKind.Skin, 0, 0, 1_700_000_000));
    }

    // ================================================================ 上行解析（裸标量）

    /// <summary>
    /// ★★★ 本域第一陷阱：上行是**裸标量**。
    /// <c>UI_SkinSelected.lua:164</c> <c>CallServer(Player_SetVisitingCard, self.selectSkinId)</c>
    /// —— 实参就是个数字。同时容忍 <c>{id:N}</c> 与单元素数组（防御性）。
    /// </summary>
    [Theory]
    [InlineData("7", 7)]
    [InlineData("{\"id\":7}", 7)]
    [InlineData("[7]", 7)]
    [InlineData("0", 0)]
    [InlineData("\"7\"", 7)]
    [InlineData("-1", -1)]
    [InlineData("null", -1)]
    [InlineData("{}", -1)]
    public void 裸标量上行能被正确解析(string json, int expected)
        => Assert.Equal(expected, DecorFeature.ReadScalarId(JsonNode.Parse(json)));

    // ================================================================ 70 头像框

    /// <summary>
    /// ★ 70 成功回**裸数组** <c>[id]</c>（<c>UI_User.lua:1265</c> 读 <c>#tbParam</c> 与 <c>tbParam[1]</c>）。
    /// 回对象会让那个分支整段跳过 ⇒ 界面静默不刷新。
    ///
    /// ★ 靶子排除默认框 id 1：用 id 1 会同时走「默认框免校验」的旁路，
    /// 这条用例就验不到「拥有校验通过」那条正路了。
    /// </summary>
    [Fact]
    public async Task 头像框成功回裸数组且落存档()
    {
        var h = await FeatureHarness.CreateAsync(nameof(头像框成功回裸数组且落存档));
        var table = LoadTable();
        int id = table.Heads.Values
            .Where(r => r.Type == 1 && r.Id != DecorTable.DefaultHeadFrame)
            .Select(r => r.Id)
            .FirstOrDefault();
        Assert.True(id > 0, "配置表里没有「非默认的永久头像框」，用例前提不成立");
        Give(h, DecorKind.HeadFrame, id);

        await HeadFrameAsync(h, id);

        var reply = Assert.IsType<JsonArray>(LastReply(h));
        Assert.Single(reply);
        Assert.Equal(id, (int)reply[0]!);
        Assert.Equal(id, h.Player.HeadFrame);
    }

    /// <summary>
    /// ★ 未获得的头像框要拒绝，且**回包带 <c>nError</c>**（让遮罩/界面有反馈）。
    ///
    /// ★★ 靶子必须排除 <see cref="DecorTable.DefaultHeadFrame"/>（id 1）——
    /// 那个 id 是「无装扮」的默认值、**设计上就无需拥有**（见
    /// <see cref="默认头像框1无需拥有即可设置"/>）。第一版用例直接取
    /// <c>PickId(type:1)</c> 拿到的正是 id 1 ⇒ 用例红了但实现是对的。
    /// </summary>
    [Fact]
    public async Task 未获得的头像框被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(未获得的头像框被拒绝));
        var table = LoadTable();
        int id = table.Heads.Values
            .Where(r => r.Type == 1 && r.Id != DecorTable.DefaultHeadFrame)
            .Select(r => r.Id)
            .FirstOrDefault();
        Assert.True(id > 0, "配置表里没有「非默认的永久头像框」，用例前提不成立");
        // 故意不 Give

        await HeadFrameAsync(h, id);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.True(reply.ContainsKey("nError"));
        Assert.NotEqual(id, h.Player.HeadFrame);
    }

    /// <summary>
    /// ★ 默认头像框 1 无需拥有即可设置。取证 <c>CardCommon.lua:1020</c>：
    /// 客户端发现当前头像框过期时**自己发 <c>Player_SetHeadFrame, 1</c>** ——
    /// 若服务端拒绝这条，玩家的头像框会永久卡在过期状态。
    /// </summary>
    [Fact]
    public async Task 默认头像框1无需拥有即可设置()
    {
        var h = await FeatureHarness.CreateAsync(nameof(默认头像框1无需拥有即可设置));

        await HeadFrameAsync(h, DecorTable.DefaultHeadFrame);

        var reply = Assert.IsType<JsonArray>(LastReply(h));
        Assert.Equal(DecorTable.DefaultHeadFrame, (int)reply[0]!);
        Assert.Equal(DecorTable.DefaultHeadFrame, h.Player.HeadFrame);
    }

    /// <summary>★ 配置表里不存在的 id 必须拒绝（客户端查不到配置会反手发 Set…(0) 脱下来）。</summary>
    [Fact]
    public async Task 配置表里没有的头像框被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(配置表里没有的头像框被拒绝));
        h.Player.SetTask(DecorTable.HeadFrameGroup, 999_999,
            (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await HeadFrameAsync(h, 999_999);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.True(reply.ContainsKey("nError"));
    }

    // ================================================================ 91/92/93 名片三件套

    /// <summary>
    /// ★★ 91/92/93 存的位置必须与已实现的 cmd 94 读的位置一致 ——
    /// 这条是「改了但读不出来」的回归闸。断言方式：设置后用 94 的读取函数验。
    /// </summary>
    [Fact]
    public async Task 名片三件套分别落到正确字段且与94口径一致()
    {
        var h = await FeatureHarness.CreateAsync(nameof(名片三件套分别落到正确字段且与94口径一致));
        var table = LoadTable();

        int skin = PickId(table, DecorKind.Skin, type: 1);
        int board = PickId(table, DecorKind.Board, type: 1);
        int babble = PickId(table, DecorKind.Babble, type: 1);
        Assert.True(skin > 0 && board > 0 && babble > 0, "三张表都要有永久款，用例前提不成立");

        Give(h, DecorKind.Skin, skin);
        Give(h, DecorKind.Board, board);
        Give(h, DecorKind.Babble, babble);

        await VisitingCardAsync(h, skin);
        await ListSkinAsync(h, board);
        await BubbleAsync(h, babble);

        // 用 94 的读取口径复核（而不是直接读 Extra，确保两边一致）
        var state = MainCityFeature.ReadCardState(h.Player);
        Assert.Equal(skin, state.VisitingCardId);
        Assert.Equal(board, state.PlayerListSkinId);
        Assert.Equal(babble, state.ChatBubbleId);
    }

    /// <summary>
    /// ★★ 成功回包**绝不能带 <c>nError</c>** —— Lua 里 <c>0</c> 也是真值，
    /// 带 <c>nError=0</c> 会让客户端弹 <c>ui.Error.0</c> 错误框（本项目七种约定之一）。
    /// </summary>
    [Fact]
    public async Task 名片成功回包不带nError且含三件套字段()
    {
        var h = await FeatureHarness.CreateAsync(nameof(名片成功回包不带nError且含三件套字段));
        var table = LoadTable();
        int skin = PickId(table, DecorKind.Skin, type: 1);
        Assert.True(skin > 0);
        Give(h, DecorKind.Skin, skin);

        await VisitingCardAsync(h, skin);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
        // UI_User.lua:1306 要这两个字段同时存在
        Assert.True(reply.ContainsKey("ChatBubbleID"));
        Assert.True(reply.ContainsKey("PlayerListSkinID"));
        Assert.True(reply.ContainsKey("VisitingCardID"));
    }

    /// <summary>
    /// ★ 脱下（id=0）必须放行 —— 客户端自查过期后就是发 0
    /// （<c>CardCommon.lua:1054/1060/1066</c>），拒绝会让玩家永久卡在过期装扮上。
    /// </summary>
    [Fact]
    public async Task 名片脱下id0一律放行()
    {
        var h = await FeatureHarness.CreateAsync(nameof(名片脱下id0一律放行));
        var table = LoadTable();
        int skin = PickId(table, DecorKind.Skin, type: 1);
        Assert.True(skin > 0);
        Give(h, DecorKind.Skin, skin);
        await VisitingCardAsync(h, skin);
        Assert.Equal(skin, MainCityFeature.ReadCardState(h.Player).VisitingCardId);

        await VisitingCardAsync(h, 0);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
        Assert.Equal(0, MainCityFeature.ReadCardState(h.Player).VisitingCardId);
    }

    /// <summary>★ 未获得的名片要拒绝，且不能改动存档。</summary>
    [Fact]
    public async Task 未获得的名片被拒绝且不改存档()
    {
        var h = await FeatureHarness.CreateAsync(nameof(未获得的名片被拒绝且不改存档));
        var table = LoadTable();
        int skin = PickId(table, DecorKind.Skin, type: 1);
        Assert.True(skin > 0);

        await VisitingCardAsync(h, skin);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.True(reply.ContainsKey("nError"));
        Assert.Equal(0, MainCityFeature.ReadCardState(h.Player).VisitingCardId);
    }

    /// <summary>★ 三条互不干扰：改气泡不能把名片/标识清掉。</summary>
    [Fact]
    public async Task 改气泡不影响名片与标识()
    {
        var h = await FeatureHarness.CreateAsync(nameof(改气泡不影响名片与标识));
        var table = LoadTable();
        int skin = PickId(table, DecorKind.Skin, type: 1);
        int babble = PickId(table, DecorKind.Babble, type: 1);
        Assert.True(skin > 0 && babble > 0);
        Give(h, DecorKind.Skin, skin);
        Give(h, DecorKind.Babble, babble);

        await VisitingCardAsync(h, skin);
        await BubbleAsync(h, babble);

        var state = MainCityFeature.ReadCardState(h.Player);
        Assert.Equal(skin, state.VisitingCardId);
        Assert.Equal(babble, state.ChatBubbleId);
    }

    // ================================================================ 95 主题 + 装饰

    /// <summary>
    /// ★★★ 95 必须**先推 pf 1026 再回包**：客户端 <c>MainOrnament:UpdateOrnament()</c>
    /// 不收参数（<c>LuaCall.lua:1239</c>），转头读任务值（<c>MainOrnament.lua:112</c>）
    /// ⇒ 不推 1026 就是「数据对了但界面不动」。
    /// </summary>
    [Fact]
    public async Task 换主题写任务值并推1026()
    {
        var h = await FeatureHarness.CreateAsync(nameof(换主题写任务值并推1026));
        const int theme = 2;
        // 先让玩家拥有该主题（获得时间 > 0）
        h.Player.SetTask(DecorFeature.OrnamentGroup, theme,
            (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await DecorateAsync(h, theme, decorateId: theme);

        Assert.Equal(theme, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentTheme));
        // type=2 的 id > 0 ⇒ 装饰开关置 1
        Assert.Equal(1, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentShowDecorate));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
    }

    /// <summary>
    /// ★ <c>type=2</c> 的 <c>id</c> 是**开关语义**不是主题 id：
    /// 客户端「要显示填主题 id、隐藏填 0」（<c>UI_MainThemeTip.lua:202..205</c>）。
    /// </summary>
    [Fact]
    public async Task 装饰开关按id是否大于0存布尔()
    {
        var h = await FeatureHarness.CreateAsync(nameof(装饰开关按id是否大于0存布尔));
        const int theme = 3;
        h.Player.SetTask(DecorFeature.OrnamentGroup, theme,
            (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await DecorateAsync(h, theme, decorateId: 0);

        Assert.Equal(theme, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentTheme));
        Assert.Equal(0, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentShowDecorate));
    }

    /// <summary>★ 未拥有的主题要拒绝。</summary>
    [Fact]
    public async Task 未拥有的主题被拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(未拥有的主题被拒绝));

        await DecorateAsync(h, themeId: 4242, decorateId: 0);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.True(reply.ContainsKey("nError"));
        Assert.Equal(0, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentTheme));
    }

    /// <summary>
    /// ★ 主题 id 0（= 恢复默认）无需拥有。客户端把 0 当 1 处理
    /// （<c>MainOrnament.lua:113</c> <c>if id == 0 then id = 1 end</c>）。
    /// </summary>
    [Fact]
    public async Task 主题id0恢复默认无需拥有()
    {
        var h = await FeatureHarness.CreateAsync(nameof(主题id0恢复默认无需拥有));

        await DecorateAsync(h, themeId: 0, decorateId: 0);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
    }

    /// <summary>★ 永久拥有标记（<c>id + 20000</c>）也算拥有（<c>MainOrnament.lua:121</c>）。</summary>
    [Fact]
    public async Task 永久拥有标记也算拥有主题()
    {
        var h = await FeatureHarness.CreateAsync(nameof(永久拥有标记也算拥有主题));
        const int theme = 5;
        h.Player.SetTask(DecorFeature.OrnamentGroup, theme + DecorFeature.OrnamentPermanentBase, 1);

        await DecorateAsync(h, theme, decorateId: 0);

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
        Assert.Equal(theme, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentTheme));
    }

    // ================================================================ 96 背景

    /// <summary>★ 96 存偏移 2000（<c>MainOrnament.lua:175</c>）并推 1026。</summary>
    [Fact]
    public async Task 换背景写偏移2000并推1026()
    {
        var h = await FeatureHarness.CreateAsync(nameof(换背景写偏移2000并推1026));

        await BackgroundAsync(h, 3);

        Assert.Equal(3, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentBg));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
    }

    /// <summary>
    /// ★ 背景 0 = 关闭（<c>UI_MainThemeTip.lua:216</c> / <c>UI_SpineShow.lua:53</c> 都会发 0）。
    /// 必须放行，否则玩家关不掉背景。
    /// </summary>
    [Fact]
    public async Task 背景0关闭必须放行()
    {
        var h = await FeatureHarness.CreateAsync(nameof(背景0关闭必须放行));
        await BackgroundAsync(h, 5);
        Assert.Equal(5, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentBg));

        await BackgroundAsync(h, 0);

        Assert.Equal(0, h.Player.GetTask(DecorFeature.OrnamentGroup, DecorFeature.OrnamentCurrentBg));
        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.False(reply.ContainsKey("nError"));
    }

    /// <summary>★ 非法参数要有回包（不能静默丢弃，否则界面可能卡住）。</summary>
    [Fact]
    public async Task 非法参数也要回包()
    {
        var h = await FeatureHarness.CreateAsync(nameof(非法参数也要回包));

        await h.DispatchLuaCallAsync(DecorFeature.ChangeBackgroundCmd, "\"abc\"");

        var reply = Assert.IsType<JsonObject>(LastReply(h));
        Assert.True(reply.ContainsKey("nError"));
    }

    /// <summary>★ 六条命令全部有回包（本域没有「故意不回包」的命令）。</summary>
    [Fact]
    public async Task 六条命令都会回包()
    {
        var h = await FeatureHarness.CreateAsync(nameof(六条命令都会回包));

        int before = h.Outbound.Count;
        await HeadFrameAsync(h, DecorTable.DefaultHeadFrame);
        await VisitingCardAsync(h, 0);
        await ListSkinAsync(h, 0);
        await BubbleAsync(h, 0);
        await DecorateAsync(h, 0, 0);
        await BackgroundAsync(h, 0);

        Assert.Equal(before + 6, h.Outbound.Count);
    }
}
