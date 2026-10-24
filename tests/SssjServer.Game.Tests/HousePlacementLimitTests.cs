using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

public class HousePlacementLimitTests
{
    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);
    private static HouseFurniture Furniture(uint id, int template, int room = 1, uint parent = 2)
        => new(id, template, room, parent, 4, 5, 0, 1, 0);
    private static Player Seed(int level, params HouseFurniture[] items)
    {
        var player = new Player("placement-limit", 77);
        CafeFeature.WriteState(player, new CafeFeature.CafeState(level, 0, 0));
        HouseFeature.WriteState(player, new HouseState { Furniture = items.ToList(), NextObjId = 2000 });
        return player;
    }
    private static furniture Move(HouseFurniture f, uint room, uint parent) => new()
    {
        ObjId = f.ObjId, TemplateId = (uint)f.TemplateId, RoomId = room, ParentId = parent,
        Pose = new objPose { TemplateId = (uint)f.TemplateId, ParentTemplateId = parent,
            PosX = 9, PosY = 10, Rotate = 90 },
    };
    private static async Task<UpdateHouseRsp> Save(Player player, params furniture[] changes)
    {
        var req = new UpdateHouseReq { Roleid = player.Roleid };
        req.Furnitures.AddRange(changes);
        var fake = new FakeSession { Tag = player };
        await new HouseFeature(Table, TestPaths.SettingDir).HandleUpdateHouse(fake,
            new Packet { Pf = Pf.ClickUpdateHouseReq, Serial = 95, Payload = req.ToByteArray() });
        return Assert.IsType<UpdateHouseRsp>(fake.LastOf(Pf.UpdateHouseRsp).Message);
    }

    [Theory]
    [InlineData(1, 2, 10, false)]
    [InlineData(2, 2, 10, true)]
    [InlineData(7, 6, 30, false)]
    [InlineData(8, 6, 30, true)]
    public async Task RoomOpeningUsesOriginalLevelFlags(int level, int room, uint parent, bool accepted)
    {
        var f = Furniture(1000, 703, 0, 0);
        var player = Seed(level, f);
        var reply = await Save(player, Move(f, (uint)room, parent));
        var actual = HouseFeature.ReadOrCreate(player, Table).Furniture.Single();
        Assert.Equal(accepted ? room : 0, actual.RoomId);
        Assert.Equal((uint)actual.RoomId, Assert.Single(reply.NewFurnitures).RoomId);
    }

    [Theory]
    [InlineData(1, 6, true)]
    [InlineData(1, 7, false)]
    [InlineData(3, 7, true)]
    public async Task SeatCapacityRespectsLevelBoundary(int level, int count, bool accepted)
    {
        var items = Enumerable.Range(0, count).Select(i => Furniture((uint)(1000 + i), 101, 0, 0)).ToArray();
        var player = Seed(level, items);
        await Save(player, items.Select(f => Move(f, 1, 2)).ToArray());
        var state = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(accepted ? count : 0, state.Furniture.Count(f => f.RoomId == 1));
        Assert.Equal(count, state.Furniture.Count);
    }

    [Fact]
    public async Task MultiSeatTablesCountPointsRatherThanFurnitureInstancesOrSavedBizcount()
    {
        Assert.Equal(2, Table.Items[102].BizPointCount);
        var items = Enumerable.Range(0, 4).Select(i => Furniture((uint)(1000 + i), 102, 0, 0)).ToArray();
        var player = Seed(1, items);
        await Save(player, items.Select(f => Move(f, 1, 2)).ToArray());
        Assert.All(HouseFeature.ReadOrCreate(player, Table).Furniture, f => Assert.Equal(0, f.RoomId));
    }

    [Fact]
    public async Task SmallFurnitureUsesIndependentCategoryCapacity()
    {
        Assert.Equal(104, Table.Items[501].Type);
        var items = Enumerable.Range(0, 5).Select(i => Furniture((uint)(1000 + i), 501, 0, 0)).ToArray();
        var player = Seed(1, items);
        await Save(player, items.Select(f => Move(f, 1, 2)).ToArray());
        Assert.All(HouseFeature.ReadOrCreate(player, Table).Furniture, f => Assert.Equal(0, f.RoomId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingFurnitureAtCapacityDoesNotDependOnBatchOrder(bool reverse)
    {
        var items = Enumerable.Range(0, 6).Select(i => Furniture((uint)(1000 + i), 101)).ToList();
        var stored = Furniture(1006, 101, 0, 0);
        items.Add(stored);
        var player = Seed(1, items.ToArray());
        furniture[] changes = [Move(items[0], 0, 0), Move(stored, 1, 2)];
        await Save(player, reverse ? changes.Reverse().ToArray() : changes);
        var state = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(6, state.Furniture.Count(f => f.RoomId == 1));
        Assert.Equal(0, state.Furniture.Single(f => f.ObjId == 1000).RoomId);
        Assert.Equal(1, state.Furniture.Single(f => f.ObjId == 1006).RoomId);
    }

    [Fact]
    public async Task LegacyExcessCanMoveOrDecreaseWithoutDeletingOwnedFurniture()
    {
        var items = Enumerable.Range(0, 7).Select(i => Furniture((uint)(1000 + i), 101)).ToArray();
        var player = Seed(1, items);
        await Save(player, Move(items[0], 1, 2));
        Assert.Equal(9u, HouseFeature.ReadOrCreate(player, Table).Furniture[0].PosX);
        await Save(player, Move(items[0], 0, 0));
        var state = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(7, state.Furniture.Count);
        Assert.Equal(6, state.Furniture.Count(f => f.RoomId == 1));
    }

    [Fact]
    public async Task ClosedRoomLegacyFurnitureCanStillBeStored()
    {
        var f = Furniture(1000, 703, 6, 30);
        var player = Seed(1, f);
        await Save(player, Move(f, 0, 0));
        Assert.Equal(0, HouseFeature.ReadOrCreate(player, Table).Furniture.Single().RoomId);
    }
}
