using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Workbench.Testing.Domain.OwnedLab;

public static class OwnedWireCodec
{
    public const int HeaderLength = 52;
    public const int MaximumBodyLength = 262144;
    public const int BmpLength = 172854;

    public static (OwnedIdentity Identity, OwnedState State) ReadReady(OwnedPacket packet)
    {
        var reader = new BodyReader(Typed(packet, OwnedWireKind.Ready));
        return (reader.Identity(), reader.State());
    }

    public static OwnedObservation ReadResult(OwnedPacket packet, OwnedWireKind expectedRequestKind)
    {
        var body = Typed(packet, OwnedWireKind.Result);
        if (expectedRequestKind is not (OwnedWireKind.SetTitle or OwnedWireKind.ClickSave or OwnedWireKind.Observe)) Fail();
        var reader = new BodyReader(body);
        if (reader.U16() != (ushort)expectedRequestKind) Fail();
        var identity = reader.Identity(); var state = reader.State(); reader.U32();
        return new(identity, state, reader.Take(BmpLength).ToArray());
    }

    public static string ReadError(OwnedPacket packet, OwnedWireKind expectedRequestKind)
    {
        var body = Typed(packet, OwnedWireKind.Error);
        if (expectedRequestKind is < OwnedWireKind.Init or > OwnedWireKind.Close) Fail();
        var reader = new BodyReader(body);
        if (reader.U16() != (ushort)expectedRequestKind) Fail();
        return reader.U16() switch { 3 => "ProtocolViolation", 4 => "TargetUnavailable", 5 => "DriverFailure", 6 => "CaptureFailure", _ => throw new OwnedLabException("ProtocolViolation") };
    }

    private static byte[] Typed(OwnedPacket packet, OwnedWireKind kind)
    {
        if (packet is null || packet.Body is null) throw new OwnedLabException("ProtocolViolation");
        if (packet.Kind != kind) Fail();
        var body = packet.Body.ToArray();
        Header(packet.Kind, packet.Sequence, packet.Session, body.Length);
        ValidateBody(kind, body); return body;
    }

    public static byte[] Encode(OwnedPacket packet)
    {
        var body = packet.Body.ToArray();
        Header(packet.Kind, packet.Sequence, packet.Session, body.Length);
        ValidateBody(packet.Kind, body);
        var frame = new byte[HeaderLength + body.Length];
        "PWB-LAB1"u8.CopyTo(frame); BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(10), (ushort)packet.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), packet.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), (uint)body.Length);
        Encoding.ASCII.GetBytes(packet.Session, frame.AsSpan(20, 32)); body.CopyTo(frame, HeaderLength); return frame;
    }

    public static OwnedPacket Decode(ReadOnlySpan<byte> frame, string session, uint sequence)
    {
        if (frame.Length < HeaderLength || !frame[..8].SequenceEqual("PWB-LAB1"u8) || BinaryPrimitives.ReadUInt16LittleEndian(frame[8..]) != 1) Fail();
        var kind = (OwnedWireKind)BinaryPrimitives.ReadUInt16LittleEndian(frame[10..]);
        var actualSequence = BinaryPrimitives.ReadUInt32LittleEndian(frame[12..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(frame[16..]);
        if (length > MaximumBodyLength || frame.Length != HeaderLength + (long)length || actualSequence != sequence || !OwnedJson.Hex(session, 32)) Fail();
        for (var i = 0; i < 32; i++) if (frame[20 + i] != session[i]) Fail();
        Header(kind, actualSequence, session, (int)length);
        ValidateBody(kind, frame[HeaderLength..]);
        return new(kind, actualSequence, session, frame[HeaderLength..].ToArray());
    }

    private static void Header(OwnedWireKind kind, uint sequence, string session, int length)
    {
        if (!Enum.IsDefined(kind) || kind == OwnedWireKind.TargetReady || sequence == 0 || !OwnedJson.Hex(session, 32) || length > MaximumBodyLength) Fail();
    }

    private static void ValidateBody(OwnedWireKind kind, ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new BodyReader(body);
            switch (kind)
            {
                case OwnedWireKind.Init:
                    if (!OwnedJson.Hex(Encoding.ASCII.GetString(reader.Take(32)), 32)) Fail();
                    if (reader.String(32).Length == 0 || reader.String(128).Length == 0 || reader.String(64) != FixedOwnedPlans.TargetId) Fail();
                    reader.Take(32 * 3); if (reader.I64() <= 0 || reader.U32() is < 1 or > 60000) Fail(); break;
                case OwnedWireKind.SetTitle: ValidateTitle(reader.String(1024)); break;
                case OwnedWireKind.ClickSave: case OwnedWireKind.Observe: case OwnedWireKind.Close: break;
                case OwnedWireKind.Ready: ValidateIdentity(reader.Identity()); ValidateState(reader.State()); break;
                case OwnedWireKind.Result:
                    if (reader.U16() is < 2 or > 4) Fail();
                    ValidateIdentity(reader.Identity()); ValidateState(reader.State());
                    if (reader.U32() != BmpLength) Fail(); ValidateBmp(reader.Take(BmpLength)); break;
                case OwnedWireKind.Closed: if (reader.I32() != 0) Fail(); break;
                case OwnedWireKind.Error:
                    if (reader.U16() is < 1 or > 5 || reader.U16() is < 3 or > 6) Fail(); break;
                default: Fail(); break;
            }
            if (!reader.Empty) Fail();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException or System.Text.Json.JsonException) { Fail(); }
    }

    internal static void ValidateIdentity(OwnedIdentity identity)
    {
        if (identity.BrokerPid == 0 || identity.TargetPid == 0 || identity.BrokerPid == identity.TargetPid || identity.BrokerCreationFileTime == 0 || identity.TargetCreationFileTime == 0 || identity.Hwnd == 0 || !OwnedJson.Hex(identity.ImageSha, 64)) Fail();
    }
    internal static void ValidateState(OwnedState state)
    {
        ValidateTitle(state.Title); Text(state.Toast, 64); Text(state.Validation, 128); Text(state.CreatedCount, 2);
        if (!int.TryParse(state.CreatedCount, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 0 or > 20 || count.ToString(CultureInfo.InvariantCulture) != state.CreatedCount) Fail();
    }
    internal static void ValidateTitle(string title)
    {
        Text(title, 1024); if (title.Length > 256 || title.Any(c => c <= 0x1f || c is >= '\u007f' and <= '\u009f' or '\u2028' or '\u2029')) Fail();
    }
    private static void Text(string text, int max)
    {
        OwnedJson.ValidateUnicode(text);
        if (text.StartsWith('\ufeff') || text.Contains('\0') || OwnedJson.Utf8.GetByteCount(text) > max) Fail();
    }
    internal static void ValidateBmp(ReadOnlySpan<byte> bmp)
    {
        if (bmp.Length != BmpLength || !bmp[..2].SequenceEqual("BM"u8) || BinaryPrimitives.ReadInt32LittleEndian(bmp[2..]) != BmpLength || BinaryPrimitives.ReadInt32LittleEndian(bmp[6..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(bmp[10..]) != 54 || BinaryPrimitives.ReadInt32LittleEndian(bmp[14..]) != 40 || BinaryPrimitives.ReadInt32LittleEndian(bmp[18..]) != 320 || BinaryPrimitives.ReadInt32LittleEndian(bmp[22..]) != 180 || BinaryPrimitives.ReadUInt16LittleEndian(bmp[26..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bmp[28..]) != 24 || BinaryPrimitives.ReadInt32LittleEndian(bmp[30..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(bmp[34..]) != 172800) Fail();
        if (!bmp.Slice(54 + (179 - 158) * 960 + 286 * 3, 3).SequenceEqual(new byte[] { 113, 57, 17 }) || !bmp.Slice(54 + (179 - 158) * 960 + 302 * 3, 3).SequenceEqual(new byte[] { 31, 73, 149 })) Fail();
    }
    private static void Fail() => throw new OwnedLabException("ProtocolViolation");

    private ref struct BodyReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        internal bool Empty => _remaining.IsEmpty;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length) Fail();
            var value = _remaining[..count]; _remaining = _remaining[count..]; return value;
        }
        internal ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        internal ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        internal string String(int max)
        {
            var length = U32(); if (length > max) Fail();
            var text = OwnedJson.Utf8.GetString(Take((int)length)); Text(text, max); return text;
        }
        internal OwnedIdentity Identity() => new(U32(), U64(), U32(), U64(), U64(), Convert.ToHexStringLower(Take(32)));
        internal OwnedState State() => new(String(1024), String(64), String(128), String(2));
    }
}
