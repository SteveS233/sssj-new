using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 任务 / 成就系统（<c>MissionMgrMsg</c> 族 + LuaCall 200 <c>MissionNotify</c>）。
///
/// 断言的核心取证（每条都能追到 文件:行号）：
/// <list type="number">
/// <item>上行有 7 个名字（<c>MissionManager:SendToServer</c> 的全部调用点），
///   下行只有 <c>MissionMgrMsg</c> 一个（S2CCall.lua:85）。</item>
/// <item><c>MissionManager:Dispatcher</c>（MissionManager.lua:657..899）只解引用
///   <c>nError</c> / <c>nMission</c> / <c>tbIdList</c> / <c>tbItems</c> 四个字段。</item>
/// <item>单领**不能**带 <c>tbIdList</c>：MissionManager.lua:734..740 的
///   <c>if not tbList then … else ui.HaveGetMission = false end</c> 让「空数组」与「不给」语义相反。</item>
/// <item>任务值位域：进度 = <c>LoadBits(v,1,31)</c>、已领 = <c>LoadBits(v,0,0)</c>
///   （MissionManager.lua:597/:604 + Lib.lua:519）。</item>
/// </list>
/// </summary>
public class MissionFeatureTests
{
    // ============ 命令名 ============

    /// <summary>
    /// 上行 7 个名字来自 <c>MissionManager:SendToServer</c> 的全部调用点：
    /// UI_Achievement.lua:550/553/555/559/561/864、UI_Guidancetask.lua:365/367、
    /// UI_ClubTask.lua:292/297、UI_AchieveReward.lua:12、GirlMissionManager.lua:386。
    /// </summary>
    [Fact]
    public void UpFunctions_AreTheSevenNamesFromLua()
    {
        Assert.Equal(new[]
        {
            "AchievementGetAward",
            "MissionGetAward",
            "MissionActiveAward",
            "GuideMissionGetAward",
            "GuideProgressGetAward",
            "ClubMissionGetAward",
            "ClubProgressGetAward",
        }, MissionFeature.UpFunctions);
    }

    /// <summary>
    /// 下行只有 <c>S2C.MissionMgrMsg</c>（S2CCall.lua:85），且与任何上行名都不同 ——
    /// 名字错了客户端只打一行 <c>S2C.ProcCmd error</c> 就静默丢包（S2CCall.lua:11..12）。
    /// </summary>
    [Fact]
    public void DownFunction_IsMissionMgrMsg_AndDiffersFromEveryUpName()
    {
        Assert.Equal("MissionMgrMsg", MissionFeature.DownFunction);
        Assert.DoesNotContain(MissionFeature.DownFunction, MissionFeature.UpFunctions);
    }

    /// <summary>nMission 编号对齐 MissionManager.lua:656 的注释。</summary>
    [Fact]
    public void MissionCodes_MatchDispatcherComment()
    {
        Assert.Equal(0, MissionFeature.MissionAchieve);
        Assert.Equal(1, MissionFeature.MissionDaily);
        Assert.Equal(2, MissionFeature.MissionActivePoint);
        Assert.Equal(3, MissionFeature.MissionGuide);
        Assert.Equal(4, MissionFeature.MissionGuideProgress);
        Assert.Equal(5, MissionFeature.MissionClub);
        Assert.Equal(6, MissionFeature.MissionClubProgress);
    }

    /// <summary>固定 taskId 与 group 常量对齐 MissionManager.lua:6..16 / LuaCall.lua:172。</summary>
    [Fact]
    public void TaskIdConstants_MatchMissionManagerLua()
    {
        Assert.Equal(5, MissionTable.MissionGroup);           // MissionManager.lua:6
        Assert.Equal(6, MissionTable.NormalGroup);            // MissionManager.lua:15
        Assert.Equal(18, MissionTable.OtherGroup);            // MissionManager.lua:16
        Assert.Equal(20000, MissionFeature.GirlTaskId);       // :7
        Assert.Equal(20001, MissionFeature.GirlEventTaskId);  // :8
        Assert.Equal(30000, MissionFeature.ActivePointTaskId);         // :10
        Assert.Equal(30001, MissionFeature.PointAwardTaskId);          // :11
        Assert.Equal(41001, MissionFeature.GuideCompleteAwardTaskId);  // :12
        Assert.Equal(51001, MissionFeature.ClubCompleteAwardTaskId);   // :13
        Assert.Equal(200, MissionFeature.MissionNotifyCmd);            // LuaCall.lua:172
    }

    // ============ 任务值位域 ============

    /// <summary>
    /// <c>GetTaskPro = LoadBits(v,1,31)</c>（MissionManager.lua:597）、
    /// <c>GetTaskAward = LoadBits(v,0,0)</c>（:604），组合方向必须能来回还原。
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]      // 只有已领标记、进度 0
    [InlineData(2, 1, 0)]      // 进度 1、未领
    [InlineData(3, 1, 1)]      // 进度 1、已领
    [InlineData(11, 5, 1)]     // 进度 5、已领
    [InlineData(200, 100, 0)]  // 进度 100、未领
    public void TaskValueBits_RoundTrip(int taskValue, int progress, int claimed)
    {
        Assert.Equal(progress, MissionFeature.ProgressOf(taskValue));
        Assert.Equal(claimed, MissionFeature.ClaimedOf(taskValue));
        Assert.Equal(taskValue, MissionFeature.ComposeTaskValue(progress, claimed));
    }

    /// <summary>负值/溢出被夹住：客户端 <c>LoadBits</c> 用 <c>%</c>/<c>/</c> 模拟位运算，负数会算错。</summary>
    [Fact]
    public void ComposeTaskValue_ClampsNegativeAndOverflow()
    {
        Assert.Equal(0, MissionFeature.ComposeTaskValue(-5, 0));
        Assert.Equal(1, MissionFeature.ComposeTaskValue(-5, 1));
        Assert.True(MissionFeature.ComposeTaskValue(int.MaxValue, 1) > 0);   // 不能溢出成负数
        Assert.Equal(0, MissionFeature.ProgressOf(-1));
        Assert.Equal(0, MissionFeature.ClaimedOf(-1));
    }

    /// <summary>组合键就是 <c>(group &lt;&lt; 16) | taskId</c>（Game.Player.GetTask RVA 0x18448F0）。</summary>
    [Fact]
    public void ComposeTaskId_MatchesClientKeyLayout()
    {
        Assert.Equal(((uint)5 << 16) | 40001, Player.ComposeTaskId(5, 40001));
        Assert.Equal(5, Player.TaskGroupOf(Player.ComposeTaskId(5, 40001)));
        Assert.Equal(40001, Player.TaskIdOf(Player.ComposeTaskId(5, 40001)));
    }

    // ============ 回包字段 ============

    /// <summary>
    /// 最小合法回包只有两个字段。<c>nError</c> 必填 —— MissionManager.lua:663
    /// <c>if tbParam.nError &gt; 0 then</c> 直接和 0 比大小，nil 会抛
    /// 「attempt to compare nil with number」；<c>nMission</c> 是 7 路分发依据。
    /// </summary>
    [Fact]
    public void BuildReply_MinimalPayload_HasOnlyErrorAndMission()
    {
        var obj = MissionFeature.BuildReply(MissionFeature.MissionGuide, 0, null);

        Assert.Equal(new[] { "nError", "nMission" }, obj.Select(kv => kv.Key));
        Assert.Equal(0, obj["nError"]!.GetValue<int>());
        Assert.Equal(3, obj["nMission"]!.GetValue<int>());
        Assert.Equal("""{"nError":0,"nMission":3}""", obj.ToJsonString());
    }

    /// <summary>
    /// ★ 单领时**必须省略** <c>tbIdList</c>：MissionManager.lua:734..740
    /// <c>if not tbList then tbList={}; insert(ui.nCurId) else ui.HaveGetMission=false end</c>
    /// —— 给空数组会让客户端以为「一键领了 0 条」，奖励弹窗空白且一键按钮被清掉。
    /// </summary>
    [Fact]
    public void BuildReply_SingleDailyClaim_OmitsIdList()
    {
        var obj = MissionFeature.BuildReply(MissionFeature.MissionDaily, 0, null);
        Assert.Null(obj["tbIdList"]);
        Assert.Equal("""{"nError":0,"nMission":1}""", obj.ToJsonString());
    }

    /// <summary>一键领取才带 <c>tbIdList</c>，且是扁平的 missionId 数组。</summary>
    [Fact]
    public void BuildReply_OneKeyDailyClaim_CarriesFlatIdList()
    {
        var obj = MissionFeature.BuildReply(MissionFeature.MissionDaily, 0, new[] { 101, 105 });
        var arr = Assert.IsType<JsonArray>(obj["tbIdList"]);
        Assert.Equal(new[] { 101, 105 }, arr.Select(n => n!.GetValue<int>()));
        Assert.Equal("""{"nError":0,"nMission":1,"tbIdList":[101,105]}""", obj.ToJsonString());
    }

    /// <summary>
    /// nMission==2 带 <c>tbItems</c>，且**必须为空数组**：
    /// MissionManager.lua:785 <c>if tbItems and next(tbItems)</c> 对空表等价于不给；
    /// 非空会走到 :791 <c>table.insert(tbCharacterCard, tbGDPLN)</c> —— 那里的
    /// <c>tbGDPLN</c> 是未定义局部变量（nil），是客户端 bug。
    /// </summary>
    [Fact]
    public void BuildReply_ActivePoint_CarriesEmptyItems()
    {
        var obj = MissionFeature.BuildReply(MissionFeature.MissionActivePoint, 0, null);
        Assert.Empty(Assert.IsType<JsonArray>(obj["tbItems"]));
        Assert.Equal("""{"nError":0,"nMission":2,"tbItems":[]}""", obj.ToJsonString());
    }

    /// <summary>其它 nMission 不带 tbItems（不多塞无语义字段）。</summary>
    [Fact]
    public void BuildReply_NonActivePoint_HasNoItems()
    {
        foreach (int m in new[] { 0, 1, 3, 4, 5, 6 })
            Assert.Null(MissionFeature.BuildReply(m, 0, null)["tbItems"]);
    }

    /// <summary>失败回包同样带全 <c>nError</c>/<c>nMission</c>（否则遮罩要等 2 秒超时才消）。</summary>
    [Fact]
    public void BuildReply_Failure_StillCarriesBothRequiredFields()
    {
        var obj = MissionFeature.BuildReply(MissionFeature.MissionAchieve, MissionFeature.ErrAlreadyGot, null);
        Assert.Equal(MissionFeature.ErrAlreadyGot, obj["nError"]!.GetValue<int>());
        Assert.Equal(0, obj["nMission"]!.GetValue<int>());
    }

    // ============ LuaCall 200 MissionNotify ============

    /// <summary>
    /// tbParam 三字段来自 LuaCall.lua:2827 的注释 <c>{Type = 0, Id = achieve.id,}</c>
    /// 与 <c>RemindManager:NewMissionRemind</c> 实际读到的字段（RemindManager.lua:61..78）。
    /// </summary>
    [Fact]
    public void BuildNotifyPayload_ExactWireShape()
    {
        Assert.Equal("""{"Type":0,"Id":101,"Count":1}""",
            MissionFeature.BuildNotifyPayload(MissionFeature.NotifyTypeMission, 101, 1).ToJsonString());
        Assert.Equal("""{"Type":1,"Id":7,"Count":3}""",
            MissionFeature.BuildNotifyPayload(MissionFeature.NotifyTypeAchieve, 7, 3).ToJsonString());
    }

    /// <summary>
    /// 推 MissionNotify 前必须校验 Id 在对应配置表里：
    /// <c>RemindManager:NewMissionRemind</c> 会 <c>UIText(..) .. tbConfig.desc</c>
    /// （RemindManager.lua:65），查不到时字符串连接 nil 直接抛。
    /// Type 只能是 0（DailyMission.txt）或 1（Achievement.txt，group 6）。
    /// </summary>
    [Fact]
    public void CanPushMissionNotify_OnlyKnownIdsAndTypes()
    {
        var feature = FeatureWith(MissionTable.Load(SettingDir));

        Assert.True(feature.CanPushMissionNotify(MissionFeature.NotifyTypeMission, 101));  // DailyMission.txt
        Assert.True(feature.CanPushMissionNotify(MissionFeature.NotifyTypeAchieve, 1));    // Achievement.txt
        Assert.False(feature.CanPushMissionNotify(MissionFeature.NotifyTypeMission, 424242));
        Assert.False(feature.CanPushMissionNotify(MissionFeature.NotifyTypeMission, 40001));  // 指引任务不在日常表里
        Assert.False(feature.CanPushMissionNotify(MissionFeature.NotifyTypeAchieve, 424242));
        Assert.False(feature.CanPushMissionNotify(2, 101));   // RemindManager 只认 Type 0/1
    }

    /// <summary>
    /// 下行线形：LuaCall 200 走数字命令族 —— <c>sFunction="LuaCall"</c> +
    /// <c>sJsonParam={"sCmd":200,"tbParam":{...}}</c>（AGENTS.md §2.4）。
    /// </summary>
    [Fact]
    public async Task MissionNotify_DownWireShape()
    {
        using var h = await MissionHarness.CreateAsync("mission_notify_wire");

        h.LuaCalls.CallClient(h.Session, MissionFeature.MissionNotifyCmd,
            MissionFeature.BuildNotifyPayload(MissionFeature.NotifyTypeMission, 101, 2));

        var (down, json) = Assert.Single(h.Outbound);
        Assert.Equal("LuaCall", down);
        Assert.Equal("""{"sCmd":200,"tbParam":{"Type":0,"Id":101,"Count":2}}""", json);
    }

    /// <summary>
    /// LuaCall 200 是纯下行（LuaCall.lua:2828 只有 MODULE_CLIENT 分支）：
    /// 收到上行只记 WARN、不回包，但**必须已注册**（否则 LuaCallRouter 会刷 unhandled 告警）。
    /// </summary>
    [Fact]
    public async Task MissionNotify_Upstream_IsRegisteredButSilent()
    {
        using var h = await MissionHarness.CreateAsync("mission_notify_up");

        await h.DispatchLuaCallAsync("""{"sCmd":200,"tbParam":{"Type":0,"Id":101}}""");

        Assert.Empty(h.Outbound);
    }

    // ============ 配置表解析 ============

    /// <summary>
    /// <c>[a,b,c]</c> / <c>[a-b-c]</c> 的拆法与客户端一致（先去方括号，再 Split）：
    /// MissionManager.lua:58..59（missionMax）、:63..64（ChapterLimit）。
    /// 空串 → 空列表（Lua <c>Split</c> 对空串返回 <c>{}</c>，Lib.lua:253）。
    /// </summary>
    [Fact]
    public void ParseIntList_MatchesLuaSplitSemantics()
    {
        Assert.Equal(new[] { 5 }, MissionTable.ParseIntList("[5]", ','));
        Assert.Equal(new[] { 4, 12, 1, 1 }, MissionTable.ParseIntList("[4,12,1,1]", ','));
        Assert.Equal(new[] { 2, 4, 1 }, MissionTable.ParseIntList("[2-4-1]", '-'));
        Assert.Empty(MissionTable.ParseIntList("", ','));
        Assert.Empty(MissionTable.ParseIntList("[]", ','));
    }

    /// <summary><c>awardsList</c> 的 (G,D,P,L,N) 拆法（MissionManager.lua:67..71）。</summary>
    [Fact]
    public void ParseTuples_ReadsGdplnList()
    {
        var tuples = MissionTable.ParseTuples("[15-2-1-1-20,9-16-1-1-1]");
        Assert.Equal(2, tuples.Count);
        Assert.Equal(new[] { 15, 2, 1, 1, 20 }, tuples[0]);
        Assert.Equal(new[] { 9, 16, 1, 1, 1 }, tuples[1]);
        Assert.Empty(MissionTable.ParseTuples(""));
    }

    /// <summary>
    /// 完成阈值：成就用 <c>assistparam[1]</c> 选 missionMax 的第几项，其余固定第 1 项
    /// （<c>MissionManager:CheckInfoState</c>，MissionManager.lua:931..951）。
    /// </summary>
    [Fact]
    public void RequiredCount_UsesAssistParamForAchievementsOnly()
    {
        var achieve = new MissionEntry
        {
            Id = 35,
            Group = MissionTable.NormalGroup,
            Counts = new List<int> { 4, 12, 1, 1 },
            Assist = new List<int> { 2 },
        };
        Assert.Equal(12, achieve.RequiredCount(achieveKey: true));   // missionMax 的第 2 项
        Assert.Equal(4, achieve.RequiredCount(achieveKey: false));   // 任务口径固定第 1 项

        // assistparam 指到越界 / 完全没有 missionMax → int.MaxValue（永远不可领，宁可不发也不错发）
        var broken = new MissionEntry { Id = 1, Counts = new List<int> { 5 }, Assist = new List<int> { 4 } };
        Assert.Equal(int.MaxValue, broken.RequiredCount(achieveKey: true));
        Assert.Equal(int.MaxValue, new MissionEntry { Id = 2 }.RequiredCount(achieveKey: false));
    }

    /// <summary>配置目录不存在 → 空表 + 不抛（与 ChapterTable 一致的降级策略）。</summary>
    [Fact]
    public void MissionTable_Load_MissingDirectory_DegradesToEmpty()
    {
        var t = MissionTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-setting-" + Guid.NewGuid()));
        Assert.Empty(t.Daily);
        Assert.Empty(t.Guide);
        Assert.Empty(t.ActivePointAwards);
        Assert.Equal(0, t.ActivePointMax);
    }

    /// <summary>八张真实表的关键口径（列名被改了要立刻红）。</summary>
    [Fact]
    public void RealSettingTables_KeyFacts()
    {
        var table = MissionTable.Load(SettingDir);

        // DailyMission.txt：17 行，missionType == -1 的废弃行不装载（MissionManager.lua:57）
        Assert.Equal(16, table.Daily.Count);
        Assert.DoesNotContain(5, table.Daily.Keys);   // id=5「强化少女」missionType=-1
        Assert.Contains(101, table.Daily.Keys);
        Assert.All(table.Daily.Values, e => Assert.Equal(MissionTable.MissionGroup, e.Group));

        // GuideMission.txt：15 行，40001 通 1-1、阈值 1
        Assert.Equal(15, table.Guide.Count);
        var guide = table.GetGuide(40001);
        Assert.NotNull(guide);
        Assert.Equal(new[] { 1, 1, 1 }, guide!.TargetChapter);
        Assert.Equal(1, guide.RequiredCount(achieveKey: false));
        Assert.Equal(MissionTable.MissionGroup, guide.Group);
        Assert.Equal(40001, table.GetGuide(40002)!.PreId);    // 任务链的前置关系

        // Achievement.txt：group 6，assistparam 决定阈值项
        var achieve = table.GetAchieve(31);
        Assert.NotNull(achieve);
        Assert.Equal(MissionTable.NormalGroup, achieve!.Group);
        Assert.Equal(new[] { 1, 6, 1, 1 }, achieve.Counts);
        Assert.Equal(new[] { 4 }, achieve.Assist);
        Assert.Equal(1, achieve.RequiredCount(achieveKey: true));
        Assert.True(table.Achieve.Count > 1000);

        // AchievementOther.txt：group 18
        Assert.NotEmpty(table.AchieveOther);
        Assert.All(table.AchieveOther.Values, e => Assert.Equal(MissionTable.OtherGroup, e.Group));

        // 三段 group-5 id 区间互不重叠（1..110 / 40001..40027 / 50001..50024）
        Assert.NotEmpty(table.Club);
        Assert.Empty(table.Daily.Keys.Intersect(table.Guide.Keys));
        Assert.Empty(table.Daily.Keys.Intersect(table.Club.Keys));
        Assert.Empty(table.Guide.Keys.Intersect(table.Club.Keys));

        // ★★ 第十轮更新：`MissionAward.txt` 在新版 `setting.u` 里**换了表结构** ——
        // 旧快照是单列 `Rewards`（当时记「客户端读 Item1..5 读不到」是个真 bug），
        // 新版改成 `Item1..Item5` 五列，并把档位从 25/50/75/100/125 下调到 10/25/50/75/100。
        // ⇒ 那个「列名对不上」的缺陷**原厂后来自己修了**。
        //   我们的解析本来就同时认两种列名（MissionTable.cs:297），所以代码不用改。
        var active = table.GetActivePointAward(1);
        Assert.NotNull(active);
        Assert.Equal(10, active!.Point);
        Assert.Equal(3, active.Items.Count);
        Assert.Equal(new[] { 15, 1, 1, 1, 10000 }, active.Items[0]);
        Assert.Equal(new[] { 9, 16, 1, 1, 1 }, active.Items[1]);
        Assert.Equal(100, table.ActivePointMax);

        // GuideMissionAward.txt / ClubMissionAward.txt：物品列是 Item1..Item5
        Assert.Equal(5, table.GuideProgressAwards.Count);
        Assert.Equal(5, table.ClubProgressAwards.Count);
        Assert.Equal(3, table.GetGuideProgressAward(1)!.Point);
        Assert.Equal(15, table.GetGuideProgressAward(5)!.Point);
        Assert.Equal(500, table.GetClubProgressAward(1)!.Point);
        Assert.Equal(new[] { 7, 1, 4, 1, 5 }, Assert.Single(table.GetGuideProgressAward(1)!.Items));
    }

    // ============ 派生进度（指引任务 targetChapter）============

    /// <summary>
    /// 指引任务的完成条件是 <c>GuideMission.txt</c> 的 <c>targetChapter</c>
    /// 「完成条件(通关关卡)」列（如 40001 = <c>[1-1-1]</c>），服务端用
    /// <see cref="Player.Levels"/> 的通关次数当进度。
    /// </summary>
    [Fact]
    public void DerivedProgress_FromTargetChapterPassCount()
    {
        var entry = GuideEntry(40001, new[] { 1, 1, 1 });
        var player = new Player("acc", 1);

        Assert.Equal(0, MissionFeature.DerivedProgress(player, entry));

        player.RecordLevelPass(MapList.MapId(1, 1, 1), starMask: 7);
        Assert.Equal(1, MissionFeature.DerivedProgress(player, entry));

        player.RecordLevelPass(MapList.MapId(1, 1, 1), starMask: 7);
        Assert.Equal(2, MissionFeature.DerivedProgress(player, entry));
    }

    /// <summary>没有 targetChapter 的任务（如 40022「队伍练度 3900」）服务端算不出来 → 0。</summary>
    [Fact]
    public void DerivedProgress_WithoutTargetChapter_IsZero()
    {
        var player = new Player("acc", 1);
        player.RecordLevelPass(MapList.MapId(1, 1, 1), 7);
        Assert.Equal(0, MissionFeature.DerivedProgress(player, GuideEntry(40022, Array.Empty<int>())));
    }

    // ============ Extra["mission"] 存档 ============

    /// <summary>新号没有这份记录 → 空集合（不抛）。</summary>
    [Fact]
    public void ReadRecord_FreshPlayer_IsEmpty()
    {
        var rec = MissionFeature.ReadRecord(new Player("acc", 1));
        Assert.Empty(rec.Claimed("mission"));
        Assert.Empty(rec.Flags("activePoint"));
    }

    /// <summary>记录写进 <c>Extra["mission"]</c> 后能原样读回（随存档 round-trip）。</summary>
    [Fact]
    public void Record_RoundTripsThroughExtra()
    {
        var player = new Player("acc", 1);
        var rec = MissionFeature.ReadRecord(player);
        rec.Claimed("guide").Add(40001);
        rec.Claimed("mission").Add(101);
        rec.Flags("guideProgress").Add(1);
        MissionFeature.WriteRecord(player, rec);

        Assert.True(player.Extra.ContainsKey(MissionFeature.ExtraKey));

        var back = MissionFeature.ReadRecord(player);
        Assert.Equal(new[] { 40001 }, back.Claimed("guide"));
        Assert.Equal(new[] { 101 }, back.Claimed("mission"));
        Assert.Equal(new[] { 1 }, back.Flags("guideProgress"));
        Assert.Empty(back.Claimed("achieve"));
    }

    /// <summary>存档被手改坏（不是对象 / 元素不是数字）时安全降级，不抛。</summary>
    [Fact]
    public void ReadRecord_MalformedExtra_IsSafe()
    {
        var p1 = new Player("a", 1);
        p1.Extra[MissionFeature.ExtraKey] = JsonValue.Create(42);
        Assert.Empty(MissionFeature.ReadRecord(p1).Claimed("mission"));

        var p2 = new Player("b", 2);
        p2.Extra[MissionFeature.ExtraKey] = new JsonObject
        {
            ["claimed"] = new JsonObject { ["mission"] = new JsonArray(101, "x", 105) },
        };
        Assert.Equal(new[] { 101, 105 }, MissionFeature.ReadRecord(p2).Claimed("mission"));
    }

    /// <summary>Extra 里存的 JSON 形状固定下来（存档兼容性）。</summary>
    [Fact]
    public void Record_ExtraJsonShape()
    {
        var player = new Player("acc", 1);
        var rec = MissionFeature.ReadRecord(player);
        rec.Claimed("guide").Add(40002);
        rec.Flags("activePoint").Add(3);
        MissionFeature.WriteRecord(player, rec);

        Assert.Equal("""{"v":1,"claimed":{"guide":[40002]},"flags":{"activePoint":[3]}}""",
            player.Extra[MissionFeature.ExtraKey]!.ToJsonString());
    }

    // ============ 任务值投影（SyncTaskValues）============

    /// <summary>
    /// 指引任务通关后，投影出 <c>(进度&lt;&lt;1)|0</c> 的任务值增量；
    /// 客户端 <c>CheckInfoState</c> 于是判为「可领」（MissionManager.lua:944）。
    /// </summary>
    [Fact]
    public void SyncTaskValues_ProjectsDerivedGuideProgress()
    {
        var feature = FeatureWith(MissionTable.NewForTest()
            .Put("guide", GuideEntry(40001, new[] { 1, 1, 1 })));
        var player = new Player("acc", 1);

        Assert.Empty(feature.SyncTaskValues(player));   // 没通关 → 0，与缺省一致，不产生增量

        player.RecordLevelPass(MapList.MapId(1, 1, 1), 7);
        var changed = feature.SyncTaskValues(player);

        var (id, value) = Assert.Single(changed);
        Assert.Equal(Player.ComposeTaskId(MissionTable.MissionGroup, 40001), id);
        Assert.Equal(2, value);                                      // 进度 1、未领
        Assert.Equal(2, player.GetTask(MissionTable.MissionGroup, 40001));
        Assert.Empty(feature.SyncTaskValues(player));                 // 幂等
    }

    /// <summary>已领记录 → bit0 置 1，进度位保留（客户端 GetTaskAward 会读到 1）。</summary>
    [Fact]
    public void SyncTaskValues_ProjectsClaimedBitWithoutLosingProgress()
    {
        var feature = FeatureWith(MissionTable.NewForTest()
            .Put("guide", GuideEntry(40001, new[] { 1, 1, 1 })));
        var player = new Player("acc", 1);
        player.RecordLevelPass(MapList.MapId(1, 1, 1), 7);

        var rec = MissionFeature.ReadRecord(player);
        rec.Claimed("guide").Add(40001);
        MissionFeature.WriteRecord(player, rec);

        feature.SyncTaskValues(player);
        int v = player.GetTask(MissionTable.MissionGroup, 40001);
        Assert.Equal(1, MissionFeature.ProgressOf(v));
        Assert.Equal(1, MissionFeature.ClaimedOf(v));
    }

    /// <summary>别的通道已经写进来的进度**只升不降**：投影不会把已存计数清成派生值。</summary>
    [Fact]
    public void SyncTaskValues_NeverLowersExistingProgress()
    {
        var feature = FeatureWith(MissionTable.NewForTest()
            .Put("daily", DailyEntry(101, required: 5)));
        var player = new Player("acc", 1);
        player.SetTask(MissionTable.MissionGroup, 101, MissionFeature.ComposeTaskValue(3, 0));

        Assert.Empty(feature.SyncTaskValues(player));
        Assert.Equal(3, MissionFeature.ProgressOf(player.GetTask(MissionTable.MissionGroup, 101)));
    }

    /// <summary>
    /// 三个位标记键（30001 / 41001 / 51001）按档位 id 或 bit，bit 下标从 1 起
    /// （<c>LoadBits(nFlag, i, i)</c>，MissionManager.lua:625..627；UI_Achievement.lua:715 <c>for i=1,5</c>）。
    /// </summary>
    [Fact]
    public void SyncTaskValues_ProjectsProgressAwardBitmasks()
    {
        var feature = FeatureWith(MissionTable.NewForTest());
        var player = new Player("acc", 1);

        var rec = MissionFeature.ReadRecord(player);
        rec.Flags("activePoint").Add(1);
        rec.Flags("activePoint").Add(3);
        rec.Flags("guideProgress").Add(2);
        rec.Flags("clubProgress").Add(5);
        MissionFeature.WriteRecord(player, rec);

        feature.SyncTaskValues(player);

        Assert.Equal((1 << 1) | (1 << 3),
            player.GetTask(MissionTable.MissionGroup, MissionFeature.PointAwardTaskId));
        Assert.Equal(1 << 2,
            player.GetTask(MissionTable.MissionGroup, MissionFeature.GuideCompleteAwardTaskId));
        Assert.Equal(1 << 5,
            player.GetTask(MissionTable.MissionGroup, MissionFeature.ClubCompleteAwardTaskId));
    }

    /// <summary>投影只碰任务/成就/指引/社团的 id 与三个位标记键，绝不动别的 group（如咖啡馆等级 (1,32)）。</summary>
    [Fact]
    public void SyncTaskValues_DoesNotTouchForeignTaskKeys()
    {
        var feature = FeatureWith(MissionTable.Load(SettingDir));
        var player = new Player("acc", 1);
        player.SetTask(1, 32, 7);     // CafeFeature 的咖啡馆等级
        player.SetTask(1, 15, 6);     // GameModule 的跳教程种子

        feature.SyncTaskValues(player);

        Assert.Equal(7, player.GetTask(1, 32));
        Assert.Equal(6, player.GetTask(1, 15));
        Assert.Equal(0, player.GetTask(MissionTable.MissionGroup, MissionFeature.ActivePointTaskId));
    }

    /// <summary>
    /// pf 1026 <c>TaskValueSyncRsp</c> 只带增量即可 —— 客户端
    /// <c>Game.Player.OnTaskValueSyncRsp</c>（RVA 0x1852B18）是逐条 merge、不清表。
    /// </summary>
    [Fact]
    public void BuildTaskValueSync_CarriesEveryChangedPair()
    {
        var rsp = MissionFeature.BuildTaskValueSync(new (uint, int)[]
        {
            (Player.ComposeTaskId(5, 40001), 3),
            (Player.ComposeTaskId(5, 30001), 2),
        });

        Assert.Equal(2, rsp.Arr.Count);
        Assert.Equal(Player.ComposeTaskId(5, 40001), rsp.Arr[0].Id);
        Assert.Equal(3, rsp.Arr[0].Value);
        Assert.Equal(Player.ComposeTaskId(5, 30001), rsp.Arr[1].Id);
        Assert.Equal(2, rsp.Arr[1].Value);
    }

    // ============ 端到端：pf 1022 上行 → pf 1024 MissionMgrMsg ============

    /// <summary>
    /// 每一个上行名都必须**已注册**（GameModule 反射装配）并回在 <c>MissionMgrMsg</c> 上，
    /// 且回包一定含 <c>nError</c>/<c>nMission</c> —— 否则 <c>Game.Blocking</c> 遮罩不会 Hide。
    /// </summary>
    [Theory]
    [InlineData("AchievementGetAward", 0)]
    [InlineData("MissionGetAward", 1)]
    [InlineData("MissionActiveAward", 2)]
    [InlineData("GuideMissionGetAward", 3)]
    [InlineData("GuideProgressGetAward", 4)]
    [InlineData("ClubMissionGetAward", 5)]
    [InlineData("ClubProgressGetAward", 6)]
    public async Task EveryUpFunction_RepliesOnMissionMgrMsg(string function, int expectedMission)
    {
        using var h = await MissionHarness.CreateAsync("mission_" + function);

        await h.DispatchFunctionAsync(function, """{"nId":999999,"nType":0}""");

        var (down, json) = Assert.Single(h.Outbound);
        Assert.Equal("MissionMgrMsg", down);
        Assert.NotEqual("LuaCall", down);      // 不能走数字命令族，否则会被当分片处理

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Null(root["sCmd"]);             // 这一族没有 sCmd/tbParam 外壳
        Assert.Null(root["tbParam"]);
        Assert.Equal(expectedMission, root["nMission"]!.GetValue<int>());
        Assert.NotNull(root["nError"]);
    }

    /// <summary>不存在的 id → nError = <see cref="MissionFeature.ErrNotExist"/>，但仍然回包（遮罩要消）。</summary>
    [Fact]
    public async Task UnknownId_RepliesNotExist()
    {
        using var h = await MissionHarness.CreateAsync("mission_unknown");

        await h.DispatchFunctionAsync(MissionFeature.FnGuideMissionGetAward, """{"nId":424242,"nType":0}""");

        var (_, json) = Assert.Single(h.Outbound);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(MissionFeature.ErrNotExist, root["nError"]!.GetValue<int>());
        Assert.Equal(MissionFeature.MissionGuide, root["nMission"]!.GetValue<int>());
    }

    /// <summary>
    /// 社团进度奖励的判定值是 <c>ClubCommon.MyClubInfo.primary.weektaskvalue</c>
    /// （MissionManager.lua:968），服务端没有这份数据 → 明确拒绝，不错发。
    /// </summary>
    [Fact]
    public async Task ClubProgressGetAward_RepliesNoServerData()
    {
        using var h = await MissionHarness.CreateAsync("mission_clubprog");

        await h.DispatchFunctionAsync(MissionFeature.FnClubProgressGetAward, """{"nId":1,"nType":0}""");

        var (_, json) = Assert.Single(h.Outbound);
        Assert.Equal(MissionFeature.ErrNoServerData, JsonNode.Parse(json)!["nError"]!.GetValue<int>());
    }

    /// <summary>未登录（Tag 非 Player）时只记日志，不回包也不抛。</summary>
    [Fact]
    public async Task WithoutLogin_DoesNotReply()
    {
        using var h = await MissionHarness.CreateAsync("mission_nologin", attachPlayer: false);

        foreach (var fn in MissionFeature.UpFunctions)
            await h.DispatchFunctionAsync(fn, """{"nId":1}""");

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// 一键领取（<c>MissionGetAward</c> nId=0 nType=1，UI_Achievement.lua:864）在
    /// 一条日常任务都没完成时回 <c>tbIdList=[]</c>：这是「一键领了 0 条」的真实语义
    /// （客户端据此把 <c>HaveGetMission</c> 置 false，MissionManager.lua:739）。
    /// </summary>
    [Fact]
    public async Task OneKeyDailyClaim_FreshPlayer_RepliesEmptyIdList()
    {
        using var h = await MissionHarness.CreateAsync("mission_onekey");

        await h.DispatchFunctionAsync(MissionFeature.FnMissionGetAward, """{"nId":0,"nType":1}""");

        var (_, json) = Assert.Single(h.Outbound);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(0, root["nError"]!.GetValue<int>());
        Assert.Equal(MissionFeature.MissionDaily, root["nMission"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(root["tbIdList"]));
    }

    /// <summary>
    /// 一键领取会把「进度已够」的日常任务全部领掉并回 <c>tbIdList</c>；
    /// 少女事件（<c>missionType == 2</c>）不进一键范围（UI_Achievement.lua:257 的 <c>type ~= 2</c>）。
    /// </summary>
    [Fact]
    public async Task OneKeyDailyClaim_CollectsFinishedNonGirlMissions()
    {
        using var h = await MissionHarness.CreateAsync("mission_onekey2");
        var table = MissionTable.Load(SettingDir);

        // 101「任意战斗5次」missionType=0，阈值 5；1「摸头」missionType=2（少女事件）
        int need101 = table.GetDaily(101)!.RequiredCount(achieveKey: false);
        h.Player.SetTask(MissionTable.MissionGroup, 101, MissionFeature.ComposeTaskValue(need101, 0));
        int need1 = table.GetDaily(1)!.RequiredCount(achieveKey: false);
        h.Player.SetTask(MissionTable.MissionGroup, 1, MissionFeature.ComposeTaskValue(need1, 0));
        Assert.Equal(2, table.GetDaily(1)!.Type);

        await h.DispatchFunctionAsync(MissionFeature.FnMissionGetAward, """{"nId":0,"nType":1}""");

        var arr = Assert.IsType<JsonArray>(JsonNode.Parse(Assert.Single(h.Outbound).Json)!["tbIdList"]);
        Assert.Equal(new[] { 101 }, arr.Select(n => n!.GetValue<int>()));
        Assert.Equal(1, MissionFeature.ClaimedOf(h.Player.GetTask(MissionTable.MissionGroup, 101)));
        Assert.Equal(0, MissionFeature.ClaimedOf(h.Player.GetTask(MissionTable.MissionGroup, 1)));
    }

    /// <summary>
    /// ★ 完整闭环：通关 1-1 → 指引任务 40001 领奖成功（nError=0）→ 再领报「已领取」。
    /// 用真实的 <c>extracted/setting/GuideMission.txt</c>（40001 的 targetChapter=[1-1-1]、missionMax=[1]）。
    /// </summary>
    [Fact]
    public async Task GuideMission40001_ClaimAfterClearingChapter1_1()
    {
        using var h = await MissionHarness.CreateAsync("mission_guide40001");

        h.Player.RecordLevelPass(MapList.MapId(1, 1, 1), starMask: 7);

        await h.DispatchFunctionAsync(MissionFeature.FnGuideMissionGetAward, """{"nId":40001,"nType":0}""");
        var first = JsonNode.Parse(h.Outbound[^1].Json)!;
        Assert.Equal(0, first["nError"]!.GetValue<int>());
        Assert.Equal(MissionFeature.MissionGuide, first["nMission"]!.GetValue<int>());
        Assert.Null(first["tbIdList"]);   // 单领不带 tbIdList

        // 已领标记落在任务值 bit0 与 Extra 两处
        Assert.Equal(1, MissionFeature.ClaimedOf(h.Player.GetTask(MissionTable.MissionGroup, 40001)));
        Assert.Equal(1, MissionFeature.ProgressOf(h.Player.GetTask(MissionTable.MissionGroup, 40001)));
        Assert.Contains(40001, MissionFeature.ReadRecord(h.Player).Claimed("guide"));

        await h.DispatchFunctionAsync(MissionFeature.FnGuideMissionGetAward, """{"nId":40001,"nType":0}""");
        Assert.Equal(MissionFeature.ErrAlreadyGot,
            JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());
    }

    /// <summary>没通关就领指引任务 → 进度不足（服务端不信客户端的按钮状态）。</summary>
    [Fact]
    public async Task GuideMission40001_WithoutClearing_RepliesNotFinished()
    {
        using var h = await MissionHarness.CreateAsync("mission_guide_nf");

        await h.DispatchFunctionAsync(MissionFeature.FnGuideMissionGetAward, """{"nId":40001,"nType":0}""");

        Assert.Equal(MissionFeature.ErrNotFinished,
            JsonNode.Parse(Assert.Single(h.Outbound).Json)!["nError"]!.GetValue<int>());
    }

    /// <summary>
    /// 指引任务完成数进度奖励（nMission=4）：完成数 = 进度达标的指引任务条数
    /// （<c>GetGuideMissionCompleteNum</c>，MissionManager.lua:631..641），
    /// 第 1 档阈值 Point=3 → 通关 1-1/1-2/1-3 后可领，并把 bit1 写进 (5,41001)。
    /// </summary>
    [Fact]
    public async Task GuideProgressGetAward_ClaimsAfterThreeGuideMissionsDone()
    {
        using var h = await MissionHarness.CreateAsync("mission_guideprog");

        // 阈值不到（只完成 2 条）→ 拒绝
        h.Player.RecordLevelPass(MapList.MapId(1, 1, 1), 7);
        h.Player.RecordLevelPass(MapList.MapId(1, 2, 1), 7);
        await h.DispatchFunctionAsync(MissionFeature.FnGuideProgressGetAward, """{"nId":1,"nType":0}""");
        Assert.Equal(MissionFeature.ErrNotFinished,
            JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());

        // 第 3 条完成 → 可领
        h.Player.RecordLevelPass(MapList.MapId(1, 3, 1), 7);
        await h.DispatchFunctionAsync(MissionFeature.FnGuideProgressGetAward, """{"nId":1,"nType":0}""");
        Assert.Equal(0, JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());
        Assert.Equal(1 << 1,
            h.Player.GetTask(MissionTable.MissionGroup, MissionFeature.GuideCompleteAwardTaskId));

        // 重复领取 → 已领取
        await h.DispatchFunctionAsync(MissionFeature.FnGuideProgressGetAward, """{"nId":1,"nType":0}""");
        Assert.Equal(MissionFeature.ErrAlreadyGot,
            JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());
    }

    /// <summary>
    /// 活跃点数进度奖励（nMission=2）：点数够（任务值 (5,30000) 是**裸值**，MissionManager.lua:619）
    /// → 领取成功并把档位 bit 写进 (5,30001)；重复领取报「已领取」；
    /// 活跃点数本身不会被投影成位域。
    /// </summary>
    [Fact]
    public async Task MissionActiveAward_ClaimsWhenPointsReached()
    {
        using var h = await MissionHarness.CreateAsync("mission_active");
        int point = MissionTable.Load(SettingDir).GetActivePointAward(1)!.Point;

        await h.DispatchFunctionAsync(MissionFeature.FnMissionActiveAward, """{"nId":1,"nType":0}""");
        Assert.Equal(MissionFeature.ErrNotFinished,
            JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());

        h.Player.SetTask(MissionTable.MissionGroup, MissionFeature.ActivePointTaskId, point);

        await h.DispatchFunctionAsync(MissionFeature.FnMissionActiveAward, """{"nId":1,"nType":0}""");
        var root = JsonNode.Parse(h.Outbound[^1].Json)!;
        Assert.Equal(0, root["nError"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(root["tbItems"]));

        Assert.Equal(1 << 1, h.Player.GetTask(MissionTable.MissionGroup, MissionFeature.PointAwardTaskId));
        Assert.Equal(point, h.Player.GetTask(MissionTable.MissionGroup, MissionFeature.ActivePointTaskId));

        await h.DispatchFunctionAsync(MissionFeature.FnMissionActiveAward, """{"nId":1,"nType":0}""");
        Assert.Equal(MissionFeature.ErrAlreadyGot,
            JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());
    }

    /// <summary>
    /// 成就领奖（nMission=0）：阈值取 <c>missionMax[assistparam[1]]</c>。
    /// 用真实 Achievement.txt id=1（<c>missionMax=[5] assistparam=[1]</c>，group 6）。
    /// </summary>
    [Fact]
    public async Task AchievementGetAward_UsesGroup6AndAssistParam()
    {
        using var h = await MissionHarness.CreateAsync("mission_achieve");
        var cfg = MissionTable.Load(SettingDir).GetAchieve(1)!;
        Assert.Equal(MissionTable.NormalGroup, cfg.Group);

        await h.DispatchFunctionAsync(MissionFeature.FnAchievementGetAward, """{"nId":1,"nType":0}""");
        Assert.Equal(MissionFeature.ErrNotFinished,
            JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());

        h.Player.SetTask(MissionTable.NormalGroup, 1,
            MissionFeature.ComposeTaskValue(cfg.RequiredCount(achieveKey: true), 0));

        await h.DispatchFunctionAsync(MissionFeature.FnAchievementGetAward, """{"nId":1,"nType":0}""");
        Assert.Equal(0, JsonNode.Parse(h.Outbound[^1].Json)!["nError"]!.GetValue<int>());
        Assert.Equal(1, MissionFeature.ClaimedOf(h.Player.GetTask(MissionTable.NormalGroup, 1)));
        Assert.Contains(1, MissionFeature.ReadRecord(h.Player).Claimed("achieve"));
    }

    /// <summary>上行 tbParam 只有 nId/nType（MissionManager.lua:922）；缺字段时按 0 处理，不抛。</summary>
    [Fact]
    public async Task MalformedUpstreamParam_IsTolerated()
    {
        using var h = await MissionHarness.CreateAsync("mission_malformed");

        await h.DispatchFunctionAsync(MissionFeature.FnMissionGetAward, "{}");
        await h.DispatchFunctionAsync(MissionFeature.FnMissionGetAward, """{"nId":"7"}""");
        await h.DispatchFunctionAsync(MissionFeature.FnMissionGetAward, """{"nId":null,"nType":null}""");

        Assert.Equal(3, h.Outbound.Count);
        Assert.All(h.Outbound, o =>
        {
            var root = JsonNode.Parse(o.Json)!;
            Assert.Equal("MissionMgrMsg", o.Function);
            Assert.NotNull(root["nError"]);
            Assert.Equal(MissionFeature.MissionDaily, root["nMission"]!.GetValue<int>());
        });
    }

    // ---- 小工具 ----

    /// <summary><c>&lt;仓库根&gt;/extracted/setting</c>。用编译期源文件路径反推，不依赖运行期 CWD。</summary>
    private static string SettingDir { get; } = ResolveSettingDir();

    private static string ResolveSettingDir([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extracted", "setting");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"从 {thisFile} 向上找不到 extracted/setting");
    }

    private static MissionEntry GuideEntry(int id, IReadOnlyList<int> targetChapter) => new()
    {
        Id = id,
        Group = MissionTable.MissionGroup,
        Counts = new List<int> { 1 },
        TargetChapter = targetChapter.ToList(),
    };

    private static MissionEntry DailyEntry(int id, int required) => new()
    {
        Id = id,
        Group = MissionTable.MissionGroup,
        Counts = new List<int> { required },
    };

    /// <summary>用指定配置表造一个 feature（不经反射装配，配置完全可控；只测不需要 ctx 的纯逻辑）。</summary>
    private static MissionFeature FeatureWith(MissionTable table)
    {
        var feature = new MissionFeature();
        typeof(MissionFeature)
            .GetField("_table", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(feature, table);
        return feature;
    }
}

/// <summary>
/// 任务系统专用的端到端夹具。与 <c>LuaCallHarness</c> 的差别只有一个：
/// 显式把 <see cref="ServerOptions.SettingDirectory"/> 指到**仓库里的真实 setting 目录**
/// （用 <see cref="CallerFilePathAttribute"/> 反推仓库根），
/// 否则 <c>dotnet test</c> 的工作目录随 <c>ArtifactsPath</c> 落在 /tmp，
/// 反射装配出来的 <see cref="MissionFeature"/> 会读到空配置表，端到端断言全部失去意义。
///
/// 其余照旧：真实 <see cref="GameModule"/>（反射装配所有 <see cref="IGameFeature"/>）
/// + 真实 <see cref="LuaCallRouter"/> + loopback <see cref="ClientSession"/>，
/// 把 <c>NtfS2CCallSender</c> 换成内存捕获器来断言 pf 1024 的 (sFunction, sJsonParam)。
/// </summary>
internal sealed class MissionHarness : IDisposable
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

    private MissionHarness(TcpListener listener, TcpClient client, TcpClient accepted,
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

    public static async Task<MissionHarness> CreateAsync(string account, bool attachPlayer = true)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "sssj-mission-tests", Guid.NewGuid().ToString("N"));

        var pf = new PfRouter();
        var luaCalls = new LuaCallRouter();
        var module = new GameModule(new ServerOptions
        {
            DataDirectory = dataDir,
            SettingDirectory = ResolveSettingDir(),
        });
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

        var harness = new MissionHarness(listener, client, accepted, session, luaCalls, player, dataDir);

        // 覆盖 GameModule 装好的真实 sender：只收集，不落到 socket
        luaCalls.NtfS2CCallSender = (_, function, sJsonParam) =>
        {
            harness.Outbound.Add((function, Encoding.UTF8.GetString(sJsonParam)));
            return Task.CompletedTask;
        };

        return harness;
    }

    /// <summary>模拟 pf 1022 里 sFunction=&lt;字符串命令&gt; 的上行（S2CCall.lua 那一族）。</summary>
    public Task DispatchFunctionAsync(string function, string sJsonParam)
        => LuaCalls.DispatchAsync(Session, function, sJsonParam);

    /// <summary>模拟 pf 1022 里 sFunction="LuaCall" 的上行（数字命令族）。</summary>
    public Task DispatchLuaCallAsync(string sJsonParam)
        => LuaCalls.DispatchAsync(Session, LuaCallRouter.LuaCallFunction, sJsonParam);

    private static string ResolveSettingDir([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extracted", "setting");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"从 {thisFile} 向上找不到 extracted/setting");
    }

    public void Dispose()
    {
        try { Session.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _accepted.Dispose(); } catch { }
        try { _listener.Stop(); } catch { }
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
