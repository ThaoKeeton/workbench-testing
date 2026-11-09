using System.Collections.ObjectModel;

namespace Workbench.Testing.Domain.Sources.Figma;

// Shared synchronous input boundary for reader, builder and retained controller admission.
internal static class FigmaInputCapture
{
    internal static FigmaReadRequest CaptureRequest(FigmaReadRequest request)
    {
        try
        {
            if (request is null || request.FileKey is not { Length: >= 1 and <= 128 } key
                || key.Any(c => !AsciiLetterOrDigit(c)) || request.NodeIds is null)
                throw Invalid();
            if (request.Version is { } version && (!Text(version, 64, true) || version.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
                throw Invalid();
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in request.NodeIds)
            {
                if (ids.Count == 16 || !NodeId(id) || !seen.Add(id)) throw Invalid();
                ids.Add(id);
            }
            if (ids.Count == 0) throw Invalid();
            return new(key, ids.AsReadOnly(), request.Version);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    // The request has already crossed CaptureRequest; only these bounded owned node IDs are revisited.
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureCriteria(
        FigmaReadRequest capturedRequest, IReadOnlyDictionary<string, IReadOnlyList<string>> acceptanceCriteria)
    {
        try
        {
            if (acceptanceCriteria is null) throw Invalid();
            var keys = new HashSet<string>(capturedRequest.NodeIds, StringComparer.Ordinal);
            var copied = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var total = 0;
            foreach (var entry in acceptanceCriteria)
            {
                if (copied.Count == 16 || entry.Key is null || !keys.Contains(entry.Key) || copied.ContainsKey(entry.Key) || entry.Value is null)
                    throw Invalid();
                var criteria = new List<string>();
                foreach (var criterion in entry.Value)
                {
                    if (criteria.Count == 16 || total == 200 || !Text(criterion, 4096, true)) throw Invalid();
                    criteria.Add(criterion);
                    total++;
                }
                if (criteria.Count == 0) throw Invalid();
                copied.Add(entry.Key, criteria.AsReadOnly());
            }
            if (copied.Count != keys.Count) throw Invalid();
            return new ReadOnlyDictionary<string, IReadOnlyList<string>>(copied);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    internal static bool Text(string? value, int maximum, bool required)
        => value is not null && value.Length <= maximum && (!required || !string.IsNullOrWhiteSpace(value)) && ValidUnicode(value);

    private static bool NodeId(string? value)
    {
        if (value is not { Length: >= 3 and <= 64 }) return false;
        var colon = value.IndexOf(':');
        return colon > 0 && colon < value.Length - 1
            && value.Where((_, index) => index != colon).All(c => c is >= '0' and <= '9');
    }

    private static bool AsciiLetterOrDigit(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
    private static bool ValidUnicode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }

    private static FigmaReadException Invalid() => new(FigmaReadError.InvalidRequest);
}
