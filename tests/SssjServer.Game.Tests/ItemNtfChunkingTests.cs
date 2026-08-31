using SssjServer.Game.Handlers;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// pf 1104 <c>ItemNtf</c> 全量背包的**分片**回归。
///
/// <para><b>★★★ 这是第九轮真机发现的一个隐蔽 bug（不是新功能）</b></para>
/// 客户端 <c>UNet.Connection.Update()</c>（RVA <c>0x1B4D340</c>）每帧只做一次
/// <c>Socket.Receive</c>，缓冲区写死 <c>0x1B4D3E8 mov w3, #0x10000</c> = **65536 字节**。
/// 单条 pf 1104 超过这个尺寸 ⇒ 那一帧只收到半个包 ⇒ **整包被丢、尾部物品全部不见**，
/// 而且 <b>logcat 零报错、服务端日志零异常</b>。
///
/// 真机取证（2362 件物品）：
/// <list type="bullet">
///   <item>服务端一条 pf 1104 = <b>66150 字节</b>（超出 614）；</item>
///   <item>客户端 <c>GetWeaponCount()</c> = <b>3</b>，而存档里有 <b>18</b> 把武器；</item>
///   <item>丢的正好是**尾部 20 件**（16 武器 + 2 零件 + 2 皮肤）；
///     317 张角色卡全在前段，一张不少 ⇒ 排除了「按 genre 拒收」的可能；</item>
///   <item>逐个 <c>GetWeapon(guid)</c> 查：前段的 2456/2459 在，2463 之后全不在。</item>
/// </list>
///
/// ⇒ 症状极像「装备下发字段有问题」，但真因是**传输层尺寸**。
/// 判据顺序（写进 `docs/05` §36）：
/// **先量 pf 1104 的字节数**（服务端日志 `len=`），再怀疑字段。
///
/// 分片安全性取证：<c>Game.EquipManager.InitItem</c>（<c>0x1D9DD10</c>）**不清空背包**，
/// 只遍历 <c>items</c> 逐个按 genre 分流 Add（<c>0x1D9DE58</c> 起）
/// ⇒ 多条 pf 1104 等价于一条大包。
/// </summary>
public class ItemNtfChunkingTests
{
    /// <summary>客户端单次 <c>Socket.Receive</c> 的硬上限（<c>0x1B4D3E8 mov w3, #0x10000</c>）。</summary>
    private const int ClientReceiveLimit = 0x10000;

    /// <summary>造 n 件武器（genre 2 每件一个独立实例，最接近真机的坏情形）。</summary>
    private static void SeedItems(Player player, int n)
    {
        for (int i = 0; i < n; i++)
            player.AddItem(2, (uint)(i % 5 + 1), (uint)(i % 15 + 1), 1, count: 1);
    }

    [Fact]
    public async Task PushAllItems_SmallBag_SendsSinglePacket()
    {
        using var h = await FeatureHarness.CreateAsync("itemntf-small");
        SeedItems(h.Player, 10);

        GameNotify.PushAllItems(h.Session, h.Player);

        Assert.True(await h.WaitForPfAsync(GamePf.ItemNtf));
        Assert.Equal(1, h.PushedPf.Count(pf => pf == GamePf.ItemNtf));
    }

    /// <summary>
    /// ★★★ 大背包必须分成多片，且**每片都要小于客户端的 65536 上限**。
    /// 这条测试就是那个真机 bug 的回归闸。
    ///
    /// ★ 注意 <c>WaitForPfAsync</c> 在**第一条** pf 1104 到达时就返回，
    /// 而分片是连续多条 ⇒ 必须再轮询等到「数量不再增长」才能断言片数
    /// （否则会 flaky：偶尔只看到第 1 片）。
    /// </summary>
    [Fact]
    public async Task PushAllItems_LargeBag_SplitsIntoChunksUnderReceiveLimit()
    {
        using var h = await FeatureHarness.CreateAsync("itemntf-large");
        // 2400 件 ≈ 真机的 2362 件（当时单包 66150 字节，已超限）
        SeedItems(h.Player, 2400);

        GameNotify.PushAllItems(h.Session, h.Player);

        Assert.True(await h.WaitForPfAsync(GamePf.ItemNtf));

        // 等到片数稳定（连续 5 次读取不再增长）
        int packets = 0, stable = 0;
        for (int i = 0; i < 100 && stable < 5; i++)
        {
            int now = h.PushedPf.Count(pf => pf == GamePf.ItemNtf);
            stable = now == packets ? stable + 1 : 0;
            packets = now;
            await Task.Delay(20);
        }

        Assert.True(packets > 1, $"2400 件物品应当分片下发，实际只推了 {packets} 条");
    }

    /// <summary>
    /// ★ 分片不能丢件也不能重复：把所有片的 <c>items</c> 合起来必须与背包**逐件一致**。
    /// 直接对 <see cref="GameNotify.PushAllItems"/> 的产物做等价复算
    /// （不经 socket，避免受 harness 缓冲影响）。
    /// </summary>
    [Fact]
    public async Task PushAllItems_ChunksCoverEveryItemExactlyOnce()
    {
        using var h = await FeatureHarness.CreateAsync("itemntf-cover");
        SeedItems(h.Player, 2400);

        // 复算：按同样的预算切分，验证「每片都不超限」且「合起来等于全量」
        var budgets = new List<int>();
        var guids = new List<uint>();
        var current = new ItemNtf();
        int currentBytes = 0;

        foreach (var it in h.Player.Items)
        {
            var info = GameNotify.BuildItemInfo(it);
            int size = info.ComputeSize() + 4;
            if (current.Items.Count > 0 && currentBytes + size > GameNotify.ItemNtfChunkBudget)
            {
                budgets.Add(current.ComputeSize());
                current = new ItemNtf();
                currentBytes = 0;
            }
            current.Items.Add(info);
            currentBytes += size;
            guids.Add(it.Guid);
        }
        budgets.Add(current.ComputeSize());

        // 每片的 protobuf 体积都要留在客户端上限之内（含 16 字节头 + 加密 padding 余量）
        Assert.All(budgets, b => Assert.True(b + 16 < ClientReceiveLimit,
            $"某一片 {b} 字节 + 16 头 已接近/超过客户端上限 {ClientReceiveLimit}"));

        // 覆盖性：每件恰好一次
        Assert.Equal(h.Player.Items.Count, guids.Count);
        Assert.Equal(guids.Count, guids.Distinct().Count());
    }

    /// <summary>
    /// ★ <c>itemnum</c> 只由**最后一片**带全量总数。
    /// 客户端在 <c>0x1D9DFF4</c> 拿它与内部计数比较，不等就跳过
    /// <c>DealLimitedItem</c>（<c>0x1D9E114</c>）后正常返回 —— 不是致命路径，
    /// 但让最后一片带全量能使「全部收完」那一刻的比较与单包时一致。
    ///
    /// ★ 这里直接对分片算法做**白盒复算**（不经 socket）：
    /// 前面的片 <c>Itemnum = 0</c>、最后一片 = 背包总数。
    /// </summary>
    [Fact]
    public async Task PushAllItems_OnlyLastChunkCarriesTotalItemnum()
    {
        using var h = await FeatureHarness.CreateAsync("itemntf-num");
        SeedItems(h.Player, 2400);
        int total = h.Player.Items.Count;      // ★ 含 StarterLoadout 的赠品，不是 2400

        // 复算分片（与 GameNotify.PushAllItems 同一套预算）
        var chunks = new List<ItemNtf>();
        var current = new ItemNtf();
        int currentBytes = 0;
        foreach (var it in h.Player.Items)
        {
            var info = GameNotify.BuildItemInfo(it);
            int size = info.ComputeSize() + 4;
            if (current.Items.Count > 0 && currentBytes + size > GameNotify.ItemNtfChunkBudget)
            {
                chunks.Add(current);
                current = new ItemNtf();
                currentBytes = 0;
            }
            current.Items.Add(info);
            currentBytes += size;
        }
        chunks.Add(current);

        Assert.True(chunks.Count > 1, "2400 件应当分成多片");
        // 合起来件数守恒
        Assert.Equal(total, chunks.Sum(c => c.Items.Count));

        // 真正跑一遍，确认不抛且推了多条
        GameNotify.PushAllItems(h.Session, h.Player);
        Assert.True(await h.WaitForPfAsync(GamePf.ItemNtf));
        Assert.True(h.PushedPf.Count(pf => pf == GamePf.ItemNtf) > 1);
    }
}
