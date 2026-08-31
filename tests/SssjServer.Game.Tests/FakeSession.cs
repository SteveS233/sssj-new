using SssjServer.Protocol;

namespace SssjServer.Game.Tests;

/// <summary>
/// 内存版 <see cref="SssjServer.Game.IGameSession"/>：记录所有 Send/Push 与加密事件与顺序，
/// 供 Game 层单测确定性断言，无需真 socket。
/// </summary>
public sealed class FakeSession : IGameSession
{
    public record Outbound(int Pf, uint Serial, ProtoMessage Message);

    public List<Outbound> Sent { get; } = new();
    public List<string> Events { get; } = new();
    public object? Tag { get; set; }

    public int Id { get; set; } = 1;

    /// <summary>EnableCrypto 是否被调用过。</summary>
    public bool CryptoEnabled { get; private set; }
    public string? ClientKey { get; private set; }

    public void EnableCrypto(string clientKey)
    {
        CryptoEnabled = true;
        ClientKey = clientKey;
        Events.Add($"EnableCrypto:{clientKey}");
    }

    public void Send(int pf, uint serial, ProtoMessage message)
    {
        Sent.Add(new Outbound(pf, serial, message));
        Events.Add($"Send:{pf}:{serial}");
    }

    public void Push(int pf, ProtoMessage message)
    {
        Sent.Add(new Outbound(pf, 0, message));
        Events.Add($"Push:{pf}");
    }

    /// <summary>第 <paramref name="index"/> 个 Send=pf 与请求 serial 对应（不参与 Push）。</summary>
    public Outbound LastOf(int pf)
        => Sent.Last(m => m.Pf == pf);
}
