using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// <see cref="GirlLoveTable"/> 的配置表探针：断言四张表被正确解析。
///
/// 存在的理由与 <c>CafeTableProbeTests</c> 同：配置表读错**不抛不报错、只是静默返回 0**
/// （AGENTS.md §5 的表头行数坑），必须用「对着真实表文件的具体数值」把它钉住。
/// 本组用的靶子全部来自 `work/girl-story-forensics.md` 里已取证的实测值。
/// </summary>
public class GirlLoveTableTests
{
    private static GirlLoveTable Load() => GirlLoveTable.Load(TestPaths.SettingDir);

    [Fact]
    public void 经验曲线_普通少女单级增量与实测一致()
    {
        var t = Load();

        // 取证 work/girl-story-forensics.md B.3：1→100、2→105、3→109
        Assert.Equal(100, t.LevelUpExp(1, 1));
        Assert.Equal(105, t.LevelUpExp(1, 2));
        Assert.Equal(109, t.LevelUpExp(1, 3));
    }

    [Fact]
    public void 经验曲线_联动少女走独立列()
    {
        var t = Load();

        // ★★ 第十轮更新：配置表快照换成新版 `setting.u` 后，联动曲线由
        // 「线性 100/200/300」变成 **300/315/330**（与普通曲线同形的递增）。
        // 真机自报印证：`[Z1] link_lv1=300`（旧快照 100）。
        // ⇒ 这不是解析错误，是**配置演进**；旧断言 100/200/300 已作废。
        Assert.Equal(300, t.LevelUpExp(201, 1));
        Assert.Equal(315, t.LevelUpExp(201, 2));
        Assert.Equal(330, t.LevelUpExp(201, 3));
    }

    [Fact]
    public void 联动少女判定按_id_区间()
    {
        Assert.False(GirlLoveTable.IsLinkGirl(1));
        Assert.False(GirlLoveTable.IsLinkGirl(32));
        Assert.False(GirlLoveTable.IsLinkGirl(200));   // 边界：LinkGirlId 本身不算
        Assert.True(GirlLoveTable.IsLinkGirl(201));
        Assert.True(GirlLoveTable.IsLinkGirl(232));
        Assert.False(GirlLoveTable.IsLinkGirl(233));
    }

    [Fact]
    public void 上限_新表清空BanGirl后全员100()
    {
        var t = Load();

        // ★★★ 第十轮更新（与第八轮结论相反，且是**配置变了**而不是谁读错）：
        // 旧快照 `Friendliness.txt` 里 `BanGirl = [13,16]` 从 Level 81 连续到 100
        // ⇒ 少女 13/16 上限 80。**新版把这一列整列清空**（真机自报 `[Z1] ban_levels=0`）
        // ⇒ 全部少女上限都是 100（游戏后期把这两位的好感度开放到满级了）。
        // ⇒ 保留这条测试是为了**钉住「BanGirl 解析仍然在工作」**：
        //    列为空时必须退化成 HardMaxLevel，而不是抛或返回 0。
        Assert.Equal(GirlLoveTable.HardMaxLevel, t.MaxLevel(13));
        Assert.Equal(GirlLoveTable.HardMaxLevel, t.MaxLevel(16));
        Assert.Equal(GirlLoveTable.HardMaxLevel, t.MaxLevel(1));
        Assert.Equal(GirlLoveTable.HardMaxLevel, t.MaxLevel(12));
        Assert.Equal(GirlLoveTable.HardMaxLevel, t.MaxLevel(32));
    }

    [Fact]
    public void 上限_末级经验为0且中间级仍可升()
    {
        var t = Load();

        // ★ 新表 BanGirl 全空 ⇒ 少女 13 在 80 级也还能继续升（旧表这里是 0）
        Assert.True(t.LevelUpExp(13, 80) > 0);
        Assert.True(t.LevelUpExp(1, 80) > 0);
        // 末级 100 的 Exp 列本身是 0（这一条新旧一致，是「封顶」的表达方式）
        Assert.Equal(0, t.LevelUpExp(1, 100));
        Assert.Equal(0, t.LevelUpExp(13, 100));
    }

    [Fact]
    public void 羁绊剧情_少女1的档位含1与10()
    {
        var t = Load();

        var levels = t.FetterLevels(1);
        Assert.Contains(1, levels);
        Assert.Contains(10, levels);
        // 升序
        Assert.Equal(levels.OrderBy(x => x).ToArray(), levels.ToArray());
    }

    [Fact]
    public void 羁绊剧情_主线奖励是逗号分隔的五元组()
    {
        var t = Load();

        // 取证 Fetter.txt 第 4 行：MainAward=[15-2-1-1-30,15-1-1-1-5000,7-1-4-1-3,16-2-1-1-1]
        // Main=[1,...] ⇒ storyIndex = 1
        var story = t.StoryOf(1, 1);
        Assert.NotNull(story);
        Assert.True(story!.IsMain);
        Assert.Equal(1, story.Level);
        Assert.Equal(4, story.Award.Count);
        Assert.Equal(new[] { 15, 2, 1, 1, 30 }, story.Award[0]);
        Assert.Equal(new[] { 15, 1, 1, 1, 5000 }, story.Award[1]);
        Assert.Equal(new[] { 7, 1, 4, 1, 3 }, story.Award[2]);
        Assert.Equal(new[] { 16, 2, 1, 1, 1 }, story.Award[3]);
    }

    [Fact]
    public void 羁绊剧情_第二档主线下标是5()
    {
        var t = Load();

        // Fetter.txt 第 5 行：Id=1 Level=10 Main=[5,...] ⇒ storyIndex 5、门槛 10
        var story = t.StoryOf(1, 5);
        Assert.NotNull(story);
        Assert.Equal(10, story!.Level);
        Assert.True(story.IsMain);
    }

    [Fact]
    public void 羁绊剧情_解析到相当数量的剧情段()
    {
        var t = Load();

        // Fetter.txt 有 192 行数据，每行 1 主线 + 最多 4 支线
        // 只做下界断言（避免把配置行数写死成脆弱断言）
        Assert.True(t.StoryCount >= 192,
            $"剧情段数只解析出 {t.StoryCount}，疑似 Main/Sub 列解析失败");
    }

    [Fact]
    public void 开放档位_少女1的10级已登记()
    {
        var t = Load();

        // FriendlinessUnlock.txt 第 4 行：GirlId=1 AwardIndex=1 Level=10
        Assert.True(t.IsFriendlinessUnlocked(1, 10));
        // 未登记的档位视为未开放（1 级不在该表里）
        Assert.False(t.IsFriendlinessUnlocked(1, 999));
    }

    [Fact]
    public void 喜爱礼物_解析出条目且格式为GDPL()
    {
        var t = Load();

        // ★ 实测 Secret.txt：FitItem 是 **'-' 分隔**（[5-1-1-2]），不是逗号
        // 规律：少女 N 的喜爱礼物 = 5-1-N-2（SecretId 11）与 5-1-N-4（SecretId 12）
        Assert.True(t.LoveGiftCount > 0, "Secret.txt 的 FitItem 一条都没解析出来");
        Assert.True(t.IsLoveGift(1, 5, 1, 1, 2), "少女 1 的低阶喜爱礼物 5-1-1-2 未解析出来");
        Assert.True(t.IsLoveGift(1, 5, 1, 1, 4), "少女 1 的高阶喜爱礼物 5-1-1-4 未解析出来");
        Assert.True(t.IsLoveGift(6, 5, 1, 6, 2), "少女 6 的喜爱礼物 5-1-6-2 未解析出来");
        // 明显不是礼物的组合应为 false
        Assert.False(t.IsLoveGift(1, 1, 1, 1, 1));
        // ★ 别人的喜爱礼物对本人不算「喜爱」
        Assert.False(t.IsLoveGift(1, 5, 1, 2, 2));
    }

    [Fact]
    public void ParseDashArray_吃短横分隔()
    {
        Assert.Equal(new[] { 5, 1, 1, 2 }, GirlLoveTable.ParseDashArray("[5-1-1-2]"));
        Assert.Empty(GirlLoveTable.ParseDashArray(""));
        Assert.Empty(GirlLoveTable.ParseDashArray("[]"));
    }

    [Fact]
    public void 空表_降级不抛()
    {
        var t = GirlLoveTable.Empty;

        Assert.Equal(1, t.MaxLevel(1));
        Assert.Equal(0, t.LevelUpExp(1, 1));
        Assert.Empty(t.FetterLevels(1));
        Assert.Null(t.StoryOf(1, 1));
        Assert.False(t.IsFriendlinessUnlocked(1, 1));
        Assert.False(t.IsLoveGift(1, 5, 1, 1, 2));
    }

    [Fact]
    public void ParseIntArray_吃中括号与空值()
    {
        Assert.Equal(new[] { 13, 16 }, GirlLoveTable.ParseIntArray("[13,16]"));
        Assert.Empty(GirlLoveTable.ParseIntArray(""));
        Assert.Empty(GirlLoveTable.ParseIntArray("[]"));
        Assert.Equal(new[] { 5 }, GirlLoveTable.ParseIntArray("[5]"));
    }

    [Fact]
    public void ParseFetterItem_只取前导整数()
    {
        // ★ 混合数组：遇到第一个非整数就停（服务端只需要第 1 个 index）
        var v = GirlLoveTable.ParseFetterItem(
            "[1,1,Card/girl01_card01_s.png,girl01/girl01_love01,girl01/girl01_love01,girl01_love_sum01,-44.9,-27.6,Card/girl01_card01_s.png]");
        Assert.Equal(new[] { 1, 1 }, v.ToArray());

        Assert.Empty(GirlLoveTable.ParseFetterItem(""));
        Assert.Empty(GirlLoveTable.ParseFetterItem("[]"));
    }
}
