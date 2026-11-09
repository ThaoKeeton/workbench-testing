using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Workbench.Sdk;
using Workbench.Testing.Domain.Serialization;

namespace Workbench.Testing.Domain.Generation.Ai;

/// <summary>One fixed-endpoint, bounded generation. Injected handlers remain caller-owned.</summary>
public sealed class AiTestCaseGenerator(HttpMessageHandler? handler = null)
{
    internal const int ByteLimit = 256 * 1024;
    internal const string Model = "gpt-5.4-mini-2026-03-17";

    public Task<AiGenerationCapture> GenerateAsync(IReadOnlyList<TestSourceSnapshot> sources,
        Func<CancellationToken, Task<string?>> getKey, CancellationToken cancellationToken = default)
    {
        var captured = AiInputCapture.Capture(sources);
        if (getKey is null) throw new AiGenerationException(AiGenerationError.InvalidRequest);
        return GenerateCapturedAsync(captured, getKey, cancellationToken);
    }

    private async Task<AiGenerationCapture> GenerateCapturedAsync(AiInputCapture input,
        Func<CancellationToken, Task<string?>> getKey, CancellationToken callerCancellation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, deadline.Token);
        var cancellationToken = linked.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? key;
            try { key = await getKey(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); throw new AiFailure(AiGenerationError.CredentialsUnavailable); }
            cancellationToken.ThrowIfCancellationRequested();
            if (key is not { Length: >= 1 and <= 4096 } || key.Any(c => c is < (char)33 or > (char)126))
                throw new AiFailure(AiGenerationError.CredentialsUnavailable);
            // The key belongs only in Authorization, even when selected source text accidentally contains it.
            try { using var checkedInput = StrictJson.Parse(input.Json, key); }
            catch (JsonException) { throw new AiFailure(AiGenerationError.InvalidRequest); }
            cancellationToken.ThrowIfCancellationRequested();

            AiGenerationCapture capture;
            using (var client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null,
                AutomaticDecompression = DecompressionMethods.None
            }, disposeHandler: handler is null) { Timeout = System.Threading.Timeout.InfiniteTimeSpan })
            using (var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses"))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                message.Content = new ByteArrayContent(AiRequestBuilder.Create(input));
                message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode) throw StatusError(response);
                var content = response.Content;
                if (content.Headers.ContentLength > ByteLimit) throw new AiFailure(AiGenerationError.ResponseTooLarge);
                var contentType = content.Headers.ContentType;
                if (content.Headers.ContentEncoding.Count != 0 || contentType is null
                    || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                    || !content.Headers.TryGetValues("Content-Type", out var contentTypes) || contentTypes.Take(2).Count() != 1
                    || contentType.Parameters.Count > 1 || contentType.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase))
                    || contentType.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                    throw new AiFailure(AiGenerationError.ResponseInvalid);
                byte[] bytes;
                // Retain and await actual work, including stream disposal, even when dependencies ignore cancellation.
                await using (var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bytes = await ReadBodyAsync(stream, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                cancellationToken.ThrowIfCancellationRequested();
                capture = AiResponseParser.Parse(input, bytes, key);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return capture;
        }
        catch (Exception error)
        {
            if (callerCancellation.IsCancellationRequested) throw new OperationCanceledException("AI generation canceled.", callerCancellation);
            if (deadline.IsCancellationRequested) throw new AiGenerationException(AiGenerationError.Timeout);
            throw error is AiFailure failure ? new AiGenerationException(failure.Error, failure.RetryAfterSeconds) : new AiGenerationException(AiGenerationError.Unavailable);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var remaining = ByteLimit + 1 - (int)output.Length;
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > ByteLimit) throw new AiFailure(AiGenerationError.ResponseTooLarge);
        }
    }

    private static AiFailure StatusError(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(AiGenerationError.AuthenticationRefused),
        HttpStatusCode.TooManyRequests => new(AiGenerationError.RateLimited, RetryAfter(response)),
        _ => new(AiGenerationError.Unavailable)
    };

    private static int? RetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        using var entries = values.GetEnumerator();
        if (!entries.MoveNext()) return null;
        var value = entries.Current;
        if (entries.MoveNext()) return null;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 86400 ? seconds : null;
    }
}
