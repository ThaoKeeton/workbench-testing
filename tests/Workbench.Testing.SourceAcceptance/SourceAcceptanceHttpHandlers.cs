using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Workbench.Testing.SourceAcceptance;

internal sealed class SourceAcceptanceFigmaHandler(SourceAcceptanceScenarioLedger ledger) : HttpMessageHandler
{
    private int _disposed;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => SourceAcceptanceTransport.SendAsync(ledger, SourceAcceptanceProvider.Figma, request, cancellationToken, () => IsDisposed);
    protected override void Dispose(bool disposing) { Volatile.Write(ref _disposed, 1); base.Dispose(disposing); }
}
internal sealed class SourceAcceptanceAiHandler(SourceAcceptanceScenarioLedger ledger) : HttpMessageHandler
{
    private int _disposed;
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => SourceAcceptanceTransport.SendAsync(ledger, SourceAcceptanceProvider.Ai, request, cancellationToken, () => IsDisposed);
    protected override void Dispose(bool disposing) { Volatile.Write(ref _disposed, 1); base.Dispose(disposing); }
}

// Terminal in-memory transport. It neither retains requests nor constructs a network-capable handler.
internal static class SourceAcceptanceTransport
{
    private const string Model = "gpt-5.4-mini-2026-03-17";
    private const string FigmaUri = "https://api.figma.com/v1/files/SourceAcceptanceFile1/nodes?ids=2%3A3%2C1%3A2&depth=2&version=source-v1";
    private const string FigmaJson = "{\"name\":\"Synthetic source acceptance file\",\"version\":\"source-v1\",\"nodes\":{\"2:3\":{\"document\":{\"id\":\"2:3\",\"name\":\"Synthetic heading\",\"type\":\"TEXT\",\"characters\":\"Synthetic heading text\"}},\"1:2\":{\"document\":{\"id\":\"1:2\",\"name\":\"Synthetic confirmation\",\"type\":\"TEXT\",\"characters\":\"Synthetic confirmation text\"}}}}";
    internal static async Task<HttpResponseMessage> SendAsync(SourceAcceptanceScenarioLedger ledger, SourceAcceptanceProvider provider,
        HttpRequestMessage request, CancellationToken token, Func<bool> isDisposed)
    {
        var ticket = ledger.Enter(provider); var outcome = "rejected";
        try
        {
            if (!ticket.Planned || isDisposed() || !(provider == SourceAcceptanceProvider.Figma ? ValidateFigma(request) : await ValidateAiAsync(request).ConfigureAwait(false))
                || !ledger.Accept(ticket)) throw Refused();
            ledger.StartReleaseMonitor(ticket);
            using var cancellation = token.Register(() => ledger.ObserveCancellation(ticket));
            if (ticket.Scenario == SourceAcceptanceScenario.Hold) await ticket.Release.Task.ConfigureAwait(false);
            var bytes = ticket.Scenario == SourceAcceptanceScenario.Malformed ? Encoding.UTF8.GetBytes("{malformed synthetic JSON")
                : provider == SourceAcceptanceProvider.Figma ? Encoding.UTF8.GetBytes(FigmaJson) : AiReply(ticket.Scenario);
            var response = new HttpResponseMessage(ticket.Scenario == SourceAcceptanceScenario.HttpFailure ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            outcome = "replied"; return response;
        }
        catch (Exception) { throw Refused(); }
        finally { ledger.Settle(ticket, outcome); }
    }
    private static HttpRequestException Refused() => new("Synthetic source request refused.");
    private static bool Header(HttpRequestMessage request, string name, string expected)
        => request.Headers.TryGetValues(name, out var values) && values.SequenceEqual([expected]);
    private static bool ValidateFigma(HttpRequestMessage request) => request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == FigmaUri
        && request.Content is null && request.Headers.Authorization is null && Header(request, "X-Figma-Token", SourceAcceptanceProtocol.FigmaToken);
    private static async Task<bool> ValidateAiAsync(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://api.openai.com/v1/responses"
            || !Header(request, "Authorization", "Bearer " + SourceAcceptanceProtocol.OpenAiKey) || request.Headers.Contains("X-Figma-Token")
            || request.Content is not { } content || content.Headers.ContentLength > 65536
            || content.Headers.ContentEncoding.Count != 0 || content.Headers.ContentType?.MediaType != "application/json"
            || content.Headers.ContentType.CharSet != "utf-8" || content.Headers.ContentType.Parameters.Count != 1) return false;
        using var bytes = new MemoryStream();
        await using (var stream = await content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 65537 - (int)bytes.Length)), CancellationToken.None).ConfigureAwait(false);
                if (read == 0) break;
                bytes.Write(buffer, 0, read); if (bytes.Length > 65536) return false;
            }
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        var root = document.RootElement;
        if (!Exact(root, "model", "reasoning", "store", "stream", "max_output_tokens", "instructions", "input", "text")
            || root.GetProperty("model").GetString() != Model || root.GetProperty("store").GetBoolean() || root.GetProperty("stream").GetBoolean()
            || root.GetProperty("max_output_tokens").GetInt32() != 8192 || !Exact(root.GetProperty("reasoning"), "effort")
            || root.GetProperty("reasoning").GetProperty("effort").GetString() != "none" || root.GetProperty("instructions").ValueKind != JsonValueKind.String) return false;
        var text = root.GetProperty("text");
        if (!Exact(text, "format")) return false;
        var format = text.GetProperty("format");
        if (!Exact(format, "type", "name", "strict", "schema") || format.GetProperty("type").GetString() != "json_schema"
            || format.GetProperty("name").GetString() != "test_cases" || !format.GetProperty("strict").GetBoolean() || !ValidateSchema(format.GetProperty("schema"))) return false;
        using var input = JsonDocument.Parse(root.GetProperty("input").GetString()!);
        if (!Exact(input.RootElement, "sources")) return false;
        var sources = input.RootElement.GetProperty("sources");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() != 1) return false;
        var source = sources[0];
        if (!Exact(source, "snapshotId", "snapshotRevision", "kind", "providerId", "providerVersion", "documentId", "title", "items")
            || !Hex(source.GetProperty("snapshotId").GetString(), 32) || !Hex(source.GetProperty("snapshotRevision").GetString(), 64)
            || source.GetProperty("kind").GetString() != "Figma" || source.GetProperty("providerId").GetString() != "figma.rest.nodes.manual-criteria"
            || source.GetProperty("providerVersion").GetString() != "1" || source.GetProperty("documentId").GetString() != "SourceAcceptanceFile1@source-v1"
            || source.GetProperty("title").GetString() != "Synthetic source acceptance file") return false;
        var items = source.GetProperty("items");
        return items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 2
            && Item(items[0], "2:3", "Synthetic heading", "Synthetic heading text", 0, ["Synthetic heading is visible.", "Synthetic navigation is reviewed manually."])
            && Item(items[1], "1:2", "Synthetic confirmation", "Synthetic confirmation text", 2, ["Synthetic confirmation is reviewed manually."]);
    }
    private static bool Item(JsonElement item, string locator, string title, string characters, int first, string[] expected)
    {
        if (!Exact(item, "locator", "title", "description", "criteria") || item.GetProperty("locator").GetString() != locator
            || item.GetProperty("title").GetString() != title || item.GetProperty("description").GetString() != "Observed type: TEXT\nObserved characters: " + characters) return false;
        var criteria = item.GetProperty("criteria");
        if (criteria.ValueKind != JsonValueKind.Array || criteria.GetArrayLength() != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++)
            if (!Exact(criteria[i], "criterionIndex", "text") || criteria[i].GetProperty("criterionIndex").GetInt32() != first + i
                || criteria[i].GetProperty("text").GetString() != expected[i]) return false;
        return true;
    }
    private static bool Exact(JsonElement element, params string[] names) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Count() == names.Length && names.All(name => element.TryGetProperty(name, out _))
        && element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == names.Length;
    private static bool Hex(string? value, int length) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidateSchema(JsonElement schema)
    {
        // Independent fixed contract, compared semantically (property order is immaterial).
        const string expected = """
            {"type":"object","properties":{"cases":{"type":"array","items":{"type":"object","properties":{"criterionIndex":{"type":"integer","minimum":0,"maximum":2},"title":{"type":"string","minLength":1,"maxLength":200},"prerequisites":{"type":"string","minLength":0,"maxLength":4096},"steps":{"type":"array","items":{"type":"object","properties":{"instruction":{"type":"string","minLength":1,"maxLength":4096},"expected":{"type":"string","minLength":1,"maxLength":4096}},"required":["instruction","expected"],"additionalProperties":false},"minItems":1,"maxItems":16}},"required":["criterionIndex","title","prerequisites","steps"],"additionalProperties":false},"minItems":1,"maxItems":32}},"required":["cases"],"additionalProperties":false}
            """;
        using var document = JsonDocument.Parse(expected);
        return Equal(schema, document.RootElement);
    }
    private static bool Equal(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) return false;
        if (expected.ValueKind == JsonValueKind.Object)
            return Exact(actual, expected.EnumerateObject().Select(p => p.Name).ToArray()) && expected.EnumerateObject().All(p => Equal(actual.GetProperty(p.Name), p.Value));
        if (expected.ValueKind == JsonValueKind.Array)
            return actual.GetArrayLength() == expected.GetArrayLength() && actual.EnumerateArray().Zip(expected.EnumerateArray()).All(p => Equal(p.First, p.Second));
        return JsonElement.DeepEquals(actual, expected);
    }
    private static byte[] AiReply(SourceAcceptanceScenario scenario) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        id = "resp_source_acceptance_1", model = Model, status = "completed",
        output = new[] { new { type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text",
            text = JsonSerializer.Serialize(new { cases = new[] { 2, 0, 1 }.Select(index => new { criterionIndex = index, title = "Synthetic manual case " + index,
                prerequisites = scenario == SourceAcceptanceScenario.Partial ? new string('p', 4096) : "Synthetic manual review only.",
                steps = new[] { new { instruction = "Review synthetic criterion " + index + " manually.", expected = "Synthetic criterion " + index + " is satisfied." } } }) }) } } } }
    });
}
