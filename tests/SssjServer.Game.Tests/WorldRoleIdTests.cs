using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <c>World.DeriveRoleId</c> 的值域约束与旧存档迁移（<c>World.MigrateRoleId</c>）。
///
/// 为什么单独一个测试文件：这条约束**不属于任何功能域**，但被聊天域硬依赖 ——
/// <c>gsproto.NoticeChattingMsg.sender_id</c> / <c>receiver_id</c> 是 **uint32**
/// （全 gsproto 里只有这两个角色 id 是 32 位），而客户端拿它与 uint64 的
/// <c>me.id</c>（= <c>PlayerNtf.roleid</c>）**直接比较**：
/// <code>
/// UI_Chat.lua:581/589/595   senderID == me.id ? ShowRight : ShowLeft   -- 消息在右边还是左边
/// PlayerLogic.lua:283       me.id ~= sender_id → UI_Main:UpdateChatMessage  -- 主城飘字
/// </code>
/// roleid 超过 <c>0xFFFFFFFF</c> 就永远比不相等 ⇒ 玩家自己的消息被渲染成「别人的」，
/// 还会每条都在主城弹飘字。取证与取舍见 `docs/05` §18.5。
/// </summary>
public class WorldRoleIdTests
{
    /// <summary>★ 值域必须落在 uint32 内（这是聊天域「自己的消息显示在右侧」的唯一前提）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("selftest")]
    [InlineData("店长")]
    [InlineData("a")]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyz")]
    public void 派生roleid_落在uint32内(string account)
    {
        ulong id = World.DeriveRoleId(account);

        Assert.InRange(id, 1UL, uint.MaxValue);
    }

    /// <summary>同一账号必须每次算出同一个 roleid（存档按账号命名，roleid 是对外 id）。</summary>
    [Fact]
    public void 派生roleid_确定性()
    {
        Assert.Equal(World.DeriveRoleId("abc"), World.DeriveRoleId("abc"));
        Assert.NotEqual(World.DeriveRoleId("abc"), World.DeriveRoleId("abd"));
    }

    /// <summary>大量账号下不应出现明显碰撞（32 位空间 + FNV-1a，1 万个账号内应无碰撞）。</summary>
    [Fact]
    public void 派生roleid_一万个账号无碰撞()
    {
        var seen = new HashSet<ulong>();
        for (int i = 0; i < 10_000; i++)
            Assert.True(seen.Add(World.DeriveRoleId($"acc{i}")), $"acc{i} 与之前的账号碰撞");
    }

    /// <summary>★ 旧口径（[1, 2^53)）的存档 roleid 要被重算进 uint32。</summary>
    [Fact]
    public void 迁移_超出uint32的旧roleid被重算()
    {
        // 5239054864099717 是本仓库实测存档 data/_.player.json 里的旧值
        var player = new Player("", 5239054864099717UL);

        World.MigrateRoleId(player);

        Assert.Equal(World.DeriveRoleId(""), player.Roleid);
        Assert.InRange(player.Roleid, 1UL, uint.MaxValue);
    }

    /// <summary>迁移必须幂等：已在 uint32 内的 roleid 一个字节都不能动。</summary>
    [Fact]
    public void 迁移_幂等()
    {
        var player = new Player("keep", 4242UL);

        World.MigrateRoleId(player);
        Assert.Equal(4242UL, player.Roleid);

        // 新口径算出来的值本身在范围内，第二次迁移也不动
        var derived = new Player("keep", World.DeriveRoleId("keep"));
        ulong before = derived.Roleid;
        World.MigrateRoleId(derived);
        World.MigrateRoleId(derived);
        Assert.Equal(before, derived.Roleid);
    }
}

/// <summary>
/// 「同一账号的新连接顶掉旧连接」时，旧会话必须同时从 <c>OnlineSessions()</c> 里消失。
///
/// 为什么单独测：所有「按在线会话广播」的功能（聊天世界频道、社团频道、GM 下发 Lua）
/// 都依赖这个快照。先前 <c>World.Login</c> 只 <c>Remove</c> 了 <c>_bySession</c>、漏了
/// <c>_sessions</c>，真机实测聊天广播会往已断开的旧 socket 上多推一条
/// （`work/server.log` 里出现 `session 1 => pf=1012` + `session 2 => pf=1012`）。
/// </summary>
public class WorldSessionTests
{
    private sealed class StubSession : IGameSession
    {
        public StubSession(int id) => Id = id;
        public int Id { get; }
        public object? Tag { get; set; }
        public void EnableCrypto(string clientKey) { }
        public void Send(int pf, uint serial, SssjServer.Protocol.ProtoMessage message) { }
        public void Push(int pf, SssjServer.Protocol.ProtoMessage message) { }
    }

    private static World NewWorld()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sssj-world-tests", Guid.NewGuid().ToString("N"));
        var store = new PlayerStore(new SssjServer.Core.ServerOptions { DataDirectory = dir });
        return new World(store);
    }

    [Fact]
    public void 新连接顶掉旧连接_旧会话不再出现在在线快照里()
    {
        var world = NewWorld();
        var old = new StubSession(1);
        var fresh = new StubSession(2);

        world.Login(old, "dup");
        Assert.Single(world.OnlineSessions());

        world.Login(fresh, "dup");

        var online = world.OnlineSessions();
        Assert.Single(online);
        Assert.Equal(2, online[0].Id);
    }

    [Fact]
    public void 不同账号的会话互不影响()
    {
        var world = NewWorld();
        world.Login(new StubSession(1), "a");
        world.Login(new StubSession(2), "b");

        Assert.Equal(2, world.OnlineSessions().Count);
    }
}
