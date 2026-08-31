using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 个人资料页（<c>UI_User</c>）的六个命令：个性签名 + 展示角色卡 + 展示武器，各一对 get/set。
///
/// <para><b>0. 真机频次</b></para>
/// 三个 getter 在本轮真机日志里各 <b>13 次 unhandled</b>（`work/unhandled-roadmap.md` 里
/// 排在商店与编队之后），且是**同一个界面**一次打开就全发 —— 打开个人资料页时
/// `UI_User` 会一次性请求签名/展示卡/展示武器三份数据。
///
/// <para><b>1. ★ 回包是「裸数组」，不是对象</b></para>
/// 这一族与本项目其它域都不同：<c>tbParam</c> 本身就是数组，没有外层字段名。
/// 取证（`LuaCall.lua` 的 `MODULE_SERVER` 分支 + `UI_User.lua` 的消费点）：
/// <code>
/// -- LuaCall.lua:1207  cmd 71
/// local tbRet = Player:GetSign(); LuaCall:CallClient(sCmd, tbRet);
/// -- UI_User.lua:1337
/// function tbClass:OnRecvSign(tbParam)
///     if tbParam and #tbParam &gt; 0 then
///         if tbParam[1] == "" then  self.Signature.text = Text("ui.UI_User.DefaultSign")
///         else self.Signature.text = Game.TextFilter.Instance:Filter(tbParam[1]) end
///     end
/// end
///
/// -- UI_User.lua:1368 / :1477（73 / 77 同构）
/// function tbClass:OnRecvExhibitionItem(tbParam)
///     if tbParam then
///         self.ExhibitionItem = {0, 0, 0};                        -- 先兜底成 3 个空位
///         for idx = 1, #tbParam do
///             local card = Game.EquipManager.GetCharacterCard(tbParam[idx])   -- ← 元素是 guid
/// </code>
/// ⇒ 71 回 <c>["签名"]</c>，73 回 <c>[卡guid, …]</c>，77 回 <c>[武器guid, …]</c>，
/// 各最多 3 个（界面就 3 个槽，`{0,0,0}`）。
///
/// <para><b>2. 三个消费点都有 nil 保护 ⇒ 空数组是新号的正解</b></para>
/// `#tbParam &gt; 0` / `if tbParam` + 内部 `{0,0,0}` 兜底 —— 回 <c>[]</c> 甚至回 nil 都不崩。
/// 这在本项目里少见（多数域少个字段就直接抛），所以这一族**没有静默失败风险**。
///
/// <para><b>3. ★ 展示武器现在只能回空数组</b></para>
/// 服务端还不能下发 genre 2 武器（`docs/05` §0.3 的
/// `skillinfo`/`weaponinlay`/`attrlist`/`ownguid` 四字段红线），玩家背包里没有武器实例，
/// 回一个 guid 只会让 `Game.EquipManager.GetWeapon` 返回 nil、那个槽画成空。
/// 所以 <see cref="OnGetExhibitionWeapon"/> 会把存档里的 guid **过滤成实际持有的**，
/// 新号自然就是 <c>[]</c>。等武器域落地后无需改这里。
///
/// <para><b>4. 签名不需要服务端过滤</b></para>
/// 客户端自己过 <c>Game.TextFilter.Instance:Filter</c>（`UI_User.lua:1343`），
/// 服务端只做长度截断（防止存档被塞爆）。
///
/// <para><b>5. 回包不能带 nError</b></para>
/// 这是 LuaCall 数字命令族，客户端 `if tbParam.nError then` 是**真值判断**（0 也为真）——
/// 见 AGENTS.md §5。而且本族回的是数组，压根没有放 nError 的地方。
/// </summary>
public sealed class UserProfileFeature : IGameFeature
{
    /// <summary>LuaCall.Player_GetSign = 71（LuaCall.lua:69「请求个性签名」）。</summary>
    public const int GetSignCmd = 71;

    /// <summary>LuaCall.Player_SetSign = 72（:70「设置个性签名」）。</summary>
    public const int SetSignCmd = 72;

    /// <summary>LuaCall.Player_GetExhibitionItem = 73（:71「请求展示的角色卡」）。</summary>
    public const int GetExhibitionItemCmd = 73;

    /// <summary>LuaCall.Player_SetExhibitionItem = 74（:72「设置展示角色卡」）。</summary>
    public const int SetExhibitionItemCmd = 74;

    /// <summary>LuaCall.Player_GetExhibitionWeapon = 77（:76「请求展示的武器」）。</summary>
    public const int GetExhibitionWeaponCmd = 77;

    /// <summary>LuaCall.Player_SetExhibitionWeapon = 78（:77「设置展示武器」）。</summary>
    public const int SetExhibitionWeaponCmd = 78;

    /// <summary>展示槽位数（<c>UI_User.lua:1370/:1479</c> 的 <c>{0, 0, 0}</c>）。</summary>
    public const int ExhibitionSlots = 3;

    /// <summary>签名长度上限（客户端未取证，服务端自定的存档保护值）。</summary>
    public const int SignMaxLength = 64;

    /// <summary><see cref="Player.Extra"/> 里存个人资料的键。</summary>
    public const string ExtraKey = "profile";

    private GameFeatureContext _ctx = null!;

    public string Name => "个人资料页（签名 71/72、展示角色卡 73/74、展示武器 77/78）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        ctx.MapLuaCall(GetSignCmd, OnGetSign);
        ctx.MapLuaCall(SetSignCmd, OnSetSign);
        ctx.MapLuaCall(GetExhibitionItemCmd, OnGetExhibitionItem);
        ctx.MapLuaCall(SetExhibitionItemCmd, OnSetExhibitionItem);
        ctx.MapLuaCall(GetExhibitionWeaponCmd, OnGetExhibitionWeapon);
        ctx.MapLuaCall(SetExhibitionWeaponCmd, OnSetExhibitionWeapon);
    }

    // ================= 签名 =================

    private Task OnGetSign(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        string sign = ReadSign(player);
        // ★ 裸数组：tbParam[1] 就是签名。空串会让客户端显示 ui.UI_User.DefaultSign
        Reply(session, cmd, new JsonArray { sign });
        Log.Info($"[session {session.Id}] Player_GetSign → \"{sign}\"（{sign.Length} 字）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 设置签名。上行 <c>tbParam</c> 实测是数组（与 getter 对称，客户端
    /// <c>UI_ChangeSignature</c> 发的就是 <c>{新签名}</c>），这里两种形状都吃。
    /// </summary>
    private Task OnSetSign(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        string sign = param switch
        {
            JsonArray arr when arr.Count > 0 => arr[0]?.ToString() ?? "",
            JsonValue v => v.ToString(),
            JsonObject o => o["sign"]?.ToString() ?? o["szSign"]?.ToString() ?? "",
            _ => "",
        };
        if (sign.Length > SignMaxLength)
            sign = sign[..SignMaxLength];

        WriteProfile(player, p => p["sign"] = sign);
        _ctx.Store.Save(player);

        // 回执同样是裸数组（客户端 UI_User:OnRecvSetSign 只用来关弹窗）
        Reply(session, cmd, new JsonArray { sign });
        Log.Info($"[session {session.Id}] Player_SetSign → \"{sign}\"");
        return Task.CompletedTask;
    }

    // ================= 展示角色卡 =================

    private Task OnGetExhibitionItem(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var guids = ReadSlots(player, "cards")
            // 只回**实际持有**的卡：客户端 GetCharacterCard 拿不到就把槽画空，
            // 回一个已经不存在的 guid 等于白占一个槽
            .Where(g => player.Items.Any(i => i.Guid == g && i.Genre == 1))
            .Take(ExhibitionSlots)
            .ToList();

        Reply(session, cmd, ToArray(guids));
        Log.Info($"[session {session.Id}] Player_GetExhibitionItem → [{string.Join(",", guids)}]");
        return Task.CompletedTask;
    }

    private Task OnSetExhibitionItem(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var guids = ParseGuids(param).Take(ExhibitionSlots).ToList();
        WriteProfile(player, p => p["cards"] = ToArray(guids));
        _ctx.Store.Save(player);

        Reply(session, cmd, ToArray(guids));
        Log.Info($"[session {session.Id}] Player_SetExhibitionItem → [{string.Join(",", guids)}]");
        return Task.CompletedTask;
    }

    // ================= 展示武器 =================

    private Task OnGetExhibitionWeapon(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★ 过滤成实际持有的 genre 2 武器。服务端目前不下发武器（docs/05 §0.3），
        // 所以新号这里必然是空数组 —— 那正是客户端期望的「三个空槽」。
        var guids = ReadSlots(player, "weapons")
            .Where(g => player.Items.Any(i => i.Guid == g && i.Genre == 2))
            .Take(ExhibitionSlots)
            .ToList();

        Reply(session, cmd, ToArray(guids));
        Log.Info($"[session {session.Id}] Player_GetExhibitionWeapon → [{string.Join(",", guids)}]" +
                 (guids.Count == 0 ? "（服务端还不下发 genre 2 武器，空是预期）" : ""));
        return Task.CompletedTask;
    }

    private Task OnSetExhibitionWeapon(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var guids = ParseGuids(param).Take(ExhibitionSlots).ToList();
        WriteProfile(player, p => p["weapons"] = ToArray(guids));
        _ctx.Store.Save(player);

        Reply(session, cmd, ToArray(guids));
        Log.Info($"[session {session.Id}] Player_SetExhibitionWeapon → [{string.Join(",", guids)}]");
        return Task.CompletedTask;
    }

    // ================= 存档 / 工具 =================

    /// <summary>读签名（缺省空串 ⇒ 客户端显示默认签名）。</summary>
    internal static string ReadSign(Player player)
        => player.Extra.TryGetValue(ExtraKey, out var node) && node is JsonObject o
            ? o["sign"]?.ToString() ?? ""
            : "";

    /// <summary>读某个展示槽的 guid 列表（缺省空）。</summary>
    internal static List<uint> ReadSlots(Player player, string key)
    {
        var result = new List<uint>();
        if (player.Extra.TryGetValue(ExtraKey, out var node) && node is JsonObject o
            && o[key] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is null)
                    continue;
                if (uint.TryParse(item.ToString(), out var g) && g > 0)
                    result.Add(g);
            }
        }
        return result;
    }

    /// <summary>就地改 <c>Extra["profile"]</c>（不存在就建）。</summary>
    private static void WriteProfile(Player player, Action<JsonObject> mutate)
    {
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is not JsonObject obj)
        {
            obj = new JsonObject();
            player.Extra[ExtraKey] = obj;
        }
        mutate(obj);
    }

    /// <summary>从上行解析 guid 列表：既吃裸数组，也吃 <c>{tbItems:[…]}</c> 之类的包装。</summary>
    internal static List<uint> ParseGuids(JsonNode? param)
    {
        var result = new List<uint>();
        JsonArray? arr = param as JsonArray;
        if (arr is null && param is JsonObject o)
        {
            foreach (var key in new[] { "tbItems", "items", "list", "guids" })
            {
                if (o[key] is JsonArray a)
                {
                    arr = a;
                    break;
                }
            }
        }
        if (arr is null)
            return result;

        foreach (var item in arr)
        {
            if (item is null)
                continue;
            if (uint.TryParse(item.ToString(), out var g) && g > 0)
                result.Add(g);
        }
        return result;
    }

    private static JsonArray ToArray(IEnumerable<uint> values)
    {
        var arr = new JsonArray();
        foreach (var v in values)
            arr.Add(v);
        return arr;
    }

    /// <summary>回一条 LuaCall 下行。★ 本族的 tbParam 是**裸数组**，不能包成对象。</summary>
    private void Reply(ClientSession session, int cmd, JsonArray payload)
        => _ctx.LuaCalls.CallClient(session, cmd, payload);
}
