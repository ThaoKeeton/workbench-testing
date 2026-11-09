using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Sources.Figma;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class FigmaCaptureTests
{
    private const int ByteLimit = 1024 * 1024;
    private const string Secret = "synthetic-only-token";

    [Fact]
    public async Task Reader_sends_one_fixed_GET_with_escaped_captured_ids_version_and_per_request_credentials()
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Json(["2:3", "1:2"], version: "v/🧪+")));
        var reader = new FigmaFileReader(transport);
        string[] ids = ["2:3", "1:2"];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reader.ReadAsync(new("Ab12", ids, "v/🧪+"), _ => { entered.SetResult(); return release.Task; });
        await entered.Task;
        ids[0] = "9:9";
        release.SetResult(Secret);
        var capture = await pending;
        Assert.Equal("https://api.figma.com/v1/files/Ab12/nodes?ids=2%3A3%2C1%3A2&depth=2&version=v%2F%F0%9F%A7%AA%2B", Assert.Single(transport.Requests).Uri);
        Assert.Equal("GET", transport.Requests[0].Method);
        Assert.Equal(Secret, transport.Requests[0].Token);
        Assert.Null(transport.Requests[0].Authorization);
        Assert.Equal(new[] { "2:3", "1:2" }, capture.Request.NodeIds);
        Assert.Equal("v/🧪+", capture.Version);
        Assert.False(transport.Disposed);
        await reader.ReadAsync(new("Other", ["2:3", "1:2"]), _ => Task.FromResult<string?>("second-synthetic"));
        Assert.Equal("second-synthetic", transport.Requests[1].Token);
        Assert.DoesNotContain("version=", transport.Requests[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_preserves_exact_bytes_observed_text_order_and_defensive_collections()
    {
        var raw = Encoding.UTF8.GetBytes(" \n" + Json(["1:2", "2:3"], characters: "literal https://invalid.test/ SetText 🧪") + "\n");
        using var transport = new RecordingHandler(_ => BytesResponse(raw));
        var capture = await new FigmaFileReader(transport).ReadAsync(new("Ab12", ["2:3", "1:2"]), Token);
        Assert.Equal(raw, capture.OriginalJson);
        raw[0] = 0;
        var returned = capture.OriginalJson;
        returned[1] = 0;
        Assert.Equal((byte)' ', capture.OriginalJson[0]);
        Assert.Equal((byte)'\n', capture.OriginalJson[1]);
        Assert.Equal("Observed file 🧪", capture.Name);
        Assert.Equal("v1", capture.Version);
        Assert.Equal(new[] { "2:3", "1:2" }, capture.Nodes.Select(n => n.Id));
        Assert.All(capture.Nodes, n => { Assert.Equal("Observed " + n.Id, n.Name); Assert.Equal("TEXT", n.Type); Assert.Equal("literal https://invalid.test/ SetText 🧪", n.Characters); });
        Assert.Throws<NotSupportedException>(() => ((IList<string>)capture.Request.NodeIds)[0] = "8:8");
        Assert.Throws<NotSupportedException>(() => ((IList<FigmaNodeCapture>)capture.Nodes).Clear());
    }

    [Fact]
    public async Task Caller_node_list_is_enumerated_once_before_credential_work()
    {
        var ids = new OnceList<string>(["1:2"]);
        using var transport = new RecordingHandler(_ => JsonResponse(Json()));
        var capture = await new FigmaFileReader(transport).ReadAsync(new("Ab12", ids), _ =>
        {
            Assert.Equal(1, ids.Enumerations);
            return Task.FromResult<string?>(Secret);
        });
        Assert.Equal("1:2", Assert.Single(capture.Nodes).Id);
        Assert.Equal(1, ids.Enumerations);
    }

    [Fact]
    public async Task Caller_collection_exceptions_cannot_override_invalid_input_classification()
    {
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        var tokenCalls = 0;
        await ErrorAsync(FigmaReadError.InvalidRequest, () => new FigmaFileReader(transport).ReadAsync(
            new("Ab12", new ThrowingList()), _ => { tokenCalls++; return Token(default); }));
        Assert.Equal(0, tokenCalls);
        Assert.Empty(transport.Requests);
        var capture = await CaptureAsync();
        Error(FigmaReadError.InvalidRequest, () => FigmaSourceBuilder.CreateDraft(capture,
            new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = new ThrowingList() }));
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return [null!];
        foreach (var key in new[] { "", "a/b", "ä", new string('a', 129) }) yield return [new FigmaReadRequest(key, ["1:2"])];
        yield return [new FigmaReadRequest("a", null!)];
        yield return [new FigmaReadRequest("a", [])];
        yield return [new FigmaReadRequest("a", ["1:2", "1:2"])];
        yield return [new FigmaReadRequest("a", Enumerable.Range(0, 17).Select(i => $"1:{i}").ToArray())];
        foreach (var id in new[] { "", "I1:2;3:4", "1-2", "1:2/", "١:2", new string('1', 63) + ":2" }) yield return [new FigmaReadRequest("a", [id])];
        foreach (var version in new[] { "", "v 1", "v\n1", "v\u007f", "\ud800", new string('v', 65) }) yield return [new FigmaReadRequest("a", ["1:2"], version)];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_request_refuses_before_credentials_or_transport(FigmaReadRequest request)
    {
        var credentials = 0;
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        await ErrorAsync(FigmaReadError.InvalidRequest, () => new FigmaFileReader(transport).ReadAsync(request, _ => { credentials++; return Token(default); }));
        Assert.Equal(0, credentials);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("x\r\ny")] [InlineData("x y")] [InlineData("🧪")] [InlineData("x\u007f")]
    public async Task Unavailable_or_invalid_credentials_never_reach_HTTP(string? token)
    {
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        await ErrorAsync(FigmaReadError.CredentialsUnavailable, () => new FigmaFileReader(transport).ReadAsync(new("Ab12", ["1:2"]), _ => Task.FromResult(token)));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Credential_length_boundary_and_callback_failure_are_safe()
    {
        using var transport = new RecordingHandler(_ => JsonResponse(Json()));
        var reader = new FigmaFileReader(transport);
        await reader.ReadAsync(new("a", ["1:2"]), _ => Task.FromResult<string?>(new string('a', 4096)));
        await ErrorAsync(FigmaReadError.CredentialsUnavailable, () => reader.ReadAsync(new("a", ["1:2"]), _ => Task.FromResult<string?>(new string('a', 4097))));
        var error = await ErrorAsync(FigmaReadError.CredentialsUnavailable, () => reader.ReadAsync(new("a", ["1:2"]), _ => throw new InvalidOperationException(Secret)));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(401, FigmaReadError.AuthenticationRefused)] [InlineData(403, FigmaReadError.AuthenticationRefused)]
    [InlineData(404, FigmaReadError.NotFound)] [InlineData(429, FigmaReadError.RateLimited)]
    [InlineData(302, FigmaReadError.Unavailable)] [InlineData(400, FigmaReadError.Unavailable)] [InlineData(500, FigmaReadError.Unavailable)]
    public async Task HTTP_errors_are_safe_single_attempts_without_error_body_reads(int status, FigmaReadError expected)
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes(Secret));
        using var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(body), ReasonPhrase = Secret };
        response.Headers.TryAddWithoutValidation("Location", "https://other.invalid/" + Secret);
        response.Headers.TryAddWithoutValidation("Upgrade-Link", "https://other.invalid/" + Secret);
        using var transport = new RecordingHandler(_ => response);
        var error = await ErrorAsync(expected, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
        Assert.Single(transport.Requests);
        Assert.Equal(0, body.BytesRead);
        Assert.True(body.Disposed);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("0", 0)] [InlineData("86400", 86400)] [InlineData("123", 123)]
    [InlineData("86401", null)] [InlineData("-1", null)] [InlineData("1.5", null)] [InlineData("Wed, 21 Oct 2015 07:28:00 GMT", null)]
    [InlineData("999999999999999999999999", null)] [InlineData("1, 2", null)]
    public async Task Rate_limit_metadata_accepts_only_bounded_integer_seconds(string header, int? expected)
    {
        using var transport = new RecordingHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.TryAddWithoutValidation("Retry-After", header); return r; });
        var error = await ErrorAsync(FigmaReadError.RateLimited, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
        Assert.Equal(expected, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task Transport_exceptions_discard_request_and_exception_details()
    {
        using var transport = new RecordingHandler(_ => throw new HttpRequestException(Secret, new IOException(Secret)));
        var error = await ErrorAsync(FigmaReadError.Unavailable, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Null(error.RetryAfterSeconds);
    }

    [Theory]
    [InlineData("text/html", null)] [InlineData("application/problem+json", null)]
    [InlineData("application/json; charset=utf-16", null)] [InlineData("application/json", "gzip")]
    [InlineData(null, null)]
    public async Task Unsupported_content_headers_refuse_before_reading_body(string? mediaType, string? encoding)
    {
        using var body = new TrackingStream(Encoding.UTF8.GetBytes(Json()));
        using var transport = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (mediaType is not null) response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
            if (encoding is not null) response.Content.Headers.ContentEncoding.Add(encoding);
            return response;
        });
        await ErrorAsync(FigmaReadError.ResponseInvalid, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
        Assert.Equal(0, body.BytesRead);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Oversized_header_or_chunked_body_refuses_with_at_most_one_extra_byte(bool declared)
    {
        using var body = new TrackingStream(new byte[ByteLimit + 8192]);
        using var transport = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new("application/json");
            if (declared) response.Content.Headers.ContentLength = ByteLimit + 8192;
            return response;
        });
        await ErrorAsync(FigmaReadError.ResponseTooLarge, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
        Assert.Equal(declared ? 0 : ByteLimit + 1, body.BytesRead);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task Exactly_one_MiB_valid_JSON_and_utf8_charset_are_accepted()
    {
        var json = Encoding.UTF8.GetBytes(Json());
        var bytes = json.Concat(Enumerable.Repeat((byte)' ', ByteLimit - json.Length)).ToArray();
        using var transport = new RecordingHandler(_ => { var response = BytesResponse(bytes); response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=\"UTF-8\""); return response; });
        Assert.Equal(bytes, (await new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token)).OriginalJson);
    }

    public static IEnumerable<object[]> InvalidResponses()
    {
        var valid = Json();
        yield return ["{"];
        yield return [valid.Replace("\"name\":\"Observed file", "\"name\":\"x\",\"name\":\"Observed file", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":{", "\"future\":{\"x\":1,\"x\":2,", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":{", "\"future\":{\"x\":1,\"\\u0078\":2,", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":{", "\"future\":{\"\\uD800\":0,", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":{", "\"future\":{\"x\":\"\\uDC00\",", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":{", "\"future\":{\"x\":\"\\uD800x\",", StringComparison.Ordinal)];
        yield return [valid.Replace("\"future\":{", "\"future\":{\"x\":" + new string('[', 33) + "0" + new string(']', 33) + ",", StringComparison.Ordinal)];
        yield return ["{\"name\":\"File\",\"version\":\"v1\",\"nodes\":{}}"];
        yield return ["{\"name\":\"File\",\"version\":\"v1\",\"nodes\":{\"1:2\":null}}"];
        yield return [Json(["1:2", "2:3"])];
        yield return [valid.Replace("\"id\":\"1:2\"", "\"id\":\"2:3\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"document\":{", "\"document\":null,\"ignored\":{", StringComparison.Ordinal)];
        yield return [valid.Replace("\"version\":\"v1\"", "\"version\":\"\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"version\":\"v1\"", "\"version\":\"" + new string('v', 65) + "\"", StringComparison.Ordinal)];
        yield return [valid.Replace("\"name\":\"Observed file", "\"name\":\"" + new string('a', 201), StringComparison.Ordinal)];
        yield return [valid.Replace("\"type\":\"TEXT\"", "\"type\":\"" + new string('t', 65) + "\"", StringComparison.Ordinal)];
        yield return [Json(characters: new string('c', 4097))];
        yield return [valid.Replace("\"name\":\"Observed 1:2\"", "\"name\":false", StringComparison.Ordinal)];
        yield return [valid.Replace("\"characters\":\"Observed text\"", "\"characters\":null", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task Malformed_duplicate_unicode_depth_identity_or_field_failures_refuse_the_whole_response(string json)
    {
        using var transport = new RecordingHandler(_ => JsonResponse(json));
        await ErrorAsync(FigmaReadError.ResponseInvalid, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
    }

    [Fact]
    public async Task Invalid_UTF8_and_explicit_version_mismatch_refuse()
    {
        using var transport = new RecordingHandler(_ => BytesResponse([0xff, .. Encoding.UTF8.GetBytes(Json())]));
        await ErrorAsync(FigmaReadError.ResponseInvalid, () => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Token));
        using var versionTransport = new RecordingHandler(_ => JsonResponse(Json()));
        await ErrorAsync(FigmaReadError.ResponseInvalid, () => new FigmaFileReader(versionTransport).ReadAsync(new("a", ["1:2"], "v2"), Token));
    }

    [Theory]
    [InlineData("credential")] [InlineData("transport")] [InlineData("body")]
    public async Task Cancellation_waits_for_actual_ignored_cancellation_work_then_sanitizes(string stage)
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new GatedStream(Encoding.UTF8.GetBytes(Json()), entered, release);
        using var transport = new RecordingHandler(async (_, _) =>
        {
            if (stage == "transport") { entered.SetResult(); await release.Task; }
            return stage == "body" ? StreamResponse(body) : JsonResponse(Json());
        });
        async Task<string?> Credential(CancellationToken _)
        {
            if (stage == "credential") { entered.SetResult(); await release.Task; }
            return Secret;
        }
        var pending = new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), Credential, caller.Token);
        await entered.Task;
        caller.Cancel();
        Assert.False(pending.IsCompleted);
        if (stage == "body") Assert.False(body.Disposed);
        release.SetResult();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        if (stage == "credential") Assert.Empty(transport.Requests);
        if (stage == "body") Assert.True(body.Disposed);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task Already_cancelled_caller_does_not_evaluate_credentials_or_send()
    {
        using var caller = new CancellationTokenSource(); caller.Cancel();
        using var transport = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FigmaFileReader(transport).ReadAsync(new("a", ["1:2"]), _ => throw new InvalidOperationException(Secret), caller.Token));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task Builder_maps_only_explicit_defensively_captured_criteria_and_generated_actions_remain_manual()
    {
        var capture = await CaptureAsync(["2:3", "1:2"]);
        string[] criteria = ["  User criterion 🧪  ", "=literal"];
        var values = new OnceList<string>(criteria);
        var supplied = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["1:2"] = ["Other criterion"], ["2:3"] = values };
        var draft = FigmaSourceBuilder.CreateDraft(capture, supplied);
        criteria[0] = "changed"; supplied.Clear();
        Assert.Equal(1, values.Enumerations);
        Assert.Equal(TestSourceKind.Figma, draft.Kind);
        Assert.Equal("figma.rest.nodes.manual-criteria", draft.ProviderId);
        Assert.Equal("1", draft.ProviderVersion);
        Assert.Equal("Ab12@v1", draft.DocumentId);
        Assert.Equal(capture.Name, draft.Title);
        Assert.Equal(capture.OriginalJson, draft.OriginalBytes);
        Assert.Equal(new[] { "2:3", "1:2" }, draft.Items.Select(i => i.Locator));
        Assert.Equal(new[] { "  User criterion 🧪  ", "=literal" }, draft.Items[0].AcceptanceCriteria);
        Assert.Equal("Observed 2:3", draft.Items[0].Title);
        Assert.Contains("TEXT", draft.Items[0].Description, StringComparison.Ordinal);
        Assert.Contains("Observed text", draft.Items[0].Description, StringComparison.Ordinal);
        Assert.All(draft.Items, i => Assert.Null(i.VirtualScenario));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)draft.Items[0].AcceptanceCriteria).Clear());
        draft.OriginalBytes[0] = 0;
        Assert.Equal((byte)'{', capture.OriginalJson[0]);
        using var disk = new TestDirectory();
        var store = new ProjectTestStore(disk.User, "default");
        var first = await store.PublishSourceAsync(PluginTestingContextTests.Owner, FigmaSourceBuilder.CreateDraft(capture, Criteria(capture.Request.NodeIds)));
        var second = await store.PublishSourceAsync(PluginTestingContextTests.Owner, FigmaSourceBuilder.CreateDraft(capture, Criteria(capture.Request.NodeIds, criterion: "Changed user criterion")));
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(first.OriginalSha256, second.OriginalSha256);
        Assert.All(new RuleTestCaseGenerator().Generate([first]).Cases, c => Assert.All(c.Steps, s => Assert.Null(s.Action)));
    }

    [Fact]
    public async Task Builder_refuses_missing_extra_duplicate_ordinal_keys_invalid_text_and_all_input_caps()
    {
        var capture = await CaptureAsync();
        var invalid = new List<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            null!, new Dictionary<string, IReadOnlyList<string>>(),
            new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = ["a"], ["2:3"] = ["b"] },
            new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = null! },
            new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = [] },
            new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = Enumerable.Repeat("a", 17).ToArray() },
            new DuplicateDictionary("1:2")
        };
        foreach (var value in new[] { "", " \t", "\ud800", new string('c', 4097) })
            invalid.Add(new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = [value] });
        foreach (var values in invalid) Error(FigmaReadError.InvalidRequest, () => FigmaSourceBuilder.CreateDraft(capture, values));
        Error(FigmaReadError.InvalidRequest, () => FigmaSourceBuilder.CreateDraft(null!, Criteria(["1:2"])));
        var tooMuchDescription = await CaptureAsync(characters: new string('c', 4096));
        Error(FigmaReadError.InvalidRequest, () => FigmaSourceBuilder.CreateDraft(tooMuchDescription, Criteria(["1:2"])));
        var withoutCharacters = Json().Replace(",\"characters\":\"Observed text\"", "", StringComparison.Ordinal);
        using var transport = new RecordingHandler(_ => JsonResponse(withoutCharacters));
        var captureWithoutCharacters = await new FigmaFileReader(transport).ReadAsync(new("Ab12", ["1:2"]), Token);
        Assert.Null(Assert.Single(captureWithoutCharacters.Nodes).Characters);
        Assert.Contains("TEXT", Assert.Single(FigmaSourceBuilder.CreateDraft(captureWithoutCharacters, Criteria(["1:2"])).Items).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exactly_200_explicit_criteria_generate_200_manual_cases_and_201_refuses_without_additional_IO()
    {
        var ids = Enumerable.Range(0, 13).Select(i => $"1:{i}").ToArray();
        var tokenCalls = 0;
        using var transport = new RecordingHandler(_ => JsonResponse(Json(ids)));
        var capture = await new FigmaFileReader(transport).ReadAsync(new("Ab12", ids), _ => { tokenCalls++; return Token(default); });
        var criteria = Criteria(ids, total: 200);
        var draft = FigmaSourceBuilder.CreateDraft(capture, criteria);
        using var disk = new TestDirectory();
        var store = new ProjectTestStore(disk.User, "default");
        var source = await store.PublishSourceAsync(PluginTestingContextTests.Owner, draft);
        var cases = new RuleTestCaseGenerator().Generate([source]);
        Assert.Equal(200, cases.Cases.Count);
        Assert.All(cases.Cases, c => Assert.Null(Assert.Single(c.Steps).Action));
        Error(FigmaReadError.InvalidRequest, () => FigmaSourceBuilder.CreateDraft(capture, Criteria(ids, total: 201)));
        Assert.Single(transport.Requests); Assert.Equal(1, tokenCalls);
        Assert.Single(await store.ListSourcesAsync());
    }

    [Fact]
    public async Task Legal_input_can_still_refuse_the_authoritative_Core_encoded_envelope_without_partial_publication()
    {
        var ids = Enumerable.Range(0, 13).Select(i => $"1:{i}").ToArray();
        var capture = await CaptureAsync(ids);
        var draft = FigmaSourceBuilder.CreateDraft(capture, Criteria(ids, total: 200, criterion: new string('界', 4096)));
        using var disk = new TestDirectory();
        var store = new ProjectTestStore(disk.User, "default");
        var error = await Assert.ThrowsAsync<PluginTestingException>(() => store.PublishSourceAsync(PluginTestingContextTests.Owner, draft));
        Assert.Equal("StoreLimitExceeded", error.ErrorCode);
        Assert.Empty(await store.ListSourcesAsync());
    }

    private static Task<string?> Token(CancellationToken _) => Task.FromResult<string?>(Secret);
    private static async Task<FigmaCapture> CaptureAsync(string[]? ids = null, string characters = "Observed text")
    {
        ids ??= ["1:2"];
        using var transport = new RecordingHandler(_ => JsonResponse(Json(ids, characters: characters)));
        return await new FigmaFileReader(transport).ReadAsync(new("Ab12", ids), Token);
    }
    private static Dictionary<string, IReadOnlyList<string>> Criteria(IEnumerable<string> ids, int? total = null, string criterion = "Explicit user criterion")
    {
        var remaining = total;
        return ids.ToDictionary(id => id, _ =>
        {
            var count = remaining is { } n ? Math.Min(16, n) : 1;
            if (remaining is not null) remaining -= count;
            return (IReadOnlyList<string>)Enumerable.Repeat(criterion, count).ToArray();
        }, StringComparer.Ordinal);
    }
    private static string Json(string[]? ids = null, string version = "v1", string characters = "Observed text")
    {
        ids ??= ["1:2"];
        return JsonSerializer.Serialize(new { name = "Observed file 🧪", version, nodes = ids.ToDictionary(id => id, id => new { document = new { id, name = "Observed " + id, type = "TEXT", characters, future = new { inert = "https://never-follow.invalid", actions = new[] { "Click", "SetText" } } }, components = new { } }), future = new { inert = true } }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
    private static HttpResponseMessage JsonResponse(string json) => BytesResponse(Encoding.UTF8.GetBytes(json));
    private static HttpResponseMessage BytesResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("application/json");
        return response;
    }
    private static FigmaReadException Error(FigmaReadError expected, Action action)
    {
        var error = Assert.Throws<FigmaReadException>(action); Assert.Equal(expected, error.Error); Assert.Null(error.InnerException); return error;
    }
    private static async Task<FigmaReadException> ErrorAsync(FigmaReadError expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<FigmaReadException>(action); Assert.Equal(expected, error.Error); Assert.Null(error.InnerException); return error;
    }
    private sealed record SentRequest(string Uri, string Method, string? Token, string? Authorization);
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((r, _) => Task.FromResult(send(r))) { }
        internal List<SentRequest> Requests { get; } = [];
        internal bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Method.Method, request.Headers.TryGetValues("X-Figma-Token", out var values) ? Assert.Single(values) : null, request.Headers.Authorization?.ToString()));
            return send(request, cancellationToken);
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
        { var count = await base.ReadAsync(buffer, cancellationToken); BytesRead += count; return count; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class GatedStream(byte[] bytes, TaskCompletionSource entered, TaskCompletionSource release) : TrackingStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(); await release.Task; return await base.ReadAsync(buffer, CancellationToken.None); }
    }
    private sealed class OnceList<T>(T[] values) : IReadOnlyList<T>
    {
        internal int Enumerations { get; private set; }
        public int Count => values.Length;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() { Assert.Equal(1, ++Enumerations); return ((IEnumerable<T>)values).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class ThrowingList : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => throw new FigmaReadException(FigmaReadError.AuthenticationRefused);
        public IEnumerator<string> GetEnumerator() => throw new FigmaReadException(FigmaReadError.AuthenticationRefused);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class DuplicateDictionary(string key) : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        public int Count => 2;
        public IEnumerable<string> Keys => [key, key];
        public IEnumerable<IReadOnlyList<string>> Values => [["a"], ["b"]];
        public IReadOnlyList<string> this[string index] => ["a"];
        public bool ContainsKey(string index) => index == key;
        public bool TryGetValue(string index, out IReadOnlyList<string> value) { value = ["a"]; return ContainsKey(index); }
        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>)new[] { new KeyValuePair<string, IReadOnlyList<string>>(key, ["a"]), new(key, ["b"]) }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
