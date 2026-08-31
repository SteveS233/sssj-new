using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 「访问别人的咖啡馆」域：pf 1107 <c>OtherHouseInfoReq</c> → pf 1108 <c>OtherHouseInfoRsp</c>，
/// 以及同域的 pf 1109 → 1110「随便逛逛」随机列表。
///
/// 每条断言都对应一处客户端取证（RVA / lua 文件:行号 / proto 字段号），
/// 完整推导见 <see cref="VisitCafeFeature"/> 的类注释与 <c>work/visit-cafe-forensics.md</c>。
/// </summary>
public class VisitCafeFeatureTests
{
    private static CafeTable CafeCfg => CafeTable.Load(TestPaths.SettingDir);
    private static HouseTable HouseCfg => HouseTable.Load(TestPaths.SettingDir);
    private static CharacterCardTable Cards => CharacterCardTable.Load(TestPaths.SettingDir);

    private const string Account = "visit-test";

    private static Player NewPlayer(ulong roleid = 470244594UL) => new(Account, roleid);

    private static VisitCafeFeature Feature() => new(CafeCfg, HouseCfg, Cards);

    private static Packet ReqPacket(ulong roleid, uint serial = 77)
        => new()
        {
            Pf = GamePf.OtherHouseInfo,
            Serial = serial,
            Payload = new OtherHouseInfoReq { Roleid = roleid }.ToByteArray(),
        };

    /// <summary>本账号机器人池里第一个人的 roleid（= 玩家的初始好友之一）。</summary>
    private static ulong FirstBotId()
        => FriendFeature.BuildBots(FriendFeature.SeedOf(Account), Cards)[0].Roleid;

    /// <summary>驱动 pf 1107 handler；返回 FakeSession 以便断言「有没有回包」。</summary>
    private static FakeSession Dispatch(Player? player, ulong target, uint serial = 77)
    {
        var fake = new FakeSession { Tag = player };
        Feature().HandleOtherHouseInfo(fake, ReqPacket(target, serial)).GetAwaiter().GetResult();
        return fake;
    }

    private static OtherHouseInfoRsp Respond(Player player, ulong target, uint serial = 77)
        => (OtherHouseInfoRsp)Dispatch(player, target, serial).LastOf(GamePf.OtherHouseInfoRsp).Message;

    // =================================================================
    //  一、拒绝路径：三种失败一律「不回包」
    // =================================================================

    /// <summary>
    /// ★ 目标是自己 ⇒ **不回包**。客户端 <c>0x1D320B0</c> 对
    /// <c>rsp.roleid == Player.me.id</c> 直接 return（静默、连日志都没有），
    /// 而且 <c>set_CafeRoleId</c> 写成自己的 id 会让整套数据写进**自己**的
    /// <c>playerHouseInfo</c> 容器（<c>get_HouseInfo</c> RVA <c>0x1D622C8</c> 按
    /// <c>IsInSelfCafe</c> 分流）—— 等于污染玩家自己的店。
    /// </summary>
    [Fact]
    public void 访问自己_不回包()
    {
        var player = NewPlayer();
        var fake = Dispatch(player, player.Roleid);
        Assert.Empty(fake.Sent);
    }

    /// <summary>roleid 缺失 / 为 0 ⇒ 不回包（没有可访问的目标）。</summary>
    [Fact]
    public void 目标为0_不回包()
        => Assert.Empty(Dispatch(NewPlayer(), 0UL).Sent);

    /// <summary>
    /// 目标不在机器人池里 ⇒ 不回包。单机服里除自己只有
    /// <see cref="FriendFeature.BotPoolSize"/> 个确定性机器人，
    /// 回一个「凭空捏造的人」会让客户端进一个数据来源不明的场景。
    /// </summary>
    [Fact]
    public void 目标不在机器人池_不回包()
        => Assert.Empty(Dispatch(NewPlayer(), 999999999UL).Sent);

    /// <summary>
    /// 会话未登录（<c>Tag</c> 非 <see cref="Player"/>）⇒ 不回包。
    /// ★ 与 pf 1048/1109 不同：那两个是「客户端带 serial 等回调」，不回会悬挂；
    /// 而 1107 成功回包的副作用是**立刻 <c>Map.Load(6)</c>**（<c>0x1D321BC</c>），
    /// 半成品会把玩家送进一个退不回去的空场景 ⇒ 宁可不回。
    /// </summary>
    [Fact]
    public void 未登录_不回包()
        => Assert.Empty(Dispatch(null, FirstBotId()).Sent);

    // =================================================================
    //  二、成功回包：五个字段与 serial
    // =================================================================

    /// <summary>
    /// 成功时回 pf 1108，且 <b>serial 必须回显请求的 serial</b>
    /// （<c>UNet.Net.Dispatch</c> 同时跑 pf 处理函数与 serial 回调，AGENTS.md §2.4）。
    /// </summary>
    [Fact]
    public void 成功回1108且serial回显()
    {
        var fake = Dispatch(NewPlayer(), FirstBotId(), serial: 4321);
        var sent = Assert.Single(fake.Sent);
        Assert.Equal(GamePf.OtherHouseInfoRsp, sent.Pf);
        Assert.Equal(4321u, sent.Serial);
    }

    /// <summary>
    /// <c>docs/05</c> §19.7 的「必填五件套」——本域实测更正为
    /// <b>四必填（roleid/rolename/houseinfo/entrusttime）+ cafeinfo 可省但照发</b>，
    /// <c>musicinfo</c>(#6) 省略（<c>0x1D321D8 cbz x0</c> 有保护）。
    /// </summary>
    [Fact]
    public void 五个字段齐备_musicinfo省略()
    {
        var rsp = Respond(NewPlayer(), FirstBotId());

        Assert.True(rsp.HasRoleid);
        Assert.True(rsp.HasRolename);
        Assert.True(rsp.HasHouseinfo);
        Assert.True(rsp.HasCafeinfo);
        Assert.True(rsp.HasEntrusttime);
        Assert.False(rsp.HasMusicinfo);
    }

    /// <summary>
    /// ★★ <c>rsp.roleid</c> 必须是**目标**而不是玩家自己 —— 相等会让客户端
    /// <c>0x1D320B0</c> 静默 return。<c>rolename</c> 与机器人档案一致（非空串）。
    /// </summary>
    [Fact]
    public void roleid是目标且不等于自己()
    {
        var player = NewPlayer();
        ulong target = FirstBotId();
        var rsp = Respond(player, target);

        Assert.Equal(target, rsp.Roleid);
        Assert.NotEqual(player.Roleid, rsp.Roleid);
        Assert.False(string.IsNullOrEmpty(rsp.Rolename));
    }

    /// <summary>
    /// <c>entrusttime</c> 固定 0 = 「没有正在进行的委托」。
    /// <c>0x1D69F38 get_entrusttime → 0x1D69F44 set_EntrustTime</c> 是无条件读的，
    /// 委托子系统本服未实现，0 是这一族最安全的值。
    /// </summary>
    [Fact]
    public void entrusttime为0()
        => Assert.Equal(0u, Respond(NewPlayer(), FirstBotId()).Entrusttime);

    // =================================================================
    //  三、houseinfo：复用 pf 1049 那套已验证能过 map 6 的构造
    // =================================================================

    /// <summary>
    /// ★★ <b>一件系统家具都不能发</b>：<c>UpdateOtherHouseInfo</c> 在
    /// <c>0x1D69F9C</c>/<c>0x1D69FA8</c> 调 <c>InitFixedData(false)</c> +
    /// <c>InitDefaultData(false)</c> 自建那 54 件（与 pf 1048 同构）。
    /// 服务端只发玩家家具 ⇒ 全部 <c>objId ≥ 1000</c>。
    /// </summary>
    [Fact]
    public void 家具全是玩家家具_objId不小于1000()
    {
        var cache = Respond(NewPlayer(), FirstBotId()).Houseinfo!;

        Assert.NotEmpty(cache.FurnitureList);
        Assert.All(cache.FurnitureList,
            f => Assert.True(f.ObjId >= HouseTable.MinPlayerObjId,
                             $"objId={f.ObjId} 撞上客户端自建的系统家具（≤ {HouseTable.MaxSystemObjId}）"));
    }

    /// <summary>
    /// ★ <b>每件家具必须带 <c>pose</c></b>：<c>NCafeItem.UpdateInfo</c>（RVA <c>0x1D2C68C</c>）
    /// 在 <c>0x1D2C944 get_posX</c> 之前只有抛 NRE 的 null 检查，
    /// <c>has_pose</c> 在整条链路一次都没被调用。缺 pose = 每件家具都在循环里抛。
    ///
    /// 同时验 <b>每件的 <c>objPose</c> 是独立实例</b>：<c>set_pose</c>(<c>0x1D2C854</c>)
    /// 是引用赋值，而 <c>CheckDataChanged</c>(<c>0x1D2CFE4</c>) 会往同一个实例回写坐标
    /// ⇒ 共享实例会让挪一件家具静默污染另一件。
    /// </summary>
    [Fact]
    public void 每件家具都带独立的pose实例()
    {
        var list = Respond(NewPlayer(), FirstBotId()).Houseinfo!.FurnitureList;

        Assert.All(list, f => Assert.True(f.HasPose, $"objId={f.ObjId} 没带 pose"));

        var seen = new HashSet<objPose>(ReferenceEqualityComparer.Instance.ToGeneric<objPose>());
        foreach (var f in list)
            Assert.True(seen.Add(f.Pose!), $"objId={f.ObjId} 与别的家具共享了同一个 objPose 实例");
    }

    /// <summary>
    /// <c>roomList</c> 必须发满 <see cref="HouseRooms.Ids"/> = {1 Cafe, 2 Rest, 6 Dorm}：
    /// <c>ReqSaveArea</c>（RVA <c>0x1D6B11C</c>）的 <c>GetArea(type)</c> 无 nil 保护，
    /// 而 <c>Areas</c> 的唯一填充来源就是 <c>roomList</c>（<c>OnUpdateRoom</c> <c>0x1D696F0</c>）。
    /// </summary>
    [Fact]
    public void roomList发满三个区域()
    {
        var cache = Respond(NewPlayer(), FirstBotId()).Houseinfo!;
        Assert.Equal(HouseRooms.Ids.OrderBy(x => x).Select(x => (uint)x),
                     cache.RoomList.Select(r => r.RoomId).OrderBy(x => x));
    }

    /// <summary>
    /// ★ <c>houseinfo.roleid</c> 是**被访问者**的 id。
    /// <c>UpdateOtherHouseInfo</c> 这条路不读它（只读三个 list），
    /// 但 <c>HouseCache</c> 同时也被 <c>HouseManager.OnGetHouseInfoRsp</c> 那条路消费，
    /// 口径保持一致才不会在将来接装修时错乱。
    /// </summary>
    [Fact]
    public void houseinfo的roleid是被访问者()
    {
        ulong target = FirstBotId();
        Assert.Equal(target, Respond(NewPlayer(), target).Houseinfo!.Roleid);
    }

    /// <summary>
    /// <c>templateId</c> 一律过 <c>NCafeItems.txt</c> 校验，且不含
    /// <see cref="HouseTable.PoolBlockedTemplateIds"/>（20/21）——
    /// 后者会让 <c>LoadItem</c>(<c>0x1A3BD10</c>) 静默断链、加载条永远停在 0.4。
    /// </summary>
    [Fact]
    public void 家具templateId全部合法且不含黑名单()
    {
        var table = HouseCfg;
        foreach (var f in Respond(NewPlayer(), FirstBotId()).Houseinfo!.FurnitureList)
        {
            Assert.True(table.IsKnownTemplate((int)f.TemplateId), $"templateId={f.TemplateId} 不在表里");
            Assert.False(table.IsPoolBlockedTemplate((int)f.TemplateId),
                         $"templateId={f.TemplateId} 在 LoadItem 黑名单里");
        }
    }

    // =================================================================
    //  四、cafeinfo：四个标量都写，四个 repeated 一律空
    // =================================================================

    /// <summary>
    /// 四个标量全部写出。虽然客户端逐个 <c>has_</c> 判断（漏了不崩），
    /// 但 <c>cafeLevel</c> 挂在 controller 上、<c>Clear()</c> 不动它
    /// ⇒ 漏写会沿用**玩家自己店**的等级，表现为「每个人的店等级都一样」。
    /// </summary>
    [Fact]
    public void cafeinfo四个标量都写出()
    {
        var cafe = Respond(NewPlayer(), FirstBotId()).Cafeinfo!;
        Assert.True(cafe.HasLevel);
        Assert.True(cafe.HasHot);
        Assert.True(cafe.HasComfort);
        Assert.True(cafe.HasBasetime);
    }

    /// <summary>
    /// ★★ 四个 repeated 一律**空**，理由见 <see cref="VisitCafeFeature"/> §7b：
    /// 座位有 <c>CheckSeatValid</c>(<c>0x1D67590</c>) 的「position/10 必须是真实餐位家具」
    /// 静默丢弃约束；少女属性在访客模式下**读不到**
    /// （<c>GirlCommon.lua:92</c> <c>IsInSelfCafe()</c> 闸门）。
    ///
    /// ★ 空 <c>roomgirlslist</c> 同时规避了「位置编码」陷阱：
    /// <c>0x1D6AB2C</c> 用外层下标索引硬编码的 <c>[Cafe,Rest,Dorm]</c>，
    /// 发第 4 个会 <c>ArgumentOutOfRangeException</c>。
    /// </summary>
    [Fact]
    public void cafeinfo四个清单一律空()
    {
        var cafe = Respond(NewPlayer(), FirstBotId()).Cafeinfo!;
        Assert.Empty(cafe.Seatlist);
        Assert.Empty(cafe.Customerqueue);
        Assert.Empty(cafe.Coffeelist);
        Assert.Empty(cafe.Roomgirlslist);
    }

    /// <summary>
    /// 等级钳在 <c>[1, MaxDerivedLevel]</c>：既要落在 <c>NCafeLevels.txt</c> 实有键里，
    /// 也要避开 <c>UI_NCafeVisit.lua:206..213</c> <c>switchToRoom</c> 的高等级分支。
    /// 舒适度不超过配置表上限（越界会静默退化成零加成）。
    /// </summary>
    [Fact]
    public void 等级与舒适度都在合法区间()
    {
        var table = CafeCfg;
        foreach (var bot in FriendFeature.BuildBots(FriendFeature.SeedOf(Account), Cards))
        {
            var cafe = VisitCafeFeature.DeriveCafe(bot, table);
            Assert.InRange(cafe.Level, (uint)CafeFeature.MinLevel, (uint)VisitCafeFeature.MaxDerivedLevel);
            Assert.True(cafe.Comfort <= (uint)table.MaxComfort,
                        $"comfort={cafe.Comfort} 超出 {table.MaxComfort}");
        }
    }

    /// <summary>
    /// ★ 舒适度必须与好友列表里显示的 <c>comfortscore</c> 同源
    /// （<c>FriendFeature.BuildFriendInfo</c> 的 <c>["comfortscore"]</c>），
    /// 否则会出现「列表里写 3000、进店显示 800」的自相矛盾。
    /// </summary>
    [Fact]
    public void 舒适度与好友档案的comfortscore同源()
    {
        var table = CafeCfg;
        foreach (var bot in FriendFeature.BuildBots(FriendFeature.SeedOf(Account), Cards))
        {
            uint expected = (uint)Math.Clamp(bot.ComfortScore, 0,
                Math.Min(VisitCafeFeature.MaxDerivedComfort, table.MaxComfort));
            Assert.Equal(expected, VisitCafeFeature.DeriveCafe(bot, table).Comfort);
        }
    }

    /// <summary>
    /// ★ 确定性：同一个好友的店，每次访问都一样（等级/人气/舒适度逐字段相同）。
    /// 客户端以 roleid 为键缓存过一些东西，「同一个人每次不一样」会让玩家困惑。
    /// </summary>
    [Fact]
    public void 同一好友的店是确定性的()
    {
        var player = NewPlayer();
        ulong target = FirstBotId();

        var a = Respond(player, target).Cafeinfo!;
        var b = Respond(player, target).Cafeinfo!;

        Assert.Equal(a.Level, b.Level);
        Assert.Equal(a.Hot, b.Hot);
        Assert.Equal(a.Comfort, b.Comfort);
    }

    /// <summary>
    /// ★ 区分度：机器人池里的等级不能全都一样（否则派生等于没派生）。
    /// </summary>
    [Fact]
    public void 不同好友的店等级有区分度()
    {
        var table = CafeCfg;
        var levels = FriendFeature.BuildBots(FriendFeature.SeedOf(Account), Cards)
            .Select(b => VisitCafeFeature.DeriveCafe(b, table).Level)
            .Distinct()
            .ToList();
        Assert.True(levels.Count >= 3, $"{FriendFeature.BotPoolSize} 个机器人只派生出 {levels.Count} 种等级");
    }

    // =================================================================
    //  五、pf 1109 / 1110「随便逛逛」
    // =================================================================

    /// <summary>
    /// 驱动 pf 1109 handler，返回 FakeSession。
    /// ★ 阻塞等待放在**非测试方法**里，既没有死锁风险（handler 全程同步、
    /// 末尾 <c>return Task.CompletedTask</c>），也顺带避开 xUnit1031 分析器
    /// —— 与 <c>HouseFeatureTests.RespondWith</c> 同一套做法。
    /// </summary>
    private static FakeSession DispatchRandom(Player? player, uint serial = 88)
    {
        var fake = new FakeSession { Tag = player };
        var packet = new Packet
        {
            Pf = GamePf.HouseRandomList,
            Serial = serial,
            Payload = new HouseRandomListReq().ToByteArray(),
        };
        Feature().HandleHouseRandomList(fake, packet).GetAwaiter().GetResult();
        return fake;
    }

    private static HouseRandomListRsp RandomList(Player? player, uint serial = 88)
        => (HouseRandomListRsp)DispatchRandom(player, serial).LastOf(GamePf.HouseRandomListRsp).Message;

    /// <summary>
    /// pf 1109 回非空列表 —— 此前 <c>GameModule</c> 回**空** <c>HouseRandomListRsp</c>，
    /// 导致 <c>UI_CafeVisitMain.lua:607..610</c> 恒弹「没有可访问的房间」。
    /// 客户端消费只有一句（<c>0x1D33438 rolelist → AddRandomList</c>），无字段校验。
    /// </summary>
    [Fact]
    public void 随机列表非空且回显serial()
    {
        var sent = Assert.Single(DispatchRandom(NewPlayer(), serial: 4242).Sent);
        Assert.Equal(GamePf.HouseRandomListRsp, sent.Pf);
        Assert.Equal(4242u, sent.Serial);
        Assert.NotEmpty(((HouseRandomListRsp)sent.Message).Rolelist);
    }

    /// <summary>
    /// ★★ 随机列表**绝不能含自己** —— 含了会让那一次随机访问撞上
    /// <c>0x1D320B0</c> 静默 return，表现为「点了没反应」。
    /// </summary>
    [Fact]
    public void 随机列表不含自己()
    {
        var player = NewPlayer();
        Assert.DoesNotContain(player.Roleid, RandomList(player).Rolelist);
    }

    /// <summary>随机列表里的每个 id 都必须是「真的能访问」的（在机器人池里）。</summary>
    [Fact]
    public void 随机列表每个id都能被1107接受()
    {
        var player = NewPlayer();
        foreach (ulong id in RandomList(player).Rolelist)
            Assert.Equal(id, Respond(player, id).Roleid);
    }

    /// <summary>
    /// 未登录时仍然要回包（回空列表）：1109 是带 serial 的请求，
    /// 不回会让客户端 <c>ReqHouseRandomList</c> 的回调永久悬挂。
    /// </summary>
    [Fact]
    public void 未登录也回1110空列表()
        => Assert.Empty(RandomList(null).Rolelist);

    // =================================================================
    //  六、与好友域的一致性（两个 feature 必须看到同一批人）
    // =================================================================

    /// <summary>
    /// ★★ <see cref="VisitCafeFeature"/> 不复用 <see cref="FriendFeature"/> 的实例，
    /// 而是按同一套纯函数（<c>SeedOf(account)</c> + <c>BuildBots</c>）重建机器人池。
    /// 这条断言钉住「两边逐字段相同」—— 否则会出现「好友列表里有这个人，
    /// 点访问却说不在池里」。
    ///
    /// ★ 注意不能直接 <c>Assert.Equal(record, record)</c>：<see cref="FriendFeature.FriendBot"/>
    /// 里 <c>Cards</c> 是 <c>IReadOnlyList</c>，record 的自动相等对它走**引用相等**，
    /// 两次独立 <c>BuildBots</c> 的 List 必然是不同实例 ⇒ 恒不相等。
    /// 所以逐标量比 + 单独比卡列表内容。
    /// </summary>
    [Fact]
    public void 机器人池与好友域逐字段一致()
    {
        var player = NewPlayer();
        var mine = Feature().BotsOf(player);
        var theirs = FriendFeature.BuildBots(FriendFeature.SeedOf(Account), Cards);

        Assert.Equal(theirs.Count, mine.Count);
        for (int i = 0; i < theirs.Count; i++)
        {
            var (a, b) = (theirs[i], mine[i]);
            Assert.Equal(a.Roleid, b.Roleid);
            Assert.Equal(a.RoleName, b.RoleName);
            Assert.Equal(a.Level, b.Level);
            Assert.Equal(a.GirlIconId, b.GirlIconId);
            Assert.Equal(a.HeadFrameId, b.HeadFrameId);
            Assert.Equal(a.SignInfo, b.SignInfo);
            Assert.Equal(a.State, b.State);
            Assert.Equal(a.OffSeconds, b.OffSeconds);
            Assert.Equal(a.CardNum, b.CardNum);
            Assert.Equal(a.ComfortScore, b.ComfortScore);
            // BotCard 是全值类型字段的 record ⇒ 逐元素值相等可用
            Assert.Equal(a.Cards, b.Cards);
        }
    }

    /// <summary>
    /// 好友域给的初始好友（<c>EnsureSeeded</c> 分配的前几个）必须都能访问 ——
    /// 这正是玩家在好友资料页点「访问」的那批人。
    /// </summary>
    [Fact]
    public void 初始好友全部可访问()
    {
        var player = NewPlayer();
        var state = FriendFeature.ReadState(player);
        FriendFeature.EnsureSeeded(player, state, Cards);

        Assert.NotEmpty(state.Hy);
        foreach (ulong id in state.Hy)
            Assert.Equal(id, Respond(player, id).Roleid);
    }
}

/// <summary>把非泛型 <see cref="ReferenceEqualityComparer"/> 适配成泛型（引用相等去重用）。</summary>
internal static class ReferenceComparerExtensions
{
    public static IEqualityComparer<T> ToGeneric<T>(this ReferenceEqualityComparer c) where T : class
        => new Wrapper<T>(c);

    private sealed class Wrapper<T>(ReferenceEqualityComparer inner) : IEqualityComparer<T> where T : class
    {
        public bool Equals(T? a, T? b) => inner.Equals(a, b);
        public int GetHashCode(T obj) => inner.GetHashCode(obj);
    }
}
