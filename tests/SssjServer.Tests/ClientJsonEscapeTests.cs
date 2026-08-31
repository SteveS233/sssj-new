using System.Text.Json;
using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

/// <summary>
/// 下行 JSON 的转义必须落在客户端 Lua 解码器支持的集合内。
///
/// 取证：extracted/lua/Json.lua:252
/// <code>charstounescape = "\"'\\bfnrt/";</code>
/// 只支持 <c>\" \' \\ \b \f \n \r \t \/</c> —— **没有 \uXXXX**。
/// System.Text.Json 的默认编码器会把 <c>"</c> 写成 <c>\u0022</c>、中文写成 <c>\uXXXX</c>，
/// 那样客户端 <c>Json.Decode</c> 会抛错，被 S2C.ProcCmd 的 pcall 吞掉，
/// 只打印 <c>S2C.ProcCmd Error:: LuaCall</c> 后静默丢包（真机实测）。
/// </summary>
public class ClientJsonEscapeTests
{
    private static string Serialize(object o) => JsonSerializer.Serialize(o, LuaCallRouter.ClientJson);

    [Fact]
    public void 引号必须写成反斜杠引号而不是_u0022()
    {
        string json = Serialize(new { luaStr = "print(\"hi\")" });
        Assert.DoesNotContain("\\u0022", json);
        Assert.Contains("\\\"hi\\\"", json);
    }

    [Fact]
    public void 中文必须原样输出不做_u_转义()
    {
        string json = Serialize(new { msg = "体力不足" });
        Assert.DoesNotContain("\\u", json);
        Assert.Contains("体力不足", json);
    }

    [Fact]
    public void 换行仍然用受支持的反斜杠n转义()
    {
        string json = Serialize(new { luaStr = "a\nb" });
        Assert.Contains("a\\nb", json);
        Assert.DoesNotContain("\\u000A", json);
    }

    [Fact]
    public void 尖括号与加号等符号不再被_u_转义()
    {
        // 默认编码器会把 < > & + ' 全转成 \uXXXX，Lua 解码器一样吃不下
        string json = Serialize(new { s = "a<b>c&d+e'f" });
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void CallClient_的完整包体也不含_u_转义()
    {
        // 走 BuildFullJson 的真实路径（internal 可见性由 InternalsVisibleTo 提供）
        string json = LuaCallRouter.BuildFullJson(999999, new { luaStr = "print(\"体力\", me.vigour)" });
        Assert.DoesNotContain("\\u", json);
        Assert.Contains("\"sCmd\":999999", json);
    }
}
