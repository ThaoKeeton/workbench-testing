using System.Globalization;
using System.Net;

namespace Workbench.Testing.Domain.Sources.Figma;

/// <summary>One fixed-endpoint, bounded read. An injected handler is caller-owned test transport, not a sandbox.</summary>
public sealed class FigmaFileReader(HttpMessageHandler? handler = null)
{
    internal const int ByteLimit = 1024 * 1024;

    public Task<FigmaCapture> ReadAsync(FigmaReadRequest request, Func<CancellationToken, Task<string?>> getToken,
        CancellationToken cancellationToken = default)
    {
        var captured = FigmaInputCapture.CaptureRequest(request);
        if (getToken is null) throw new FigmaReadException(FigmaReadError.InvalidRequest);
        return ReadCapturedAsync(captured, getToken, cancellationToken);
    }

    private async Task<FigmaCapture> ReadCapturedAsync(FigmaReadRequest request, Func<CancellationToken, Task<string?>> getToken,
        CancellationToken callerCancellation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, deadline.Token);
        var cancellationToken = linked.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? token;
            try { token = await getToken(cancellationToken).ConfigureAwait(false); }
            catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); throw new FigmaReadException(FigmaReadError.CredentialsUnavailable); }
            cancellationToken.ThrowIfCancellationRequested();
            if (token is not { Length: >= 1 and <= 4096 } || token.Any(c => c is < (char)33 or > (char)126))
                throw new FigmaReadException(FigmaReadError.CredentialsUnavailable);

            using var client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false,
                Credentials = null
            }, disposeHandler: handler is null) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            var uri = "https://api.figma.com/v1/files/" + Uri.EscapeDataString(request.FileKey)
                + "/nodes?ids=" + Uri.EscapeDataString(string.Join(',', request.NodeIds)) + "&depth=2"
                + (request.Version is null ? "" : "&version=" + Uri.EscapeDataString(request.Version));
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Add("X-Figma-Token", token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode) throw StatusError(response);
            var content = response.Content;
            if (content.Headers.ContentLength > ByteLimit) throw new FigmaReadException(FigmaReadError.ResponseTooLarge);
            var contentType = content.Headers.ContentType;
            if (content.Headers.ContentEncoding.Count != 0 || !string.Equals(contentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || contentType?.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                throw new FigmaReadException(FigmaReadError.ResponseInvalid);
            // Await actual work even if the callback, transport or stream ignores cancellation. No abandoned timeout task.
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await ReadBodyAsync(stream, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var capture = FigmaResponseParser.Parse(request, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            return capture;
        }
        catch (Exception error)
        {
            if (callerCancellation.IsCancellationRequested) throw new OperationCanceledException("Figma capture canceled.", callerCancellation);
            if (deadline.IsCancellationRequested) throw new FigmaReadException(FigmaReadError.Timeout);
            if (error is FigmaReadException) throw;
            throw new FigmaReadException(FigmaReadError.Unavailable);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var remaining = ByteLimit + 1 - (int)output.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > ByteLimit) throw new FigmaReadException(FigmaReadError.ResponseTooLarge);
        }
    }

    private static FigmaReadException StatusError(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(FigmaReadError.AuthenticationRefused),
        HttpStatusCode.NotFound => new(FigmaReadError.NotFound),
        HttpStatusCode.TooManyRequests => new(FigmaReadError.RateLimited, RetryAfter(response)),
        _ => new(FigmaReadError.Unavailable)
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
