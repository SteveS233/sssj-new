using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class HouseComfortTests
{
    [Fact]
    public async Task CafeDownloadRecalculatesAfterInventoryRemoval()
    {
        using var h = await FeatureHarness.CreateAsync("comfort-download");
        var row = Table.Items.Values.First(r => r.Type == 104 && r.AddScore > 0 && !r.IsSystem);
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(1, 123, 9999));
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = [Item(1000, row.Id)], NextObjId = 1001 });
        await h.DispatchLuaCallAsync(112, "{}");
        Assert.Equal(row.AddScore, CafeFeature.ReadState(h.Player).Comfort);
        Assert.Equal(123, CafeFeature.ReadState(h.Player).Hot);
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = [], NextObjId = 1001 });
        await h.DispatchLuaCallAsync(112, "{}");
        Assert.Equal(0, CafeFeature.ReadState(h.Player).Comfort);
    }

    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);
    private static HouseFurniture Item(uint id, int template, int room = 0, uint parent = 0)
        => new(id, template, room, parent, 0, 0, 0, 0, 0);

    [Fact]
    public void WarehouseAndPlacedInstancesUseTheSameRanking()
    {
        var row = Table.Items.Values.First(r => r.Type == 104 && r.AddScore > 0 && !r.IsSystem);
        var stored = new HouseState { Furniture = [Item(1000, row.Id)], NextObjId = 1001 };
        var placed = new HouseState { Furniture = [Item(1000, row.Id, 1, 2)], NextObjId = 1001 };
        Assert.Equal(row.AddScore, HouseComfort.Calculate(Table, stored, 1, int.MaxValue));
        Assert.Equal(HouseComfort.Calculate(Table, stored, 1, int.MaxValue),
            HouseComfort.Calculate(Table, placed, 1, int.MaxValue));
    }

    [Fact]
    public void HighestScoresFillLevelDependentSlotsAndSalePromotesTheNextItem()
    {
        var rows = Table.Items.Values.Where(r => r.Type == 104 && r.AddScore > 0 && !r.IsSystem)
            .OrderByDescending(r => r.AddScore).ToArray();
        var high = rows.First(); var low = rows.Last();
        Assert.True(high.AddScore > low.AddScore);
        int slots = Table.ItemTypes.Values.Where(r => r.Types.Contains(104)).Sum(r => r.Limit[0]);
        var items = Enumerable.Range(0, slots).Select(i => Item((uint)(1000 + i), high.Id)).ToList();
        items.Add(Item(2000, low.Id));
        var state = new HouseState { Furniture = items, NextObjId = 2001 };
        Assert.Equal(slots * high.AddScore, HouseComfort.Calculate(Table, state, 1, int.MaxValue));
        items.RemoveAt(0);
        Assert.Equal((slots - 1) * high.AddScore + low.AddScore,
            HouseComfort.Calculate(Table, state, 1, int.MaxValue));
        Assert.Equal(7, HouseComfort.Calculate(Table, state, 1, 7));
    }

    [Fact]
    public void DecorSubtypesShareOneRankingPool()
    {
        var floor = Table.Items.Values.First(r => r.Type == 50 && r.AddScore > 0 && !r.IsSystem);
        int[] decor = [50, 51, 55, 56, 58, 60, 61, 62, 64, 80, 83, 81, 84, 73];
        int slots = Table.ItemTypes.Values.Where(r => r.Types.Any(decor.Contains)).Sum(r => r.Limit[0]);
        var state = new HouseState { Furniture = Enumerable.Range(0, slots + 2)
            .Select(i => Item((uint)(1000 + i), floor.Id)).ToList(), NextObjId = 2000 };
        Assert.Equal(slots * floor.AddScore, HouseComfort.Calculate(Table, state, 1, int.MaxValue));
    }
}
