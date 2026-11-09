using System.Text;
using System.Text.Json;
using Workbench.Testing.Domain.Serialization;

namespace Workbench.Testing.Domain.Sources.Figma;

internal static class FigmaResponseParser
{
    internal static FigmaCapture Parse(FigmaReadRequest request, byte[] bytes)
    {
        try
        {
            using var document = StrictJson.Parse(bytes);
            var root = document.RootElement;
            var name = Text(Property(root, "name"), 200, true);
            var version = Text(Property(root, "version"), 64, true);
            if (request.Version is { } expected && !string.Equals(version, expected, StringComparison.Ordinal)) throw Invalid();
            var nodes = Property(root, "nodes");
            if (nodes.ValueKind != JsonValueKind.Object) throw Invalid();
            var requested = new HashSet<string>(request.NodeIds, StringComparer.Ordinal);
            var count = 0;
            foreach (var entry in nodes.EnumerateObject())
            {
                if (!requested.Contains(entry.Name)) throw Invalid();
                count++;
            }
            if (count != requested.Count) throw Invalid();
            var selected = new List<FigmaNodeCapture>();
            foreach (var id in request.NodeIds)
            {
                var node = Property(Property(nodes, id), "document");
                if (Text(Property(node, "id"), 64, true) != id) throw Invalid();
                selected.Add(new(id, Text(Property(node, "name"), 200, true), Text(Property(node, "type"), 64, true),
                    node.TryGetProperty("characters", out var characters) ? Text(characters, 4096, false) : null));
            }
            return new(request, bytes, name, version, selected);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException)
        { throw Invalid(); }
    }

    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : throw Invalid();

    private static string Text(JsonElement value, int maximum, bool required)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        return FigmaInputCapture.Text(text, maximum, required) ? text : throw Invalid();
    }

    private static FigmaReadException Invalid() => new(FigmaReadError.ResponseInvalid);
}
