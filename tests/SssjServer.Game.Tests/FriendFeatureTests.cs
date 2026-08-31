using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 好友域（<see cref="FriendFeature"/>，LuaCall 80..90 + 101）。
/// 取证见 <c>work/friend-domain-forensics.md</c>。
///
/// 断言重点是取证文档里列出的、一漏就崩/静默失败的那几处：
/// <list type="number">
///   <item><b>六个「漏了就抛」的字段</b>：<c>level</c> / <c>off_times</c> / <c>signinfo</c> /
///     <c>headframeid</c> / HMD 的 <c>flag</c> / <c>cardlist[i].skilllist</c>；</item>
///   <item><b>成功回包绝不带 <c>nError</c></b>（LuaCall 数字族，Lua 真值判断，0 也为真）；</item>
///   <item><b>上行是 <c>JsonArray</c> / null 时不能抛</b>（<c>JsonNode["key"]</c> 对数组会抛，
///     AGENTS.md 记录过的真机崩溃）；</item>
///   <item><b>每日收电力上限 10</b>（<c>UI_Main.lua:9</c>）与任务值 <c>(1,20)/(1,21)</c>；</item>
///   <item><b>83 / 85 之后必须补发 <c>sCmd=80</c></b>（取证文档陷阱 11 / 12）。</item>
/// </list>
/// ★ 断言 pf 推送一律用 <see cref="FeatureHarness.WaitForPfAsync"/>（<c>session.Push</c> 是异步写 socket）。
/// </summary>
public class FriendFeatureTests
{
    private static readonly CharacterCardTable Cards = CharacterCardTable.Load(TestPaths.SettingDir);

    /// <summary>某账号会看到的那批机器人（与服务端跑出来的是同一批 —— 确定性由种子保证）。</summary>
    private static List<FriendFeature.FriendBot> BotsOf(string account)
        => FriendFeature.BuildBots(FriendFeature.SeedOf(account), Cards);

    private static Task SendAsync(FeatureHarness h, int cmd, string tbParam)
        => h.DispatchLuaCallAsync(cmd, tbParam);

    /// <summary>本次会话里发出去的、sCmd == <paramref name="cmd"/> 的全部 tbParam（按顺序）。</summary>
    private static List<JsonNode> Replies(FeatureHarness h, int cmd)
    {
        var result = new List<JsonNode>();
        foreach (var (function, json) in h.Outbound)
        {
            if (function != "LuaCall")
                continue;
            var root = JsonNode.Parse(json)!;
            if (root["sCmd"]!.GetValue<int>() == cmd && root["tbParam"] is JsonNode tb)
                result.Add(tb);
        }
        return result;
    }

    private static JsonObject LastReply(FeatureHarness h, int cmd)
    {
        var all = Replies(h, cmd);
        Assert.NotEmpty(all);
        return Assert.IsType<JsonObject>(all[^1]);
    }

    /// <summary>本次会话里发出去的全部 <c>S2C.CallClientTip</c> 的第一个元素（本地化 key）。</summary>
    private static List<string> Tips(FeatureHarness h)
        => h.Outbound.Where(o => o.Function == FriendFeature.FnCallClientTip)
                     .Select(o => JsonNode.Parse(o.Json)![0]!.GetValue<string>())
                     .ToList();

    /// <summary>补发的 cmd 80 里，reqfriendtype 的序列。</summary>
    private static List<int> PushedListTypes(FeatureHarness h)
        => Replies(h, FriendFeature.CmdReqList)
            .Select(tb => tb["reqfriendtype"]!.GetValue<int>())
            .ToList();

    private static JsonArray ListOf(JsonObject payload, int type)
        => Assert.IsType<JsonArray>(payload[FriendFeature.ListKeyOf(type)!]);

    // ================================================================ 80 Friend_ReqList

    /// <summary>
    /// 四个页签（真机实测只发 80 这一个命令，<c>work/probe-three-domains.md</c> §1）+ 绑定活动 type 6。
    /// 回包必须 echo <c>reqfriendtype</c>（<c>Friend.lua:80</c> 是唯一分流依据），
    /// 5 个列表 key 全在，且**请求的那一类**是数组。
    /// </summary>
    [Theory]
    [InlineData(FriendFeature.TypeHY)]          // Friend.lua:81
    [InlineData(FriendFeature.TypeFX)]          // Friend.lua:84
    [InlineData(FriendFeature.TypeSQ)]          // Friend.lua:87
    [InlineData(FriendFeature.TypeHMD)]         // Friend.lua:90
    [InlineData(FriendFeature.TypeBindList)]    // Friend.lua:93
    public async Task ReqList_EchoesType_AndCarriesAllFiveListKeys(int type)
    {
        using var h = await FeatureHarness.CreateAsync($"friend-list-{type}");

        await SendAsync(h, FriendFeature.CmdReqList, $"{{\"reqfriendtype\":{type}}}");

        var payload = LastReply(h, FriendFeature.CmdReqList);
        Assert.Equal(type, payload["reqfriendtype"]!.GetValue<int>());
        Assert.Equal(h.Player.Roleid, payload["roleid"]!.GetValue<ulong>());
        foreach (var key in new[] { "HYList", "FXList", "SQList", "HMDList", "BindList" })
            Assert.IsType<JsonArray>(payload[key]);
        Assert.IsType<JsonArray>(payload[FriendFeature.ListKeyOf(type)!]);

        // LuaCall 数字族：成功回包绝不能带 nError（Lua 里 0 也为真）
        Assert.Null(payload["nError"]);
        // RspType 语义未取证 → 不发
        Assert.Null(payload["RspType"]);
    }

    /// <summary>
    /// 新号首次请求就必须有内容（机器人），否则四个页签全空、增删改 10 条命令在界面上根本触发不了。
    /// 数量按 <see cref="FriendFeature.InitialFriendCount"/>/<see cref="FriendFeature.InitialRequestCount"/>/
    /// <see cref="FriendFeature.FxPageSize"/>；黑名单从 0 起（玩家自己拉黑后才有）；
    /// 绑定活动 type 6 一律空（<c>UI_BindEvent.lua:93/126</c> 缺字段会抛，而活动本身没实现）。
    /// </summary>
    [Fact]
    public async Task ReqList_NewPlayer_HasBotsInThreeTabs_AndEmptyHmdAndBind()
    {
        using var h = await FeatureHarness.CreateAsync("friend-bots");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        Assert.Equal(FriendFeature.InitialFriendCount,
                     ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY).Count);

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":3}""");
        Assert.Equal(FriendFeature.InitialRequestCount,
                     ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeSQ).Count);

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":2}""");
        Assert.Equal(FriendFeature.FxPageSize,
                     ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeFX).Count);

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":4}""");
        Assert.Empty(ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHMD));

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":6}""");
        Assert.Empty(ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeBindList));
    }

    /// <summary>
    /// ★★ 本域最贵的一条：好友列表元素的**字段齐全性**。
    /// 六个硬必填（漏了客户端抛）+ 服务端必须算好的 <c>canrecv</c>/<c>cansend</c>
    /// + 咖啡馆访问界面要的 <c>comfortscore</c>/<c>cafeflag</c>；
    /// 有意不发的 <c>visitingcarddata</c>（陷阱 7）也要守住。
    /// </summary>
    [Fact]
    public async Task ReqList_HyEntry_CarriesEveryMandatoryField()
    {
        using var h = await FeatureHarness.CreateAsync("friend-fields");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var list = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY);
        Assert.NotEmpty(list);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var node in list)
        {
            var e = Assert.IsType<JsonObject>(node);

            // —— 六个「漏了就抛」里的四个（另两个是 HMD 的 flag 与 cardlist 的 skilllist）——
            Assert.True(e["level"]!.GetValue<int>() > 0);                 // "LV." .. nil 抛
            long off = e["off_times"]!.GetValue<long>();                  // 算术，在判 state 之前
            Assert.InRange(off, 1, now);
            Assert.NotNull(e["signinfo"]);                                // TextFilter.Filter(nil) 抛 NRE
            Assert.IsType<string>(e["signinfo"]!.GetValue<string>());
            // headframeid 必须命中 HeadFrame.txt（GetBorderID(nil) 返回 nil，不是 0）
            Assert.Equal(FriendFeature.BotHeadFrameId, e["headframeid"]!.GetValue<int>());

            // ★ 客户端边界 bug：off_times 恰好是 now-3天/now-7天时 strSecsTime 返回 nil（陷阱 23）
            Assert.NotEqual(now - 86400L * 3, off);
            Assert.NotEqual(now - 86400L * 7, off);

            // 头像少女 id 必须有图（1..16），否则显示 Num/what.png 问号，
            // 而 UI_BindEvent.lua:93 那处字符串拼接遇 nil 直接抛
            Assert.InRange(e["girliconid"]!.GetValue<int>(), 1, FriendFeature.BotGirlIconMax);

            // roleid 落在机器人区间，且不是 0（0 会让所有增删改上行发出 nil 目标）
            Assert.InRange(e["roleid"]!.GetValue<ulong>(),
                           FriendFeature.BotRoleIdMin,
                           FriendFeature.BotRoleIdMin + FriendFeature.BotRoleIdSpan);

            Assert.False(string.IsNullOrEmpty(e["rolename"]!.GetValue<string>()));
            Assert.NotEqual(FriendFeature.NotActiveName, e["rolename"]!.GetValue<string>());
            Assert.InRange(e["state"]!.GetValue<int>(), 0, 1);

            // 服务端必须算好的两个布尔（客户端从不计算它们；漏了「一键收取/赠送」永久灰）
            Assert.IsType<bool>(e["canrecv"]!.GetValue<bool>());
            Assert.IsType<bool>(e["cansend"]!.GetValue<bool>());

            Assert.NotNull(e["cardnum"]);
            // 咖啡馆访问界面：cafeflag 缺失/0 会让该好友从「我的好友」列表静默消失
            Assert.True(e["cafeflag"]!.GetValue<int>() > 0);
            Assert.NotNull(e["comfortscore"]);

            // cardlist 每个元素都必须带 skilllist（pairs(nil) 抛）
            var cardlist = Assert.IsType<JsonArray>(e["cardlist"]);
            foreach (var c in cardlist)
            {
                var card = Assert.IsType<JsonObject>(c);
                Assert.IsType<JsonArray>(card["skilllist"]);
                foreach (var k in new[] { "g", "d", "p", "l", "enhancelevel", "breaklevel" })
                    Assert.NotNull(card[k]);
            }

            // ★ 有意不发：id 落在 Board.txt/Skin.txt 之外就会 .bg 抛（陷阱 7）
            Assert.Null(e["visitingcarddata"]);
            // 黑名单以外的类型不发 flag
            Assert.Null(e["flag"]);
        }
    }

    /// <summary>
    /// 新号的好友列表里必须**至少有一个 <c>canrecv=true</c>**，否则「一键收取」按钮
    /// （<c>ResetGetEnergyAllBtn</c>，<c>UI_Friend.lua:523..534</c> 全表扫 canrecv）永久灰，
    /// 81 这条命令在界面上就永远点不出来。
    /// </summary>
    [Fact]
    public async Task ReqList_NewPlayer_HasAtLeastOneCollectableFriend()
    {
        using var h = await FeatureHarness.CreateAsync("friend-canrecv");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var list = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY);

        Assert.Contains(list, n => n!["canrecv"]!.GetValue<bool>());
        Assert.Contains(list, n => n!["cansend"]!.GetValue<bool>());
    }

    /// <summary>
    /// ★ 黑名单条目的 <c>flag</c> 是位域且**必须 bit0 = 1**：
    /// <c>UI_Friend.lua:150</c> <c>if roleInfo.flag &amp; 0x1 &gt; 0</c> 既没有 nil 保护
    /// （缺字段抛），又会把 <c>flag == 0</c> 的条目过滤掉（界面完全看不见，静默丢数据）。
    /// </summary>
    [Fact]
    public async Task ReqList_HmdEntry_CarriesFlagWithBit0Set()
    {
        using var h = await FeatureHarness.CreateAsync("friend-hmdflag");
        ulong victim = BotsOf("friend-hmdflag")[0].Roleid;

        await SendAsync(h, FriendFeature.CmdHmdAdd, $"{{\"hmdlist\":[{victim}],\"log_addtype\":1}}");
        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":4}""");

        var list = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHMD);
        var entry = Assert.IsType<JsonObject>(Assert.Single(list));
        Assert.Equal(victim, entry["roleid"]!.GetValue<ulong>());
        int flag = entry["flag"]!.GetValue<int>();
        Assert.True((flag & FriendFeature.HmdFlagIBlockedHim) > 0,
                    "flag 的 bit0 必须置位，否则 UI_Friend.lua:150 会把这条过滤掉");
        // 六个必填字段在黑名单条目里同样必须齐（四个渲染函数是同一套字段）
        Assert.NotNull(entry["level"]);
        Assert.NotNull(entry["off_times"]);
        Assert.NotNull(entry["signinfo"]);
        Assert.NotNull(entry["headframeid"]);
    }

    // ================================================================ 成功回包不带 nError

    /// <summary>
    /// ★ 全域 12 条命令（80..90 + 101）的成功回包一律**不带 <c>nError</c>**。
    /// 好友域的 <c>else</c> 分支虽然一个都不读它（取证文档 §3.1），但全项目约定如此
    /// （<c>AGENTS.md</c> §5：Lua 真值判断，<c>0</c> 也为真），
    /// 而且将来有人给这些 <c>On*</c> 加标准判错分支时不会一起翻车。
    /// </summary>
    [Theory]
    [InlineData(FriendFeature.CmdReqList, """{"reqfriendtype":1}""")]
    [InlineData(FriendFeature.CmdRecvEnergy, """{"recvenergylist":[],"sendenergylist":[],"log_gettype":2}""")]
    [InlineData(FriendFeature.CmdSendEnergy, """{"sendenergylist":[],"log_sendtype":2}""")]
    [InlineData(FriendFeature.CmdChangeFx, """{"roleid":1}""")]
    [InlineData(FriendFeature.CmdAdd, """{"askroleid":1,"answerroleid":0,"log_addtype":2}""")]
    [InlineData(FriendFeature.CmdPass, """{"answerroleid":1,"asklist":[],"log_yestype":2}""")]
    [InlineData(FriendFeature.CmdIgnore, """{"answerroleid":1,"asklist":[],"log_notype":2}""")]
    [InlineData(FriendFeature.CmdDel, """{"roleid":1,"deleteroleid":0}""")]
    [InlineData(FriendFeature.CmdHmdDel, """{"roleid":1,"hmdlist":[],"log_deltype":2}""")]
    [InlineData(FriendFeature.CmdHmdAdd, """{"roleid":1,"hmdlist":[],"log_addtype":1}""")]
    [InlineData(FriendFeature.CmdRequestPlayerInfo, """{"roleid":424242}""")]
    public async Task Replies_NeverCarryNError(int cmd, string tbParam)
    {
        using var h = await FeatureHarness.CreateAsync($"friend-nerr-{cmd}");

        await SendAsync(h, cmd, tbParam);

        foreach (var (function, json) in h.Outbound)
        {
            if (function != "LuaCall")
                continue;
            var tb = JsonNode.Parse(json)!["tbParam"];
            if (tb is JsonObject obj)
                Assert.False(obj.ContainsKey("nError"),
                    $"cmd {cmd} 的回包带了 nError（LuaCall 数字族成功回包绝不能带）：{json}");
        }
    }

    /// <summary>
    /// ★ 上行可能是 <c>JsonArray</c>（Lua 空表被 <c>Json.Encode</c> 编成 <c>[]</c>）、
    /// <c>null</c>（真机见过整个 tbParam 键都没有的上行，<c>work/probe-three-domains.md</c> §2）
    /// 或缺字段（<c>UI_IDAdd.lua:74</c> 的 <c>tonumber</c> 可能返回 nil）。
    /// <c>JsonNode["key"]</c> 对 JsonArray **会抛** ⇒ 12 条命令逐个过一遍，一条都不能抛。
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"recvenergylist":{},"asklist":"x","hmdlist":3,"roleid":"abc"}""")]
    public async Task MalformedParam_DoesNotThrow(string tbParam)
    {
        using var h = await FeatureHarness.CreateAsync($"friend-mal-{tbParam.Length}");

        foreach (int cmd in FriendFeature.HandledCmds)
            await SendAsync(h, cmd, tbParam);
    }

    // ================================================================ 81 / 82 电力

    /// <summary>
    /// 81 收取：加**货币 id 1（体力）**、推 pf 1032、写任务值 <c>(1,20)/(1,21)</c> 并推 pf 1026。
    /// ★ 货币是 id 1 不是 id 13（<c>MoneyLogic.lua:11</c> <c>eMoneyType_Vigour = 1</c>；
    /// id 13 是咖啡馆吧台那条链的「电力（额外体力）」）。
    /// </summary>
    [Fact]
    public async Task RecvEnergy_AddsVigour_PushesMoneyAndTaskValues()
    {
        using var h = await FeatureHarness.CreateAsync("friend-recv");
        // 新号建号时体力就是满的（World.cs:182），先腾出空间否则会被上限截断
        h.Player.SetMoney(MoneyType.Vigour, 0);

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var hy = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY);
        ulong first = hy[0]!["roleid"]!.GetValue<ulong>();

        await SendAsync(h, FriendFeature.CmdRecvEnergy,
                        $"{{\"roleid\":{h.Player.Roleid},\"recvenergylist\":[{first}]," +
                        "\"sendenergylist\":[],\"log_gettype\":1}");

        Assert.Equal(FriendFeature.EnergyPerRecv, h.Player.GetMoney(MoneyType.Vigour));
        int today = FriendFeature.DayIndex(DateTimeOffset.UtcNow);
        Assert.Equal(today, h.Player.GetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyDayTaskId));
        Assert.Equal(1, h.Player.GetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyCountTaskId));
        Assert.Equal(1, FriendFeature.GainedToday(h.Player, today));

        // 界面刷新完全依赖这两条推送
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf), "必须推 pf 1032 改体力");
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp), "必须推 pf 1026 改 (1,20)/(1,21)");

        Assert.Contains(FriendFeature.TipGetEnergyValue, Tips(h));
    }

    /// <summary>同一个好友同一天只能收一次：第二次是 no-op，且下一次 cmd 80 的 <c>canrecv</c> 变 false。</summary>
    [Fact]
    public async Task RecvEnergy_SameFriendTwiceInOneDay_SecondIsNoop()
    {
        using var h = await FeatureHarness.CreateAsync("friend-recv-twice");
        h.Player.SetMoney(MoneyType.Vigour, 0);
        ulong first = BotsOf("friend-recv-twice")[0].Roleid;
        string body = $"{{\"recvenergylist\":[{first}],\"sendenergylist\":[],\"log_gettype\":1}}";

        await SendAsync(h, FriendFeature.CmdRecvEnergy, body);
        await SendAsync(h, FriendFeature.CmdRecvEnergy, body);

        Assert.Equal(FriendFeature.EnergyPerRecv, h.Player.GetMoney(MoneyType.Vigour));
        Assert.Equal(1, h.Player.GetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyCountTaskId));

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var entry = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY)
            .First(n => n!["roleid"]!.GetValue<ulong>() == first);
        Assert.False(entry!["canrecv"]!.GetValue<bool>());
    }

    /// <summary>
    /// ★ 每日上限 10（<c>UI_Main.lua:9</c> <c>MaxGainEnergyNum = 10</c>）：
    /// 已到上限时不再加体力、发 <c>ui.UI_Friend.GetEnergyLimited</c>，
    /// 且下一次 cmd 80 的 <c>canrecv</c> 必须全 false（否则「一键收取」一直可点、每次都被拒）。
    /// </summary>
    [Fact]
    public async Task RecvEnergy_WhenDailyLimitReached_GrantsNothingAndTips()
    {
        using var h = await FeatureHarness.CreateAsync("friend-recv-limit");
        h.Player.SetMoney(MoneyType.Vigour, 0);
        int today = FriendFeature.DayIndex(DateTimeOffset.UtcNow);
        h.Player.SetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyDayTaskId, today);
        h.Player.SetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyCountTaskId,
                         FriendFeature.MaxGainEnergyNum);

        ulong first = BotsOf("friend-recv-limit")[0].Roleid;
        await SendAsync(h, FriendFeature.CmdRecvEnergy,
                        $"{{\"recvenergylist\":[{first}],\"sendenergylist\":[],\"log_gettype\":2}}");

        Assert.Equal(0, h.Player.GetMoney(MoneyType.Vigour));
        Assert.Equal(FriendFeature.MaxGainEnergyNum,
                     h.Player.GetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyCountTaskId));
        Assert.Contains(FriendFeature.TipGetEnergyLimited, Tips(h));

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var hy = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY);
        Assert.All(hy, n => Assert.False(n!["canrecv"]!.GetValue<bool>()));
    }

    /// <summary>
    /// ★ 陷阱 26：「一键收取」即使两个列表都为空也会发（<c>UI_Friend.lua:575</c> 无
    /// <c>if #… &gt; 0</c> 保护，而 82 有 <c>:600</c>）⇒ 服务端必须容忍，且不能误改任何状态。
    /// </summary>
    [Fact]
    public async Task RecvEnergy_BothListsEmpty_IsTolerated()
    {
        using var h = await FeatureHarness.CreateAsync("friend-recv-empty");
        h.Player.SetMoney(MoneyType.Vigour, 0);

        await SendAsync(h, FriendFeature.CmdRecvEnergy,
                        """{"recvenergylist":[],"sendenergylist":[],"log_gettype":2}""");

        Assert.Equal(0, h.Player.GetMoney(MoneyType.Vigour));
        Assert.Equal(0, h.Player.GetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyCountTaskId));
        Assert.NotNull(LastReply(h, FriendFeature.CmdRecvEnergy));
    }

    /// <summary>81 的自动回赠部分：<c>sendenergylist</c> 里的好友当天 <c>cansend</c> 变 false。</summary>
    [Fact]
    public async Task RecvEnergy_AutoGiftBack_MarksCanSendFalse()
    {
        using var h = await FeatureHarness.CreateAsync("friend-recv-back");
        h.Player.SetMoney(MoneyType.Vigour, 0);
        ulong first = BotsOf("friend-recv-back")[0].Roleid;

        await SendAsync(h, FriendFeature.CmdRecvEnergy,
                        $"{{\"recvenergylist\":[{first}],\"sendenergylist\":[{first}],\"log_gettype\":1}}");

        // 有回赠时用「成功收取%d电力，已自动回赠」这条文案
        Assert.Contains(FriendFeature.TipGetEnergyValueFeedback, Tips(h));

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var entry = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY)
            .First(n => n!["roleid"]!.GetValue<ulong>() == first);
        Assert.False(entry!["cansend"]!.GetValue<bool>());
    }

    /// <summary>
    /// 82 赠送：只改 <c>cansend</c> 记账。
    /// ★ 有意**不动** <c>(1,20)/(1,21)</c>（那是「今日收取次数」，
    /// <c>UI_Main.lua:871..881 GetGainEnergyNum</c>）也**不扣自己的电力**（是否扣未取证）。
    /// </summary>
    [Fact]
    public async Task SendEnergy_MarksCanSendFalse_AndLeavesDailyCounterAlone()
    {
        using var h = await FeatureHarness.CreateAsync("friend-send");
        int vigourBefore = h.Player.GetMoney(MoneyType.Vigour);
        ulong first = BotsOf("friend-send")[0].Roleid;

        await SendAsync(h, FriendFeature.CmdSendEnergy,
                        $"{{\"roleid\":{h.Player.Roleid},\"sendenergylist\":[{first}],\"log_sendtype\":1}}");

        Assert.Equal(vigourBefore, h.Player.GetMoney(MoneyType.Vigour));
        Assert.Equal(0, h.Player.GetTask(FriendFeature.EnergyTaskGroup, FriendFeature.EnergyCountTaskId));
        Assert.Contains(FriendFeature.TipSendEnergy, Tips(h));

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var entry = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY)
            .First(n => n!["roleid"]!.GetValue<ulong>() == first);
        Assert.False(entry!["cansend"]!.GetValue<bool>());
        Assert.True(entry["canrecv"]!.GetValue<bool>(), "赠送不该影响收取");
    }

    // ================================================================ 83 ChangeFX

    /// <summary>
    /// ★★ 陷阱 11：<c>OnFriend_ChangeFX</c>（<c>UI_Friend.lua:972..975</c>）是空实现
    /// ⇒ 只回 83 的话「换一批」永远没反应。必须补发 <c>sCmd=80 / reqfriendtype=2</c>，
    /// 而且新一批**内容要真的变了**（否则轮换在界面上看不出来）。
    /// </summary>
    [Fact]
    public async Task ChangeFx_ResendsList80Type2_WithDifferentBatch()
    {
        using var h = await FeatureHarness.CreateAsync("friend-changefx");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":2}""");
        var before = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeFX)
            .Select(n => n!["roleid"]!.GetValue<ulong>()).ToList();
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdChangeFx, $"{{\"roleid\":{h.Player.Roleid}}}");

        // ① 83 本身要回包（否则 LuaCall:Dispatcher 的 Blocking.Hide 语义与原服不一致）
        Assert.NotNull(LastReply(h, FriendFeature.CmdChangeFx));
        // ② ★ 必须补发一条 cmd 80 / type=2
        Assert.Contains(FriendFeature.TypeFX, PushedListTypes(h));
        var after = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeFX)
            .Select(n => n!["roleid"]!.GetValue<ulong>()).ToList();
        Assert.Equal(FriendFeature.FxPageSize, after.Count);
        // ★ 空闲池（16-4-3=9）≥ 2 倍页长（4）⇒ 前后两批**完全不重叠**，
        //   否则客户端 table.sort 一排，界面上看起来就像「换一批没反应」（陷阱 11 验不出来）
        Assert.Empty(before.Intersect(after));
        Assert.Contains(FriendFeature.TipChangeFx, Tips(h));
    }

    // ================================================================ 84 Add

    /// <summary>
    /// 84：本地服没有对端 ⇒ 目标是机器人时**直接成为好友**（取证文档 §8.2-7），
    /// 回包照原服恒为**空对象**（<c>LuaCall.lua:1370..1371</c> 的 <c>tbRet</c> 是未声明全局 = nil），
    /// 随后补发 80/type=1 与 80/type=2（目标要从「发现」页消失）。
    /// </summary>
    [Fact]
    public async Task Add_MakesBotAFriend_RepliesEmptyObject_AndResendsBothLists()
    {
        using var h = await FeatureHarness.CreateAsync("friend-add");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":2}""");
        ulong target = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeFX)[0]!
            ["roleid"]!.GetValue<ulong>();
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdAdd,
                        $"{{\"askroleid\":{h.Player.Roleid},\"answerroleid\":{target},\"log_addtype\":2}}");

        Assert.Equal("{}", Replies(h, FriendFeature.CmdAdd)[0].ToJsonString());
        Assert.Contains(FriendFeature.TypeHY, PushedListTypes(h));
        Assert.Contains(FriendFeature.TypeFX, PushedListTypes(h));
        Assert.Contains(FriendFeature.TipAddFriendOk, Tips(h));

        var state = FriendFeature.ReadState(h.Player);
        Assert.Contains(target, state.Hy);
        Assert.Equal(FriendFeature.InitialFriendCount + 1, state.Hy.Count);
    }

    /// <summary>
    /// 好友上限必须用**客户端同一张表**（<c>Friend/Friend.txt</c>），
    /// 否则出现「客户端不让点但服务端允许」或反之（陷阱 16）。LV.1 的上限是 10。
    /// </summary>
    [Fact]
    public async Task Add_WhenAtLimit_IsRejectedWithLimitTip()
    {
        using var h = await FeatureHarness.CreateAsync("friend-add-limit");
        var bots = BotsOf("friend-add-limit");

        // 先把好友数顶到 LV.1 的上限 10（Friend.txt:4 rolelevel=1 → FriendNumLimit=10）
        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        var state = FriendFeature.ReadState(h.Player);
        foreach (var bot in bots)
        {
            if (state.Hy.Count >= FriendTable.FallbackLimit)
                break;
            if (!state.Hy.Contains(bot.Roleid))
                state.Hy.Add(bot.Roleid);
        }
        FriendFeature.WriteState(h.Player, state);
        Assert.Equal(FriendTable.FallbackLimit, state.Hy.Count);

        ulong outsider = bots.First(b => !state.Hy.Contains(b.Roleid)).Roleid;
        h.Outbound.Clear();
        await SendAsync(h, FriendFeature.CmdAdd,
                        $"{{\"askroleid\":{h.Player.Roleid},\"answerroleid\":{outsider},\"log_addtype\":1}}");

        Assert.Contains(FriendFeature.TipLimit, Tips(h));
        Assert.Equal(FriendTable.FallbackLimit, FriendFeature.ReadState(h.Player).Hy.Count);
    }

    /// <summary>加自己 / 加不存在的编号一律拒绝，且不能崩。</summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(424242UL)]
    public async Task Add_UnknownTarget_IsRejected(ulong target)
    {
        using var h = await FeatureHarness.CreateAsync($"friend-add-bad-{target}");

        await SendAsync(h, FriendFeature.CmdAdd,
                        $"{{\"askroleid\":{h.Player.Roleid},\"answerroleid\":{target},\"log_addtype\":1}}");

        Assert.Contains(FriendFeature.TipNotExitRole, Tips(h));
        Assert.Equal(FriendFeature.InitialFriendCount, FriendFeature.ReadState(h.Player).Hy.Count);
    }

    // ================================================================ 85 Pass / 86 Ignore

    /// <summary>
    /// ★★ 陷阱 12（本域最严重的静默失败）：一键通过后 <c>UI_Friend.lua:688</c>
    /// <c>self:ShowFriendList(true)</c> 把 <c>curType</c> 写成布尔 ⇒ 四个页签全 SetActive(false)、
    /// 面板变空白。唯一的救法是服务端补发 <c>sCmd=80</c>（type=1 与 type=3 都要）。
    /// </summary>
    [Fact]
    public async Task Pass_MovesSqToHy_AndResendsList80ForType1And3()
    {
        using var h = await FeatureHarness.CreateAsync("friend-pass");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":3}""");
        var sq = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeSQ)
            .Select(n => n!["roleid"]!.GetValue<ulong>()).ToList();
        Assert.NotEmpty(sq);
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdPass,
                        $"{{\"answerroleid\":{h.Player.Roleid}," +
                        $"\"asklist\":[{string.Join(",", sq)}],\"log_yestype\":2}}");

        Assert.Equal("{}", Replies(h, FriendFeature.CmdPass)[0].ToJsonString());
        var types = PushedListTypes(h);
        Assert.Contains(FriendFeature.TypeHY, types);
        Assert.Contains(FriendFeature.TypeSQ, types);
        Assert.Contains(FriendFeature.TipAddFriendOk, Tips(h));

        var state = FriendFeature.ReadState(h.Player);
        Assert.Empty(state.Sq);
        Assert.Equal(FriendFeature.InitialFriendCount + sq.Count, state.Hy.Count);
        foreach (ulong id in sq)
            Assert.Contains(id, state.Hy);
    }

    /// <summary>86 忽略：从申请列表移除（回到「发现」页空闲池）+ 补发 80/type=3。</summary>
    [Fact]
    public async Task Ignore_RemovesFromSq_AndResendsList80Type3()
    {
        using var h = await FeatureHarness.CreateAsync("friend-ignore");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":3}""");
        var sq = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeSQ)
            .Select(n => n!["roleid"]!.GetValue<ulong>()).ToList();
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdIgnore,
                        $"{{\"answerroleid\":{h.Player.Roleid}," +
                        $"\"asklist\":[{sq[0]}],\"log_notype\":1}}");

        Assert.Contains(FriendFeature.TypeSQ, PushedListTypes(h));
        var state = FriendFeature.ReadState(h.Player);
        Assert.DoesNotContain(sq[0], state.Sq);
        Assert.DoesNotContain(sq[0], state.Hy);
    }

    // ================================================================ 87 Del

    /// <summary>
    /// 87 删除好友（本域唯一没有 <c>log_*type</c> 的命令，<c>UI_FriendInfo.lua:455..460</c>）：
    /// 从好友列表移除 + 补发 80/type=1，且该机器人回到「发现」页空闲池。
    /// </summary>
    [Fact]
    public async Task Del_RemovesFromHy_AndResendsList80Type1()
    {
        using var h = await FeatureHarness.CreateAsync("friend-del");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        ulong target = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY)[0]!
            ["roleid"]!.GetValue<ulong>();
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdDel,
                        $"{{\"roleid\":{h.Player.Roleid},\"deleteroleid\":{target}}}");

        Assert.Equal("{}", Replies(h, FriendFeature.CmdDel)[0].ToJsonString());
        Assert.Contains(FriendFeature.TypeHY, PushedListTypes(h));
        var state = FriendFeature.ReadState(h.Player);
        Assert.DoesNotContain(target, state.Hy);
        Assert.Equal(FriendFeature.InitialFriendCount - 1, state.Hy.Count);

        // 回到空闲池 ⇒ 换一批之后可能重新出现在「发现」页
        Assert.Contains(target, FriendFeature.FreePool(BotsOf("friend-del"), state).Select(b => b.Roleid));
    }

    // ================================================================ 88 / 89 黑名单

    /// <summary>
    /// ★★ 陷阱 13：<c>Friend.lua:185..194</c> 只从 **FXList** 移除、**从不往 HMDList 添加**
    /// ⇒ 不补发 80/type=4 的话拉黑的人在黑名单页签里根本看不见。
    /// 拉黑好友时还要同步补 80/type=1（他要从好友列表消失）。
    /// </summary>
    [Fact]
    public async Task HmdAdd_AddsWithFlag1_AndResendsList80Type4AndType1()
    {
        using var h = await FeatureHarness.CreateAsync("friend-hmdadd");

        await SendAsync(h, FriendFeature.CmdReqList, """{"reqfriendtype":1}""");
        ulong target = ListOf(LastReply(h, FriendFeature.CmdReqList), FriendFeature.TypeHY)[0]!
            ["roleid"]!.GetValue<ulong>();
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdHmdAdd,
                        $"{{\"roleid\":{h.Player.Roleid},\"hmdlist\":[{target}],\"log_addtype\":1}}");

        var types = PushedListTypes(h);
        Assert.Contains(FriendFeature.TypeHMD, types);
        Assert.Contains(FriendFeature.TypeHY, types);
        Assert.Contains(FriendFeature.TipAddHmd, Tips(h));

        var state = FriendFeature.ReadState(h.Player);
        var entry = Assert.Single(state.Hmd);
        Assert.Equal(target, entry.Roleid);
        Assert.Equal(FriendFeature.HmdFlagIBlockedHim, entry.Flag);
        Assert.DoesNotContain(target, state.Hy);
        // 被拉黑的人不能再出现在「发现」页
        Assert.DoesNotContain(target,
            FriendFeature.FreePool(BotsOf("friend-hmdadd"), state).Select(b => b.Roleid));
    }

    /// <summary>★ 不能把自己加进黑名单（<c>ui.UI_Friend.RejectAddSelfHMD</c> 客户端零引用 ⇒ 只能由服务端发）。</summary>
    [Fact]
    public async Task HmdAdd_Self_IsRejectedWithTip()
    {
        using var h = await FeatureHarness.CreateAsync("friend-hmdself");

        await SendAsync(h, FriendFeature.CmdHmdAdd,
                        $"{{\"roleid\":{h.Player.Roleid},\"hmdlist\":[{h.Player.Roleid}],\"log_addtype\":1}}");

        Assert.Contains(FriendFeature.TipRejectAddSelfHmd, Tips(h));
        Assert.Empty(FriendFeature.ReadState(h.Player).Hmd);
    }

    /// <summary>
    /// ★ 陷阱 14（客户端 bug）：<c>UI_FriendInfo.lua:521</c> 解除黑名单时写的是
    /// <c>FriendLogic.SQList[k] = nil</c>（应为 HMDList）⇒ 本地不会真的移除，
    /// **必须**靠服务端补发 80/type=4。
    /// </summary>
    [Fact]
    public async Task HmdDel_RemovesAndResendsList80Type4()
    {
        using var h = await FeatureHarness.CreateAsync("friend-hmddel");
        ulong target = BotsOf("friend-hmddel")[0].Roleid;

        await SendAsync(h, FriendFeature.CmdHmdAdd, $"{{\"hmdlist\":[{target}],\"log_addtype\":1}}");
        h.Outbound.Clear();

        await SendAsync(h, FriendFeature.CmdHmdDel,
                        $"{{\"roleid\":{h.Player.Roleid},\"hmdlist\":[{target}],\"log_deltype\":1}}");

        Assert.Equal("{}", Replies(h, FriendFeature.CmdHmdDel)[0].ToJsonString());
        Assert.Contains(FriendFeature.TypeHMD, PushedListTypes(h));
        Assert.Empty(FriendFeature.ReadState(h.Player).Hmd);
    }

    // ================================================================ 90 / 101 角色资料

    /// <summary>
    /// 90 命中机器人：回一份「角色资料」，<c>UI_IDAdd:OnUpdateRoleInfo</c>（<c>UI_IDAdd.lua:45..66</c>）
    /// 读的 4 个字段必须齐（<c>headframeid</c> 在 <c>:62</c> 无 nil 保护）。
    /// </summary>
    [Fact]
    public async Task IdAddInfo_KnownBot_RepliesRoleInfoWithFourReadFields()
    {
        using var h = await FeatureHarness.CreateAsync("friend-idadd");
        var bot = BotsOf("friend-idadd")[0];

        await SendAsync(h, FriendFeature.CmdIdAddInfo, $"{{\"roleid\":{bot.Roleid}}}");

        var info = LastReply(h, FriendFeature.CmdIdAddInfo);
        Assert.Equal(bot.Roleid, info["roleid"]!.GetValue<ulong>());
        Assert.Equal(bot.RoleName, info["rolename"]!.GetValue<string>());        // UI_IDAdd.lua:63
        Assert.Equal(bot.HeadFrameId, info["headframeid"]!.GetValue<int>());     // :50/:62（无保护）
        Assert.Equal(bot.GirlIconId, info["girliconid"]!.GetValue<int>());       // :61
        // UI_FriendInfo 的 Panel 路径还要这几个
        Assert.Equal(bot.Level, info["level"]!.GetValue<int>());
        Assert.NotNull(info["signinfo"]);
        Assert.NotNull(info["cardnum"]);
        Assert.IsType<JsonArray>(info["cardlist"]);
        // ★ 不发 visitingcarddata ⇒ skinId 为 0 ⇒ 走 Panel 路径（Skin 路径还要 weaponnum/pet_num）
        Assert.Null(info["visitingcarddata"]);
        Assert.Null(info["nError"]);
    }

    /// <summary>
    /// ★ 90 查不到人时的原服做法（<c>LuaCall.lua:1445..1447</c>）：
    /// **不回 90**，只发一条 <c>CallClientTip(["ui.UI_Friend.NotExitRole"])</c>。
    /// </summary>
    [Fact]
    public async Task IdAddInfo_UnknownRoleId_SendsTipAndDoesNotReply()
    {
        using var h = await FeatureHarness.CreateAsync("friend-idadd-bad");

        await SendAsync(h, FriendFeature.CmdIdAddInfo, """{"roleid":424242}""");

        Assert.Empty(Replies(h, FriendFeature.CmdIdAddInfo));
        Assert.Contains(FriendFeature.TipNotExitRole, Tips(h));
    }

    /// <summary>
    /// ★ 101 查不到人时**没有**那个 if（<c>LuaCall.lua:1489..1490</c>）⇒ 回
    /// <c>rolename="Not Active"</c> + <c>level=1</c> 哨兵让客户端画「长期未登录」灰头像，
    /// 但 Panel 路径里 <c>headframeid</c>/<c>signinfo</c>/<c>cardlist</c> 在那个 if 之外、照样要给。
    /// </summary>
    [Fact]
    public async Task RequestPlayerInfo_UnknownRoleId_RepliesNotActiveSentinel()
    {
        using var h = await FeatureHarness.CreateAsync("friend-info-bad");

        await SendAsync(h, FriendFeature.CmdRequestPlayerInfo, """{"roleid":424242}""");

        var info = LastReply(h, FriendFeature.CmdRequestPlayerInfo);
        Assert.Equal(FriendFeature.NotActiveName, info["rolename"]!.GetValue<string>());
        Assert.Equal(1, info["level"]!.GetValue<int>());
        Assert.NotNull(info["headframeid"]);
        Assert.NotNull(info["signinfo"]);
        Assert.NotNull(info["off_times"]);
        Assert.IsType<JsonArray>(info["cardlist"]);
    }

    /// <summary>101 也可能查自己（例如从聊天点自己的头像）。</summary>
    [Fact]
    public async Task RequestPlayerInfo_Self_RepliesOwnProfile()
    {
        using var h = await FeatureHarness.CreateAsync("friend-info-self");

        await SendAsync(h, FriendFeature.CmdRequestPlayerInfo, $"{{\"roleid\":{h.Player.Roleid}}}");

        var info = LastReply(h, FriendFeature.CmdRequestPlayerInfo);
        Assert.Equal(h.Player.Roleid, info["roleid"]!.GetValue<ulong>());
        Assert.Equal(h.Player.Level, info["level"]!.GetValue<int>());
        Assert.NotEqual(FriendFeature.NotActiveName, info["rolename"]!.GetValue<string>());
        Assert.NotNull(info["signinfo"]);
    }

    /// <summary>90 与 101 共用同一个服务端函数 ⇒ 命中时字段必须一模一样（取证文档 §2.6）。</summary>
    [Fact]
    public async Task IdAddInfo_And_RequestPlayerInfo_ShareTheSameShape()
    {
        using var h = await FeatureHarness.CreateAsync("friend-info-same");
        ulong id = BotsOf("friend-info-same")[1].Roleid;

        await SendAsync(h, FriendFeature.CmdIdAddInfo, $"{{\"roleid\":{id}}}");
        await SendAsync(h, FriendFeature.CmdRequestPlayerInfo, $"{{\"roleid\":{id}}}");

        Assert.Equal(LastReply(h, FriendFeature.CmdIdAddInfo).ToJsonString(),
                     LastReply(h, FriendFeature.CmdRequestPlayerInfo).ToJsonString());
    }

    // ================================================================ 机器人生成

    /// <summary>
    /// ★ 确定性：同一账号任意多次生成都是同一批人（否则客户端本地缓存/PlayerPrefs 会错乱），
    /// 不同账号是不同批；roleid 落在 6 位区间且池内唯一。
    /// </summary>
    [Fact]
    public void Bots_AreDeterministicPerAccount_AndUnique()
    {
        // 逐字段签名：record 的自动 Equals 对 IReadOnlyList<BotCard> 走引用比较，
        // 两次独立构造的实例不会相等 —— 所以比签名而不是比对象。
        static string Sig(FriendFeature.FriendBot b)
            => $"{b.Roleid}/{b.RoleName}/{b.Level}/{b.GirlIconId}/{b.HeadFrameId}/{b.SignInfo}/" +
               $"{b.State}/{b.OffSeconds}/{b.CardNum}/{b.ComfortScore}/" +
               string.Join(";", b.Cards.Select(c =>
                   $"{c.G}-{c.D}-{c.P}-{c.L}+{c.EnhanceLevel}+{c.BreakLevel}"));

        var a1 = BotsOf("acc-A");
        var a2 = BotsOf("acc-A");
        var b1 = BotsOf("acc-B");

        Assert.Equal(FriendFeature.BotPoolSize, a1.Count);
        Assert.Equal(a1.Select(Sig), a2.Select(Sig));
        Assert.NotEqual(a1.Select(x => x.Roleid), b1.Select(x => x.Roleid));

        Assert.Equal(a1.Count, a1.Select(x => x.Roleid).Distinct().Count());
        foreach (var bot in a1)
        {
            Assert.InRange(bot.Roleid, FriendFeature.BotRoleIdMin,
                           FriendFeature.BotRoleIdMin + FriendFeature.BotRoleIdSpan - 1);
            // 昵称不能撞上「该 id 无此玩家」的哨兵
            Assert.NotEqual(FriendFeature.NotActiveName, bot.RoleName);
        }
        // 昵称池与机器人池等长 ⇒ 同一账号内不重名
        Assert.Equal(a1.Count, a1.Select(x => x.RoleName).Distinct().Count());
        // 空闲池必须 ≥ 2 倍「发现」页长，83 换一批才能给出完全新的一批（见 BotPoolSize 注释）
        Assert.True(FriendFeature.BotPoolSize - FriendFeature.InitialFriendCount
                    - FriendFeature.InitialRequestCount >= 2 * FriendFeature.FxPageSize);
    }

    /// <summary>
    /// 机器人字段的合法性（不合法客户端会崩或显示问号）：
    /// <c>girliconid</c> ∈ 1..16（<c>Npc/icon_n_girlNN.png</c> 都有图）、
    /// <c>headframeid</c> 命中 <c>HeadFrame.txt</c>、<c>signinfo</c> 非 null、
    /// <c>off_seconds</c> 避开 <c>strSecsTime</c> 的两个雷（3 天 / 7 天整）、
    /// <c>cardlist</c> 的四元组必须在 <c>CharacterCard.txt</c> 里真实存在。
    /// </summary>
    [Fact]
    public void Bots_HaveOnlyLegalConfigIds()
    {
        var headFrames = SettingTable.Load(Path.Combine(TestPaths.SettingDir, "HeadFrame.txt"))!;
        var legalFrames = headFrames.Rows.Select(r => headFrames.Int(r, "ID", -1)).ToHashSet();

        foreach (var bot in BotsOf("acc-legal"))
        {
            Assert.InRange(bot.GirlIconId, 1, FriendFeature.BotGirlIconMax);
            Assert.Contains(bot.HeadFrameId, legalFrames);
            Assert.NotNull(bot.SignInfo);
            Assert.InRange(bot.Level, 1, 999);
            Assert.NotEqual(86400 * 3, bot.OffSeconds);
            Assert.NotEqual(86400 * 7, bot.OffSeconds);
            Assert.True(bot.OffSeconds > 0);
            Assert.Equal(FriendFeature.BotCardCount, bot.Cards.Count);
            foreach (var c in bot.Cards)
            {
                Assert.NotNull(Cards.Get(c.G, c.D, c.P, c.L));   // 客户端 CreateItem 查不到就抛
                Assert.True(c.EnhanceLevel > 0);
            }
        }
    }

    /// <summary>缺角色卡表时降级成 <c>cardlist: []</c>（空数组合法，<c>UI_FriendInfo.lua:191</c> 有 nil 保护）。</summary>
    [Fact]
    public void Bots_WithoutCardTable_HaveEmptyCardList()
    {
        var bots = FriendFeature.BuildBots(FriendFeature.SeedOf("acc-nocard"), CharacterCardTable.Empty);
        Assert.All(bots, b => Assert.Empty(b.Cards));
        // 依然可以组出合法的 FriendInfo（cardlist 是空数组、不是缺字段）
        var info = FriendFeature.BuildFriendInfo(bots[0], 1_700_000_000, true, true, null);
        Assert.Empty(Assert.IsType<JsonArray>(info["cardlist"]));
    }

    // ================================================================ 配置表

    /// <summary>
    /// ★ 好友上限必须与客户端 <c>FriendLogic:GetFriendNumLimit</c>（<c>Friend.lua:225..236</c>）
    /// 完全同口径，含三条推论：LV151..299 回落到 150 档（99）、LV300 是 100、
    /// **LV&gt;1000 退化成 10**（<c>Friend.lua:226</c> 直接 return）。
    /// </summary>
    [Fact]
    public void FriendTable_MatchesClientLadder()
    {
        var table = FriendTable.Load(TestPaths.SettingDir);
        Assert.Equal(151, table.Count);                     // Friend.txt:4..154

        Assert.Equal(10, table.NumLimit(1));                // Friend.txt:4
        Assert.Equal(15, table.NumLimit(10));               // :13
        Assert.Equal(27, table.NumLimit(30));               // :33
        Assert.Equal(39, table.NumLimit(50));               // :53
        Assert.Equal(69, table.NumLimit(100));              // :103
        Assert.Equal(99, table.NumLimit(150));              // :153
        Assert.Equal(99, table.NumLimit(151));              // ★ 151..299 没有独立配置行 → 回落 150
        Assert.Equal(99, table.NumLimit(299));
        Assert.Equal(100, table.NumLimit(300));             // :154（GM 账号就是 LV.300）
        Assert.Equal(100, table.NumLimit(1000));            // 300..1000 都吃 300 那一档
        Assert.Equal(FriendTable.FallbackLimit, table.NumLimit(1001));   // ★ >1000 退化成 10
        Assert.Equal(FriendTable.FallbackLimit, table.NumLimit(0));      // tbFriendCfg[0] 不存在
        Assert.Equal(FriendTable.FallbackLimit, table.NumLimit(-1));

        // 缺表降级成固定 10，不抛
        Assert.Equal(FriendTable.FallbackLimit, FriendTable.Empty.NumLimit(300));
    }

    // ================================================================ 存档

    /// <summary>
    /// 状态存 <c>Player.Extra["friend"]</c>，随 <c>PlayerStore</c> round-trip
    /// （种子必须一起落盘，否则重启后机器人换了一批人）。
    /// </summary>
    [Fact]
    public void State_RoundTripsThroughPlayerStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sssj-friend-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlayerStore(new ServerOptions { DataDirectory = dir, Sid = 3 });
            var player = new Player("friend_acc", 42);

            var state = FriendFeature.ReadState(player);
            Assert.True(FriendFeature.EnsureSeeded(player, state, Cards));
            Assert.False(FriendFeature.EnsureSeeded(player, state, Cards));   // 幂等
            state.Hmd.Add(new FriendFeature.HmdEntry(123456, FriendFeature.HmdFlagIBlockedHim));
            state.MarkOf(123456).TakeDay = 777;
            state.MarkOf(123456).SendDay = 778;
            state.FxCursor = 5;
            FriendFeature.WriteState(player, state);
            store.Save(player);

            var loaded = store.Load("friend_acc");
            Assert.NotNull(loaded);
            var back = FriendFeature.ReadState(loaded!);
            Assert.Equal(FriendFeature.SeedOf("friend_acc"), back.Seed);
            Assert.Equal(state.Hy, back.Hy);
            Assert.Equal(state.Sq, back.Sq);
            Assert.Equal(5, back.FxCursor);
            var entry = Assert.Single(back.Hmd);
            Assert.Equal(123456UL, entry.Roleid);
            Assert.Equal(FriendFeature.HmdFlagIBlockedHim, entry.Flag);
            Assert.Equal(777, back.MarkOf(123456).TakeDay);
            Assert.Equal(778, back.MarkOf(123456).SendDay);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary><c>Extra["friend"]</c> 被手改成别的形状（数组/垃圾）时退回空状态，不抛。</summary>
    [Fact]
    public void ReadState_BrokenExtra_FallsBackToEmpty()
    {
        var player = new Player("acc", 1);
        player.Extra[FriendFeature.ExtraKey] = new JsonArray();
        var s1 = FriendFeature.ReadState(player);
        Assert.Empty(s1.Hy);
        Assert.Equal(0u, s1.Seed);

        player.Extra[FriendFeature.ExtraKey] = new JsonObject
        {
            [FriendFeature.NodeHy] = "oops",
            [FriendFeature.NodeHmd] = new JsonArray { 1, "x" },
            [FriendFeature.NodeEnergy] = new JsonArray(),
        };
        var s2 = FriendFeature.ReadState(player);
        Assert.Empty(s2.Hy);
        Assert.Empty(s2.Hmd);
        Assert.Empty(s2.Energy);
    }

    // ================================================================ 参数解析 / 装配

    /// <summary>
    /// ★ 读参数必须对 <c>JsonArray</c> / null / 类型错乱全部免疫
    /// （<c>JsonNode["key"]</c> 对 JsonArray 会抛，AGENTS.md 记录的真机崩溃）。
    /// </summary>
    [Fact]
    public void ParamReaders_TolerateArrayNullAndGarbage()
    {
        var array = JsonNode.Parse("[]");
        Assert.Equal(0, FriendFeature.ReadInt(array, "reqfriendtype", 0));
        Assert.Equal(0UL, FriendFeature.ReadUlong(array, "roleid"));
        Assert.Empty(FriendFeature.ReadIdList(array, "asklist"));

        Assert.Equal(7, FriendFeature.ReadInt(null, "x", 7));
        Assert.Equal(0UL, FriendFeature.ReadUlong(null, "roleid"));
        Assert.Empty(FriendFeature.ReadIdList(null, "asklist"));

        // 真实上行
        Assert.Equal(3, FriendFeature.ReadInt(JsonNode.Parse("""{"reqfriendtype":3}"""), "reqfriendtype", 0));
        Assert.Equal(123456UL, FriendFeature.ReadUlong(JsonNode.Parse("""{"roleid":123456}"""), "roleid"));
        Assert.Equal(new ulong[] { 1, 2, 3 },
            FriendFeature.ReadIdList(JsonNode.Parse("""{"asklist":[1,2,3]}"""), "asklist"));

        // Lua 侧偶尔给字符串/浮点（number 是 double）
        Assert.Equal(6, FriendFeature.ReadInt(JsonNode.Parse("""{"reqfriendtype":"6"}"""), "reqfriendtype", 0));
        Assert.Equal(2, FriendFeature.ReadInt(JsonNode.Parse("""{"reqfriendtype":2.0}"""), "reqfriendtype", 0));
        Assert.Equal(100001UL, FriendFeature.ReadUlong(JsonNode.Parse("""{"roleid":"100001"}"""), "roleid"));
        Assert.Equal(100001UL, FriendFeature.ReadUlong(JsonNode.Parse("""{"roleid":100001.0}"""), "roleid"));

        // 数组里混进垃圾/0 一律丢掉（0 目标会让上行发出 nil）
        Assert.Equal(new ulong[] { 5 },
            FriendFeature.ReadIdList(JsonNode.Parse("""{"asklist":[0,"x",5,null]}"""), "asklist"));
        // 键存在但不是数组
        Assert.Empty(FriendFeature.ReadIdList(JsonNode.Parse("""{"asklist":{"a":1}}"""), "asklist"));
    }

    /// <summary>
    /// 反射装配（<c>GameFeature.cs:181..199</c>）+ ★ **cmd 80 只能有一个 owner**：
    /// <c>LuaCallRouter.Map</c> 是 <c>_map[cmd] = handler</c>（<c>LuaCallRouter.cs:78..81</c>），
    /// 两个 feature 都注册 80 会让后装配的静默覆盖前者（装配顺序来自类型名排序 ⇒ 难查）。
    /// </summary>
    [Fact]
    public void Feature_IsDiscovered_AndIsTheSoleOwnerOfCmd80()
    {
        var features = GameFeatureLoader.Discover();
        var mine = features.OfType<FriendFeature>().SingleOrDefault();
        Assert.NotNull(mine);
        Assert.False(string.IsNullOrWhiteSpace(mine!.Name));

        Assert.Equal(new[] { 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 101 },
                     FriendFeature.HandledCmds);
        Assert.DoesNotContain(FriendFeature.CmdReqList, MainCityFeature.HandledCmds);
    }

    /// <summary>类型 → 列表 key 的映射（<c>Friend.lua:81/84/87/90/93</c>）；★ 没有 5。</summary>
    [Fact]
    public void ListKeyOf_MatchesFriendLua()
    {
        Assert.Equal("HYList", FriendFeature.ListKeyOf(1));
        Assert.Equal("FXList", FriendFeature.ListKeyOf(2));
        Assert.Equal("SQList", FriendFeature.ListKeyOf(3));
        Assert.Equal("HMDList", FriendFeature.ListKeyOf(4));
        Assert.Null(FriendFeature.ListKeyOf(5));            // Friend.lua 里没有 5
        Assert.Equal("BindList", FriendFeature.ListKeyOf(6));
        Assert.Null(FriendFeature.ListKeyOf(0));

        Assert.Equal("好友", FriendFeature.TypeName(1));
        Assert.Equal("黑名单", FriendFeature.TypeName(4));
        Assert.Equal("未知", FriendFeature.TypeName(99));
    }

    /// <summary>
    /// 日切口径必须与客户端 <c>GetLocalDay4AM</c>（<c>Lib.lua:1701..1707</c>）一致 ——
    /// 直接复用 <see cref="CafeFeature.CafeDayIndex"/>，不要再造一个 <c>/86400</c>。
    /// </summary>
    [Fact]
    public void DayIndex_SharesCafeFeatureImplementation()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(CafeFeature.CafeDayIndex(now), FriendFeature.DayIndex(now));
        // 4 点偏移：UTC 03:59 与 04:01 落在不同的「日」
        var before = new DateTimeOffset(2024, 5, 1, 3, 59, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2024, 5, 1, 4, 1, 0, TimeSpan.Zero);
        Assert.Equal(FriendFeature.DayIndex(before) + 1, FriendFeature.DayIndex(after));
    }
}
