using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 社团域（<c>LuaCall.Club_*</c> 15 条命令）。完整取证见 `work/club-domain-forensics.md`，
/// 实现说明与实测差异见 `work/club-domain-impl.md`。
///
/// 断言重点是六条「漏了就出事」的硬约束：
/// <list type="number">
/// <item>★★ <b>族 B「非空表即成功」</b>（<c>if next(tbParam)</c>，9 个命令）——
///   失败**只能回空表**。回 <c>{nError=n}</c> 会被判成成功，进而
///   <c>ClubCommon.lua:459</c> 把 <c>me.clubid</c> 清 0、<c>UI_ClubMember.lua:645</c> 退回主城。</item>
/// <item>★★ <c>Player.ClubId</c> 与社团对象**原子一致** —— <c>GameModule.cs:590</c> 已无条件
///   把 <c>ClubId</c> 发进 pf 1005，不一致会让 <c>UI_ClubReward.lua:35</c> 的 <c>next(nil)</c> 抛。</item>
/// <item>★★ 三种「指数」道具（<c>15-13</c>/<c>15-12</c>/<c>15-36</c>）**不能进背包** ——
///   服务端只映射了 <c>(15,14,1,1)→货币 11</c>（<c>Config/ItemBaseTable.cs:202</c>）。</item>
/// <item>★ 上行 <c>tbParam</c> 可能是 <c>null</c>（真机实测 10003 就是）或 <c>JsonArray</c>，
///   <c>JsonNode["key"]</c> 对后者**会抛**（AGENTS.md 记录过的真机崩溃）。</item>
/// <item>★ 10002 必须**覆盖** <c>GameModule.cs:120..127</c> 的「恒回空对象」占位实现。</item>
/// <item>★ 三个回包形状与直觉完全不同：10008 回**裸数组**、10014 回**整个 primary**、
///   10027 必须**回显 <c>strType</c>**。</item>
/// </list>
/// </summary>
public class ClubFeatureTests
{
    // ================================================================
    //  小工具
    // ================================================================

    /// <summary>取最后一条 <c>sFunction=="LuaCall"</c> 的下行（跳过夹在中间的 CallClientTip）。</summary>
    private static JsonObject LuaReply(FeatureHarness h)
    {
        for (int i = h.Outbound.Count - 1; i >= 0; i--)
        {
            if (h.Outbound[i].Function == LuaCallRouter.LuaCallFunction)
                return Assert.IsType<JsonObject>(JsonNode.Parse(h.Outbound[i].Json));
        }
        throw new InvalidOperationException("没有任何 LuaCall 下行");
    }

    /// <summary>最后一条 LuaCall 下行的 <c>tbParam</c>。</summary>
    private static JsonNode TbParam(FeatureHarness h) => LuaReply(h)["tbParam"]
        ?? throw new InvalidOperationException("下行缺 tbParam");

    /// <summary>最后一条 LuaCall 下行的 <c>tbParam</c>，且断言它是对象。</summary>
    private static JsonObject TbObject(FeatureHarness h) => Assert.IsType<JsonObject>(TbParam(h));

    /// <summary>本次会话发出的全部 <c>CallClientTip</c> 的第一个元素（= 文案 key）。</summary>
    private static List<string> TipKeys(FeatureHarness h)
    {
        var keys = new List<string>();
        foreach (var (function, json) in h.Outbound)
        {
            if (function != ClubFeature.FnCallClientTip)
                continue;
            if (JsonNode.Parse(json) is JsonArray arr && arr.Count > 0)
                keys.Add(arr[0]!.GetValue<string>());
        }
        return keys;
    }

    /// <summary>是否有任何 LuaCall 下行的 tbParam 带了 <c>nError</c> 键（族 B 的红线）。</summary>
    private static bool AnyReplyHasNError(FeatureHarness h)
    {
        foreach (var (function, json) in h.Outbound)
        {
            if (function != LuaCallRouter.LuaCallFunction)
                continue;
            if (JsonNode.Parse(json) is JsonObject o && o["tbParam"] is JsonObject tb
                && tb.ContainsKey("nError"))
                return true;
        }
        return false;
    }

    private const string CreateOk =
        "{\"clubname\":\"星海事务所\",\"applylevel\":10,\"autojoinlevel\":20}";

    /// <summary>造一个「LV.300 + 1000 免费钻」的会话（GM 账号的形态）。</summary>
    private static async Task<FeatureHarness> RichAsync(string account, int level = 300)
    {
        var h = await FeatureHarness.CreateAsync(account);
        h.Player.Name = "店长";
        h.Player.Level = level;
        h.Player.AddMoney(MoneyType.FreeDiamond, 1000);
        return h;
    }

    /// <summary>造一个「已经有自己的社团（我是社长）」的会话。</summary>
    private static async Task<FeatureHarness> WithOwnClubAsync(string account)
    {
        var h = await RichAsync(account);
        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub, CreateOk);
        h.Outbound.Clear();
        return h;
    }

    /// <summary>
    /// 造一个「自动入团进了别人社团（我是普通成员）」的会话。
    /// 走真实链路：10003 拿发现列表 → 挑一个 <c>autojoinlevel ≤ 玩家等级</c> 的 → 10005。
    /// </summary>
    private static async Task<FeatureHarness> WithJoinedClubAsync(string account)
    {
        var h = await RichAsync(account);
        await h.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "null");
        var fx = Assert.IsType<JsonArray>(TbObject(h)["fx"]);
        JsonNode? target = null;
        foreach (var n in fx)
        {
            if (n!["autojoinlevel"]!.GetValue<int>() <= h.Player.Level)
            {
                target = n;
                break;
            }
        }
        Assert.NotNull(target);          // DiscoverClubs 偶数位恒是「低门槛自动准入」档
        ulong id = target!["id"]!.GetValue<ulong>();

        h.Outbound.Clear();
        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, $"{{\"clublist\":[{id}]}}");
        Assert.NotEqual(0UL, h.Player.ClubId);
        h.Outbound.Clear();
        return h;
    }

    // ================================================================
    //  10001 Club_CreateClub
    // ================================================================

    /// <summary>
    /// ★★ 创建成功后 <see cref="Player.ClubId"/> 与 <c>Extra["club"]["club"]["id"]</c>
    /// 必须**一起**落地（取证文档 §8.5 的硬约束），并且能从存档 round-trip 回来。
    /// </summary>
    [Fact]
    public async Task 创建社团_ClubId与社团对象原子一致且落盘()
    {
        using var h = await RichAsync("club-create-atomic");

        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub, CreateOk);

        var primary = Assert.IsType<JsonObject>(TbObject(h)["primary"]);
        ulong id = primary["id"]!.GetValue<ulong>();
        Assert.NotEqual(0UL, id);
        Assert.Equal(ClubFeature.OwnClubIdOf(h.Player), id);
        Assert.Equal(id, h.Player.ClubId);

        // Extra 里的 id 必须与 ClubId 完全一致
        var bagNode = Assert.IsType<JsonObject>(h.Player.Extra[ClubBagKey]);
        var clubNode = Assert.IsType<JsonObject>(bagNode["club"]);
        Assert.Equal(id, clubNode["id"]!.GetValue<ulong>());

        // 重新从存档读出来（PlayerStore round-trip）也要一致
        var reloaded = h.Module.World.GetOrCreatePlayer("club-create-atomic");
        Assert.Equal(id, reloaded.ClubId);
        Assert.NotNull(ClubBag.Read(reloaded).Club);
        Assert.Equal(id, ClubBag.Read(reloaded).Club!.Id);
    }

    private const string ClubBagKey = "club";

    /// <summary>创建社团要扣 <c>ClubLevels[1].CreateFee</c>（250 免费钻）并推 pf 1032，否则界面上钻石数不掉。</summary>
    [Fact]
    public async Task 创建社团_扣250免费钻并推pf1032()
    {
        using var h = await RichAsync("club-create-fee");
        var lv1 = ClubTable.Load(TestPaths.SettingDir).Level(1);
        Assert.Equal(MoneyType.FreeDiamond, lv1.CreateFeeType);
        Assert.Equal(250, lv1.CreateFee);
        int before = h.Player.GetMoney(MoneyType.FreeDiamond);

        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub, CreateOk);

        Assert.Equal(before - 250, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf),
                    "没有推 pf 1032，界面上的钻石数不会掉（取证文档 §8.6）");
    }

    /// <summary>
    /// 名字宽度必须在 <c>GetStringLen</c> ∈ [4,12] 内（<c>UI_Search.lua:8..14</c> 简中档）。
    /// GM 通道绕得过客户端校验，服务端必须复校；失败回**空表**而不是 <c>{nError}</c>。
    /// </summary>
    [Theory]
    [InlineData("\"一\"", false)]           // 宽度 2 < 4
    [InlineData("\"社团\"", true)]          // 宽度 4
    [InlineData("\"六个汉字的社团\"", false)] // 宽度 14 > 12
    [InlineData("\"\"", false)]
    public async Task 创建社团_名字宽度校验(string nameJson, bool ok)
    {
        using var h = await RichAsync($"club-name-{nameJson.Length}-{ok}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub,
            $"{{\"clubname\":{nameJson},\"applylevel\":1,\"autojoinlevel\":1}}");

        if (ok)
        {
            Assert.NotEqual(0UL, h.Player.ClubId);
            return;
        }
        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Empty(TbObject(h));                                  // ★ 空表
        Assert.False(AnyReplyHasNError(h), "★ 失败回包绝不能带 nError");
        Assert.Contains(ClubFeature.TipMaxName, TipKeys(h));
    }

    /// <summary>申请等级 &gt; 自动准入等级要被拒（<c>UI_Search.lua:342</c> 同款判据）。</summary>
    [Fact]
    public async Task 创建社团_申请等级大于自动准入被拒()
    {
        using var h = await RichAsync("club-create-lv");

        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub,
            "{\"clubname\":\"星海事务所\",\"applylevel\":50,\"autojoinlevel\":10}");

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipErrorCreateLevel, TipKeys(h));
    }

    /// <summary>钻石不足时不能建社团、也不能扣钱。</summary>
    [Fact]
    public async Task 创建社团_钻石不足被拒且不扣钱()
    {
        using var h = await FeatureHarness.CreateAsync("club-create-poor");
        h.Player.Level = 300;
        // ★ 钻石的「买得起吗」口径是 3 + 12 + 18 三种相加（ShopFeature.cs:981 的池定义，
        //   与客户端 MoneyLogic.lua:150 的 freeDiamond + payDiamond 同源），
        //   所以要把整池都压到 10，只清货币 3 不够。
        h.Player.SetMoney(MoneyType.FreeDiamond, 10);
        h.Player.SetMoney(MoneyType.PayDiamond, 0);
        h.Player.SetMoney(ShopFeature.MoneyPayIosDiamond, 0);

        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub, CreateOk);

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Equal(10, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipNotEnoughDiamond, TipKeys(h));
    }

    /// <summary>已经有社团时再建要被拒（<c>ui.Club.AlreadyOwnClub</c>），且不能扣第二次钱。</summary>
    [Fact]
    public async Task 创建社团_已有社团时被拒()
    {
        using var h = await WithOwnClubAsync("club-create-twice");
        int money = h.Player.GetMoney(MoneyType.FreeDiamond);
        ulong id = h.Player.ClubId;

        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub,
            "{\"clubname\":\"另一个社团\",\"applylevel\":1,\"autojoinlevel\":1}");

        Assert.Equal(id, h.Player.ClubId);
        Assert.Equal(money, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Contains(ClubFeature.TipAlreadyOwnClub, TipKeys(h));
    }

    /// <summary>创建社团要写第一条日志「%s创建了社团」（type 1，strlist 长度必须是 1）。</summary>
    [Fact]
    public async Task 创建社团_写下第一条日志()
    {
        using var h = await WithOwnClubAsync("club-create-log");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubLog, "null");

        var logs = Assert.IsType<JsonArray>(TbObject(h)["actionloglist"]);
        Assert.Single(logs);
        var log = Assert.IsType<JsonObject>(logs[0]);
        Assert.Equal(1, log["type"]!.GetValue<int>());
        var strlist = Assert.IsType<JsonArray>(log["strlist"]);
        Assert.Single(strlist);                     // ★ type 1 要求 #strlist == 1，否则渲染成空串
        Assert.Equal("店长", strlist[0]!.GetValue<string>());
        Assert.True(log["time"]!.GetValue<long>() > 0);
    }

    // ================================================================
    //  10002 Club_ReqClubInfo（含 GameModule 占位覆盖）
    // ================================================================

    /// <summary>没有社团时回空对象 —— <c>ClubCommon.lua:454</c> 的 <c>not club.primary</c> 就是合法「没社团」。</summary>
    [Fact]
    public async Task ReqClubInfo_没有社团回空对象()
    {
        using var h = await FeatureHarness.CreateAsync("club-info-empty");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");

        Assert.Equal(ClubFeature.CmdReqClubInfo, LuaReply(h)["sCmd"]!.GetValue<int>());
        Assert.Empty(TbObject(h));
        Assert.False(AnyReplyHasNError(h));
    }

    /// <summary>
    /// ★★ 回归护栏：10002 必须**覆盖** <c>GameModule.cs:120..127</c> 那个「恒回空对象」的占位。
    ///
    /// 两个只有本 Feature 才会产生的信号：
    /// ① 有社团时回包里有 <c>primary</c>（占位永远回 <c>{}</c>）；
    /// ② <c>ClubId != 0</c> 却没有社团对象时会**就地清零**（占位不碰存档）。
    /// 做法参照 <c>HouseFeatureTests.MapPf_覆盖GameModule的空HouseCache</c>。
    /// </summary>
    [Fact]
    public async Task ReqClubInfo_覆盖GameModule的10002占位()
    {
        using var h = await WithOwnClubAsync("club-info-override");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");

        var tb = TbObject(h);
        Assert.True(tb.ContainsKey("primary"),
            "10002 回包没有 primary ⇒ 仍然是 GameModule.cs:123 的空对象占位在处理（features 装配应在 :171 之后覆盖它）");
        Assert.True(tb.ContainsKey("memberlist"));
    }

    /// <summary>
    /// ★★★ <c>ClubId != 0</c> 但没有社团对象 = 客户端必崩的前提（<c>UI_ClubReward.lua:35</c>
    /// 的 <c>next(nil)</c>）。10002 必须就地把 <c>ClubId</c> 清零并落盘。
    /// </summary>
    [Fact]
    public async Task ReqClubInfo_ClubId非0但没有社团对象时自愈清零()
    {
        using var h = await FeatureHarness.CreateAsync("club-info-heal");
        h.Player.ClubId = 987654;                        // 人为破坏不变量

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Empty(TbObject(h));
        var reloaded = h.Module.World.GetOrCreatePlayer("club-info-heal");
        Assert.Equal(0UL, reloaded.ClubId);
    }

    /// <summary>
    /// <c>primary</c> 的十二个运行时必填字段必须齐全（取证文档 §4.2 的「最小可用 primary」）。
    /// 其中三个漏了会**直接抛 Lua 错误**：<c>reqdissolvetime</c>（<c>UI_ClubMember.lua:104</c>
    /// 的 <c>nil &gt; 0</c>）、<c>weektaskvalue</c>（<c>UI_ClubTask.lua:229</c>）、
    /// <c>notice</c>（<c>UI_Club.lua:411</c> 的 <c>string.len(nil)</c>）。
    /// </summary>
    [Fact]
    public async Task ReqClubInfo_primary十二个必填字段齐全()
    {
        using var h = await WithOwnClubAsync("club-info-primary");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");

        var primary = Assert.IsType<JsonObject>(TbObject(h)["primary"]);
        foreach (var key in new[]
        {
            "id", "name", "level", "count", "exp", "applylevel", "autojoinlevel",
            "notice", "headername", "reqdissolvetime", "send_mail_times", "weektaskvalue",
        })
        {
            Assert.True(primary.ContainsKey(key), $"primary 缺字段 {key}");
            Assert.NotNull(primary[key]);
        }
        Assert.Equal(0, primary["reqdissolvetime"]!.GetValue<long>());   // 0 = 未申请解散
        Assert.Equal("", primary["notice"]!.GetValue<string>());         // 空串安全，nil 才抛
        // ★ 自建社团现在含 NpcMemberCount 个机器人团员（社团管理三条需要操作对象）
        Assert.Equal(1 + ClubFeature.NpcMemberCount, primary["count"]!.GetValue<int>());
        // ★ 10014 会把整个 primary 当 tbParam 回给族 A 判据 ⇒ 绝不能有 nError 键
        Assert.False(primary.ContainsKey("nError"));
    }

    /// <summary>
    /// ★ 刻意**不下发 <c>clubboss</c>** —— <c>ClubCommon.lua:462</c> 的
    /// <c>club.clubboss and club.clubboss.stageid or 0</c> 因此得 0，客户端不加载任何 boss 配置。
    /// 这是「boss 整域不实现」的落地方式（<c>ClubBossTime.txt</c> 9 期全过期，硬造也必炸）。
    /// </summary>
    [Fact]
    public async Task ReqClubInfo_不下发clubboss以关闭boss子系统()
    {
        using var h = await WithOwnClubAsync("club-info-noboss");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");

        var tb = TbObject(h);
        Assert.False(tb.ContainsKey("clubboss"));
        Assert.False(tb.ContainsKey("historychatlist"));
    }

    /// <summary>成员列表里「我」必须是社长（pos=9），否则主页三个管理按钮全不显示。</summary>
    [Fact]
    public async Task ReqClubInfo_成员表里我是社长且必填字段齐全()
    {
        using var h = await WithOwnClubAsync("club-info-member");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");

        var members = Assert.IsType<JsonArray>(TbObject(h)["memberlist"]);
        // ★ 第十五轮起自建社团会补 NpcMemberCount 个机器人团员（否则社团管理三条没有操作对象）
        //   ⇒ 断言从「只有我一个」改成「我在里面、且只有我是社长」，不写死人数。
        Assert.Equal(1 + ClubFeature.NpcMemberCount, members.Count);
        var me = Assert.IsType<JsonObject>(
            members.First(n => n!["roleid"]!.GetValue<ulong>() == h.Player.Roleid));
        Assert.Single(members, n => n!["pos"]!.GetValue<int>() == ClubFeature.PosHeader);
        Assert.Equal(ClubFeature.PosHeader, me["pos"]!.GetValue<int>());
        Assert.False(me.ContainsKey("post"), "★ 键名是 pos，不是原服注释块里的 post（LuaCall.lua:2130 是旧版）");
        Assert.Equal(h.Player.Roleid, me["roleid"]!.GetValue<ulong>());
        Assert.Equal(1, me["state"]!.GetValue<int>());
        Assert.Equal(0, me["headframeid"]!.GetValue<int>());   // 0 恒安全（GetBorderID(0) → 1）
        Assert.Equal(0, me["listskinid"]!.GetValue<int>());
        foreach (var key in new[]
        {
            "roleid", "name", "level", "pos", "state", "girliconid", "headframeid",
            "off_times", "last7_contribute",
        })
        {
            Assert.True(me.ContainsKey(key), $"member 缺字段 {key}（漏了会在 UI_ClubMember 抛 Lua 错误）");
        }
        Assert.True(me["girliconid"]!.GetValue<int>() > 0,
                    "girliconid 为 0/nil 会让 CardCommon:GetGirlIcon 的 string.format 抛");
    }

    // ================================================================
    //  10003 Club_FXClubList
    // ================================================================

    /// <summary>
    /// ★★ 真机实测上行**连 tbParam 键都没有**（`work/probe-three-domains.md` §2），
    /// 服务端侧 <c>param == null</c>。回包两组列表，六列全部有合法值。
    /// </summary>
    [Fact]
    public async Task FXClubList_上行null不抛且六列齐全()
    {
        using var h = await RichAsync("club-fx-null");

        // 模拟真机形状：sJsonParam 里根本没有 tbParam 键
        await h.LuaCalls.DispatchAsync(h.Session, LuaCallRouter.LuaCallFunction,
                                       $"{{\"sCmd\":{ClubFeature.CmdFxClubList}}}");

        var tb = TbObject(h);
        var applyed = Assert.IsType<JsonArray>(tb["applyedlist"]);
        var fx = Assert.IsType<JsonArray>(tb["fx"]);
        Assert.Empty(applyed);
        Assert.Equal(ClubFeature.DiscoverClubCount, fx.Count);

        var table = ClubTable.Load(TestPaths.SettingDir);
        foreach (var n in fx)
        {
            var o = Assert.IsType<JsonObject>(n);
            foreach (var key in new[] { "id", "name", "level", "count", "applylevel", "autojoinlevel" })
                Assert.True(o.ContainsKey(key), $"发现列表元素缺 {key}（UI_Search.lua:190..203 无 nil 保护）");

            int level = o["level"]!.GetValue<int>();
            Assert.True(table.HasLevel(level),
                        $"社团等级 {level} 不在 ClubLevels.txt 里 ⇒ UI_Search.lua:194 的 tbLvConfig 为 nil");
            Assert.InRange(o["count"]!.GetValue<int>(), 1, table.Level(level).CountLmt);
            Assert.InRange(o["applylevel"]!.GetValue<int>(),
                           ClubFeature.MinApplyLevel, ClubFeature.MaxApplyLevel);
            Assert.InRange(o["autojoinlevel"]!.GetValue<int>(),
                           o["applylevel"]!.GetValue<int>(), ClubFeature.MaxApplyLevel);
            Assert.NotEqual("", o["name"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// ★ 上行是 <c>JsonArray</c>（或裸标量）时也不能抛 ——
    /// <c>JsonNode["key"]</c> 对 <c>JsonArray</c> 会抛 <c>InvalidOperationException</c>，
    /// 这条在真机上崩过（AGENTS.md §5 / 咖啡馆 115 命令同款）。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task FXClubList_畸形上行不抛(string tbParam)
    {
        using var h = await RichAsync($"club-fx-bad-{tbParam.Length}-{tbParam.GetHashCode()}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, tbParam);

        Assert.Equal(ClubFeature.DiscoverClubCount,
                     Assert.IsType<JsonArray>(TbObject(h)["fx"]).Count);
    }

    /// <summary>
    /// ★ 「同一账号每次看到同一批」是硬要求（否则每次点刷新列表都变，像 bug）。
    /// 生成器是 splitmix64 确定性混淆，不是 <c>Random</c>。
    /// </summary>
    [Fact]
    public async Task FXClubList_确定性_同账号两次完全一致()
    {
        using var h = await RichAsync("club-fx-stable");

        await h.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "null");
        string first = TbObject(h).ToJsonString();
        h.Outbound.Clear();
        await h.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "null");
        string second = TbObject(h).ToJsonString();

        Assert.Equal(first, second);
    }

    /// <summary>不同账号要看到不同的列表（否则「确定性」退化成「写死」）。</summary>
    [Fact]
    public async Task FXClubList_不同账号看到不同列表()
    {
        using var a = await RichAsync("club-fx-a");
        using var b = await RichAsync("club-fx-b");

        await a.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "null");
        await b.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "null");

        Assert.NotEqual(TbObject(a).ToJsonString(), TbObject(b).ToJsonString());
    }

    /// <summary>假社团 id 恒在 <c>100001..100800</c>，与「自己的社团」段永不重叠、彼此不重号。</summary>
    [Fact]
    public async Task FXClubList_假社团id不撞号且与自己的社团分段()
    {
        using var h = await RichAsync("club-fx-ids");
        var feature = new ClubFeature(ClubTable.Load(TestPaths.SettingDir));

        var ids = feature.DiscoverClubs(h.Player).Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var id in ids)
            Assert.InRange(id, ClubFeature.NpcClubIdBase, ClubFeature.NpcClubIdBase + 800);
        Assert.True(ClubFeature.OwnClubIdOf(h.Player) > ClubFeature.NpcClubIdBase + 800);
        Assert.DoesNotContain(ClubFeature.OwnClubIdOf(h.Player), ids);
    }

    /// <summary>假社团名字同批不重复（列表里出现两个同名社团很假）。</summary>
    [Fact]
    public async Task FXClubList_假社团名字同批不重复()
    {
        using var h = await RichAsync("club-fx-names");
        var feature = new ClubFeature(ClubTable.Load(TestPaths.SettingDir));

        var names = feature.DiscoverClubs(h.Player).Select(c => c.Name).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ================================================================
    //  10004 Club_FindAClub —— ★ 族 B
    // ================================================================

    /// <summary>搜索命中自己的社团时回 primary-lite。</summary>
    [Fact]
    public async Task FindAClub_命中自己的社团()
    {
        using var h = await WithOwnClubAsync("club-find-mine");
        ulong id = h.Player.ClubId;

        await h.DispatchLuaCallAsync(ClubFeature.CmdFindAClub, $"{{\"clubid\":{id}}}");

        var tb = TbObject(h);
        Assert.Equal(id, tb["id"]!.GetValue<ulong>());
        Assert.Equal("星海事务所", tb["name"]!.GetValue<string>());
        Assert.True(tb.ContainsKey("count") && tb.ContainsKey("applylevel") && tb.ContainsKey("autojoinlevel"));
    }

    /// <summary>
    /// ★★ 族 B 红线：查不到时**只能回空表** <c>{}</c>。
    /// 回 <c>{nError=n}</c> 会因 <c>next(tbParam)</c> 为真被判成成功，
    /// 然后 <c>UI_Search:UpdateSearchList</c> 读 <c>ClubPrimaryData.count</c> 抛。
    /// </summary>
    [Fact]
    public async Task FindAClub_查不到只回空表且绝不带nError()
    {
        using var h = await RichAsync("club-find-miss");

        await h.DispatchLuaCallAsync(ClubFeature.CmdFindAClub, "{\"clubid\":424242}");

        Assert.Empty(TbObject(h));
        Assert.False(AnyReplyHasNError(h), "★ 族 B 回 nError 会被判成成功并让客户端崩");
        Assert.Contains(ClubFeature.TipNoClubData, TipKeys(h));
    }

    /// <summary>上行畸形（数组 / null）时按「查不到」处理，不抛。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1]")]
    public async Task FindAClub_畸形上行按查不到处理(string tbParam)
    {
        using var h = await RichAsync($"club-find-bad-{tbParam.Length}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdFindAClub, tbParam);

        Assert.Empty(TbObject(h));
    }

    // ================================================================
    //  10005 / 10100 申请 / 取消申请 —— ★ 族 B
    // ================================================================

    /// <summary>
    /// 等级 ≥ <c>autojoinlevel</c> ⇒ 自动入团，回**完整 club 对象**
    /// （<c>UI_Search.lua:390</c> 要求 <c>primary + applylist + memberlist</c> 三者齐全才切界面），
    /// 且 <see cref="Player.ClubId"/> 与社团对象原子一致。
    /// </summary>
    [Fact]
    public async Task ApplyClubList_自动入团_回完整club且ClubId原子一致()
    {
        using var h = await RichAsync("club-apply-auto");
        await h.DispatchLuaCallAsync(ClubFeature.CmdFxClubList, "null");
        var fx = Assert.IsType<JsonArray>(TbObject(h)["fx"]);
        var target = fx.First(n => n!["autojoinlevel"]!.GetValue<int>() <= h.Player.Level)!;
        ulong id = target["id"]!.GetValue<ulong>();
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, $"{{\"clublist\":[{id}]}}");

        var tb = TbObject(h);
        Assert.True(tb.ContainsKey("primary") && tb.ContainsKey("applylist") && tb.ContainsKey("memberlist"));
        Assert.Equal(id, h.Player.ClubId);
        Assert.Equal(id, Assert.IsType<JsonObject>(tb["primary"])["id"]!.GetValue<ulong>());
        Assert.Contains(ClubFeature.TipAutoJoin, TipKeys(h));

        // 成员表 = 假社长(pos=9, 离线) + 我(pos=0, 在线)
        var members = Assert.IsType<JsonArray>(tb["memberlist"]);
        Assert.Equal(2, members.Count);
        var me = members.First(n => n!["roleid"]!.GetValue<ulong>() == h.Player.Roleid)!;
        Assert.Equal(ClubFeature.PosOrdinary, me["pos"]!.GetValue<int>());
        var boss = members.First(n => n!["roleid"]!.GetValue<ulong>() != h.Player.Roleid)!;
        Assert.Equal(ClubFeature.PosHeader, boss["pos"]!.GetValue<int>());
        Assert.NotEqual(0L, boss["off_times"]!.GetValue<long>());   // 0 会显示成「2个月前」
    }

    /// <summary>
    /// 等级不够自动准入（但够申请）⇒ 记进「申请中」，回 <c>{applyedlist, fx}</c> 刷新列表，
    /// 按钮翻成「已申请」（客户端自己写 <c>appled</c>）。
    /// </summary>
    [Fact]
    public async Task ApplyClubList_需审批时记进申请中并回两组列表()
    {
        using var h = await RichAsync("club-apply-pending", level: 999);
        // 挑一个 autojoinlevel == 999 的（奇数位恒是这一档），把玩家等级压到 998 就只能等审批
        var feature = new ClubFeature(ClubTable.Load(TestPaths.SettingDir));
        var target = feature.DiscoverClubs(h.Player)
                            .First(c => c.AutoJoinLevel == ClubFeature.MaxApplyLevel);
        h.Player.Level = 998;

        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, $"{{\"clublist\":[{target.Id}]}}");

        Assert.Equal(0UL, h.Player.ClubId);
        var tb = TbObject(h);
        var applyed = Assert.IsType<JsonArray>(tb["applyedlist"]);
        Assert.Single(applyed);
        Assert.Equal(target.Id, applyed[0]!["id"]!.GetValue<ulong>());
        Assert.Equal(ClubFeature.DiscoverClubCount - 1,
                     Assert.IsType<JsonArray>(tb["fx"]).Count);
        Assert.Contains(target.Id, ClubBag.Read(h.Player).Applied);
    }

    /// <summary>等级低于 <c>applylevel</c> ⇒ 不记申请，提示 <c>ui.Club.LessThanApplyLevel</c>。</summary>
    [Fact]
    public async Task ApplyClubList_等级不够申请门槛被拒()
    {
        using var h = await RichAsync("club-apply-lowlv", level: 1);
        var feature = new ClubFeature(ClubTable.Load(TestPaths.SettingDir));
        var target = feature.DiscoverClubs(h.Player).First(c => c.ApplyLevel > 1);

        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, $"{{\"clublist\":[{target.Id}]}}");

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Empty(ClubBag.Read(h.Player).Applied);
        Assert.Contains(ClubFeature.TipLessThanApplyLevel, TipKeys(h));
    }

    /// <summary>★ 族 B：已有社团时申请要回**空表**（不是 <c>{nError}</c>）。</summary>
    [Fact]
    public async Task ApplyClubList_已有社团只回空表()
    {
        using var h = await WithOwnClubAsync("club-apply-own");

        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, "{\"clublist\":[100001]}");

        Assert.Empty(TbObject(h));
        Assert.False(AnyReplyHasNError(h));
        Assert.Contains(ClubFeature.TipAlreadyOwnClub, TipKeys(h));
    }

    /// <summary>上行畸形（<c>clublist</c> 不是数组 / 整个 tbParam 是数组）不抛，按「没有 id」处理。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"clublist\":123}")]
    [InlineData("{\"clublist\":[]}")]
    public async Task ApplyClubList_畸形上行不抛(string tbParam)
    {
        using var h = await RichAsync($"club-apply-bad-{tbParam.Length}-{tbParam.GetHashCode()}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, tbParam);

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Empty(TbObject(h));
    }

    /// <summary>取消申请要把 id 从「申请中」移除并回刷新后的两组列表。</summary>
    [Fact]
    public async Task CancelApply_从申请中移除()
    {
        using var h = await RichAsync("club-cancel", level: 998);
        var feature = new ClubFeature(ClubTable.Load(TestPaths.SettingDir));
        var target = feature.DiscoverClubs(h.Player)
                            .First(c => c.AutoJoinLevel == ClubFeature.MaxApplyLevel && c.ApplyLevel <= 998);
        await h.DispatchLuaCallAsync(ClubFeature.CmdApplyClubList, $"{{\"clublist\":[{target.Id}]}}");
        Assert.Single(ClubBag.Read(h.Player).Applied);
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdCancelApply, $"{{\"clublist\":[{target.Id}]}}");

        Assert.Empty(ClubBag.Read(h.Player).Applied);
        Assert.Empty(Assert.IsType<JsonArray>(TbObject(h)["applyedlist"]));
        Assert.Equal(ClubFeature.DiscoverClubCount,
                     Assert.IsType<JsonArray>(TbObject(h)["fx"]).Count);
    }

    // ================================================================
    //  10027 / 10028 / 10029
    // ================================================================

    /// <summary>
    /// ★★ 10027 必须**原样回显 <c>strType</c>** —— 不回显则
    /// <c>LuaCall.lua:2175..2183</c> 三个分支全不命中，<c>UI_ClubMember</c> 根本打不开
    /// （无遮罩无报错，是本域最难查的静默失败）。
    /// </summary>
    [Theory]
    [InlineData("OpenMem")]
    [InlineData("RefreshMem")]
    [InlineData("OpenBossMem")]
    public async Task ReqClubMember_原样回显strType(string strType)
    {
        using var h = await WithOwnClubAsync($"club-member-{strType}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubMember, $"{{\"strType\":\"{strType}\"}}");

        var tb = TbObject(h);
        Assert.Equal(strType, tb["strType"]!.GetValue<string>());
        // ★ 不写死人数（自建社团现在含机器人团员），只要求非空
        Assert.NotEmpty(Assert.IsType<JsonArray>(tb["memberlist"]));
    }

    /// <summary>
    /// 没有社团时回空表 —— 客户端 <c>else</c> 分支（<c>LuaCall.lua:2186..2190</c>）会
    /// <c>DissolvedClub()</c> 弹「社团已解散」回主城，这正是「社团没了」的正确表达。
    /// </summary>
    [Fact]
    public async Task ReqClubMember_没有社团回空表()
    {
        using var h = await RichAsync("club-member-none");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubMember, "{\"strType\":\"OpenMem\"}");

        Assert.Empty(TbObject(h));
    }

    /// <summary>上行是 null / 数组时不抛；<c>strType</c> 缺失时**不伪造**（不能替玩家切界面）。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task ReqClubMember_畸形上行不抛且不伪造strType(string tbParam)
    {
        using var h = await WithOwnClubAsync($"club-member-bad-{tbParam.Length}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubMember, tbParam);

        var tb = TbObject(h);
        Assert.True(tb.ContainsKey("memberlist"));
        Assert.False(tb.ContainsKey("strType"));
    }

    /// <summary>申请列表恒空（单机没有别的玩家来申请），但键必须在。</summary>
    [Fact]
    public async Task ReqApplylist_回空applylist()
    {
        using var h = await WithOwnClubAsync("club-applylist");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqApplylist, "null");

        Assert.Empty(Assert.IsType<JsonArray>(TbObject(h)["applylist"]));
    }

    /// <summary>没有社团时 10028 也回空表（客户端 <c>DissolvedClub</c>）。</summary>
    [Fact]
    public async Task ReqApplylist_没有社团回空表()
    {
        using var h = await RichAsync("club-applylist-none");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqApplylist, "null");

        Assert.Empty(TbObject(h));
    }

    /// <summary>没有社团时 10029 也回空表（回包才是打开 <c>UI_ClubBook</c> 的触发器）。</summary>
    [Fact]
    public async Task ReqClubLog_没有社团回空表()
    {
        using var h = await RichAsync("club-log-none");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubLog, "null");

        Assert.Empty(TbObject(h));
    }

    /// <summary>日志裁到 50 条（<c>ui.Club.BookTip</c>「社团日志保留最新的50条」）。</summary>
    [Fact]
    public void 社团日志_裁到50条且保留最新()
    {
        var club = new ClubState();
        for (int i = 0; i < 60; i++)
            club.AddLog(ClubLogType.Create, 1000 + i, $"第{i}条");

        Assert.Equal(ClubState.MaxLogs, club.Logs.Count);
        Assert.Equal("第59条", club.Logs[^1].Strings[0]);
        Assert.Equal("第10条", club.Logs[0].Strings[0]);
    }

    // ================================================================
    //  10008 Club_ModifyNotice —— ★ 族 B + 裸数组
    // ================================================================

    /// <summary>
    /// ★★ 回包是**裸数组** <c>["新公告"]</c>（<c>UI_Club.lua:396</c> 读 <c>tbParam[1]</c>），
    /// 不是 <c>{notice=…}</c>。裸数组天然满足族 B 的「非空表」判据、天然不含 <c>nError</c>。
    /// </summary>
    [Fact]
    public async Task ModifyNotice_回裸数组且落盘()
    {
        using var h = await WithOwnClubAsync("club-notice");

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice, "{\"newnotice\":\"今晚八点集合打黑境\"}");

        var arr = Assert.IsType<JsonArray>(TbParam(h));
        Assert.Single(arr);
        Assert.Equal("今晚八点集合打黑境", arr[0]!.GetValue<string>());
        Assert.Equal("今晚八点集合打黑境", ClubBag.Read(h.Player).Club!.Notice);

        // round-trip：10002 要能把它带回来
        h.Outbound.Clear();
        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubInfo, "null");
        Assert.Equal("今晚八点集合打黑境",
            Assert.IsType<JsonObject>(TbObject(h)["primary"])["notice"]!.GetValue<string>());
    }

    /// <summary>改公告要写一条 type 8 日志（strlist 长度必须是 2）。</summary>
    [Fact]
    public async Task ModifyNotice_写下type8日志()
    {
        using var h = await WithOwnClubAsync("club-notice-log");
        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice, "{\"newnotice\":\"新公告\"}");
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqClubLog, "null");

        var logs = Assert.IsType<JsonArray>(TbObject(h)["actionloglist"]);
        var last = Assert.IsType<JsonObject>(logs[^1]);
        Assert.Equal(ClubLogType.ModifyNotice, last["type"]!.GetValue<int>());
        Assert.Equal(2, Assert.IsType<JsonArray>(last["strlist"]).Count);
    }

    /// <summary>
    /// ★★ 族 B 红线：公告超长 / 空 / 没社团 / 没权限，全部只能回**空表**。
    /// 回 <c>{nError}</c> 会被 <c>next(tbParam)</c> 判成成功。
    /// </summary>
    [Fact]
    public async Task ModifyNotice_超长只回空表且不带nError()
    {
        using var h = await WithOwnClubAsync("club-notice-long");
        string tooLong = new('中', ClubFeature.MaxNoticeWidth / 2 + 1);   // 宽度 142 > 140

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice,
                                     $"{{\"newnotice\":\"{tooLong}\"}}");

        Assert.Empty(TbObject(h));
        Assert.False(AnyReplyHasNError(h));
        Assert.Contains(ClubFeature.TipMaxNotice, TipKeys(h));
        Assert.Equal("", ClubBag.Read(h.Player).Club!.Notice);
    }

    /// <summary>刚好 140 宽度要放行（边界与客户端 <c>ClubCommon.lua:401</c> 完全一致）。</summary>
    [Fact]
    public async Task ModifyNotice_边界140放行()
    {
        using var h = await WithOwnClubAsync("club-notice-140");
        string exact = new('中', ClubFeature.MaxNoticeWidth / 2);

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice, $"{{\"newnotice\":\"{exact}\"}}");

        Assert.Equal(exact, ClubBag.Read(h.Player).Club!.Notice);
    }

    /// <summary>没有社团时改公告只回空表 + 提示。</summary>
    [Fact]
    public async Task ModifyNotice_没有社团只回空表()
    {
        using var h = await RichAsync("club-notice-none");

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice, "{\"newnotice\":\"x\"}");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipNoClubData, TipKeys(h));
    }

    /// <summary>普通成员无权改公告（客户端 <c>UI_Club.lua:217</c> 也不显示按钮，但 GM 绕得过）。</summary>
    [Fact]
    public async Task ModifyNotice_普通成员无权限()
    {
        using var h = await WithJoinedClubAsync("club-notice-noauth");

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice, "{\"newnotice\":\"我要改公告\"}");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipNoAuthority, TipKeys(h));
        Assert.Equal("", ClubBag.Read(h.Player).Club!.Notice);
    }

    // ================================================================
    //  10014 Club_ModifyApply —— 回整个 primary
    // ================================================================

    /// <summary>
    /// ★★ 回包**整个 tbParam 就是 primary**（<c>UI_ClubMember.lua:663</c> 是
    /// <c>MyClubInfo.primary = tbParam</c> 整体替换）⇒ 只回 <c>{applylevel, autojoinlevel}</c>
    /// 会把社团名/等级/公告全冲掉。
    /// </summary>
    [Fact]
    public async Task ModifyApply_回整个primary对象()
    {
        using var h = await WithOwnClubAsync("club-modifyapply");

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyApply,
                                     "{\"applylevel\":30,\"autojoinlevel\":80}");

        var tb = TbObject(h);
        Assert.Equal(30, tb["applylevel"]!.GetValue<int>());
        Assert.Equal(80, tb["autojoinlevel"]!.GetValue<int>());
        // ★ 必须是完整 primary，不是两个字段
        Assert.Equal("星海事务所", tb["name"]!.GetValue<string>());
        Assert.Equal(h.Player.ClubId, tb["id"]!.GetValue<ulong>());
        Assert.True(tb.ContainsKey("reqdissolvetime") && tb.ContainsKey("weektaskvalue"));
        Assert.False(tb.ContainsKey("nError"), "★ 族 A 判据 if tbParam.nError then ⇒ primary 里绝不能有 nError");

        Assert.Equal(30, ClubBag.Read(h.Player).Club!.ApplyLevel);
        Assert.Equal(80, ClubBag.Read(h.Player).Club!.AutoJoinLevel);
    }

    /// <summary>非法等级对（含越界与 <c>applylevel &gt; autojoinlevel</c>）要被拒且不改状态。</summary>
    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 1000)]
    [InlineData(80, 30)]
    public async Task ModifyApply_非法等级被拒(int applyLv, int autoLv)
    {
        using var h = await WithOwnClubAsync($"club-modifyapply-bad-{applyLv}-{autoLv}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyApply,
                                     $"{{\"applylevel\":{applyLv},\"autojoinlevel\":{autoLv}}}");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipErrorCreateLevel, TipKeys(h));
        Assert.Equal(10, ClubBag.Read(h.Player).Club!.ApplyLevel);
        Assert.Equal(20, ClubBag.Read(h.Player).Club!.AutoJoinLevel);
    }

    /// <summary>普通成员无权改申请条件。</summary>
    [Fact]
    public async Task ModifyApply_普通成员无权限()
    {
        using var h = await WithJoinedClubAsync("club-modifyapply-noauth");

        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyApply,
                                     "{\"applylevel\":1,\"autojoinlevel\":1}");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipNoAuthority, TipKeys(h));
    }

    // ================================================================
    //  10012 Club_Quit
    // ================================================================

    /// <summary>社长不能退团，只能解散（<c>ui.Club.HeaderOnlyCanDissolve</c>）。</summary>
    [Fact]
    public async Task Quit_社长被拒且社团不动()
    {
        using var h = await WithOwnClubAsync("club-quit-header");
        ulong id = h.Player.ClubId;

        await h.DispatchLuaCallAsync(ClubFeature.CmdQuit, "null");

        Assert.Equal(id, h.Player.ClubId);
        Assert.NotNull(ClubBag.Read(h.Player).Club);
        Assert.Contains(ClubFeature.TipHeaderCannotQuit, TipKeys(h));
    }

    /// <summary>
    /// ★★ 普通成员退团：<see cref="Player.ClubId"/> 与社团对象必须**一起**清空
    /// （只清一半 ⇒ 下次登录 pf 1005 带着非 0 clubid，客户端在 <c>UI_ClubReward</c> 崩）。
    /// 回包是空表，客户端据此 <c>me.clubid = 0</c> 并回主城。
    /// </summary>
    [Fact]
    public async Task Quit_普通成员退团后ClubId与社团对象一起清空()
    {
        using var h = await WithJoinedClubAsync("club-quit-member");
        Assert.NotEqual(0UL, h.Player.ClubId);

        await h.DispatchLuaCallAsync(ClubFeature.CmdQuit, "null");

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Null(ClubBag.Read(h.Player).Club);
        Assert.Empty(TbObject(h));
        Assert.False(AnyReplyHasNError(h));
        Assert.Contains(ClubFeature.TipQuitSuccess, TipKeys(h));

        // 落盘 + 写任务值 (1,28)（客户端 UI_Search.lua:83 用它显示再入团倒计时）
        var reloaded = h.Module.World.GetOrCreatePlayer("club-quit-member");
        Assert.Equal(0UL, reloaded.ClubId);
        Assert.Null(ClubBag.Read(reloaded).Club);
        Assert.True(reloaded.GetTask(ClubFeature.TaskGroup, ClubFeature.TaskLastQuitTime) > 0);
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp), "没有推 pf 1026 同步任务值 (1,28)");
    }

    /// <summary>本来就没社团时退团只回空表 + 提示。</summary>
    [Fact]
    public async Task Quit_没有社团只回空表()
    {
        using var h = await RichAsync("club-quit-none");

        await h.DispatchLuaCallAsync(ClubFeature.CmdQuit, "null");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipNoClubData, TipKeys(h));
    }

    // ================================================================
    //  10022 Club_ReqDissolve
    // ================================================================

    /// <summary>
    /// ★★ 上行键名是原始 typo <c>dissoslve</c>（<c>UI_ClubMember.lua:514</c>）。
    /// 申请解散要写 <c>reqdissolvetime</c> 并回**完整 club 对象**
    /// （<c>UI_ClubMember:ShowDissolve</c> 读 <c>primary.reqdissolvetime</c> 起倒计时）。
    /// </summary>
    [Fact]
    public async Task ReqDissolve_申请解散写reqdissolvetime并回完整club()
    {
        using var h = await WithOwnClubAsync("club-dissolve");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":1}");

        var primary = Assert.IsType<JsonObject>(TbObject(h)["primary"]);
        long t = primary["reqdissolvetime"]!.GetValue<long>();
        Assert.True(t > 0, "reqdissolvetime 必须是非 0 时间戳（0 = 未申请）");
        Assert.Equal(t, ClubBag.Read(h.Player).Club!.ReqDissolveTime);
        Assert.Equal((int)t, h.Player.GetTask(ClubFeature.TaskGroup, ClubFeature.TaskDissolveTime));
        Assert.NotEqual(0UL, h.Player.ClubId);        // 申请解散期间社团还在
    }

    /// <summary><c>dissoslve=0</c> 取消解散，<c>reqdissolvetime</c> 归 0。</summary>
    [Fact]
    public async Task ReqDissolve_取消解散清零()
    {
        using var h = await WithOwnClubAsync("club-dissolve-cancel");
        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":1}");
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":0}");

        var primary = Assert.IsType<JsonObject>(TbObject(h)["primary"]);
        Assert.Equal(0L, primary["reqdissolvetime"]!.GetValue<long>());
        Assert.Equal(0L, ClubBag.Read(h.Player).Club!.ReqDissolveTime);
    }

    /// <summary>24 小时内二次申请解散要被拒（<c>ui.Club.DissolveLimit</c>）。</summary>
    [Fact]
    public async Task ReqDissolve_24小时内二次申请被拒()
    {
        using var h = await WithOwnClubAsync("club-dissolve-cd");
        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":1}");
        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":0}");
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":1}");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipDissolveLimit, TipKeys(h));
        Assert.Equal(0L, ClubBag.Read(h.Player).Club!.ReqDissolveTime);
    }

    /// <summary>非社长不能解散。</summary>
    [Fact]
    public async Task ReqDissolve_非社长被拒()
    {
        using var h = await WithJoinedClubAsync("club-dissolve-noauth");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, "{\"dissoslve\":1}");

        Assert.Empty(TbObject(h));
        Assert.Contains(ClubFeature.TipNoAuthority, TipKeys(h));
    }

    /// <summary>上行畸形（null / 数组）不抛。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task ReqDissolve_畸形上行不抛(string tbParam)
    {
        using var h = await WithOwnClubAsync($"club-dissolve-bad-{tbParam.Length}");

        await h.DispatchLuaCallAsync(ClubFeature.CmdReqDissolve, tbParam);

        // 缺 dissoslve 键时按「申请解散」（默认 1）处理，不能抛
        Assert.True(ClubBag.Read(h.Player).Club!.ReqDissolveTime > 0);
    }

    // ================================================================
    //  10026 Club_SendMail
    // ================================================================

    /// <summary>发社团邮件回 <c>{nCount = 今日已发次数}</c> 并落盘。</summary>
    [Fact]
    public async Task SendMail_计数并回nCount()
    {
        using var h = await WithOwnClubAsync("club-mail");

        await h.DispatchLuaCallAsync(ClubFeature.CmdSendMail,
                                     "{\"title\":\"社团邮件\",\"content\":\"今晚八点集合\"}");

        Assert.Equal(1, TbObject(h)["nCount"]!.GetValue<int>());
        Assert.Equal(1, ClubBag.Read(h.Player).Club!.SendMailTimes);
    }

    /// <summary>
    /// ★★ 没有社团时**刻意不回包** —— <c>ClubCommon:SendMailCallback</c>
    /// （<c>ClubCommon.lua:436</c>）第一句就是无保护的 <c>MyClubInfo.primary.send_mail_times</c>，
    /// 回任何包都会抛。社团域不挂遮罩，所以不回包是安全的。
    /// </summary>
    [Fact]
    public async Task SendMail_没有社团时刻意不回包()
    {
        using var h = await RichAsync("club-mail-none");

        await h.DispatchLuaCallAsync(ClubFeature.CmdSendMail,
                                     "{\"title\":\"t\",\"content\":\"c\"}");

        Assert.DoesNotContain(h.Outbound, x => x.Function == LuaCallRouter.LuaCallFunction);
        Assert.Contains(ClubFeature.TipNoClubData, TipKeys(h));
    }

    /// <summary>超过每日 10 封要被拒，但仍要回当前次数（客户端界面显示 x/10）。</summary>
    [Fact]
    public async Task SendMail_超过10封被拒()
    {
        using var h = await WithOwnClubAsync("club-mail-limit");
        for (int i = 0; i < ClubFeature.MaxSendMailTimes; i++)
            await h.DispatchLuaCallAsync(ClubFeature.CmdSendMail, "{\"title\":\"t\",\"content\":\"c\"}");
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdSendMail, "{\"title\":\"t\",\"content\":\"c\"}");

        Assert.Equal(ClubFeature.MaxSendMailTimes, TbObject(h)["nCount"]!.GetValue<int>());
        Assert.Contains(ClubFeature.TipMailLimit, TipKeys(h));
        Assert.Equal(ClubFeature.MaxSendMailTimes, ClubBag.Read(h.Player).Club!.SendMailTimes);
    }

    /// <summary>普通成员无权发社团邮件（同样不回包，只发 Tip）。</summary>
    [Fact]
    public async Task SendMail_普通成员无权限且不回包()
    {
        using var h = await WithJoinedClubAsync("club-mail-noauth");

        await h.DispatchLuaCallAsync(ClubFeature.CmdSendMail, "{\"title\":\"t\",\"content\":\"c\"}");

        Assert.DoesNotContain(h.Outbound, x => x.Function == LuaCallRouter.LuaCallFunction);
        Assert.Contains(ClubFeature.TipNoAuthority, TipKeys(h));
    }

    // ================================================================
    //  10020 Club_DailyAward
    // ================================================================

    /// <summary>
    /// 每日福利按 <c>ClubLevels[level].DailyAwards</c> 真实发奖（1 级 = 5 社团贡献 + 2000 金币），
    /// 并写任务值 <c>(1,35)</c> + 推 pf 1026 / 1032。
    /// ★ 不写 (1,35) 的后果是「重登后按钮又亮、可无限领」（<c>UI_ClubReward.lua:47..51</c>）。
    /// </summary>
    [Fact]
    public async Task DailyAward_按配置发奖并写任务值()
    {
        using var h = await WithOwnClubAsync("club-daily");
        int goldBefore = h.Player.GetMoney(MoneyType.Gold);
        int contribBefore = h.Player.GetMoney(11);

        await h.DispatchLuaCallAsync(ClubFeature.CmdDailyAward, "null");

        var lv1 = ClubTable.Load(TestPaths.SettingDir).Level(1);
        Assert.Equal(2, lv1.DailyAwards.Count);
        Assert.Equal(goldBefore + 2000, h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(contribBefore + 5, h.Player.GetMoney(11));        // 15-14 → 货币 11 社团贡献
        Assert.True(h.Player.GetTask(ClubFeature.TaskGroup, ClubFeature.TaskDailyAwardDay) > 0);
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf), "发奖必须推 pf 1032，否则界面数值不动");
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp), "必须推 pf 1026 同步 (1,35)");
    }

    /// <summary>同一天重复领取不能再发奖（服务端侧的兜底判据是任务值 (1,35)）。</summary>
    [Fact]
    public async Task DailyAward_同日重复领取不发奖()
    {
        using var h = await WithOwnClubAsync("club-daily-twice");
        await h.DispatchLuaCallAsync(ClubFeature.CmdDailyAward, "null");
        int gold = h.Player.GetMoney(MoneyType.Gold);

        await h.DispatchLuaCallAsync(ClubFeature.CmdDailyAward, "null");

        Assert.Equal(gold, h.Player.GetMoney(MoneyType.Gold));
    }

    /// <summary>
    /// ★ 服务端的 (1,35) 只能取 <c>max</c>：客户端 <c>GetLocalDay4AM()</c> 带
    /// <c>3600*Zone</c> 时区偏移（<c>Lib.lua:1702</c>），值只会 ≥ 服务端日序，
    /// 直接覆盖会把它调小 ⇒ 重登后按钮又亮。
    /// </summary>
    [Fact]
    public async Task DailyAward_客户端写的更大日序不被调小()
    {
        using var h = await WithOwnClubAsync("club-daily-zone");
        // 模拟客户端 pf 1027 已经上报了一个「更大」的日序（时区偏移导致）
        int clientDay = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow) + 1;
        h.Player.SetTask(ClubFeature.TaskGroup, ClubFeature.TaskDailyAwardDay, clientDay);
        int gold = h.Player.GetMoney(MoneyType.Gold);

        await h.DispatchLuaCallAsync(ClubFeature.CmdDailyAward, "null");

        Assert.Equal(gold, h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(clientDay, h.Player.GetTask(ClubFeature.TaskGroup, ClubFeature.TaskDailyAwardDay));
    }

    /// <summary>没有社团时不发奖。</summary>
    [Fact]
    public async Task DailyAward_没有社团不发奖()
    {
        using var h = await RichAsync("club-daily-none");
        int gold = h.Player.GetMoney(MoneyType.Gold);

        await h.DispatchLuaCallAsync(ClubFeature.CmdDailyAward, "null");

        Assert.Equal(gold, h.Player.GetMoney(MoneyType.Gold));
        Assert.Contains(ClubFeature.TipNoClubData, TipKeys(h));
    }

    // ================================================================
    //  ★★ 三种「指数」道具不进背包
    // ================================================================

    /// <summary>
    /// ★★★ 本域最容易写成 bug 的一处（取证文档 §9.1.1）。
    ///
    /// 这条测试**先反证危险是真的**：把四个「指数」道具直接丢给
    /// <see cref="RewardService.Grant"/>，只有 <c>15-14</c> 会被折算成货币 11
    /// （<c>Config/ItemBaseTable.cs:202</c> 是唯一的映射），另外三个**全部变成背包道具**。
    /// 然后验 <see cref="ClubFeature.SplitIndexAwards"/> 把它们摘干净、且摘出来的数量正确。
    /// </summary>
    [Fact]
    public async Task 三种指数道具_直接Grant会进背包_必须先拦下来()
    {
        var all = new List<int[]>
        {
            new[] { 15, 14, 1, 1, 5 },     // 战斗指数 ClubCoinAuto        → 货币 11（已映射）
            new[] { 15, 13, 1, 1, 50 },    // 团队指数 ClubExpAuto         → 社团经验（未映射）
            new[] { 15, 12, 1, 1, 30 },    // 活跃指数 ClubActivePointAuto → 社团活跃点（未映射）
            new[] { 15, 36, 1, 1, 2 },     // 出击次数 ItemAddClubBossFightTimeAuto → 任务值 (1,552)
        };

        // ---- 反证：不拦的话那三个会变成背包道具 ----
        using var naive = await FeatureHarness.CreateAsync("club-index-naive");
        var leaked = naive.Module.Rewards.Grant(naive.Player, all, RewardSource.Extra);
        Assert.Contains(11, leaked.ChangedMoney);
        var leakedKeys = leaked.ChangedItems.Select(i => (i.Genre, i.Detail)).ToHashSet();
        Assert.Contains(((uint)15, (uint)13), leakedKeys);
        Assert.Contains(((uint)15, (uint)12), leakedKeys);
        Assert.Contains(((uint)15, (uint)36), leakedKeys);

        // ---- 正解：先拆再发 ----
        using var ok = await FeatureHarness.CreateAsync("club-index-split");
        int itemsBefore = ok.Player.Items.Count;        // 新号自带一批初始物品，只看「有没有变多」
        int contribBefore = ok.Player.GetMoney(11);
        var (normal, clubExp, activePoint, bossTimes) = ClubFeature.SplitIndexAwards(all);
        Assert.Single(normal);
        Assert.Equal(new[] { 15, 14, 1, 1, 5 }, normal[0]);
        Assert.Equal(50, clubExp);
        Assert.Equal(30, activePoint);
        Assert.Equal(2, bossTimes);

        var granted = ok.Module.Rewards.Grant(ok.Player, normal, RewardSource.Extra);
        Assert.Empty(granted.ChangedItems);                 // ★ 背包一件都不能多
        Assert.Equal(itemsBefore, ok.Player.Items.Count);
        Assert.Equal(new[] { 11 }, granted.ChangedMoney);
        Assert.Equal(contribBefore + 5, ok.Player.GetMoney(11));
    }

    /// <summary>拆分函数不能误伤别的道具（只认 <c>15-{12,13,36}-1-1</c> 这三个精确四元组）。</summary>
    [Fact]
    public void SplitIndexAwards_只拦三种精确四元组()
    {
        var (normal, exp, active, times) = ClubFeature.SplitIndexAwards(new List<int[]>
        {
            new[] { 15, 1, 1, 1, 2000 },     // 金币，不拦
            new[] { 15, 13, 1, 2, 7 },       // Particular/Level 不是 1-1 ⇒ 不拦
            new[] { 14, 13, 1, 1, 7 },       // Genre 不是 15 ⇒ 不拦
            new[] { 15, 13, 1, 1, 7 },       // ★ 拦
            new[] { 15, 13 },                // 长度 < 4 ⇒ 丢弃（不拦也不发）
        });

        Assert.Equal(3, normal.Count);
        Assert.Equal(7, exp);
        Assert.Equal(0, active);
        Assert.Equal(0, times);
    }

    /// <summary>空输入 / null 不抛。</summary>
    [Fact]
    public void SplitIndexAwards_空输入不抛()
    {
        var (normal, exp, active, times) = ClubFeature.SplitIndexAwards(null);
        Assert.Empty(normal);
        Assert.Equal(0, exp + active + times);
    }

    // ================================================================
    //  登录钩子（ClubId 不变量 + 解散到期）
    // ================================================================

    /// <summary>
    /// ★★★ 登录钩子必须在 pf 1005 **之前**把「ClubId 非 0 但没有社团对象」修掉 ——
    /// 否则 <c>GameModule.cs:590</c> 会把那个非 0 clubid 发给客户端，
    /// <c>UI_ClubReward.lua:35</c> 的 <c>next(nil)</c> 必崩。
    /// </summary>
    [Fact]
    public async Task 登录钩子_ClubId不变量自愈()
    {
        using var h = await FeatureHarness.CreateAsync("club-hook-heal");
        h.Player.ClubId = 424242;
        var feature = new ClubFeature(ClubTable.Empty);

        Assert.True(feature.ApplyLoginInvariant(h.Player, "test"));

        Assert.Equal(0UL, h.Player.ClubId);
    }

    /// <summary>不变量本来就成立时不该报 dirty（避免每次登录都无意义写盘）。</summary>
    [Fact]
    public async Task 登录钩子_不变量成立时不写盘()
    {
        using var h = await FeatureHarness.CreateAsync("club-hook-clean");
        var feature = new ClubFeature(ClubTable.Empty);

        Assert.False(feature.ApplyLoginInvariant(h.Player, "test"));
        Assert.Equal(0UL, h.Player.ClubId);
    }

    /// <summary>
    /// 申请解散满 <see cref="ClubFeature.DissolveWaitSeconds"/>（3 天 - 1 秒）后，
    /// 登录钩子要正式解散：社团对象与 <see cref="Player.ClubId"/> 一起清空。
    /// </summary>
    [Fact]
    public async Task 登录钩子_解散到期正式解散()
    {
        using var h = await WithOwnClubAsync("club-hook-dissolve");
        var bag = ClubBag.Read(h.Player);
        bag.Club!.ReqDissolveTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    - ClubFeature.DissolveWaitSeconds - 10;
        bag.Write(h.Player);
        var feature = new ClubFeature(ClubTable.Empty);

        Assert.True(feature.ApplyLoginInvariant(h.Player, "test"));

        Assert.Equal(0UL, h.Player.ClubId);
        Assert.Null(ClubBag.Read(h.Player).Club);
        Assert.True(h.Player.GetTask(ClubFeature.TaskGroup, ClubFeature.TaskLastQuitTime) > 0);
    }

    /// <summary>还没到期的解散申请不能被提前执行。</summary>
    [Fact]
    public async Task 登录钩子_解散未到期不动()
    {
        using var h = await WithOwnClubAsync("club-hook-pending");
        ulong id = h.Player.ClubId;
        var bag = ClubBag.Read(h.Player);
        bag.Club!.ReqDissolveTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        bag.Write(h.Player);
        var feature = new ClubFeature(ClubTable.Empty);

        feature.ApplyLoginInvariant(h.Player, "test");

        Assert.Equal(id, h.Player.ClubId);
        Assert.NotNull(ClubBag.Read(h.Player).Club);
    }

    /// <summary>登录钩子要把「我」这条成员快照刷新成在线 + 当前等级/名字。</summary>
    [Fact]
    public async Task 登录钩子_刷新我的成员快照()
    {
        using var h = await WithOwnClubAsync("club-hook-snapshot");
        h.Player.Name = "新名字";
        h.Player.Level = 88;
        var feature = new ClubFeature(ClubTable.Empty);

        Assert.True(feature.ApplyLoginInvariant(h.Player, "test"));

        var me = ClubBag.Read(h.Player).Club!.Member(h.Player.Roleid)!;
        Assert.Equal("新名字", me.Name);
        Assert.Equal(88, me.Level);
        Assert.Equal(1, me.State);
    }

    // ================================================================
    //  存档 round-trip 与畸形存档
    // ================================================================

    /// <summary>
    /// ★★ <see cref="ClubBag.Write"/> 是「<c>ClubId</c> 与社团对象原子写」的唯一入口 ——
    /// 它必须同时写 <c>Extra["club"]</c> 与 <see cref="Player.ClubId"/>。
    /// </summary>
    [Fact]
    public void ClubBag_Write必须同步写ClubId()
    {
        var player = new Player("bag-write", 42);
        var bag = new ClubBag { Club = new ClubState { Id = 200999, Name = "测试社团" } };

        bag.Write(player);

        Assert.Equal(200999UL, player.ClubId);
        bag.Club = null;
        bag.Write(player);
        Assert.Equal(0UL, player.ClubId);
    }

    /// <summary>畸形 <c>Extra["club"]</c> 要降级成「没有社团」而不是抛（存档是本地文件，可能被手改坏）。</summary>
    [Theory]
    [InlineData("\"字符串\"")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"club\":123}")]
    [InlineData("{\"club\":{},\"applied\":\"x\"}")]
    public void ClubBag_畸形存档降级不抛(string json)
    {
        var player = new Player("bag-bad", 42);
        player.Extra["club"] = JsonNode.Parse(json);

        var bag = ClubBag.Read(player);

        Assert.DoesNotContain(0UL, bag.Applied);
        // {"club":{}} 是合法对象 ⇒ 会解出一个 id=0 的空社团；其余情况一律是 null
        if (json.Contains("\"club\":{}"))
            Assert.NotNull(bag.Club);
        else
            Assert.Null(bag.Club);
    }

    /// <summary>社团对象整体 round-trip（成员 + 日志 + 全部标量字段）。</summary>
    [Fact]
    public void ClubState_存档roundtrip()
    {
        var club = new ClubState
        {
            Id = 200123, Name = "回环社", Level = 4, Exp = 31000, ActivePoint = 77,
            ApplyLevel = 12, AutoJoinLevel = 34, Notice = "公告内容",
            ReqDissolveTime = 1690000000, SendMailTimes = 3, SendMailDay = 19000,
            WeekTaskValue = 555, CreateTime = 1680000000, LastDissolveReqTime = 1690000000,
            HeaderId = 99, HeaderName = "社长",
        };
        club.Members.Add(new ClubMemberState
        {
            Roleid = 99, Name = "社长", Level = 60, Pos = ClubFeature.PosHeader, State = 1,
            GirlIconId = 7, HeadFrameId = 0, OffTimes = 123, Last7Contribute = 8,
            DailyContribute = 2, ListSkinId = 0, JoinDate = 1680000000,
        });
        club.AddLog(ClubLogType.ChangePos, 1690000001, "甲", "乙", "8", "0");

        var back = ClubState.FromStorage(club.ToStorage());

        Assert.Equal(club.ToStorage().ToJsonString(), back.ToStorage().ToJsonString());
        Assert.Equal(4, back.Logs[0].Strings.Count);   // ★ type 6 的 strlist 必须是 4 个
    }

    // ================================================================
    //  配置表
    // ================================================================

    /// <summary><c>ClubLevels.txt</c> 要读到 10 级，且第 1 级与取证文档 §9.1 的逐字副本一致。</summary>
    [Fact]
    public void ClubTable_读到10级且第1级与现网一致()
    {
        var table = ClubTable.Load(TestPaths.SettingDir);

        Assert.Equal(10, table.MaxLevel);
        Assert.Equal(10, table.Levels.Count);
        var lv1 = table.Level(1);
        Assert.Equal(0, lv1.LvUpExp);
        Assert.Equal(30, lv1.CountLmt);
        Assert.Equal(3, lv1.CreateFeeType);
        Assert.Equal(250, lv1.CreateFee);
        Assert.Equal(7, lv1.DismissTime);
        Assert.Equal(17, lv1.MaxShopColumnNum);
        Assert.Equal(30, lv1.Boss);
        Assert.Equal(3, lv1.DailyKickLimit);
        Assert.Equal(2, lv1.DailyAwards.Count);
        Assert.Equal(new[] { 15, 14, 1, 1, 5 }, lv1.DailyAwards[0]);
        Assert.Equal(new[] { 15, 1, 1, 1, 2000 }, lv1.DailyAwards[1]);
        // LvUpExp 是累计门槛且单调递增（取证文档 §9.1）
        Assert.Equal(new[] { 0, 10000, 20000, 30000, 50000, 70000, 90000, 110000, 130000, 150000 },
                     table.Levels.Select(r => r.LvUpExp).ToArray());
        Assert.Equal(new[] { 30, 32, 34, 36, 38, 40, 43, 45, 47, 50 },
                     table.Levels.Select(r => r.CountLmt).ToArray());
    }

    /// <summary>由累计 exp 反算等级（<c>LvUpExp</c> 是累计门槛不是本级增量）。</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(9999, 1)]
    [InlineData(10000, 2)]
    [InlineData(49999, 4)]
    [InlineData(150000, 10)]
    [InlineData(999999, 10)]
    public void ClubTable_由exp反算等级(int exp, int level)
        => Assert.Equal(level, ClubTable.Load(TestPaths.SettingDir).LevelForExp(exp));

    /// <summary>缺配置表要降级成空表 + 回落到第 1 级兜底值，**绝不抛**（与其它配置表一致）。</summary>
    [Fact]
    public void ClubTable_缺文件降级不抛()
    {
        var table = ClubTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-dir-" + Guid.NewGuid()));

        Assert.Empty(table.Levels);
        Assert.Equal(ClubTable.MaxLevelFallback, table.MaxLevel);
        Assert.False(table.HasLevel(1));
        Assert.Same(ClubTable.FallbackLevel1, table.Level(1));
        Assert.Same(ClubTable.FallbackLevel1, table.Level(99));
        Assert.Equal(1, table.LevelForExp(999999));
    }

    /// <summary>
    /// <c>DailyAwards</c> 的写法是「一个中括号里逗号分隔多个五元组」，
    /// ★ 不能用 <c>ChapterTable.ParseTuples</c>（那个会把 <c>5,15</c> 当一段而整行丢掉）。
    /// </summary>
    [Fact]
    public void ClubTable_ParseAwardList解出多个五元组()
    {
        var got = ClubTable.ParseAwardList("[15-14-1-1-5,15-1-1-1-2000]");

        Assert.Equal(2, got.Count);
        Assert.Equal(new[] { 15, 14, 1, 1, 5 }, got[0]);
        Assert.Equal(new[] { 15, 1, 1, 1, 2000 }, got[1]);
        Assert.Empty(ClubTable.ParseAwardList(""));
        Assert.Empty(ClubTable.ParseAwardList("[abc]"));
    }

    // ================================================================
    //  装配 / 通用健壮性
    // ================================================================

    /// <summary>装配后功能名与 15 个命令都就位（反射装配的回归护栏）。</summary>
    [Fact]
    public async Task 装配_15个命令与配置表都就位()
    {
        using var h = await FeatureHarness.CreateAsync("club-install");
        var feature = new ClubFeature();

        Assert.Contains("社团", feature.Name);
        // ★ 15 基础 + 第十五轮的社团管理 5 条 = 20
        Assert.Equal(20, ClubFeature.HandledCmds.Count);
        foreach (var mgr in new[] { ClubFeature.CmdAgree, ClubFeature.CmdIgnore,
                                    ClubFeature.CmdChangePosition, ClubFeature.CmdHeaderChange,
                                    ClubFeature.CmdKickMember })
            Assert.Contains(mgr, ClubFeature.HandledCmds);
        Assert.Equal(ClubFeature.HandledCmds.Count, ClubFeature.HandledCmds.Distinct().Count());
        foreach (var cmd in ClubFeature.HandledCmds)
            Assert.InRange(cmd, 10001, 10100);

        // 每个命令都真的能被分发到（不是只打 unhandled）—— 至少要产生一条下行或不抛
        foreach (var cmd in ClubFeature.HandledCmds)
            await h.DispatchLuaCallAsync(cmd, "null");
    }

    /// <summary>★ 未登录的会话发任何社团命令都不能抛（真机上任何顺序都可能到）。</summary>
    [Fact]
    public async Task 未登录会话_所有命令都不抛()
    {
        using var h = await FeatureHarness.CreateAsync("club-anon", attachPlayer: false);

        foreach (var cmd in ClubFeature.HandledCmds)
            await h.DispatchLuaCallAsync(cmd, "null");

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// ★ 15 个命令 × 5 种畸形上行都不能抛。
    /// 这条对应 AGENTS.md 记录过的真机崩溃：<c>JsonNode["key"]</c> 对 <c>JsonArray</c> 会抛。
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[1,2,3]")]
    [InlineData("true")]
    [InlineData("\"字符串\"")]
    public async Task 所有命令_畸形上行都不抛(string tbParam)
    {
        using var h = await WithOwnClubAsync($"club-all-bad-{tbParam.Length}-{tbParam.GetHashCode()}");

        foreach (var cmd in ClubFeature.HandledCmds)
            await h.DispatchLuaCallAsync(cmd, tbParam);
    }

    /// <summary>
    /// ★★ 全域红线：**任何**回包都不能带 <c>nError</c>。
    /// 「成功回包一律省略 nError」在 6 个族里都安全，而族 B 的失败回 <c>nError</c> 是灾难。
    /// </summary>
    [Fact]
    public async Task 全域_任何回包都不带nError()
    {
        using var h = await WithOwnClubAsync("club-no-nerror");

        // 成功路径
        foreach (var cmd in ClubFeature.HandledCmds)
            await h.DispatchLuaCallAsync(cmd, "null");
        // 失败路径（畸形 + 越权 + 越界）
        await h.DispatchLuaCallAsync(ClubFeature.CmdCreateClub, "{\"clubname\":\"x\"}");
        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyNotice, "{\"newnotice\":\"\"}");
        await h.DispatchLuaCallAsync(ClubFeature.CmdModifyApply, "{\"applylevel\":9,\"autojoinlevel\":1}");
        await h.DispatchLuaCallAsync(ClubFeature.CmdFindAClub, "{\"clubid\":1}");

        Assert.False(AnyReplyHasNError(h));
    }

    /// <summary><c>GetStringLen</c> 口径：ASCII 记 1、非 ASCII 记 2（<c>Lib.lua:1268..1290</c>）。</summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("社团", 4)]
    [InlineData("社团abc", 7)]
    public void StringWidth_与客户端GetStringLen同口径(string s, int width)
        => Assert.Equal(width, ClubFeature.StringWidth(s));

    /// <summary>申请等级对的合法性判据（与 <c>UI_Search.lua:324..343</c> 三条一致）。</summary>
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 999, true)]
    [InlineData(0, 5, false)]
    [InlineData(5, 1000, false)]
    [InlineData(50, 10, false)]
    public void IsLegalApplyPair(int applyLv, int autoLv, bool ok)
        => Assert.Equal(ok, ClubFeature.IsLegalApplyPair(applyLv, autoLv));

    /// <summary>确定性混淆必须可复现（跨进程/跨次调用同一输入同一输出）。</summary>
    [Fact]
    public void Mix_确定性()
    {
        for (int salt = 0; salt < 20; salt++)
            Assert.Equal(ClubFeature.Mix(12345, salt), ClubFeature.Mix(12345, salt));
        Assert.NotEqual(ClubFeature.Mix(12345, 1), ClubFeature.Mix(12346, 1));
    }

    // ================================================================
    //  第十五轮：社团管理五条（10006/10007/10009/10010/10011）
    // ================================================================
    //
    // ★★★ 五条都是**族 B**：客户端 `if next(tbParam)` ⇒ **非空表 = 成功**。
    //   失败必须回**空表**；回 {nError=n} 会被判成成功 → 清空 me.clubid → 玩家被踢回主城。
    //   下面每条失败用例都断言「回包是空对象」，这就是那条保护的回归闸。

    /// <summary>失败回包必须是**空对象**（族 B 的命门）。</summary>
    private static void AssertRejected(FeatureHarness h)
    {
        var o = Assert.IsType<JsonObject>(TbParam(h));
        Assert.Empty(o);
    }

    /// <summary>成功回包必须是**完整社团信息**（含 primary/memberlist）。</summary>
    private static JsonObject AssertClubPayload(FeatureHarness h)
    {
        var o = Assert.IsType<JsonObject>(TbParam(h));
        Assert.True(o.ContainsKey("primary"), "成功回包缺 primary ⇒ 客户端会把 MyClubInfo 清空");
        Assert.True(o.ContainsKey("memberlist"));
        return o;
    }

    /// <summary>取社团里第一个「不是自己」的成员 roleid（机器人成员）。</summary>
    private static ulong OtherMember(FeatureHarness h)
    {
        var club = ClubBag.Read(h.Player).Club!;
        return club.Members.First(m => m.Roleid != h.Player.Roleid).Roleid;
    }

    // ---------------- 10009 职位变更 ----------------

    [Fact]
    public async Task 社长可以把成员提为副社长()
    {
        var h = await WithOwnClubAsync("club-pos-up");
        ulong target = OtherMember(h);

        await h.DispatchLuaCallAsync(ClubFeature.CmdChangePosition,
            $"{{\"memberid\":{target},\"srcpos\":0,\"dstpos\":{ClubFeature.PosVice}}}");

        AssertClubPayload(h);
        var club = ClubBag.Read(h.Player).Club!;
        Assert.Equal(ClubFeature.PosVice, club.Member(target)!.Pos);
    }

    /// <summary>★ 目标职位只能是 8/0；把人改成 9（社长）必须被拒（那要走 10010）。</summary>
    [Fact]
    public async Task 职位变更不能把人改成社长()
    {
        var h = await WithOwnClubAsync("club-pos-header");
        ulong target = OtherMember(h);

        await h.DispatchLuaCallAsync(ClubFeature.CmdChangePosition,
            $"{{\"memberid\":{target},\"srcpos\":0,\"dstpos\":{ClubFeature.PosHeader}}}");

        AssertRejected(h);
        Assert.Equal(ClubFeature.PosOrdinary, ClubBag.Read(h.Player).Club!.Member(target)!.Pos);
    }

    /// <summary>★ 普通成员无权改职位。</summary>
    [Fact]
    public async Task 普通成员不能改职位()
    {
        var h = await WithJoinedClubAsync("club-pos-noauth");
        ulong target = OtherMember(h);

        await h.DispatchLuaCallAsync(ClubFeature.CmdChangePosition,
            $"{{\"memberid\":{target},\"srcpos\":0,\"dstpos\":{ClubFeature.PosVice}}}");

        AssertRejected(h);
    }

    // ---------------- 10011 踢人 ----------------

    [Fact]
    public async Task 社长可以踢普通成员()
    {
        var h = await WithOwnClubAsync("club-kick");
        var club0 = ClubBag.Read(h.Player).Club!;
        int before = club0.Members.Count;
        ulong target = OtherMember(h);

        await h.DispatchLuaCallAsync(ClubFeature.CmdKickMember, $"{{\"kickroleid\":{target}}}");

        AssertClubPayload(h);
        var club = ClubBag.Read(h.Player).Club!;
        Assert.Equal(before - 1, club.Members.Count);
        Assert.Null(club.Member(target));
    }

    /// <summary>★ 不能踢自己（要走 10012 退团 / 10022 解散）。</summary>
    [Fact]
    public async Task 不能踢自己()
    {
        var h = await WithOwnClubAsync("club-kick-self");

        await h.DispatchLuaCallAsync(ClubFeature.CmdKickMember,
            $"{{\"kickroleid\":{h.Player.Roleid}}}");

        AssertRejected(h);
        Assert.NotNull(ClubBag.Read(h.Player).Club!.Member(h.Player.Roleid));
    }

    /// <summary>★ 普通成员无权踢人。</summary>
    [Fact]
    public async Task 普通成员不能踢人()
    {
        var h = await WithJoinedClubAsync("club-kick-noauth");
        ulong target = OtherMember(h);

        await h.DispatchLuaCallAsync(ClubFeature.CmdKickMember, $"{{\"kickroleid\":{target}}}");

        AssertRejected(h);
    }

    /// <summary>★ 不能踢职位不低于自己的人（副社长踢不动副社长）。</summary>
    [Fact]
    public async Task 不能踢职位不低于自己的人()
    {
        var h = await WithOwnClubAsync("club-kick-peer");
        ulong target = OtherMember(h);
        // 先把目标提成副社长
        await h.DispatchLuaCallAsync(ClubFeature.CmdChangePosition,
            $"{{\"memberid\":{target},\"srcpos\":0,\"dstpos\":{ClubFeature.PosVice}}}");
        // 再把自己降成副社长（直接改状态，模拟「两个副社长」）
        var bag = ClubBag.Read(h.Player);
        bag.Club!.Member(h.Player.Roleid)!.Pos = ClubFeature.PosVice;
        bag.Write(h.Player);
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdKickMember, $"{{\"kickroleid\":{target}}}");

        AssertRejected(h);
        Assert.NotNull(ClubBag.Read(h.Player).Club!.Member(target));
    }

    // ---------------- 10010 社长转让 ----------------

    [Fact]
    public async Task 社长可以转让给副社长且自己降为副社长()
    {
        var h = await WithOwnClubAsync("club-header");
        ulong target = OtherMember(h);
        await h.DispatchLuaCallAsync(ClubFeature.CmdChangePosition,
            $"{{\"memberid\":{target},\"srcpos\":0,\"dstpos\":{ClubFeature.PosVice}}}");
        h.Outbound.Clear();

        await h.DispatchLuaCallAsync(ClubFeature.CmdHeaderChange, $"{{\"viceid\":{target}}}");

        AssertClubPayload(h);
        var club = ClubBag.Read(h.Player).Club!;
        Assert.Equal(ClubFeature.PosHeader, club.Member(target)!.Pos);
        Assert.Equal(ClubFeature.PosVice, club.Member(h.Player.Roleid)!.Pos);   // ★ 自己降为副社长
    }

    /// <summary>★ 只能转让给**副社长**（客户端候选列表也只放副社长）。</summary>
    [Fact]
    public async Task 不能把社长转让给普通成员()
    {
        var h = await WithOwnClubAsync("club-header-ordinary");
        ulong target = OtherMember(h);

        await h.DispatchLuaCallAsync(ClubFeature.CmdHeaderChange, $"{{\"viceid\":{target}}}");

        AssertRejected(h);
        Assert.Equal(ClubFeature.PosHeader,
            ClubBag.Read(h.Player).Club!.Member(h.Player.Roleid)!.Pos);
    }

    // ---------------- 10006/10007 入团审批 ----------------

    /// <summary>
    /// ★ 本服 applylist 恒为空（单机没有别的玩家申请）⇒ 同意/拒绝都应被拒，
    /// 且**必须回空表**（族 B）——不能假装成功，否则客户端渲染出「已同意」的假状态。
    /// </summary>
    [Theory]
    [InlineData(ClubFeature.CmdAgree, "agreelist")]
    [InlineData(ClubFeature.CmdIgnore, "ignorelist")]
    public async Task 审批不存在的申请回空表(int cmd, string key)
    {
        var h = await WithOwnClubAsync($"club-apply-{cmd}");

        await h.DispatchLuaCallAsync(cmd, $"{{\"{key}\":[123456]}}");

        AssertRejected(h);
    }

    /// <summary>★ 普通成员无权审批。</summary>
    [Theory]
    [InlineData(ClubFeature.CmdAgree, "agreelist")]
    [InlineData(ClubFeature.CmdIgnore, "ignorelist")]
    public async Task 普通成员不能审批申请(int cmd, string key)
    {
        var h = await WithJoinedClubAsync($"club-apply-noauth-{cmd}");

        await h.DispatchLuaCallAsync(cmd, $"{{\"{key}\":[123456]}}");

        AssertRejected(h);
    }

    /// <summary>★ 空列表/裸数组不能抛（<c>JsonNode["k"]</c> 对 JsonArray 会抛）。</summary>
    [Theory]
    [InlineData(ClubFeature.CmdAgree, "[]")]
    [InlineData(ClubFeature.CmdIgnore, "{}")]
    [InlineData(ClubFeature.CmdChangePosition, "[]")]
    [InlineData(ClubFeature.CmdHeaderChange, "[]")]
    [InlineData(ClubFeature.CmdKickMember, "[]")]
    public async Task 非法上行不抛(int cmd, string tbParam)
    {
        var h = await WithOwnClubAsync($"club-bad-{cmd}-{tbParam.Length}");

        var ex = await Record.ExceptionAsync(() => h.DispatchLuaCallAsync(cmd, tbParam));

        Assert.Null(ex);
    }

    /// <summary>★ 没有社团时五条都要回空表（不能抛、不能回非空）。</summary>
    [Theory]
    [InlineData(ClubFeature.CmdAgree)]
    [InlineData(ClubFeature.CmdIgnore)]
    [InlineData(ClubFeature.CmdChangePosition)]
    [InlineData(ClubFeature.CmdHeaderChange)]
    [InlineData(ClubFeature.CmdKickMember)]
    public async Task 没有社团时回空表(int cmd)
    {
        var h = await RichAsync($"club-noclub-{cmd}");

        await h.DispatchLuaCallAsync(cmd, "{}");

        AssertRejected(h);
    }
}
