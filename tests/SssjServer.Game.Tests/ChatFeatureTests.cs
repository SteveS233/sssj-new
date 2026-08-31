using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 聊天域（pf 1010/1011/1012 + LuaCall 140/141）。完整取证见 `work/chat-domain-forensics.md`。
///
/// 断言重点是四条「漏了就出事」的硬约束：
/// ① pf 1012 的 **field 1..8 是 required**（<c>IsInitialized</c> 掩码 <c>0xFF</c>，RVA `0x337CB6C`）——
///    少一个整包被客户端丢弃并打 <c>Failed to process message. PF : 1012</c>；
/// ② 140/141 的回包是**裸数组** <c>[频道号]</c>，且**绝不能带 <c>nError</c>**
///    （LuaCall 数字族真值判断，<c>UI_Chat.lua:1539</c> 还要求 <c>#tbParam &gt; 0</c>）；
/// ③ 上行 tbParam 是 <c>JsonArray</c>（140）或 <c>null</c>（141）——
///    <c>JsonNode["key"]</c> 对 <c>JsonArray</c> 会抛，这条在真机上崩过；
/// ④ <c>headframe</c> 是组合值且头像框 id 必须命中 <c>HeadFrame.txt</c>，
///    否则 <c>UI_Chat.lua:667</c> 无 nil 保护直接抛。
/// </summary>
public class ChatFeatureTests
{
    // ---------------------------------------------------------------- 小工具

    private static Packet ChatPacket(uint type, string message,
                                     ulong targetId = 0, string targetName = "", uint serial = 9)
        => new()
        {
            Pf = GamePf.Chatting,
            Serial = serial,
            Payload = new ChattingReq
            {
                Type = type,
                TargetId = targetId,
                TargetName = targetName,
                Message = message,
            }.ToByteArray(),
        };

    /// <summary>取最后一条 pf 1024 下行，断言它是 LuaCall 族并返回 <c>{sCmd, tbParam}</c>。</summary>
    private static JsonObject LuaReply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(LuaCallRouter.LuaCallFunction, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    private static ChatFeature Feature()
        => new();

    // ================================================================
    //  ① pf 1012 的 required 字段
    // ================================================================

    /// <summary>
    /// ★ field 1..8 必须全部**显式赋值**（ProtoGen 的 setter 才会置 has 位）。
    /// 非私聊场景的 <c>receiver_id</c>/<c>receiver_name</c> 最容易漏 —— 漏了整包被丢。
    /// </summary>
    [Fact]
    public async Task 玩家消息_八个required字段全部置位()
    {
        using var h = await FeatureHarness.CreateAsync("chat-required");
        h.Player.Name = "店长";
        h.Player.Level = 42;

        var feature = Feature();
        var ntf = feature.BuildNotice(h.Player, ChatFeature.ChannelWorld, "hello", 0, "");

        Assert.True(ntf.HasType, "field 1 type 未置位");
        Assert.True(ntf.HasSenderId, "field 2 sender_id 未置位");
        Assert.True(ntf.HasReceiverId, "field 3 receiver_id 未置位（非私聊也必须显式写 0）");
        Assert.True(ntf.HasSenderName, "field 4 sender_name 未置位");
        Assert.True(ntf.HasReceiverName, "field 5 receiver_name 未置位（非私聊也必须显式写 \"\"）");
        Assert.True(ntf.HasMessage, "field 6 message 未置位");
        Assert.True(ntf.HasTime, "field 7 time 未置位");
        Assert.True(ntf.HasSenderLevel, "field 8 sender_level 未置位");

        Assert.Equal(ChatFeature.ChannelWorld, ntf.Type);
        Assert.Equal("店长", ntf.SenderName);
        Assert.Equal("hello", ntf.Message);
        Assert.Equal(42u, ntf.SenderLevel);
        Assert.True(ntf.Time > 0);
    }

    /// <summary>系统频道公告同样要求 field 1..8 齐全（哪怕 ShowSystem 只用 info + 时间）。</summary>
    [Fact]
    public void 系统公告_八个required字段全部置位_且口径与原服一致()
    {
        var ntf = ChatFeature.BuildSystemNotice("维护公告");

        Assert.True(ntf.HasType && ntf.HasSenderId && ntf.HasReceiverId && ntf.HasSenderName
                    && ntf.HasReceiverName && ntf.HasMessage && ntf.HasTime && ntf.HasSenderLevel);
        Assert.Equal(ChatFeature.ChannelSystem, ntf.Type);
        // BannerLogic.lua:615 的原服口径：AddNewChatMessage(2, 1, "system", ...)
        Assert.Equal(1u, ntf.SenderId);
        Assert.Equal("system", ntf.SenderName);
        Assert.Equal("维护公告", ntf.Message);
    }

    /// <summary>空名字不能变成 null（<c>sender_name</c> 是 required，且客户端直接渲染）。</summary>
    [Fact]
    public async Task 玩家消息_空名字有可见兜底()
    {
        using var h = await FeatureHarness.CreateAsync("chat-noname");
        h.Player.Name = "";

        var ntf = Feature().BuildNotice(h.Player, ChatFeature.ChannelWorld, "hi", 0, "");

        Assert.False(string.IsNullOrEmpty(ntf.SenderName));
    }

    // ================================================================
    //  pf 1010 主链路
    // ================================================================

    /// <summary>世界频道消息：必须回 pf 1011 应答，并把消息回显成 pf 1012（客户端不做本地回显）。</summary>
    [Fact]
    public async Task 世界频道上行_回1011并回显1012()
    {
        using var h = await FeatureHarness.CreateAsync("chat-world");
        h.Player.Name = "店长";

        await h.Pf.DispatchAsync(h.Session, ChatPacket(ChatFeature.ChannelWorld, "早上好"));

        Assert.True(await h.WaitForPfAsync(GamePf.ChattingRsp), "没有回 pf 1011（客户端 serial 记账会永久悬挂）");
        Assert.True(await h.WaitForPfAsync(GamePf.NoticeChattingMsg), "没有推 pf 1012（界面上不会出现这条消息）");
    }

    /// <summary>空正文要被拒（客户端 `0x1856C8C` 也会拦，服务端兜底），且不能推 1012。</summary>
    [Fact]
    public async Task 空正文_被拒()
    {
        using var h = await FeatureHarness.CreateAsync("chat-empty");

        Assert.NotEqual("", Feature().Validate(h.Player, ""));
    }

    /// <summary>超长正文要被拒（GM 通道能绕过客户端的长度限制）。</summary>
    [Fact]
    public async Task 超长正文_被拒()
    {
        using var h = await FeatureHarness.CreateAsync("chat-long");

        string tooLong = new('a', ChatFeature.MaxMessageLength + 1);
        Assert.NotEqual("", Feature().Validate(h.Player, tooLong));
        Assert.Equal("", Feature().Validate(h.Player, new string('a', ChatFeature.MaxMessageLength)));
    }

    /// <summary>
    /// ★ 陷阱 T5：正文含链接键但前 3 个字符不是数字 ⇒ 客户端
    /// <c>Game.Utils.GetByteArrayFromChatMsg</c>（RVA `0x15413D0`）的 <c>Int32.Parse</c> 会抛，
    /// 整条消息不显示。服务端必须在转发前拦掉。
    /// </summary>
    [Theory]
    [InlineData("hello world", true)]                 // 无链接键
    [InlineData("CardInfo=1", true)]                  // idx < 3，客户端自己安全 return
    [InlineData("abcCardInfo=1", false)]              // ★ 前 3 字 "abc" → Int32.Parse 抛
    [InlineData("012CardInfo=1", true)]               // 合法 3 位长度前缀
    [InlineData("xxPvpInfo=zz", true)]                // idx = 2 < 3
    [InlineData("xyzPvpInfo=zz", false)]
    public void 链接前缀校验(string message, bool safe)
        => Assert.Equal(safe, ChatFeature.IsLinkPrefixSafe(message));

    /// <summary>发言 CD：同一秒内连发第二条要被拒（客户端的 5 秒过滤绕得过，服务端自己限）。</summary>
    [Fact]
    public async Task 发言CD_生效()
    {
        using var h = await FeatureHarness.CreateAsync("chat-cd");

        await h.Pf.DispatchAsync(h.Session, ChatPacket(ChatFeature.ChannelWorld, "第一条"));
        // 第一条已 stamp 时间；第二条应被 CD 拒
        Assert.NotEqual("", Feature().Validate(h.Player, "第二条"));
    }

    /// <summary>未登录的会话发聊天不能抛（Tag 非 Player）。</summary>
    [Fact]
    public async Task 未登录会话_不抛()
    {
        using var h = await FeatureHarness.CreateAsync("chat-anon", attachPlayer: false);

        await h.Pf.DispatchAsync(h.Session, ChatPacket(ChatFeature.ChannelWorld, "hi"));
    }

    /// <summary>畸形 payload 不能抛（真机上任何字节都可能到）。</summary>
    [Fact]
    public async Task 畸形payload_不抛()
    {
        using var h = await FeatureHarness.CreateAsync("chat-garbage");

        await h.Pf.DispatchAsync(h.Session, new Packet
        {
            Pf = GamePf.Chatting,
            Serial = 3,
            Payload = new byte[] { 0xFF, 0xFF, 0xFF, 0x07 },
        });
    }

    // ================================================================
    //  ② / ③ LuaCall 140 / 141
    // ================================================================

    /// <summary>★ 回包必须是裸数组 <c>[频道号]</c>，不能是对象，也不能带 nError。</summary>
    [Fact]
    public async Task 切频道_回裸数组且不带nError()
    {
        using var h = await FeatureHarness.CreateAsync("chat-ch140");

        await h.DispatchLuaCallAsync(ChatFeature.ChangeChannelCmd, "[7]");

        var reply = LuaReply(h);
        Assert.Equal(ChatFeature.ChangeChannelCmd, reply["sCmd"]!.GetValue<int>());
        var tb = Assert.IsType<JsonArray>(reply["tbParam"]);
        Assert.True(tb.Count > 0, "UI_Chat.lua:1539 要求 #tbParam > 0，空数组会被静默忽略");
        Assert.Equal(7, tb[0]!.GetValue<int>());
        Assert.Equal(7u, h.Player.ChatChannelId);
    }

    /// <summary>频道号越界要被钳到 1..999（本服永不回失败，见 ChatFeature 类注释 ⑥）。</summary>
    [Theory]
    [InlineData("[0]", 1u)]
    [InlineData("[1000]", 999u)]
    [InlineData("[999]", 999u)]
    [InlineData("[1]", 1u)]
    public async Task 切频道_越界被钳(string tbParam, uint expected)
    {
        using var h = await FeatureHarness.CreateAsync($"chat-clamp-{tbParam.Length}-{expected}");

        await h.DispatchLuaCallAsync(ChatFeature.ChangeChannelCmd, tbParam);

        Assert.Equal(expected, h.Player.ChatChannelId);
        var tb = Assert.IsType<JsonArray>(LuaReply(h)["tbParam"]);
        Assert.Equal((int)expected, tb[0]!.GetValue<int>());
    }

    /// <summary>
    /// ★ 上行形状异常（对象 / null / 空数组 / 非数字）都不能抛，且要保持原频道。
    /// 这条对应 AGENTS.md 记录过的真机崩溃：<c>JsonNode["key"]</c> 对 <c>JsonArray</c> 会抛。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[\"abc\"]")]
    public async Task 切频道_畸形上行不抛且保持原频道(string tbParam)
    {
        using var h = await FeatureHarness.CreateAsync($"chat-bad-{tbParam.Length}-{tbParam.GetHashCode()}");
        h.Player.ChatChannelId = 5;

        await h.DispatchLuaCallAsync(ChatFeature.ChangeChannelCmd, tbParam);

        Assert.Equal(5u, h.Player.ChatChannelId);
        var tb = Assert.IsType<JsonArray>(LuaReply(h)["tbParam"]);
        Assert.Equal(5, tb[0]!.GetValue<int>());
    }

    /// <summary>字符串数字要能吃下（Lua 侧可能给 "7"）。</summary>
    [Fact]
    public async Task 切频道_字符串数字可解析()
    {
        using var h = await FeatureHarness.CreateAsync("chat-strnum");

        await h.DispatchLuaCallAsync(ChatFeature.ChangeChannelCmd, "[\"7\"]");

        Assert.Equal(7u, h.Player.ChatChannelId);
    }

    /// <summary>★ 141 的上行**没有 tbParam 键**（param == null），随机由服务端决定。</summary>
    [Fact]
    public async Task 随机切频道_无参数也能切且回同一个sCmd()
    {
        using var h = await FeatureHarness.CreateAsync("chat-ch141");
        h.Player.ChatChannelId = 3;

        // 模拟 141 的真实上行：sJsonParam 里根本没有 tbParam 键
        await h.LuaCalls.DispatchAsync(h.Session, LuaCallRouter.LuaCallFunction,
                                       $"{{\"sCmd\":{ChatFeature.RandomChangeChannelCmd}}}");

        var reply = LuaReply(h);
        Assert.Equal(ChatFeature.RandomChangeChannelCmd, reply["sCmd"]!.GetValue<int>());
        var tb = Assert.IsType<JsonArray>(reply["tbParam"]);
        uint got = tb[0]!.GetValue<uint>();
        Assert.InRange(got, ChatFeature.MinChannel, ChatFeature.MaxChannel);
        Assert.NotEqual(3u, got);
        Assert.Equal(got, h.Player.ChatChannelId);
    }

    /// <summary>随机切频道不会切到当前频道（否则「切了」这个语义不成立）。</summary>
    [Fact]
    public void 随机频道_不等于当前()
    {
        var feature = Feature();
        for (uint cur = 1; cur <= 20; cur++)
            Assert.NotEqual(cur, feature.RandomChannel(cur));
    }

    /// <summary>切频道后要落盘（界面关着时客户端会丢弃回包，只能靠下次 pf 1005 同步）。</summary>
    [Fact]
    public async Task 切频道_落盘()
    {
        using var h = await FeatureHarness.CreateAsync("chat-persist");

        await h.DispatchLuaCallAsync(ChatFeature.ChangeChannelCmd, "[123]");

        var reloaded = h.Module.World.GetOrCreatePlayer("chat-persist");
        Assert.Equal(123u, reloaded.ChatChannelId);
    }

    /// <summary>切到没有历史的频道要补一条系统提示（否则界面显示「暂无消息」）。</summary>
    [Fact]
    public async Task 切到空频道_补推系统提示()
    {
        using var h = await FeatureHarness.CreateAsync("chat-empty-bucket");

        await h.DispatchLuaCallAsync(ChatFeature.ChangeChannelCmd, "[321]");

        Assert.True(await h.WaitForPfAsync(GamePf.NoticeChattingMsg),
                    "切到空频道没有补推 pf 1012，界面会是空的");
    }

    // ================================================================
    //  登录钩子
    // ================================================================

    /// <summary>
    /// ★ 新号的 <c>ChatChannelId</c> 默认 0，而客户端合法区间是 1..999
    /// （<c>UI_Chat.lua:1525</c>）⇒ 登录准备阶段必须分配一个合法值。
    /// </summary>
    [Fact]
    public async Task 登录准备_把0号频道修成1()
    {
        using var h = await FeatureHarness.CreateAsync("chat-ensure");
        h.Player.ChatChannelId = 0;

        Feature().EnsureChannel(h.Session, h.Player);

        Assert.Equal(ChatFeature.MinChannel, h.Player.ChatChannelId);
    }

    /// <summary>已经合法的频道号不能被覆盖。</summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(500u)]
    [InlineData(999u)]
    public async Task 登录准备_合法频道号不动(uint channel)
    {
        using var h = await FeatureHarness.CreateAsync($"chat-keep-{channel}");
        h.Player.ChatChannelId = channel;

        Feature().EnsureChannel(h.Session, h.Player);

        Assert.Equal(channel, h.Player.ChatChannelId);
    }

    // ================================================================
    //  ④ headframe 组合值与白名单
    // ================================================================

    /// <summary>
    /// ★ <c>headframe = 头像框id * 1000 + 气泡id</c>（<c>PlayerLogic.lua:183..184</c>）。
    /// </summary>
    [Fact]
    public void 组合headframe_按1000进位()
    {
        var table = ChatTable.Load(TestPaths.SettingDir);

        // 1 与 101 都在 HeadFrame.txt 里；气泡 3 在 Babble.txt 里
        Assert.Equal(1003u, table.ComposeHeadframe(1, 3));
        Assert.Equal(101000u, table.ComposeHeadframe(101, 0));
    }

    /// <summary>
    /// ★ 表外的头像框 id 必须退成 0 —— <c>UI_Chat.lua:667</c>
    /// <c>imgBorder.sprite = Sprite(tbHeadCfg.Icon)</c> 无 nil 保护，会抛。
    /// ★★ 第十轮更新：换新版 `setting.u` 后 `HeadFrame.txt` **补上了 70 号**
    /// （旧快照 70..100 整段是空洞），所以靶子从 70 换成仍然空的 90。
    /// 现网新表的空洞：16..19 / 26..29 / 60 / 90 / 95 / 151+。
    /// </summary>
    [Theory]
    [InlineData(16u)]
    [InlineData(60u)]
    [InlineData(90u)]
    [InlineData(151u)]
    [InlineData(1000u)]
    public void 组合headframe_表外id退成0(uint frame)
    {
        var table = ChatTable.Load(TestPaths.SettingDir);

        Assert.Equal(0u, table.ComposeHeadframe(frame, 0));
    }

    /// <summary>★ 反向闸：70 号在新表里**是存在的**，必须能正常组合（不退 0）。</summary>
    [Fact]
    public void 组合headframe_新表补上的70号可用()
    {
        var table = ChatTable.Load(TestPaths.SettingDir);

        Assert.Equal(70000u, table.ComposeHeadframe(70, 0));
    }

    /// <summary>白名单真的读到了现网数据（防止路径写错导致「一切都退 0」的假绿）。</summary>
    [Fact]
    public void 装扮白名单_读到现网数据()
    {
        var table = ChatTable.Load(TestPaths.SettingDir);

        Assert.True(table.HeadFrames.Count > 100, $"HeadFrame.txt 只读到 {table.HeadFrames.Count} 个 id");
        Assert.Contains(150u, table.HeadFrames);
        Assert.DoesNotContain(16u, table.HeadFrames);
        Assert.Contains(101u, table.Babbles);
        Assert.DoesNotContain(2u, table.Babbles);   // Babble.txt 里没有 2
    }

    /// <summary>空表（缺文件）时一切装扮 id 退成 0，不抛。</summary>
    [Fact]
    public void 装扮白名单_空表安全()
    {
        Assert.Equal(0u, ChatTable.Empty.ComposeHeadframe(101, 3));
        Assert.True(ChatTable.Empty.IsHeadFrameSafe(0));
        Assert.False(ChatTable.Empty.IsHeadFrameSafe(101));
    }

    // ================================================================
    //  girlid / sender_id
    // ================================================================

    /// <summary>头像少女 id 优先用「看板娘」任务值 (2,2)。</summary>
    [Fact]
    public async Task 头像少女id_优先看板娘()
    {
        using var h = await FeatureHarness.CreateAsync("chat-girl");
        h.Player.SetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId, 7);

        Assert.Equal(7u, ChatFeature.GirlIdOf(h.Player));
    }

    /// <summary>没有看板娘就用第一张持有的卡；都没有兜底 1（0 会让头像变问号）。</summary>
    [Fact]
    public void 头像少女id_兜底()
    {
        var bare = new Player("x", 1);
        Assert.Equal(1u, ChatFeature.GirlIdOf(bare));

        bare.Girls.Add(new Girl { Girlid = 9 });
        Assert.Equal(9u, ChatFeature.GirlIdOf(bare));
    }

    /// <summary>
    /// ★★★ 协议约束（原陷阱 T1）：<c>sender_id</c> 是 uint32 而 <c>PlayerNtf.roleid</c> 是 uint64。
    /// 本轮已把 <c>World.DeriveRoleId</c> 的值域收进 uint32 并加了存档迁移
    /// （<c>World.MigrateRoleId</c>），所以**新口径下 <c>SenderIdOf == Roleid</c> 必须成立** ——
    /// 这是「自己的消息显示在右侧」的唯一前提。
    /// 同时保留「超范围会截断」这条事实，防止将来有人把值域改回去而无人察觉。
    /// </summary>
    [Fact]
    public void 发言者id_新口径下与roleid完全相等()
    {
        foreach (var account in new[] { "", "selftest", "店长", "a-very-long-account-name-0123456789" })
        {
            var p = new Player(account, World.DeriveRoleId(account));
            Assert.True(p.Roleid <= uint.MaxValue, $"账号 {account} 的 roleid {p.Roleid} 超出 uint32");
            Assert.Equal(p.Roleid, ChatFeature.SenderIdOf(p));
        }
    }

    /// <summary>超出 uint32 的 roleid（旧口径存档、未迁移时）会被截断 —— 记录这个事实。</summary>
    [Fact]
    public void 发言者id_超范围仍会截断()
    {
        var p = new Player("t", 5239054864099717UL);

        Assert.Equal(2216831365u, ChatFeature.SenderIdOf(p));
        Assert.NotEqual(p.Roleid, ChatFeature.SenderIdOf(p));
    }

    // ================================================================
    //  装配
    // ================================================================

    /// <summary>本域声明的两个 LuaCall id 必须真的注册进路由（防止改名后漏注册）。</summary>
    [Fact]
    public async Task 装配_140和141都已注册()
    {
        using var h = await FeatureHarness.CreateAsync("chat-install");

        foreach (int cmd in ChatFeature.HandledCmds)
        {
            h.Outbound.Clear();
            await h.DispatchLuaCallAsync(cmd, "[1]");
            Assert.NotEmpty(h.Outbound);
        }
    }
}
