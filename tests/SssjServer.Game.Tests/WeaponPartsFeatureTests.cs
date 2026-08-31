using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 武器零件装载 / 方案改名（LuaCall 350 / 348）。
/// 取证 `work/weapon-genre-forensics.md` §C.6 / §D.3、台账 `docs/05` §30。
///
/// 断言重点是这一域**最容易错的三条**：
/// <list type="number">
///   <item>★★★ <c>weaponinlay</c> 与 <c>ownguid</c> 是**双向冗余**，只写一边
///     ⇒ 界面一边显示装着、一边空闲；</item>
///   <item>★★ 槽位由零件的 <c>Detail</c> 决定（1 枪管 / 2 弹匣 / 3 瞄准仪 / 4 握把），
///     上行没有槽位字段；</item>
///   <item>★★ 348 回包必须是**整张字典**（客户端 <c>self.tbPlanName = tbParam</c> 全量替换）。</item>
/// </list>
/// </summary>
public class WeaponPartsFeatureTests
{
    private static JsonObject Reply(FeatureHarness h, int cmd)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(cmd, root["sCmd"]!.GetValue<int>());
        return Assert.IsType<JsonObject>(root["tbParam"]);
    }

    private static Item SeedWeapon(FeatureHarness h, uint guid)
    {
        var w = new Item
        {
            Guid = guid, Genre = WeaponTable.WeaponGenre,
            Detail = 1, Particular = 2, Level = 2, Count = 1, EnhanceLevel = 1,
        };
        h.Player.Items.Add(w);
        return w;
    }

    /// <summary>造一个零件。<paramref name="slot"/> 就是 <c>Detail</c>（零件位）。</summary>
    private static Item SeedParts(FeatureHarness h, uint guid, uint slot = WeaponPartsFeature.SlotBarrel)
    {
        var p = new Item
        {
            Guid = guid, Genre = WeaponPartsFeature.PartsGenre,
            Detail = slot, Particular = 1, Level = 3, Count = 1,
        };
        h.Player.Items.Add(p);
        return p;
    }

    private static Task LoadAsync(FeatureHarness h, uint weapon, uint parts, bool load)
        => h.DispatchLuaCallAsync(WeaponPartsFeature.CmdLoad,
            $"{{\"nWeaponGuid\":{weapon},\"nPartsGuid\":{parts},\"bLoad\":{(load ? "true" : "false")}}}");

    // ================= 350 装载 =================

    [Fact]
    public async Task 装载_两边同时写()
    {
        using var h = await FeatureHarness.CreateAsync("parts-load");
        var w = SeedWeapon(h, 5001);
        var p = SeedParts(h, 6001, WeaponPartsFeature.SlotBarrel);

        await LoadAsync(h, 5001, 6001, true);

        // ★★★ 双向冗余：武器侧 inlay + 零件侧 ownguid 都要写
        Assert.NotNull(w.Inlay);
        Assert.Equal(6001u, w.Inlay!.Barrel);
        Assert.Equal(5001u, p.OwnGuid);
    }

    [Theory]
    [InlineData(WeaponPartsFeature.SlotBarrel)]
    [InlineData(WeaponPartsFeature.SlotAmmo)]
    [InlineData(WeaponPartsFeature.SlotSight)]
    [InlineData(WeaponPartsFeature.SlotGrip)]
    public async Task 装载_槽位由零件Detail决定(uint slot)
    {
        using var h = await FeatureHarness.CreateAsync($"parts-slot-{slot}");
        var w = SeedWeapon(h, 5001);
        SeedParts(h, 6001, slot);

        // ★ 上行没有槽位字段 —— 服务端必须自己按 Detail 推
        await LoadAsync(h, 5001, 6001, true);

        Assert.NotNull(w.Inlay);
        uint[] slots = { w.Inlay!.Barrel, w.Inlay.Ammo, w.Inlay.Sight, w.Inlay.Grip };
        // 只有对应那一个槽被填上，其余仍是 0（空槽哨兵）
        for (uint i = 0; i < 4; i++)
            Assert.Equal(i + 1 == slot ? 6001u : 0u, slots[i]);
    }

    [Fact]
    public async Task 装载_同槽换件时旧件被卸下()
    {
        using var h = await FeatureHarness.CreateAsync("parts-swap");
        var w = SeedWeapon(h, 5001);
        var oldP = SeedParts(h, 6001, WeaponPartsFeature.SlotBarrel);
        var newP = SeedParts(h, 6002, WeaponPartsFeature.SlotBarrel);

        await LoadAsync(h, 5001, 6001, true);
        await LoadAsync(h, 5001, 6002, true);

        // 新件装上、旧件的 ownguid 必须清 0（否则「两个零件都说自己在同一个槽」）
        Assert.Equal(6002u, w.Inlay!.Barrel);
        Assert.Equal(5001u, newP.OwnGuid);
        Assert.Equal(0u, oldP.OwnGuid);
    }

    [Fact]
    public async Task 装载_一个零件不能同时装两把武器()
    {
        using var h = await FeatureHarness.CreateAsync("parts-exclusive");
        var w1 = SeedWeapon(h, 5001);
        var w2 = SeedWeapon(h, 5002);
        var p = SeedParts(h, 6001, WeaponPartsFeature.SlotSight);

        await LoadAsync(h, 5001, 6001, true);
        await LoadAsync(h, 5002, 6001, true);

        // ★ 装到第二把上时，第一把的 inlay 必须被摘掉
        Assert.Equal(0u, w1.Inlay!.Sight);
        Assert.Equal(6001u, w2.Inlay!.Sight);
        Assert.Equal(5002u, p.OwnGuid);
    }

    [Fact]
    public async Task 卸下_两边都清()
    {
        using var h = await FeatureHarness.CreateAsync("parts-unload");
        var w = SeedWeapon(h, 5001);
        var p = SeedParts(h, 6001, WeaponPartsFeature.SlotAmmo);

        await LoadAsync(h, 5001, 6001, true);
        await LoadAsync(h, 5001, 6001, false);

        Assert.Equal(0u, w.Inlay!.Ammo);
        Assert.Equal(0u, p.OwnGuid);
    }

    [Fact]
    public async Task 卸下_不会误清别的武器的槽()
    {
        using var h = await FeatureHarness.CreateAsync("parts-unload-safe");
        var w1 = SeedWeapon(h, 5001);
        var w2 = SeedWeapon(h, 5002);
        var p = SeedParts(h, 6001, WeaponPartsFeature.SlotGrip);

        await LoadAsync(h, 5002, 6001, true);       // 装在 w2 上
        await LoadAsync(h, 5001, 6001, false);      // 却对 w1 发卸下

        // ★ w2 的槽与零件的 ownguid 都不该被动（只在「确实装在这把的这个槽」时才清）
        Assert.Equal(6001u, w2.Inlay!.Grip);
        Assert.Equal(5002u, p.OwnGuid);
    }

    [Fact]
    public async Task 装载_回包原样回显三字段()
    {
        using var h = await FeatureHarness.CreateAsync("parts-echo");
        SeedWeapon(h, 5001);
        SeedParts(h, 6001);

        await LoadAsync(h, 5001, 6001, true);

        var r = Reply(h, WeaponPartsFeature.CmdLoad);
        Assert.Equal(5001, r["nWeaponGuid"]!.GetValue<int>());
        Assert.Equal(6001, r["nPartsGuid"]!.GetValue<int>());
        Assert.True(r["bLoad"]!.GetValue<bool>());
        // ★ LuaCall 数字族：成功绝不带 nError
        Assert.False(r.ContainsKey("nError"));
    }

    [Fact]
    public async Task 装载_推pf1031让界面能刷新()
    {
        using var h = await FeatureHarness.CreateAsync("parts-push");
        SeedWeapon(h, 5001);
        SeedParts(h, 6001);

        await LoadAsync(h, 5001, 6001, true);

        // ★ 客户端 RefreshRefigPnl 只用 nPartsGuid、其余从本地缓存重读 ⇒ 必须先推 1031
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf),
            "装载后没有推 pf 1031 —— 客户端界面不会更新");
    }

    [Theory]
    [InlineData(9999, 6001)]      // 武器不存在
    [InlineData(5001, 9999)]      // 零件不存在
    public async Task 装载_非法guid回错误码(uint weapon, uint parts)
    {
        using var h = await FeatureHarness.CreateAsync($"parts-bad-{weapon}-{parts}");
        SeedWeapon(h, 5001);
        SeedParts(h, 6001);

        await LoadAsync(h, weapon, parts, true);

        Assert.True(Reply(h, WeaponPartsFeature.CmdLoad).ContainsKey("nError"));
    }

    [Fact]
    public async Task 装载_零件位非法被拒()
    {
        using var h = await FeatureHarness.CreateAsync("parts-badslot");
        SeedWeapon(h, 5001);
        SeedParts(h, 6001, slot: 9);      // Detail=9 不是 1..4

        await LoadAsync(h, 5001, 6001, true);

        Assert.True(Reply(h, WeaponPartsFeature.CmdLoad).ContainsKey("nError"));
    }

    [Fact]
    public async Task 装载_把角色卡当零件会被拒()
    {
        using var h = await FeatureHarness.CreateAsync("parts-wronggenre");
        SeedWeapon(h, 5001);
        // genre 1 是角色卡，不是零件
        h.Player.Items.Add(new Item { Guid = 6001, Genre = 1, Detail = 1, Count = 1 });

        await LoadAsync(h, 5001, 6001, true);

        Assert.True(Reply(h, WeaponPartsFeature.CmdLoad).ContainsKey("nError"));
    }

    [Fact]
    public async Task 装载_bLoad用0和1也能识别()
    {
        using var h = await FeatureHarness.CreateAsync("parts-boolnum");
        var w = SeedWeapon(h, 5001);
        SeedParts(h, 6001);

        // ★ Lua 的布尔经 JSON 过来可能是 bool 也可能是 0/1
        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdLoad,
            "{\"nWeaponGuid\":5001,\"nPartsGuid\":6001,\"bLoad\":1}");

        Assert.Equal(6001u, w.Inlay!.Barrel);
    }

    [Fact]
    public async Task 装载_上行是数组时不抛()
    {
        using var h = await FeatureHarness.CreateAsync("parts-array");
        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdLoad, "[]");
    }

    // ================= 348 方案改名 =================

    [Fact]
    public async Task 改名_回整张字典()
    {
        using var h = await FeatureHarness.CreateAsync("parts-name");

        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName,
            "{\"id\":1,\"name\":\"输出\"}");
        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName,
            "{\"id\":5,\"name\":\"坦克\"}");

        // ★★ 客户端 self.tbPlanName = tbParam 是**全量替换**
        // ⇒ 第二次回包必须同时含方案 1 与 5，否则方案 1 的名字会被清空
        var r = Reply(h, WeaponPartsFeature.CmdChangePlanName);
        Assert.Equal("输出", r["1"]!.ToString());
        Assert.Equal("坦克", r["5"]!.ToString());
    }

    [Fact]
    public async Task 改名_持久化到存档()
    {
        using var h = await FeatureHarness.CreateAsync("parts-name-save");

        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName,
            "{\"id\":3,\"name\":\"爆发\"}");

        var names = WeaponPartsFeature.LoadPlanNames(h.Player);
        Assert.Equal("爆发", names["3"]);
    }

    [Fact]
    public async Task 改名_覆盖同一个方案()
    {
        using var h = await FeatureHarness.CreateAsync("parts-name-over");

        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName, "{\"id\":2,\"name\":\"A\"}");
        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName, "{\"id\":2,\"name\":\"B\"}");

        var r = Reply(h, WeaponPartsFeature.CmdChangePlanName);
        Assert.Equal("B", r["2"]!.ToString());
        Assert.Single(WeaponPartsFeature.LoadPlanNames(h.Player));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(-1)]
    public async Task 改名_id越界被拒(int id)
    {
        using var h = await FeatureHarness.CreateAsync($"parts-name-bad-{id}");

        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName,
            $"{{\"id\":{id},\"name\":\"x\"}}");

        Assert.True(Reply(h, WeaponPartsFeature.CmdChangePlanName).ContainsKey("nError"));
        Assert.Empty(WeaponPartsFeature.LoadPlanNames(h.Player));
    }

    [Fact]
    public async Task 改名_超长名字被截断()
    {
        using var h = await FeatureHarness.CreateAsync("parts-name-long");
        string longName = new string('长', 100);

        await h.DispatchLuaCallAsync(WeaponPartsFeature.CmdChangePlanName,
            $"{{\"id\":1,\"name\":\"{longName}\"}}");

        var names = WeaponPartsFeature.LoadPlanNames(h.Player);
        Assert.Equal(WeaponPartsFeature.MaxPlanNameLength, names["1"].Length);
    }

    // ================= 常量 =================

    [Fact]
    public void 零件位常量与配置表表头一致()
    {
        // `WeaponParts.txt` 表头第 2 行：细分D(零件位 1枪管2弹匣3瞄准仪4握把)
        Assert.Equal(1u, WeaponPartsFeature.SlotBarrel);
        Assert.Equal(2u, WeaponPartsFeature.SlotAmmo);
        Assert.Equal(3u, WeaponPartsFeature.SlotSight);
        Assert.Equal(4u, WeaponPartsFeature.SlotGrip);
        Assert.Equal("枪管", WeaponPartsFeature.SlotName(1));
        Assert.Equal("握把", WeaponPartsFeature.SlotName(4));
        Assert.Equal(4u, WeaponPartsFeature.PartsGenre);
    }
}
