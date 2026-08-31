using SssjServer.Core;
using SssjServer.Game;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// PlayerStore 存档测试：save/load 往返、缺失文件、损坏文件容错。
/// </summary>
public class PlayerStoreTests : IDisposable
{
    private readonly string _dir;

    public PlayerStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sssj-store-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private PlayerStore NewStore() => new(new ServerOptions { DataDirectory = _dir, Sid = 9 });

    [Fact]
    public void Save_Load_RoundTrips()
    {
        var store = NewStore();
        var p = new Player("tester", World.DeriveRoleId("tester"))
        {
            Name = "测试员",
            Level = 15,
            Exp = 900,
            Fightpower = 12345,
            RegisterTime = 111111,
            StartTime = 222222,
            ServerZone = 9,
            HeadFrame = 4,
            ChatChannelId = 5,
            ClubId = 77,
        };
        p.AddMoney(1, 500);
        p.Money[2] = 250;
        p.Girls.Add(new Girl { Girlid = 42, Level = 3, Exp = 0, Modelid = 8, Moodvalue = 95, Vigor = 0, Flag = 0 });
        p.AddItem(genre: 1, detail: 42, particular: 1, level: 3, count: 1);
        p.Formations.Add(new Formation { Id = 1, Title = "" });
        p.Formations[0].Slots.Add(new FormationCardSlot { MainCardGuid = p.Items[0].Guid, UsedCardGuid = p.Items[0].Guid });

        store.Save(p);

        var loaded = store.Load("tester");
        Assert.NotNull(loaded);
        Assert.Equal("tester", loaded!.Account);
        Assert.Equal(World.DeriveRoleId("tester"), loaded.Roleid);
        Assert.Equal("测试员", loaded.Name);
        Assert.Equal(15, loaded.Level);
        Assert.Equal(900, loaded.Exp);
        Assert.Equal(12345, loaded.Fightpower);
        Assert.Equal(111111UL, loaded.RegisterTime);
        Assert.Equal(9, loaded.ServerZone);
        Assert.Equal(4, loaded.HeadFrame);
        Assert.Equal(5u, loaded.ChatChannelId);
        Assert.Equal(77UL, loaded.ClubId);
        Assert.Equal(500, loaded.Money[1]);
        Assert.Equal(250, loaded.Money[2]);
        var girl = Assert.Single(loaded.Girls);
        Assert.Equal(42u, girl.Girlid);
        Assert.Equal(3u, girl.Level);
        Assert.Equal(95u, girl.Moodvalue);
        // 背包 + 编队往返
        var item = Assert.Single(loaded.Items);
        Assert.Equal(1u, item.Genre);
        Assert.Equal(42u, item.Detail);
        Assert.Equal(p.NextGuid, loaded.NextGuid);
        var slot = Assert.Single(Assert.Single(loaded.Formations).Slots);
        Assert.Equal(item.Guid, slot.MainCardGuid);
        Assert.Equal(item.Guid, slot.UsedCardGuid);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var store = NewStore();
        Assert.Null(store.Load("nobody_here"));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        // 直接写一个非法的“正常文件名”对应的损坏 JSON：先 Save 一次拿到路径，再覆盖成垃圾
        var store = NewStore();
        store.Save(new Player("victim", 1));
        var path = Path.Combine(_dir, "victim.player.json");
        Assert.True(File.Exists(path));

        File.WriteAllText(path, "{ this is not valid json !!! ");

        var loaded = store.Load("victim"); // 不得抛异常
        Assert.Null(loaded);
    }

    /// <summary>
    /// ★★ 回归：<c>LockOn</c> / <c>Flag</c> / <c>SyncRate</c> / <c>RefitNum</c> 必须持久化。
    ///
    /// <para>这四个字段 <see cref="GameNotify.BuildItemInfo"/> <b>一直在下发</b>，
    /// 但存档 DTO 此前漏了它们 ⇒ 症状是「界面上的状态重登就回退」：
    /// 玩家锁定的道具锁没了、角色卡「剧情已读」红点又回来、GM 塞的同步率被抹掉。</para>
    ///
    /// <para>★ 这类 bug <b>常规 e2e 验不出来</b>（同一会话内内存里的值一直是对的），
    /// 只有「重登后再看」才暴露 —— 所以必须由存档往返测试盯着。</para>
    /// </summary>
    [Fact]
    public void Save_Load_保留物品的LockOn与Flag与同步率()
    {
        var store = NewStore();
        var p = new Player("flags", World.DeriveRoleId("flags"));
        var card = p.AddItem(genre: 1, detail: 7, particular: 1, level: 2, count: 1);
        card.LockOn = 1;
        card.Flag = Item.FlagStoryReadBit;
        card.SyncRate = 12345;
        card.RefitNum = 4;

        store.Save(p);
        var loaded = store.Load("flags");

        var it = Assert.Single(loaded!.Items);
        Assert.Equal(1u, it.LockOn);
        Assert.Equal(Item.FlagStoryReadBit, it.Flag);
        Assert.Equal(12345u, it.SyncRate);
        Assert.Equal(4u, it.RefitNum);
    }

    /// <summary>
    /// ★★ 装备类字段（genre 2/3/4 的 <c>Skills</c>/<c>Inlay</c>/<c>Attrs</c>/<c>OwnGuid</c>）
    /// 必须存档往返保真 —— 客户端每次收 pf 1031/1104 都先 Clear 再从这些字段填回，
    /// 服务端重登后读不回来 = 「玩家的符文/技能/零件属性被清空」。
    /// </summary>
    [Fact]
    public void Save_Load_保留装备类字段()
    {
        var store = NewStore();
        var p = new Player("equip", World.DeriveRoleId("equip"));

        var weapon = p.AddItem(genre: 2, detail: 101, particular: 1, level: 1, count: 1);
        weapon.Inlay = new WeaponInlay { Barrel = 11, Ammo = 22, Sight = 33, Grip = 44 };
        weapon.Skills.Add(new ItemSkill { SkillId = 900, SkillLevel = 2, SkillType = 1 });
        weapon.Skills.Add(new ItemSkill { SkillId = 901, SkillLevel = 3, SkillType = 2 });

        var part = p.AddItem(genre: 4, detail: 202, particular: 1, level: 1, count: 1);
        part.OwnGuid = weapon.Guid;
        part.Attrs.Add(new ItemAttr { AType = 1, AValue = 500 });
        part.Attrs.Add(new ItemAttr { AType = 2, AValue = -30 });

        store.Save(p);
        var loaded = store.Load("equip");

        var w = loaded!.Items.Single(i => i.Genre == 2);
        Assert.NotNull(w.Inlay);
        Assert.Equal(11u, w.Inlay!.Barrel);
        Assert.Equal(22u, w.Inlay.Ammo);
        Assert.Equal(33u, w.Inlay.Sight);
        Assert.Equal(44u, w.Inlay.Grip);
        Assert.Equal(2, w.Skills.Count);
        Assert.Equal(900u, w.Skills[0].SkillId);
        Assert.Equal(1, w.Skills[0].SkillType);
        Assert.Equal(901u, w.Skills[1].SkillId);
        Assert.Equal(2, w.Skills[1].SkillType);

        var o = loaded.Items.Single(i => i.Genre == 4);
        Assert.Equal(w.Guid, o.OwnGuid);
        Assert.Equal(2, o.Attrs.Count);
        Assert.Equal(1u, o.Attrs[0].AType);
        Assert.Equal(500, o.Attrs[0].AValue);
        Assert.Equal(-30, o.Attrs[1].AValue);   // 负值也要保真（proto 是 int32）
    }

    /// <summary>
    /// 普通道具（genre 5）不该因为这次扩展而在存档里多出空集合字段 —— 近 2000 件道具
    /// 每件都写一个 <c>"Skills": []</c> 会把存档撑大好几倍。
    /// </summary>
    [Fact]
    public void Save_普通道具不写空集合字段()
    {
        var store = NewStore();
        var p = new Player("plain", World.DeriveRoleId("plain"));
        p.AddItem(genre: 5, detail: 1, particular: 1, level: 1, count: 10);

        store.Save(p);
        var json = File.ReadAllText(Path.Combine(_dir, "plain.player.json"));

        Assert.DoesNotContain("\"Skills\"", json);
        Assert.DoesNotContain("\"Attrs\"", json);
        Assert.DoesNotContain("\"Inlay\"", json);
    }
}
