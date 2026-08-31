using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

/// <summary>
/// <c>BootstrapHttpServer</c> 的**资源下发**（<c>/Bundles/&lt;rel&gt;</c>）端到端测试。
///
/// <para><b>★ 为什么值得单独一组测试</b>（第七轮踩的坑）</para>
/// 这条链路此前**零测试覆盖**，而它是「朋友能不能装上游戏」的唯一通道：
/// 4650 个 bundle、4.3 GB 全部经由它下发。本轮在真机上暴露了两个问题：
/// <list type="number">
///   <item>★★ <b>大文件被 cgroup 节流</b>：原实现用 <c>File.ReadAllBytes</c> 把整个文件
///     读进堆，148 MB 的 APK 会撞上 systemd 的 <c>MemoryHigh=768M</c>
///     ⇒ 内核持续回收并节流进程（<c>memory.events</c> 的 <c>high</c> 计数飙到 <b>3968</b>）
///     ⇒ 下载速度从 11 MB/s 掉到 <b>~480 KB/s</b>。
///     改成 <see cref="FileStream"/> + <c>CopyToAsync</c> 流式发送后，
///     内存占用与文件大小无关。<see cref="大文件_内容逐字节一致"/> 用 8 MB 文件守住
///     「流式发送不会截断/错位」。</item>
///   <item><b>路径前缀是 <c>/Bundles/</c></b>（对应 <c>Game.Dir.bundle</c>）。
///     少了前缀就是 404 —— 自测时最容易踩（<c>curl host:8081/version.data</c> 得到 404，
///     正确的是 <c>curl host:8081/Bundles/version.data</c>）。</item>
/// </list>
///
/// <para>测试直接起真实的 <c>BootstrapHttpServer</c> 并用 <see cref="TcpClient"/>
/// 说 HTTP/1.1，因为这个类是手写 HTTP（没有 ASP.NET Core 的 TestServer 可用），
/// 而「头是否正确、Content-Length 是否与实际字节数一致」正是要验的东西。</para>
/// </summary>
public class AssetServingTests : IDisposable
{
    private readonly string _assetDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private readonly Task _serverTask;

    public AssetServingTests()
    {
        _assetDir = Path.Combine(Path.GetTempPath(), "sssj-asset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_assetDir);

        _port = FreeTcpPort();
        var options = new ServerOptions
        {
            BindAddress = "127.0.0.1",
            HttpPort = _port,
            AssetDirectory = _assetDir,
            AssetBuildVersion = 1366,
        };
        _serverTask = new BootstrapHttpServer(options).RunAsync(_cts.Token);
        WaitUntilListening();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _serverTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* 取消是预期 */ }
        _cts.Dispose();
        try { Directory.Delete(_assetDir, recursive: true); } catch { /* best effort */ }
    }

    // =================================================================
    //  一、正常下发
    // =================================================================

    [Fact]
    public async Task 小文件_下发成功且长度正确()
    {
        var body = Encoding.ASCII.GetBytes("hello-bundle");
        File.WriteAllBytes(Path.Combine(_assetDir, "lua.u"), body);

        var (status, headers, payload) = await GetAsync("/Bundles/lua.u");

        Assert.Equal(200, status);
        Assert.Equal(body, payload);
        Assert.Equal(body.Length.ToString(), headers["Content-Length"]);
        Assert.Equal("application/octet-stream", headers["Content-Type"]);
    }

    /// <summary>
    /// ★★ 本轮那个 bug 的回归：8 MB 文件必须**逐字节一致**且
    /// <c>Content-Length</c> 与实际收到的字节数相等。
    /// 流式发送写错（缓冲区复用、少写最后一块、头里长度算错）都会在这里露出来。
    /// </summary>
    [Fact]
    public async Task 大文件_内容逐字节一致()
    {
        // 8 MB —— 远大于 80 KB 的拷贝缓冲区，能覆盖上百次循环
        var body = new byte[8 * 1024 * 1024];
        RandomNumberGenerator.Fill(body);
        File.WriteAllBytes(Path.Combine(_assetDir, "big.u"), body);

        var (status, headers, payload) = await GetAsync("/Bundles/big.u");

        Assert.Equal(200, status);
        Assert.Equal(body.Length.ToString(), headers["Content-Length"]);
        Assert.Equal(body.Length, payload.Length);
        Assert.True(body.AsSpan().SequenceEqual(payload), "流式发送的内容与源文件不一致");
    }

    [Fact]
    public async Task 子目录资源_按相对路径映射()
    {
        var dir = Path.Combine(_assetDir, "map");
        Directory.CreateDirectory(dir);
        var body = Encoding.ASCII.GetBytes("scene-bytes");
        File.WriteAllBytes(Path.Combine(dir, "maplogin_prefab.u"), body);

        var (status, _, payload) = await GetAsync("/Bundles/map/maplogin_prefab.u");

        Assert.Equal(200, status);
        Assert.Equal(body, payload);
    }

    /// <summary>清单 URL 带 <c>?time=&lt;rand&gt;</c>（RVA 0x1531578），query 必须被忽略。</summary>
    [Fact]
    public async Task 清单带time查询串_仍能命中()
    {
        File.WriteAllBytes(Path.Combine(_assetDir, "version.data"), new byte[] { 1, 2, 3, 4 });

        var (status, _, payload) = await GetAsync("/Bundles/version.data?time=987654");

        Assert.Equal(200, status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload);
    }

    // =================================================================
    //  二、拒绝路径
    // =================================================================

    /// <summary>★ 这条日志（`资源不存在`）是真机验收「客户端拉了我们没有的文件」的关键判据。</summary>
    [Fact]
    public async Task 不存在的资源_404()
    {
        var (status, _, _) = await GetAsync("/Bundles/nope/missing.u");
        Assert.Equal(404, status);
    }

    /// <summary>★ 目录穿越必须挡住（真机用 `curl --path-as-is` 验证过）。</summary>
    [Theory]
    [InlineData("/Bundles/../../../etc/passwd")]
    [InlineData("/Bundles/map/../../secret.txt")]
    public async Task 目录穿越_403(string path)
    {
        // 在资源目录**外面**放一个文件，确保就算穿越成功也能被检出
        var outside = Path.Combine(_assetDir, "..", "secret.txt");
        File.WriteAllText(outside, "TOP-SECRET");
        try
        {
            var (status, _, payload) = await GetAsync(path);
            Assert.Equal(403, status);
            Assert.DoesNotContain("TOP-SECRET", Encoding.ASCII.GetString(payload));
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    /// <summary>没有 <c>/Bundles/</c> 前缀 ⇒ 不是资源请求，落到 Route 的 404。</summary>
    [Fact]
    public async Task 少了Bundles前缀_404()
    {
        File.WriteAllBytes(Path.Combine(_assetDir, "version.data"), new byte[] { 9 });

        var (status, _, _) = await GetAsync("/version.data");

        Assert.Equal(404, status);
    }

    // =================================================================
    //  三、引导端点仍然正常（确认流式分支没抢走非资源路径）
    // =================================================================

    [Fact]
    public async Task 引导端点不受影响_serverlist与checkupdate()
    {
        var (s1, _, p1) = await GetAsync("/serverlist?platform=selftest");
        Assert.Equal(200, s1);
        Assert.Contains("serverlist", Encoding.UTF8.GetString(p1));

        var (s2, _, p2) = await GetAsync("/checkupdate");
        Assert.Equal(200, s2);
        var txt = Encoding.UTF8.GetString(p2);
        // msg 必须是**字符串** "0"（否则客户端无限重试换址）
        Assert.Contains("\"msg\":\"0\"", txt);
        Assert.Contains("1366", txt);   // 配了 AssetBuildVersion ⇒ 下发该版本号
    }

    // =================================================================
    //  辅助
    // =================================================================

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void WaitUntilListening()
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, _port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }
        throw new InvalidOperationException($"HTTP 测试服务未能在 127.0.0.1:{_port} 上起来");
    }

    /// <summary>
    /// 手写一个最小 HTTP/1.1 GET 并把响应完整读到 EOF（服务端是 <c>Connection: close</c>）。
    /// 不用 <see cref="HttpClient"/>：要能拿到**原始字节**并单独校验头里的 Content-Length。
    /// </summary>
    private async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> GetAsync(string path)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _port);
        await using var stream = tcp.GetStream();

        var req = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        await stream.WriteAsync(req);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var all = ms.ToArray();

        // 拆头 / 体
        var sep = new byte[] { 13, 10, 13, 10 };
        int idx = IndexOf(all, sep);
        Assert.True(idx >= 0, "响应里找不到头体分隔符");

        var headText = Encoding.ASCII.GetString(all, 0, idx);
        var body = all.AsSpan(idx + sep.Length).ToArray();

        var lines = headText.Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0)
                headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        return (status, headers, body);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }
}
