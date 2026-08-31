using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 起始角色卡 + 编队（StarterLoadout / Player 背包 / BuildPlayerNtf / BuildItemNtf）相关测试。
/// </summary>
public class StarterLoadoutTests : IDisposable
{
    private readonly string _dataDir;

    public StarterLoadoutTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sssj-starter-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* best effort */ }
    }

    private PlayerStore NewStore(int sid = 7) => new(new ServerOptions { DataDirectory = _dataDir, Sid = sid });

    // ---- 1) 背包 guid 唯一且单调 ----
    [Fact]
    public void AddItem_AssignsUniqueMonotonicGuids()
    {
        var p = new Player("p", World.DeriveRoleId("p"));
        // 默认从 1 开始
        var a = p.AddItem(genre: 1, detail: 1, particular: 1, level: 3, count: 1);
        var b = p.AddItem(genre: 1, detail: 2, particular: 1, level: 3, count: 1);
        var c = p.AddItem(genre: 1, detail: 3, particular: 1, level: 3, count: 1);

        Assert.Equal(1u, a.Guid);
        Assert.Equal(2u, b.Guid);
        Assert.Equal(3u, c.Guid);
        Assert.Equal(3, p.Items.Count);
        // 唯一且单调
        var guids = p.Items.Select(i => i.Guid).ToList();
        Assert.Equal(guids.Count, guids.Distinct().Count());
        for (int i = 1; i < guids.Count; i++)
            Assert.True(guids[i] > guids[i - 1]);
        // NextGuid 已推进；再分配继续 +1
        Assert.Equal(4u, p.AllocateGuid());
        Assert.Equal(5u, p.NextGuid);
    }

    [Fact]
    public void AddItem_SetsKnownFields()
    {
        var p = new Player("p", World.DeriveRoleId("p"));
        var it = p.AddItem(genre: 1, detail: 9, particular: 4, level: 3, count: 1);
        Assert.Equal(1u, it.Genre);
        Assert.Equal(9u, it.Detail);
        Assert.Equal(4u, it.Particular);
        Assert.Equal(3u, it.Level);
        Assert.Equal(1u, it.Count);
        Assert.True(it.CreateTime > 0);
        Assert.Equal(it, p.GetItem(it.Guid));
    }

    // ---- 2) PlayerStore 往返 items + formation ----
    [Fact]
    public void Store_RoundTrips_ItemsAndFormation()
    {
        var store = NewStore();
        var p = new Player("tester2", World.DeriveRoleId("tester2")) { Name = "二号" };
        StarterLoadout.Grant(p); // 4 张卡 + 1 号编队

        store.Save(p);
        var loaded = store.Load("tester2");

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Items.Count);
        Assert.Equal(4, loaded.Girls.Count);
        // guid 与 NextGuid 往返一致
        Assert.Equal(4, loaded.Items.Select(i => i.Guid).Distinct().Count());
        Assert.Equal(p.NextGuid, loaded.NextGuid);
        // item 字段往返
        var first = loaded.Items[0];
        Assert.Equal(1u, first.Genre);
        Assert.True(first.CreateTime > 0);
        // 编队槽引用都能落在卡片 item 上（genre==1）
        // ★ 槽数是 3 而不是 4（赠卡 4 张，第 4 张留背包当备用）——
        //   客户端 EquipSolution 恒有 3 个 CardGroup，见 StarterLoadout.MaxFormationSlots
        var fmt = Assert.Single(loaded.Formations);
        Assert.Equal(1u, fmt.Id);
        Assert.Equal(StarterLoadout.MaxFormationSlots, fmt.Slots.Count);
        foreach (var slot in fmt.Slots)
        {
            var item = loaded.GetItem(slot.MainCardGuid);
            Assert.NotNull(item);
            Assert.Equal(1u, item!.Genre);
            Assert.Equal(slot.UsedCardGuid, item.Guid);
        }
    }

    // ---- 3) BuildPlayerNtf 的 girllist / formationlist 与背包一致 ----
    [Fact]
    public void BuildPlayerNtf_EmitsGirllistAndFormationlist_ConsistentWithInventory()
    {
        var p = new Player("acc_ntf", World.DeriveRoleId("acc_ntf")) { Name = "编队测试" };
        StarterLoadout.Grant(p);

        var module = MakeModule();
        var ntf = module.BuildPlayerNtf(p);
        var bytes = ntf.ToByteArray();
        var parsed = ProtoMessage.Parse<PlayerNtf>(bytes);

        // girllist：每个角色卡对应一个 Girl（girlid == item.detail）
        Assert.True(parsed.HasGirllist);
        Assert.Equal(p.Girls.Count, parsed.Girllist!.Girls.Count);
        foreach (var g in parsed.Girllist.Girls)
            Assert.Contains(p.Girls, x => x.Girlid == g.Girlid);

        // formationlist：每个 maincardguid 都解析到一张 genre==1 的卡
        // ★ fightcard 数 = 3（客户端上限），**不是** p.Items.Count(=4)
        var fmt = Assert.Single(parsed.Formationlist);
        Assert.Equal(1u, fmt.Id);
        Assert.Equal(StarterLoadout.MaxFormationSlots, fmt.Fightcard.Count);
        foreach (var fc in fmt.Fightcard)
        {
            var item = p.GetItem(fc.Maincardguid);
            Assert.NotNull(item);
            Assert.Equal(1u, item!.Genre);
            Assert.Equal(fc.Usedcardguid, item.Guid);
        }
    }

    // ---- 3b) ★★★ 回归：编队槽数绝不能超过 3（否则新号首登 pf 1104 整条中断） ----
    /// <summary>
    /// 客户端 <c>Game.EquipSolution$$.ctor</c>（RVA <c>0x1D92498</c>）末句
    /// <c>mov w1, #3</c> + <c>b SetGroupSize</c> ⇒ 恒 3 个 <c>CardGroup</c>；
    /// <c>GetGroup</c>（<c>0x1D92534</c>）越界**返回 null 不抛**，
    /// 而 <c>AddEquipSolution</c>（<c>0x1D91FF8</c>）在 <c>0x1D92270 cbnz x22</c>
    /// 对该 null **抛 NRE** ⇒ <c>InitItem</c>(pf 1104 背包全量) 整条中断。
    ///
    /// <para>★ 真机症状：<c>Failed to process message. PF : 1104</c>，
    /// 且**只有新账号首登**触发（老存档的编队已被客户端回写成 3 槽）。</para>
    /// </summary>
    [Fact]
    public void 起始编队槽数_不超过客户端CardGroup上限3()
    {
        var p = new Player("acc_slots", World.DeriveRoleId("acc_slots"));
        StarterLoadout.Grant(p);

        // 赠卡仍是 4 张（背包里 4 张，第 4 张是备用卡）
        Assert.Equal(StarterLoadout.SquadSize, p.Items.Count);

        var fmt = Assert.Single(p.Formations);
        Assert.Equal(3, StarterLoadout.MaxFormationSlots);
        Assert.Equal(StarterLoadout.MaxFormationSlots, fmt.Slots.Count);
        Assert.True(fmt.Slots.Count <= 3, "编队槽数超过 3 会让客户端 pf 1104 抛 NRE");

        // 下发到线上的 FormationInfo 同样不能超过 3
        var info = Handlers.FormationHandler.BuildFormationInfo(fmt);
        Assert.Equal(StarterLoadout.MaxFormationSlots, info.Fightcard.Count);

        // 每个槽都指向真实存在的角色卡（避免 GetCharacterCard 拿到 null 后的连带问题）
        foreach (var slot in fmt.Slots)
            Assert.NotNull(p.GetItem(slot.MainCardGuid));
    }

    // ---- 4) ItemNtf 携带全部 item，itemnum 一致 ----
    [Fact]
    public void BuildItemNtf_ContainsAllItems_WithMatchingItemnum()
    {
        var p = new Player("acc_item", World.DeriveRoleId("acc_item"));
        StarterLoadout.Grant(p);

        var module = MakeModule();
        var ntf = module.BuildItemNtf(p);
        var bytes = ntf.ToByteArray();
        var parsed = ProtoMessage.Parse<ItemNtf>(bytes);

        Assert.Equal(p.Items.Count, parsed.Itemnum);
        Assert.Equal(p.Items.Count, parsed.Items.Count);
        // 每一项 guid/genre 往返一致
        foreach (var it in parsed.Items)
        {
            var src = p.GetItem(it.Guid);
            Assert.NotNull(src);
            Assert.Equal(src!.Genre, it.Genre);
        }
    }

    // ---- 5) --grant-starter false：NewPlayer 不发放任何卡/编队 ----
    [Fact]
    public void GrantStarterFalse_GrantsNothing()
    {
        var store = NewStore();
        var world = new World(store, skipTutorial: true, grantStarter: false);
        var p = world.GetOrCreatePlayer("acc_nostarter");

        Assert.Empty(p.Items);
        Assert.Empty(p.Girls);
        Assert.Empty(p.Formations);
        // 仍应有默认 NextGuid=1（无分配）
        Assert.Equal(1u, p.NextGuid);
    }

    // ---- 辅助：构造带临时数据目录的 GameModule ----
    private GameModule MakeModule() => new(new ServerOptions { DataDirectory = _dataDir, Aid = 1, Sid = 7 });
}
