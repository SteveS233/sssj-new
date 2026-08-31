using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 咖啡馆进入数据（LuaCall 112 Cafe_ReqCafeData / 241 CafeFurNum）。
///
/// 断言的依据全部来自客户端 <c>else</c> 分支（即「客户端收到回包后怎么用」）：
/// <c>NCafeNet.RspDownloadCafeData</c>（extracted/lua/NCafeNet.lua:52..83）与
/// <c>LuaCall.lua:4485..4491</c>。任何一个字段缺失都会让客户端在 nil 上解引用
/// （见 <see cref="SssjServer.Game.Handlers.CafeFeature.BuildCafeDataPayload"/> 的逐字段取证）。
/// </summary>
public class CafeFeatureTests
{
    // ---- LuaCall 112 回包字段清单 ----

    /// <summary>
    /// 回包必须**恰好**覆盖 NCafeNet.RspDownloadCafeData 读到的全部 key，一个不多一个不少。
    /// 多余字段（如 CafePb 的 nextidx/touchtime/fixedtime）按 AGENTS.md 不编造。
    /// </summary>
    [Fact]
    public void BuildCafeDataPayload_KeySet_MatchesRspDownloadCafeData()
    {
        var payload = CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(1, 0, 0), 1_700_000_000);

        string[] expected =
        [
            "basetime",       // NCafeNet.lua:57  NCafe.Data.ReqCustomerTime
            "level",          // NCafeNet.lua:59  NCafe.Data.CafeLevel
            "hot",            // NCafeNet.lua:61  NCafe.Data.Hot
            "comfort",        // NCafeNet.lua:65  NCafe.Data:SetCafeComfort
            "seatlist",       // NCafeNet.lua:67  updateSeatData（:101）
            "customerqueue",  // NCafeNet.lua:68  updateQueueData（:116）
            "coffeelist",     // NCafeNet.lua:69  updateCoffeeData（:127）
            "roomgirlslist",  // NCafeNet.lua:70  updateGirlData（:143）
            "weightlist",     // NCafeNet.lua:71  updateWeightData（:156）
            "visitedList",    // NCafeNet.lua:72  updateVisitedData（:172，驼峰，来自 HouseLogic:OnReqVisitedList）
            "boxstatelist",   // NCafeNet.lua:73  NCafePetLogic:InitFoodBoxs
            "petstatelist",   // NCafeNet.lua:74  NCafePetLogic:InitPetsInCafe
            "nextpetid",      // NCafeNet.lua:75  NCafePetLogic:UpdatePetNum
        ];

        Assert.Equal(expected.OrderBy(s => s, StringComparer.Ordinal),
                     payload.Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>
    /// 8 个清单字段必须是**空 JSON 数组**（不是 null、不是对象）：
    /// Lua 侧一律走 <c>ipairs</c>/<c>pairs</c>/<c>#</c>，nil 虽然也被各 update* 提前返回挡住，
    /// 但空数组能让 <c>NCafe.Data.Seats:Clear()</c> 之类的清理路径与原服务端一致。
    /// </summary>
    [Fact]
    public void BuildCafeDataPayload_ListFields_AreEmptyArrays()
    {
        var payload = CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(3, 12, 34), 1);

        foreach (string key in new[]
                 {
                     "seatlist", "customerqueue", "coffeelist", "roomgirlslist",
                     "weightlist", "visitedList", "boxstatelist", "petstatelist",
                 })
        {
            var node = payload[key];
            var arr = Assert.IsType<JsonArray>(node);
            Assert.Empty(arr);
        }
    }

    /// <summary>
    /// 5 个标量必须是数字：basetime → ulong 属性、level → int 属性、comfort → int 方法参数，
    /// nil 会让 xLua 的 setter/方法绑定直接报错（dump.cs:619489 / 619485 / 532026）。
    /// </summary>
    [Fact]
    public void BuildCafeDataPayload_Scalars_AreNumbers()
    {
        const long baseTime = 1_762_000_000;
        var payload = CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(5, 2900, 1500), baseTime);

        Assert.Equal(baseTime, payload["basetime"]!.GetValue<long>());
        Assert.Equal(5, payload["level"]!.GetValue<int>());
        Assert.Equal(2900, payload["hot"]!.GetValue<int>());
        Assert.Equal(1500, payload["comfort"]!.GetValue<int>());
        Assert.Equal(CafeFeature.FirstPetUid, payload["nextpetid"]!.GetValue<int>());
    }

    /// <summary>
    /// <c>nextpetid</c> = 1（无宠物）。客户端 <c>petMaxNum = (maxNum or 2) - 1</c>
    /// （NCafePetLogic.lua:872）→ 0，<c>GetPetAreaNum()</c> 仍返回 1，不会越界。
    /// </summary>
    [Fact]
    public void FirstPetUid_IsOne_SoPetMaxNumBecomesZero()
    {
        Assert.Equal(1, CafeFeature.FirstPetUid);
        Assert.Equal(0, CafeFeature.FirstPetUid - 1);
    }

    // ---- LuaCall 241 回包字段 ----

    /// <summary>
    /// 241 回包 = <c>{nRet, nNum}</c>，与原服务端分支逐字一致（LuaCall.lua:4478..4480）。
    /// <c>nRet</c> 必须存在（else 分支唯一闸门 <c>if tbParam.nRet then</c>；Lua 里 0 为真）。
    /// </summary>
    [Fact]
    public void BuildFurNumPayload_HasNRetAndNNum()
    {
        var payload = CafeFeature.BuildFurNumPayload(0);

        Assert.Equal(new[] { "nRet", "nNum" }, payload.Select(kv => kv.Key));
        Assert.Equal(0, payload["nRet"]!.GetValue<int>());
        Assert.Equal(0, payload["nNum"]!.GetValue<int>());

        Assert.Equal(17, CafeFeature.BuildFurNumPayload(17)["nNum"]!.GetValue<int>());
    }

    /// <summary>家具数量当前恒为 0，与 GameModule 对 pf 1049 回的空 HouseCache.furnitureList 一致。</summary>
    [Fact]
    public void FurnitureCount_IsZero_MatchingEmptyHouseCache()
    {
        Assert.Equal(0, CafeFeature.FurnitureCount(new Player("acc", 1)));
    }

    // ---- 服务端状态（Player.Extra["cafe"] + 任务值 (1,32)）----

    /// <summary>新号（Extra 与任务值都空）→ 等级兜底为 1（NCafeLevels.txt 最小 Level=1）。</summary>
    [Fact]
    public void ReadState_FreshPlayer_LevelFloorsToMinLevel()
    {
        var st = CafeFeature.ReadState(new Player("acc", 1));

        Assert.Equal(CafeFeature.MinLevel, st.Level);
        Assert.Equal(1, st.Level);
        Assert.Equal(0, st.Hot);
        Assert.Equal(0, st.Comfort);
    }

    /// <summary>
    /// Extra 里没有等级时，回落到任务值 (1,32) —— 这是客户端 <c>CafeLogic:GetCafeLevel()</c>
    /// 唯一认的坐标（CafeLogic.lua:20 / :449）。
    /// </summary>
    [Fact]
    public void ReadState_FallsBackToTaskValue1_32()
    {
        var player = new Player("acc", 1);
        player.SetTask(CafeFeature.LevelTaskGroup, CafeFeature.LevelTaskId, 7);

        Assert.Equal(7, CafeFeature.ReadState(player).Level);
    }

    /// <summary>Extra 里有数据时以 Extra 为准（它才是服务端权威存档）。</summary>
    [Fact]
    public void ReadState_PrefersExtraOverTaskValue()
    {
        var player = new Player("acc", 1);
        player.SetTask(CafeFeature.LevelTaskGroup, CafeFeature.LevelTaskId, 2);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 9,
            ["hot"] = 123,
            ["comfort"] = 456,
        };

        var st = CafeFeature.ReadState(player);
        Assert.Equal(9, st.Level);
        Assert.Equal(123, st.Hot);
        Assert.Equal(456, st.Comfort);
    }

    /// <summary>负数被夹到 0（人气/舒适度都是非负量；comfort 会直接进 SetCafeComfort(int)）。</summary>
    [Fact]
    public void ReadState_ClampsNegativeHotAndComfort()
    {
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 0,
            ["hot"] = -5,
            ["comfort"] = -1,
        };

        var st = CafeFeature.ReadState(player);
        Assert.Equal(CafeFeature.MinLevel, st.Level);
        Assert.Equal(0, st.Hot);
        Assert.Equal(0, st.Comfort);
    }

    /// <summary>
    /// WriteState 必须把等级镜像进任务值 (1,32)，且第二次写同一状态时返回 false
    /// （避免每次进主城都触发一次存档落盘）。
    /// </summary>
    [Fact]
    public void WriteState_MirrorsLevelIntoTaskValue_AndIsIdempotent()
    {
        var player = new Player("acc", 1);
        var st = new CafeFeature.CafeState(4, 600, 1200);

        Assert.True(CafeFeature.WriteState(player, st));
        Assert.Equal(4, player.GetTask(CafeFeature.LevelTaskGroup, CafeFeature.LevelTaskId));

        var stored = Assert.IsType<JsonObject>(player.Extra[CafeFeature.ExtraKey]);
        Assert.Equal(4, stored["level"]!.GetValue<int>());
        Assert.Equal(600, stored["hot"]!.GetValue<int>());
        Assert.Equal(1200, stored["comfort"]!.GetValue<int>());

        Assert.False(CafeFeature.WriteState(player, st));   // 无变化 → 不用落盘
        Assert.True(CafeFeature.WriteState(player, new CafeFeature.CafeState(5, 600, 1200)));
    }

    /// <summary>Extra["cafe"] 随存档 round-trip（PlayerStore 的 Extra 袋，Player.cs:74）。</summary>
    [Fact]
    public void CafeState_RoundTripsThroughPlayerStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sssj-cafe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlayerStore(new ServerOptions { DataDirectory = dir });
            var player = new Player("cafe_acc", 4242);
            CafeFeature.WriteState(player, new CafeFeature.CafeState(6, 2900, 999));
            store.Save(player);

            var loaded = store.Load("cafe_acc");
            Assert.NotNull(loaded);
            var st = CafeFeature.ReadState(loaded!);
            Assert.Equal(6, st.Level);
            Assert.Equal(2900, st.Hot);
            Assert.Equal(999, st.Comfort);
            Assert.Equal(6, loaded!.GetTask(CafeFeature.LevelTaskGroup, CafeFeature.LevelTaskId));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- 端到端：GameModule 反射装配 → LuaCall 分发 → pf 1024 的 sJsonParam ----

    /// <summary>
    /// 真实链路：<c>GameModule.Install</c> 反射装配 <see cref="CafeFeature"/> →
    /// 上行 <c>{"sCmd":112,"tbParam":[]}</c>（实测形态，空 Lua 表编码成 <c>[]</c>）→
    /// 回包 pf 1024 <c>sFunction="LuaCall"</c>、<c>sJsonParam={"sCmd":112,"tbParam":{...}}</c>。
    /// </summary>
    [Fact]
    public async Task LuaCall112_Dispatch_RepliesWithCafeData()
    {
        using var h = await LuaCallHarness.CreateAsync("cafe112");

        await h.DispatchLuaCallAsync("""{"sCmd":112,"tbParam":[]}""");

        var (function, json) = Assert.Single(h.Outbound);
        Assert.Equal("LuaCall", function);

        var root = JsonNode.Parse(json)!;
        Assert.Equal(112, root["sCmd"]!.GetValue<int>());
        var tb = Assert.IsType<JsonObject>(root["tbParam"]);

        Assert.True(tb["basetime"]!.GetValue<long>() > 1_600_000_000, "basetime 应是服务器 unix 秒");
        Assert.Equal(1, tb["level"]!.GetValue<int>());
        Assert.Equal(0, tb["hot"]!.GetValue<int>());
        Assert.Equal(0, tb["comfort"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(tb["seatlist"]));
        Assert.Empty(Assert.IsType<JsonArray>(tb["visitedList"]));
        Assert.Equal(1, tb["nextpetid"]!.GetValue<int>());

        // 顺带确认等级被镜像进任务值，下次登录 pf 1026 全量下发时客户端读到同一个值
        Assert.Equal(1, h.Player.GetTask(CafeFeature.LevelTaskGroup, CafeFeature.LevelTaskId));
    }

    /// <summary>
    /// 上行 241 **不带 tbParam**（<c>LuaCall:CallServer(LuaCall.CafeFurNum)</c>，CoffeeCommon.lua:725），
    /// 实测就是 <c>{"sCmd":241}</c>；handler 必须容忍 param 为 null。
    /// </summary>
    [Fact]
    public async Task LuaCall241_DispatchWithoutTbParam_RepliesWithFurNum()
    {
        using var h = await LuaCallHarness.CreateAsync("cafe241");

        await h.DispatchLuaCallAsync("""{"sCmd":241}""");

        var (function, json) = Assert.Single(h.Outbound);
        Assert.Equal("LuaCall", function);

        var root = JsonNode.Parse(json)!;
        Assert.Equal(241, root["sCmd"]!.GetValue<int>());
        var tb = Assert.IsType<JsonObject>(root["tbParam"]);
        Assert.Equal(0, tb["nRet"]!.GetValue<int>());
        Assert.Equal(0, tb["nNum"]!.GetValue<int>());
    }

    /// <summary>
    /// 锁死上线的 wire 形态（字段顺序 + 类型），出现意外改动时立刻失败。
    /// 这就是 pf 1024 <c>NtfS2CCall.sJsonParam</c> 的完整原文。
    /// </summary>
    [Fact]
    public void CafeDataPayload_ExactWireJson()
    {
        var payload = CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(1, 0, 0), 1_700_000_000);

        Assert.Equal(
            """{"basetime":1700000000,"level":1,"hot":0,"comfort":0,"seatlist":[],"customerqueue":[],"coffeelist":[],"roomgirlslist":[],"weightlist":[],"visitedList":[],"boxstatelist":[],"petstatelist":[],"nextpetid":1}""",
            payload.ToJsonString());

        Assert.Equal("""{"nRet":0,"nNum":0}""", CafeFeature.BuildFurNumPayload(0).ToJsonString());
    }

    /// <summary>未登录（Tag 非 Player）时只记日志，不回包也不抛。</summary>
    [Fact]
    public async Task LuaCall112_WithoutLogin_DoesNotReplyAndDoesNotThrow()
    {
        using var h = await LuaCallHarness.CreateAsync("cafe_nologin", attachPlayer: false);

        await h.DispatchLuaCallAsync("""{"sCmd":112,"tbParam":[]}""");
        await h.DispatchLuaCallAsync("""{"sCmd":241}""");

        Assert.Empty(h.Outbound);
    }

    // =====================================================================
    //  以下是本轮新增：六组清单字段（座位/顾客/咖啡/少女/权重/访客）
    //  每条断言都对应 extracted/lua 里一个**没有 nil 保护**的消费点。
    // =====================================================================

    private static CafeTable LiveTable => CafeTable.Load(TestPaths.SettingDir);

    // ---- 配置表：现网数字（一旦 extracted/setting 换版本，这里会第一个失败）----

    /// <summary>
    /// 五张经营表的现网数字。全部可用
    /// <c>awk -F'\t' 'NR&gt;3' extracted/setting/&lt;表&gt;.txt</c> 复核。
    /// </summary>
    [Fact]
    public void CafeTable_LoadsBusinessTables_WithLiveNumbers()
    {
        var t = LiveTable;

        // NCafeGuests.txt：12 行，Id = 201..212，星级 4/4/4 分布
        Assert.Equal(12, t.Guests.Count);
        Assert.Equal(201, t.Guests.Keys.Min());
        Assert.Equal(212, t.Guests.Keys.Max());
        Assert.Equal(4, t.Guests.Values.Count(g => g.Star == 1));
        Assert.Equal(4, t.Guests.Values.Count(g => g.Star == 2));
        Assert.Equal(4, t.Guests.Values.Count(g => g.Star == 3));
        // ★ Weight 列全 0，真权重在 WeightMin/WeightMax（1 星 1500~2000）
        var g201 = t.Guests[201];
        Assert.Equal(1500, g201.WeightMin);
        Assert.Equal(2000, g201.WeightMax);
        Assert.Equal(1, g201.NeedLv);              // 12 行 NeedLv 全是 1 → 1 级就全解锁
        Assert.All(t.Guests.Values, g => Assert.Equal(1, g.NeedLv));

        // CoffeeList.txt：12 行，Id = 1..12，Time 全 240 秒
        Assert.Equal(12, t.Coffees.Count);
        Assert.Equal(Enumerable.Range(1, 12), t.Coffees.Keys.OrderBy(k => k));
        Assert.All(t.Coffees.Values, c => Assert.Equal(240, c.Time));

        // NCafeLevels.txt：31 行 = 普通 1..30 + 伪等级 10001（CafeLogic.lua:44 MaxRewardLvId）
        Assert.Equal(31, t.CafeLevels.Count);
        Assert.True(t.CafeLevels.ContainsKey(10001));
        // ★ 可下发上限 30 = min(最大普通等级 30, NCafeItemTypes 餐位 Limit 列长度 30)
        Assert.Equal(30, t.MaxCafeLevel);

        // 1 级槽位：SlotEat 6 / SlotCoffee 1 / MaxQSize 1（MaxQSize 31 行全是 1）
        var lv1 = t.LevelRowFor(1)!;
        Assert.Equal(6, lv1.SlotEat);
        Assert.Equal(1, lv1.SlotCoffee);
        Assert.Equal(1, lv1.MaxQSize);
        Assert.All(t.CafeLevels.Values, r => Assert.Equal(1, r.MaxQSize));
        // 今日顾客配额：4 = 2 + 1 + 1
        Assert.Equal(4, lv1.MaxGuestTypes);
        Assert.Equal(2, lv1.Type1Num);
        Assert.Equal(1, lv1.Type2Num);
        Assert.Equal(1, lv1.OtherTypeNum);
        // ★ 31 行里 30 行「三档之和 == MaxGuestTypes」，唯一的例外是 **Level 7**：
        //   MaxGuestTypes=4 而 2+2+1=5（原厂配置表自己的笔误）。
        //   服务端实现以 MaxGuestTypes 为硬上限，见 RollDailyGuests_Level7_MaxGuestTypesWinsOverQuotaSum。
        var inconsistent = t.CafeLevels.Values
            .Where(r => r.MaxGuestTypes != r.Type1Num + r.Type2Num + r.OtherTypeNum)
            .Select(r => r.Level)
            .OrderBy(l => l)
            .ToList();
        Assert.Equal(new[] { 7 }, inconsistent);

        // ComfortEff.txt：10 行，最大 High = 40200（超过它 GetComfortConfig() 返回 nil）
        Assert.Equal(40200, t.MaxComfort);
    }

    /// <summary>缺表时降级：等级上限退到 1、舒适度上限退到 0，且不抛。</summary>
    [Fact]
    public void CafeTable_MissingBusinessTables_DegradesToSafeBounds()
    {
        var t = CafeTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-cafe-" + Guid.NewGuid()));

        Assert.Empty(t.Guests);
        Assert.Empty(t.Coffees);
        Assert.Empty(t.CafeLevels);
        Assert.Equal(1, t.MaxCafeLevel);    // 只允许 1 级 —— 保守但绝不会踩 CafeEditor.lua:112 的 error()
        Assert.Equal(0, t.MaxComfort);      // 只允许 comfort=0（ComfortEff 第一行就是 0..0）
        Assert.Null(t.LevelRowFor(1));
    }

    // ---- seatlist：★ 「省略键」与「填 0」语义相反 ----

    /// <summary>
    /// ★★ 空座位必须**整个省略** <c>customeridx</c>：
    /// <c>NCafeNet.lua:107</c> 是 <c>if v.customeridx then</c>（Lua 真值判断，0 也是真），
    /// 填 0 会走进「有顾客」分支，在座位上摆一个 <c>customertype = 0</c> 的顾客，
    /// 而 0 不在 <c>NCafeGuests.txt</c>（201..212）里 ⇒
    /// <c>UI_CafeGuestRect.lua:64</c> <c>GetCustomerCfg(guest_id).PreferDec</c> 在 nil 上抛。
    /// 形状取证同源：客户端自己上传时就是 <c>{position = o.Guid}</c> 再按需补键（<c>NCafeNet.lua:191..197</c>）。
    /// </summary>
    [Fact]
    public void BuildSeatList_EmptySeat_OmitsCustomerKeysEntirely()
    {
        var arr = CafeFeature.BuildSeatList([new CafeFeature.CafeSeat(1230, null, 0, 0, 0)]);

        var o = Assert.IsType<JsonObject>(Assert.Single(arr));
        Assert.Equal(new[] { "position" }, o.Select(kv => kv.Key));
        Assert.Equal(1230, o["position"]!.GetValue<int>());
        Assert.False(o.ContainsKey("customeridx"));     // ★ 不是 0，是没有这个键
        Assert.Equal("""{"position":1230}""", o.ToJsonString());
    }

    /// <summary>
    /// 有顾客的座位：五个键齐全，顺序与客户端上传时一致（NCafeNet.lua:191..197），
    /// 因为 <c>UpdateSeatGuest(position, customeridx, customertype, coffeetype, starttime)</c>
    /// 是一句五实参调用（<c>NCafeNet.lua:108</c>），缺一个 xLua 就绑不上。
    /// </summary>
    [Fact]
    public void BuildSeatList_OccupiedSeat_HasAllFiveKeysInUploadOrder()
    {
        var arr = CafeFeature.BuildSeatList([new CafeFeature.CafeSeat(1230, 7, 201, 3, 1_700_000_000)]);

        var o = Assert.IsType<JsonObject>(Assert.Single(arr));
        Assert.Equal(new[] { "position", "customeridx", "customertype", "coffeetype", "starttime" },
                     o.Select(kv => kv.Key));
        Assert.Equal(
            """{"position":1230,"customeridx":7,"customertype":201,"coffeetype":3,"starttime":1700000000}""",
            o.ToJsonString());
    }

    // ---- customerqueue：三键必须齐全（与座位相反，没有「空元素」这种状态）----

    /// <summary>
    /// <c>AddGuest2Queue(v.customertype, v.customeridx, v.starttime)</c>（NCafeNet.lua:122）
    /// 是一句三实参调用 ⇒ 三键必须齐全，且顺序照抄它便于对包。
    /// </summary>
    [Fact]
    public void BuildQueueList_HasAllThreeKeys()
    {
        var arr = CafeFeature.BuildQueueList([new CafeFeature.CafeQueueGuest(205, 9, 1_700_000_123)]);

        var o = Assert.IsType<JsonObject>(Assert.Single(arr));
        Assert.Equal(new[] { "customertype", "customeridx", "starttime" }, o.Select(kv => kv.Key));
        Assert.Equal("""{"customertype":205,"customeridx":9,"starttime":1700000123}""", o.ToJsonString());
    }

    // ---- coffeelist：★ 整条进店链上唯一会「因为值不对」直接抛的字段 ----

    /// <summary>
    /// <c>AddCoffeeItems(v.coffeetype, v.count, template.Time)</c>（NCafeNet.lua:136）
    /// ⇒ 两键必填；<c>template</c> 来自 <c>GetCoffeeListConfig(v.coffeetype)</c> 且**无 nil 保护**。
    /// </summary>
    [Fact]
    public void BuildCoffeeList_HasCoffeeTypeAndCount()
    {
        var arr = CafeFeature.BuildCoffeeList([new CafeFeature.CafeCoffee(2, 120)]);

        var o = Assert.IsType<JsonObject>(Assert.Single(arr));
        Assert.Equal(new[] { "coffeetype", "count" }, o.Select(kv => kv.Key));
        Assert.Equal("""{"coffeetype":2,"count":120}""", o.ToJsonString());
    }

    /// <summary>
    /// ★★ 存档里出现表外的 <c>coffeetype</c> 必须在服务端就丢掉。
    /// 这是本轮最要命的一条：客户端 <c>NCafeNet.lua:136</c> 会在 <c>template.Time</c> 上抛，
    /// 而那一句在 <c>RspDownloadCafeData</c>（<c>:69</c>）里，抛了就走不到
    /// <c>:81</c> 的 <c>Map.LoadById(6)</c> —— 症状是「点咖啡馆完全没反应、连加载条都不出现」。
    /// </summary>
    [Fact]
    public void ReadContent_DropsCoffeeTypeMissingFromCoffeeList()
    {
        var table = LiveTable;
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 1,
            ["coffeelist"] = new JsonArray
            {
                new JsonObject { ["coffeetype"] = 99, ["count"] = 5 },   // 表外 → 丢
            },
        };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), table, 1);

        Assert.Empty(content.Coffees);
        Assert.Empty(Assert.IsType<JsonArray>(
            CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(1, 0, 0), 1, null, content)["coffeelist"]));
    }

    /// <summary>合法的 coffeetype 会被保留；数量 &lt;= 0 的空槽不发（客户端 <c>:134</c> 也会跳过 <c>{}</c>）。</summary>
    [Fact]
    public void ReadContent_KeepsValidCoffee_DropsZeroCount()
    {
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 1,
            ["coffeelist"] = new JsonArray
            {
                new JsonObject { ["coffeetype"] = 1, ["count"] = 0 },    // 空槽 → 不发
                new JsonObject { ["coffeetype"] = 2, ["count"] = 30 },
            },
        };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);

        var only = Assert.Single(content.Coffees);
        Assert.Equal(2, only.CoffeeType);
        Assert.Equal(30, only.Count);
    }

    /// <summary>
    /// 咖啡种类数被钳到 <c>min(SlotCoffee, UI 4 格)</c>：1 级 <c>SlotCoffee = 1</c>。
    /// 超出后客户端 <c>UI_CoffeeMake.lua:157</c> 会在 <c>leftCoffeeGroup[idx+1].SaleObj</c> 上抛。
    /// </summary>
    [Fact]
    public void ReadContent_CapsCoffeeToSlotCoffee()
    {
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 1,
            ["coffeelist"] = new JsonArray
            {
                new JsonObject { ["coffeetype"] = 1, ["count"] = 10 },
                new JsonObject { ["coffeetype"] = 2, ["count"] = 10 },
                new JsonObject { ["coffeetype"] = 3, ["count"] = 10 },
            },
        };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);

        Assert.Single(content.Coffees);            // SlotCoffee(L1) = 1
        Assert.Equal(1, LiveTable.LevelRowFor(1)!.SlotCoffee);
        Assert.Equal(4, CafeFeature.CoffeeUiSlotCount);
    }

    // ---- roomgirlslist：★ 外层定位数组，空数组占位 ≠ 省略 ----

    /// <summary>三个区域全空 → <c>[]</c>（与 <c>[[],[],[]]</c> 等价：NCafeNet.lua:147 三次取到 nil）。</summary>
    [Fact]
    public void BuildRoomGirlsList_AllAreasEmpty_IsEmptyArray()
    {
        Assert.Empty(CafeFeature.BuildRoomGirlsList([]));
        Assert.Empty(CafeFeature.BuildRoomGirlsList(
            [new List<CafeFeature.CafeAreaGirl>(), new List<CafeFeature.CafeAreaGirl>(),
             new List<CafeFeature.CafeAreaGirl>()]));
    }

    /// <summary>
    /// ★★ 只有休息区（下标 2）有人时，**前面的经营区必须用空数组占位** ——
    /// <c>NCafeNet.lua:146..149</c> 是 <c>for index = 1, #AreaType</c> 再 <c>roomGirlsList[index]</c>，
    /// 外层是定位数组；省略空区域会把休息区的少女摆到经营区（<c>AreaType[1] = Cafe</c>）。
    /// </summary>
    [Fact]
    public void BuildRoomGirlsList_KeepsEmptyAreaSlotsSoIndexesDoNotShift()
    {
        var arr = CafeFeature.BuildRoomGirlsList(
        [
            new List<CafeFeature.CafeAreaGirl>(),                              // 经营区：空
            new List<CafeFeature.CafeAreaGirl> { new(12, 12001) },             // 休息区：1 人
            new List<CafeFeature.CafeAreaGirl>(),                              // 宿舍：空
        ]);

        Assert.Equal(CafeFeature.AreaCount, arr.Count);
        Assert.Empty(Assert.IsType<JsonArray>(arr[0]));
        Assert.Single(Assert.IsType<JsonArray>(arr[1]));
        Assert.Empty(Assert.IsType<JsonArray>(arr[2]));
        Assert.Equal("[[],[[12,12001]],[]]", arr.ToJsonString());
    }

    /// <summary>
    /// ★ 内层是**二元数组**而不是带键对象：<c>NCafeNet.lua:149</c> 用 <c>girl[1]</c>/<c>girl[2]</c>，
    /// 与客户端上传时构造的 <c>{ id, girlInfo.ModelId }</c>（<c>:362</c>）同形。
    /// 写成 <c>{"girlid":..,"modelid":..}</c> 会让 <c>girl[1]</c> 变 nil。
    /// </summary>
    [Fact]
    public void BuildRoomGirlsList_InnerElementIsPositionalPair()
    {
        var arr = CafeFeature.BuildRoomGirlsList(
            [new List<CafeFeature.CafeAreaGirl> { new(1, 1001), new(2, 2002) }]);

        var cafeArea = Assert.IsType<JsonArray>(arr[0]);
        var pair = Assert.IsType<JsonArray>(cafeArea[0]);
        Assert.Equal(2, pair.Count);
        Assert.Equal(1, pair[0]!.GetValue<int>());       // girl[1] = girlId
        Assert.Equal(1001, pair[1]!.GetValue<int>());    // girl[2] = ModelId
        Assert.Equal("[[[1,1001],[2,2002]],[],[]]", arr.ToJsonString());
    }

    /// <summary>
    /// 排班少女的 <c>ModelId</c> 一律取服务端存档里的值（不采信客户端上传值）；
    /// 玩家没有的少女丢弃。
    /// ★ <c>ModelId == 0</c> 是**合法值**，不能丢：C# <c>InitGirls</c>（<c>0x1D5C00C</c>）
    /// 在 <c>0x1D5C204</c> 判 <c>IsInSelfCafe()</c>，为 true 时把 modelId **强制置 0**
    /// （<c>0x1D5C20C</c>），而 <c>CreateGirl</c>（<c>0x1D5E504</c>）拼 prefab 用的是 <c>girlId</c>。
    /// </summary>
    [Fact]
    public void ReadContent_AreaGirls_UsesServerModelId_AndDropsOnlyUnowned()
    {
        var player = new Player("acc", 1);
        player.Girls.Add(new Girl { Girlid = 1, Modelid = 1001 });
        player.Girls.Add(new Girl { Girlid = 2, Modelid = 0 });     // ModelId 0 → 保留
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 8,     // CafeUnlockNum(L8) = 2，容得下两位
            ["roomgirlslist"] = new JsonArray
            {
                new JsonArray
                {
                    new JsonArray { 1, 999999 },   // 上传的 ModelId 不采信，用存档的 1001
                    new JsonArray { 2, 2002 },     // 存档 ModelId 是 0 → 下发 0
                    new JsonArray { 77, 7007 },    // 玩家没有这张卡 → 丢
                },
                new JsonArray(),
                new JsonArray(),
            },
        };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(8, 0, 0), LiveTable, 1);

        var cafe = content.AreaGirls[CafeFeature.AreaIndexCafe];
        Assert.Equal(2, cafe.Count);
        Assert.Equal(1, cafe[0].GirlId);
        Assert.Equal(1001, cafe[0].ModelId);   // ★ 服务端的 ModelId 赢
        Assert.Equal(2, cafe[1].GirlId);
        Assert.Equal(0, cafe[1].ModelId);      // ★ 0 合法，照发
        Assert.Empty(content.AreaGirls[CafeFeature.AreaIndexRest]);
        Assert.Empty(content.AreaGirls[CafeFeature.AreaIndexDorm]);
    }

    /// <summary>经营区人数被钳到 <c>CafeUnlockNum</c>（1 级 = 1）。</summary>
    [Fact]
    public void ReadContent_CapsCafeAreaGirlsToCafeUnlockNum()
    {
        var player = new Player("acc", 1);
        for (uint i = 1; i <= 3; i++)
            player.Girls.Add(new Girl { Girlid = i, Modelid = 1000 + i });
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 1,
            ["roomgirlslist"] = new JsonArray
            {
                new JsonArray { new JsonArray { 1, 1001 }, new JsonArray { 2, 1002 }, new JsonArray { 3, 1003 } },
            },
        };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);

        Assert.Equal(1, LiveTable.LevelRowFor(1)!.CafeUnlockNum);
        Assert.Single(content.AreaGirls[CafeFeature.AreaIndexCafe]);
    }

    // ---- weightlist：★ 大写键 + Id 必须在表内 + 总和不能为 0 ----

    /// <summary>
    /// ★ 键名是大写的 <c>Id</c> / <c>Weight</c>（全包唯一一处），
    /// 因为客户端 <c>NCafeNet.lua:163/167</c> 读的就是 <c>v.Weight</c> / <c>v.Id</c>，
    /// 而它们正是 <c>NCafeGuests.txt</c> 的列名。小写会让 <c>total + nil</c> 直接抛。
    /// </summary>
    [Fact]
    public void BuildWeightList_UsesCapitalIdAndWeightKeys()
    {
        var arr = CafeFeature.BuildWeightList([new CafeFeature.CafeGuestWeight(201, 1700)]);

        var o = Assert.IsType<JsonObject>(Assert.Single(arr));
        Assert.Equal(new[] { "Id", "Weight" }, o.Select(kv => kv.Key));
        Assert.False(o.ContainsKey("id"));
        Assert.False(o.ContainsKey("weight"));
        Assert.Equal("""{"Id":201,"Weight":1700}""", o.ToJsonString());
    }

    /// <summary>
    /// 1 级的今日顾客 = 4 类，且严格按 <c>Type1Num=2 / Type2Num=1 / OtherTypeNum=1</c> 分档，
    /// Id 必须都在 <c>NCafeGuests.txt</c> 里、权重落在各自的 <c>[WeightMin, WeightMax]</c> 内。
    /// </summary>
    [Fact]
    public void RollDailyGuests_Level1_PicksFourByStarQuota()
    {
        var table = LiveTable;

        var rolled = CafeFeature.RollDailyGuests(table, 1, roleid: 4242, dayIndex: 19_723);

        Assert.Equal(4, rolled.Count);
        Assert.Equal(2, rolled.Count(w => table.Guests[w.Id].Star == 1));
        Assert.Equal(1, rolled.Count(w => table.Guests[w.Id].Star == 2));
        Assert.Equal(1, rolled.Count(w => table.Guests[w.Id].Star == 3));
        Assert.All(rolled, w =>
        {
            var cfg = table.Guests[w.Id];        // ★ 不在表里的话这里就 KeyNotFound
            Assert.InRange(w.Weight, cfg.WeightMin, cfg.WeightMax);
        });
        // ★ 总和必须 > 0：NCafeNet.lua:167 的 v.Weight / total
        Assert.True(rolled.Sum(w => w.Weight) > 0);
        // Id 唯一、升序（便于与真机抓包逐字比对）
        Assert.Equal(rolled.Select(w => w.Id).Distinct().OrderBy(i => i), rolled.Select(w => w.Id));
    }

    /// <summary>
    /// 同一天同一角色必须抽出**完全一致**的清单（同一天内反复进店，界面上的出现概率不能跳）；
    /// 换一天必须变。种子只用 roleid + 日序号，不能用 <c>HashCode.Combine</c>（每进程随机）。
    /// </summary>
    [Fact]
    public void RollDailyGuests_StableWithinDay_AndChangesAcrossDays()
    {
        var table = LiveTable;

        var a1 = CafeFeature.RollDailyGuests(table, 1, 4242, 19_723);
        var a2 = CafeFeature.RollDailyGuests(table, 1, 4242, 19_723);
        Assert.Equal(a1.Select(w => (w.Id, w.Weight)), a2.Select(w => (w.Id, w.Weight)));

        // 连续 40 天里至少要出现一次不同（否则说明日序号没进种子）
        bool anyDifferent = Enumerable.Range(1, 40)
            .Select(d => CafeFeature.RollDailyGuests(table, 1, 4242, 19_723 + d))
            .Any(r => !r.Select(w => (w.Id, w.Weight)).SequenceEqual(a1.Select(w => (w.Id, w.Weight))));
        Assert.True(anyDifferent, "换天之后今日顾客应当变化");

        // 不同角色也应当不同（否则种子没吃 roleid）
        bool roleDifferent = Enumerable.Range(1, 40)
            .Select(r => CafeFeature.RollDailyGuests(table, 1, (ulong)r, 19_723))
            .Any(r => !r.Select(w => (w.Id, w.Weight)).SequenceEqual(a1.Select(w => (w.Id, w.Weight))));
        Assert.True(roleDifferent, "换角色之后今日顾客应当不同");
    }

    /// <summary>顾客表缺失时只能发空清单（安全：<c>NCafeNet.lua:159</c> 提前返回）。</summary>
    [Fact]
    public void RollDailyGuests_WithoutGuestTable_ReturnsEmpty()
    {
        Assert.Empty(CafeFeature.RollDailyGuests(CafeTable.Empty, 1, 1, 19_723));
    }

    /// <summary>
    /// ★ <c>NCafeLevels.txt</c> 的 <b>Level 7</b> 是唯一一行「三档配额之和(5) &gt; MaxGuestTypes(4)」
    /// 的自相矛盾数据。实现以 <c>MaxGuestTypes</c> 为硬上限：抽出来必须是 4 类，
    /// 而不是 5 类（多一类会让「今日顾客」面板多出一行，与原服不符）。
    /// </summary>
    [Fact]
    public void RollDailyGuests_Level7_MaxGuestTypesWinsOverQuotaSum()
    {
        var table = LiveTable;
        var lv7 = table.LevelRowFor(7)!;

        Assert.Equal(4, lv7.MaxGuestTypes);
        Assert.Equal(5, lv7.Type1Num + lv7.Type2Num + lv7.OtherTypeNum);
        Assert.Equal(4, CafeFeature.RollDailyGuests(table, 7, 4242, 19_723).Count);
    }

    /// <summary>进店时会带上今日顾客并落盘；同一天再进店读的是存档里那份（不重抽）。</summary>
    [Fact]
    public void ReadContent_RollsWeightsOnce_ThenReusesThemWithinSameDay()
    {
        var table = LiveTable;
        var player = new Player("acc", 4242);
        var state = new CafeFeature.CafeState(1, 0, 0);

        var first = CafeFeature.ReadContent(player, state, table, 19_723);
        Assert.Equal(4, first.Weights.Count);
        Assert.Equal(19_723, first.WeightDay);
        Assert.True(CafeFeature.WriteState(player, state, first));

        var second = CafeFeature.ReadContent(player, state, table, 19_723);
        Assert.Equal(first.Weights.Select(w => (w.Id, w.Weight)), second.Weights.Select(w => (w.Id, w.Weight)));

        // 跨天 → 重抽，并且 WeightDay 跟着走
        var next = CafeFeature.ReadContent(player, state, table, 19_724);
        Assert.Equal(19_724, next.WeightDay);
        Assert.Equal(4, next.Weights.Count);
    }

    // ---- visitedList：★ 驼峰键 + 六字段 + name 必须是字符串 ----

    /// <summary>
    /// <c>UpdateVisitedInfo(v.roleid, v.girlid, v.modelid, v.name, v.time, v.comfortscore)</c>
    /// （NCafeNet.lua:178）是一句六实参调用 ⇒ 六键必填；
    /// <c>name</c> 是 string 形参，绝不能下发 null。
    /// </summary>
    [Fact]
    public void BuildVisitedList_HasSixKeys_AndNameIsAlwaysString()
    {
        var arr = CafeFeature.BuildVisitedList(
            [new CafeFeature.CafeVisitor(10086, 1, 1001, "阿七", 1_700_000_000, 1200)]);

        var o = Assert.IsType<JsonObject>(Assert.Single(arr));
        Assert.Equal(new[] { "roleid", "girlid", "modelid", "name", "time", "comfortscore" },
                     o.Select(kv => kv.Key));
        Assert.Equal("阿七", o["name"]!.GetValue<string>());

        // 存档里 name 缺失时也要变成空串，不能是 null
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 1,
            ["visitedList"] = new JsonArray { new JsonObject { ["roleid"] = 10086 } },
        };
        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);
        Assert.Equal("", Assert.Single(content.Visitors).Name);
        var wire = Assert.IsType<JsonArray>(CafeFeature.BuildVisitedList(content.Visitors));
        Assert.Equal(System.Text.Json.JsonValueKind.String,
                     Assert.IsType<JsonObject>(wire[0])["name"]!.GetValueKind());
    }

    /// <summary>
    /// ★ key 是驼峰 <c>visitedList</c>（不来自 CafePb，是 <c>LuaCall.lua:1601</c>
    /// <c>tbRet.visitedList = HouseLogic:OnReqVisitedList(...)</c> 另挂的）。
    /// 写成全小写就等于「没这个字段」——静默丢访客且不报错。
    /// </summary>
    [Fact]
    public void CafeDataPayload_VisitedListKeyIsCamelCase()
    {
        var payload = CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(1, 0, 0), 1);

        Assert.True(payload.ContainsKey("visitedList"));
        Assert.False(payload.ContainsKey("visitedlist"));
        // 其余清单键全小写（与 CafePb 字段名一致）
        foreach (string k in new[] { "seatlist", "customerqueue", "coffeelist", "roomgirlslist", "weightlist" })
            Assert.Equal(k.ToLowerInvariant(), k);
    }

    // ---- 六组字段一起进回包 ----

    /// <summary>
    /// 有内容时六个清单全部出现在回包里，且 key 集合仍然**恰好**是那 13 个
    /// （不能因为加内容就多出键 —— 客户端只读这 13 个）。
    /// </summary>
    [Fact]
    public void BuildCafeDataPayload_WithContent_FillsSixListsAndKeepsKeySet()
    {
        var content = new CafeFeature.CafeContent
        {
            Seats = [new CafeFeature.CafeSeat(1230, null, 0, 0, 0), new CafeFeature.CafeSeat(1231, 5, 201, 1, 99)],
            Queue = [new CafeFeature.CafeQueueGuest(205, 6, 100)],
            Coffees = [new CafeFeature.CafeCoffee(1, 20)],
            AreaGirls = [new List<CafeFeature.CafeAreaGirl> { new(1, 1001) },
                         new List<CafeFeature.CafeAreaGirl>(),
                         new List<CafeFeature.CafeAreaGirl>()],
            Weights = [new CafeFeature.CafeGuestWeight(201, 1700)],
            Visitors = [new CafeFeature.CafeVisitor(10086, 1, 1001, "阿七", 1, 0)],
            WeightDay = 19_723,
        };

        var payload = CafeFeature.BuildCafeDataPayload(new CafeFeature.CafeState(3, 240, 1200), 1_700_000_000,
                                                      pet: null, content: content);

        Assert.Equal(13, payload.Count);
        Assert.Equal(2, Assert.IsType<JsonArray>(payload["seatlist"]).Count);
        Assert.Single(Assert.IsType<JsonArray>(payload["customerqueue"]));
        Assert.Single(Assert.IsType<JsonArray>(payload["coffeelist"]));
        Assert.Equal(CafeFeature.AreaCount, Assert.IsType<JsonArray>(payload["roomgirlslist"]).Count);
        Assert.Single(Assert.IsType<JsonArray>(payload["weightlist"]));
        Assert.Single(Assert.IsType<JsonArray>(payload["visitedList"]));
        // WeightDay 只存服务端，不下发
        Assert.False(payload.ContainsKey("weightday"));
    }

    // ---- level / comfort 钳位：两条「不钳就会让客户端抛」的硬约束 ----

    /// <summary>
    /// ★ 等级必须钳到 <see cref="CafeTable.MaxCafeLevel"/>（现网 30）。
    /// 超了客户端 <c>CafeEditor.lua:103..116</c> 的 <c>GetItemTypeInfoLimit</c> 会
    /// <c>error()</c>（写在 <c>return</c> 之前），而那一步在 <c>NCafe.lua:19</c>
    /// <c>FixSeatOverLimit()</c> 里、位于 <c>NCafeMgr.Ins:OnEnter</c> 之前 ——
    /// 抛了整个 <c>NCafe:OnEnter</c> 断掉，场景一个对象都不会初始化。
    /// </summary>
    [Fact]
    public void ReadState_ClampsLevelToMaxCafeLevel()
    {
        var table = LiveTable;
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject { ["level"] = 10001 };   // 伪等级也要被挡住

        Assert.Equal(30, table.MaxCafeLevel);
        Assert.Equal(30, CafeFeature.ReadState(player, table).Level);

        // 不传表时保持既有行为（不按配置钳位）——历史用例依赖这一点
        Assert.Equal(10001, CafeFeature.ReadState(player).Level);
    }

    /// <summary>
    /// ★ 舒适度必须钳到 <see cref="CafeTable.MaxComfort"/>（现网 40200）。
    /// 越界**不会抛**（<c>GetComfortConfig()</c> 的字典键是 <c>ComfortEff.Id</c> = 0..9，
    /// 匹配不上时返回的 <c>ComfortConfig[0]</c> 正是「Id=0 / 0..0 / 全 0 加成」那一行，
    /// <c>GirlVigourManager.lua:208..223</c>），而是**静默退化成零加成** ——
    /// <c>UI_CoffeeList.lua:490 comfortConf.AddEarn_Coin</c> 会显示 0% 而不是应有的 90%，
    /// <c>GirlVigourManager.lua:231 comfortConf.AddVigor</c> 也变 0。属于「数据看着对、加成全没了」。
    /// </summary>
    [Fact]
    public void ReadState_ClampsComfortToMaxComfort()
    {
        var table = LiveTable;
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject { ["level"] = 1, ["comfort"] = 999_999 };

        Assert.Equal(40200, table.MaxComfort);
        Assert.Equal(40200, CafeFeature.ReadState(player, table).Comfort);
        Assert.Equal(999_999, CafeFeature.ReadState(player).Comfort);   // 不传表 → 不钳
    }

    /// <summary>缺配置表时等级退到 1、舒适度退到 0（保守但绝不会让客户端抛）。</summary>
    [Fact]
    public void ReadState_WithoutConfig_FallsBackToSafestValues()
    {
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject { ["level"] = 12, ["comfort"] = 3000 };

        var st = CafeFeature.ReadState(player, CafeTable.Empty);
        Assert.Equal(1, st.Level);
        Assert.Equal(0, st.Comfort);
    }

    // ---- 座位 / 排队 的校验与钳位 ----

    /// <summary>
    /// 座位上的顾客 <c>customertype</c> 不在 <c>NCafeGuests.txt</c> 里时，
    /// **保留座位、清掉顾客**，且清掉的方式必须是「省略 customeridx」而不是填 0。
    /// </summary>
    [Fact]
    public void ReadContent_UnknownCustomerType_KeepsSeatButDropsGuest()
    {
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject
        {
            ["level"] = 1,
            ["seatlist"] = new JsonArray
            {
                new JsonObject
                {
                    ["position"] = 1230, ["customeridx"] = 7, ["customertype"] = 4242,
                    ["coffeetype"] = 1, ["starttime"] = 5,
                },
            },
        };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);

        var seat = Assert.Single(content.Seats);
        Assert.Equal(1230, seat.Position);
        Assert.Null(seat.CustomerIdx);
        Assert.Equal("""[{"position":1230}]""", CafeFeature.BuildSeatList(content.Seats).ToJsonString());
    }

    /// <summary>座位数被钳到 <c>SlotEat</c>（1 级 = 6，与 <c>NCafeItemTypes</c> 餐位 <c>Limit[0]</c> 相等）。</summary>
    [Fact]
    public void ReadContent_CapsSeatsToSlotEat()
    {
        var seats = new JsonArray();
        for (int i = 1; i <= 10; i++)
            seats.Add(new JsonObject { ["position"] = 1230 + i });
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject { ["level"] = 1, ["seatlist"] = seats };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);

        Assert.Equal(6, LiveTable.LevelRowFor(1)!.SlotEat);
        Assert.Equal(6, content.Seats.Count);
    }

    /// <summary>
    /// 排队人数被钳到 <c>MaxQSize</c> —— ★ 现网 31 个等级**全是 1**，
    /// 所以「排队里塞两个人」在任何等级都是非法数据。
    /// </summary>
    [Fact]
    public void ReadContent_CapsQueueToMaxQSize()
    {
        var queue = new JsonArray
        {
            new JsonObject { ["customertype"] = 201, ["customeridx"] = 1, ["starttime"] = 1 },
            new JsonObject { ["customertype"] = 202, ["customeridx"] = 2, ["starttime"] = 2 },
            new JsonObject { ["customertype"] = 4242, ["customeridx"] = 3, ["starttime"] = 3 },  // 表外 → 丢
        };
        var player = new Player("acc", 1);
        player.Extra[CafeFeature.ExtraKey] = new JsonObject { ["level"] = 1, ["customerqueue"] = queue };

        var content = CafeFeature.ReadContent(player, new CafeFeature.CafeState(1, 0, 0), LiveTable, 1);

        Assert.Equal(1, LiveTable.LevelRowFor(1)!.MaxQSize);
        var only = Assert.Single(content.Queue);
        Assert.Equal(201, only.CustomerType);
    }

    // ---- 存档：★ 绝不能碰宠物域的 Extra["cafepet"] ----

    /// <summary>
    /// ★★ <see cref="CafeFeature.WriteState"/> 会整体重写 <c>Extra["cafe"]</c>，
    /// 但**绝不能动** <c>Extra["cafepet"]</c>（<see cref="CafePetFeature.ExtraKey"/>）——
    /// 那是宠物域的独立存档（docs/05 §13.4）。
    /// </summary>
    [Fact]
    public void WriteState_NeverTouchesCafePetExtra()
    {
        var player = new Player("acc", 1);
        var petBefore = new JsonObject
        {
            ["nextuid"] = 3,
            ["pets"] = new JsonArray { new JsonObject { ["uid"] = 1, ["templateid"] = 101 } },
        };
        player.Extra[CafePetFeature.ExtraKey] = petBefore;
        string petJson = petBefore.ToJsonString();

        var content = new CafeFeature.CafeContent
        {
            Weights = [new CafeFeature.CafeGuestWeight(201, 1700)],
            WeightDay = 19_723,
        };
        Assert.True(CafeFeature.WriteState(player, new CafeFeature.CafeState(2, 30, 100), content));

        Assert.Equal(petJson, player.Extra[CafePetFeature.ExtraKey]!.ToJsonString());
        Assert.NotSame(player.Extra[CafeFeature.ExtraKey], player.Extra[CafePetFeature.ExtraKey]);
        // 反过来：宠物域的键不会被卷进 ["cafe"]
        var cafe = Assert.IsType<JsonObject>(player.Extra[CafeFeature.ExtraKey]);
        Assert.False(cafe.ContainsKey("pets"));
        Assert.False(cafe.ContainsKey("nextuid"));
    }

    /// <summary>
    /// <c>content = null</c>（只改标量）时不能抹掉已存的清单 ——
    /// 否则「别的地方顺手写一次等级」就会把今日顾客/座位全清掉。
    /// </summary>
    [Fact]
    public void WriteState_WithNullContent_PreservesExistingLists()
    {
        var player = new Player("acc", 1);
        var content = new CafeFeature.CafeContent
        {
            Seats = [new CafeFeature.CafeSeat(1230, null, 0, 0, 0)],
            Weights = [new CafeFeature.CafeGuestWeight(201, 1700)],
            WeightDay = 19_723,
        };
        CafeFeature.WriteState(player, new CafeFeature.CafeState(1, 0, 0), content);

        Assert.True(CafeFeature.WriteState(player, new CafeFeature.CafeState(2, 5, 6)));   // 只改标量

        var cafe = Assert.IsType<JsonObject>(player.Extra[CafeFeature.ExtraKey]);
        Assert.Equal(2, cafe["level"]!.GetValue<int>());
        Assert.Single(Assert.IsType<JsonArray>(cafe["seatlist"]));
        Assert.Single(Assert.IsType<JsonArray>(cafe["weightlist"]));
        Assert.Equal(19_723, cafe["weightday"]!.GetValue<int>());
    }

    /// <summary>六组内容随存档 round-trip，并且第二次写同一份内容时不再落盘。</summary>
    [Fact]
    public void CafeContent_RoundTripsThroughPlayerStore_AndIsIdempotent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sssj-cafe-content", Guid.NewGuid().ToString("N"));
        try
        {
            var table = LiveTable;
            var store = new PlayerStore(new ServerOptions { DataDirectory = dir });
            var player = new Player("cafe_content", 4242);
            player.Girls.Add(new Girl { Girlid = 1, Modelid = 1001 });

            var state = new CafeFeature.CafeState(3, 240, 1200);
            var content = new CafeFeature.CafeContent
            {
                Seats = [new CafeFeature.CafeSeat(1230, null, 0, 0, 0),
                         new CafeFeature.CafeSeat(1231, 5, 201, 1, 1_700_000_000)],
                Queue = [new CafeFeature.CafeQueueGuest(205, 6, 1_700_000_001)],
                Coffees = [new CafeFeature.CafeCoffee(1, 20)],
                AreaGirls = [new List<CafeFeature.CafeAreaGirl> { new(1, 1001) },
                             new List<CafeFeature.CafeAreaGirl>(),
                             new List<CafeFeature.CafeAreaGirl>()],
                Weights = [new CafeFeature.CafeGuestWeight(201, 1700)],
                Visitors = [new CafeFeature.CafeVisitor(10086, 1, 1001, "阿七", 1_700_000_002, 1200)],
                WeightDay = 19_723,
            };
            Assert.True(CafeFeature.WriteState(player, state, content));
            Assert.False(CafeFeature.WriteState(player, state, content));   // 无变化 → 不落盘
            store.Save(player);

            var loaded = store.Load("cafe_content")!;
            loaded.Girls.Clear();
            loaded.Girls.Add(new Girl { Girlid = 1, Modelid = 1001 });
            var back = CafeFeature.ReadContent(loaded, state, table, 19_723);

            Assert.Equal(2, back.Seats.Count);
            Assert.Null(back.Seats[0].CustomerIdx);
            Assert.Equal(5, back.Seats[1].CustomerIdx);
            Assert.Equal(201, back.Seats[1].CustomerType);
            Assert.Equal(1_700_000_000, back.Seats[1].StartTime);
            Assert.Single(back.Queue);
            Assert.Single(back.Coffees);
            Assert.Single(back.AreaGirls[CafeFeature.AreaIndexCafe]);
            Assert.Equal(1700, Assert.Single(back.Weights).Weight);
            var v = Assert.Single(back.Visitors);
            Assert.Equal(10086UL, v.RoleId);
            Assert.Equal("阿七", v.Name);
            Assert.Equal(1200, v.ComfortScore);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---- 日切 ----

    /// <summary>
    /// <see cref="CafeFeature.CafeDayIndex"/> 与客户端 <c>GetLocalDay4AM</c>（Lib.lua:1702..1706）
    /// 同一条式子：<c>floor((unix - 4h) / 86400)</c>。
    /// </summary>
    [Fact]
    public void CafeDayIndex_MatchesGetLocalDay4AM()
    {
        Assert.Equal(4 * 3600, CafeFeature.DayResetOffsetSeconds);
        Assert.Equal(0, CafeFeature.CafeDayIndex(DateTimeOffset.FromUnixTimeSeconds(4 * 3600)));
        Assert.Equal(-1, CafeFeature.CafeDayIndex(DateTimeOffset.FromUnixTimeSeconds(4 * 3600 - 1)));
        // 2024-01-01T12:00:00Z → floor((1704110400 - 14400) / 86400) = 19723
        Assert.Equal(19_723, CafeFeature.CafeDayIndex(DateTimeOffset.FromUnixTimeSeconds(1_704_110_400)));
        // 当天 03:59:59 仍算前一天
        Assert.Equal(19_722, CafeFeature.CafeDayIndex(DateTimeOffset.FromUnixTimeSeconds(1_704_081_599)));
    }

    // ---- 端到端：带真实配置表的 112 ----

    /// <summary>
    /// 直接调 handler 走一遍「有配置表」的路径：今日顾客非空、六组字段结构完整、
    /// 且回包的 key 集合仍是那 13 个。
    ///
    /// 为什么不用 <see cref="LuaCallHarness"/>：它的 <c>ServerOptions.SettingDirectory</c>
    /// 是相对路径 <c>extracted/setting</c>，在测试 bin 目录下解析不到（日志里能看到
    /// 「配置表不存在」的 WARN）；这里用 <see cref="TestPaths.SettingDir"/> 注入真表。
    /// </summary>
    [Fact]
    public void CafeDataPayload_WithLiveTable_HasNonEmptyTodayGuests()
    {
        var table = LiveTable;
        var player = new Player("acc", 4242);
        var state = CafeFeature.ReadState(player, table);
        var content = CafeFeature.ReadContent(player, state, table, 19_723);
        var payload = CafeFeature.BuildCafeDataPayload(state, 1_700_000_000, CafePetState.Read(player), content);

        Assert.Equal(13, payload.Count);
        Assert.Equal(1, payload["level"]!.GetValue<int>());
        Assert.Equal(0, payload["comfort"]!.GetValue<int>());

        var weights = Assert.IsType<JsonArray>(payload["weightlist"]);
        Assert.Equal(4, weights.Count);                    // ★ 今日顾客 4 类（1 级配额）
        foreach (var node in weights)
        {
            var o = Assert.IsType<JsonObject>(node);
            Assert.True(table.Guests.ContainsKey(o["Id"]!.GetValue<int>()));
            Assert.True(o["Weight"]!.GetValue<int>() >= 1);
        }

        // 新号其余五组仍是空数组（没座位/没排队/没上咖啡/没排班/没访客）
        foreach (string k in new[] { "seatlist", "customerqueue", "coffeelist", "roomgirlslist", "visitedList" })
            Assert.Empty(Assert.IsType<JsonArray>(payload[k]));
    }
}

/// <summary>
/// 轻量端到端夹具：真实 <see cref="GameModule"/>（会反射装配所有 <see cref="IGameFeature"/>）
/// + 真实 <see cref="LuaCallRouter"/> + loopback <see cref="ClientSession"/>，
/// 但把 <c>NtfS2CCallSender</c> 换成内存捕获器，于是不需要真的读 socket 就能断言
/// pf 1024 的 <c>(sFunction, sJsonParam)</c>。
///
/// 为什么必须用真 ClientSession：<c>LuaCallHandler</c>/<c>ScriptCallHandler</c> 的签名收的是
/// Core 的 sealed <c>ClientSession</c>（不是 Game 层的 IGameSession），无法用 FakeSession 替换。
/// 而 ClientSession 只在真正写 socket 时才用到网络，所以 loopback 连接足够。
/// </summary>
internal sealed class LuaCallHarness : IDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpClient _client;
    private readonly TcpClient _accepted;
    private readonly string _dataDir;

    public ClientSession Session { get; }
    public LuaCallRouter LuaCalls { get; }
    public Player Player { get; }

    /// <summary>捕获到的下行：(sFunction, sJsonParam 的 UTF-8 文本)。</summary>
    public List<(string Function, string Json)> Outbound { get; } = new();

    private LuaCallHarness(TcpListener listener, TcpClient client, TcpClient accepted,
                           ClientSession session, LuaCallRouter luaCalls, Player player, string dataDir)
    {
        _listener = listener;
        _client = client;
        _accepted = accepted;
        _dataDir = dataDir;
        Session = session;
        LuaCalls = luaCalls;
        Player = player;
    }

    public static async Task<LuaCallHarness> CreateAsync(string account, bool attachPlayer = true)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "sssj-feature-tests", Guid.NewGuid().ToString("N"));

        var pf = new PfRouter();
        var luaCalls = new LuaCallRouter();
        var module = new GameModule(new ServerOptions { DataDirectory = dataDir });
        module.Install(pf, luaCalls);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port);
        var accepted = await listener.AcceptTcpClientAsync();
        await connect;

        var session = new ClientSession(1, accepted);
        var player = module.World.GetOrCreatePlayer(account);
        if (attachPlayer)
            session.Tag = player;

        var harness = new LuaCallHarness(listener, client, accepted, session, luaCalls, player, dataDir);

        // 覆盖 GameModule 装好的真实 sender：只收集，不落到 socket
        luaCalls.NtfS2CCallSender = (_, function, sJsonParam) =>
        {
            harness.Outbound.Add((function, Encoding.UTF8.GetString(sJsonParam)));
            return Task.CompletedTask;
        };

        return harness;
    }

    /// <summary>模拟 pf 1022 里 sFunction="LuaCall" 的上行。</summary>
    public Task DispatchLuaCallAsync(string sJsonParam)
        => LuaCalls.DispatchAsync(Session, LuaCallRouter.LuaCallFunction, sJsonParam);

    /// <summary>模拟 pf 1022 里 sFunction=&lt;字符串命令&gt; 的上行（S2CCall.lua 那一族）。</summary>
    public Task DispatchFunctionAsync(string function, string sJsonParam)
        => LuaCalls.DispatchAsync(Session, function, sJsonParam);

    public void Dispose()
    {
        try { Session.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _accepted.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
