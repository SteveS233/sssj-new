using System;
using System.Linq;
using System.Reflection;
using SssjServer.Protocol;
using Xunit;

namespace SssjServer.Protocol.Tests;

/// <summary>
/// Walk EVERY generated ProtoMessage subtype via reflection, assert that an empty instance
/// round-trips with ComputeSize()==0, and that setting one trivially-settable scalar field
/// round-trips without exception. Catches generator bugs across all 248 types.
/// </summary>
public class GeneratedTypesTests
{
    [Fact]
    public void GeneratedMessageCount_Is248()
    {
        var all = TestHelpers.AllMessageTypes().ToList();
        Assert.Equal(248, all.Count);
    }

    [Fact]
    public void EmptyInstance_RoundTrips_ComputeSizeZero()
    {
        var all = TestHelpers.AllMessageTypes().ToList();
        foreach (var t in all)
        {
            var msg = (ProtoMessage)Activator.CreateInstance(t)!;
            int size;
            byte[] bytes;
            try
            {
                size = msg.ComputeSize();
                bytes = msg.ToByteArray();
            }
            catch (Exception ex)
            {
                throw new Exception($"ComputeSize/ToByteArray failed for {t.FullName}: {ex.Message}", ex);
            }
            Assert.True(size == 0, $"{t.FullName}: empty ComputeSize should be 0 but was {size}");
            Assert.Empty(bytes);

            // parse back the (empty) bytes without exception
            var parse = (ProtoMessage)Activator.CreateInstance(t)!;
            var r = new ProtoReader(bytes);
            parse.MergeFrom(ref r);
            Assert.True(parse.ComputeSize() == 0, $"{t.FullName}: parse(empty) ComputeSize should be 0");
        }
    }

    [Fact]
    public void SetOneScalarField_RoundTrips()
    {
        var all = TestHelpers.AllMessageTypes().ToList();
        foreach (var t in all)
        {
            var msg = (ProtoMessage)Activator.CreateInstance(t)!;
            var prop = TestHelpers.FirstSettableScalar(t);
            if (prop is null)
            {
                // no trivially-settable scalar field; still verify empty serialise+parse path
                var b0 = msg.ToByteArray();
                var p0 = (ProtoMessage)Activator.CreateInstance(t)!;
                var rr = new ProtoReader(b0);
                p0.MergeFrom(ref rr);
                continue;
            }

            object value;
            var pt = prop.PropertyType;
            if (pt == typeof(string)) value = "zz";
            else if (pt == typeof(bool)) value = true;
            else if (pt == typeof(int)) value = 12345;
            else if (pt == typeof(uint)) value = 12345u;
            else if (pt == typeof(long)) value = 1234567890123L;
            else if (pt == typeof(ulong)) value = 1234567890123UL;
            else if (pt == typeof(float)) value = 1.5f;
            else if (pt == typeof(double)) value = 2.5d;
            else if (pt == typeof(byte[])) value = new byte[] { 1, 2, 3 };
            else continue;

            try
            {
                prop.SetValue(msg, value);
            }
            catch (Exception ex)
            {
                throw new Exception($"setting {t.FullName}.{prop.Name} failed: {ex.Message}", ex);
            }

            byte[] bytes;
            try
            {
                bytes = msg.ToByteArray();
            }
            catch (Exception ex)
            {
                throw new Exception($"serialize failed for {t.FullName} after setting {prop.Name}: {ex.Message}", ex);
            }

            Assert.True(bytes.Length > 0, $"{t.FullName}: setting {prop.Name} should produce non-empty bytes");
            Assert.Equal(msg.ComputeSize(), bytes.Length);

            var parsed = (ProtoMessage)Activator.CreateInstance(t)!;
            var r = new ProtoReader(bytes);
            parsed.MergeFrom(ref r);
            var got = prop.GetValue(parsed);
            Assert.Equal(value, got);
        }
    }
}
