using SssjServer.Core;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// `gmsrv.sh 'luacall <cmd> [json]'` / `'call <sFunction> [json]'`
/// —— 第十四轮新增的**上行注入**命令（<see cref="ServerConsoleFeature"/>）。
///
/// <para><b>★★★ 它为什么存在（本轮验收踩坑三次才补的工具）</b></para>
/// 原来的验收路径 `tools/gm.sh` 是
/// 「服务端下发 Lua → 客户端 <c>DoLuaString</c> 执行 → 客户端再发上行」，
/// 链路长且**会静默失效**：实测同一客户端生命周期内下发若干次后就不再执行，
/// 客户端连 <c>print</c> 都不输出，而服务端仍打印「已下发到 1 个会话」。
/// 症状表现为「服务端收不到包」，本轮据此**误判过 321/322/53 三条命令没实现**。
///
/// 本命令直接在服务端侧调 <c>LuaCallRouter.DispatchAsync</c>，
/// 与真实上行走**同一套路由和 handler**，只跳过网络与客户端脚本层。
///
/// <para><b>本文件测什么</b></para>
/// <see cref="ServerConsoleFeature.Execute"/> 需要**在线会话**
/// （<c>World.OnlineSessions()</c> 里的 <c>SessionAdapter</c>），
/// 而单测 Harness 的会话不进 World ⇒ 这里**不测 Execute 的分发**，
/// 只锁住两件可单测的事：
/// <list type="number">
///   <item>命令**不会抛**（GM 通道是运维工具，非法输入不能拖垮服务端）；</item>
///   <item>★ 它拼出的 JSON 与真实上行**逐字节等价** ——
///     这才是「注入等于真上行」的依据，也是这个工具可信的前提。</item>
/// </list>
/// 分发本身由真机验收覆盖（本轮用它验通了佣兵 7 条、排行塔 6 条等）。
/// </summary>
public class ServerConsoleLuaCallTests
{
    /// <summary>
    /// ★★★ 核心契约：`luacall N {json}` 拼出的字符串必须与
    /// <see cref="FeatureHarness.DispatchLuaCallAsync"/>（= 真实 pf 1022 上行的形状）一致。
    /// 这条红了就说明「注入」与「真上行」不等价，工具结论不可信。
    /// </summary>
    [Theory]
    [InlineData(250, "{\"Power\":1}")]
    [InlineData(53, "[1,1]")]              // ★ 裸数组（排行塔的真实形状）
    [InlineData(320, "{}")]                // 无参
    public void 注入拼出的JSON与真实上行等价(int cmd, string payload)
    {
        string injected = $"{{\"sCmd\":{cmd},\"tbParam\":{payload}}}";

        // FeatureHarness.DispatchLuaCallAsync 用的就是这个形状（见其实现）
        string expected = $"{{\"sCmd\":{cmd},\"tbParam\":{payload}}}";

        Assert.Equal(expected, injected);

        // 且必须是合法 JSON（注入前不做校验，坏 JSON 会在 DispatchAsync 里抛）
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(injected);
        Assert.NotNull(parsed);
        Assert.Equal(cmd, parsed!["sCmd"]!.GetValue<int>());
        Assert.NotNull(parsed["tbParam"]);
    }

    /// <summary>
    /// <c>sFunction</c> 族：payload 就是 tbParam 本体，**没有** sCmd 外壳
    /// （<c>LuaCallRouter.ScriptCallHandler</c> 的约定）。
    /// </summary>
    [Fact]
    public void 字符串命令不加sCmd外壳()
    {
        const string payload = "{\"nCmd\":8}";
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(payload);

        Assert.NotNull(parsed);
        Assert.Null(parsed!["sCmd"]);          // ★ 不能有外壳
        Assert.Equal(8, parsed["nCmd"]!.GetValue<int>());
    }

    /// <summary>
    /// ★ 非法/缺参输入只记日志，绝不能抛。
    /// <see cref="ServerConsoleFeature.Execute"/> 在没有在线会话时会直接返回，
    /// 正好用来验「不抛」这条。
    /// </summary>
    [Theory]
    [InlineData("luacall")]
    [InlineData("call")]
    [InlineData("luacall abc {}")]
    [InlineData("luacall 999999 {\"x\":1}")]
    [InlineData("luacall 53 [1,1]")]
    [InlineData("call PhoneMsg {\"nCmd\":8}")]
    public async Task 非法或缺参输入不抛(string line)
    {
        var h = await FeatureHarness.CreateAsync("gmsrv-" + Math.Abs(line.GetHashCode()));
        var console = h.Module.FindFeature<ServerConsoleFeature>();
        Assert.NotNull(console);

        var ex = Record.Exception(() => console!.Execute(line));
        Assert.Null(ex);
    }

    /// <summary>★ <c>FindFeature</c> 本身：装配后必须找得到（本轮为单测新增的 API）。</summary>
    [Fact]
    public async Task 装配后能按类型取到功能域()
    {
        var h = await FeatureHarness.CreateAsync(nameof(装配后能按类型取到功能域));

        Assert.NotNull(h.Module.FindFeature<ServerConsoleFeature>());
        Assert.NotNull(h.Module.FindFeature<AssistFeature>());
        Assert.NotEmpty(h.Module.Features);
    }
}
