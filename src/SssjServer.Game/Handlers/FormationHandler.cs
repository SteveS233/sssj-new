using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 编队保存 <c>LuaCall.Formation_Update = 21</c>（请求更新编队配置）。
///
/// **这是「出击」按钮的第一道服务器闸门**：客户端 <c>UI_Formation:ApplySave</c>
/// （UI_Formation.lua:920）发现本地编队与服务器版本不一致时，会
/// <code>LuaCall:CallServer(LuaCall.Formation_Update, {Id = nTeamId, Info = tb})</code>
/// 然后**等服务器回包**才继续 —— 回包触发 <c>UI_Formation:OnUpdateEnd</c>（lua:1011）
/// → <c>pSaveEnd()</c> → <c>CheckPower</c> → … → <c>EnterChapter()</c> → ChapterMsg 进关。
/// 服务端不回，出击按钮就“点了没反应”（实测：客户端会隔 1~2 秒重发 21 号命令）。
///
/// 上行 tbParam（真实抓包）：
/// <code>
/// {"Id":1,"Info":[{"MainCard":1,"UsedCard":1,"Secondarycard":[],"WeaponId":0,"Rune":[0,0,0,0]}, ...]}
/// </code>
/// 字段对应 proto 的 <c>FightCard{maincardguid, secondarycardguid[], usedcardguid, weaponguid, runeitem[]}</c>。
///
/// 下行：<c>LuaCall:CallClient(21, tbRet)</c>，客户端只取 <c>tbRet.ret</c> 传给 OnUpdateEnd
/// （而 OnUpdateEnd 连参数都没用），所以内容只需存在即可；这里回 <c>{ret=0}</c>。
/// 另外补推 pf 1033 <c>FormationInfoUpdateNtf</c>，让客户端 <c>Game.EquipManager</c> 的
/// 服务器版本与本地一致，避免下次出击又发一遍 21。
/// </summary>
public sealed class FormationHandler
{
    /// <summary>LuaCall.Formation_Update = 21（LuaCall.lua:26）。</summary>
    public const int UpdateCmd = 21;

    /// <summary>LuaCall.Formation_Change_Name = 24（LuaCall.lua:29，修改编队名字）。</summary>
    public const int ChangeNameCmd = 24;

    private readonly PlayerStore _store;
    private readonly LuaCallRouter _luaCalls;

    public FormationHandler(PlayerStore store, LuaCallRouter luaCalls)
    {
        _store = store;
        _luaCalls = luaCalls;
    }

    /// <summary>LuaCall 21：保存编队并回包。</summary>
    public Task OnUpdate(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Formation_Update 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        uint id = (uint)ReadInt(param?["Id"], 1);
        var slots = ParseSlots(param?["Info"]);

        var formation = player.Formations.FirstOrDefault(f => f.Id == id);
        if (formation is null)
        {
            formation = new Formation { Id = id };
            player.Formations.Add(formation);
        }
        formation.Slots.Clear();
        foreach (var s in slots)
            formation.Slots.Add(s);

        _store.Save(player);
        Log.Info($"[session {session.Id}] 编队保存 team={id} 槽位={slots.Count} " +
                 $"[{string.Join(",", slots.Select(s => s.MainCardGuid))}]");

        // 同步服务器版本（客户端 Game.EquipManager.UpdateFormationNtf）
        session.Push(GamePf.FormationInfoUpdateNtf, new FormationInfoUpdateNtf
        {
            Info = BuildFormationInfo(formation),
        });

        // 回执：客户端 UI_Formation:OnUpdateEnd → pSaveEnd() 继续出击流程。
        // ★ 必须回**原样的 cmd**，不能硬编码 21：客户端是 `LuaCall:CallClient(sCmd, tbRet)`
        // （LuaCall.lua:763 / :779），22/23 复用同一个服务端函数但要刷不同界面
        // （21→UI_Formation、22→UI_ChooseEquip、23→UI_ChooseGirl+UI_BattleMain2）。
        // 回错了的表现是「在选武器/选卡界面点确定，界面不关也不刷新」。
        // 见 FormationExtraFeature。
        _luaCalls.CallClient(session, cmd, new { ret = 0, Id = id });
        return Task.CompletedTask;
    }

    /// <summary>LuaCall 24：改编队名字。tbParam = {Id, Name}（客户端只要回执）。</summary>
    public Task OnChangeName(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        uint id = (uint)ReadInt(param?["Id"], 1);
        string name = param?["Name"]?.ToString() ?? param?["name"]?.ToString() ?? "";
        var formation = player.Formations.FirstOrDefault(f => f.Id == id);
        if (formation is not null)
        {
            formation.Title = name;
            _store.Save(player);
        }
        Log.Info($"[session {session.Id}] 编队改名 team={id} → {name}");

        _luaCalls.CallClient(session, ChangeNameCmd, new { ret = 0, Id = id, Name = name });
        return Task.CompletedTask;
    }

    /// <summary>把 Player.Formation 转成 proto 的 FormationInfo（含副卡/武器/符文槽）。</summary>
    public static FormationInfo BuildFormationInfo(Formation formation)
    {
        var fi = new FormationInfo { Id = formation.Id, Title = formation.Title };
        foreach (var slot in formation.Slots)
        {
            var card = new FightCard
            {
                Maincardguid = slot.MainCardGuid,
                Usedcardguid = slot.UsedCardGuid,
                Weaponguid = slot.WeaponGuid,
            };
            foreach (var g in slot.SecondaryCardGuids)
                card.Secondarycardguid.Add(g);
            foreach (var r in slot.RuneItems)
                card.Runeitem.Add(r);
            fi.Fightcard.Add(card);
        }
        return fi;
    }

    /// <summary>解析上行 Info 数组为编队槽位。</summary>
    internal static List<FormationCardSlot> ParseSlots(JsonNode? info)
    {
        var result = new List<FormationCardSlot>();
        if (info is not JsonArray arr)
            return result;

        foreach (var node in arr)
        {
            if (node is null)
                continue;
            var slot = new FormationCardSlot
            {
                MainCardGuid = (uint)ReadInt(node["MainCard"], 0),
                UsedCardGuid = (uint)ReadInt(node["UsedCard"], 0),
                WeaponGuid = (uint)ReadInt(node["WeaponId"], 0),
            };
            if (node["Secondarycard"] is JsonArray sec)
                foreach (var s in sec)
                    slot.SecondaryCardGuids.Add((uint)ReadInt(s, 0));
            if (node["Rune"] is JsonArray runes)
                foreach (var r in runes)
                    slot.RuneItems.Add((uint)ReadInt(r, 0));
            result.Add(slot);
        }
        return result;
    }

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var d) ? (int)d : fallback;
        }
    }
}
