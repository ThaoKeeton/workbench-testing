using System.Buffers.Binary;
using System.Text;
using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class OwnedWireTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Typed_responses_validate_manually_constructed_packets_and_freeze_captures()
    {
        var identity = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(identity, 10); BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(12), 20); BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(16), 200);
        BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(24), 300);
        var state = StringBody("title").Concat(StringBody("Saved")).Concat(StringBody("")).Concat(StringBody("1")).ToArray();
        var ready = new OwnedPacket(OwnedWireKind.Ready, 1, Session, [.. identity, .. state]);
        Assert.Equal("title", OwnedWireCodec.ReadReady(ready).State.Title);
        var body = new byte[] { 2, 0 }.Concat(identity).Concat(state).Concat(new byte[] { 54, 163, 2, 0 }).Concat(OwnedLabTestRig.Bmp()).ToArray();
        var result = new OwnedPacket(OwnedWireKind.Result, 2, Session, body);
        var observation = OwnedWireCodec.ReadResult(result, OwnedWireKind.SetTitle);
        Assert.Equal(172854, observation.Bmp.Length); body[^1] = 87; Assert.Equal(0, observation.Bmp[^1]);
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadResult(result, OwnedWireKind.Observe));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadResult(result, OwnedWireKind.Init));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadReady(ready with { Body = [.. ready.Body, 0] }));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadReady(ready with { Kind = OwnedWireKind.Result }));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadReady(ready with { Sequence = 0 }));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadReady(ready with { Session = new('A', 32) }));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadReady(ready with { Body = null! }));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadReady(null!));
        foreach (var (code, expected) in new[] { (3, "ProtocolViolation"), (4, "TargetUnavailable"), (5, "DriverFailure"), (6, "CaptureFailure") })
        {
            var error = new OwnedPacket(OwnedWireKind.Error, 3, Session, [2, 0, (byte)code, 0]);
            Assert.Equal(expected, OwnedWireCodec.ReadError(error, OwnedWireKind.SetTitle));
            Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadError(error, OwnedWireKind.Observe));
            Assert.Throws<OwnedLabException>(() => OwnedWireCodec.ReadError(error with { Body = [2, 0, 7, 0] }, OwnedWireKind.SetTitle));
        }
    }

    [Fact]
    public void Init_body_accepts_only_exact_fixed_target_bounded_grant_and_no_extra_fields()
    {
        var prefix = Encoding.ASCII.GetBytes("abcdef0123456789abcdef0123456789").Concat(StringBody("project")).Concat(StringBody("plugin")).Concat(StringBody("owned.win32.task-form.v1")).Concat(new byte[96]).ToArray();
        var tail = new byte[12]; BinaryPrimitives.WriteInt64LittleEndian(tail, 1794096000000); BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(8), 60000);
        var body = prefix.Concat(tail).ToArray(); Assert.Equal(body, OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Init, body), Session, 1).Body);
        var expired = body.ToArray(); Array.Clear(expired, expired.Length - 4, 4);
        var tooLong = body.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(tooLong.AsSpan(tooLong.Length - 4), 60001);
        var badTarget = body.ToArray(); badTarget[32 + 4 + 7 + 4 + 6 + 4] = (byte)'X';
        foreach (var invalid in new[] { expired, tooLong, badTarget, body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Init, invalid), Session, 1));
        var longProject = Encoding.ASCII.GetBytes("abcdef0123456789abcdef0123456789").Concat(StringBody(new string('x', 33))).Concat(StringBody("plugin")).Concat(StringBody("owned.win32.task-form.v1")).Concat(new byte[96]).Concat(tail).ToArray();
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Init, longProject), Session, 1));
    }

    [Fact]
    public void Actual_sized_result_is_validated_before_identity_state_or_capture_is_accepted()
    {
        byte[] Identity()
        {
            var bytes = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 10); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), 100);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 20); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 200);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 300); return bytes;
        }
        var state = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, (byte)'0' };
        var identity = Identity(); var body = new byte[] { 2, 0 }.Concat(identity).Concat(state).Concat(new byte[] { 54, 163, 2, 0 }).Concat(OwnedLabTestRig.Bmp()).ToArray();
        var frame = RawFrame(OwnedWireKind.Result, body); Assert.Equal(body, OwnedWireCodec.Decode(frame, Session, 1).Body);
        foreach (var offset in new[] { 2, 14, 26 })
        {
            var corrupt = body.ToArray(); Array.Clear(corrupt, offset, offset == 26 ? 8 : 4);
            Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Result, corrupt), Session, 1));
        }
        var samePid = body.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(samePid.AsSpan(14), 10);
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Result, samePid), Session, 1));
        var badBmp = body.ToArray(); badBmp[^1] = 1; badBmp[2 + 64 + state.Length + 4 + 54 + (179 - 158) * 960 + 286 * 3] ^= 1;
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Result, badBmp), Session, 1));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(frame[..^1], Session, 1));
        var badCount = identity.Concat(StringBody("")).Concat(StringBody("")).Concat(StringBody("")).Concat(StringBody("01")).ToArray();
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(RawFrame(OwnedWireKind.Ready, badCount), Session, 1));
        var validTitle = new string('x', 254) + "🧪"; Assert.Equal(StringBody(validTitle), OwnedWireCodec.Decode(RawFrame(OwnedWireKind.SetTitle, StringBody(validTitle)), Session, 1).Body);
    }

    private static byte[] RawFrame(OwnedWireKind kind, byte[] body)
    {
        var bytes = new byte[52 + body.Length]; Encoding.ASCII.GetBytes("PWB-LAB1").CopyTo(bytes, 0); bytes[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)kind); bytes[12] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), body.Length); Encoding.ASCII.GetBytes(Session).CopyTo(bytes, 20); body.CopyTo(bytes, 52); return bytes;
    }

    [Fact]
    public void SetTitle_uses_exact_52_byte_header_and_strict_UTF8_body()
    {
        byte[] body = [4, 0, 0, 0, 0xf0, 0x9f, 0x94, 0xac];
        var encoded = OwnedWireCodec.Encode(new(OwnedWireKind.SetTitle, 1, Session, body));
        Assert.Equal(60, encoded.Length);
        Assert.Equal(Encoding.ASCII.GetBytes("PWB-LAB1"), encoded[..8]);
        Assert.Equal(new byte[] { 1, 0, 2, 0, 1, 0, 0, 0, 8, 0, 0, 0 }, encoded[8..20]);
        Assert.Equal(body, OwnedWireCodec.Decode(encoded, Session, 1).Body);
    }

    [Fact]
    public void Corpus_rejects_partial_trailing_cross_session_out_of_order_unknown_and_oversize_before_body()
    {
        var valid = OwnedWireCodec.Encode(new(OwnedWireKind.ClickSave, 1, Session, []));
        List<byte[]> cases = [valid[..51], [.. valid, 0]];
        var cross = valid.ToArray(); cross[20] = (byte)'f'; cases.Add(cross);
        var order = valid.ToArray(); order[12] = 2; cases.Add(order);
        var unknown = valid.ToArray(); unknown[10] = 99; cases.Add(unknown);
        var version = valid.ToArray(); version[8] = 2; cases.Add(version);
        var huge = valid.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(16), 262145); cases.Add(huge);
        var uppercase = valid.ToArray(); uppercase[20] = (byte)'A'; cases.Add(uppercase);
        var targetReady = valid.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(targetReady.AsSpan(10), 32768); cases.Add(targetReady);
        foreach (var bytes in cases) Assert.Equal("ProtocolViolation", Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Decode(bytes, Session, 1)).ErrorCode);
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.Observe, 0, Session, [])));
        var finalSequence = OwnedWireCodec.Encode(new(OwnedWireKind.Observe, uint.MaxValue, Session, []));
        Assert.Equal(uint.MaxValue, OwnedWireCodec.Decode(finalSequence, Session, uint.MaxValue).Sequence);
    }

    [Theory]
    [InlineData("valid 🧪", true)]
    [InlineData("", true)]
    [InlineData("\u00a0\u3000", true)]
    [InlineData("bad\0", false)]
    [InlineData("bad\u007f", false)]
    [InlineData("bad\u2028", false)]
    public void Title_rules_are_enforced_at_the_wire_boundary(string value, bool valid)
    {
        var body = StringBody(value);
        if (valid) Assert.Equal(body, OwnedWireCodec.Decode(OwnedWireCodec.Encode(new(OwnedWireKind.SetTitle, 1, Session, body)), Session, 1).Body);
        else Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.SetTitle, 1, Session, body)));
    }

    [Fact]
    public void Corpus_rejects_257_units_invalid_UTF8_BOM_extra_fields_and_bad_identity_or_BMP()
    {
        foreach (var body in new[] { StringBody(new string('x', 257)), new byte[] { 2, 0, 0, 0, 0xc0, 0xaf }, StringBody("\ufeffx"), [.. StringBody("ok"), (byte)0] })
            Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.SetTitle, 1, Session, body)));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.Ready, 1, Session, new byte[64])));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.Result, 1, Session, new byte[172854])));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.Closed, 1, Session, [1, 0, 0, 0])));
        Assert.Throws<OwnedLabException>(() => OwnedWireCodec.Encode(new(OwnedWireKind.Error, 1, Session, [2, 0, 7, 0])));
    }

    internal static byte[] StringBody(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); var result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, bytes.Length); bytes.CopyTo(result, 4); return result;
    }
}
