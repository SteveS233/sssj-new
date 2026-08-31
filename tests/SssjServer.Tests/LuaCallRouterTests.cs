using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

public class LuaCallRouterTests
{
    [Fact]
    public void BuildChunks_Over64k_SplitsCorrectly_ReproducesOriginalJson()
    {
        const int cmd = 42;
        const long unix = 1_700_000_000;

        // 构造一个超过 64000 字节的 sJsonParam 原文：{"sCmd":42,"tbParam":"<100000个a>"}
        var bigStr = new string('a', 100_000);
        var full = new JsonObject
        {
            ["sCmd"] = cmd,
            ["tbParam"] = bigStr,
        };
        var fullBytes = Encoding.UTF8.GetBytes(full.ToJsonString());

        Assert.True(fullBytes.Length > LuaCallRouter.ProSize,
            $"前置条件：原文应超过 {LuaCallRouter.ProSize} 字节，实际 {fullBytes.Length}");

        var chunks = LuaCallRouter.BuildChunks(cmd, fullBytes, unix);

        // 片数 = ceil(len / 64000)
        int expectedChunks = (fullBytes.Length + LuaCallRouter.ProSize - 1) / LuaCallRouter.ProSize;
        Assert.Equal(expectedChunks, chunks.Count);

        // 只有最后一片 CallEnd=1
        for (int i = 0; i < chunks.Count; i++)
        {
            var headerText = Encoding.UTF8.GetString(chunks[i].Header);
            var header = JsonNode.Parse(headerText)!;
            Assert.Equal(unix, header["CallTime"]!.GetValue<long>());
            Assert.Equal(cmd, header["CallCmd"]!.GetValue<int>());
            Assert.Equal(i == chunks.Count - 1, header["CallEnd"]!.GetValue<int>() == 1);
        }

        // 除最后一片外每片切片都正好 64000 字节
        for (int i = 0; i < chunks.Count - 1; i++)
            Assert.Equal(LuaCallRouter.ProSize, chunks[i].Slice.Length);

        // 按序拼接所有切片还原原文
        var rebuilt = new byte[fullBytes.Length];
        int offset = 0;
        foreach (var (_, slice) in chunks)
        {
            slice.CopyTo(rebuilt, offset);
            offset += slice.Length;
        }
        Assert.Equal(fullBytes, rebuilt);
    }

    [Fact]
    public void BuildChunks_UnderOrAt64k_IsSingleChunk_LastOnly_NoSplitBoundary()
    {
        // 刚好 = 64000 不算超，应当只有一片
        byte[] bytes = Encoding.UTF8.GetBytes(new string('x', LuaCallRouter.ProSize));
        var chunks = LuaCallRouter.BuildChunks(7, bytes, 1234L);
        Assert.Single(chunks);
        Assert.Equal(1, JsonNode.Parse(Encoding.UTF8.GetString(chunks[0].Header))!["CallEnd"]!.GetValue<int>());
        Assert.Equal(bytes, chunks[0].Slice);
    }

    [Fact]
    public async Task DispatchAsync_UnhandledCmd_LogsWarn_DoesNotThrow()
    {
        var router = new LuaCallRouter();
        var json = """{"sCmd":999999,"tbParam":{"x":1}}""";
        // 未映射任何 handler，只应打日志，不抛异常
        // 需要一个 ClientSession；无法直接 new（依赖 TcpClient），此处仅验证 Parse 与分支不抛。
        // 为覆盖该逻辑，我们用一个本地回环 TcpClient 会话。
        using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;

        using var client = new System.Net.Sockets.TcpClient();
        var connect = client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var accepted = await server.AcceptTcpClientAsync();
        await connect;

        var session = new ClientSession(1, client);
        try
        {
            await router.DispatchAsync(session, "LuaCall", json); // 未注册 → Warn，不抛
            await router.DispatchAsync(session, "OtherFunc", json); // 未知 sFunction → Warn，不抛
        }
        finally
        {
            session.Dispose();
        }
    }
}
