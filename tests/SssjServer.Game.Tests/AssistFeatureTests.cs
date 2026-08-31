using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 助战域（LuaCall 250 <c>AssistList</c>）。台账 `docs/05` §43。
///
/// 断言重点（前两条是「玩家点了出击没反应」的直接原因）：
/// <list type="number">
///   <item>★★★ **任何情况下都必须回包** —— <c>UI_Formation.lua:949</c> 把「开始战斗」
///         的回调存起来等回包，不回就永远点不动出击；</item>
///   <item>★★★ 回包是**数组**，每项的 <c>Card</c> 至少 1 张、每张必带 <c>Skill</c>
///         （<c>UI_Support.lua:135/:327</c> 都无 nil 保护）；</item>
///   <item>★ <c>type=1</c>（好友）必须与好友域实际好友一致，否则客户端
///         <c>UI_Support.lua:30</c> 会把它降级成陌生人；</item>
///   <item>★ 上行是 <c>{ChapterType, Power}</c>，但也可能是别的形状 ⇒ 不能直读键。</item>
/// </list>
/// </summary>
public class AssistFeatureTests
{
    private static Task AssistAsync(FeatureHarness h, string tbParam = "{\"ChapterType\":[1],\"Power\":12345}")
        => h.DispatchLuaCallAsync(AssistFeature.AssistListCmd, tbParam);

    private static JsonArray LastReply(FeatureHarness h)
    {
        var (_, json) = h.LastOutbound;
        return Assert.IsType<JsonArray>(JsonNode.Parse(json)!["tbParam"]);
    }

    /// <summary>
    /// ★★★ 必须回包，且是数组。这条红了就等于「玩家点出击没反应」。
    /// </summary>
    [Fact]
    public async Task 必须回包且是数组()
    {
        var h = await FeatureHarness.CreateAsync(nameof(必须回包且是数组));

        int before = h.Outbound.Count;
        await AssistAsync(h);

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.NotNull(LastReply(h));
    }

    /// <summary>
    /// ★★★ 每项的 <c>Card</c> 必须非空、且每张卡必须带 <c>Skill</c> 数组
    /// 与 <c>G/D/P/L/Enhance/Break/RefitNum/SyncRate</c>
    /// （<c>UI_Support.lua:297..323</c> 逐个无保护地读）。
    /// </summary>
    [Fact]
    public async Task 每项都有非空Card且字段齐全()
    {
        var h = await FeatureHarness.CreateAsync(nameof(每项都有非空Card且字段齐全));

        await AssistAsync(h);
        var arr = LastReply(h);
        Assert.NotEmpty(arr);   // 默认开机器人 ⇒ 应该有候选

        foreach (var node in arr)
        {
            var o = Assert.IsType<JsonObject>(node);

            // 顶层字段（UI_Support.lua:131/:133 读 name/level，:30 读 id/type）
            Assert.True(o.ContainsKey("id"));
            Assert.True(o.ContainsKey("type"));
            Assert.True(o.ContainsKey("name"));
            Assert.True(o.ContainsKey("level"));

            var cards = Assert.IsType<JsonArray>(o["Card"]);
            Assert.NotEmpty(cards);          // ★ :135 Info.Card[1] 无 nil 保护

            foreach (var cn in cards)
            {
                var c = Assert.IsType<JsonObject>(cn);
                foreach (var key in new[] { "G", "D", "P", "L", "Enhance", "Break", "RefitNum", "SyncRate" })
                    Assert.True(c.ContainsKey(key), $"助战卡缺字段 {key}");

                // ★★★ Skill 必须非空数组 —— UI_Support.lua:230 是
                //     `MainCardInfo.Skill[1].id` 直接下标、无 nil 保护。
                //     第十三轮这里只断言 IsType<JsonArray>（空数组也通过），
                //     真机上就抛了 LuaException 并吞掉整段渲染 ⇒ 加严到 NotEmpty。
                var skills = Assert.IsType<JsonArray>(c["Skill"]);
                Assert.NotEmpty(skills);

                // :230 取 [1] 的 .id 与 .level，:237 把 level 塞进 string.format("Lv.%d")
                var first = Assert.IsType<JsonObject>(skills[0]);
                Assert.True(first.ContainsKey("id"), "助战卡技能缺 id");
                Assert.True(first.ContainsKey("level"), "助战卡技能缺 level");
                Assert.True(first["level"]!.GetValue<int>() >= 0);
            }
        }
    }

    /// <summary>
    /// ★★★ 第十四轮真机回归闸：<c>Skill[1]</c> 必须是**这张卡自己的战术技能**。
    ///
    /// <c>UI_Support.lua:230</c> 拿 <c>Skill[1].id</c> 去
    /// <c>SkillDescription.GetName(id)</c> 显示技能名，而
    /// <c>SetSkillLevel</c>（<c>:325</c>）只在 id 命中该卡自身技能时才应用等级
    /// ⇒ 发别的 id 会显示**错误的技能名**且等级覆盖失效。
    /// </summary>
    [Fact]
    public async Task Skill首项必须是该卡自己的战术技能()
    {
        var h = await FeatureHarness.CreateAsync(nameof(Skill首项必须是该卡自己的战术技能));

        await AssistAsync(h);
        var arr = LastReply(h);
        Assert.NotEmpty(arr);

        int checkedCards = 0;
        foreach (var node in arr)
        {
            foreach (var cn in (JsonArray)node!["Card"]!)
            {
                var c = (JsonObject)cn!;
                var tpl = h.Module.CharacterCards.Get(
                    c["G"]!.GetValue<uint>(), c["D"]!.GetValue<uint>(),
                    c["P"]!.GetValue<uint>(), c["L"]!.GetValue<uint>());
                if (tpl is null || tpl.TacticalSkill <= 0)
                    continue;

                int firstId = ((JsonArray)c["Skill"]!)[0]!["id"]!.GetValue<int>();
                Assert.Equal(tpl.TacticalSkill, firstId);
                checkedCards++;
            }
        }

        // 防「一张都没校验到」的假绿：机器人池的卡都来自 CharacterCard.txt
        Assert.True(checkedCards > 0, "没有校验到任何一张有战术技能的卡，断言形同虚设");
    }

    /// <summary>
    /// ★ <c>type</c> 只能是 0/1/2（<c>UI_Support.lua</c> 用它选背景图与
    /// <c>UIText('UI_Support.type'..Info.type)</c> 取文案，别的值会显示成缺失 key）。
    /// </summary>
    [Fact]
    public async Task type字段只能是0或1或2()
    {
        var h = await FeatureHarness.CreateAsync(nameof(type字段只能是0或1或2));

        await AssistAsync(h);

        foreach (var node in LastReply(h))
        {
            int type = (int)node!["type"]!;
            Assert.Contains(type, new[] { AssistFeature.TypeStranger, AssistFeature.TypeFriend, AssistFeature.TypeClub });
        }
    }

    /// <summary>★ 候选数量不超过上限（避免列表过长）。</summary>
    [Fact]
    public async Task 候选数量不超过上限()
    {
        var h = await FeatureHarness.CreateAsync(nameof(候选数量不超过上限));

        await AssistAsync(h);

        Assert.True(LastReply(h).Count <= AssistFeature.MaxCandidates);
    }

    /// <summary>
    /// ★★ 同一账号两次请求结果必须一致（机器人池是确定性派生的）。
    /// 不一致会让玩家每次出击看到不同的「好友」，且与好友域列表打架。
    /// </summary>
    [Fact]
    public async Task 同一账号两次请求结果一致()
    {
        var h = await FeatureHarness.CreateAsync(nameof(同一账号两次请求结果一致));

        await AssistAsync(h);
        string first = LastReply(h).ToJsonString();
        await AssistAsync(h);
        string second = LastReply(h).ToJsonString();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// ★★★ 上行形状可能不是对象（本项目通用坑：<c>JsonNode["key"]</c> 对
    /// <c>JsonArray</c> 会抛）。三种形状都不能崩，且都要回包。
    /// </summary>
    [Theory]
    [InlineData("{\"ChapterType\":[1,2],\"Power\":999}")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("123")]
    public async Task 各种上行形状都不崩且都回包(string tbParam)
    {
        var h = await FeatureHarness.CreateAsync(
            nameof(各种上行形状都不崩且都回包) + tbParam.GetHashCode());

        int before = h.Outbound.Count;
        await AssistAsync(h, tbParam);

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.NotNull(LastReply(h));
    }

    /// <summary>
    /// ★ 关掉机器人（生产环境 <c>--bots false</c>）时列表为空，但**仍然回包** ——
    /// 否则生产环境的玩家会点不动出击（比列表为空严重得多）。
    /// </summary>
    [Fact]
    public async Task 关掉机器人时回空数组但仍然回包()
    {
        var h = await FeatureHarness.CreateAsync(
            nameof(关掉机器人时回空数组但仍然回包), enableBots: false);

        int before = h.Outbound.Count;
        await AssistAsync(h);

        Assert.Equal(before + 1, h.Outbound.Count);
        Assert.Empty(LastReply(h));
    }
}
