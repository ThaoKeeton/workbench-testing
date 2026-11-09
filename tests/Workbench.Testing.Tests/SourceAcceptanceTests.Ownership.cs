using System.Diagnostics;
using System.Net;
using System.Text;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Generation.Ai;
using Workbench.Testing.SourceAcceptance;
using Xunit;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed partial class SourceAcceptanceTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task OwnershipAndHold_actual_IO_and_retained_notification_join_before_caller_owned_handlers_dispose(bool ai, bool lifetimeStop)
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        TestSourceSnapshot? source = null;
        if (ai) { Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Happy)); source = await rig.ImportFigma(); }
        var provider = ai ? SourceAcceptanceProvider.Ai : SourceAcceptanceProvider.Figma;
        Assert.True(rig.Ledger.TryArm(provider, SourceAcceptanceScenario.Hold));
        var tail = new AsyncTestGate(); rig.Notify = tail.HoldAsync;
        var work = rig.Life.RetainActivityAsync(() => rig.Activity.RemoteSourceAsync(() => source?.Id, async (id, token) =>
        {
            if (ai) await rig.Controller.GenerateWithAiAsync([id!], Workbench.Testing.NativeSourceCredentials.ForAction(rig.Context.Secrets, "openai-api-key"), token);
            else await rig.Controller.ImportFigmaAsync(Request, Criteria(), Workbench.Testing.NativeSourceCredentials.ForAction(rig.Context.Secrets, "figma-token"), token);
            return true;
        }, _ => Task.CompletedTask, () => Task.CompletedTask));
        var ownership = new SourceAcceptanceOwnership(rig.Ledger, rig.Figma, rig.Ai, rig.Life.StopAsync);
        Task? stop = null, leave = null; Action? release = null;
        using var caller = new CancellationTokenSource();
        try
        {
            await Until(() => { using var state = State(rig.Ledger); return state.RootElement.GetProperty("phase").GetString() == "held"; });
            release = Assert.IsType<Action>(rig.Ledger.CaptureRelease());
            await rig.Remote.RequestCancellationAsync();
            if (lifetimeStop) { rig.Context.Close(PluginStopReason.PluginDisabled); await rig.Context.CancelLifetimeAsync(); caller.Cancel(); }
            else leave = rig.Life.PrepareLeaveAsync();
            stop = ownership.StopAsync(caller.Token);
            using (var state = State(rig.Ledger)) Assert.True(state.RootElement.GetProperty(ai ? "ai" : "figma").GetProperty("cancellationObserved").GetBoolean());
            Assert.False(stop.IsCompleted); Assert.False(work.IsCompleted); Assert.False(rig.Owner.DrainAsync().IsCompleted);
            Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed); Assert.False(rig.Life.ReadAdmission().ActivityStarts);
            Assert.False((await rig.Command("source-acceptance-release")).Succeeded);
            release();
            await tail.Entered.Task.WaitAsync(Budget);
            Assert.False(stop.IsCompleted); Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed);
            tail.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            if (lifetimeStop) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            else await stop.WaitAsync(Budget);
            Assert.True(rig.Figma.IsDisposed); Assert.True(rig.Ai.IsDisposed); Assert.Same(stop, ownership.StopAsync());
            Assert.Equal(ai ? 1 : 0, (await new ProjectTestStore(rig.Disk.User, "default").ListSourcesAsync()).Count);
            Assert.Empty(await new ProjectTestStore(rig.Disk.User, "default").ListCaseSetsAsync());
        }
        finally
        {
            release?.Invoke(); rig.Ledger.ReleasePending(); tail.Release.TrySetResult();
            _ = await Record.ExceptionAsync(() => work);
            if (leave is not null) _ = await Record.ExceptionAsync(() => leave);
            if (stop is not null) _ = await Record.ExceptionAsync(() => stop);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnershipAndHold_preserves_first_failure_and_attempts_both_disposals_after_actual_stop(bool stopFails)
    {
        var ledger = new SourceAcceptanceScenarioLedger("default"); var gate = new AsyncTestGate();
        var first = new InvalidOperationException("Synthetic first failure"); var calls = 0;
        using var figma = new DisposalHandler(() => { calls++; if (!stopFails) throw first; });
        using var ai = new DisposalHandler(() => { calls++; throw new IOException("Synthetic later failure"); });
        var owner = new SourceAcceptanceOwnership(ledger, figma, ai, async _ => { await gate.HoldAsync(); if (stopFails) throw first; });
        var stop = owner.StopAsync();
        await gate.Entered.Task.WaitAsync(Budget);
        Assert.False(stop.IsCompleted); Assert.Equal(0, calls); Assert.False(ledger.CommandsOpen);
        gate.Release.TrySetResult();
        Assert.Same(first, await Assert.ThrowsAsync<InvalidOperationException>(() => stop));
        Assert.Equal(2, calls); Assert.Same(stop, owner.StopAsync());
    }

    [Fact]
    public async Task OwnershipAndHold_latches_completion_before_invoking_a_reentrant_stop_prefix()
    {
        var ledger = new SourceAcceptanceScenarioLedger("default"); var gate = new AsyncTestGate();
        using var figma = new DisposalHandler(() => { }); using var ai = new DisposalHandler(() => { });
        SourceAcceptanceOwnership? owner = null; Task? nested = null;
        owner = new(ledger, figma, ai, _ => { nested = owner!.StopAsync(); return gate.HoldAsync(); });
        var stop = owner.StopAsync();
        try { await gate.Entered.Task.WaitAsync(Budget); Assert.Same(stop, nested); Assert.False(stop.IsCompleted); }
        finally { gate.Release.TrySetResult(); await stop; }
    }

    [Fact]
    public async Task ProtocolAndLimits_stale_captured_release_never_retargets_the_next_held_request()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        Task<TestSourceSnapshot>? second = null;
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold));
        var first = rig.ImportFigma();
        try
        {
            await Until(() => { using var s = State(rig.Ledger); return s.RootElement.GetProperty("phase").GetString() == "held"; });
            var oldRelease = Assert.IsType<Action>(rig.Ledger.CaptureRelease()); oldRelease(); oldRelease(); await first;
            Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); second = rig.ImportFigma();
            await Until(() => { using var s = State(rig.Ledger); return s.RootElement.GetProperty("phase").GetString() == "held"; });
            oldRelease(); Assert.False(second.IsCompleted);
            Assert.False((await rig.Command("source-acceptance-arm-figma-happy")).Succeeded);
            Assert.True((await rig.Command("source-acceptance-release")).Succeeded); await second;
            Assert.Equal(2, Count(rig.Ledger, "figma", "accepted"));
        }
        finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => first); if (second is not null) _ = await Record.ExceptionAsync(() => second); }
    }

    [Theory]
    [InlineData(false, false, "TestingFailed")] [InlineData(false, true, "InvalidTestSource")] [InlineData(true, true, "InvalidTestSource")]
    public async Task FailureAndPartial_actual_HTTP_or_malformed_reply_preserves_old_records_and_selection(bool ai, bool malformed, string code)
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Happy)); var source = await rig.ImportFigma();
        var set = await rig.Controller.GenerateAsync([source.Id]); var sourceBytes = await File.ReadAllBytesAsync(SourcePath(rig.Disk.User, source.Id));
        var caseBytes = await File.ReadAllBytesAsync(CasePath(rig.Disk.User, set.Id));
        Assert.True(rig.Ledger.TryArm(ai ? SourceAcceptanceProvider.Ai : SourceAcceptanceProvider.Figma,
            malformed ? SourceAcceptanceScenario.Malformed : SourceAcceptanceScenario.HttpFailure));
        var error = await Assert.ThrowsAsync<PluginTestingException>(async () => { if (ai) await rig.GenerateAi(source.Id); else await rig.ImportFigma(); });
        Assert.Equal(code, error.ErrorCode); Assert.Null(error.InnerException);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(SourcePath(rig.Disk.User, source.Id)));
        Assert.Equal(caseBytes, await File.ReadAllBytesAsync(CasePath(rig.Disk.User, set.Id)));
        Assert.Equal(set.Id, rig.Controller.Snapshot.Draft!.Id);
        Assert.Single(await rig.Context.Testing.ListSourcesAsync()); Assert.Single(await rig.Context.Testing.ListCaseSetsAsync());
        Assert.DoesNotContain(SourceAcceptanceProtocol.OpenAiKey, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcceptanceProtocol.FigmaToken, error.ToString(), StringComparison.Ordinal);
        Assert.All(rig.Diagnostics, diagnostic => Assert.DoesNotContain("source-acceptance-", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailureAndPartial_receipt_is_reopened_after_real_prerequisite_bound_refuses_case_publication()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Happy)); var source = await rig.ImportFigma();
        var old = await rig.Controller.GenerateAsync([source.Id]);
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Ai, SourceAcceptanceScenario.Partial)); var result = await rig.GenerateAi(source.Id);
        Assert.Equal(AiGenerationPublicationState.Failed, result.State); Assert.Equal("StoreLimitExceeded", result.ErrorCode); Assert.Null(result.CaseSet);
        Assert.Equal(old.Id, rig.Controller.Snapshot.Draft!.Id);
        var store = new ProjectTestStore(rig.Disk.User, "default");
        Assert.Equal(result.Receipt.OriginalBytes, (await store.ReadSourceAsync(result.Receipt.Id)).OriginalBytes);
        Assert.Equal(2, (await store.ListSourcesAsync()).Count); Assert.Single(await store.ListCaseSetsAsync());
        Assert.Equal(1, Count(rig.Ledger, "ai", "entered")); Assert.Equal(1, Count(rig.Ledger, "ai", "settled"));
    }

    [Fact]
    public async Task ProtocolAndLimits_unarmed_or_foreign_requests_fail_closed_and_schema_cannot_leak_the_request()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        Assert.Equal("TestingFailed", (await Assert.ThrowsAsync<PluginTestingException>(() => rig.ImportFigma())).ErrorCode);
        using var client = new HttpClient(rig.Figma, disposeHandler: false);
        using var message = new HttpRequestMessage(HttpMethod.Get, "https://synthetic.invalid/private-path");
        message.Headers.Add("X-Figma-Token", "foreign-synthetic-private-value");
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Happy));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(message));
        Assert.Equal("Synthetic source request refused.", error.Message); Assert.Null(error.InnerException);
        Assert.Equal(2, Count(rig.Ledger, "figma", "entered")); Assert.Equal(0, Count(rig.Ledger, "figma", "accepted"));
        Assert.Equal(2, Count(rig.Ledger, "figma", "unexpected")); Assert.Equal(2, Count(rig.Ledger, "figma", "settled"));
        using var state = State(rig.Ledger); var root = state.RootElement;
        Assert.Equal(new[] { "ai", "figma", "fixtureId", "generation", "instanceId", "outcome", "phase", "pluginId", "projectId", "provider", "scenario", "schemaVersion" }, root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var provider in new[] { "ai", "figma" })
            Assert.Equal(new[] { "accepted", "cancellationObserved", "entered", "limitExceeded", "settled", "unexpected" }, root.GetProperty(provider).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("instanceId").GetString()!);
        Assert.DoesNotContain("private", rig.Ledger.StateJson(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new SourceAcceptanceScenarioLedger("not-canonical"));
    }

    [Fact]
    public async Task HappyTraceAndReopen_default_factory_retains_real_supervisor_while_remote_cases_stay_manual()
    {
        await using var rig = await Rig.CreateAsync();
        var source = await rig.Controller.ImportExampleAsync(TestSourceKind.Requirements); var set = await rig.Controller.GenerateAsync([source.Id]);
        Assert.False((await rig.Context.BackgroundTasks.StartFromHostAsync(TestingController.BackgroundTaskId, CancellationToken.None)).Succeeded);
        await rig.Controller.RunAsync(set.Cases.Take(2).Select(c => c.Id).ToArray());
        var run = Assert.IsType<TestRunSnapshot>(await rig.Controller.WaitForRunSettlementAsync());
        Assert.Equal(TestRunState.Completed, run.State);
        Assert.Equal(8, run.Events.Count(e => e.Kind == TestRunEventKind.StepFinished));
        Assert.Equal(run.HeadHash, (await new ProjectTestStore(rig.Disk.User, "default").ReadRunAsync(run.Plan.Id)).HeadHash);
        Assert.Equal(0, Count(rig.Ledger, "figma", "entered")); Assert.Equal(0, Count(rig.Ledger, "ai", "entered"));
    }

    private static async Task Until(Func<bool> observed)
    { var watch = Stopwatch.StartNew(); while (!observed()) { Assert.True(watch.Elapsed < Budget, "Actual fixture boundary not reached."); await Task.Delay(1); } }
    private sealed class DisposalHandler(Action dispose) : HttpMessageHandler
    {
        private bool _disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override void Dispose(bool disposing) { if (!_disposed) { _disposed = true; dispose(); } base.Dispose(disposing); }
    }
}
