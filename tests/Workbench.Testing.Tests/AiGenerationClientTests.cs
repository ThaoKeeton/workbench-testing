using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Sdk;
using Workbench.Testing.Domain.Generation.Ai;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class AiGenerationClientTests
{
    private const int ByteLimit = 256 * 1024;
    private const string Model = "gpt-5.4-mini-2026-03-17";
    private const string Secret = "synthetic-only-ai-key";
    private const string Id = "11111111111111111111111111111111";
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task One_fixed_POST_captures_literal_sources_before_credentials_and_requests_strict_manual_cases()
    {
        string[] criteria = ["  criterion 🧪  ", "second"];
        TestSourceItem[] items = [new("item/🧪", "Item", "literal https://never-follow.invalid Click", criteria, TestVirtualScenario.TaskFormPositive)];
        TestSourceSnapshot[] sources = [Source() with { Items = items }];
        using var transport = new RecordingHandler(_ => JsonResponse(Response(Cases(0, 1))));
        var client = new AiTestCaseGenerator(transport);
        Assert.Empty(transport.Requests);
        var keys = 0;
        var capture = await client.GenerateAsync(sources, _ =>
        {
            keys++;
            criteria[0] = Secret;
            items[0] = Item("changed");
            sources[0] = Source() with { Title = "changed" };
            return Key(default);
        });
        var sent = Assert.Single(transport.Requests);
        Assert.Equal(1, keys);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("https://api.openai.com/v1/responses", sent.Uri);
        Assert.Equal("Bearer " + Secret, sent.Authorization);
        Assert.Equal("application/json; charset=utf-8", sent.ContentType);
        Assert.Equal(new[] { "Authorization" }, sent.HeaderNames);
        var expectedInput = "{\"sources\":[{\"snapshotId\":\"" + Id + "\",\"snapshotRevision\":\"" + Revision
            + "\",\"kind\":\"Requirements\",\"providerId\":\"manual.requirements\",\"providerVersion\":\"1\",\"documentId\":\"doc\",\"title\":\"source\",\"items\":[{\"locator\":\"item/🧪\",\"title\":\"Item\",\"description\":\"literal https://never-follow.invalid Click\",\"criteria\":[{\"criterionIndex\":0,\"text\":\"  criterion 🧪  \"},{\"criterionIndex\":1,\"text\":\"second\"}]}]}]}";
        expectedInput = expectedInput.Replace("🧪", "\\uD83E\\uDDEA", StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes(expectedInput), capture.InputJson);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput))), capture.InputSha256);
        using var request = JsonDocument.Parse(sent.Body);
        var root = request.RootElement;
        Assert.Equal(new[] { "input", "instructions", "max_output_tokens", "model", "reasoning", "store", "stream", "text" }, root.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(8192, root.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("none", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(expectedInput, root.GetProperty("input").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("instructions").GetString()));
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("test_cases", format.GetProperty("name").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema");
        ClosedObject(schema, "cases");
        var caseArray = schema.GetProperty("properties").GetProperty("cases");
        Assert.Equal(1, caseArray.GetProperty("minItems").GetInt32());
        Assert.Equal(32, caseArray.GetProperty("maxItems").GetInt32());
        var caseSchema = caseArray.GetProperty("items");
        ClosedObject(caseSchema, "criterionIndex", "title", "prerequisites", "steps");
        var properties = caseSchema.GetProperty("properties");
        Assert.Equal("integer", properties.GetProperty("criterionIndex").GetProperty("type").GetString());
        Assert.Equal(0, properties.GetProperty("criterionIndex").GetProperty("minimum").GetInt32());
        Assert.Equal(1, properties.GetProperty("criterionIndex").GetProperty("maximum").GetInt32());
        StringBounds(properties.GetProperty("title"), 1, 200);
        StringBounds(properties.GetProperty("prerequisites"), 0, 4096);
        var steps = properties.GetProperty("steps");
        Assert.Equal(1, steps.GetProperty("minItems").GetInt32());
        Assert.Equal(16, steps.GetProperty("maxItems").GetInt32());
        var stepSchema = steps.GetProperty("items");
        ClosedObject(stepSchema, "instruction", "expected");
        StringBounds(stepSchema.GetProperty("properties").GetProperty("instruction"), 1, 4096);
        StringBounds(stepSchema.GetProperty("properties").GetProperty("expected"), 1, 4096);
        Assert.Equal(new[] { new TestTrace(Id, Revision, "item/🧪", 0), new TestTrace(Id, Revision, "item/🧪", 1) }, capture.OriginalTraces);
        Assert.All(capture.Cases, c => Assert.All(c.Steps, s => Assert.Null(s.Action)));
        Assert.False(transport.Disposed);
        await client.GenerateAsync([Source() with { Items = [Item("item", "a", "b")] }], _ => Task.FromResult<string?>("other-synthetic-key"));
        Assert.Equal("Bearer other-synthetic-key", transport.Requests[1].Authorization);
    }

    [Fact]
    public async Task Capture_preserves_response_bytes_plain_values_and_all_ordered_original_traces_defensively()
    {
        var raw = Encoding.UTF8.GetBytes(" \n" + Response(Cases(2, 0, 1), future: new { url = "https://never-follow.invalid", actions = new[] { "Click" } }) + "\n");
        using var transport = new RecordingHandler(_ => BytesResponse(raw));
        var second = Source("22222222222222222222222222222222") with { Kind = TestSourceKind.Figma, Items = [Item("last", "other")] };
        var first = Source() with { Items = [Item("first", "zero", "one")] };
        var capture = await new AiTestCaseGenerator(transport).GenerateAsync([first, second], Key);
        Assert.Equal(Model, capture.Model);
        Assert.Equal("resp_synthetic-1", capture.ResponseId);
        Assert.Equal(raw, capture.ResponseJson);
        raw[0] = 0;
        capture.ResponseJson[1] = 0;
        capture.InputJson[0] = 0;
        Assert.Equal((byte)' ', capture.ResponseJson[0]);
        Assert.Equal((byte)'\n', capture.ResponseJson[1]);
        Assert.Equal((byte)'{', capture.InputJson[0]);
        Assert.Equal(new[] { new TestTrace(Id, Revision, "first", 0), new TestTrace(Id, Revision, "first", 1), new TestTrace(second.Id, Revision, "last", 0) }, capture.OriginalTraces);
        Assert.Equal(new[] { 2, 0, 1 }, capture.Cases.Select(c => c.CriterionIndex));
        Assert.Equal("  Generated 🧪  ", capture.Cases[0].Title);
        Assert.Equal("", capture.Cases[0].Prerequisites);
        Assert.Equal("  Click is literal  ", capture.Cases[0].Steps[0].Instruction);
        Assert.Equal("expected 🧪", capture.Cases[0].Steps[0].Expected);
        Assert.Throws<NotSupportedException>(() => ((IList<TestTrace>)capture.OriginalTraces).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AiGeneratedCase>)capture.Cases).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<AiGeneratedStep>)capture.Cases[0].Steps).Clear());
        Assert.All(capture.Cases, c => Assert.All(c.Steps, s => Assert.Null(s.Action)));
    }

    [Fact]
    public async Task Source_item_and_criterion_collections_are_enumerated_once_before_key_evaluation()
    {
        var criteria = new OnceList<string>(["criterion"]);
        var items = new OnceList<TestSourceItem>([Item() with { AcceptanceCriteria = criteria }]);
        var sources = new OnceList<TestSourceSnapshot>([Source() with { Items = items, OriginalBytes = null!, OriginalSha256 = null! }]);
        using var transport = new RecordingHandler(_ => JsonResponse(Response()));
        await new AiTestCaseGenerator(transport).GenerateAsync(sources, _ =>
        {
            Assert.Equal(1, sources.Enumerations);
            Assert.Equal(1, items.Enumerations);
            Assert.Equal(1, criteria.Enumerations);
            return Key(default);
        });
        Assert.Equal(1, sources.Enumerations);
        Assert.Equal(1, items.Enumerations);
        Assert.Equal(1, criteria.Enumerations);
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        yield return [null!];
        yield return [Array.Empty<TestSourceSnapshot>()];
        yield return [new TestSourceSnapshot[] { null! }];
        yield return [new[] { Source(), Source() }];
        yield return [Enumerable.Range(1, 9).Select(i => Source(i.ToString("x32"))).ToArray()];
        foreach (var id in new[] { "", "1", new string('A', 32), new string('g', 32), new string('1', 33) }) yield return [new[] { Source() with { Id = id } }];
        foreach (var revision in new[] { "", new string('a', 63), new string('A', 64), new string('z', 64) }) yield return [new[] { Source() with { Revision = revision } }];
        yield return [new[] { Source() with { Kind = (TestSourceKind)99 } }];
        yield return [new[] { Source() with { ProviderId = "openai.responses.test-cases" } }];
        foreach (var text in new[] { null, "", " \t", "\ud800", new string('p', 101) }) yield return [new[] { Source() with { ProviderId = text! } }];
        yield return [new[] { Source() with { ProviderVersion = new string('v', 41) } }];
        yield return [new[] { Source() with { ProviderVersion = "" } }];
        yield return [new[] { Source() with { DocumentId = new string('d', 201) } }];
        yield return [new[] { Source() with { DocumentId = "" } }];
        foreach (var text in new[] { null, "", " \t", "\udc00", new string('t', 201) }) yield return [new[] { Source() with { Title = text! } }];
        yield return [new[] { Source() with { Items = null! } }];
        yield return [new[] { Source() with { Items = [] } }];
        yield return [new[] { Source() with { Items = [null!] } }];
        yield return [new[] { Source() with { Items = [Item(), Item()] } }];
        yield return [new[] { Source() with { Items = Enumerable.Range(0, 257).Select(i => Item(i.ToString())).ToArray() } }];
        foreach (var text in new[] { null, "", " \t", "\ud800", new string('l', 513) }) yield return [new[] { Source() with { Items = [Item() with { Locator = text! }] } }];
        foreach (var text in new[] { null, "", " \t", "\ud800", new string('t', 201) }) yield return [new[] { Source() with { Items = [Item() with { Title = text! }] } }];
        foreach (var text in new[] { null, "\ud800", new string('d', 4097) }) yield return [new[] { Source() with { Items = [Item() with { Description = text! }] } }];
        yield return [new[] { Source() with { Items = [Item() with { AcceptanceCriteria = null! }] } }];
        yield return [new[] { Source() with { Items = [Item() with { AcceptanceCriteria = [] }] } }];
        yield return [new[] { Source() with { Items = [Item() with { AcceptanceCriteria = Enumerable.Repeat("c", 17).ToArray() }] } }];
        foreach (var text in new[] { null, "", " \t", "\ud800", new string('c', 4097) }) yield return [new[] { Source() with { Items = [Item("item", text!)] } }];
        yield return [new[] { Source() with { Items = Enumerable.Range(0, 3).Select(i => Item(i.ToString(), Enumerable.Repeat("c", 11).ToArray())).ToArray() } }];
        yield return [new ThrowingList<TestSourceSnapshot>()];
        yield return [new[] { Source() with { Items = new ThrowingList<TestSourceItem>() } }];
        yield return [new[] { Source() with { Items = [Item() with { AcceptanceCriteria = new ThrowingList<string>() }] } }];
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void Invalid_input_throws_synchronously_before_any_key_or_HTTP(IReadOnlyList<TestSourceSnapshot> sources)
    {
        var keys = 0;
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException(Secret));
        var error = Assert.Throws<AiGenerationException>(() => { _ = new AiTestCaseGenerator(transport).GenerateAsync(sources, _ => { keys++; return Key(default); }); });
        Assert.Equal(AiGenerationError.InvalidRequest, error.Error);
        Assert.Null(error.InnerException);
        Assert.Equal(0, keys);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void Null_key_callback_is_an_invalid_request_before_HTTP()
    {
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException(Secret));
        var error = Assert.Throws<AiGenerationException>(() => { _ = new AiTestCaseGenerator(transport).GenerateAsync([Source()], null!); });
        Assert.Equal(AiGenerationError.InvalidRequest, error.Error);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Eight_sources_and_32_criteria_are_accepted_and_every_criterion_requires_coverage()
    {
        var sources = Enumerable.Range(1, 8).Select(i => Source(i.ToString("x32")) with { Items = [Item("item", "a", "b", "c", "d")] }).ToArray();
        using var transport = new RecordingHandler(_ => JsonResponse(Response(Cases(Enumerable.Range(0, 32).ToArray()))));
        var capture = await new AiTestCaseGenerator(transport).GenerateAsync(sources, Key);
        Assert.Equal(32, capture.OriginalTraces.Count);
        Assert.Equal(32, capture.Cases.Count);
        Assert.Equal(sources.Select(s => s.Id), capture.OriginalTraces.Where((_, i) => i % 4 == 0).Select(t => t.SnapshotId));
    }

    [Fact]
    public async Task Input_byte_limit_is_exact_without_truncation_and_overflow_precedes_credentials()
    {
        // Hand-counted JSON overhead: 1420 bytes, plus 32 * 4096 ASCII text units.
        // 64826 substitutions with U+754C add 129652 UTF-8 bytes: total exactly 262144.
        var remaining = 64826;
        var criteria = Enumerable.Range(0, 32).Select(_ =>
        {
            var count = Math.Min(4096, remaining); remaining -= count;
            return new string('界', count) + new string('x', 4096 - count);
        }).ToArray();
        var source = Source() with { Items = Enumerable.Range(0, 2).Select(i => new TestSourceItem($"item-{i}", "Item", "", criteria.Skip(i * 16).Take(16).ToArray(), null)).ToArray() };
        using var transport = new RecordingHandler(_ => JsonResponse(Response(Cases(Enumerable.Range(0, 32).ToArray()))));
        var capture = await new AiTestCaseGenerator(transport).GenerateAsync([source], Key);
        Assert.Equal(ByteLimit, capture.InputJson.Length);
        var keyCalls = 0;
        var error = Assert.Throws<AiGenerationException>(() => { _ = new AiTestCaseGenerator(transport).GenerateAsync([source with { Title = "source!" }], _ => { keyCalls++; return Key(default); }); });
        Assert.Equal(AiGenerationError.InvalidRequest, error.Error);
        Assert.Equal(0, keyCalls);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task SDK_text_maxima_are_preserved_and_original_bytes_and_virtual_scenarios_are_ignored()
    {
        var source = Source() with
        {
            ProviderId = new string('p', 100), ProviderVersion = new string('v', 40), DocumentId = new string('d', 200), Title = new string('t', 200),
            OriginalBytes = [0xff], OriginalSha256 = "ignored", Items = [new(new string('l', 512), new string('i', 200), new string('d', 4096), [new string('c', 4096)], (TestVirtualScenario)999)]
        };
        using var transport = new RecordingHandler(_ => JsonResponse(Response()));
        var capture = await new AiTestCaseGenerator(transport).GenerateAsync([source], Key);
        using var input = JsonDocument.Parse(capture.InputJson);
        var item = input.RootElement.GetProperty("sources")[0].GetProperty("items")[0];
        Assert.Equal(new string('d', 4096), item.GetProperty("description").GetString());
        Assert.Equal(new string('c', 4096), item.GetProperty("criteria")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("Original", Encoding.UTF8.GetString(capture.InputJson), StringComparison.Ordinal);
        Assert.Null(capture.Cases[0].Steps[0].Action);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("x y")] [InlineData("x\r\ny")] [InlineData("🧪")] [InlineData("x\u007f")]
    public async Task Unavailable_or_invalid_key_never_reaches_HTTP(string? key)
    {
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException(Secret));
        await ErrorAsync(AiGenerationError.CredentialsUnavailable, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], _ => Task.FromResult(key)));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Key_length_and_unexpected_callback_failure_remain_safe()
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Response()));
        var client = new AiTestCaseGenerator(transport);
        await client.GenerateAsync([Source()], _ => Task.FromResult<string?>(new string('k', 4096)));
        await ErrorAsync(AiGenerationError.CredentialsUnavailable, () => client.GenerateAsync([Source()], _ => Task.FromResult<string?>(new string('k', 4097))));
        await ErrorAsync(AiGenerationError.CredentialsUnavailable, () => client.GenerateAsync([Source()], _ => throw new InvalidOperationException(Secret)));
        await ErrorAsync(AiGenerationError.CredentialsUnavailable, () => client.GenerateAsync([Source()], _ => throw new AiGenerationException(AiGenerationError.RateLimited)));
        await ErrorAsync(AiGenerationError.CredentialsUnavailable, () => client.GenerateAsync([Source()], _ => null!));
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("synthetic-only-ai-key")] [InlineData("synthetic-\"-key")]
    public async Task Credential_in_captured_input_is_refused_after_lookup_before_HTTP_including_JSON_escaping(string key)
    {
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException(Secret));
        var calls = 0;
        var source = Source() with { Items = [Item() with { Description = "before " + key + " after" }] };
        var error = await ErrorAsync(AiGenerationError.InvalidRequest, () => new AiTestCaseGenerator(transport).GenerateAsync([source], _ => { calls++; return Task.FromResult<string?>(key); }));
        Assert.Equal(1, calls);
        Assert.Empty(transport.Requests);
        Assert.DoesNotContain(key, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, AiGenerationError.AuthenticationRefused)] [InlineData(403, AiGenerationError.AuthenticationRefused)]
    [InlineData(429, AiGenerationError.RateLimited)] [InlineData(302, AiGenerationError.Unavailable)]
    [InlineData(400, AiGenerationError.Unavailable)] [InlineData(404, AiGenerationError.Unavailable)] [InlineData(500, AiGenerationError.Unavailable)]
    public async Task HTTP_failures_have_closed_errors_and_never_read_error_bodies_or_retry(int status, AiGenerationError expected)
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes(Secret));
        using var transport = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body), ReasonPhrase = Secret };
            response.Headers.TryAddWithoutValidation("Location", "https://never-follow.invalid/" + Secret);
            return response;
        });
        await ErrorAsync(expected, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
        Assert.Equal(0, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("0", 0)] [InlineData("86400", 86400)] [InlineData("123", 123)]
    [InlineData("86401", null)] [InlineData("-1", null)] [InlineData("1.5", null)] [InlineData("+1", null)]
    [InlineData("Wed, 21 Oct 2015 07:28:00 GMT", null)] [InlineData("999999999999999999", null)] [InlineData("1, 2", null)]
    public async Task Rate_limit_retains_only_one_integer_Retry_After_within_a_day(string value, int? expected)
    {
        using var transport = new RecordingHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.TryAddWithoutValidation("Retry-After", value); return response; });
        var error = await ErrorAsync(AiGenerationError.RateLimited, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
        Assert.Equal(expected, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task Multiple_retry_headers_are_discarded_and_foreign_exception_classifications_are_not_trusted()
    {
        using var transport = new RecordingHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.TryAddWithoutValidation("Retry-After", new[] { "1", "2" }); return r; });
        Assert.Null((await ErrorAsync(AiGenerationError.RateLimited, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key))).RetryAfterSeconds);
        using var throwing = new RecordingHandler(_ => throw new AiGenerationException(AiGenerationError.RateLimited, 10));
        Assert.Null((await ErrorAsync(AiGenerationError.Unavailable, () => new AiTestCaseGenerator(throwing).GenerateAsync([Source()], Key))).RetryAfterSeconds);
        using var network = new RecordingHandler(_ => throw new HttpRequestException(Secret, new IOException(Secret)));
        await ErrorAsync(AiGenerationError.Unavailable, () => new AiTestCaseGenerator(network).GenerateAsync([Source()], Key));
    }

    [Theory]
    [InlineData(null, null)] [InlineData("text/html", null)] [InlineData("application/problem+json", null)]
    [InlineData("application/json; charset=utf-16", null)] [InlineData("application/json", "gzip")]
    [InlineData("application/json; profile=x", null)] [InlineData("application/json; charset=utf-8; profile=x", null)]
    [InlineData("application/json; charset=utf-8; charset=utf-8", null)]
    public async Task Unsupported_response_headers_are_rejected_before_reading(string? mediaType, string? encoding)
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes(Response()));
        using var transport = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (mediaType is not null) response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            if (encoding is not null) response.Content.Headers.ContentEncoding.Add(encoding);
            return response;
        });
        await ErrorAsync(AiGenerationError.ResponseInvalid, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
        Assert.Equal(0, body.BytesRead);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Oversized_header_or_stream_reads_at_most_the_cap_plus_one_byte(bool declared)
    {
        using var body = new TrackingStream(new byte[ByteLimit + 8192]);
        using var transport = new RecordingHandler(_ => { var r = StreamResponse(body); if (declared) r.Content.Headers.ContentLength = ByteLimit + 8192; return r; });
        await ErrorAsync(AiGenerationError.ResponseTooLarge, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
        Assert.Equal(declared ? 0 : ByteLimit + 1, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Exactly_256_KiB_of_valid_unencoded_JSON_with_UTF8_charset_is_accepted()
    {
        var json = Encoding.UTF8.GetBytes(Response());
        var bytes = json.Concat(Enumerable.Repeat((byte)' ', ByteLimit - json.Length)).ToArray();
        using var transport = new RecordingHandler(_ => { var response = BytesResponse(bytes); response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=\"UTF-8\""); return response; });
        Assert.Equal(bytes, (await new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key)).ResponseJson);
    }

    public static IEnumerable<object[]> InvalidEnvelopes()
    {
        var valid = Response();
        yield return ["{"];
        yield return ["null"];
        yield return [valid.Replace("\"id\":", "\"id\":\"resp_other\",\"id\":", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":null", "\"future\":{\"x\":1,\"\\u0078\":2}", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":null", "\"future\":{\"\\uD800\":1}", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":null", "\"future\":\"\\uDC00\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":null", "\"future\":\"\\uD800x\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":null", "\"future\":" + new string('[', 33) + "0" + new string(']', 33), StringComparison.Ordinal)];
        yield return [valid.Replace(Model, "other-model", StringComparison.Ordinal)];
        foreach (var id in new[] { "", "resp_", "response_x", "resp_x/y", "resp_🧪", "resp_" + new string('x', 124) }) yield return [valid.Replace("resp_synthetic-1", id, StringComparison.Ordinal)];
        yield return [valid.Replace("\"role\":\"assistant\"", "\"role\":\"user\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"type\":\"message\"", "\"type\":\"function_call\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"type\":\"output_text\"", "\"type\":\"tool_call\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"status\":\"completed\",\"role\"", "\"status\":\"in_progress\",\"role\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"output\":[", "\"output\":[],\"ignored\":[", StringComparison.Ordinal)];
        yield return [valid.Replace("\"content\":[", "\"content\":[],\"ignored\":[", StringComparison.Ordinal)];
        using var doc = JsonDocument.Parse(valid);
        var message = doc.RootElement.GetProperty("output")[0].GetRawText();
        yield return [valid.Replace(message, message + "," + message, StringComparison.Ordinal)];
        var part = doc.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetRawText();
        yield return [valid.Replace(part, part + "," + part, StringComparison.Ordinal)];
        yield return [valid.Replace("\"text\":", "\"text\":null,\"ignored\":", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(InvalidEnvelopes))]
    public async Task Invalid_envelope_duplicate_Unicode_depth_model_and_tool_output_are_rejected(string json)
    {
        using var transport = new RecordingHandler(_ => JsonResponse(json));
        await ErrorAsync(AiGenerationError.ResponseInvalid, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
    }

    [Theory]
    [InlineData("incomplete", AiGenerationError.ResponseIncomplete)] [InlineData("in_progress", AiGenerationError.ResponseIncomplete)]
    [InlineData("failed", AiGenerationError.ResponseIncomplete)] [InlineData("queued", AiGenerationError.ResponseIncomplete)]
    public async Task Noncompleted_response_is_distinguishable_from_malformed_output(string status, AiGenerationError expected)
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Response().Replace("\"status\":\"completed\"", "\"status\":\"" + status + "\"", StringComparison.Ordinal)));
        await ErrorAsync(expected, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
    }

    [Fact]
    public async Task Refusal_error_and_incomplete_details_are_distinguished_without_provider_text()
    {
        using var refusal = new RecordingHandler(_ => JsonResponse(Response().Replace("\"type\":\"output_text\"", "\"type\":\"refusal\",\"refusal\":\"private upstream reason\"", StringComparison.Ordinal)));
        await ErrorAsync(AiGenerationError.ResponseRefused, () => new AiTestCaseGenerator(refusal).GenerateAsync([Source()], Key));
        foreach (var field in new[] { "error", "incomplete_details" })
        {
            using var transport = new RecordingHandler(_ => JsonResponse(Response().Replace("\"" + field + "\":null", "\"" + field + "\":{\"message\":\"private upstream reason\"}", StringComparison.Ordinal)));
            await ErrorAsync(AiGenerationError.ResponseIncomplete, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
        }
    }

    [Theory]
    [InlineData("resp_a")] [InlineData("resp_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_-")]
    public async Task Valid_response_ID_forms_are_retained(string id)
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Response().Replace("resp_synthetic-1", id, StringComparison.Ordinal)));
        Assert.Equal(id, (await new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key)).ResponseId);
    }

    [Fact]
    public async Task Response_ID_maximum_128_units_is_accepted()
    {
        var id = "resp_" + new string('a', 123);
        using var transport = new RecordingHandler(_ => JsonResponse(Response().Replace("resp_synthetic-1", id, StringComparison.Ordinal)));
        Assert.Equal(id, (await new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key)).ResponseId);
    }

    public static IEnumerable<object[]> InvalidGeneratedJson()
    {
        var valid = Cases();
        yield return ["null"];
        yield return ["{\"cases\":[]}"];
        yield return [valid[..^1] + ",\"extra\":null}"];
        yield return [valid.Replace("\"criterionIndex\":0", "\"criterionIndex\":0,\"extra\":null", StringComparison.Ordinal)];
        yield return [valid.Replace("\"instruction\":", "\"action\":{\"kind\":\"Click\"},\"instruction\":", StringComparison.Ordinal)];
        yield return [valid.Replace("\"criterionIndex\":0", "\"criterionIndex\":-1", StringComparison.Ordinal)];
        yield return [valid.Replace("\"criterionIndex\":0", "\"criterionIndex\":1", StringComparison.Ordinal)];
        yield return [valid.Replace("\"criterionIndex\":0", "\"criterionIndex\":0.0", StringComparison.Ordinal)];
        yield return [valid.Replace("\"criterionIndex\":0", "\"criterionIndex\":\"0\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"criterionIndex\":0,", "", StringComparison.Ordinal)];
        yield return [valid.Replace("\"title\":", "\"title\":\"duplicate\",\"title\":", StringComparison.Ordinal)];
        yield return [valid.Replace("\"expected\":", "\"\\u0065xpected\":\"duplicate\",\"expected\":", StringComparison.Ordinal)];
        yield return ["{\"cases\":[{\"criterionIndex\":0,\"title\":\"a\",\"prerequisites\":\"\",\"steps\":[]}]}"];
        yield return [valid.Replace("\"steps\":[", "\"steps\":null,\"ignored\":[", StringComparison.Ordinal)];
        foreach (var field in new[] { "title", "instruction", "expected", "prerequisites" })
        {
            using var doc = JsonDocument.Parse(valid);
            var item = doc.RootElement.GetProperty("cases")[0];
            var old = field is "title" or "prerequisites" ? item.GetProperty(field).GetRawText() : item.GetProperty("steps")[0].GetProperty(field).GetRawText();
            var maximum = field == "title" ? 200 : 4096;
            foreach (var value in new[] { "null", "\"\\uD800\"", JsonSerializer.Serialize(new string('x', maximum + 1)) })
                yield return [valid.Replace("\"" + field + "\":" + old, "\"" + field + "\":" + value, StringComparison.Ordinal)];
            var node = System.Text.Json.Nodes.JsonNode.Parse(valid)!;
            var parent = field is "title" or "prerequisites" ? node["cases"]![0]! : node["cases"]![0]!["steps"]![0]!;
            parent.AsObject().Remove(field);
            yield return [node.ToJsonString()];
            if (field != "prerequisites")
                foreach (var value in new[] { "\"\"", "\" \\t\"" }) yield return [valid.Replace("\"" + field + "\":" + old, "\"" + field + "\":" + value, StringComparison.Ordinal)];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidGeneratedJson))]
    public async Task Generated_schema_is_closed_complete_bounded_and_strict_about_indices_and_Unicode(string generated)
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Response(generated)));
        await ErrorAsync(AiGenerationError.ResponseInvalid, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
    }

    [Fact]
    public async Task Every_original_criterion_is_covered_and_repeated_coverage_is_allowed()
    {
        var source = Source() with { Items = [Item("item", "first", "second")] };
        using var missing = new RecordingHandler(_ => JsonResponse(Response(Cases(0))));
        await ErrorAsync(AiGenerationError.ResponseInvalid, () => new AiTestCaseGenerator(missing).GenerateAsync([source], Key));
        using var transport = new RecordingHandler(_ => JsonResponse(Response(Cases(1, 0, 1))));
        Assert.Equal(new[] { 1, 0, 1 }, (await new AiTestCaseGenerator(transport).GenerateAsync([source], Key)).Cases.Select(c => c.CriterionIndex));
    }

    [Theory]
    [InlineData(1, 1, true)] [InlineData(32, 1, true)] [InlineData(33, 1, false)]
    [InlineData(1, 16, true)] [InlineData(1, 17, false)]
    [InlineData(16, 16, true)] [InlineData(17, 16, false)]
    public async Task Case_per_case_step_and_total_step_caps_are_enforced(int cases, int steps, bool accepted)
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Response(Generated(Enumerable.Repeat(0, cases).ToArray(), steps))));
        var operation = new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key);
        if (accepted) Assert.Equal(cases * steps, (await operation).Cases.Sum(c => c.Steps.Count));
        else await ErrorAsync(AiGenerationError.ResponseInvalid, () => operation);
    }

    [Fact]
    public async Task Maximum_output_text_lengths_are_UTF16_units_and_literal_whitespace_is_preserved()
    {
        var title = string.Concat(Enumerable.Repeat("🧪", 100));
        var longText = string.Concat(Enumerable.Repeat("🧪", 2048));
        var generated = JsonSerializer.Serialize(new { cases = new[] { new { criterionIndex = 0, title, prerequisites = longText, steps = new[] { new { instruction = longText, expected = longText } } } } }, JsonOptions);
        using var transport = new RecordingHandler(_ => JsonResponse(Response(generated)));
        var item = Assert.Single((await new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key)).Cases);
        Assert.Equal(title, item.Title);
        Assert.Equal(longText, item.Prerequisites);
        Assert.Equal(longText, Assert.Single(item.Steps).Instruction);
    }

    [Theory]
    [InlineData("raw")] [InlineData("escaped-envelope-value")] [InlineData("escaped-property")]
    [InlineData("generated-text")] [InlineData("escaped-generated-text")]
    public async Task Credential_reflection_is_refused_even_when_decoded_from_JSON_strings_or_property_names(string place)
    {
        var escaped = string.Concat(Secret.Select(c => "\\u" + ((int)c).ToString("x4")));
        var json = place switch
        {
            "raw" => Response(future: Secret),
            "escaped-envelope-value" => Response().Replace("\"future\":null", "\"future\":\"" + escaped + "\"", StringComparison.Ordinal),
            "escaped-property" => Response().Replace("\"future\":null", "\"" + escaped + "\":null", StringComparison.Ordinal),
            "generated-text" => Response(Cases().Replace("expected \\uD83E\\uDDEA", Secret, StringComparison.Ordinal)),
            _ => Response(Cases().Replace("expected \\uD83E\\uDDEA", escaped, StringComparison.Ordinal))
        };
        using var transport = new RecordingHandler(_ => JsonResponse(json));
        await ErrorAsync(AiGenerationError.ResponseInvalid, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
    }

    [Fact]
    public async Task Invalid_UTF8_and_unexpected_stream_creation_read_or_disposal_failures_are_safe()
    {
        using var invalid = new RecordingHandler(_ => BytesResponse([0xff, 0xfe]));
        await ErrorAsync(AiGenerationError.ResponseInvalid, () => new AiTestCaseGenerator(invalid).GenerateAsync([Source()], Key));
        foreach (var stage in new[] { "stream", "body", "dispose" })
        {
            using var transport = new RecordingHandler(_ => stage == "stream"
                ? ContentResponse(new ControlledContent(_ => throw new IOException(Secret)))
                : StreamResponse(new FailingStream(Encoding.UTF8.GetBytes(Response()), stage)));
            await ErrorAsync(AiGenerationError.Unavailable, () => new AiTestCaseGenerator(transport).GenerateAsync([Source()], Key));
        }
    }

    [Theory]
    [InlineData("credential")] [InlineData("transport")] [InlineData("stream")] [InlineData("body")] [InlineData("dispose")]
    public async Task Ignored_cancellation_keeps_actual_work_owned_until_it_settles_then_sanitizes(string stage)
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new GatedStream(Encoding.UTF8.GetBytes(Response()), stage, entered, release);
        using var transport = new RecordingHandler(async (_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            if (stage == "transport") { entered.SetResult(); await release.Task; }
            if (stage == "stream") return ContentResponse(new ControlledContent(async _ => { entered.SetResult(); await release.Task; return body; }));
            return stage is "body" or "dispose" ? StreamResponse(body) : JsonResponse(Response());
        });
        async Task<string?> Credential(CancellationToken token)
        {
            Assert.True(token.CanBeCanceled);
            if (stage == "credential") { entered.SetResult(); await release.Task; }
            return Secret;
        }
        var pending = new AiTestCaseGenerator(transport).GenerateAsync([Source()], Credential, caller.Token);
        await entered.Task;
        caller.Cancel();
        Assert.False(pending.IsCompleted);
        if (stage == "body") Assert.False(body.Disposed);
        release.SetResult();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal("AI generation canceled.", error.Message);
        if (stage == "credential") Assert.Empty(transport.Requests);
        if (stage is "body" or "stream" or "dispose") Assert.True(body.Disposed);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task Already_cancelled_caller_does_not_evaluate_key_or_transport()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel();
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException(Secret));
        var keys = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiTestCaseGenerator(transport).GenerateAsync([Source()], _ => { keys++; return Key(default); }, caller.Token));
        Assert.Equal(0, keys);
        Assert.Empty(transport.Requests);
    }

    [Fact(Timeout = 120000)]
    public async Task Cooperative_90_second_deadline_covers_key_work_and_is_distinct_from_caller_cancellation()
    {
        var elapsed = Stopwatch.StartNew();
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException(Secret));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new AiTestCaseGenerator(transport).GenerateAsync([Source()], async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { entered.SetResult(); }
            await release.Task;
            return Secret;
        });
        await entered.Task;
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 89, 110);
        Assert.False(pending.IsCompleted);
        release.SetResult();
        await ErrorAsync(AiGenerationError.Timeout, () => pending);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void Public_error_enum_and_retry_metadata_are_closed()
    {
        var error = new AiGenerationException((AiGenerationError)999, 10);
        Assert.Equal(AiGenerationError.Unavailable, error.Error);
        Assert.Null(error.RetryAfterSeconds);
        Assert.Null(new AiGenerationException(AiGenerationError.RateLimited, 86401).RetryAfterSeconds);
        Assert.Null(new AiGenerationException(AiGenerationError.RateLimited, -1).RetryAfterSeconds);
        Assert.Null(new AiGenerationException(AiGenerationError.ResponseInvalid, 1).RetryAfterSeconds);
    }

    private static TestSourceSnapshot Source(string id = Id) => new(id, "22222222222222222222222222222222", "workbench.testing", DateTimeOffset.UnixEpoch,
        Revision, TestSourceKind.Requirements, "manual.requirements", "1", "doc", "source", [], "ignored", [Item()]);
    private static TestSourceItem Item(string locator = "item", params string[] criteria) => new(locator, "Item", "", criteria.Length == 0 ? ["criterion"] : criteria, null);
    private static Task<string?> Key(CancellationToken _) => Task.FromResult<string?>(Secret);
    private static string Cases(params int[] indices) => Generated(indices.Length == 0 ? [0] : indices, 1);
    private static string Generated(int[] indices, int steps) => JsonSerializer.Serialize(new { cases = indices.Select(criterionIndex => new
    {
        criterionIndex, title = "  Generated 🧪  ", prerequisites = "", steps = Enumerable.Range(0, steps).Select(_ => new { instruction = "  Click is literal  ", expected = "expected 🧪" }).ToArray()
    }).ToArray() }, JsonOptions);
    private static string Response(string? generated = null, object? future = null) => JsonSerializer.Serialize(new
    {
        id = "resp_synthetic-1", model = Model, status = "completed", error = (object?)null, incomplete_details = (object?)null,
        output = new[] { new { id = "msg_synthetic", type = "message", status = "completed", role = "assistant", content = new[] { new { type = "output_text", text = generated ?? Cases(), annotations = Array.Empty<object>() } } } }, future
    }, JsonOptions);
    private static HttpResponseMessage JsonResponse(string json) => BytesResponse(Encoding.UTF8.GetBytes(json));
    private static HttpResponseMessage BytesResponse(byte[] bytes) => ContentResponse(new ByteArrayContent(bytes));
    private static HttpResponseMessage StreamResponse(Stream stream) => ContentResponse(new StreamContent(stream));
    private static HttpResponseMessage ContentResponse(HttpContent content)
    {
        content.Headers.ContentType = new("application/json");
        return new(HttpStatusCode.OK) { Content = content };
    }
    private static async Task<AiGenerationException> ErrorAsync(AiGenerationError expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<AiGenerationException>(action);
        Assert.Equal(expected, error.Error);
        Assert.Equal("AI generation failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private upstream reason", error.ToString(), StringComparison.Ordinal);
        return error;
    }
    private static void ClosedObject(JsonElement schema, params string[] fields)
    {
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(fields, schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(fields, schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }
    private static void StringBounds(JsonElement schema, int minimum, int maximum)
    {
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(minimum, schema.GetProperty("minLength").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maxLength").GetInt32());
    }
    private sealed record SentRequest(string Uri, string Method, string? Authorization, string? ContentType, string[] HeaderNames, byte[] Body);
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((request, _) => Task.FromResult(send(request))) { }
        internal List<SentRequest> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Method.Method, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.ToString(),
                request.Headers.Select(h => h.Key).Order().ToArray(), request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return await send(request, cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int BytesRead { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await base.ReadAsync(buffer, cancellationToken); BytesRead += read; return read; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class GatedStream(byte[] bytes, string stage, TaskCompletionSource entered, TaskCompletionSource release) : TrackingStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stage == "body") { entered.TrySetResult(); await release.Task; }
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
        public override async ValueTask DisposeAsync()
        {
            if (stage == "dispose") { entered.TrySetResult(); await release.Task; }
            await base.DisposeAsync();
        }
    }
    private sealed class FailingStream(byte[] bytes, string stage) : TrackingStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => stage == "body" ? ValueTask.FromException<int>(new IOException(Secret)) : base.ReadAsync(buffer, cancellationToken);
        public override ValueTask DisposeAsync() => stage == "dispose" ? ValueTask.FromException(new IOException(Secret)) : base.DisposeAsync();
    }
    private sealed class ControlledContent(Func<CancellationToken, Task<Stream>> create) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => create(cancellationToken);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException(Secret);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class OnceList<T>(T[] values) : IReadOnlyList<T>
    {
        internal int Enumerations { get; private set; }
        public int Count => values.Length;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() { Assert.Equal(1, ++Enumerations); return ((IEnumerable<T>)values).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ThrowingList<T> : IReadOnlyList<T>
    {
        public int Count => throw new InvalidOperationException(Secret);
        public T this[int index] => throw new InvalidOperationException(Secret);
        public IEnumerator<T> GetEnumerator() => throw new AiGenerationException(AiGenerationError.AuthenticationRefused);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
