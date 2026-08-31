using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 手机短信 <c>sFunction="PhoneMsg"</c>（S2CCall.lua 那一族字符串命令），本次只做 nCmd=8。
///
/// 最关键的一条断言：**下行命令名是 <c>ServerPhoneMsg</c> 而不是 <c>PhoneMsg</c>**。
/// 取证：<c>S2CCall.lua:245 function S2C.ServerPhoneMsg(tbParam)</c>，
/// 且全仓 806 个 .lua 里没有任何 <c>S2C.PhoneMsg</c>；若按 "PhoneMsg" 回，客户端
/// <c>S2C.ProcCmd</c> 的 <c>type(S2C[sCmd]) ~= 'function'</c> 分支（S2CCall.lua:11）会直接丢弃。
/// </summary>
public class PhoneMsgFeatureTests
{
    /// <summary>上行名 ≠ 下行名，且下行名必须是 S2CCall.lua 里真实存在的那个。</summary>
    [Fact]
    public void FunctionNames_UpIsPhoneMsg_DownIsServerPhoneMsg()
    {
        Assert.Equal("PhoneMsg", PhoneMsgFeature.UpFunction);
        Assert.Equal("ServerPhoneMsg", PhoneMsgFeature.DownFunction);
        Assert.NotEqual(PhoneMsgFeature.UpFunction, PhoneMsgFeature.DownFunction);
    }

    /// <summary>
    /// nCmd=8 回包字段：<c>nError</c>（S2CCall.lua:247 直接和 0 比大小，nil 会抛
    /// 「attempt to compare nil with number」）、<c>nCmd</c>（S2CCall.lua:297 分发）、
    /// <c>tbList</c>（S2CCall.lua:298 → PhoneMsgLogic.lua:1816 整体覆盖 tbBBSApplyList）。
    /// </summary>
    [Fact]
    public void BuildBbsApplyListPayload_KeySet_AndSuccessCode()
    {
        var payload = PhoneMsgFeature.BuildBbsApplyListPayload(Array.Empty<int>());

        Assert.Equal(new[] { "nError", "nCmd", "tbList" }, payload.Select(kv => kv.Key));
        Assert.Equal(0, payload["nError"]!.GetValue<int>());   // 0 = 成功；>0 会弹 ui.Error.<n>
        Assert.Equal(8, payload["nCmd"]!.GetValue<int>());
        Assert.Equal(PhoneMsgFeature.CmdBbsApplyList, payload["nCmd"]!.GetValue<int>());
    }

    /// <summary>
    /// 新号 → <c>tbList</c> 是**空数组**而不是缺省/null：
    /// <c>ServerResponseAddBBSApply</c>（PhoneMsgLogic.lua:1822）会直接
    /// <c>table.insert(self.tbBBSApplyList, nMsgId)</c>，表不存在会抛。
    /// </summary>
    [Fact]
    public void BuildBbsApplyListPayload_EmptyList_IsEmptyJsonArray()
    {
        var arr = Assert.IsType<JsonArray>(PhoneMsgFeature.BuildBbsApplyListPayload(Array.Empty<int>())["tbList"]);
        Assert.Empty(arr);
    }

    /// <summary>
    /// <c>tbList</c> 是**扁平的帖子 id 数字数组**（不是对象数组）：
    /// 消费处 <c>for i,v in ipairs(PhoneMsgLogic.tbBBSApplyList) do ... GetBBSInfo(v)</c>
    /// （UI_Phone.lua:1747..1750）把元素本身当配置 id 用。
    /// </summary>
    [Fact]
    public void BuildBbsApplyListPayload_IsFlatNumberArray_InOrder()
    {
        var arr = Assert.IsType<JsonArray>(
            PhoneMsgFeature.BuildBbsApplyListPayload(new[] { 1001, 1002, 2003 })["tbList"]);

        Assert.Equal(3, arr.Count);
        Assert.Equal(new[] { 1001, 1002, 2003 }, arr.Select(n => n!.GetValue<int>()));
    }

    /// <summary>
    /// 锁死上线的 wire 形态。这就是 pf 1024 <c>NtfS2CCall.sJsonParam</c> 的完整原文
    /// （sFunction = "ServerPhoneMsg"，无外壳、不分片）。
    /// </summary>
    [Fact]
    public void BbsApplyListPayload_ExactWireJson()
    {
        Assert.Equal("""{"nError":0,"nCmd":8,"tbList":[]}""",
            PhoneMsgFeature.BuildBbsApplyListPayload(Array.Empty<int>()).ToJsonString());

        Assert.Equal("""{"nError":0,"nCmd":8,"tbList":[11,22]}""",
            PhoneMsgFeature.BuildBbsApplyListPayload(new[] { 11, 22 }).ToJsonString());
    }

    // ---- Player.Extra["phonemsg"] ----

    /// <summary>新号没有 Extra["phonemsg"] → 空列表（不抛）。</summary>
    [Fact]
    public void ReadBbsApplyList_FreshPlayer_IsEmpty()
    {
        Assert.Empty(PhoneMsgFeature.ReadBbsApplyList(new Player("acc", 1)));
    }

    /// <summary>Extra 里有列表时按序读出；非法/非正数元素被丢弃（帖子 id 从 1 起）。</summary>
    [Fact]
    public void ReadBbsApplyList_ReadsFromExtra_AndSkipsInvalid()
    {
        var player = new Player("acc", 1);
        player.Extra[PhoneMsgFeature.ExtraKey] = new JsonObject
        {
            [PhoneMsgFeature.BbsApplyField] = new JsonArray(5, 0, 7, -1, 9),
        };

        Assert.Equal(new[] { 5, 7, 9 }, PhoneMsgFeature.ReadBbsApplyList(player));
    }

    /// <summary>Extra["phonemsg"] 结构异常（不是对象 / 字段不是数组）时安全返回空。</summary>
    [Fact]
    public void ReadBbsApplyList_MalformedExtra_IsEmpty()
    {
        var p1 = new Player("a", 1);
        p1.Extra[PhoneMsgFeature.ExtraKey] = JsonValue.Create(42);
        Assert.Empty(PhoneMsgFeature.ReadBbsApplyList(p1));

        var p2 = new Player("b", 2);
        p2.Extra[PhoneMsgFeature.ExtraKey] = new JsonObject { [PhoneMsgFeature.BbsApplyField] = 7 };
        Assert.Empty(PhoneMsgFeature.ReadBbsApplyList(p2));
    }

    /// <summary>子命令名表覆盖 PhoneMsgLogic.lua:34..43 的全部 TYPE_* 常量（日志可读性）。</summary>
    [Fact]
    public void SubCmdName_CoversKnownTypes()
    {
        Assert.Contains("TYPE_BBSAPPLYLIST", PhoneMsgFeature.SubCmdName(8));
        Assert.Contains("TYPE_LETTERTITLE", PhoneMsgFeature.SubCmdName(4));
        Assert.Contains("TYPE_BBSEVENT", PhoneMsgFeature.SubCmdName(13));
        Assert.Equal("未知", PhoneMsgFeature.SubCmdName(999));
    }

    // ---- 端到端：GameModule 反射装配 → sFunction 分发 → pf 1024 ----

    /// <summary>
    /// 实测上行 <c>{"nCmd":8}</c>（PhoneMsgLogic.lua:1799..1804，tbParam 只有 nCmd 一个字段）
    /// → 回包 pf 1024 <c>sFunction="ServerPhoneMsg"</c>，<c>sJsonParam</c> 就是 tbParam 本体
    /// （这一族**没有** sCmd/tbParam 外壳，也**不分片**）。
    /// </summary>
    [Fact]
    public async Task PhoneMsg_Cmd8_RepliesOnServerPhoneMsg_WithFlatParam()
    {
        using var h = await LuaCallHarness.CreateAsync("phone8");

        await h.DispatchFunctionAsync(PhoneMsgFeature.UpFunction, """{"nCmd":8}""");

        var (function, json) = Assert.Single(h.Outbound);
        Assert.Equal("ServerPhoneMsg", function);
        Assert.NotEqual("PhoneMsg", function);
        Assert.NotEqual("LuaCall", function);   // 不能走数字命令族，否则会被当分片处理

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Null(root["sCmd"]);              // 无外壳
        Assert.Null(root["tbParam"]);
        Assert.Equal(0, root["nError"]!.GetValue<int>());
        Assert.Equal(8, root["nCmd"]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(root["tbList"]));
    }

    /// <summary>
    /// ★★★ 第九轮：**除 nCmd 8 之外的子命令已全部实现** ——
    /// 本测试原名 <c>PhoneMsg_UnimplementedSubCmd_DoesNotReply</c>、断言「一律不回包」，
    /// 实现后它是第一个变红的测试，这正是回归闸该做的事。
    ///
    /// 现在的契约（详见 <see cref="PhoneMsgFeature"/> 与 `docs/05` §35）：
    /// <list type="bullet">
    ///   <item>已实现的子命令（4 改标题 / 12 剧情触发私信 …）**必须回包**
    ///     —— 那些发起点大多挂着 <c>Game.Blocking</c> 遮罩；</item>
    ///   <item><b>真正未知</b>的 nCmd（空对象 ⇒ nCmd = -1）也要回包（带 <c>nError &gt; 0</c>），
    ///     否则遮罩要等超时；</item>
    ///   <item>每个回包都必须带**数值** <c>nError</c>
    ///     （`S2CCall.lua:247` 无条件与 0 比大小，nil 会抛）。</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task PhoneMsg_EverySubCmd_Replies_WithNumericNError()
    {
        using var h = await LuaCallHarness.CreateAsync("phone_other");

        // nCmd 4 改标题：npc 1 本来没有私信，服务端要能建出来并回包
        await h.DispatchFunctionAsync(PhoneMsgFeature.UpFunction,
            """{"nCmd":4,"nNpcId":1,"strTitle":"x"}""");
        // nCmd 12 剧情触发：id=9 在 LetterInfoNew 里没有对应话题（孤儿配置）⇒ 也要回包
        await h.DispatchFunctionAsync(PhoneMsgFeature.UpFunction, """{"nCmd":12,"nMsgId":9}""");
        // 真正未知的 nCmd（空对象 ⇒ -1）
        await h.DispatchFunctionAsync(PhoneMsgFeature.UpFunction, """{}""");

        // 三条都必须有回包，且都带可读为数字的 nError
        Assert.Equal(3, h.Outbound.Count);
        foreach (var (function, json) in h.Outbound)
        {
            Assert.Equal(PhoneMsgFeature.DownFunction, function);
            var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
            Assert.NotNull(root["nError"]);
            _ = root["nError"]!.GetValue<int>();
        }

        // 未知 nCmd 那条必须是失败码
        var last = Assert.IsType<JsonObject>(JsonNode.Parse(h.Outbound[^1].Json));
        Assert.True(last["nError"]!.GetValue<int>() > 0);
    }

    /// <summary>未登录（Tag 非 Player）时只记日志，不回包也不抛。</summary>
    [Fact]
    public async Task PhoneMsg_WithoutLogin_DoesNotReply()
    {
        using var h = await LuaCallHarness.CreateAsync("phone_nologin", attachPlayer: false);

        await h.DispatchFunctionAsync(PhoneMsgFeature.UpFunction, """{"nCmd":8}""");

        Assert.Empty(h.Outbound);
    }

    /// <summary>
    /// Extra 里已有可发表列表时，回包应带上（验证 nCmd=9「新增可发表」落地后
    /// 这条命令能把存量正确回放给客户端）。
    /// </summary>
    [Fact]
    public async Task PhoneMsg_Cmd8_ReplaysStoredList()
    {
        using var h = await LuaCallHarness.CreateAsync("phone_stored");
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = new JsonObject
        {
            [PhoneMsgFeature.BbsApplyField] = new JsonArray(11, 22),
        };

        await h.DispatchFunctionAsync(PhoneMsgFeature.UpFunction, """{"nCmd":8}""");

        var (_, json) = Assert.Single(h.Outbound);
        var arr = Assert.IsType<JsonArray>(JsonNode.Parse(json)!["tbList"]);
        Assert.Equal(new[] { 11, 22 }, arr.Select(n => n!.GetValue<int>()));
    }
}
