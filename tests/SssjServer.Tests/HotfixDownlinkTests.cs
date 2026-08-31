using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

/// <summary>
/// **热更新下发链路**的回归闸（第十一轮新增）。
///
/// <para>热更新（通道 ①）的机制只有一句话：
/// <b><c>buildVersion</c> 变大 + 某个 bundle 的 <c>hash</c> 变了 ⇒ 客户端下次启动只重下那个包。</b>
/// 服务端一侧要保证三件事，本文件逐条守住：</para>
/// <list type="number">
///   <item><b><c>--asset-build-version auto</c> 必须现读清单</b> —— 手写死的版本号在
///     <c>tools/hotfix.py</c> bump 之后会**静默失效**：客户端拿到旧号 ⇒ 什么都不下，
///     而且**两端日志都完全干净**。这是本轮识别出的第一号陷阱
///     （见 <see cref="AssetManifest"/> 类注释）。</item>
///   <item><b><c>urllist.crc</c> 必须保持空对象</b> —— 客户端
///     <c>EntryMgr.CheckBundleCRC</c>（RVA <c>0x21D012C</c>）只在这张字典里
///     **登记了该包**时才拿它比 CRC，不匹配就**跳过 <c>BundleEncrypt.Decrypt</c>**
///     （<c>Assets.LoadBundleOne</c> <c>0x1760300</c> 的分支）⇒ 把加密包当明文加载 ⇒ 必崩。
///     ⇒ 换句话说：热更能成立**正是因为**这张表是空的。</item>
///   <item><b>清单本身要能经 <c>/Bundles/version.data</c> 原样取回</b>
///     （客户端会带 <c>?time=&lt;rand&gt;</c>，query 必须被忽略）。</item>
/// </list>
///
/// <para>★ 这里刻意**不测** <c>tools/hotfix.py</c> 自己（那是 Python 侧，
/// 由 <c>bundle_patch.py verify</c> 的自检 + 真机验收覆盖）；本文件只管
/// 「服务端会不会把正确的版本号与清单发出去」。</para>
/// </summary>
public class HotfixDownlinkTests : IDisposable
{
    private readonly string _assetDir;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;
    private readonly Task _serverTask;

    public HotfixDownlinkTests()
    {
        _assetDir = Path.Combine(Path.GetTempPath(), "sssj-hotfix-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_assetDir);

        _port = FreeTcpPort();
        var options = new ServerOptions
        {
            BindAddress = "127.0.0.1",
            HttpPort = _port,
            GateHost = "127.0.0.1",
            AssetDirectory = _assetDir,
            // ★ 本组测试全部走 auto：这才是热更新推荐的部署姿势
            AssetBuildVersionAuto = true,
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
    //  一、AssetManifest：只读前 8 字节的 buildVersion
    // =================================================================

    [Fact]
    public void 清单解析_读出第二个int32为buildVersion()
    {
        WriteManifest(version: 10136, buildVersion: 1366);
        var mf = new AssetManifest(_assetDir);
        Assert.Equal(1366, mf.ReadBuildVersion());
    }

    [Fact]
    public void 清单解析_文件改动后立刻反映新版本()
    {
        WriteManifest(10136, 1366);
        var mf = new AssetManifest(_assetDir);
        Assert.Equal(1366, mf.ReadBuildVersion());

        // ★ 模拟 tools/hotfix.py publish：只改 buildVersion
        WriteManifest(10136, 1367);
        Assert.Equal(1367, mf.ReadBuildVersion());
    }

    [Fact]
    public void 清单解析_缺失或过短时降级为0()
    {
        var mf = new AssetManifest(_assetDir);
        // 文件不存在
        Assert.Equal(0, mf.ReadBuildVersion());

        // 只有 4 字节（少于 8）⇒ 也当不可用，而不是读出垃圾
        File.WriteAllBytes(Path.Combine(_assetDir, "version.data"), new byte[4]);
        Assert.Equal(0, mf.ReadBuildVersion());
    }

    [Fact]
    public void 清单解析_目录未配置时不抛()
    {
        var mf = new AssetManifest("");
        Assert.Equal(0, mf.ReadBuildVersion());
    }

    // =================================================================
    //  二、checkupdate：auto 模式跟着清单走
    // =================================================================

    [Fact]
    public async Task CheckUpdate_auto模式下发清单里的buildVersion()
    {
        WriteManifest(10136, 1366);
        var url = await CheckUpdateUrlListAsync();
        Assert.Equal(1366, (int)url["buildversion"]!);
        Assert.Equal("1366", (string)url["updatever"]!);
    }

    /// <summary>
    /// ★★★ 本文件最重要的一条：**发布热更后无需重启服务端**。
    /// 同一个 <c>BootstrapHttpServer</c> 实例，清单从 1366 bump 到 1367，
    /// 下一次 <c>checkupdate</c> 就必须回 1367。
    /// 若这条红了，就退回「必须重启服务端」的老姿势 —— 那意味着
    /// 每次热更都要断一次所有在线玩家的连接。
    /// </summary>
    [Fact]
    public async Task CheckUpdate_清单bump后无需重启即生效()
    {
        WriteManifest(10136, 1366);
        Assert.Equal(1366, (int)(await CheckUpdateUrlListAsync())["buildversion"]!);

        WriteManifest(10136, 1367);
        Assert.Equal(1367, (int)(await CheckUpdateUrlListAsync())["buildversion"]!);

        WriteManifest(10136, 1400);
        Assert.Equal(1400, (int)(await CheckUpdateUrlListAsync())["buildversion"]!);
    }

    [Fact]
    public async Task CheckUpdate_清单不可用时回退为跳过更新()
    {
        // 没有 version.data ⇒ buildversion 必须是 0（客户端打
        // "buildversion is 0. dont update" 并跳过整条资源更新），
        // 而不是把一个非 0 的旧值发出去让客户端去下一个不存在的清单。
        var url = await CheckUpdateUrlListAsync();
        Assert.Equal(0, (int)url["buildversion"]!);
        Assert.Empty((JsonArray)url["updateurl"]!);
    }

    [Fact]
    public async Task CheckUpdate_msg恒为字符串0()
    {
        WriteManifest(10136, 1366);
        var body = await GetTextAsync("/checkupdate");
        // msg 表示「请求被受理」，不是「无更新」；写成数字 0 会让客户端无限换址重试
        Assert.Contains("\"msg\":\"0\"", body);
    }

    /// <summary>
    /// ★★★ <c>urllist.crc</c> 必须**永远是空对象**。
    ///
    /// 取证（<c>EntryMgr.CheckBundleCRC</c> RVA <c>0x21D012C</c>）：
    /// 该字典里**登记了** <c>lua.u</c> / <c>setting.u</c> 时，客户端会用它比对
    /// 本地文件的 CRC32；不等 ⇒ 返回 false ⇒ <c>Assets.LoadBundleOne</c>
    /// （<c>0x1760300</c>）**跳过** <c>BundleEncrypt.Decrypt</c>
    /// ⇒ 把加密包当明文喂给 <c>LoadFromMemory</c> ⇒ 游戏起不来。
    ///
    /// 而热更换包后 CRC 必然与登记值不同 ⇒ **登记 = 热更必崩**。
    /// 空表则命中 <c>ContainsKey</c> 为假的分支（<c>0x21D03F0</c>）⇒ 恒放行。
    /// </summary>
    [Fact]
    public async Task CheckUpdate_CrcDictionary_MustStayEmpty()
    {
        WriteManifest(10136, 1366);
        var url = await CheckUpdateUrlListAsync();

        var crc = Assert.IsType<JsonObject>(url["crc"]);
        Assert.Empty(crc);

        // apklist 同理（客户端 DownloadVersionList 压根不读它，但留着字段更保险）
        Assert.IsType<JsonObject>(url["apklist"]);
    }

    // =================================================================
    //  三、清单本体的下发（客户端会带 ?time=<rand>）
    // =================================================================

    [Fact]
    public async Task 清单可经Bundles路径原样取回_且忽略time查询串()
    {
        WriteManifest(10136, 1366);
        var expected = File.ReadAllBytes(Path.Combine(_assetDir, "version.data"));

        var (status, _, body) = await GetAsync("/Bundles/version.data?time=54321");

        Assert.Equal(200, status);
        Assert.Equal(expected, body);
        // 客户端拿到的前 8 字节就是 version/buildVersion
        Assert.Equal(10136, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(0, 4)));
        Assert.Equal(1366, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4, 4)));
    }

    /// <summary>
    /// 热更后被改的那个包必须能按**新字节**取回（客户端会用清单的 hash 做 CRC32 校验，
    /// 服务端发的必须是磁盘上的最新内容，不能有缓存）。
    /// </summary>
    [Fact]
    public async Task 被改的包重新下发时拿到的是新字节()
    {
        WriteManifest(10136, 1366);
        var path = Path.Combine(_assetDir, "lua.u");

        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("OLD-LUA-BUNDLE"));
        var (_, _, first) = await GetAsync("/Bundles/lua.u");
        Assert.Equal("OLD-LUA-BUNDLE", Encoding.ASCII.GetString(first));

        // 模拟 hotfix：包变了、清单也 bump
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("NEW-LUA-BUNDLE-AFTER-HOTFIX"));
        WriteManifest(10136, 1367);

        var (_, headers, second) = await GetAsync("/Bundles/lua.u");
        Assert.Equal("NEW-LUA-BUNDLE-AFTER-HOTFIX", Encoding.ASCII.GetString(second));
        Assert.Equal(second.Length.ToString(), headers["Content-Length"]);
        Assert.Equal(1367, (int)(await CheckUpdateUrlListAsync())["buildversion"]!);
    }

    // =================================================================
    //  四、手写死版本号的老姿势仍然可用（向后兼容）
    // =================================================================

    [Fact]
    public async Task 显式版本号模式不读清单()
    {
        // 另起一个实例：AssetBuildVersionAuto = false + 写死 1366
        int port = FreeTcpPort();
        using var cts = new CancellationTokenSource();
        var dir = Path.Combine(Path.GetTempPath(), "sssj-hotfix-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // 清单里是 9999，但显式模式必须无视它
        WriteManifest(10136, 9999, dir);

        var options = new ServerOptions
        {
            BindAddress = "127.0.0.1",
            HttpPort = port,
            GateHost = "127.0.0.1",
            AssetDirectory = dir,
            AssetBuildVersion = 1366,
            AssetBuildVersionAuto = false,
        };
        var task = new BootstrapHttpServer(options).RunAsync(cts.Token);
        try
        {
            WaitUntilListening(port);
            var text = await GetTextAsync("/checkupdate", port);
            var url = (JsonObject)JsonNode.Parse(text)!["urllist"]!;
            Assert.Equal(1366, (int)url["buildversion"]!);
        }
        finally
        {
            cts.Cancel();
            // ★ 用 await 而不是 task.Wait()：xUnit1031（阻塞会死锁）。
            //   取消导致的异常是**预期**的，吞掉。
            try { await task; } catch { /* 取消是预期 */ }
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // =================================================================
    //  辅助
    // =================================================================

    /// <summary>
    /// 写一份最小但**结构合法**的 <c>version.data</c>：
    /// <c>version</c> / <c>buildVersion</c> 两个 int32 + 四个空分段的计数。
    /// 与 <c>tools/verdata.py</c> 的 <c>serialize()</c> 同口径
    /// （packInfo.count、packInfo.files、bundles、a2b、abinfo 各一个 int32 计数）。
    /// </summary>
    private void WriteManifest(int version, int buildVersion, string? dir = null)
    {
        var buf = new byte[4 * 7];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0), version);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), buildVersion);
        // 其余全 0：packInfo.count=0 / files=0 / bundles=0 / a2b=0 / abinfo=0
        File.WriteAllBytes(Path.Combine(dir ?? _assetDir, "version.data"), buf);
    }

    private async Task<JsonObject> CheckUpdateUrlListAsync()
    {
        var text = await GetTextAsync("/checkupdate");
        var root = JsonNode.Parse(text) as JsonObject;
        Assert.NotNull(root);
        return Assert.IsType<JsonObject>(root!["urllist"]);
    }

    private async Task<string> GetTextAsync(string path, int? port = null)
    {
        var (_, _, body) = await GetAsync(path, port);
        return Encoding.UTF8.GetString(body);
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private void WaitUntilListening(int? port = null)
    {
        int p = port ?? _port;
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, p);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }
        throw new InvalidOperationException($"HTTP 测试服务未能在 127.0.0.1:{p} 上起来");
    }

    private async Task<(int Status, Dictionary<string, string> Headers, byte[] Body)> GetAsync(
        string path, int? port = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port ?? _port);
        await using var stream = tcp.GetStream();

        var req = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        await stream.WriteAsync(req);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var all = ms.ToArray();

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
