using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 编队更新的另两个入口（LuaCall 22 <c>ChooseEquip_Update</c> / 23 <c>ChooseGirl_Update</c>）。
///
/// 这两个命令在真机日志里累计 <b>17 次 unhandled</b>（23 十二次 / 22 五次），
/// 卡在「出击前换卡/换武器」这一步，是 <c>work/unhandled-roadmap.md</c> 里排第二的缺口。
///
/// 断言围绕一条容易写错的规则：★ **回执必须回原样的 sCmd**。
/// 客户端是 <c>LuaCall:CallClient(sCmd, tbRet)</c>（LuaCall.lua:763 / :779），
/// 三个命令刷不同界面（21→UI_Formation、22→UI_ChooseEquip、23→UI_ChooseGirl+UI_BattleMain2）。
/// 回错了不会报错、不会崩 —— 只是「在选武器界面点了确定，界面不关也不刷新」，
/// 属于本项目最重视的那类**静默失败**。
/// </summary>
public class FormationExtraFeatureTests
{
    /// <summary>上行形状取自真机日志（cmd=22/23 的 param 与 cmd=21 完全一致）。</summary>
    private const string Info = """
        {"Id":1,"Info":[
          {"MainCard":11,"UsedCard":11,"Secondarycard":[],"WeaponId":0,"Rune":[0,0,0,0]},
          {"MainCard":12,"UsedCard":12,"Secondarycard":[],"WeaponId":0,"Rune":[0,0,0,0]}
        ]}
        """;

    [Theory]
    [InlineData(FormationExtraFeature.ChooseEquipUpdateCmd)]
    [InlineData(FormationExtraFeature.ChooseGirlUpdateCmd)]
    public async Task Update_SavesFormation_AndEchoesSameCmd(int cmd)
    {
        using var h = await FeatureHarness.CreateAsync($"formx-{cmd}");

        await h.DispatchLuaCallAsync(cmd, Info);

        // ① 编队真的存下来了
        var team = h.Player.Formations.FirstOrDefault(f => f.Id == 1);
        Assert.NotNull(team);
        Assert.Equal(2, team!.Slots.Count);
        Assert.Equal(11u, team.Slots[0].MainCardGuid);
        Assert.Equal(12u, team.Slots[1].MainCardGuid);

        // ② ★ 回执的 sCmd 必须与上行一致（回成 21 会让客户端刷错界面）
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(cmd, root["sCmd"]!.GetValue<int>());

        // ③ LuaCall 数字命令族：成功回包**绝不能带 nError**（AGENTS.md §5 的坑，
        //    客户端 `if tbParam.nError then` 是真值判断，0 也是真）
        var tbParam = Assert.IsType<JsonObject>(root["tbParam"]);
        Assert.False(tbParam.ContainsKey("nError"));
        Assert.Equal(0, tbParam["ret"]!.GetValue<int>());
    }

    /// <summary>22/23 与 21 落到同一份编队数据上（原服是同一个 <c>FormationLogic:UpdateFormation</c>）。</summary>
    [Fact]
    public async Task Update_SharesSameStorageAsCmd21()
    {
        using var h = await FeatureHarness.CreateAsync("formx-shared");

        // 先用 21 存一次
        await h.DispatchLuaCallAsync(FormationHandler.UpdateCmd, Info);
        Assert.Equal(2, h.Player.Formations.First(f => f.Id == 1).Slots.Count);

        // 再用 23 存一个只有 1 个槽的版本 —— 应该整体覆盖（Slots.Clear 后重填）
        await h.DispatchLuaCallAsync(FormationExtraFeature.ChooseGirlUpdateCmd,
            """{"Id":1,"Info":[{"MainCard":99,"UsedCard":99,"Secondarycard":[],"WeaponId":0,"Rune":[0,0,0,0]}]}""");

        var team = h.Player.Formations.First(f => f.Id == 1);
        Assert.Single(team.Slots);
        Assert.Equal(99u, team.Slots[0].MainCardGuid);
        // 没有多建一个编队
        Assert.Single(h.Player.Formations, f => f.Id == 1);
    }

    /// <summary>未登录 / 畸形参数不抛异常。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Id":"x"}""")]
    [InlineData("null")]
    public async Task Update_MalformedParam_DoesNotThrow(string json)
    {
        using var h = await FeatureHarness.CreateAsync($"formx-bad-{json.Length}");
        await h.DispatchLuaCallAsync(FormationExtraFeature.ChooseGirlUpdateCmd, json);
    }

    /// <summary>
    /// ★ 编队更新必须推 **pf 1033 `FormationInfoUpdateNtf`**，让客户端
    /// <c>Game.EquipManager</c> 的「服务器版本」与本地一致 —— 不推的话下次出击
    /// 客户端会认为本地更新，再发一遍 21/22/23。
    ///
    /// 这条同时是 <see cref="FeatureHarness.PushedPf"/> 的自验：那个字段本轮才从
    /// 「死字段」改成真正记录（从 loopback 排包解析 16 字节头的 pf），
    /// 如果解析逻辑写错，这里会立刻失败。
    /// </summary>
    [Theory]
    [InlineData(FormationExtraFeature.ChooseEquipUpdateCmd)]
    [InlineData(FormationExtraFeature.ChooseGirlUpdateCmd)]
    public async Task Update_PushesFormationInfoUpdateNtf(int cmd)
    {
        using var h = await FeatureHarness.CreateAsync($"formx-pf-{cmd}");

        await h.DispatchLuaCallAsync(cmd, Info);

        // ★ 用 WaitForPfAsync 而不是裸读 PushedPf：Push 是异步写 socket，
        // handler 返回时字节未必已到达 loopback 另一端（实测会 flaky）。
        Assert.True(await h.WaitForPfAsync((int)GamePf.FormationInfoUpdateNtf),
            $"没等到 pf 1033，实际推了：[{string.Join(",", h.PushedPf)}]");
    }

    /// <summary>命令号与 <c>LuaCall.lua</c> 的定义一致（:27 / :28）。</summary>
    [Fact]
    public void CommandIds_MatchLuaCallDefinition()
    {
        Assert.Equal(22, FormationExtraFeature.ChooseEquipUpdateCmd);
        Assert.Equal(23, FormationExtraFeature.ChooseGirlUpdateCmd);
        Assert.Equal(21, FormationHandler.UpdateCmd);
    }
}
