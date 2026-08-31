using SssjServer.Game;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// LuaCall 名称表：从 docs/luacall-table.json 生成的 (name, 中文注释) 查询。
/// </summary>
public class LuaCallNamesTests
{
    [Theory]
    [InlineData(1, "Card_Decompose", "角色卡分解协议")]
    [InlineData(11, "Shop_ReqGoodsList", "请求商品列表")]
    [InlineData(10001, "Club_CreateClub", "创建一个社团")]
    public void Resolve_KnownId(int cmd, string name, string comment)
    {
        var (n, c) = LuaCallNames.Resolve(cmd);
        Assert.Equal(name, n);
        Assert.Equal(comment, c);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]      // 表中无 id=2
    [InlineData(999977)] // 未知
    public void Resolve_UnknownId_ReturnsNulls(int cmd)
    {
        var (n, c) = LuaCallNames.Resolve(cmd);
        Assert.Null(n);
        Assert.Null(c);
    }

    [Fact]
    public void FormatLog_IncludesNameAndChineseComment()
    {
        var line = LuaCallNames.FormatLog(11, "{\"page\":1}");
        Assert.Equal("cmd=11 (Shop_ReqGoodsList 请求商品列表) param={\"page\":1}", line);
    }

    [Fact]
    public void FormatLog_UnknownCommand_MarksUnNamed()
    {
        var line = LuaCallNames.FormatLog(2, "null");
        Assert.Contains("UnNamed", line);
    }
}
