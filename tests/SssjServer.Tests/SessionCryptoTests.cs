using SssjServer.Core;
using Xunit;

namespace SssjServer.Tests;

public class SessionCryptoTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void KnownAnswer_RealSessionCapture()
    {
        // 真实会话抓包：VerifyRsp 下发该 client_key 后，这段明文加密成如下密文。
        const string clientKey = "20CE43B3703E435F9FE4E56DFC404156";
        var plain = Hex("0A001220363034656466313232323334");
        var expectedCipher = Hex("277F3F75321DB7DE35A6A478ADFEE9338E0EAEDD4747F2D5");

        var crypto = new SessionCrypto(clientKey);

        var cipher = crypto.Encrypt(plain);
        Assert.Equal(expectedCipher, cipher);

        var back = crypto.Decrypt(cipher);
        Assert.Equal(plain, back);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(70000)]
    public void EncryptDecrypt_RoundTrip_RandomBuffers(int length)
    {
        var crypto = new SessionCrypto("0123456789ABCDEF0123456789ABCDEF");
        var plain = new byte[length];
        new Random(1234 + length).NextBytes(plain);

        var cipher = crypto.Encrypt(plain);
        var back = crypto.Decrypt(cipher);

        Assert.Equal(plain, back);
        Assert.NotEqual(plain, cipher); // CBC 下明文≠密文（除非全零且无前块，这里长度>1 一般成立）
    }

    [Fact]
    public void Constructor_ShortKey_Throws()
    {
        // 31 字节 ASCII < 32
        Assert.Throws<ArgumentException>(() => new SessionCrypto(new string('x', 31)));
        // 空串
        Assert.Throws<ArgumentException>(() => new SessionCrypto(""));
    }

    [Fact]
    public void Decrypt_Empty_ReturnsEmpty()
    {
        var crypto = new SessionCrypto("0123456789ABCDEF0123456789ABCDEF");
        Assert.Empty(crypto.Decrypt(ReadOnlySpan<byte>.Empty));
        Assert.Empty(crypto.Encrypt(ReadOnlySpan<byte>.Empty));
    }
}
