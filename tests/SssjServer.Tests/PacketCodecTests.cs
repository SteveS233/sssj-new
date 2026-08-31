using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

public class PacketCodecTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    /// <summary>真实抓包：客户端 Verify 请求（明文，pf=1102）。</summary>
    private const string VerifyCapture = "4E0400002000000001000000008800000A0873656C667465737412002002300B";

    [Fact]
    public void TryDecode_RealVerifyCapture()
    {
        var bytes = Hex(VerifyCapture);
        Assert.True(PacketCodec.TryDecode(bytes, out var pkt, out int consumed),
            "应当能从真实抓包解码出完整封包");
        Assert.Equal(1102, pkt.Pf);
        Assert.Equal(1u, pkt.Serial);
        Assert.False(pkt.Compressed);
        Assert.Equal(16, pkt.Payload.Length);
        Assert.Equal(32, consumed);
    }

    [Fact]
    public void Encode_RoundTrips_WithTryDecode()
    {
        byte[] payload = { 1, 2, 3, 4, 5, 250 };
        var encoded = PacketCodec.Encode(1001, 7, payload);

        Assert.Equal(Packet.HeaderSize + payload.Length, encoded.Length);
        Assert.Equal(Packet.Magic, encoded[Packet.HeaderSize - 3]);

        Assert.True(PacketCodec.TryDecode(encoded, out var pkt, out int consumed));
        Assert.Equal(1001, pkt.Pf);
        Assert.Equal(7u, pkt.Serial);
        Assert.False(pkt.Compressed);
        Assert.Equal(payload, pkt.Payload);
        Assert.Equal(encoded.Length, consumed);
    }

    [Fact]
    public void Encode_CompressedFlag_RoundTrips()
    {
        var encoded = PacketCodec.Encode(1024, 0, new byte[] { 9 }, compressed: true);
        Assert.Equal(1, encoded[12]); // 压缩标志
        Assert.True(PacketCodec.TryDecode(encoded, out var pkt, out _));
        Assert.True(pkt.Compressed);
    }

    [Fact]
    public void TryDecode_Truncated_ReturnsFalse_ConsumesNothing()
    {
        var full = PacketCodec.Encode(1102, 1, new byte[16]);

        // 少于 16 字节头
        Assert.False(PacketCodec.TryDecode(full[..4], out var p1, out int c1));
        Assert.Equal(0, c1);

        // 头部完整但声明总长未收齐
        Assert.False(PacketCodec.TryDecode(full[..20], out var p2, out int c2));
        Assert.Equal(0, c2);

        // 恰好差 1 字节到总长
        Assert.False(PacketCodec.TryDecode(full[..^1], out var p3, out int c3));
        Assert.Equal(0, c3);
    }

    [Fact]
    public void TryDecode_TwoConcatenatedPackets_DecodeInSequence()
    {
        var first = PacketCodec.Encode(1102, 1, new byte[] { 0xAA });
        var second = PacketCodec.Encode(1001, 2, new byte[] { 0xBB, 0xCC });
        var concat = first.Concat(second).ToArray();

        Assert.True(PacketCodec.TryDecode(concat, out var pa, out int ca));
        Assert.Equal(1102, pa.Pf);
        Assert.Equal(1u, pa.Serial);

        Assert.True(PacketCodec.TryDecode(concat.AsSpan(ca), out var pb, out int cb));
        Assert.Equal(1001, pb.Pf);
        Assert.Equal(2u, pb.Serial);
        Assert.Equal(concat.Length, ca + cb);
    }

    [Fact]
    public void TryDecode_BadMagic_Throws()
    {
        var bytes = Hex(VerifyCapture);
        bytes[13] = 0x00; // 破坏魔数
        Assert.Throws<InvalidDataException>(() => PacketCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void TryDecode_TotalLessThanHeader_Throws()
    {
        // total = 8 < 16
        byte[] bytes = { 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0x88, 0, 0 };
        Assert.Throws<InvalidDataException>(() => PacketCodec.TryDecode(bytes, out _, out _));
    }
}
