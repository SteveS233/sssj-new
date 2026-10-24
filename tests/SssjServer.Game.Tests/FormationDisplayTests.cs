using SssjServer.Game;
using SssjServer.Game.Handlers;
using System.Threading.Tasks;
using Xunit;

namespace SssjServer.Game.Tests;

public class FormationDisplayTests
{
    [Fact]
    public async Task RenameAndEquip_SurviveReloadFromDisk()
    {
        using var h = await FeatureHarness.CreateAsync("formation-reload");
        var weapon = h.Player.AddItem(2, 1, 2, 2, 1);
        await h.DispatchLuaCallAsync(FormationHandler.UpdateCmd,
            "{\"Id\":1,\"Info\":[{\"MainCard\":1,\"UsedCard\":1,\"WeaponId\":" + weapon.Guid + ",\"Rune\":[0,0,0,0]}]}");
        await h.DispatchLuaCallAsync(FormationHandler.ChangeNameCmd,
            "{\"Id\":1,\"Name\":\"验证队伍\"}");
        var reloaded = new PlayerStore(h.Options).Load(h.Player.Account);
        Assert.NotNull(reloaded);
        var team = Assert.Single(reloaded!.Formations);
        Assert.Equal("验证队伍", team.Title);
        Assert.Equal(weapon.Guid, Assert.Single(team.Slots).WeaponGuid);
    }

    [Theory]
    [InlineData("", "队伍 1")]
    [InlineData("  ", "队伍 1")]
    [InlineData("我的队伍", "我的队伍")]
    public void WireTitle_UsesDefaultOnlyForBlankNames(string title, string expected)
    {
        var formation = new Formation { Id = 1, Title = title };
        Assert.Equal(expected, FormationHandler.BuildFormationInfo(formation).Title);
        Assert.Equal(title, formation.Title);
    }
}
