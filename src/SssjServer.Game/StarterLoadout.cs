namespace SssjServer.Game;

/// <summary>
/// 新建角色（首次建号）赠送的起始角色卡小队 + 默认编队。
///
/// 卡片是 inventory item：genre=1（emGenreType.Card），detail=少女 id，particular=变体，level=阶。
/// 这里只放“赠几张、哪张”这一组常量，方便一处改；<see cref="Grant"/> 负责把卡落成
/// Player.Items（Item 实例）+ Player.Girls（GirlInfo）+ Player.Formations（1 号编队）。
/// 仅在首次建号时调用（World.NewPlayer 在 ServerOptions.GrantStarter 开启时调用）；
/// 从磁盘加载的玩家不经过这里，其存盘数据优先。
/// </summary>
public static class StarterLoadout
{
    /// <summary>赠卡数量。当前 4 张（与 <see cref="Cards"/> 一一对应）。</summary>
    public const int SquadSize = 4;

    /// <summary>
    /// ★★★ 一个编队最多能有几个 <c>FightCard</c> 槽：**3**（客户端硬上限，不是我们的选择）。
    ///
    /// <para>取证（本轮真机 NRE 反查）：<c>Game.EquipSolution$$.ctor</c>（RVA <c>0x1D92498</c>）
    /// 的最后一句是 <c>mov w1, #3</c> + <c>b Game.EquipSolution$$SetGroupSize</c>
    /// ⇒ 新建的 <c>EquipSolution</c> 恒有 **3** 个 <c>CardGroup</c>。
    /// 而 <c>Game.EquipSolution$$GetGroup</c>（RVA <c>0x1D92534</c>）是
    /// <c>cmp Count, groupnum; b.le → mov x0, xzr</c>
    /// —— <c>groupnum >= Count</c> 时**返回 null 而不抛**；
    /// 回到 <c>Game.EquipManager$$AddEquipSolution</c>（RVA <c>0x1D91FF8</c>）
    /// 的 <c>0x1D92270 cbnz x22</c> 处才对这个 null **抛 NullReferenceException**。</para>
    ///
    /// <para>★ 后果比看起来严重：<c>AddEquipSolution</c> 是 <c>Game.EquipManager.InitItem</c>
    /// （pf <b>1104</b> 背包**全量**）的一环 ⇒ 发第 4 个槽会让**整条 1104 中断**，
    /// 客户端打 <c>Failed to process message. PF : 1104</c>。</para>
    ///
    /// <para>★★ 这个 bug 只有**新账号首登**才会触发（老存档的编队是客户端经
    /// <c>ParseSlots</c> 回写的 3 槽），所以本地长期 e2e 一直没暴露 ——
    /// 而「每个朋友都是新账号」正好全中。</para>
    /// </summary>
    public const int MaxFormationSlots = 3;

    /// <summary>
    /// 起始卡片（Detail=少女 id, Particular=变体, Level=阶）。
    ///
    /// 每个元组都来自 extracted/setting/CardRefit.txt（表头：G - 大类角色卡为1 / D - 少女id / P / L），
    /// 选取表中可用 Detail（少女 id）最小的 4 位（最早/基础少女），每一行如下：
    ///   (1, 1, 3) 私服·洛可可      ← CardRefit.txt 第 10 行
    ///   (2, 1, 3) 私服·高奈利亚    ← CardRefit.txt 第 4 行
    ///   (3, 1, 3) 私服·朱诺        ← CardRefit.txt 第 12 行
    ///   (4, 7, 3) 战术装备·沐恩    ← CardRefit.txt 第 15 行
    /// genre 恒为 1（角色卡）。
    /// </summary>
    public static readonly (uint Detail, uint Particular, uint Level)[] Cards =
    {
        (1, 1, 3),
        (2, 1, 3),
        (3, 1, 3),
        (4, 7, 3),
    };

    /// <summary>
    /// 把起始卡落成 Player 的 Items + Girls，并建 1 号编队。
    /// 第 1 张卡是队长（maincardguid == usedcardguid），其余卡作为额外 FightCard 槽。
    /// </summary>
    public static void Grant(Player player)
    {
        if (player.Items.Count > 0)
            return; // 已有背包时不再重复赠送（防御）

        for (int i = 0; i < Cards.Length; i++)
        {
            var (detail, particular, level) = Cards[i];
            var item = player.AddItem(genre: 1, detail, particular, level, count: 1);
            // 对应 GirlInfo：level=1（角色等级），modelid=少女 id，mood/vigor 取正常非零值，flag=1
            player.Girls.Add(new Girl
            {
                Girlid = detail,
                Level = 1,
                Modelid = detail,
                Moodvalue = 100,
                Vigor = 100,
                Flag = 1,
            });
        }

        // 1 号编队：队长 = 第 1 张卡实例 guid，其余卡各占一个 FightCard 槽
        // ★ 最多只放 MaxFormationSlots(=3) 个槽 —— 客户端 EquipSolution 恒有 3 个 CardGroup，
        //   多发一个就会让 pf 1104 整条中断（取证见 MaxFormationSlots 注释）。
        //   赠卡是 4 张，第 4 张留在背包里当备用卡，不上阵。
        var formation = new Formation { Id = 1, Title = "" };
        foreach (var item in player.Items)
        {
            if (formation.Slots.Count >= MaxFormationSlots)
                break;
            formation.Slots.Add(new FormationCardSlot
            {
                MainCardGuid = item.Guid,
                UsedCardGuid = item.Guid,
            });
        }
        player.Formations.Add(formation);
    }
}
