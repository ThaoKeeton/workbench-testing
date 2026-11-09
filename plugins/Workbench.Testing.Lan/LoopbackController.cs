using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Workbench.Testing.Lan;

public sealed class LoopbackController
{
    private readonly X509Certificate2 _identity;
    private readonly byte[] _agentPin;
    public LoopbackController(X509Certificate2 identity, X509Certificate2 trustedAgent)
    {
        LoopbackProtocol.RequireIdentity(identity); ArgumentNullException.ThrowIfNull(trustedAgent);
        _identity = identity; _agentPin = SHA256.HashData(trustedAgent.RawData);
    }

    public async Task<DispatchResult> DispatchAsync(IPEndPoint endpoint, SyntheticComparison request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IPAddress.IsLoopback(endpoint.Address)) throw new ArgumentException("Only loopback endpoints are supported.", nameof(endpoint));
        if (!LoopbackProtocol.Valid(request)) throw new ArgumentException("Synthetic request refused.", nameof(request));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(LoopbackProtocol.ConnectionBudget);
        var sent = false;
        try
        {
            using var connection = new TcpClient(endpoint.AddressFamily);
            await connection.ConnectAsync(endpoint, budget.Token).ConfigureAwait(false);
            using var tls = new SslStream(connection.GetStream(), false, (_, cert, _, _) => LoopbackProtocol.Matches(cert, _agentPin));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "workbench-loopback", ClientCertificates = new X509CertificateCollection { _identity },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, budget.Token).ConfigureAwait(false);
            sent = true; // A partial write is already uncertain; never retry it here.
            await LoopbackProtocol.WriteAsync(tls, request, budget.Token).ConfigureAwait(false);
            using var document = await LoopbackProtocol.ReadAsync(tls, budget.Token).ConfigureAwait(false);
            return LoopbackProtocol.ParseReply(document.RootElement, request.RequestId);
        }
        catch (OperationCanceledException) when (!sent && cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or AuthenticationException or OperationCanceledException or JsonException or SocketException)
        { return new(request.RequestId, sent ? DispatchStatus.OutcomeUnknown : DispatchStatus.Rejected); }
    }
}
