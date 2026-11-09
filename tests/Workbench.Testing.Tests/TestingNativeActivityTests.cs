using System.Net;
using System.Text;
using Workbench.Sdk;
using Workbench.Testing;
using Workbench.Testing.Domain;
using Xunit;

namespace Workbench.Core.Tests;

[Collection(BlockingCallbackCollection.Name)]
public sealed class TestingNativeActivityTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static Task Notify() => Task.CompletedTask;

    [Fact]
    public async Task Conflict_and_final_idle_refuse_every_transition_before_capture_or_backend_prefix()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var set = await fixture.ImportAndGenerateAsync();
        var parent = await fixture.Controller.RunAsync([set.Cases[0].Id]);
        await fixture.Controller.WaitForRunSettlementAsync();
        var remote = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, remote);
        var io = new AsyncTestGate(); var ui = new AsyncTestGate(); var calls = 0;
        var accepted = activity.SourceAsync(() => 7, async value => { await io.HoldAsync(); return value; }, ui.HoldAsync);
        try
        {
            await io.Entered.Task.WaitAsync(Budget);
            Assert.True(activity.IsBusy);
            await Refused(() => activity.SourceAsync(() => { calls++; return 1; }, Task.FromResult, Notify));
            await Refused(() => activity.RemoteSourceAsync(() => { calls++; return 1; }, (value, _) => Task.FromResult(value), _ => Notify(), Notify));
            Func<Task<TestRunSnapshot>>[] transitions =
            [
                () => fixture.Controller.RunAsync([set.Cases[0].Id]),
                () => fixture.Controller.QueueRunAsync([set.Cases[0].Id]),
                () => fixture.Controller.StartQueuedRunAsync(),
                () => fixture.Controller.RetryAsync(parent.Plan.Id)
            ];
            foreach (var transition in transitions)
                await Refused(() => activity.VirtualAsync(() => { calls++; return transition(); }, Notify));
            io.Release.TrySetResult(); await ui.Entered.Task.WaitAsync(Budget);
            Assert.False(activity.IsBusy); // Informational idle is earlier than actual completion.
            Assert.False(accepted.IsCompleted); Assert.False(activity.DrainAsync().IsCompleted);
            await Refused(() => activity.VirtualAsync(() => { calls++; return Task.FromResult(2); }, Notify));
            Assert.Equal(0, calls);
        }
        finally { io.Release.TrySetResult(); ui.Release.TrySetResult(); await accepted; await activity.DrainAsync(); }
        Assert.Equal(9, await activity.VirtualAsync(() => Task.FromResult(9), Notify));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentAttemptRefusal_blocks_real_local_and_remote_publication_without_changing_live_run(bool running)
    {
        using var handler = new FigmaHandler(); var adapter = new GatedVirtualAdapter();
        var fixture = await TestingDomainFixture.CreateAsync(adapter, figmaReader: new(handler));
        var remote = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, remote);
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); var run = await fixture.Controller.QueueRunAsync([set.Cases[0].Id]);
            if (running) { await fixture.Controller.StartQueuedRunAsync(); await adapter.ActionGate.Entered.Task.WaitAsync(Budget); }
            var before = await fixture.Context.Testing.ReadRunAsync(run.Plan.Id); var sources = fixture.Controller.Snapshot.Sources.Count;
            var credentials = 0;
            await Refused(() => activity.SourceAsync(() => TestSourceKind.Figma, kind => fixture.Controller.ImportExampleAsync(kind), Notify));
            await Refused(() => activity.RemoteSourceAsync(FigmaInput,
                (input, token) => fixture.Controller.ImportFigmaAsync(input.Request, input.Criteria, _ =>
                { credentials++; return Task.FromResult<string?>("synthetic-only-token"); }, token), failure =>
                { Assert.IsType<NativeActivityAdmissionException>(failure); return Notify(); }, Notify));
            Assert.Equal(0, credentials); Assert.Equal(0, handler.Requests); Assert.False(remote.IsBusy);
            Assert.Equal(sources, fixture.Controller.Snapshot.Sources.Count);
            var unchanged = await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.Equal(before.HeadHash, unchanged.HeadHash); Assert.Equal(before.Sequence, unchanged.Sequence);
        }
        finally { adapter.ReleaseAll(); await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proven_predelegate_remote_refusal_is_tagged_without_running_capture_or_operation_settlement(bool terminal)
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); var remote = new NativeRemoteOperation();
        var activity = new NativeActivityCoordinator(fixture.Controller, remote); using var seal = remote.Seal();
        if (terminal) remote.Terminal();
        var calls = 0;
        await Refused(() => activity.RemoteSourceAsync(() => { calls++; return 1; }, (value, _) => Task.FromResult(value),
            _ => { calls++; return Notify(); }, Notify), terminal ? "TestingContextClosed" : "StoreBusy");
        Assert.Equal(0, calls); await activity.DrainAsync();
    }

    [Fact]
    public async Task TerminalHandoff_joins_shared_actual_Stop_and_supervisor_postfix_before_source_prefix()
    {
        var stop = new AsyncTestGate(); var postfix = new AsyncTestGate();
        var fixture = await TestingDomainFixture.CreateAsync(firstStopPrefix: stop, delegatePostfix: postfix);
        var activity = new NativeActivityCoordinator(fixture.Controller, new());
        Task<int>? source = null; Task<TestRunSnapshot?>? oldWaiter = null;
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); var run = await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await postfix.Entered.Task.WaitAsync(Budget);
            var terminal = await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.Equal(TestRunState.Completed, terminal.State); Assert.True(fixture.Controller.Snapshot.OwnsRun);
            var calls = 0;
            source = activity.SourceAsync(() => "captured", value => { Assert.Equal("captured", value); calls++; return Task.FromResult(42); }, Notify);
            await Task.WhenAny(stop.Entered.Task, source).WaitAsync(Budget);
            Assert.True(stop.Entered.Task.IsCompleted, "Source handoff must enter the actual supervisor retirement, before its backend.");
            oldWaiter = fixture.Controller.WaitForRunSettlementAsync();
            Assert.Equal(0, calls); Assert.False(source.IsCompleted); Assert.False(oldWaiter.IsCompleted);
            Assert.Equal(1, fixture.RetirementStopCalls); // Both waiters share the held first actual Stop.
            await Refused(() => activity.VirtualAsync(() => fixture.Controller.RunAsync([set.Cases[0].Id]), Notify));
            stop.Release.TrySetResult();
            Assert.False((await fixture.FirstStopReturned.WaitAsync(Budget)).Succeeded);
            Assert.Equal(PluginBackgroundTaskState.Stopping, Assert.Single(fixture.Tasks.GetSnapshots()).State);
            Assert.False(source.IsCompleted); Assert.False(oldWaiter.IsCompleted); Assert.Equal(0, calls);
            postfix.Release.TrySetResult();
            Assert.Equal(42, await source.WaitAsync(Budget));
            Assert.Equal(run.Plan.Id, Assert.IsType<TestRunSnapshot>(await oldWaiter.WaitAsync(Budget)).Plan.Id);
            var unchanged = await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.Equal(terminal.HeadHash, unchanged.HeadHash); Assert.Equal(terminal.Sequence, unchanged.Sequence);
            Assert.DoesNotContain(unchanged.Events, e => e.Kind is TestRunEventKind.CancelRequested or TestRunEventKind.Cancelled);
            Assert.True(fixture.RetirementStopCalls >= 2); // A finite pending Stop must be joined again, by the same retirement.
            Assert.Equal(1, calls);
        }
        finally
        {
            stop.Release.TrySetResult(); postfix.Release.TrySetResult();
            if (source is not null) _ = await Record.ExceptionAsync(() => source);
            if (oldWaiter is not null) _ = await Record.ExceptionAsync(() => oldWaiter);
            await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteHandoffCancel_has_CTS_before_capture_and_never_starts_credentials_or_HTTP(bool reentrant)
    {
        using var handler = new FigmaHandler(); var stop = new AsyncTestGate(); var postfix = new AsyncTestGate();
        var fixture = await TestingDomainFixture.CreateAsync(firstStopPrefix: stop, delegatePostfix: postfix, figmaReader: new(handler));
        var remote = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, remote);
        var ui = new AsyncTestGate(); var credentials = 0; var captures = 0;
        Task<TestSourceSnapshot>? source = null; Task? cancel = null;
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); var run = await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await postfix.Entered.Task.WaitAsync(Budget); var terminal = await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            var thread = Environment.CurrentManagedThreadId;
            source = activity.RemoteSourceAsync(() =>
            {
                Assert.Equal(thread, Environment.CurrentManagedThreadId); Assert.True(remote.IsBusy); captures++;
                if (reentrant) cancel = remote.RequestCancellationAsync();
                return FigmaInput();
            }, (input, token) => fixture.Controller.ImportFigmaAsync(input.Request, input.Criteria, _ =>
            { credentials++; return Task.FromResult<string?>("synthetic-only-token"); }, token), _ => Notify(), ui.HoldAsync);
            Assert.Equal(1, captures);
            await Task.WhenAny(stop.Entered.Task, source, ui.Entered.Task).WaitAsync(Budget);
            Assert.True(stop.Entered.Task.IsCompleted, "Cancellation must not skip the actual retirement join.");
            cancel ??= remote.RequestCancellationAsync(); await cancel;
            Assert.True(remote.IsBusy); Assert.False(source.IsCompleted); Assert.False(activity.DrainAsync().IsCompleted);
            Assert.Equal(0, credentials); Assert.Equal(0, handler.Requests);
            stop.Release.TrySetResult(); await fixture.FirstStopReturned.WaitAsync(Budget);
            Assert.False(source.IsCompleted); postfix.Release.TrySetResult();
            await ui.Entered.Task.WaitAsync(Budget);
            Assert.False(remote.IsBusy); Assert.False(source.IsCompleted);
            await Refused(() => activity.VirtualAsync(() => Task.FromResult(1), Notify));
            ui.Release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source);
            Assert.Equal(0, credentials); Assert.Equal(0, handler.Requests);
            Assert.Equal(terminal.HeadHash, (await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).HeadHash);
            await remote.RequestCancellationAsync();
            Assert.True(await activity.RemoteSourceAsync(() => true, (value, token) =>
            { Assert.False(token.IsCancellationRequested); return Task.FromResult(value); }, _ => Notify(), Notify));
        }
        finally
        {
            stop.Release.TrySetResult(); postfix.Release.TrySetResult(); ui.Release.TrySetResult();
            if (source is not null) _ = await Record.ExceptionAsync(() => source);
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task TypedRefusalAndCapture_keeps_acknowledged_status_and_same_code_operation_failures_distinct()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var receipt = await fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements);
        var draft = new NativeSourceDraft(); draft.CompleteFigma(receipt);
        var activity = new NativeActivityCoordinator(fixture.Controller, new()); var io = new AsyncTestGate(); var ui = new AsyncTestGate();
        var original = draft.Status; var captures = 0;
        var accepted = activity.RemoteSourceAsync(() => 1, async (value, _) => { await io.HoldAsync(); return value; }, _ => Notify(), ui.HoldAsync);
        try
        {
            await io.Entered.Task.WaitAsync(Budget);
            var refusal = await Refused(() => activity.RemoteSourceAsync<int, int>(() =>
            { captures++; draft.Begin("must not run"); throw new PluginTestingException("StoreBusy"); }, (value, _) => Task.FromResult(value),
                failure => { if (failure is not null) draft.Fail(failure); return Notify(); }, Notify));
            Assert.Equal("StoreBusy", refusal.ToTestingException().ErrorCode); Assert.Equal(0, captures); Assert.Equal(original, draft.Status);
            io.Release.TrySetResult(); await ui.Entered.Task.WaitAsync(Budget);
            await Refused(() => activity.SourceAsync(() => { captures++; return 2; }, Task.FromResult, Notify));
            Assert.Equal(original, draft.Status);
        }
        finally { io.Release.TrySetResult(); ui.Release.TrySetResult(); await accepted; }
        foreach (var captureFailure in new[] { true, false })
        {
            var operationError = new PluginTestingException("StoreBusy"); var settlements = 0;
            var work = activity.RemoteSourceAsync(() => { if (captureFailure) throw operationError; return 1; },
                (int _, CancellationToken _) => Task.FromException<int>(operationError), failure =>
                { settlements++; Assert.Same(operationError, failure); draft.Fail(failure!); return Notify(); }, Notify);
            Assert.Same(operationError, await Assert.ThrowsAsync<PluginTestingException>(() => work));
            Assert.Equal(1, settlements); Assert.Contains(receipt.Id, draft.Status); Assert.Contains("StoreBusy", draft.Status);
        }
    }

    [Fact]
    public async Task Captured_mutable_Figma_input_and_AI_selection_survive_actual_terminal_wait_and_later_form_input()
    {
        using var handler = new FigmaHandler(); var postfix = new AsyncTestGate();
        var fixture = await TestingDomainFixture.CreateAsync(delegatePostfix: postfix, figmaReader: new(handler));
        var activity = new NativeActivityCoordinator(fixture.Controller, new()); Task<TestSourceSnapshot>? source = null;
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await postfix.Entered.Task.WaitAsync(Budget);
            string[] nodes = ["1:2"]; string[] criteria = ["  Original literal  "];
            var input = new NativeFigmaInput(new("Ab12", nodes, "v1"), new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = criteria });
            string[] selected = [new('a', 32)]; var captures = 0;
            var ownedSelected = NativeActivityInputs.Selected(() => selected);
            source = activity.RemoteSourceAsync(() => { captures++; return NativeActivityInputs.Figma(() => input); },
                (captured, token) => fixture.Controller.ImportFigmaAsync(captured.Request, captured.Criteria,
                    _ => Task.FromResult<string?>("synthetic-only-token"), token), _ => Notify(), Notify);
            Assert.Equal(1, captures); Assert.False(source.IsCompleted);
            nodes[0] = "8:9"; criteria[0] = "Changed"; selected[0] = new('b', 32);
            var draft = new NativeSourceDraft(); draft.Capture("Correction", "", [new("7:8", "Later input")], selected);
            postfix.Release.TrySetResult(); var saved = await source.WaitAsync(Budget);
            Assert.Equal("Ab12@v1", saved.DocumentId); Assert.Equal("1:2", Assert.Single(saved.Items).Locator);
            Assert.Equal("  Original literal  ", Assert.Single(saved.Items[0].AcceptanceCriteria));
            Assert.Equal(new string('a', 32), Assert.Single(ownedSelected)); Assert.Equal("Correction", draft.Reopen().FileKey);
            Assert.Equal(1, handler.Requests); Assert.Equal(1, captures);
        }
        finally { postfix.Release.TrySetResult(); if (source is not null) _ = await Record.ExceptionAsync(() => source); await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalIdleAndLifecycle_seals_delayed_prefixes_and_joins_cancellation_and_final_dispatch(bool terminal)
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var owner = new NativeActionOwner(); var remote = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, remote);
        var prefix = new AsyncTestGate(); var action = new AsyncTestGate(); var ui = new AsyncTestGate(); var callback = new AsyncTestGate();
        var work = owner.UserAsync(() => activity.RemoteSourceAsync(() => 1, async (value, token) =>
        {
            using var registration = token.Register(() => callback.HoldAsync().GetAwaiter().GetResult());
            await action.HoldAsync(); return value;
        }, _ => Notify(), ui.HoldAsync));
        Task? delayed = null;
        try
        {
            await action.Entered.Task.WaitAsync(Budget);
            delayed = owner.UserAsync(async () => { await prefix.HoldAsync(); return await activity.VirtualAsync(() => Task.FromResult(2), Notify); });
            await prefix.Entered.Task.WaitAsync(Budget);
            using var ownerSeal = owner.Seal(); using var activitySeal = activity.Seal(); using var remoteSeal = remote.Seal();
            if (terminal) { owner.Terminal(); activity.Terminal(); remote.Terminal(); }
            var cancel = remote.RequestCancellationAsync(); await callback.Entered.Task.WaitAsync(Budget);
            var ownerDrain = owner.DrainAsync(); var activityDrain = activity.DrainAsync();
            prefix.Release.TrySetResult();
            var refusal = await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => delayed);
            Assert.Equal(terminal ? "TestingContextClosed" : "StoreBusy", refusal.ErrorCode);
            action.Release.TrySetResult(); Assert.False(work.IsCompleted); Assert.False(activityDrain.IsCompleted);
            callback.Release.TrySetResult(); await cancel; await ui.Entered.Task.WaitAsync(Budget);
            Assert.False(work.IsCompleted); Assert.False(ownerDrain.IsCompleted); Assert.False(activityDrain.IsCompleted);
            await owner.InternalAsync(() => Task.CompletedTask);
            ui.Release.TrySetResult(); await work; await ownerDrain; await activityDrain;
        }
        finally { prefix.Release.TrySetResult(); action.Release.TrySetResult(); callback.Release.TrySetResult(); ui.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => work); if (delayed is not null) _ = await Record.ExceptionAsync(() => delayed); }
        if (terminal) await Refused(() => activity.VirtualAsync(() => Task.FromResult(3), Notify), "TestingContextClosed");
        else Assert.Equal(3, await activity.VirtualAsync(() => Task.FromResult(3), Notify));
    }

    [Fact]
    public async Task Observer_failure_preserves_acknowledged_outcome_and_the_original_operation_failure()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); var activity = new NativeActivityCoordinator(fixture.Controller, new());
        var set = await fixture.ImportAndGenerateAsync();
        var run = await activity.VirtualAsync(async () =>
        { await fixture.Controller.RunAsync([set.Cases[0].Id]); return Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync()); },
            () => throw new IOException("synthetic observer failure"));
        Assert.Equal(TestRunState.Completed, run.State); Assert.Equal(run.HeadHash, (await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).HeadHash);
        var original = new PluginTestingException("StoreIoFailure");
        var error = await Assert.ThrowsAsync<PluginTestingException>(() => activity.VirtualAsync(() => Task.FromException<int>(original),
            () => throw new IOException("different observer failure")));
        Assert.Same(original, error); Assert.Equal(5, await activity.VirtualAsync(() => Task.FromResult(5), Notify));
        Assert.Equal(6, await activity.RemoteSourceAsync(() => 6, (value, _) => Task.FromResult(value),
            _ => throw new IOException("remote observer failure"), () => throw new IOException("final observer failure")));
    }

    [Fact]
    public async Task Throwing_cancel_callback_remains_owned_without_replacing_acknowledged_receipt()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); var receipt = await fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements);
        var draft = new NativeSourceDraft(); var remote = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, remote);
        var actual = new AsyncTestGate(); var ui = new AsyncTestGate();
        var work = activity.RemoteSourceAsync(() => 1, async (value, token) =>
        {
            using var registration = token.Register(() => throw new IOException("synthetic callback failure"));
            await actual.HoldAsync(); draft.CompleteFigma(receipt); return value;
        }, failure => { if (failure is not null) draft.Fail(failure); return Notify(); }, ui.HoldAsync);
        try
        {
            await actual.Entered.Task.WaitAsync(Budget); await Assert.ThrowsAnyAsync<Exception>(() => remote.RequestCancellationAsync());
            Assert.False(work.IsCompleted); Assert.False(activity.DrainAsync().IsCompleted);
            actual.Release.TrySetResult(); await ui.Entered.Task.WaitAsync(Budget);
            Assert.Contains(receipt.Id, draft.Status); Assert.Contains("TestingFailed", draft.Status); Assert.False(work.IsCompleted);
        }
        finally { actual.Release.TrySetResult(); ui.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => work); await activity.DrainAsync(); }
    }

    private static async Task<NativeActivityAdmissionException> Refused(Func<Task> action, string code = "StoreBusy")
    { var error = await Assert.ThrowsAsync<NativeActivityAdmissionException>(action); Assert.Equal(code, error.ErrorCode); return error; }

    private static NativeFigmaInput FigmaInput() => new(new("Ab12", ["1:2"], "v1"),
        new Dictionary<string, IReadOnlyList<string>> { ["1:2"] = ["  Original literal  "] });

    private sealed class FigmaHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("https://api.figma.com/v1/files/Ab12/nodes?ids=1%3A2&depth=2&version=v1", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"name":"Observed","version":"v1","nodes":{"1:2":{"document":{"id":"1:2","name":"First","type":"FRAME"}}}}""", Encoding.UTF8, "application/json") });
        }
    }
}
