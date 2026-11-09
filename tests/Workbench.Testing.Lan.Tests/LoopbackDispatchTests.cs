using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Workbench.Testing.Lan;
using Xunit;

namespace Workbench.Testing.Lan.Tests;

public sealed class LoopbackDispatchTests
{
    private static X509Certificate2 Identity(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    private static async Task SendRawFrame(IPEndPoint endpoint, X509Certificate2 controller, X509Certificate2 agent, int length, byte[] body)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var connection = new TcpClient(); await connection.ConnectAsync(endpoint, budget.Token);
        var pin = SHA256.HashData(agent.RawData);
        using var tls = new SslStream(connection.GetStream(), false, (_, cert, _, _) =>
            cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), pin));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = "test", ClientCertificates = new X509CertificateCollection { controller } }, budget.Token);
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, length);
        await tls.WriteAsync(header, budget.Token);
        if (body.Length != 0) await tls.WriteAsync(body, budget.Token);
        await tls.FlushAsync(budget.Token);
    }

    [Fact]
    public async Task Authorized_peers_exchange_synthetic_results_over_loopback()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        await using var agent = LoopbackAgent.Start(b, a);
        var controller = new LoopbackController(a, b);
        Assert.True(IPAddress.IsLoopback(agent.Endpoint.Address));
        var passed = await controller.DispatchAsync(agent.Endpoint, new(Guid.NewGuid(), "example", "example"));
        var failed = await controller.DispatchAsync(agent.Endpoint, new(Guid.NewGuid(), "actual", "different"));
        Assert.Equal(DispatchStatus.Passed, passed.Status);
        Assert.Equal(DispatchStatus.Failed, failed.Status);
        Assert.Equal(2, agent.AcceptedCount);
    }

    [Fact]
    public async Task Unknown_client_cannot_execute_or_consume_request_id()
    {
        // Matching visible names must not grant the outsider the authorized identity.
        using var a = Identity("authorized"); using var b = Identity("agent"); using var outsider = Identity("authorized");
        await using var agent = LoopbackAgent.Start(b, a);
        var request = new SyntheticComparison(Guid.NewGuid(), "x", "x");
        var refused = await new LoopbackController(outsider, b).DispatchAsync(agent.Endpoint, request);
        Assert.NotEqual(DispatchStatus.Passed, refused.Status);
        Assert.Equal(0, agent.AcceptedCount);
        Assert.Equal(DispatchStatus.Passed, (await new LoopbackController(a, b).DispatchAsync(agent.Endpoint, request)).Status);
    }

    [Fact]
    public async Task Wrong_agent_identity_is_rejected_before_any_execution()
    {
        using var a = Identity("controller"); using var b = Identity("agent"); using var wrong = Identity("other-agent");
        await using var agent = LoopbackAgent.Start(b, a);
        var request = new SyntheticComparison(Guid.NewGuid(), "x", "x");
        var refused = await new LoopbackController(a, wrong).DispatchAsync(agent.Endpoint, request);
        Assert.NotEqual(DispatchStatus.Passed, refused.Status);
        Assert.Equal(0, agent.AcceptedCount);
        Assert.Equal(DispatchStatus.Passed, (await new LoopbackController(a, b).DispatchAsync(agent.Endpoint, request)).Status);
    }

    [Fact]
    public async Task Duplicate_and_revoked_requests_are_not_executed()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        await using var agent = LoopbackAgent.Start(b, a);
        var controller = new LoopbackController(a, b);
        var request = new SyntheticComparison(Guid.NewGuid(), "x", "x");
        Assert.Equal(DispatchStatus.Passed, (await controller.DispatchAsync(agent.Endpoint, request)).Status);
        Assert.Equal(DispatchStatus.Duplicate, (await controller.DispatchAsync(agent.Endpoint, request)).Status);
        agent.Revoke();
        var refused = await controller.DispatchAsync(agent.Endpoint, new(Guid.NewGuid(), "x", "x"));
        Assert.NotEqual(DispatchStatus.Passed, refused.Status);
        Assert.Equal(1, agent.AcceptedCount);
    }

    [Fact]
    public async Task Non_loopback_destination_is_rejected_before_connecting()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        var controller = new LoopbackController(a, b);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.DispatchAsync(new(IPAddress.Parse("192.0.2.1"), 443), new(Guid.NewGuid(), "x", "x")));
    }

    [Fact]
    public async Task Invalid_or_oversized_tasks_are_refused_before_dispatch()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        await using var agent = LoopbackAgent.Start(b, a);
        var controller = new LoopbackController(a, b);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.DispatchAsync(agent.Endpoint, new(Guid.Empty, "x", "x")));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.DispatchAsync(agent.Endpoint, new(Guid.NewGuid(), new string('x', 257), "x")));
        Assert.Equal(0, agent.AcceptedCount);
    }

    [Fact]
    public async Task Cancellation_before_connect_does_not_execute_a_task()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        await using var agent = LoopbackAgent.Start(b, a);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LoopbackController(a, b).DispatchAsync(agent.Endpoint, new(Guid.NewGuid(), "x", "x"), cancellation.Token));
        Assert.Equal(0, agent.AcceptedCount);
    }

    [Fact]
    public async Task Disconnect_after_request_returns_unknown_without_retry()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(budget.Token);
            using var ssl = new SslStream(connection.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = b, ClientCertificateRequired = true }, budget.Token);
            var header = new byte[4]; await ssl.ReadExactlyAsync(header, budget.Token);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
            Assert.InRange(length, 1, 4096);
            await ssl.ReadExactlyAsync(new byte[length], budget.Token);
        }, budget.Token);
        var result = await new LoopbackController(a, b).DispatchAsync(endpoint, new(Guid.NewGuid(), "x", "x"), budget.Token);
        await server;
        Assert.Equal(DispatchStatus.OutcomeUnknown, result.Status);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task Oversized_frame_is_refused_without_disabling_the_agent()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        await using var agent = LoopbackAgent.Start(b, a);
        await SendRawFrame(agent.Endpoint, a, b, 4097, []);
        var result = await new LoopbackController(a, b).DispatchAsync(agent.Endpoint, new(Guid.NewGuid(), "x", "x"));
        Assert.Equal(DispatchStatus.Passed, result.Status);
        Assert.Equal(1, agent.AcceptedCount);
    }

    [Fact]
    public async Task Malformed_response_is_reported_as_unknown()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(budget.Token);
            using var tls = new SslStream(connection.GetStream(), false, (_, _, _, _) => true);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = b, ClientCertificateRequired = true }, budget.Token);
            var header = new byte[4]; await tls.ReadExactlyAsync(header, budget.Token);
            var requestLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
            await tls.ReadExactlyAsync(new byte[requestLength], budget.Token);
            var reply = Encoding.UTF8.GetBytes("{\"Status\":99}");
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, reply.Length);
            await tls.WriteAsync(header, budget.Token); await tls.WriteAsync(reply, budget.Token); await tls.FlushAsync(budget.Token);
        }, budget.Token);
        var result = await new LoopbackController(a, b).DispatchAsync((IPEndPoint)listener.LocalEndpoint, new(Guid.NewGuid(), "x", "x"), budget.Token);
        await server;
        Assert.Equal(DispatchStatus.OutcomeUnknown, result.Status);
    }

    [Fact]
    public async Task Duplicate_json_fields_are_refused_without_disabling_the_agent()
    {
        using var a = Identity("controller"); using var b = Identity("agent");
        await using var agent = LoopbackAgent.Start(b, a);
        var id = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("{\"RequestId\":\"" + id + "\",\"Actual\":\"x\",\"Actual\":\"x\",\"Expected\":\"x\"}");
        await SendRawFrame(agent.Endpoint, a, b, payload.Length, payload);
        var result = await new LoopbackController(a, b).DispatchAsync(agent.Endpoint, new(id, "x", "x"));
        Assert.Equal(DispatchStatus.Passed, result.Status);
        Assert.Equal(1, agent.AcceptedCount);
    }
}
