using System.Net;
using System.Net.Sockets;
using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

public class ClientSessionTests : IAsyncLifetime
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private ClientSession? _session;

    private async Task MakePairAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _client = new TcpClient();
        var connect = _client.ConnectAsync(IPAddress.Loopback, port);
        var accepted = await _listener.AcceptTcpClientAsync();
        await connect;
        _session = new ClientSession(1, accepted);
    }

    public async Task InitializeAsync() => await MakePairAsync();

    public Task DisposeAsync()
    {
        _session?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Feed_BadMagic_ClosesSession_ExceptionDoesNotEscape()
    {
        var bad = PacketCodec.Encode(1102, 1, new byte[8]);
        bad[13] = 0x00; // 破坏 0x88 魔数

        int dispatched = 0;
        // FeedAsync 内部捕获 InvalidDataException，异常不得逸出，会话应关闭
        await _session!.FeedAsync(
            (_, _) => { dispatched++; return Task.CompletedTask; },
            bad);

        Assert.True(_session.IsClosed, "协议失步后会话应关闭");
        Assert.Equal(0, dispatched); // 该封包本身不应被分发
    }

    [Fact]
    public async Task Feed_ValidThenBadMagic_DispatchesValid_ThenCloses()
    {
        var valid = PacketCodec.Encode(1008, 1, new byte[] { 0x01 });
        var bad = PacketCodec.Encode(1001, 2, new byte[8]);
        bad[13] = 0x00; // 坏魔数
        var concat = valid.Concat(bad).ToArray();

        var serials = new List<uint>();
        await _session!.FeedAsync(
            (_, p) => { serials.Add(p.Serial); return Task.CompletedTask; },
            concat);

        // 合法封包先被分发，随后失步关闭
        Assert.Equal(new uint[] { 1 }, serials);
        Assert.True(_session.IsClosed);
    }

    [Fact]
    public async Task Feed_200PacketsInOneChunk_DispatchedInOrder()
    {
        const int count = 200;
        var all = new List<byte>();
        for (int i = 0; i < count; i++)
            all.AddRange(PacketCodec.Encode(1001, (uint)i, new byte[] { (byte)i }));

        var serials = new List<uint>();
        await _session!.FeedAsync(
            (_, p) => { serials.Add(p.Serial); return Task.CompletedTask; },
            all.ToArray());

        Assert.Equal(count, serials.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal((uint)i, serials[i]); // 严格按序
        Assert.False(_session.IsClosed, "合法批量不应关闭会话");
    }

    [Fact]
    public async Task Feed_MultipleChunks_FragmentedPacket_StaysAliveThenDispatches()
    {
        // 模拟 TCP 分片：一个封包分三片喂入，前两片不足以解码（不关闭、不捕获异常），最后一片凑齐后分发。
        var full = PacketCodec.Encode(1102, 9, new byte[32]);
        int th1 = 10, th2 = 24; // 两个切点

        var serials = new List<uint>();
        // 片 1
        await _session!.FeedAsync(Capture(serials), full[..th1]);
        Assert.False(_session.IsClosed);
        Assert.Empty(serials);
        // 片 2
        await _session!.FeedAsync(Capture(serials), full[th1..th2]);
        Assert.False(_session.IsClosed);
        Assert.Empty(serials);
        // 片 3：凑齐
        await _session!.FeedAsync(Capture(serials), full[th2..]);
        Assert.Equal(new uint[] { 9 }, serials);
        Assert.False(_session.IsClosed);
    }

    private static Func<ClientSession, Packet, Task> Capture(List<uint> serials)
        => (_, p) => { serials.Add(p.Serial); return Task.CompletedTask; };
}
