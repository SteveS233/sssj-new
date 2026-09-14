using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 咖啡馆宠物子域（<c>sFunction = "NCafePetLogic"</c>，13 个已实现 sCmd）。
///
/// 断言分四类：
/// <list type="number">
/// <item><b>下行 <c>sCmd</c> 的白名单</b> —— 这是本域最容易致命的地方：客户端
///   <c>NCafePetLogic:Dispatcher</c>（<c>NCafePetLogic.lua:1036..1042</c>）是
///   <c>self[sCmd.."Rsp"](self, tbParam)</c> 且**没有 nil 判断**，回一个拼不出函数的 sCmd
///   = <c>attempt to call a nil value</c>。有 6 个上行命令的同名 Rsp 根本不存在，
///   必须换名字回（<c>OnNewPet → AdoptPet</c> 等）。</item>
/// <item><b>字符串命令族的 <c>nError = 0</c></b>（AGENTS.md §5 / docs/05 §0.1）。</item>
/// <item><b>逐字段必填性</b> —— 每条断言都对着一行会在 nil 上炸的客户端代码：
///   <c>GiveUpPetRsp</c> 的 <c>-1 * tbParam.boxId</c>（<c>:803</c>）、
///   <c>AdoptPetRsp</c> 的 <c>-1 * tbParam.boxId</c>（<c>:741</c>）与 12 个
///   <c>NPetInfo</c> uint 字段（<c>dump.cs:532995</c>）、
///   <c>UpdateFoodBoxsRsp</c> 的 <c>self['foodBox'..i] % 10</c>（<c>:133</c>）。</item>
/// <item><b>畸形上行不抛</b> —— 缺 sCmd / 非对象 / 缺参数 / 未登录。</item>
/// </list>
/// </summary>
public class CafePetFeatureTests
{
    // ---- 现网配置里被本测试钉住的数字（一变就该失败，提示去核对配置） ----

    /// <summary>罐头 1（低星）：<c>PetFoodInfo.txt</c> 行 1，道具 <c>[21-1-1-2]</c>。</summary>
    private const int Food1 = 1;
    private const uint Food1G = 21, Food1D = 1, Food1P = 1, Food1L = 2;

    /// <summary>宠物玩具Ⅰ <c>(22,1,1,1)</c>：<c>ItemList.txt</c> 里 <c>ExtParam1 = 30</c>（好感经验）。</summary>
    private const uint ToyG = 22, ToyD = 1, ToyP = 1, ToyL = 1;
    private const int ToyExp = 30;

    /// <summary>宠物清洁Ⅰ <c>(22,2,1,1)</c>：<c>ExtParam1 = 20</c>（精力）。</summary>
    private const uint CleanG = 22, CleanD = 2, CleanP = 1, CleanL = 1;
    private const int CleanHot = 20;

    private static Task SendAsync(FeatureHarness h, JsonObject param)
        => h.DispatchFunctionAsync(CafePetFeature.Function, param.ToJsonString());

    private static Task SendAsync(FeatureHarness h, string sCmd, params (string Key, JsonNode? Value)[] fields)
    {
        var obj = new JsonObject { ["sCmd"] = sCmd };
        foreach (var (k, v) in fields)
            obj[k] = v;
        return SendAsync(h, obj);
    }

    /// <summary>取最后一条下行，校验 sFunction 与 sCmd，并顺手断言成功包带 <c>nError = 0</c>。</summary>
    private static JsonObject LastReply(FeatureHarness h, string expectSCmd, bool expectSuccess = true)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(CafePetFeature.Function, function);
        var obj = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(expectSCmd, obj["sCmd"]!.GetValue<string>());
        if (expectSuccess)
        {
            // 字符串命令族的成功回包必须带 nError = 0（docs/05 §0.1）
            Assert.Equal(0, obj["nError"]!.GetValue<int>());
            Assert.Null(obj["Error"]);      // SetFoodRsp:229 是真值判断，成功包绝不能带
        }
        return obj;
    }

    /// <summary>给玩家造一个食盆状态（绕过解锁/投食流程，直接写 Extra）。</summary>
    private static CafePetState Seed(Player player, Action<CafePetState> mutate)
    {
        var state = CafePetState.Read(player);
        state.EnsureSlots(6);
        mutate(state);
        state.Write(player);
        return state;
    }

    // =================================================================
    //  0. 配置表
    // =================================================================

    /// <summary>
    /// 七张表都能读到，并钉住现网关键数字。
    /// 这些数字是后面所有端到端断言的前提，单独立一条测试便于定位「是配置变了还是逻辑错了」。
    /// </summary>
    [Fact]
    public void CafeTable_LoadsAllSevenTables_WithLiveNumbers()
    {
        var t = CafeTable.Load(TestPaths.SettingDir);

        Assert.True(t.IsComplete);

        // PetFoodInfo.txt：现网只有 2 行（Id 1 低星 / Id 2 高星）
        // ★ 第十轮换新版 setting.u 后 PetFoodInfo.txt 从 2 种食物扩到 **8 种**
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, t.PetFoods.Keys.OrderBy(k => k).ToArray());
        var f1 = t.PetFoods[1];
        Assert.Equal(new[] { 21, 1, 1, 2 }, f1.Gdpl);              // GDPL = [21-1-1-2]
        Assert.Equal((MoneyType.Gold, 5000), (f1.CostType, f1.CostNum));      // PriceGet = [2,5000]
        Assert.Equal((MoneyType.FreeDiamond, 1), (f1.SpeedType, f1.SpeedNumPer)); // PricePer = [3,1]
        Assert.Equal(new[] { (1, 90), (2, 10) }, f1.RareWeights);  // GetPetRare = {{1,90},{2,10}}
        Assert.Equal(new[] { (2, 90), (3, 10) }, t.PetFoods[2].RareWeights);

        // FoodBoxs.txt：三档解锁价，第一个免费（Index 是「已解锁数 + 1」）
        Assert.Equal(3, t.FoodBoxPrices.Count);
        Assert.Equal(new FoodBoxPriceRow(1, MoneyType.FreeDiamond, 0), t.FoodBoxPriceFor(0));
        Assert.Equal(new FoodBoxPriceRow(2, MoneyType.FreeDiamond, 100), t.FoodBoxPriceFor(1));
        Assert.Equal(new FoodBoxPriceRow(3, MoneyType.FreeDiamond, 200), t.FoodBoxPriceFor(2));
        Assert.Null(t.FoodBoxPriceFor(3));   // 全解锁后没有第 4 档

        // BuffFood.txt：4 种咖啡馆食物
        Assert.Equal(new[] { 1, 2, 3, 4 }, t.BuffFoods.Keys.OrderBy(k => k));
        Assert.Equal((MoneyType.CoffeeCoin, 500, 200, 1),
            (t.BuffFoods[1].PriceType, t.BuffFoods[1].PriceNum, t.BuffFoods[1].AddPoint, t.BuffFoods[1].Rarity));
        Assert.Equal((MoneyType.FreeDiamond, 20, 3000, 4),
            (t.BuffFoods[4].PriceType, t.BuffFoods[4].PriceNum, t.BuffFoods[4].AddPoint, t.BuffFoods[4].Rarity));

        // PetInfo.txt：★★ 第十轮换新版 setting.u 后从 9 只扩到 21 只，
        // 且**多出了第 4 族（401..403）** —— 旧快照只有 1xx/2xx/3xx 三族。
        // 这里只钉「族间空白行不被读成数据」+「Rare 分族正确」，**不写死全量 id 清单**
        // —— 写死会随每次配置更新变红（同 OnBoxFoodDone 那条的教训）。
        Assert.True(t.PetTemplates.Count >= 9, $"PetInfo.txt 只读到 {t.PetTemplates.Count} 只宠物");
        Assert.All(t.PetTemplates.Keys, id => Assert.InRange(id, 101, 499));
        // ★ Rare 是**独立的一列**，与 id 百位无关（新表里 201 就不是 Rare 2）。
        // 只钉「三档都非空、且互不重叠」—— 这才是 GetPetRare 权重能工作的前提。
        var r1 = t.TemplatesOfRare(1).Select(x => x.Id).ToArray();
        var r2 = t.TemplatesOfRare(2).Select(x => x.Id).ToArray();
        var r3 = t.TemplatesOfRare(3).Select(x => x.Id).ToArray();
        Assert.NotEmpty(r1);
        Assert.NotEmpty(r2);
        Assert.NotEmpty(r3);
        Assert.Empty(r1.Intersect(r2));
        Assert.Empty(r2.Intersect(r3));
        Assert.Empty(r1.Intersect(r3));

        // AreaUnlock.txt：6 行 ⇒ 6 个指派位，Cost 全 0（一开始就全开）
        Assert.Equal(6, t.MaxPetAreaCount);

        // LoveLevel.txt / GiveBack.txt：50 级
        Assert.Equal(10, t.LoveLevelNeedExp(1));
        Assert.Equal(30, t.LoveLevelNeedExp(2));
        Assert.Equal(50, t.GiveBacks.Count);
        Assert.Equal(new[] { 22, 1, 1, 1, 1 }, t.GiveBackItemFor(1));   // 1 级 → 1 个宠物玩具Ⅰ
        Assert.Equal(new[] { 22, 1, 1, 1, 2 }, t.GiveBackItemFor(5));   // 门槛取「最后一个 index < level」
        Assert.Equal(new[] { 22, 1, 1, 1, 55 }, t.GiveBackItemFor(50)); // 50 级 → 55 个
    }

    /// <summary>好感等级上限是客户端硬编码的 30/40/50（<c>UI_PetList.lua:728..733</c>），不在配置表里。</summary>
    [Fact]
    public void MaxLoveLevel_IsHardcodedPerRare()
    {
        Assert.Equal(30, CafeTable.MaxLoveLevelForRare(1));
        Assert.Equal(40, CafeTable.MaxLoveLevelForRare(2));
        Assert.Equal(50, CafeTable.MaxLoveLevelForRare(3));
        Assert.Equal(30, CafeTable.MaxLoveLevelForRare(0));   // 未知稀有度按最低档兜底
    }

    /// <summary>缺表时降级成 <see cref="CafeTable.Empty"/> 的等价物，不抛。</summary>
    [Fact]
    public void CafeTable_MissingDirectory_DegradesWithoutThrowing()
    {
        var t = CafeTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-dir-" + Guid.NewGuid()));

        Assert.False(t.IsComplete);
        Assert.Empty(t.PetFoods);
        Assert.Empty(t.PetTemplates);
        Assert.Equal(1, t.MaxPetAreaCount);          // 兜底 1，保证还能指派一只
        Assert.Equal(1, t.LoveLevelNeedExp(1));      // 兜底 1，保证不除零
        Assert.Null(t.GiveBackItemFor(1));
    }

    /// <summary>两个配置串解析器：<c>[2,5000]</c> 与 <c>{{1,90},{2,10}}</c>。</summary>
    [Fact]
    public void ConfigParsers_HandleLiveFormats()
    {
        Assert.Equal((2, 5000), CafeTable.ParsePair("[2,5000]"));
        Assert.Equal((3, 1), CafeTable.ParsePair("[3,1]"));
        Assert.Equal((0, 0), CafeTable.ParsePair(""));          // 没配价格
        Assert.Equal((0, 0), CafeTable.ParsePair("[3]"));       // 位数不足

        Assert.Equal(new[] { (1, 90), (2, 10) }, CafeTable.ParsePairList("{{1,90},{2,10}}"));
        Assert.Empty(CafeTable.ParsePairList(""));
    }

    // =================================================================
    //  1. 下行 sCmd 白名单（本域最致命的坑）
    // =================================================================

    /// <summary>
    /// 允许的下行 sCmd 只有 14 个，每个在 <c>NCafePetLogic.lua</c> / <c>NCafePetTour.lua</c> 里
    /// 都有真实的 <c>&lt;sCmd&gt;Rsp</c>。特别注意**上行名不在里面**的那 6 个。
    /// </summary>
    [Fact]
    public void AllowedReplyCmds_AreExactlyTheOnesWithRspFunctions()
    {
        Assert.Equal(
            new[]
            {
                "AdoptPet",             // :718 AdoptPetRsp        ← 上行是 OnNewPet
                "ChangePetCafeState",   // :827
                "CheckTourIsSuccess",   // NCafePetTour.lua:418
                "CompleteTour",         // NCafePetTour.lua:436
                "GetCafeFoodNum",       // :432
                "GiveUpPet",            // :793 GiveUpPetRsp       ← 上行是 OnGiveupPet
                "GiveUpPetOwned",       // :962 GiveUpPetOwnedRsp  ← 上行是 DelPet
                "PetGift",              // :920
                "PetRename",            // :943
                "SetFood",              // :225
                "SetTourPets",          // NCafePetTour.lua:128
                "UnLockTourArea",       // NCafePetTour.lua:63
                "UpdateFoodBoxs",       // :100
                "UpdatePet",            // :761（纯下行推送）
            },
            CafePetFeature.AllowedReplyCmds.OrderBy(s => s, StringComparer.Ordinal));

        // 这 6 个上行 sCmd 没有同名 Rsp，绝不能原样回显
        foreach (string upstream in new[]
                 {
                     CafePetFeature.CmdOnFoodBoxOpen, CafePetFeature.CmdOnBoxFoodDone,
                     CafePetFeature.CmdOnGetGiveupItem, CafePetFeature.CmdOnNewPet,
                     CafePetFeature.CmdOnGiveupPet, CafePetFeature.CmdDelPet,
                 })
        {
            Assert.DoesNotContain(upstream, CafePetFeature.AllowedReplyCmds);
        }
    }

    /// <summary>白名单外的 sCmd 直接抛 —— 宁可服务端自测失败，也不要在真机上让客户端 nil 调用。</summary>
    [Fact]
    public void ReplySCmd_RejectsCmdWithoutRspFunction()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CafePetFeature.ReplySCmd(CafePetFeature.CmdOnNewPet, new JsonObject()));
        Assert.Contains("OnNewPetRsp", ex.Message);
    }

    /// <summary><c>sCmd</c> 与 <c>nError</c> 排在最前，且已有 <c>nError</c> 时不被覆盖（失败包用 1）。</summary>
    [Fact]
    public void ReplySCmd_PutsSCmdAndNErrorFirst()
    {
        var ok = CafePetFeature.ReplySCmd(CafePetFeature.RspSetFood, new JsonObject { ["param"] = new JsonArray() });
        Assert.Equal("""{"sCmd":"SetFood","nError":0,"param":[]}""", ok.ToJsonString());

        var bad = CafePetFeature.ReplySCmd(CafePetFeature.RspSetFood, CafePetFeature.BuildErrorPayload());
        Assert.Equal("""{"sCmd":"SetFood","Error":1,"nError":1}""", bad.ToJsonString());
    }

    // =================================================================
    //  2. GetCafeFoodNum —— AGENTS.md 里记的那条 unhandled
    // =================================================================

    /// <summary>
    /// 真机日志里的 <c>unhandled sFunction=NCafePetLogic param={"sCmd":"GetCafeFoodNum"}</c>。
    /// 上行**只有 sCmd**（<c>NCafePetLogic.lua:421..425</c> 的 tbParam 除 sCmd 外是空的），
    /// 回包也**只需要 sCmd + nError**：<c>GetCafeFoodNumRsp</c>（<c>:432..496</c>）除了
    /// <c>if not tbParam then return end</c> 之外一个字段都不读（要读的 ChangePetList 在
    /// <c>:437..444</c> 被整块注释掉了），它做的是「拿任务值重算 + 重建定时器 + 刷 3 个界面」。
    /// </summary>
    [Fact]
    public async Task GetCafeFoodNum_RepliesWithSCmdAndNErrorOnly()
    {
        using var h = await FeatureHarness.CreateAsync("pet_foodnum");

        await SendAsync(h, CafePetFeature.CmdGetCafeFoodNum);

        var reply = LastReply(h, CafePetFeature.RspGetCafeFoodNum);
        Assert.Equal("""{"sCmd":"GetCafeFoodNum","nError":0}""", reply.ToJsonString());
    }

    /// <summary>
    /// <c>(49, 200)</c> <c>lastCalTime</c> 必须被推进到「现在」并推给客户端（pf 1026）——
    /// 客户端 <c>GetFoodBuffTime</c>（<c>NCafePetLogic.lua:642..666</c>）拿它当基准算
    /// <c>CafeFoodTime = nTime + lastCalTime</c>，服务端不更新就等于时间不流动。
    /// </summary>
    [Fact]
    public async Task GetCafeFoodNum_AdvancesLastCalTimeAndPushesTaskValues()
    {
        using var h = await FeatureHarness.CreateAsync("pet_lastcal");
        Assert.Equal(0, h.Player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.TaskLastCalTime));

        await SendAsync(h, CafePetFeature.CmdGetCafeFoodNum);

        int last = h.Player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.TaskLastCalTime);
        Assert.True(last > 1_700_000_000, $"(49,200) 应是服务器 unix 秒，实际 {last}");

        // ★ 光改任务值不够，必须**推 pf 1026** —— 客户端全靠 group 49 的任务值渲染
        //   食物点数/食盆倒计时，不推的话界面一直是旧值。
        //   （本测试原先只断言了任务值本身，因为 FeatureHarness.PushedPf 曾是死字段；
        //    该字段本轮已实现，断言 pf 一律用 WaitForPfAsync，裸读会 flaky。）
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp),
            $"没等到 pf 1026，实际推了：[{string.Join(",", h.PushedPf)}]");
    }

    /// <summary>
    /// 任务值坐标必须与客户端读的完全一致：食物 id 在 <c>101 + (i-1)*2</c>、
    /// 剩余秒数在 <c>+1</c>（<c>NCafePetLogic.lua:624..626</c>），共 20 格。
    /// </summary>
    [Fact]
    public void FoodSlotTaskIds_MatchClientLayout()
    {
        Assert.Equal(101, CafePetFeature.FoodSlotIdTask(1));
        Assert.Equal(102, CafePetFeature.FoodSlotTimeTask(1));
        Assert.Equal(103, CafePetFeature.FoodSlotIdTask(2));
        Assert.Equal(139, CafePetFeature.FoodSlotIdTask(20));
        Assert.Equal(140, CafePetFeature.FoodSlotTimeTask(20));
        Assert.Equal(20, CafePetFeature.TaskFoodSlotCount);
        Assert.Equal(49, CafePetFeature.TaskGroup);      // NCafePetTour.lua:11 覆盖了 :28 的 95
    }

    /// <summary>
    /// 图鉴计数坐标 = <c>1000 + floor(tmpId/100)*20 + tmpId%10</c>
    /// （<c>NCafePetTour.lua:452..454</c>）。领养时要 +1，否则「第一次遇到该品种」的
    /// <c>UI_PetNew</c> 弹窗永远不出（<c>NCafePetLogic.lua:751</c>）。
    /// </summary>
    [Fact]
    public void RikiTaskId_MatchesGetTmpNowPawCount()
    {
        Assert.Equal(1021, CafePetFeature.RikiTaskId(101));
        Assert.Equal(1023, CafePetFeature.RikiTaskId(103));
        Assert.Equal(1041, CafePetFeature.RikiTaskId(201));
        Assert.Equal(1063, CafePetFeature.RikiTaskId(303));
    }

    // =================================================================
    //  3. UpdateFoodBoxs —— 第二条「进店必发」
    // =================================================================

    /// <summary>
    /// <c>param</c> 必须是**长度恰好 3、按 uid 升序**的稠密数组：
    /// <c>UpdateFoodBoxsRsp</c> 用的是数组下标（<c>:106 for i,v in pairs(tbParam.param)</c>），
    /// 而 <c>:131..136</c> 又无条件跑一遍 <c>self['foodBox'..i] % 10</c>（i=1..3）——
    /// 少发一格就是 <c>nil % 10</c>，并且 <c>UpdateFoodBoxsTime</c>（<c>:194</c>）会把
    /// <c>foodBox{i}Time - GetTime()</c> 放进每秒定时器里反复抛。
    /// </summary>
    [Fact]
    public async Task UpdateFoodBoxs_ParamIsAlwaysThreeDenseEntries()
    {
        using var h = await FeatureHarness.CreateAsync("pet_boxes");

        await SendAsync(h, CafePetFeature.CmdUpdateFoodBoxs);

        var reply = LastReply(h, CafePetFeature.RspUpdateFoodBoxs);
        var param = Assert.IsType<JsonArray>(reply["param"]);
        Assert.Equal(CafePetFeature.FoodBoxCount, param.Count);
        for (int i = 0; i < param.Count; i++)
            Assert.Equal(i + 1, param[i]!["uid"]!.GetValue<int>());
    }

    /// <summary>
    /// ★ 未解锁的格子**不能带 <c>type</c> 字段**。四个 <c>elseif</c> 都以
    /// <c>if v.type and ...</c> 开头（<c>NCafePetLogic.lua:108..116</c>），
    /// <c>type</c> 为 nil 时 state 停在初值 0 = 未解锁；
    /// 填 <c>type = 0</c> 会被判成「已解锁无食物」，玩家不花钱白拿一个食盆。
    /// </summary>
    [Fact]
    public void BuildBoxParam_LockedBoxOmitsTypeKey()
    {
        var player = new Player("acc", 1);
        var state = Seed(player, s =>
        {
            s.Box(1)!.Unlocked = true;                       // 已解锁、空
            s.Box(2)!.Unlocked = true;
            s.Box(2)!.Type = Food1;                          // 有食物倒计时
            s.Box(2)!.Time = 1_700_000_000;
            // 食盆 3 保持未解锁
        });

        var param = CafePetFeature.BuildBoxParam(state);

        Assert.Equal(new[] { "uid", "type", "time" }, param[0]!.AsObject().Select(kv => kv.Key));
        Assert.Equal(0, param[0]!["type"]!.GetValue<int>());
        Assert.Equal(Food1, param[1]!["type"]!.GetValue<int>());
        Assert.Equal(1_700_000_000L, param[1]!["time"]!.GetValue<long>());

        // 第 3 格：只有 uid，没有 type / time
        Assert.Equal(new[] { "uid" }, param[2]!.AsObject().Select(kv => kv.Key));
        Assert.Null(param[2]!["type"]);
        Assert.Null(param[2]!["time"]);
    }

    /// <summary>有宠物的格子要带 <c>pet</c>（<c>ClickFood</c> 的 state 3 分支要 <c>boxPet{i}</c>）。</summary>
    [Fact]
    public void BuildBoxParam_AttractedPetGoesIntoPetField()
    {
        var player = new Player("acc", 1);
        var state = Seed(player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Pet = new CafePet { Uid = 1, TemplateId = 102, Gender = 2, Nature = 3, Iq = 66, Lv = 1 };
        });

        var pet = Assert.IsType<JsonObject>(CafePetFeature.BuildBoxParam(state)[0]!["pet"]);
        Assert.Equal(102u, pet["templateid"]!.GetValue<uint>());
        Assert.Equal(2u, pet["gender"]!.GetValue<uint>());
    }

    // =================================================================
    //  4. 食盆生命周期：解锁 → 投食 → 加速 → 领养
    // =================================================================

    /// <summary>
    /// 解锁第一个食盆是免费的（<c>FoodBoxs.txt</c> Index=1 → <c>[3,0]</c>），
    /// 回包 sCmd 用 <b><c>UpdateFoodBoxs</c></b>（<c>OnFoodBoxOpenRsp</c> 不存在）。
    /// </summary>
    [Fact]
    public async Task OnFoodBoxOpen_FirstBoxIsFree_AndRepliesUpdateFoodBoxs()
    {
        using var h = await FeatureHarness.CreateAsync("pet_open1");

        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen, ("nBoxId", 1));

        var reply = LastReply(h, CafePetFeature.RspUpdateFoodBoxs);
        var param = Assert.IsType<JsonArray>(reply["param"]);
        Assert.Equal(0, param[0]!["type"]!.GetValue<int>());     // 已解锁、无食物
        Assert.Null(param[1]!["type"]);                          // 2/3 还锁着
        Assert.Equal(1, CafePetState.Read(h.Player).UnlockedCount);
    }

    /// <summary>
    /// 第二个食盆要 100 免费钻，余额不足时**不回包、不扣钱、不解锁**
    /// （本域不挂 Blocking 遮罩，所以「不回包」是安全的降级 —— docs/05 §0.4）。
    /// </summary>
    [Fact]
    public async Task OnFoodBoxOpen_SecondBoxChargesDiamond_AndRejectsWhenPoor()
    {
        using var h = await FeatureHarness.CreateAsync("pet_open2");
        Seed(h.Player, s => s.Box(1)!.Unlocked = true);
        // ★ 新号自带 3000 免费钻（ServerOptions.StartDiamond），验「余额不足」必须先清零
        h.Player.SetMoney(MoneyType.FreeDiamond, 0);

        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen, ("nBoxId", 2));
        Assert.Empty(h.Outbound);                                   // 钻石 0，直接拒
        Assert.Equal(1, CafePetState.Read(h.Player).UnlockedCount);

        h.Player.SetMoney(MoneyType.FreeDiamond, 150);
        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen, ("nBoxId", 2));

        LastReply(h, CafePetFeature.RspUpdateFoodBoxs);
        Assert.Equal(50, h.Player.GetMoney(MoneyType.FreeDiamond));  // 150 - 100
        Assert.Equal(2, CafePetState.Read(h.Player).UnlockedCount);
    }

    /// <summary>越界 / 重复解锁不抛异常。</summary>
    [Fact]
    public async Task OnFoodBoxOpen_OutOfRange_DoesNotThrow()
    {
        using var h = await FeatureHarness.CreateAsync("pet_open_bad");

        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen, ("nBoxId", 0));
        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen, ("nBoxId", 99));
        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen);         // 完全不带 nBoxId

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// 投食：扣 1 个 <c>(21,1,1,2)</c> 罐头（<c>PetFoodInfo.GDPL</c>），
    /// 食盆变成 <c>type = 罐头 id</c> + 8 小时倒计时（<see cref="CafePetFeature.FoodAttractSeconds"/>），
    /// 并推 pf 1031 让仓库刷新。
    /// </summary>
    [Fact]
    public async Task SetFood_ConsumesCanAndStartsEightHourTimer()
    {
        using var h = await FeatureHarness.CreateAsync("pet_setfood");
        Seed(h.Player, s => s.Box(1)!.Unlocked = true);
        h.Player.AddItem(Food1G, Food1D, Food1P, Food1L, 3);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await SendAsync(h, CafePetFeature.CmdSetFood, ("nBoxId", 1), ("nId", Food1));

        var reply = LastReply(h, CafePetFeature.RspSetFood);
        var box = Assert.IsType<JsonArray>(reply["param"])[0]!;
        Assert.Equal(Food1, box["type"]!.GetValue<int>());
        long due = box["time"]!.GetValue<long>();
        Assert.InRange(due, before + CafePetFeature.FoodAttractSeconds,
                            before + CafePetFeature.FoodAttractSeconds + 5);

        Assert.Equal(2u, h.Player.Items.Single(i => i.Genre == Food1G).Count);
    }

    /// <summary>
    /// 投食失败一律回 <c>{Error = 1}</c> —— <c>SetFoodRsp</c> 的
    /// <c>if tbParam.Error then return end</c>（<c>NCafePetLogic.lua:229</c>）是本族唯一的失败闸门。
    /// 三种失败：食盆未解锁 / 罐头 id 不在配置 / 背包里没有那个罐头。
    /// </summary>
    [Fact]
    public async Task SetFood_Failures_ReplyWithErrorKey()
    {
        using var h = await FeatureHarness.CreateAsync("pet_setfood_err");

        // ① 食盆未解锁
        await SendAsync(h, CafePetFeature.CmdSetFood, ("nBoxId", 1), ("nId", Food1));
        var r1 = LastReply(h, CafePetFeature.RspSetFood, expectSuccess: false);
        Assert.Equal(1, r1["Error"]!.GetValue<int>());
        Assert.Equal(1, r1["nError"]!.GetValue<int>());

        // ② 罐头 id 不在 PetFoodInfo.txt（现网只有 1、2）
        Seed(h.Player, s => s.Box(1)!.Unlocked = true);
        await SendAsync(h, CafePetFeature.CmdSetFood, ("nBoxId", 1), ("nId", 999));
        Assert.Equal(1, LastReply(h, CafePetFeature.RspSetFood, expectSuccess: false)["Error"]!.GetValue<int>());

        // ③ 背包里没有罐头
        await SendAsync(h, CafePetFeature.CmdSetFood, ("nBoxId", 1), ("nId", Food1));
        Assert.Equal(1, LastReply(h, CafePetFeature.RspSetFood, expectSuccess: false)["Error"]!.GetValue<int>());
        Assert.Equal(0, CafePetState.Read(h.Player).Box(1)!.Type);
    }

    /// <summary>
    /// 加速价必须与客户端的展示式**逐字**一致：
    /// <c>speedNumPer * (hour*6 + floor(min/10) + 1)</c>（<c>UI_PetFinishFast.lua:61</c>）。
    /// 末尾那个 <c>+1</c> 是写死的，所以剩 0 秒也收 1 份。
    /// </summary>
    [Fact]
    public void SpeedUpCost_MatchesClientDisplayFormula()
    {
        Assert.Equal(1, CafePetFeature.SpeedUpCost(0, 1));            // 0h0m → 0*6+0+1
        Assert.Equal(1, CafePetFeature.SpeedUpCost(599, 1));          // 9m59s → 仍是 1
        Assert.Equal(2, CafePetFeature.SpeedUpCost(600, 1));          // 10m   → 0*6+1+1
        Assert.Equal(7, CafePetFeature.SpeedUpCost(3600, 1));         // 1h    → 1*6+0+1
        Assert.Equal(49, CafePetFeature.SpeedUpCost(8 * 3600, 1));    // 8h    → 8*6+0+1
        Assert.Equal(98, CafePetFeature.SpeedUpCost(8 * 3600, 2));    // 单价 2
        Assert.Equal(1, CafePetFeature.SpeedUpCost(-100, 1));         // 负数按 0 处理
    }

    /// <summary>
    /// 加速完成：按公式扣免费钻（<c>PricePer = [3,1]</c>），食盆立刻结算出宠物，
    /// 回包 sCmd 是 <b><c>SetFood</c></b>（<c>OnBoxFoodDoneRsp</c> 不存在；而
    /// <c>SetFoodRsp</c> 会顺手 <c>UI.Close('UI_PetFinishFast')</c>，<c>:272</c>）。
    /// </summary>
    [Fact]
    public async Task OnBoxFoodDone_ChargesByFormula_AndResolvesPet()
    {
        using var h = await FeatureHarness.CreateAsync("pet_fast");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Seed(h.Player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Type = Food1;
            s.Box(1)!.Time = now + 3600;      // 剩 1 小时 → 1*6+0+1 = 7 钻
        });
        h.Player.SetMoney(MoneyType.FreeDiamond, 10);

        await SendAsync(h, CafePetFeature.CmdOnBoxFoodDone, ("nBoxId", 1));

        var reply = LastReply(h, CafePetFeature.RspSetFood);
        var box = Assert.IsType<JsonArray>(reply["param"])[0]!;
        Assert.Equal(0, box["type"]!.GetValue<int>());                 // type 归 0 + 有 pet ⇒ state 3
        var pet = Assert.IsType<JsonObject>(box["pet"]);
        Assert.Equal(3, h.Player.GetMoney(MoneyType.FreeDiamond));     // 10 - 7

        // 罐头 1 的稀有度权重是 {{1,90},{2,10}} ⇒ 只可能抽到 Rare 1/2 的宠物。
        // ★★ 第十轮改成**按表现算**候选集，不再写死 id 清单 ——
        // 换新版 `setting.u` 后 `PetInfo.txt` 补了 104/105/204/206/305/306，
        // 写死清单会随每次配置更新变红，而它要钉的其实是「不会抽到 Rare ≥ 3」。
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        var expected = cafe.TemplatesOfRare(1).Concat(cafe.TemplatesOfRare(2))
                           .Select(p => p.Id).ToArray();
        Assert.NotEmpty(expected);
        Assert.Contains(pet["templateid"]!.GetValue<int>(), expected);
    }

    /// <summary>钻石不够时回 Error，不扣钱、不结算。</summary>
    [Fact]
    public async Task OnBoxFoodDone_NotEnoughMoney_RepliesError()
    {
        using var h = await FeatureHarness.CreateAsync("pet_fast_poor");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Seed(h.Player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Type = Food1;
            s.Box(1)!.Time = now + 8 * 3600;
        });
        h.Player.SetMoney(MoneyType.FreeDiamond, 0);   // 新号自带 3000，先清零才能验拒绝路径

        await SendAsync(h, CafePetFeature.CmdOnBoxFoodDone, ("nBoxId", 1));

        Assert.Equal(1, LastReply(h, CafePetFeature.RspSetFood, expectSuccess: false)["Error"]!.GetValue<int>());
        Assert.Equal(Food1, CafePetState.Read(h.Player).Box(1)!.Type);
    }

    /// <summary>
    /// 倒计时自然到点：客户端每秒定时器（<c>NCafePetLogic.lua:189..213</c>）会补发一条
    /// <c>UpdateFoodBoxs</c>，服务端就在这条上**懒结算**（不需要任何服务端定时器）。
    /// </summary>
    [Fact]
    public async Task UpdateFoodBoxs_ResolvesExpiredFoodIntoPet()
    {
        using var h = await FeatureHarness.CreateAsync("pet_expire");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Seed(h.Player, s =>
        {
            s.Box(2)!.Unlocked = true;
            s.Box(2)!.Type = Food1;
            s.Box(2)!.Time = now - 1;         // 已过期
        });

        await SendAsync(h, CafePetFeature.CmdUpdateFoodBoxs);

        var box = Assert.IsType<JsonArray>(LastReply(h, CafePetFeature.RspUpdateFoodBoxs)["param"])[1]!;
        Assert.Equal(0, box["type"]!.GetValue<int>());
        Assert.NotNull(box["pet"]);
        Assert.Equal(0L, box["time"]!.GetValue<long>());
        Assert.NotNull(CafePetState.Read(h.Player).Box(2)!.Pet);   // 已落盘
    }

    // =================================================================
    //  5. 领养 / 放生
    // =================================================================

    /// <summary>
    /// 领养回包（<c>AdoptPetRsp</c>，<c>NCafePetLogic.lua:718..759</c>）：
    /// <list type="bullet">
    ///   <item>下行 sCmd = <b><c>AdoptPet</c></b>（上行是 <c>OnNewPet</c>）；</item>
    ///   <item><c>param</c> 必填 —— <c>:719</c> 第一句就转调 <c>SetFoodRsp</c>；</item>
    ///   <item><c>boxId</c> 必填 —— <c>:741</c> <c>-1 * tbParam.boxId</c> 是无条件算术
    ///     （★ 上行叫 <c>nBoxId</c>、下行叫 <c>boxId</c>）；</item>
    ///   <item><c>petInfo</c> 的 12 个字段全必填 —— 逐个写进 C# <c>NPetInfo</c> 的 uint 公开字段。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task OnNewPet_ReplyHasBoxIdParamAndTwelveFieldPetInfo()
    {
        using var h = await FeatureHarness.CreateAsync("pet_adopt");
        Seed(h.Player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Pet = new CafePet
            {
                Uid = 1, TemplateId = 103, Gender = 1, Nature = 2, Iq = 88,
                Hobby = 3, BirthDay = 1_600_000_000, MeetDay = 1_700_000_000, Lv = 1,
            };
        });

        await SendAsync(h, CafePetFeature.CmdOnNewPet, ("nBoxId", 1), ("name", "小黑"));

        var reply = LastReply(h, CafePetFeature.RspAdoptPet);
        Assert.Equal(1, reply["boxId"]!.GetValue<int>());                       // ★ 键名是 boxId
        Assert.Equal(CafePetFeature.FoodBoxCount, Assert.IsType<JsonArray>(reply["param"]).Count);

        var info = Assert.IsType<JsonObject>(reply["petInfo"]);
        Assert.Equal(
            new[]
            {
                "birthday", "exp", "gender", "hobby", "hot", "iq",
                "lv", "meetday", "name", "nature", "templateid", "uid",
            },
            info.Select(kv => kv.Key).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal("小黑", info["name"]!.GetValue<string>());
        Assert.Equal(103u, info["templateid"]!.GetValue<uint>());
        Assert.Equal(1u, info["uid"]!.GetValue<uint>());
        Assert.Equal(1u, info["lv"]!.GetValue<uint>());

        // 食盆已清空，宠物进了名册，nextpetid 前进
        var st = CafePetState.Read(h.Player);
        Assert.Null(st.Box(1)!.Pet);
        Assert.Equal(2, st.NextUid);
        Assert.Equal("小黑", st.Pets.Single().Name);
    }

    /// <summary>
    /// 领养要把图鉴计数（<c>(49, 1000 + RikiId)</c> 的低 16 位）+1，
    /// 并**先推 pf 1026 再回包** —— 客户端 <c>:751</c> 用
    /// <c>GetTmpNowPawCount(tmpId) == 1</c> 决定要不要弹 <c>UI_PetNew</c>，读的是本地任务值。
    /// </summary>
    [Fact]
    public async Task OnNewPet_IncrementsPawCountBeforeReplying()
    {
        using var h = await FeatureHarness.CreateAsync("pet_paw");
        Seed(h.Player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Pet = new CafePet { Uid = 1, TemplateId = 201, Gender = 1, Nature = 1, Lv = 1 };
        });

        await SendAsync(h, CafePetFeature.CmdOnNewPet, ("nBoxId", 1), ("name", "喵"));

        int task = CafePetFeature.RikiTaskId(201);
        Assert.Equal(1041, task);
        Assert.Equal(1, h.Player.GetTask(CafePetFeature.TaskGroup, task) & 0xFFFF);
    }

    /// <summary>空名字/缺 name 时兜底成 <c>pet&lt;uid&gt;</c>：
    /// <c>customName</c> 是 C# <c>string</c> 字段（<c>dump.cs:533000</c>），
    /// 客户端 <c>self.Name.text = info.customName</c>（<c>UI_PetInfo.lua:270</c>）不能吃 nil。</summary>
    [Fact]
    public void SanitizeName_NeverReturnsEmpty()
    {
        Assert.Equal("pet7", CafePetFeature.SanitizeName(null, 7));
        Assert.Equal("pet7", CafePetFeature.SanitizeName(JsonValue.Create(""), 7));
        Assert.Equal("pet7", CafePetFeature.SanitizeName(JsonValue.Create("   "), 7));
        Assert.Equal("旺财", CafePetFeature.SanitizeName(JsonValue.Create("旺财"), 7));
        Assert.Equal(24, CafePetFeature.SanitizeName(JsonValue.Create(new string('x', 99)), 7).Length);
    }

    /// <summary>
    /// 放生「吸引来的」宠物：下行 sCmd = <b><c>GiveUpPet</c></b>，
    /// <c>boxId</c> **必填**（<c>:803</c> <c>PetController:GiveUpPet(-1 * tbParam.boxId)</c> 无条件算术），
    /// <c>param</c> 也必填（<c>:802</c> 转调 <c>SetFoodRsp</c>）。
    /// 食盆随后变成「放生礼物待领」（<c>type = 9999</c>）。
    /// ★ 上行键名是 <c>boxId</c>（不带 n），与 <c>SetFood</c> 那批的 <c>nBoxId</c> 不同。
    /// </summary>
    [Fact]
    public async Task OnGiveupPet_ReplyNeedsBoxIdAndTurnsBoxIntoGift()
    {
        using var h = await FeatureHarness.CreateAsync("pet_giveup");
        Seed(h.Player, s =>
        {
            s.Box(3)!.Unlocked = true;
            s.Box(3)!.Pet = new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 };
        });

        await SendAsync(h, CafePetFeature.CmdOnGiveupPet, ("boxId", 3));

        var reply = LastReply(h, CafePetFeature.RspGiveUpPet);
        Assert.Equal(3, reply["boxId"]!.GetValue<int>());
        var box = Assert.IsType<JsonArray>(reply["param"])[2]!;
        Assert.Equal(CafePetFeature.GiveUpGiftType, box["type"]!.GetValue<int>());
        Assert.Null(box["pet"]);
        Assert.Empty(CafePetState.Read(h.Player).Pets);      // 没被领养，不进名册
    }

    /// <summary>
    /// 领放生礼物：按 <c>GiveBack.txt</c> 1 级档发 1 个宠物玩具Ⅰ <c>[22-1-1-1-1]</c>，
    /// 回 <b><c>UpdateFoodBoxs</c></b>（<c>OnGetGiveupItemRsp</c> 不存在），并把食盆清空。
    /// </summary>
    [Fact]
    public async Task OnGetGiveupItem_GrantsGiveBackItemAndClearsBox()
    {
        using var h = await FeatureHarness.CreateAsync("pet_gift");
        Seed(h.Player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Type = CafePetFeature.GiveUpGiftType;
        });

        await SendAsync(h, CafePetFeature.CmdOnGetGiveupItem, ("boxId", 1));

        // 最后一条是 UpdateFoodBoxs；中间还会有一条 LuaCall 75 的奖励弹窗
        var reply = LastReply(h, CafePetFeature.RspUpdateFoodBoxs);
        Assert.Equal(0, Assert.IsType<JsonArray>(reply["param"])[0]!["type"]!.GetValue<int>());
        Assert.Equal(1u, h.Player.Items.Single(i => i.Genre == ToyG && i.Detail == ToyD).Count);
    }

    // =================================================================
    //  6. 指派
    // =================================================================

    /// <summary>
    /// 指派回包必须同时带 <c>nIndex</c> 与 <c>uid</c>
    /// （<c>:828 if not tbParam.nIndex or not tbParam.uid then return end</c>；
    /// Lua 里 0 为真，所以「撤下」用 <c>uid = 0</c> 能过闸门）。
    /// </summary>
    [Fact]
    public async Task ChangePetCafeState_ReplyHasIndexAndUid()
    {
        using var h = await FeatureHarness.CreateAsync("pet_assign");
        Seed(h.Player, s => s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 }));

        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 1), ("uid", 1));

        var reply = LastReply(h, CafePetFeature.RspChangePetCafeState);
        Assert.Equal(1, reply["nIndex"]!.GetValue<int>());
        Assert.Equal(1, reply["uid"]!.GetValue<int>());
        Assert.Null(reply["nCleanIndex"]);                 // 之前没在别的位置
        Assert.Equal(1, CafePetState.Read(h.Player).PetsInCafeCount);

        // 撤下
        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 1), ("uid", 0));
        Assert.Equal(0, LastReply(h, CafePetFeature.RspChangePetCafeState)["uid"]!.GetValue<int>());
        Assert.Equal(0, CafePetState.Read(h.Player).PetsInCafeCount);
    }

    /// <summary>
    /// ★ 换位时**必须**带 <c>nCleanIndex</c>：客户端靠它把旧位置清 0
    /// （<c>:843..849</c>）。不带 = 同一只宠物在客户端占两个位置，与服务端不一致。
    /// </summary>
    [Fact]
    public async Task ChangePetCafeState_MovingPetSendsNCleanIndex()
    {
        using var h = await FeatureHarness.CreateAsync("pet_move");
        Seed(h.Player, s => s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 }));

        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 2), ("uid", 1));
        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 5), ("uid", 1));

        var reply = LastReply(h, CafePetFeature.RspChangePetCafeState);
        Assert.Equal(5, reply["nIndex"]!.GetValue<int>());
        Assert.Equal(2, reply["nCleanIndex"]!.GetValue<int>());

        var st = CafePetState.Read(h.Player);
        Assert.Equal(1, st.PetsInCafeCount);
        Assert.Equal(1, st.InCafe[4]);
        Assert.Equal(0, st.InCafe[1]);
    }

    /// <summary>指派位越界（&gt; <c>AreaUnlock.txt</c> 行数 6）或 uid 不属于自己 → 不回包不抛。</summary>
    [Fact]
    public async Task ChangePetCafeState_RejectsBadIndexAndForeignPet()
    {
        using var h = await FeatureHarness.CreateAsync("pet_assign_bad");

        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 7), ("uid", 0));
        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 0), ("uid", 0));
        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 1), ("uid", 42));
        await SendAsync(h, CafePetFeature.CmdChangePetCafeState);

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// <c>petstatelist</c> 是**按指派位下标**的稠密数组（<c>InitPetsInCafe</c> 的
    /// <c>for k,v in ipairs</c>，<c>:865</c>）：位置 2 有宠物、位置 1 空必须发
    /// <c>[{uid:0},{uid:5}]</c>，否则宠物会跑到位置 1。
    /// </summary>
    [Fact]
    public void BuildPetStateList_IsDenseBySlot()
    {
        var player = new Player("acc", 1);
        var state = Seed(player, s =>
        {
            s.Pets.Add(new CafePet { Uid = 5, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 });
            s.InCafe[1] = 5;      // 位置 2
        });

        var list = CafePetFeature.BuildPetStateList(state);
        Assert.Equal(6, list.Count);                                 // AreaUnlock.txt 6 行
        Assert.Equal(0, list[0]!["uid"]!.GetValue<int>());
        Assert.Equal(5, list[1]!["uid"]!.GetValue<int>());

        // 一只都没指派时是空数组（客户端 PetsInCafe_Sort = {}，AdoptPetRsp 的自动指派靠 not [i] 找空位）
        var empty = Seed(new Player("acc2", 2), _ => { });
        Assert.Empty(CafePetFeature.BuildPetStateList(empty));
    }

    // =================================================================
    //  7. 送礼 / 改名 / 放生已领养
    // =================================================================

    /// <summary>
    /// 送玩具（genre 22 / detail 1）加好感经验，<c>ExtParam1</c> 是每个的量（玩具Ⅰ = 30）。
    /// ★ <c>UpdatePet</c> 必须**先于** <c>PetGift</c> 回包：<c>PetGiftRsp</c> 转调的
    /// <c>UI_PetList:OnPetGift</c>（<c>UI_PetList.lua:706..746</c>）读的是客户端**本地缓存**
    /// 的 <c>lv</c>/<c>loveExp</c>，顺序反了就显示旧数值。
    /// </summary>
    [Fact]
    public async Task PetGift_Toy_AddsLoveExp_AndPushesUpdatePetFirst()
    {
        using var h = await FeatureHarness.CreateAsync("pet_gift_toy");
        Seed(h.Player, s => s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 }));
        h.Player.AddItem(ToyG, ToyD, ToyP, ToyL, 5);

        await SendAsync(h, CafePetFeature.CmdPetGift,
            ("uid", 1), ("tbItem", new JsonArray { (int)ToyG, (int)ToyD, (int)ToyP, (int)ToyL, 2 }));

        // 倒数第二条是 UpdatePet，最后一条才是 PetGift
        Assert.Equal(2, h.Outbound.Count);
        var update = Assert.IsType<JsonObject>(JsonNode.Parse(h.Outbound[0].Json));
        Assert.Equal(CafePetFeature.RspUpdatePet, update["sCmd"]!.GetValue<string>());
        Assert.Equal(0, update["nError"]!.GetValue<int>());
        var info = Assert.IsType<JsonArray>(update["tbPetInfo"])[0]!;

        var reply = LastReply(h, CafePetFeature.RspPetGift);
        Assert.Equal(new[] { 22, 1, 1, 1, 2 },
                     Assert.IsType<JsonArray>(reply["tbItem"]).Select(n => n!.GetValue<int>()));

        // 2 × 30 = 60 点；1 级需 10、2 级需 30、3 级需 60 ⇒ 60 - 10 - 30 = 20，停在 3 级
        Assert.Equal(3u, info["lv"]!.GetValue<uint>());
        Assert.Equal(20u, info["exp"]!.GetValue<uint>());
        Assert.Equal(3u, CafePetState.Read(h.Player).Pet(1)!.Lv);
        Assert.Equal(3u, h.Player.Items.Single(i => i.Genre == ToyG).Count);   // 5 - 2
        Assert.Equal(ToyExp, 30);
    }

    /// <summary>送清洁用品（detail 2）加精力，上限 100（<c>UI_PetList.lua:703</c>）。</summary>
    [Fact]
    public async Task PetGift_Clean_AddsHot_CappedAt100()
    {
        using var h = await FeatureHarness.CreateAsync("pet_gift_clean");
        Seed(h.Player, s => s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 }));
        h.Player.AddItem(CleanG, CleanD, CleanP, CleanL, 20);

        await SendAsync(h, CafePetFeature.CmdPetGift,
            ("uid", 1), ("tbItem", new JsonArray { (int)CleanG, (int)CleanD, (int)CleanP, (int)CleanL, 3 }));
        Assert.Equal((uint)(CleanHot * 3), CafePetState.Read(h.Player).Pet(1)!.Hot);   // 60

        await SendAsync(h, CafePetFeature.CmdPetGift,
            ("uid", 1), ("tbItem", new JsonArray { (int)CleanG, (int)CleanD, (int)CleanP, (int)CleanL, 5 }));
        Assert.Equal((uint)CafeTable.MaxPetHot, CafePetState.Read(h.Player).Pet(1)!.Hot);   // 封顶 100
    }

    /// <summary>送礼的四类非法上行都不回包、不扣道具、不抛。</summary>
    [Fact]
    public async Task PetGift_InvalidRequests_AreIgnored()
    {
        using var h = await FeatureHarness.CreateAsync("pet_gift_bad");
        Seed(h.Player, s => s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 }));
        h.Player.AddItem(ToyG, ToyD, ToyP, ToyL, 1);

        await SendAsync(h, CafePetFeature.CmdPetGift, ("uid", 99),
            ("tbItem", new JsonArray { 22, 1, 1, 1, 1 }));                        // 不是自己的宠物
        await SendAsync(h, CafePetFeature.CmdPetGift, ("uid", 1), ("tbItem", new JsonArray { 22, 1 }));  // 元组不足
        await SendAsync(h, CafePetFeature.CmdPetGift, ("uid", 1));                // 完全没 tbItem
        await SendAsync(h, CafePetFeature.CmdPetGift, ("uid", 1),
            ("tbItem", new JsonArray { 5, 1, 1, 2, 1 }));                         // genre 不是 22
        await SendAsync(h, CafePetFeature.CmdPetGift, ("uid", 1),
            ("tbItem", new JsonArray { 22, 1, 1, 1, 99 }));                       // 数量不足

        Assert.Empty(h.Outbound);
        Assert.Equal(1u, h.Player.Items.Single(i => i.Genre == ToyG).Count);
        Assert.Equal(1u, CafePetState.Read(h.Player).Pet(1)!.Lv);
    }

    /// <summary>好感升级用「当级内经验」口径（<c>UI_PetInfo.lua:261</c> 直接显示 <c>loveExp/needExp</c>）。</summary>
    [Fact]
    public void AddLoveExp_UsesWithinLevelExp_AndStopsAtRareCap()
    {
        var feature = NewFeature();

        var pet = new CafePet { Uid = 1, TemplateId = 101, Lv = 1, Exp = 0 };   // Rare 1 ⇒ 上限 30 级
        Assert.Equal((1, 9), feature.AddLoveExp(pet, 9));           // 1 级需 10，不够
        Assert.Equal((2, 0), feature.AddLoveExp(pet, 10));          // 正好升级，当级经验归 0
        Assert.Equal((3, 20), feature.AddLoveExp(pet, 60));         // 10 + 30 = 40，余 20

        pet.Lv = 30;
        pet.Exp = 0;
        Assert.Equal(30, feature.AddLoveExp(pet, 999_999).Lv);      // 到 Rare 1 的上限就不再升
    }

    /// <summary>改名：先推 <c>UpdatePet</c> 再回 <c>PetRename</c>（<c>PetRenameRsp</c> 会重开读本地缓存的列表）。</summary>
    [Fact]
    public async Task PetRename_PushesUpdatePetThenReplies()
    {
        using var h = await FeatureHarness.CreateAsync("pet_rename");
        Seed(h.Player, s => s.Pets.Add(new CafePet
        {
            Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1, Name = "旧名",
        }));

        await SendAsync(h, CafePetFeature.CmdPetRename, ("uid", 1), ("name", "新名"));

        Assert.Equal(2, h.Outbound.Count);
        var update = Assert.IsType<JsonObject>(JsonNode.Parse(h.Outbound[0].Json));
        Assert.Equal(CafePetFeature.RspUpdatePet, update["sCmd"]!.GetValue<string>());
        Assert.Equal("新名", Assert.IsType<JsonArray>(update["tbPetInfo"])[0]!["name"]!.GetValue<string>());

        Assert.Equal("新名", LastReply(h, CafePetFeature.RspPetRename)["name"]!.GetValue<string>());
        Assert.Equal("新名", CafePetState.Read(h.Player).Pet(1)!.Name);
    }

    /// <summary>
    /// 放生已领养的宠物：下行 sCmd = <b><c>GiveUpPetOwned</c></b>（上行是 <c>DelPet</c>），
    /// <c>uid</c> 必填（<c>:978..981</c>），正被指派时还要带 <c>nIndex</c>（<c>:966..968</c>），
    /// 并按好感等级返还 <c>GiveBack.txt</c> 的物品。
    /// </summary>
    [Fact]
    public async Task DelPet_ReplyHasUidAndNIndex_AndGrantsGiveBack()
    {
        using var h = await FeatureHarness.CreateAsync("pet_del");
        Seed(h.Player, s =>
        {
            s.Pets.Add(new CafePet { Uid = 4, TemplateId = 101, Gender = 1, Nature = 1, Lv = 5 });
            s.InCafe[2] = 4;      // 指派在位置 3
        });

        await SendAsync(h, CafePetFeature.CmdDelPet, ("uid", 4));

        var reply = LastReply(h, CafePetFeature.RspGiveUpPetOwned);
        Assert.Equal(4, reply["uid"]!.GetValue<int>());
        Assert.Equal(3, reply["nIndex"]!.GetValue<int>());

        var st = CafePetState.Read(h.Player);
        Assert.Empty(st.Pets);
        Assert.Equal(0, st.PetsInCafeCount);
        // 5 级档 = [22-1-1-1-2] ⇒ 2 个宠物玩具Ⅰ
        Assert.Equal(2u, h.Player.Items.Single(i => i.Genre == ToyG && i.Detail == ToyD).Count);
    }

    /// <summary>没被指派的宠物放生时**不带** <c>nIndex</c>（条件必填）。</summary>
    [Fact]
    public async Task DelPet_UnassignedPet_OmitsNIndex()
    {
        using var h = await FeatureHarness.CreateAsync("pet_del2");
        Seed(h.Player, s => s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 }));

        await SendAsync(h, CafePetFeature.CmdDelPet, ("uid", 1));

        var reply = LastReply(h, CafePetFeature.RspGiveUpPetOwned);
        Assert.Equal(1, reply["uid"]!.GetValue<int>());
        Assert.Null(reply["nIndex"]);
    }

    /// <summary>放生不存在的宠物 → 不回包不抛。</summary>
    [Fact]
    public async Task DelPet_UnknownUid_IsIgnored()
    {
        using var h = await FeatureHarness.CreateAsync("pet_del_bad");

        await SendAsync(h, CafePetFeature.CmdDelPet, ("uid", 123));
        await SendAsync(h, CafePetFeature.CmdDelPet);

        Assert.Empty(h.Outbound);
    }

    // =================================================================
    //  8. BuyCafeFood + 食物点数结算
    // =================================================================

    /// <summary>
    /// 买咖啡馆食物：扣 <c>BuffFood.PriceGet</c>，往 <c>(49, 101/102)</c> 加
    /// <c>AddPoint × 12</c> 秒，回包 sCmd 用 <b><c>GetCafeFoodNum</c></b> ——
    /// <c>BuyCafeFoodRsp</c>（<c>:556..561</c>）虽然存在但函数体是空的，
    /// 真正会刷 <c>UI_PetAddBuff</c>/<c>UI_PetMain</c>/<c>UI_NCafe</c> 的是 <c>GetCafeFoodNumRsp</c>。
    /// </summary>
    [Fact]
    public async Task BuyCafeFood_ChargesAndAddsSeconds_RepliesGetCafeFoodNum()
    {
        using var h = await FeatureHarness.CreateAsync("pet_buyfood");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 1000);

        await SendAsync(h, CafePetFeature.CmdBuyCafeFood, ("nFoodId", 1), ("nNum", 1));

        LastReply(h, CafePetFeature.RspGetCafeFoodNum);
        Assert.Equal(500, h.Player.GetMoney(MoneyType.CoffeeCoin));          // BuffFood 1 = [7,500]

        // 食物 1 的 AddPoint = 200 ⇒ 200 * 12 = 2400 秒，存在槽位 1（= 食物 id）
        Assert.Equal(1, h.Player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(1)));
        Assert.Equal(2400, h.Player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(1)));
        Assert.Equal(200, CafePetFeature.TotalFoodSeconds(h.Player) / CafePetFeature.SecondsPerFoodPoint);
    }

    /// <summary>钱不够 / 食物 id 不存在 → 不回包、不扣钱。</summary>
    [Fact]
    public async Task BuyCafeFood_RejectsUnknownFoodAndPoorPlayer()
    {
        using var h = await FeatureHarness.CreateAsync("pet_buyfood_bad");

        await SendAsync(h, CafePetFeature.CmdBuyCafeFood, ("nFoodId", 99), ("nNum", 1));
        await SendAsync(h, CafePetFeature.CmdBuyCafeFood, ("nFoodId", 1), ("nNum", 1));   // 咖啡币 0
        await SendAsync(h, CafePetFeature.CmdBuyCafeFood);

        Assert.Empty(h.Outbound);
        Assert.Equal(0, CafePetFeature.TotalFoodSeconds(h.Player));
    }

    /// <summary>点数封顶 10000 点（<c>NCafePetLogic.lua:11</c>）—— 满了就不扣钱也不加点。</summary>
    [Fact]
    public async Task BuyCafeFood_StopsAtMaxFoodPoint()
    {
        using var h = await FeatureHarness.CreateAsync("pet_buyfood_cap");
        h.Player.SetMoney(MoneyType.FreeDiamond, 999);
        h.Player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(4), 4);
        h.Player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(4),
                         CafePetFeature.MaxCafeFoodPoint * CafePetFeature.SecondsPerFoodPoint);

        await SendAsync(h, CafePetFeature.CmdBuyCafeFood, ("nFoodId", 4), ("nNum", 1));

        LastReply(h, CafePetFeature.RspGetCafeFoodNum);
        Assert.Equal(999, h.Player.GetMoney(MoneyType.FreeDiamond));         // 一分没扣
        Assert.Equal(CafePetFeature.MaxCafeFoodPoint,
                     CafePetFeature.TotalFoodSeconds(h.Player) / CafePetFeature.SecondsPerFoodPoint);
    }

    /// <summary>
    /// 结算规则来自客户端 <c>GetFoodBuffTime</c> 的三个分支（<c>:659..665</c>）：
    /// <b>只有店内有宠物时食物才被消耗</b>。店里没宠物 ⇒ 一秒都不能扣，
    /// 否则玩家离开咖啡馆期间食物白白蒸发。
    /// </summary>
    [Fact]
    public void SettleFoodPoints_OnlyConsumesWhenPetsAreInCafe()
    {
        var feature = NewFeature();

        var player = new Player("acc", 1);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(1), 1);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(1), 1000);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.TaskLastCalTime, 1_700_000_000);

        // ① 店内无宠物：不扣，但 lastCalTime 仍推进
        var empty = CafePetState.Read(player);
        feature.SettleFoodPoints(player, empty, 1_700_000_600);
        Assert.Equal(1000, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(1)));
        Assert.Equal(1_700_000_600, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.TaskLastCalTime));

        // ② 店内有宠物：按 now - lastCalTime 扣
        var withPet = Seed(player, s =>
        {
            s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 });
            s.InCafe[0] = 1;
        });
        feature.SettleFoodPoints(player, withPet, 1_700_000_900);
        Assert.Equal(700, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(1)));
    }

    /// <summary>
    /// 扣完的槽位要把「食物 id」也清 0，否则客户端 <c>GetCurBuffState</c>（<c>:623..632</c>）
    /// 会继续把它当「当前生效 buff」上报（<c>resTime</c> 是 0，界面显示 0h0min 的鬼影 buff）。
    /// 多槽位时按 <c>Rarity</c> 从高到低消耗，与 <c>GetCurBuffState</c> 取最大 Rarity 一致。
    /// </summary>
    [Fact]
    public void SettleFoodPoints_DrainsHighestRarityFirst_AndClearsEmptySlotId()
    {
        var feature = NewFeature();

        var player = new Player("acc", 1);
        // 槽 1 = 食物 1（Rarity 1）100 秒；槽 4 = 食物 4（Rarity 4）50 秒
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(1), 1);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(1), 100);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(4), 4);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(4), 50);
        player.SetTask(CafePetFeature.TaskGroup, CafePetFeature.TaskLastCalTime, 1_700_000_000);

        var state = Seed(player, s =>
        {
            s.Pets.Add(new CafePet { Uid = 1, TemplateId = 101, Gender = 1, Nature = 1, Lv = 1 });
            s.InCafe[0] = 1;
        });
        feature.SettleFoodPoints(player, state, 1_700_000_060);   // 过去 60 秒

        // Rarity 4 的 50 秒先被吃光（并清掉 id），剩下 10 秒从 Rarity 1 扣
        Assert.Equal(0, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(4)));
        Assert.Equal(0, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(4)));
        Assert.Equal(90, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotTimeTask(1)));
        Assert.Equal(1, player.GetTask(CafePetFeature.TaskGroup, CafePetFeature.FoodSlotIdTask(1)));
    }

    // =================================================================
    //  9. 畸形上行 / 未实现命令
    // =================================================================

    /// <summary>
    /// 缺 sCmd、非对象、未知 sCmd、已取证但未实现的 sCmd —— 全部只记日志，
    /// **不回包**（回一个拼不出 <c>*Rsp</c> 的 sCmd 会让客户端 nil 调用）、不抛。
    /// </summary>
    [Fact]
    public async Task MalformedAndUnimplementedCommands_DoNotReplyOrThrow()
    {
        using var h = await FeatureHarness.CreateAsync("pet_malformed");

        await h.DispatchFunctionAsync(CafePetFeature.Function, "{}");            // 缺 sCmd
        await h.DispatchFunctionAsync(CafePetFeature.Function, "[]");            // 不是对象
        await h.DispatchFunctionAsync(CafePetFeature.Function, "null");
        await h.DispatchFunctionAsync(CafePetFeature.Function, "123");
        await h.DispatchFunctionAsync(CafePetFeature.Function, """{"sCmd":""}""");
        await h.DispatchFunctionAsync(CafePetFeature.Function, """{"sCmd":"NoSuchCommand"}""");
        foreach (string sCmd in CafePetFeature.KnownUnimplemented.Keys)
            await SendAsync(h, sCmd);

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// 「已取证但未实现」清单本身也钉住：<c>OnBuyBoxFood</c> 是死代码
    /// （唯一调用点 <c>UI_PetFood.lua:21</c> 被注释掉了）、
    /// <c>GetPetMissionAward</c> 跨域、<c>GetPawAward</c> 属图鉴子系统且全仓无 Rsp。
    ///
    /// ★ 远征四条（<c>UnLockTourArea</c>/<c>SetTourPets</c>/<c>CheckTourIsSuccess</c>/
    /// <c>CompleteTour</c>）**第十八轮已实现**，从本清单移出 —— 见 <see cref="CafePetTourTests"/>。
    /// </summary>
    [Fact]
    public void KnownUnimplemented_ListsTheThreeDeferredCommands()
    {
        Assert.Equal(
            new[] { "GetPawAward", "GetPetMissionAward", "OnBuyBoxFood" },
            CafePetFeature.KnownUnimplemented.Keys.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Contains("死代码", CafePetFeature.KnownUnimplemented["OnBuyBoxFood"]);
    }

    /// <summary>未登录（<c>session.Tag</c> 非 Player）时只记日志，不回包也不抛。</summary>
    [Fact]
    public async Task WithoutLogin_DoesNotReplyAndDoesNotThrow()
    {
        using var h = await FeatureHarness.CreateAsync("pet_nologin", attachPlayer: false);

        await SendAsync(h, CafePetFeature.CmdGetCafeFoodNum);
        await SendAsync(h, CafePetFeature.CmdUpdateFoodBoxs);
        await SendAsync(h, CafePetFeature.CmdOnNewPet, ("nBoxId", 1), ("name", "x"));

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// 两套键名都要认：<c>SetFood</c>/<c>OnFoodBoxOpen</c>/<c>OnBoxFoodDone</c>/<c>OnNewPet</c>
    /// 发的是 <c>nBoxId</c>，<c>OnGiveupPet</c>/<c>OnGetGiveupItem</c> 发的是 <c>boxId</c>。
    /// </summary>
    [Fact]
    public void ReadBoxId_AcceptsBothNamings()
    {
        Assert.Equal(2, CafePetFeature.ReadBoxId(new JsonObject { ["nBoxId"] = 2 }));
        Assert.Equal(3, CafePetFeature.ReadBoxId(new JsonObject { ["boxId"] = 3 }));
        Assert.Equal(0, CafePetFeature.ReadBoxId(new JsonObject()));
    }

    // =================================================================
    //  10. 存档
    // =================================================================

    /// <summary>
    /// <c>Player.Extra["cafepet"]</c> 随存档 round-trip；
    /// 键名与协议同构（<c>uid/templateid/exp/hot/name/...</c> 就是 proto <c>gsproto.CafePet</c>）。
    /// </summary>
    [Fact]
    public void State_RoundTripsThroughPlayerStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sssj-cafepet-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlayerStore(new ServerOptions { DataDirectory = dir });
            var player = new Player("cafepet_acc", 777);
            Seed(player, s =>
            {
                s.Box(1)!.Unlocked = true;
                s.Box(2)!.Unlocked = true;
                s.Box(2)!.Type = 2;
                s.Box(2)!.Time = 1_800_000_000;
                s.Box(3)!.Unlocked = true;
                s.Box(3)!.Pet = new CafePet { Uid = 9, TemplateId = 303, Gender = 2, Nature = 4, Iq = 250, Lv = 1 };
                s.Pets.Add(new CafePet
                {
                    Uid = 1, TemplateId = 102, Exp = 12, Hot = 34, Name = "阿花",
                    BirthDay = 1_600_000_000, MeetDay = 1_650_000_000,
                    Hobby = 2, Iq = 99, Nature = 3, Gender = 2, Lv = 7,
                });
                s.NextUid = 2;
                s.InCafe[3] = 1;
            });
            store.Save(player);

            var loaded = store.Load("cafepet_acc");
            Assert.NotNull(loaded);
            var st = CafePetState.Read(loaded!);

            Assert.Equal(3, st.UnlockedCount);
            Assert.Equal(2, st.Box(2)!.Type);
            Assert.Equal(1_800_000_000L, st.Box(2)!.Time);
            Assert.Equal(303u, st.Box(3)!.Pet!.TemplateId);

            var pet = st.Pets.Single();
            Assert.Equal((1u, 102u, 12u, 34u, "阿花"), (pet.Uid, pet.TemplateId, pet.Exp, pet.Hot, pet.Name));
            Assert.Equal((2u, 99u, 3u, 2u, 7u), (pet.Hobby, pet.Iq, pet.Nature, pet.Gender, pet.Lv));
            Assert.Equal(2, st.NextUid);
            Assert.Equal(1, st.PetsInCafeCount);
            Assert.Equal(1, st.InCafe[3]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 存档里的 <c>nextuid</c> 落后于实际宠物时自愈 —— 否则会发出重复 uid，
    /// 客户端 <c>PetInfoDic</c> 是按 uid 的字典（<c>dump.cs:532873</c>），撞车 = 覆盖掉一只宠物。
    /// </summary>
    [Fact]
    public void State_HealsStaleNextUid()
    {
        var player = new Player("acc", 1);
        player.Extra[CafePetFeature.ExtraKey] = new JsonObject
        {
            ["pets"] = new JsonArray
            {
                new JsonObject { ["uid"] = 5, ["templateid"] = 101, ["lv"] = 1, ["gender"] = 1 },
            },
            ["nextuid"] = 1,
        };

        Assert.Equal(6, CafePetState.Read(player).NextUid);
    }

    /// <summary>新号：三个食盆全锁、无宠物、<c>nextpetid = 1</c>（⇒ 客户端 <c>petMaxNum = 0</c>）。</summary>
    [Fact]
    public void State_FreshPlayer_AllBoxesLocked()
    {
        var st = CafePetState.Read(new Player("acc", 1));

        Assert.Equal(CafePetFeature.FoodBoxCount, st.Boxes.Count);
        Assert.All(st.Boxes, b => Assert.False(b.Unlocked));
        Assert.Empty(st.Pets);
        Assert.Equal(CafeFeature.FirstPetUid, st.NextUid);
        Assert.Equal(0, st.PetsInCafeCount);
        Assert.Empty(CafePetFeature.BuildBoxStateList(st));
        Assert.Empty(CafePetFeature.BuildPetStateList(st));
    }

    /// <summary>
    /// <c>CafePet.ToJson</c> 的键必须恰好是 proto <c>gsproto.CafePet</c> 的字段 1..12。
    /// proto 里还有 <c>ext1..ext5</c>，客户端从不读 ⇒ 按「没有证据不编」的规矩不下发。
    /// </summary>
    [Fact]
    public void CafePetJson_KeysMatchProtoCafePet()
    {
        var json = new CafePet
        {
            Uid = 1, TemplateId = 101, Exp = 2, Hot = 3, Name = "n",
            BirthDay = 4, MeetDay = 5, Hobby = 6, Iq = 7, Nature = 8, Gender = 1, Lv = 9,
        }.ToJson();

        Assert.Equal(
            """{"uid":1,"templateid":101,"exp":2,"hot":3,"name":"n","birthday":4,"meetday":5,"hobby":6,"iq":7,"nature":8,"gender":1,"lv":9}""",
            json.ToJsonString());
        Assert.Equal(12, json.Count);
    }

    // =================================================================
    //  11. 与 LuaCall 112 的衔接（CafeFeature 侧）
    // =================================================================

    /// <summary>
    /// 进店的 LuaCall 112 要把**真实**食盆/宠物数据填进
    /// <c>boxstatelist</c>/<c>petstatelist</c>/<c>nextpetid</c>，
    /// 并紧接着推一条 <c>UpdatePet</c>（否则 <c>OnCafeItemLoaded</c> 拿 uid 去
    /// <c>PetController:InitPet</c> 时客户端缓存里没有 <c>NPetInfo</c>，摆不出宠物模型）。
    /// </summary>
    [Fact]
    public async Task LuaCall112_CarriesRealPetDataAndPushesUpdatePet()
    {
        using var h = await FeatureHarness.CreateAsync("pet_enter");
        Seed(h.Player, s =>
        {
            s.Box(1)!.Unlocked = true;
            s.Box(1)!.Type = 2;
            s.Box(1)!.Time = 1_900_000_000;
            s.Pets.Add(new CafePet
            {
                Uid = 1, TemplateId = 202, Gender = 2, Nature = 1, Lv = 3, Name = "咪咪",
            });
            s.NextUid = 2;
            s.InCafe[0] = 1;
        });

        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "[]");

        // 第 1 条：LuaCall 112 的回包
        Assert.Equal(2, h.Outbound.Count);
        Assert.Equal("LuaCall", h.Outbound[0].Function);
        var tb = Assert.IsType<JsonObject>(JsonNode.Parse(h.Outbound[0].Json)!["tbParam"]);
        var boxes = Assert.IsType<JsonArray>(tb["boxstatelist"]);
        Assert.Single(boxes);                                                 // 只发已解锁的那一格
        Assert.Equal(1, boxes[0]!["uid"]!.GetValue<int>());
        Assert.Equal(2, boxes[0]!["type"]!.GetValue<int>());
        Assert.Equal(6, Assert.IsType<JsonArray>(tb["petstatelist"]).Count);  // 稠密：6 个指派位
        Assert.Equal(1, Assert.IsType<JsonArray>(tb["petstatelist"])[0]!["uid"]!.GetValue<int>());
        Assert.Equal(2, tb["nextpetid"]!.GetValue<int>());

        // 第 2 条：UpdatePet 推送
        var (fn, json) = h.LastOutbound;
        Assert.Equal(CafePetFeature.Function, fn);
        var push = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(CafePetFeature.RspUpdatePet, push["sCmd"]!.GetValue<string>());
        Assert.Equal(0, push["nError"]!.GetValue<int>());
        Assert.Equal("咪咪", Assert.IsType<JsonArray>(push["tbPetInfo"])[0]!["name"]!.GetValue<string>());
    }

    /// <summary>没有宠物时不发多余的 <c>UpdatePet</c>（新号只有 112 一条回包）。</summary>
    [Fact]
    public async Task LuaCall112_FreshPlayer_SendsOnlyCafeData()
    {
        using var h = await FeatureHarness.CreateAsync("pet_enter_fresh");

        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "[]");

        var (function, json) = Assert.Single(h.Outbound);
        Assert.Equal("LuaCall", function);
        var tb = Assert.IsType<JsonObject>(JsonNode.Parse(json)!["tbParam"]);
        Assert.Empty(Assert.IsType<JsonArray>(tb["boxstatelist"]));
        Assert.Empty(Assert.IsType<JsonArray>(tb["petstatelist"]));
        Assert.Equal(1, tb["nextpetid"]!.GetValue<int>());
    }

    // =================================================================
    //  12. 完整链路回归
    // =================================================================

    /// <summary>
    /// 一条真机会走的完整序列，按客户端的实际发包顺序串起来：
    /// <code>
    /// 进店 LuaCall 112 → UpdateFoodBoxs（OnCafeItemLoaded）→ GetCafeFoodNum（OnEnterCafe）
    ///   → OnFoodBoxOpen（点未解锁食盆，免费）
    ///   → SetFood（UI_PetFood 选罐头）
    ///   → OnBoxFoodDone（UI_PetFinishFast 加速）
    ///   → OnNewPet（UI_PetMeet 领养）
    ///   → ChangePetCafeState（AdoptPetRsp 自动补发的那条）
    ///   → PetGift（UI_PetList 送礼）
    ///   → DelPet（放生）
    /// </code>
    /// 全程不抛异常，每一步的下行 sCmd 都在白名单内、都带 <c>nError = 0</c>，
    /// 且状态一路正确演进 —— 这条是本域最有价值的回归网。
    /// </summary>
    [Fact]
    public async Task FullHappyPath_FromEnteringCafeToReleasingPet()
    {
        using var h = await FeatureHarness.CreateAsync("pet_e2e");
        h.Player.AddItem(Food1G, Food1D, Food1P, Food1L, 1);
        h.Player.AddItem(ToyG, ToyD, ToyP, ToyL, 4);
        h.Player.SetMoney(MoneyType.FreeDiamond, 100);

        // ① 进店
        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "[]");
        await SendAsync(h, CafePetFeature.CmdUpdateFoodBoxs);
        LastReply(h, CafePetFeature.RspUpdateFoodBoxs);
        await SendAsync(h, CafePetFeature.CmdGetCafeFoodNum);
        LastReply(h, CafePetFeature.RspGetCafeFoodNum);

        // ② 解锁第一个食盆（免费）
        await SendAsync(h, CafePetFeature.CmdOnFoodBoxOpen, ("nBoxId", 1));
        LastReply(h, CafePetFeature.RspUpdateFoodBoxs);
        Assert.Equal(100, h.Player.GetMoney(MoneyType.FreeDiamond));

        // ③ 投放罐头
        await SendAsync(h, CafePetFeature.CmdSetFood, ("nBoxId", 1), ("nId", Food1));
        LastReply(h, CafePetFeature.RspSetFood);
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == Food1G);

        // ④ 加速完成（8h → 8*6+0+1 = 49 钻）
        await SendAsync(h, CafePetFeature.CmdOnBoxFoodDone, ("nBoxId", 1));
        LastReply(h, CafePetFeature.RspSetFood);
        Assert.Equal(51, h.Player.GetMoney(MoneyType.FreeDiamond));
        var attracted = CafePetState.Read(h.Player).Box(1)!.Pet;
        Assert.NotNull(attracted);

        // ⑤ 领养
        await SendAsync(h, CafePetFeature.CmdOnNewPet, ("nBoxId", 1), ("name", "团子"));
        var adopt = LastReply(h, CafePetFeature.RspAdoptPet);
        Assert.Equal(1, adopt["petInfo"]!["uid"]!.GetValue<int>());
        Assert.Equal(attracted!.TemplateId, adopt["petInfo"]!["templateid"]!.GetValue<uint>());

        // ⑥ 客户端 AdoptPetRsp 自动补发的指派
        await SendAsync(h, CafePetFeature.CmdChangePetCafeState, ("nIndex", 1), ("uid", 1));
        LastReply(h, CafePetFeature.RspChangePetCafeState);
        Assert.Equal(1, CafePetState.Read(h.Player).PetsInCafeCount);

        // ⑦ 送 4 个玩具（4 × 30 = 120 点 ⇒ 10/30/60 三级消耗完，停在 4 级余 20）
        await SendAsync(h, CafePetFeature.CmdPetGift,
            ("uid", 1), ("tbItem", new JsonArray { (int)ToyG, (int)ToyD, (int)ToyP, (int)ToyL, 4 }));
        LastReply(h, CafePetFeature.RspPetGift);
        var pet = CafePetState.Read(h.Player).Pet(1)!;
        Assert.Equal(4u, pet.Lv);
        Assert.Equal(20u, pet.Exp);

        // ⑧ 放生：4 级档返还 [22-1-1-1-2]，并清掉指派位
        await SendAsync(h, CafePetFeature.CmdDelPet, ("uid", 1));
        var del = LastReply(h, CafePetFeature.RspGiveUpPetOwned);
        Assert.Equal(1, del["nIndex"]!.GetValue<int>());
        var st = CafePetState.Read(h.Player);
        Assert.Empty(st.Pets);
        Assert.Equal(0, st.PetsInCafeCount);
        Assert.Equal(2, st.NextUid);          // uid 不回收（客户端 PetInfoDic 按 uid 索引）
        Assert.Equal(2u, h.Player.Items.Single(i => i.Genre == ToyG && i.Detail == ToyD).Count);
    }

    // =================================================================
    //  工具
    // =================================================================

    /// <summary>
    /// 造一个只带配置表的 feature 实例（走 <c>internal CafePetFeature(CafeTable)</c>）。
    /// 本文件里几条纯算法测试（<see cref="CafePetFeature.AddLoveExp"/> /
    /// <see cref="CafePetFeature.SettleFoodPoints"/>）只需要配置表就位，
    /// 不必凑一个 <c>GameFeatureContext</c>。
    /// </summary>
    private static CafePetFeature NewFeature() => new(CafeTable.Load(TestPaths.SettingDir));
}
