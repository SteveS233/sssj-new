using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 誓约系统（<c>Pldge</c>）—— 少女的「阶段任务 → 进阶」养成线。
///
/// <para><b>0. ★★★ 本域为什么能做（第十五轮推翻的旧结论）</b></para>
/// <see cref="MainCityFeature"/> 旧注释称「打开开关，玩家点进去就会加载**本服没实现的**誓约地图」，
/// 据此把入口写死关闭。**本轮四条判据全部证伪**：
/// <code>
/// 真机 CS.Game.Map.Find(11001)          → Game.Map+Info（有效），scene = "Promise"
/// adb ls .../files/map/ | grep promise   → mappromise_prefab.u          （设备上有）
/// ls work/assets-serve/map/              → mappromise_prefab.u          （资源站有）
/// grep -c mappromise version.data        → 2                            （清单已登记）
/// </code>
/// ★ 当初误判的原因：用了**不存在的 API** <c>Map.GetInfo</c>（正确的是 <c>Map.Find</c>，
/// <c>dump.cs:555419</c>）拿到 nil。
/// ⇒ <b>判「某数据不存在」之前，先用已知存在的同类值验证查询手段本身</b>
/// （我是拿 map 2/6/7 对照才发现是方法名错，不是数据缺失）。
/// 完整取证见 <c>work/pledge-forensics.md</c>。
///
/// <para><b>1. 命令面：7 条里只有 4 条有活跃上行点</b></para>
/// <code>
/// 232 Pldge_ReqOpenGirls   GirlPromise.lua:541  {}                      ✅
/// 233 Pldge_NewGirl        GirlPromise.lua:589  {girlid}                ✅
/// 234 Pldge_SetTaskFinish  GirlPromise.lua:610  {girlid, taskid}        ✅
/// 235 Pldge_StepUpGirl     GirlPromise.lua:627  {girlid}                ✅
/// 236 Pldge_PushGirlData   0 处  ⇒ 纯下行通道，无上行
/// 237 Pldge_ReqOneGirl     0 处  ⇒ 客户端永不发送
/// 238 Pldge_ComplateTask   0 处  ⇒ 客户端永不发送
/// </code>
/// （判据：<c>grep -rn "LuaCall.&lt;名&gt;" extracted/lua/ | grep -v LuaCall.lua</c>）
/// 另接管 <b>231</b> <c>Pldge_ReqGirls</c>（原在 <see cref="MainCityFeature"/> 回空列表）。
///
/// <para><b>2. ★★★ 第八种错误约定：<c>Err</c></b></para>
/// 234 的客户端消费点是
/// <code>
/// GirlPromise.lua:613  if tbParam.Err == nil then ... UI.Call("UI_PromiseTarget","CompleteOneTask", tbParam.id)
/// </code>
/// ⇒ 失败键是 **<c>Err</c>**（首字母大写、**判 nil** 而不是判真值），
/// 成功时**绝不能带**这个键。本项目此前已见 7 种约定，这是**第 8 种**
/// （既不是 <c>nError</c>、不是大写 <c>Error</c>、也不是 <c>err</c>）。
/// ⇒ 再次印证 AGENTS.md §5 那条「**逐条命令确认错误约定，不要整域一刀切**」。
///
/// <para><b>3. 回包形状（逐条取证消费点）</b></para>
/// <list type="bullet">
///   <item><b>232</b> → <c>{OpenGirlList = [id,...]}</c>（<c>GirlPromise.lua:550</c> 取 <c>.OpenGirlList</c> 键）。
///     ★ <c>LuaCall.lua:3023</c> 的原服注释写「返回 <c>[1,2,3]</c>」裸数组，
///     **与真实消费点矛盾** ⇒ 以消费点为准。</item>
///   <item><b>231</b> → **数组**，每项 <c>{girlid, step, cardguid, taskdatas:[{id,count,status}]}</c>
///     （<c>GirlPromise.lua:573</c> <c>GirlList = tbParam</c>）。</item>
///   <item><b>233</b> → **单个少女对象**（同上元素形状）；<c>GirlPromise.lua:598</c> 无保护读 <c>girlid</c>。</item>
///   <item><b>234</b> → <c>{id, count, status}</c>；失败 <c>{Err = 1}</c>。</item>
///   <item><b>235</b> → 单个少女对象；<c>:630</c> 只判 <c>not tbParam</c> 后就重拉 231，
///     ⇒ 内容不被读，但**必须非 nil**。</item>
/// </list>
///
/// <para><b>4. 业务规则</b></para>
/// <list type="bullet">
///   <item>step 0 是「未激活」占位；<c>step0.tasklist[1]</c> 是**激活消耗**任务
///     （实测各少女都是 <c>ReduceItem count=10 gdpl=9-16-1-1</c> = 誓约碎片 ×10）。</item>
///   <item>阶段内任务全部 <c>status=2</c>（已完成）才允许 235 进阶。</item>
///   <item>16 个少女、每人 7~8 个 step、693 条任务、16 种 <c>strtype</c>。</item>
/// </list>
///
/// <para><b>5. ★ 任务进度的实现范围（本轮）</b></para>
/// 693 条任务分 16 种类型（<c>ReduceItem</c> 308 / <c>FriendLevel</c> 97 / <c>CardCount</c> 56 …），
/// 逐类埋点是独立的大工程。本轮实现 <b>234「手动点击完成任务」</b>这条**客户端真实会发**的通道：
/// 玩家在 <c>UI_PromiseTarget</c> 点「完成」，服务端校验并把该任务置为已完成。
/// 这与原厂交互一致（该命令的中文注释就是「手动点击完成任务」）。
/// ★ <c>ReduceItem</c> 类型会**真实扣道具**；其余类型按「点击即达成」处理，
/// 因为它们的进度埋点分散在各玩法里、且客户端不会上报。
/// </summary>
public sealed class PledgeFeature : IGameFeature
{
    /// <summary>231 <c>Pldge_ReqGirls</c>：拥有的誓约少女列表。</summary>
    public const int CmdReqGirls = 231;

    /// <summary>232 <c>Pldge_ReqOpenGirls</c>：开放的誓约少女 id 列表。</summary>
    public const int CmdReqOpenGirls = 232;

    /// <summary>233 <c>Pldge_NewGirl</c>：激活某誓约少女。</summary>
    public const int CmdNewGirl = 233;

    /// <summary>234 <c>Pldge_SetTaskFinish</c>：手动点击完成任务。</summary>
    public const int CmdSetTaskFinish = 234;

    /// <summary>235 <c>Pldge_StepUpGirl</c>：阶段进阶。</summary>
    public const int CmdStepUpGirl = 235;

    /// <summary>存档键（<c>Player.Extra</c>）。</summary>
    public const string ExtraKey = "pledge";

    /// <summary>任务状态：未完成。</summary>
    public const int TaskStatusDoing = 1;

    /// <summary>任务状态：已完成。★ 客户端 <c>UI_PromiseTarget</c> 按此判勾选。</summary>
    public const int TaskStatusDone = 2;

    /// <summary>「消耗道具」类任务的 <c>strtype</c>（唯一会真实扣物品的类型）。</summary>
    public const string TypeReduceItem = "ReduceItem";

    private GameFeatureContext _ctx = null!;
    private PledgeTable _table = null!;

    public string Name => "誓约（LuaCall 231/232/233/234/235）";

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        CmdReqGirls, CmdReqOpenGirls, CmdNewGirl, CmdSetTaskFinish, CmdStepUpGirl,
    };

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        // ★ 231 覆盖 MainCityFeature 的空列表实现（那里只回 []，本域接真实数据）
        ctx.MapLuaCall(CmdReqGirls, OnReqGirls);
        ctx.MapLuaCall(CmdReqOpenGirls, OnReqOpenGirls);
        ctx.MapLuaCall(CmdNewGirl, OnNewGirl);
        ctx.MapLuaCall(CmdSetTaskFinish, OnSetTaskFinish);
        ctx.MapLuaCall(CmdStepUpGirl, OnStepUpGirl);
    }

    // ================================================================ 232

    /// <summary>
    /// 232 开放少女列表 → <c>{OpenGirlList = [id,...]}</c>。
    ///
    /// ★ 名单由**服务端**说了算：客户端本地的 <c>Pldge:GetOpenGirlList()</c> 因
    /// <c>isopen</c> 列是日期字符串而恒返回空（<see cref="PledgeStepRow.OpenTimeRaw"/>），
    /// 但界面读的是本回包写入的 <c>GirlPromise.OpenGirlList</c>（<c>GirlPromise.lua:550</c>）。
    /// </summary>
    internal Task OnReqOpenGirls(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player)
            return Task.CompletedTask;

        var ids = _table.OpenGirlIds(DateTime.Now);
        var arr = new JsonArray();
        foreach (int id in ids)
            arr.Add(id);

        Log.Info($"[session {session.Id}] Pldge_ReqOpenGirls(232) → 开放少女 {ids.Count} 位：" +
                 $"{string.Join(",", ids)}");

        // ★ 必须是「对象包数组」，不是裸数组（GirlPromise.lua:550 取 .OpenGirlList 键）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["OpenGirlList"] = arr });
        return Task.CompletedTask;
    }

    // ================================================================ 231

    /// <summary>
    /// 231 拥有的誓约少女列表 → **数组**，每项 <c>{girlid, step, cardguid, taskdatas}</c>。
    /// </summary>
    internal Task OnReqGirls(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var state = LoadState(player);
        var arr = new JsonArray();
        foreach (var g in state.Girls.Values.OrderBy(g => g.GirlId))
            arr.Add(BuildGirl(g));

        Log.Info($"[session {session.Id}] Pldge_ReqGirls(231) → 已激活 {state.Girls.Count} 位誓约少女" +
                 (state.Girls.Count > 0
                     ? $"（{string.Join(",", state.Girls.Values.OrderBy(g => g.GirlId).Select(g => $"girl{g.GirlId}:step{g.Step}"))}）"
                     : "（新号为空）"));

        _ctx.LuaCalls.CallClient(session, cmd, arr);
        return Task.CompletedTask;
    }

    // ================================================================ 233

    /// <summary>
    /// 233 激活某誓约少女。上行 <c>{girlid}</c>。
    ///
    /// 校验链：少女在开放名单里 → 未激活过 → step0 的激活任务材料够 → 扣材料 → 建档。
    /// ★ 回**单个少女对象**（<c>GirlPromise.lua:599</c> <c>GirlList[girlID] = tbParam</c>）。
    /// </summary>
    internal Task OnNewGirl(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int girlId = ReadInt(param, "girlid");
        if (girlId <= 0)
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) girlid 非法：{girlId}，不回包");
            return Task.CompletedTask;
        }

        if (!_table.OpenGirlIds(DateTime.Now).Contains(girlId))
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) 少女 {girlId} 不在开放名单，拒绝");
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (state.Girls.ContainsKey(girlId))
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) 少女 {girlId} 已激活过，拒绝");
            return Task.CompletedTask;
        }

        // 激活消耗 = step0 的第一个任务（实测都是 ReduceItem 誓约碎片 ×10）
        var actTask = _table.ActivateTask(girlId);
        if (actTask is not null && actTask.StrType == TypeReduceItem && actTask.ReduceGdpl.Count >= 4)
        {
            if (!TryConsume(session, player, actTask, out string why))
            {
                Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) 少女 {girlId} 激活材料不足：{why}，拒绝");
                return Task.CompletedTask;
            }
        }

        var girl = new PledgeGirlState { GirlId = girlId, Step = 1 };
        ResetTasks(girl);
        state.Girls[girlId] = girl;
        SaveState(player, state);

        Log.Info($"[session {session.Id}] Pldge_NewGirl(233) 激活誓约少女 {girlId}" +
                 $"（step=1、任务 {girl.Tasks.Count} 条" +
                 (actTask is not null && actTask.ReduceGdpl.Count >= 4
                     ? $"、消耗 ({string.Join(",", actTask.ReduceGdpl)})×{actTask.Count}"
                     : "") + "）");

        _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
        return Task.CompletedTask;
    }

    // ================================================================ 234

    /// <summary>
    /// 234 手动点击完成任务。上行 <c>{girlid, taskid}</c>。
    ///
    /// ★★★ 错误约定是**第八种**：失败回 <c>{Err = 1}</c>，成功**绝不能带 <c>Err</c>**
    /// （<c>GirlPromise.lua:613</c> <c>if tbParam.Err == nil then</c>）。
    /// 成功回 <c>{id, count, status}</c>（<c>:614</c> 读 <c>tbParam.id</c>）。
    /// </summary>
    internal Task OnSetTaskFinish(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int girlId = ReadInt(param, "girlid");
        int taskId = ReadInt(param, "taskid");

        var state = LoadState(player);
        if (!state.Girls.TryGetValue(girlId, out var girl))
        {
            Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 少女 {girlId} 未激活 → Err");
            Fail(session, cmd);
            return Task.CompletedTask;
        }

        var task = girl.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
        {
            Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 任务 {taskId} 不在少女 {girlId} " +
                     $"当前阶段（step={girl.Step}）的任务列表里 → Err");
            Fail(session, cmd);
            return Task.CompletedTask;
        }

        if (task.Status == TaskStatusDone)
        {
            Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 任务 {taskId} 已完成过 → Err");
            Fail(session, cmd);
            return Task.CompletedTask;
        }

        var cfg = _table.Task(taskId);
        // ★ 只有 ReduceItem 会真实扣道具；其余类型按「点击即达成」（见类注释 §5）
        if (cfg is not null && cfg.StrType == TypeReduceItem && cfg.ReduceGdpl.Count >= 4)
        {
            if (!TryConsume(session, player, cfg, out string why))
            {
                Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 任务 {taskId} 材料不足：{why} → Err");
                Fail(session, cmd);
                return Task.CompletedTask;
            }
        }

        task.Count = cfg?.Count ?? task.Count;
        task.Status = TaskStatusDone;
        SaveState(player, state);

        int done = girl.Tasks.Count(t => t.Status == TaskStatusDone);
        Log.Info($"[session {session.Id}] Pldge_SetTaskFinish(234) 少女 {girlId} 任务 {taskId} " +
                 $"({cfg?.StrType ?? "?"}) 完成 ⇒ 本阶段进度 {done}/{girl.Tasks.Count}");

        // ★ 成功回包绝不带 Err
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["id"] = task.Id,
            ["count"] = task.Count,
            ["status"] = task.Status,
        });
        return Task.CompletedTask;
    }

    // ================================================================ 235

    /// <summary>
    /// 235 阶段进阶。上行 <c>{girlid}</c>。
    ///
    /// 前置：本阶段任务**全部** <c>status=2</c>；且未到最大阶段。
    /// ★ 回包内容不被客户端读（<c>GirlPromise.lua:630</c> 只判非 nil 后重拉 231），
    /// 但**必须非 nil**，否则界面不刷新。
    /// </summary>
    internal Task OnStepUpGirl(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int girlId = ReadInt(param, "girlid");
        var state = LoadState(player);
        if (!state.Girls.TryGetValue(girlId, out var girl))
        {
            Log.Warn($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 未激活，不回包");
            return Task.CompletedTask;
        }

        int unfinished = girl.Tasks.Count(t => t.Status != TaskStatusDone);
        if (unfinished > 0)
        {
            Log.Warn($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 还有 {unfinished} " +
                     $"条任务未完成（step={girl.Step}），拒绝进阶");
            return Task.CompletedTask;
        }

        int max = _table.MaxStep(girlId);
        if (girl.Step >= max)
        {
            Log.Warn($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 已是最高阶段 " +
                     $"{girl.Step}/{max}，拒绝");
            return Task.CompletedTask;
        }

        int from = girl.Step;
        girl.Step++;
        ResetTasks(girl);
        SaveState(player, state);

        var stepCfg = _table.Step(girlId, girl.Step);
        Log.Info($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 进阶 step {from} → {girl.Step}" +
                 $"（新阶段任务 {girl.Tasks.Count} 条" +
                 (stepCfg is not null && stepCfg.CardGdpl.Count >= 4
                     ? $"、阶段卡 ({string.Join(",", stepCfg.CardGdpl)}) lv{stepCfg.CardLv}"
                     : "") + "）");

        _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
        return Task.CompletedTask;
    }

    // ================================================================ 内部

    /// <summary>失败回包：<b>只带 <c>Err</c></b>（第八种错误约定，见类注释 §2）。</summary>
    private void Fail(ClientSession session, int cmd)
        => _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["Err"] = 1 });

    /// <summary>
    /// 造一个誓约少女的回包对象：<c>{girlid, step, cardguid, taskdatas:[{id,count,status}]}</c>
    /// （形状取证 <c>LuaCall.lua:3000</c> 的原服注释 + <c>GirlPromise.lua:573/599</c>）。
    /// </summary>
    private JsonObject BuildGirl(PledgeGirlState g)
    {
        var tasks = new JsonArray();
        foreach (var t in g.Tasks)
        {
            tasks.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["count"] = t.Count,
                ["status"] = t.Status,
            });
        }
        return new JsonObject
        {
            ["girlid"] = g.GirlId,
            ["step"] = g.Step,
            ["cardguid"] = g.CardGuid,
            ["taskdatas"] = tasks,
        };
    }

    /// <summary>把少女的任务列表重置成「当前阶段」的那批（全部未完成）。</summary>
    private void ResetTasks(PledgeGirlState girl)
    {
        girl.Tasks.Clear();
        var cfg = _table.Step(girl.GirlId, girl.Step);
        if (cfg is null)
            return;
        foreach (int tid in cfg.TaskList)
        {
            girl.Tasks.Add(new PledgeTaskState
            {
                Id = tid,
                Count = 0,
                Status = TaskStatusDoing,
            });
        }
    }

    /// <summary>
    /// 扣除 <c>ReduceItem</c> 任务要求的材料。成功 true；不足 false 并给出原因。
    /// ★ 扣完要推 <b>pf 1031</b>（不是 1104）让背包界面刷新（AGENTS.md §5）。
    /// </summary>
    private bool TryConsume(ClientSession session, Player player, PledgeTaskRow cfg, out string why)
    {
        why = "";
        var g = cfg.ReduceGdpl;
        uint gg = (uint)g[0], dd = (uint)g[1], pp = (uint)g[2], ll = (uint)g[3];
        int need = Math.Max(1, cfg.Count);

        int have = player.Items
            .Where(i => i.Genre == gg && i.Detail == dd && i.Particular == pp && i.Level == ll)
            .Sum(i => (int)i.Count);
        if (have < need)
        {
            why = $"({gg},{dd},{pp},{ll}) 需要 {need}、持有 {have}";
            return false;
        }

        int left = need;
        var touched = new List<Item>();
        foreach (var it in player.Items
                     .Where(i => i.Genre == gg && i.Detail == dd && i.Particular == pp && i.Level == ll)
                     .ToList())
        {
            if (left <= 0)
                break;
            int take = Math.Min(left, (int)it.Count);
            it.Count -= (uint)take;
            left -= take;
            touched.Add(it);
        }

        if (touched.Count > 0)
            GameNotify.PushItems(session, touched);
        return true;
    }

    private static int ReadInt(JsonNode? param, string key)
    {
        // ★ JsonNode["k"] 对 JsonArray 会抛（AGENTS.md §5）⇒ 必须先判类型
        if (param is not JsonObject o || o[key] is not JsonValue v)
            return 0;
        if (v.TryGetValue<int>(out int n))
            return n;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out int m) ? m : 0;
    }

    // ---------------- 存档 ----------------

    internal PledgeState LoadState(Player player) => LoadStateStatic(player);

    internal static PledgeState LoadStateStatic(Player player)
    {
        player.Extra.TryGetValue(ExtraKey, out var node);
        return PledgeState.FromJson(node as JsonObject);
    }

    private void SaveState(Player player, PledgeState state)
    {
        player.Extra[ExtraKey] = state.ToJson();
        _ctx.Store.Save(player);
    }
}
