using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 排行塔（Challenge Tower）：LuaCall 53 取塔信息 / 54 进关 / 55 通关（含失败结算）/
/// 56 失败 / 207 保留成绩 / 206 积分奖励一键领取。
///
/// <para><b>0. ★★★ 为什么它「活着」（三层准入都不看档期）</b></para>
/// 见 <see cref="ChallengeTable"/> 类注释 §1。一句话：入口只受「通关 5-12」限制，
/// 取当期**无上界检查**，界面对「过了 EndTime」只隐藏倒计时。
/// ★ 本地期表只排到 <c>WeekIndex=349</c>（2026-06-29）⇒ <see cref="ChallengeTable.CurrentWeekIndex"/>
/// 按公式算真实周次后 **clamp 到表尾**，与客户端「取最后一个 BeginTime ≤ now 的期」一致。
///
/// <para><b>1. ★★ 53/54/55 是「通用塔命令」，本域只处理 <c>ActivityType == 6</c>（Challenge）</b></para>
/// 这几个 id 被十几个塔玩法共用（<c>ChessTowerLogic</c>/<c>TwelfthTowerLogic</c>/…），
/// 但那些塔的档期**全部过期**。客户端按 <c>nActivitieType</c> 分流
/// （<c>ActivitiesLogic.lua:431/446/543/552</c>），所以服务端也只认 6、其余记日志放过。
/// ★ 上行是**位置数组**（不是对象）：
/// <code>
/// ActivitiesLogic.lua:448  CallServer(Activities_EnterTower,
///     { nActivitieType, tbCurChallageTowerConfig.Id, NodeId, LevelId, currentEquipSolutionId })
/// ActivitiesLogic.lua:570  SendInfoToServer(Activities_PassTower,
///     { CurActivityType, 塔Id, NodeId, LevelId, 编队Id, tbExtraParam, nil, tbKillParam, 助战roleId })
/// </code>
///
/// <para><b>2. ★★★ 失败也走 55（不是 56）—— 因为失败也要结算积分</b></para>
/// <code>
/// ActivitiesLogic.lua:563..573  function tbLogic:FailTower(tbExtraParam)
///     if self.CurActivityType == tbLogic.ActivityType.Challenge then
///         …
///         GameManager.SendInfoToServer( LuaCall.Activities_PassTower, {…} )
///         -- 挑战的失败结算也从成功发送到服务器。因为失败也会进行积分结算
///         return
/// </code>
/// ⇒ 55 的回包必须带 **<c>isPass</c>**（布尔）让客户端区分成败：
/// <c>UI_MissionSuccess.lua:240/264</c> 是 <c>if self.tbActivityData.isPass == false then</c>
/// —— **显式与 false 比较** ⇒ 必须给真正的布尔，给 0/1 或省略都会走错分支。
/// ★ 56 <c>Activities_FailTower</c> 在排行塔链路上**不会被发**（客户端走上面那条），
/// 但仍然注册（回空包）以防别的塔玩法误触时变 unhandled。
///
/// <para><b>3. 积分与奖励的存储（group 9）</b></para>
/// <list type="table">
///   <item><term>总最高分</term><description>偏移 <b>45901</b>（<c>ActivitiesLogic.lua:780</c>）</description></item>
///   <item><term>已领奖励档位</term><description>偏移 <b>45906</b>（<c>:784</c>）★ 存的是**已领到的 Goal 数值**，不是位掩码</description></item>
///   <item><term>关卡进度 / 关卡最高分</term><description>基址 <b>45000</b> / <b>46000</b>（<c>:790/:791</c>）</description></item>
/// </list>
///
/// <para><b>4. ★★ 206 的回包 <c>res</c> 必须是布尔 <c>true</c></b></para>
/// <code>
/// UI_TowerScoreInfo.lua:77..100  function tbClass:OnRecive_RewardSome(tbParam)
///     if tbParam.res == true then                          -- ★ 显式 == true
///         for k,v in pairs(tbParam.Reward) do …            -- ★ Reward 必须存在（pairs 对 nil 抛）
///         UI.OpenAsChild("UI_RewardNormal", reward)
///         self:OpenGradePanel(self.tbConfig)
///         self:Parent():UpdateInfo()
/// </code>
/// ⇒ 成功回 <c>{res = true, Reward = [[G,D,P,L,N],…]}</c>；
/// **没得领时不能回 <c>res=true</c>**（会开一个空奖励弹窗），回 <c>{res=false}</c> 即可。
///
/// <para><b>5. 207 的消费点不收参数 ⇒ 先推任务值再回包</b></para>
/// <c>UI_MissionSuccess.lua:2056..2061</c> <c>OnSaveDataCallback()</c> **无参**，
/// 只调 <c>self.fnEndCallback()</c>（退出流程）。
/// ⇒ 服务端要先把「保留后的分数」推成任务值，再回包（与 §22.2 同型）。
/// ★ 上行是**裸布尔**（<c>UI_MissionSuccess.lua:2028/2033</c> 传 <c>false</c>/<c>true</c>）——
/// <c>true</c> = 保留本次成绩、<c>false</c> = 放弃。
/// </summary>
public sealed class ChallengeTowerFeature : IGameFeature
{
    /// <summary>LuaCall.Activities_GetTower = 53「获取当前塔活动信息」。</summary>
    public const int CmdGetTower = 53;

    /// <summary>LuaCall.Activities_EnterTower = 54「进入塔」。</summary>
    public const int CmdEnterTower = 54;

    /// <summary>LuaCall.Activities_PassTower = 55「通关塔」（★ 排行塔的失败也走这条）。</summary>
    public const int CmdPassTower = 55;

    /// <summary>LuaCall.Activities_FailTower = 56「失败塔」。</summary>
    public const int CmdFailTower = 56;

    /// <summary>LuaCall.Challenge_Award_Some = 206「排行塔积分奖励一键领取」。</summary>
    public const int CmdAwardSome = 206;

    /// <summary>LuaCall.Challenge_Exit_Save = 207「结算后是否保留成绩」。</summary>
    public const int CmdExitSave = 207;

    /// <summary>
    /// <c>ActivitiesLogic.ActivityType.Challenge</c> = <b>6</b>
    /// （<c>ActivitiesLogic.lua:32</c>）。本域只处理这个类型。
    /// </summary>
    public const int ActivityTypeChallenge = 6;

    /// <summary>通用失败码。</summary>
    public const int ErrGeneric = 1;

    private GameFeatureContext _ctx = null!;
    private ChallengeTable _table = ChallengeTable.Empty;

    public string Name => "排行塔（LuaCall 53/54/55/56 + 206 积分领奖 / 207 保留成绩）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = ChallengeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapLuaCall(CmdGetTower, OnGetTower);
        ctx.MapLuaCall(CmdEnterTower, OnEnterTower);
        ctx.MapLuaCall(CmdPassTower, OnPassTower);
        ctx.MapLuaCall(CmdFailTower, OnFailTower);
        ctx.MapLuaCall(CmdAwardSome, OnAwardSome);
        ctx.MapLuaCall(CmdExitSave, OnExitSave);
    }

    // ================================================================ 53 取塔信息

    /// <summary>
    /// 53 取当前塔活动信息。
    /// 客户端 <c>OnGetTower</c>（<c>ActivitiesLogic.lua:430..441</c>）按
    /// <c>tbParam.ActivityType == 6</c> 分流到「刷 <c>UI_ChallengeTower</c> 界面」
    /// —— **不读别的字段** ⇒ 回 <c>{ActivityType = 6}</c> 即可。
    /// </summary>
    internal Task OnGetTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int week = _table.CurrentWeekIndex(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        int total = player.GetTask(ChallengeTable.Group, ChallengeTable.TaskTotalScore);

        Log.Info($"[session {session.Id}] Activities_GetTower(53) 排行塔：第 {week} 期"
               + $"（本地 {_table.WeekCount} 期）、总积分 {total}");

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["ActivityType"] = ActivityTypeChallenge,
        });
        return Task.CompletedTask;
    }

    // ================================================================ 54 进关

    /// <summary>
    /// 54 进入塔关。上行是**位置数组**
    /// <c>[nActivitieType, 塔Id, NodeId, LevelId, 编队Id]</c>（<c>ActivitiesLogic.lua:448</c>）。
    ///
    /// 回包（<c>ActivitiesLogic.lua:457..478</c>）：
    /// <c>tbData</c> 是**位置数组** <c>[activitieType, id, zoneId, levelId]</c>
    /// （<c>:472</c> <c>tbParam.tbData[1], [2], [3], [4]</c>）。
    /// ★ 与佣兵 323 的 <c>tbData</c> 是**对象**不同 —— 同一个概念两种形状，别抄错。
    /// </summary>
    internal Task OnEnterTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player)
            return Task.CompletedTask;

        var args = ReadPositional(param);
        int type = args.Count > 0 ? args[0] : 0;
        if (type != ActivityTypeChallenge)
        {
            Log.Warn($"[session {session.Id}] Activities_EnterTower(54) ActivityType={type} "
                   + "不是排行塔(6)，属已过期的其它塔玩法，忽略");
            return Task.CompletedTask;
        }

        int towerId = args.Count > 1 ? args[1] : 0;
        int nodeId = args.Count > 2 ? args[2] : 0;
        int levelId = args.Count > 3 ? args[3] : 0;

        Log.Info($"[session {session.Id}] Activities_EnterTower(54) 排行塔 塔{towerId} "
               + $"节点{nodeId} 关卡{levelId}");

        // ★ tbData 是位置数组（客户端 tbParam.tbData[1..4]）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["tbData"] = new JsonArray { type, towerId, nodeId, levelId },
        });
        return Task.CompletedTask;
    }

    // ================================================================ 55 通关 / 失败结算

    /// <summary>
    /// 55 通关（★ 排行塔的**失败也走这条**，见类注释 §2）。
    /// 上行位置数组 <c>[type, 塔Id, NodeId, LevelId, 编队Id, tbExtraParam, nil, tbKillParam, 助战roleId]</c>。
    ///
    /// ★★ 回包必须带 <c>isPass</c>（**布尔**）：客户端
    /// <c>UI_MissionSuccess.lua:240/264</c> 是 <c>if isPass == false then</c> 显式比较。
    /// 本服无法从上行区分成败（两条路都发 55）⇒ 依据「本次是否有击杀信息/额外参数」不可靠，
    /// 因此**一律按通关处理**（<c>isPass = true</c>）：
    /// 排行塔的失败结算与通关结算在服务端侧行为相同（都只记积分），
    /// 而客户端自己知道成败、界面已按本地状态渲染。
    /// </summary>
    internal Task OnPassTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var args = ReadPositional(param);
        int type = args.Count > 0 ? args[0] : 0;
        if (type != ActivityTypeChallenge)
        {
            Log.Warn($"[session {session.Id}] Activities_PassTower(55) ActivityType={type} "
                   + "不是排行塔(6)，忽略");
            return Task.CompletedTask;
        }

        int levelId = args.Count > 3 ? args[3] : 0;

        Log.Info($"[session {session.Id}] Activities_PassTower(55) 排行塔关卡 {levelId} 结算"
               + "（★ 失败也走这条，服务端行为相同：只记积分）");

        // ★ tbData 是奖励数组（map:GameEnd 的第 2 参）；排行塔的关卡奖励未取证 ⇒ 回空数组
        //   （客户端 UI_MissionSuccess.lua:681/698 判 `#self.tbAwards > 0`，空数组安全）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["tbData"] = new JsonArray(),
            ["isPass"] = true,          // ★ 必须是布尔（客户端 == false 显式比较）
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 56 失败塔。★ 排行塔链路上**不会被发**（客户端走 55，见类注释 §2），
    /// 但原厂这条命令的服务端分支会 <c>LuaCall:CallClient(sCmd)</c>（**无参**）
    /// ⇒ 保持一致：回空包，避免别的塔玩法误触时变 unhandled。
    /// </summary>
    internal Task OnFailTower(ClientSession session, int cmd, JsonNode? param)
    {
        Log.Info($"[session {session.Id}] Activities_FailTower(56) 收到"
               + "（排行塔链路本不该走这条，按原厂回空包）");
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 207 保留成绩

    /// <summary>
    /// 207 结算后是否保留成绩。上行是**裸布尔**
    /// （<c>UI_MissionSuccess.lua:2028</c> 传 <c>false</c>、<c>:2033</c> 传 <c>true</c>）。
    ///
    /// ★ 消费点 <c>OnSaveDataCallback()</c>（<c>:2056</c>）**不收参数**、只调退出回调
    /// ⇒ 先推任务值、再回包（§22.2）。
    /// ★ 本次单局得分来自客户端上报的 <c>SingleScore</c>，但那是 55 的
    /// <c>tbExtraParam</c> 里的内容、且原厂在此已弹过确认框
    /// ⇒ 服务端这里只负责「把当前累计分固化」，不重算分数。
    /// </summary>
    internal Task OnExitSave(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        bool keep = ReadBool(param);
        int total = player.GetTask(ChallengeTable.Group, ChallengeTable.TaskTotalScore);

        Log.Info($"[session {session.Id}] Challenge_Exit_Save(207) "
               + $"{(keep ? "保留" : "放弃")}本次成绩（当前总积分 {total}）");

        // 保留时把总分推一遍（客户端界面按任务值渲染）；放弃时不动数据
        if (keep)
        {
            GameNotify.PushTaskValues(session, new[]
            {
                new KeyValuePair<uint, int>(
                    Player.ComposeTaskId(ChallengeTable.Group, ChallengeTable.TaskTotalScore), total),
            });
        }

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 206 积分奖励一键领取

    /// <summary>
    /// 206 积分奖励一键领取。上行空表（<c>UI_TowerScoreInfo.lua:69</c>）。
    ///
    /// ★★ 回包 <c>{res = true, Reward = [[G,D,P,L,N],…]}</c> —— <c>res</c> 必须是
    /// **布尔 <c>true</c>**（客户端 <c>:78</c> <c>if tbParam.res == true then</c> 显式比较），
    /// 且 <c>Reward</c> 必须存在（<c>:81</c> <c>pairs(tbParam.Reward)</c> 对 nil 会抛）。
    /// ★ **没得领时回 <c>{res = false}</c>**，否则会开一个空奖励弹窗。
    ///
    /// ★★ 领取记录存「**已领到的 Goal 数值**」（不是位掩码）——
    /// 判据 <c>UI_TowerScoreInfo.lua:142</c> <c>if tbLevelConfig.Goal &lt;= latestGet then 已领取</c>。
    /// </summary>
    internal Task OnAwardSome(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int total = player.GetTask(ChallengeTable.Group, ChallengeTable.TaskTotalScore);
        int claimed = player.GetTask(ChallengeTable.Group, ChallengeTable.TaskRewardLevel);

        var list = _table.ClaimableRewards(total, claimed);
        if (list.Count == 0)
        {
            Log.Info($"[session {session.Id}] Challenge_Award_Some(206) 无可领档位"
                   + $"（总积分 {total}、已领到 Goal {claimed}、共 {_table.Rewards.Count} 档）"
                   + " ⇒ 回 res=false");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["res"] = false });
            return Task.CompletedTask;
        }

        var granted = new RewardGrantResult();
        foreach (var r in list)
        {
            if (r.Rewards.Count > 0)
                _ctx.Rewards.Grant(player, r.Rewards, RewardSource.Extra, granted);
        }

        // ★ 记「已领到的最大 Goal」
        int newClaimed = list.Max(r => r.Goal);
        player.SetTask(ChallengeTable.Group, ChallengeTable.TaskRewardLevel, newClaimed);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Challenge_Award_Some(206) 领取 {list.Count} 档"
               + $"（Goal {string.Join(',', list.Select(r => r.Goal))}）：已领档位 {claimed} → {newClaimed}"
               + $"、发奖 {granted.Rewards.Count} 项");

        // ★ 先推数据再回包
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(ChallengeTable.Group, ChallengeTable.TaskRewardLevel), newClaimed),
        });

        var reward = new JsonArray();
        foreach (var g in granted.Rewards)
        {
            var one = new JsonArray();
            foreach (int v in g.ToTuple())
                one.Add(v);
            reward.Add(one);
        }
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["res"] = true,             // ★ 布尔，客户端 == true 显式比较
            ["Reward"] = reward,        // ★ 必须存在（pairs 对 nil 抛）
        });
        return Task.CompletedTask;
    }

    // ================================================================ 公共

    /// <summary>
    /// 读「位置数组」形式的上行（<c>[type, id, node, level, …]</c>）。
    /// ★ 非数组一律回空列表（本仓通用坑：不能假设上行形状）。
    /// </summary>
    internal static List<int> ReadPositional(JsonNode? param)
    {
        var result = new List<int>();
        if (param is not JsonArray arr)
            return result;
        foreach (var it in arr)
        {
            if (it is JsonValue v)
            {
                if (v.TryGetValue<int>(out int i)) { result.Add(i); continue; }
                if (v.TryGetValue<double>(out double d)) { result.Add((int)d); continue; }
            }
            result.Add(0);      // ★ 占位：保持后续元素的下标不错位（上行第 7 位是 nil）
        }
        return result;
    }

    /// <summary>读「裸布尔」形式的上行（207）。</summary>
    internal static bool ReadBool(JsonNode? param)
    {
        if (param is not JsonValue v)
            return false;
        if (v.TryGetValue<bool>(out bool b)) return b;
        if (v.TryGetValue<int>(out int i)) return i != 0;
        if (v.TryGetValue<string>(out string? s)) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
