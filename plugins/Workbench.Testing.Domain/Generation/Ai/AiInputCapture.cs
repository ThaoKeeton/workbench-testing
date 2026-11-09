using System.Text.Encodings.Web;
using System.Text.Json;
using Workbench.Sdk;
using Workbench.Testing.Domain.Serialization;

namespace Workbench.Testing.Domain.Generation.Ai;

// This synchronous boundary never carries caller collections, OriginalBytes or VirtualScenario into async work.
internal sealed class AiInputCapture(byte[] json, IReadOnlyList<TestTrace> traces)
{
    internal byte[] Json { get; } = json;
    internal IReadOnlyList<TestTrace> Traces { get; } = traces;

    internal static AiInputCapture Capture(IReadOnlyList<TestSourceSnapshot> sources)
    {
        try
        {
            if (sources is null) throw Invalid();
            using var output = new MemoryStream();
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var traces = new List<TestTrace>();
            writer.WriteStartObject();
            writer.WriteStartArray("sources");
            foreach (var source in sources)
            {
                if (ids.Count == 8 || source is null || !LowerHex(source.Id, 32) || !LowerHex(source.Revision, 64)
                    || !ids.Add(source.Id) || !Enum.IsDefined(source.Kind) || source.ProviderId == "openai.responses.test-cases"
                    || !StrictJson.Text(source.ProviderId, 100, true) || !StrictJson.Text(source.ProviderVersion, 40, true)
                    || !StrictJson.Text(source.DocumentId, 200, true) || !StrictJson.Text(source.Title, 200, true) || source.Items is null) throw Invalid();
                writer.WriteStartObject();
                writer.WriteString("snapshotId", source.Id);
                writer.WriteString("snapshotRevision", source.Revision);
                writer.WriteString("kind", source.Kind.ToString());
                writer.WriteString("providerId", source.ProviderId);
                writer.WriteString("providerVersion", source.ProviderVersion);
                writer.WriteString("documentId", source.DocumentId);
                writer.WriteString("title", source.Title);
                writer.WriteStartArray("items");
                var locators = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in source.Items)
                {
                    if (locators.Count == 256 || item is null || !StrictJson.Text(item.Locator, 512, true) || !locators.Add(item.Locator)
                        || !StrictJson.Text(item.Title, 200, true) || !StrictJson.Text(item.Description, 4096, false) || item.AcceptanceCriteria is null) throw Invalid();
                    writer.WriteStartObject();
                    writer.WriteString("locator", item.Locator);
                    writer.WriteString("title", item.Title);
                    writer.WriteString("description", item.Description);
                    writer.WriteStartArray("criteria");
                    var localIndex = 0;
                    foreach (var criterion in item.AcceptanceCriteria)
                    {
                        if (localIndex == 16 || traces.Count == 32 || !StrictJson.Text(criterion, 4096, true)) throw Invalid();
                        writer.WriteStartObject();
                        writer.WriteNumber("criterionIndex", traces.Count);
                        writer.WriteString("text", criterion);
                        writer.WriteEndObject();
                        traces.Add(new(source.Id, source.Revision, item.Locator, localIndex++));
                    }
                    if (localIndex == 0) throw Invalid();
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                if (locators.Count == 0) throw Invalid();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            if (ids.Count == 0) throw Invalid();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            if (output.Length > AiTestCaseGenerator.ByteLimit) throw Invalid();
            return new(output.ToArray(), traces.AsReadOnly());
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }

    private static bool LowerHex(string? value, int length)
        => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static AiGenerationException Invalid() => new(AiGenerationError.InvalidRequest);
}
