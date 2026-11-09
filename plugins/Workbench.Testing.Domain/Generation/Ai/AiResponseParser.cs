using System.Text;
using System.Text.Json;
using Workbench.Testing.Domain.Serialization;

namespace Workbench.Testing.Domain.Generation.Ai;

internal static class AiResponseParser
{
    internal static AiGenerationCapture Parse(AiInputCapture input, byte[] bytes, string key)
    {
        try
        {
            using var response = StrictJson.Parse(bytes, key);
            var root = response.RootElement;
            var responseId = Text(Property(root, "id"), 128, true);
            if (!responseId.StartsWith("resp_", StringComparison.Ordinal) || responseId.Length < 6
                || responseId.AsSpan(5).ContainsAnyExcept("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_-")) throw Invalid();
            var model = Text(Property(root, "model"), 128, true);
            if (model != AiTestCaseGenerator.Model) throw Invalid();
            if (Text(Property(root, "status"), 64, true) != "completed" || NonNull(root, "error") || NonNull(root, "incomplete_details"))
                throw new AiFailure(AiGenerationError.ResponseIncomplete);
            var message = Only(Property(root, "output"));
            if (Text(Property(message, "type"), 64, true) != "message" || Text(Property(message, "role"), 64, true) != "assistant"
                || Text(Property(message, "status"), 64, true) != "completed") throw Invalid();
            var part = Only(Property(message, "content"));
            var partType = Text(Property(part, "type"), 64, true);
            if (partType == "refusal") throw new AiFailure(AiGenerationError.ResponseRefused);
            if (partType != "output_text") throw Invalid();
            var text = Text(Property(part, "text"), AiTestCaseGenerator.ByteLimit, true);
            using var generated = StrictJson.Parse(Encoding.UTF8.GetBytes(text), key);
            ExactProperties(generated.RootElement, "cases");
            var values = Property(generated.RootElement, "cases");
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 32) throw Invalid();
            var cases = new List<AiGeneratedCase>();
            var covered = new bool[input.Traces.Count];
            var totalSteps = 0;
            foreach (var value in values.EnumerateArray())
            {
                ExactProperties(value, "criterionIndex", "title", "prerequisites", "steps");
                var index = Property(value, "criterionIndex");
                if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var criterionIndex) || criterionIndex < 0 || criterionIndex >= covered.Length) throw Invalid();
                covered[criterionIndex] = true;
                var title = Text(Property(value, "title"), 200, true);
                var prerequisites = Text(Property(value, "prerequisites"), 4096, false);
                var stepValues = Property(value, "steps");
                if (stepValues.ValueKind != JsonValueKind.Array || stepValues.GetArrayLength() is < 1 or > 16) throw Invalid();
                var steps = new List<AiGeneratedStep>();
                foreach (var step in stepValues.EnumerateArray())
                {
                    if (++totalSteps > 256) throw Invalid();
                    ExactProperties(step, "instruction", "expected");
                    steps.Add(new(Text(Property(step, "instruction"), 4096, true), Text(Property(step, "expected"), 4096, true)));
                }
                cases.Add(new(criterionIndex, title, prerequisites, steps));
            }
            if (covered.Any(value => !value)) throw Invalid();
            return new(input.Json, input.Traces, bytes, model, responseId, cases);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException or FormatException or ArgumentException)
        { throw Invalid(); }
    }

    private static bool NonNull(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null;
    private static JsonElement Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : throw Invalid();
    private static JsonElement Only(JsonElement value)
        => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1 ? value[0] : throw Invalid();
    private static string Text(JsonElement value, int maximum, bool required)
        => value.ValueKind == JsonValueKind.String && StrictJson.Text(value.GetString(), maximum, required) ? value.GetString()! : throw Invalid();
    private static void ExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != names.Length || names.Any(name => !value.TryGetProperty(name, out _))) throw Invalid();
    }
    private static AiFailure Invalid() => new(AiGenerationError.ResponseInvalid);
}
