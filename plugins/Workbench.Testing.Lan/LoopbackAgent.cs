using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Workbench.Testing.Lan;

public sealed class LoopbackAgent : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly X509Certificate2 _identity;
    private readonly byte[] _controllerPin;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Guid> _accepted = [];
    private readonly Task _loop;
    private bool _revoked;
    private int _disposed;

    private LoopbackAgent(X509Certificate2 identity, X509Certificate2 controller)
    {
        _identity = identity; _controllerPin = SHA256.HashData(controller.RawData);
        _listener = new TcpListener(IPAddress.Loopback, 0); _listener.Start(8);
        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _loop = ServeAsync();
    }

    public IPEndPoint Endpoint { get; }
    public int AcceptedCount { get { lock (_sync) return _accepted.Count; } }
    public static LoopbackAgent Start(X509Certificate2 agent, X509Certificate2 authorizedController)
    {
        LoopbackProtocol.RequireIdentity(agent); ArgumentNullException.ThrowIfNull(authorizedController);
        return new(agent, authorizedController);
    }
    public void Revoke() { lock (_sync) _revoked = true; }

    private bool Authorized(X509Certificate? certificate)
    { lock (_sync) return !_revoked && LoopbackProtocol.Matches(certificate, _controllerPin); }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            using (connection)
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                budget.CancelAfter(LoopbackProtocol.ConnectionBudget);
                try { await HandleAsync(connection, budget.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidDataException or AuthenticationException or OperationCanceledException or JsonException or SocketException) { }
            }
        }
    }

    private async Task HandleAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using var tls = new SslStream(connection.GetStream(), false, (_, cert, _, _) => Authorized(cert));
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _identity, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken).ConfigureAwait(false);
        using var document = await LoopbackProtocol.ReadAsync(tls, cancellationToken).ConfigureAwait(false);
        var request = LoopbackProtocol.ParseRequest(document.RootElement);
        DispatchStatus status;
        lock (_sync)
        {
            if (_revoked || _accepted.Count >= 512) status = DispatchStatus.Rejected;
            else if (!_accepted.Add(request.RequestId)) status = DispatchStatus.Duplicate;
            else status = string.Equals(request.Actual, request.Expected, StringComparison.Ordinal) ? DispatchStatus.Passed : DispatchStatus.Failed;
        }
        await LoopbackProtocol.WriteAsync(tls, new DispatchResult(request.RequestId, status), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Revoke(); _stop.Cancel(); _listener.Stop();
        try { await _loop.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
