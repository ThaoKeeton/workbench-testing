using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Workbench.Testing.Lan;

// This experimental foundation is limited to one machine and synthetic
// string comparisons. It is not a remote-desktop or LAN service.
public enum DispatchStatus { Passed, Failed, Duplicate, Rejected, OutcomeUnknown }
public sealed record SyntheticComparison(Guid RequestId, string Actual, string Expected);
public sealed record DispatchResult(Guid RequestId, DispatchStatus Status);

internal static class LoopbackProtocol
{
    internal const int MaximumFrame = 4096;
    internal static readonly TimeSpan ConnectionBudget = TimeSpan.FromSeconds(5);

    internal static void RequireIdentity(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
            throw new ArgumentException("A current certificate with its private key is required.", nameof(certificate));
    }

    internal static bool Matches(X509Certificate? candidate, byte[] pin)
    {
        if (candidate is null) return false;
        try
        {
            using var cert = X509CertificateLoader.LoadCertificate(candidate.GetRawCertData());
            return DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow < cert.NotAfter.ToUniversalTime()
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), pin);
        }
        catch (CryptographicException) { return false; }
    }

    internal static bool Valid(SyntheticComparison? request) => request is not null && request.RequestId != Guid.Empty
        && request.Actual is not null && request.Expected is not null && request.Actual.Length <= 256 && request.Expected.Length <= 256;

    internal static async Task WriteAsync(Stream stream, object value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length is 0 or > MaximumFrame) throw new InvalidDataException("Frame refused.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaximumFrame) throw new InvalidDataException("Frame refused.");
        var payload = new byte[length]; await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
    }

    internal static bool ExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!names.Contains(item.Name, StringComparer.Ordinal) || !seen.Add(item.Name)) return false;
        return seen.Count == names.Length;
    }

    internal static SyntheticComparison ParseRequest(JsonElement value)
    {
        if (!ExactProperties(value, "RequestId", "Actual", "Expected") ||
            value.GetProperty("RequestId").ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(value.GetProperty("RequestId").GetString(), "D", out var id) ||
            value.GetProperty("Actual").ValueKind != JsonValueKind.String || value.GetProperty("Expected").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Request refused.");
        var request = new SyntheticComparison(id, value.GetProperty("Actual").GetString()!, value.GetProperty("Expected").GetString()!);
        if (!Valid(request)) throw new InvalidDataException("Request refused.");
        return request;
    }

    internal static DispatchResult ParseReply(JsonElement value, Guid expected)
    {
        if (!ExactProperties(value, "RequestId", "Status") || value.GetProperty("RequestId").ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(value.GetProperty("RequestId").GetString(), "D", out var id) || id != expected ||
            value.GetProperty("Status").ValueKind != JsonValueKind.Number || !value.GetProperty("Status").TryGetInt32(out var status) ||
            status is < (int)DispatchStatus.Passed or > (int)DispatchStatus.Rejected)
            throw new InvalidDataException("Response refused.");
        return new(id, (DispatchStatus)status);
    }
}
