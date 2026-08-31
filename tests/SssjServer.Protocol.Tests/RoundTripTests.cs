using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Protocol.Tests;

public static class TestHelpers
{
    public static byte[] Hex(string s)
    {
        s = s.Replace(" ", "").Replace("\n", "").Replace("\t", "");
        if (s.Length % 2 != 0) throw new ArgumentException("odd hex length");
        var buf = new byte[s.Length / 2];
        for (int i = 0; i < buf.Length; i++)
            buf[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return buf;
    }

    public static string Dump(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));

    /// <summary>walk every generated ProtoMessage subtype via reflection.</summary>
    public static IEnumerable<Type> AllMessageTypes()
    {
        var baseType = typeof(ProtoMessage);
        return baseType.Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName);
    }

    /// <summary>the first settable property of a simple scalar/string kind, if any.</summary>
    public static PropertyInfo? FirstSettableScalar(Type t)
    {
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length != 0) continue;
            var pt = p.PropertyType;
            if (pt == typeof(int) || pt == typeof(uint) || pt == typeof(long) || pt == typeof(ulong)
                || pt == typeof(bool) || pt == typeof(string) || pt == typeof(float) || pt == typeof(double)
                || pt == typeof(byte[]))
                return p;
        }
        return null;
    }
}

public class RoundTripTests
{
    [Fact]
    public void VerifyReq_ByteExact_RealCapture()
    {
        // real client payload captured on the wire
        byte[] wire = TestHelpers.Hex("0A0873656C667465737412002002300B");
        var req = ProtoMessage.Parse<VerifyReq>(wire);

        Assert.Equal("selftest", req.Platform);
        Assert.True(req.HasPlatform);
        Assert.Equal("", req.Account);
        Assert.True(req.HasAccount);
        Assert.Equal(1, req.Version);          // sint32 zigzag: wire 20 02 -> 1
        Assert.True(req.HasVersion);
        Assert.Equal(11, req.Unityplat);       // int32: wire 30 0B -> 11
        Assert.True(req.HasUnityplat);
        Assert.False(req.HasEnvir);

        // re-serialise must reproduce the exact same 16 bytes (ascending field order, only set fields)
        byte[] re = req.ToByteArray();
        Assert.Equal(TestHelpers.Dump(wire), TestHelpers.Dump(re));
        Assert.Equal(16, re.Length);
    }

    [Fact]
    public void VerifyReq_RoundTrip()
    {
        var in_ = new VerifyReq
        {
            Platform = "selftest",
            Account = "acct_1",
            ThirdKey = "k3",
            Version = -3,
            Cdkey = "cd",
            Unityplat = 7,
            Envir = "env"
        };
        var bytes = in_.ToByteArray();
        var out_ = ProtoMessage.Parse<VerifyReq>(bytes);
        Assert.Equal(in_.Platform, out_.Platform);
        Assert.Equal(in_.Account, out_.Account);
        Assert.Equal(in_.ThirdKey, out_.ThirdKey);
        Assert.Equal(in_.Version, out_.Version);
        Assert.Equal(in_.Cdkey, out_.Cdkey);
        Assert.Equal(in_.Unityplat, out_.Unityplat);
        Assert.Equal(in_.Envir, out_.Envir);
    }

    [Fact]
    public void VerifyRsp_RoundTrip()
    {
        var in_ = new VerifyRsp
        {
            Roleid = 12345678901234567890UL,
            GsId = -1234567890123L,
            Key = new byte[] { 0x00, 0x11, 0x22, 0xFF },
            ClientKey = "clientkey_32_bytes_padding_x",
            Aid = -700000,
            Sid = 42,
            Usecode = false,
            Optime = 999999999999999L,
            Createrole = true,
            Account = "a",
            Ext = "e",
            Retid = 201,
            Strret = "ok"
        };
        var bytes = in_.ToByteArray();
        var out_ = ProtoMessage.Parse<VerifyRsp>(bytes);
        Assert.Equal(in_.Roleid, out_.Roleid);
        Assert.Equal(in_.GsId, out_.GsId);
        Assert.Equal(in_.Key, out_.Key);
        Assert.Equal(in_.ClientKey, out_.ClientKey);
        Assert.Equal(in_.Aid, out_.Aid);
        Assert.Equal(in_.Sid, out_.Sid);
        Assert.Equal(in_.Usecode, out_.Usecode);
        Assert.Equal(in_.Optime, out_.Optime);
        Assert.Equal(in_.Createrole, out_.Createrole);
        Assert.Equal(in_.Account, out_.Account);
        Assert.Equal(in_.Ext, out_.Ext);
        Assert.Equal(in_.Retid, out_.Retid);
        Assert.Equal(in_.Strret, out_.Strret);
    }

    [Fact]
    public void LoginReq_RoundTrip_All15Fields()
    {
        var in_ = new LoginReq
        {
            Account = "acct",
            Equdid = "equdid-0001",
            Iccid = "iccid-0002",
            Platform = 1,
            Emulator = true,
            Version = 2,
            Devname = "HONOR PGT-AN20",
            Starttime = 1700000000000000000UL,
            Userstate = 3,
            Channel = "selftest",
            Bindplatform = "bp",
            CRCValue = 0xDEADBEEFu,
            Subplatform = "sp",
            Clientver = "1.0.0",
            Gstime = "2024/01/01 00:00:00"
        };
        var bytes = in_.ToByteArray();
        var out_ = ProtoMessage.Parse<LoginReq>(bytes);
        Assert.Equal(in_.Account, out_.Account);
        Assert.Equal(in_.Equdid, out_.Equdid);
        Assert.Equal(in_.Iccid, out_.Iccid);
        Assert.Equal(in_.Platform, out_.Platform);
        Assert.Equal(in_.Emulator, out_.Emulator);
        Assert.Equal(in_.Version, out_.Version);
        Assert.Equal(in_.Devname, out_.Devname);
        Assert.Equal(in_.Starttime, out_.Starttime);
        Assert.Equal(in_.Userstate, out_.Userstate);
        Assert.Equal(in_.Channel, out_.Channel);
        Assert.Equal(in_.Bindplatform, out_.Bindplatform);
        Assert.Equal(in_.CRCValue, out_.CRCValue);
        Assert.Equal(in_.Subplatform, out_.Subplatform);
        Assert.Equal(in_.Clientver, out_.Clientver);
        Assert.Equal(in_.Gstime, out_.Gstime);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, 2)]
    [InlineData(int.MinValue, 0xFFFFFFFFu)]
    [InlineData(int.MaxValue, 0xFFFFFFFEu)]
    public void ZigZag_Int32_RoundTrip(int value, uint expectedWire)
    {
        var bytes = new byte[16];
        var w = new ProtoWriter(bytes);
        w.WriteSInt32(value);
        Assert.Equal(SizeOf.SInt32(value), w.Position);
        var r = new ProtoReader(bytes.AsSpan(0, w.Position));
        Assert.Equal(value, r.ReadSInt32());

        // also verify the zigzag encoding of the wire value
        var w2 = new ProtoWriter(bytes);
        w2.WriteUInt32(expectedWire);
        var r2 = new ProtoReader(bytes.AsSpan(0, w2.Position));
        Assert.Equal(value, r2.ReadSInt32());
    }

    [Fact]
    public void ZigZag_Int64_RoundTrip()
    {
        long[] vals = { 0, -1, 1, long.MinValue, long.MaxValue, 123456789012345L, -987654321012345L };
        var bytes = new byte[32];
        foreach (var v in vals)
        {
            var w = new ProtoWriter(bytes);
            w.WriteSInt64(v);
            Assert.Equal(SizeOf.SInt64(v), w.Position);
            var r = new ProtoReader(bytes.AsSpan(0, w.Position));
            Assert.Equal(v, r.ReadSInt64());
        }
    }

    [Fact]
    public void ZigZag_MaxRoundTrip()
    {
        var b = new byte[16];
        var w = new ProtoWriter(b);
        w.WriteSInt32(int.MaxValue);
        var r = new ProtoReader(b.AsSpan(0, w.Position));
        Assert.Equal(int.MaxValue, r.ReadSInt32());

        var wl = new ProtoWriter(b);
        wl.WriteSInt64(long.MaxValue);
        var rl = new ProtoReader(b.AsSpan(0, wl.Position));
        Assert.Equal(long.MaxValue, rl.ReadSInt64());

        // min values
        var wm = new ProtoWriter(b);
        wm.WriteSInt32(int.MinValue);
        var rm = new ProtoReader(b.AsSpan(0, wm.Position));
        Assert.Equal(int.MinValue, rm.ReadSInt32());

        var wml = new ProtoWriter(b);
        wml.WriteSInt64(long.MinValue);
        var rml = new ProtoReader(b.AsSpan(0, wml.Position));
        Assert.Equal(long.MinValue, rml.ReadSInt64());
    }

    [Fact]
    public void Negative_Int32_Uses10ByteVarint_AndRoundTrips()
    {
        var b = new byte[16];
        var w = new ProtoWriter(b);
        w.WriteInt32(-1);
        Assert.Equal(10, w.Position); // negative int32 => 10-byte two's-complement varint
        var r = new ProtoReader(b.AsSpan(0, w.Position));
        Assert.Equal(-1, r.ReadInt32());

        var w2 = new ProtoWriter(b);
        w2.WriteInt32(int.MinValue);
        Assert.Equal(10, w2.Position);
        var r2 = new ProtoReader(b.AsSpan(0, w2.Position));
        Assert.Equal(int.MinValue, r2.ReadInt32());

        // same for int64
        var w3 = new ProtoWriter(b);
        w3.WriteInt64(-5);
        Assert.Equal(10, w3.Position);
        var r3 = new ProtoReader(b.AsSpan(0, w3.Position));
        Assert.Equal(-5L, r3.ReadInt64());
    }

    [Fact]
    public void ProtoMessage_ToByteArray_AssertWrittenLength()
    {
        var req = new VerifyReq { Platform = "selftest", Version = 1, Unityplat = 11 };
        var bytes = req.ToByteArray(); // Debug assert fires if WriteTo length != ComputeSize
        Assert.Equal(req.ComputeSize(), bytes.Length);
    }

    [Fact]
    public void SizeOf_MatchWrite_IntVarints()
    {
        foreach (var v in new[] { 0, 1, 127, 128, 16383, 16384, int.MaxValue, -1, -2, int.MinValue })
        {
            var b = new byte[16];
            var w = new ProtoWriter(b);
            w.WriteInt32(v);
            Assert.Equal(SizeOf.Int32(v), w.Position);
        }
    }
}
