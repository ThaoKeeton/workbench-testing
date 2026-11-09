using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Sdk;

namespace Workbench.Testing.Domain.OwnedLab;

public static class FixedOwnedPlans
{
    public const string TargetId = "owned.win32.task-form.v1";
    public const ushort Protocol = 1;

    public static OwnedInputPlan Create(OwnedScenario scenario)
    {
        var steps = Catalog(scenario);
        var bytes = OwnedJson.Write(w =>
        {
            w.WriteStartObject(); w.WriteNumber("schema", 1); w.WriteString("targetId", TargetId);
            w.WriteNumber("protocol", Protocol); w.WriteNumber("scenario", (ushort)scenario); w.WriteStartArray("steps");
            foreach (var step in steps)
            {
                w.WriteStartObject(); w.WriteString("id", step.Id); w.WriteNumber("kind", (int)step.Action.Kind);
                w.WriteString("elementId", step.Action.ElementId); w.WriteString("value", step.Action.Value); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        });
        return new(TargetId, Protocol, scenario, Array.AsReadOnly(steps), bytes, OwnedJson.Hash(bytes));
    }

    public static OwnedInputPlan ParseFrozen(ReadOnlyMemory<byte> bytes)
    {
        var frozen = bytes.ToArray();
        try
        {
            if (frozen.Length > 4 * 1024 * 1024) throw new OwnedLabException("InvalidPlan");
            using var document = OwnedJson.Parse(frozen);
            var root = document.RootElement;
            OwnedJson.Fields(root, "schema", "targetId", "protocol", "scenario", "steps");
            var scenario = (OwnedScenario)root.GetProperty("scenario").GetUInt16();
            var expected = Catalog(scenario);
            if (root.GetProperty("schema").GetInt32() != 1 || OwnedJson.Text(root, "targetId") != TargetId || root.GetProperty("protocol").GetUInt16() != Protocol) throw new OwnedLabException("InvalidPlan");
            var items = root.GetProperty("steps").EnumerateArray().ToArray();
            if (items.Length != 4) throw new OwnedLabException("InvalidPlan");
            for (var i = 0; i < items.Length; i++)
            {
                OwnedJson.Fields(items[i], "id", "kind", "elementId", "value");
                if (OwnedJson.Text(items[i], "id") != expected[i].Id || items[i].GetProperty("kind").GetInt32() != (int)expected[i].Action.Kind || OwnedJson.Text(items[i], "elementId") != expected[i].Action.ElementId || OwnedJson.NullableText(items[i], "value") != expected[i].Action.Value) throw new OwnedLabException("InvalidPlan");
            }
            return new(TargetId, Protocol, scenario, Array.AsReadOnly(expected), frozen, OwnedJson.Hash(frozen));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException or KeyNotFoundException)
        { throw new OwnedLabException("InvalidPlan"); }
    }

    private static OwnedStep[] Catalog(OwnedScenario scenario)
    {
        if (!Enum.IsDefined(scenario)) throw new OwnedLabException("InvalidPlan");
        var empty = scenario == OwnedScenario.EmptyTitle;
        return [new("step-1", new(TestActionKind.SetText, "task.title", empty ? "" : "Owned pilot task")),
            new("step-2", new(TestActionKind.Click, "task.save", null)),
            new("step-3", new(TestActionKind.AssertText, empty ? "task.validation" : "task.toast", empty ? "Title required" : scenario == OwnedScenario.AssertionFailure ? "DELIBERATELY WRONG TOAST" : "Saved")),
            new("step-4", new(TestActionKind.AssertText, "task.createdCount", empty ? "0" : "1"))];
    }
}

internal static class OwnedJson
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return stream.ToArray();
    }
    internal static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) throw new JsonException();
        _ = Utf8.GetString(bytes);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
    }
    internal static void Fields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(p => p.Name).SequenceEqual(fields, StringComparer.Ordinal)) throw new JsonException();
    }
    internal static string Text(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString() ?? throw new JsonException();
        ValidateUnicode(value); return value;
    }
    internal static string? NullableText(JsonElement element, string property) => element.GetProperty(property).ValueKind == JsonValueKind.Null ? null : Text(element, property);
    internal static void ValidateUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) throw new JsonException(); }
            else if (char.IsLowSurrogate(value[i])) throw new JsonException();
        }
    }
}
