using System.Text;
using System.Text.Json;

namespace Workbench.Testing.Domain.Serialization;

// Both bounded readers use the same strict UTF-8, depth, duplicate-name and Unicode checks.
internal static class StrictJson
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes, string? prohibitedText = null)
    {
        var utf8 = new UTF8Encoding(false, true);
        _ = utf8.GetCharCount(bytes.Span);
        if (prohibitedText is not null && utf8.GetString(bytes.Span).Contains(prohibitedText, StringComparison.Ordinal)) throw Invalid();
        var reader = new Utf8JsonReader(bytes.Span, new JsonReaderOptions { MaxDepth = 32 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
            {
                if (reader.ValueIsEscaped) ValidateEscapes(reader.ValueSpan);
                var text = reader.GetString()!;
                if (!Text(text, int.MaxValue, false) || prohibitedText is not null && text.Contains(prohibitedText, StringComparison.Ordinal)
                    || reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(text)) throw Invalid();
            }
        }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }

    internal static bool Text(string? value, int maximum, bool required)
    {
        if (value is null || value.Length > maximum || required && string.IsNullOrWhiteSpace(value)) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    // Utf8JsonReader checks escape syntax. Reject unpaired escaped UTF-16 before replacement decoding.
    private static void ValidateEscapes(ReadOnlySpan<byte> token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] != '\\' || token[++i] != 'u') continue;
            var scalar = Hex(token.Slice(i + 1, 4));
            i += 4;
            if (scalar is >= 0xdc00 and <= 0xdfff) throw Invalid();
            if (scalar is < 0xd800 or > 0xdbff) continue;
            if (i + 6 >= token.Length || token[i + 1] != '\\' || token[i + 2] != 'u' || Hex(token.Slice(i + 3, 4)) is < 0xdc00 or > 0xdfff) throw Invalid();
            i += 6;
        }
    }

    private static int Hex(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var b in bytes) value = (value << 4) + (b is >= (byte)'0' and <= (byte)'9' ? b - '0' : b is >= (byte)'a' and <= (byte)'f' ? b - 'a' + 10 : b - 'A' + 10);
        return value;
    }

    private static JsonException Invalid() => new("Invalid bounded JSON.");
}
