using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Workbench.Sdk;
using Workbench.Testing.SourceAcceptance;
using Xunit;

namespace Workbench.Core.Tests;

public sealed partial class SourceAcceptanceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProtocolAndLimits_AI_request_bound_refuses_before_acceptance_and_never_reads_beyond_cap_plus_one(bool unknownLength)
    {
        var ledger = new SourceAcceptanceScenarioLedger("default");
        using var handler = new SourceAcceptanceAiHandler(ledger); using var client = new HttpClient(handler, disposeHandler: false);
        using var content = new OversizedContent(unknownLength);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SourceAcceptanceProtocol.OpenAiKey);
        Assert.True(ledger.TryArm(SourceAcceptanceProvider.Ai, SourceAcceptanceScenario.Happy));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(unknownLength ? 65537 : 0, content.Body.BytesRead);
        Assert.Equal(1, Count(ledger, "ai", "entered")); Assert.Equal(0, Count(ledger, "ai", "accepted"));
        Assert.Equal(1, Count(ledger, "ai", "settled")); Assert.Equal(1, Count(ledger, "ai", "unexpected"));
    }

    [Fact]
    public async Task ProtocolAndLimits_delayed_arm_prefix_rechecks_context_and_ledger_after_external_admission_callback()
    {
        await using var rig = await Rig.CreateAsync();
        var arm = rig.Context.FindCommand("source-acceptance-arm-figma-happy")!;
        rig.BeforeArm = () => rig.Context.Close(PluginStopReason.PluginDisabled);
        Assert.False((await arm.ExecuteAsync(CancellationToken.None)).Succeeded);
        using var state = State(rig.Ledger); Assert.Equal(0, state.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(0, Count(rig.Ledger, "figma", "entered"));
        await using var second = await Rig.CreateAsync();
        second.BeforeArm = second.Ledger.SealCommands;
        Assert.False((await second.Command("source-acceptance-arm-figma-happy")).Succeeded);
        Assert.False((await second.Command("source-acceptance-state")).Succeeded);
        Assert.False((await second.Command("source-acceptance-release")).Succeeded);
        Assert.Equal(0, Count(second.Ledger, "figma", "entered"));
    }

    private sealed class OversizedContent : HttpContent
    {
        internal CountingStream Body { get; } = new();
        private readonly bool _unknown;
        internal OversizedContent(bool unknown)
        { _unknown = unknown; Headers.ContentType = new("application/json") { CharSet = "utf-8" }; }
        protected override bool TryComputeLength(out long length) { length = 65538; return !_unknown; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("Use the actual bounded request stream.");
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(Body);
    }
    private sealed class CountingStream() : MemoryStream(new byte[65538])
    {
        internal int BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var count = await base.ReadAsync(buffer, cancellationToken); BytesRead += count; return count; }
    }
}
