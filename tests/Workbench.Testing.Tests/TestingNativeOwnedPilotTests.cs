using Workbench.Sdk;
using Workbench.Testing;
using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

[Collection(BlockingCallbackCollection.Name)]
public sealed class TestingNativeOwnedPilotTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static Task Notify() => Task.CompletedTask;

    [Fact]
    public async Task Selected_terminal_history_cannot_unlock_the_actual_live_virtual_attempt()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        using var rig = new OwnedLabTestRig(); var pilot = rig.Controller(); await pilot.InitializeAsync();
        var activity = new NativeActivityCoordinator(fixture.Controller, new()); var bridge = new NativeOwnedPilot(pilot, activity, new());
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); var old = await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await fixture.Controller.WaitForRunSettlementAsync();
            var current = await fixture.Controller.QueueRunAsync([set.Cases[0].Id]); await fixture.Controller.SelectRunAsync(old.Plan.Id);
            var before = await fixture.Context.Testing.ReadRunAsync(current.Plan.Id);
            await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => bridge.RunAsync(() => OwnedScenario.Positive, Notify));
            Assert.Equal(0, rig.Driver.PrepareCount); Assert.Empty(pilot.Snapshot.Runs);
            Assert.Equal(old.Plan.Id, fixture.Controller.Snapshot.SelectedRun!.Plan.Id);
            Assert.Equal(before.HeadHash, (await fixture.Context.Testing.ReadRunAsync(current.Plan.Id)).HeadHash);
        }
        finally { await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Running_virtual_attempt_refuses_owned_without_cancelling_or_changing_it()
    {
        var adapter = new GatedVirtualAdapter(); var fixture = await TestingDomainFixture.CreateAsync(adapter);
        using var rig = new OwnedLabTestRig(); var pilot = rig.Controller(); await pilot.InitializeAsync();
        var bridge = new NativeOwnedPilot(pilot, new(fixture.Controller, new()), new());
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); var run = await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await adapter.ActionGate.Entered.Task.WaitAsync(Budget); var before = await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => bridge.RunAsync(() => OwnedScenario.Positive, Notify));
            Assert.Equal(0, rig.Driver.PrepareCount);
            Assert.Equal(before.HeadHash, (await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).HeadHash);
        }
        finally { adapter.ReleaseAll(); await pilot.StopAsync(); await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Pending_handoff_cancel_has_an_entry_before_capture_and_never_creates_owned_bytes(bool reentrant)
    {
        var stop = new AsyncTestGate(); var postfix = new AsyncTestGate();
        var fixture = await TestingDomainFixture.CreateAsync(firstStopPrefix: stop, delegatePostfix: postfix);
        using var rig = new OwnedLabTestRig(); var pilot = rig.Controller(); await pilot.InitializeAsync();
        var operation = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, new());
        var bridge = new NativeOwnedPilot(pilot, activity, operation); var initial = new AsyncTestGate();
        Task<OwnedRunSnapshot>? work = null; Task? cancel = null;
        try
        {
            var set = await fixture.ImportAndGenerateAsync(); await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await postfix.Entered.Task.WaitAsync(Budget);
            var capturedThread = Environment.CurrentManagedThreadId;
            work = bridge.RunAsync(() =>
            {
                Assert.Equal(capturedThread, Environment.CurrentManagedThreadId); Assert.True(operation.IsBusy);
                if (reentrant) cancel = bridge.CancelAsync();
                return OwnedScenario.Positive;
            }, async () =>
            {
                if (!bridge.IsPendingHandoff) return;
                Assert.True(bridge.IsBusy); Assert.False(activity.CanAdmit); Assert.False(pilot.Snapshot.IsBusy);
                await initial.HoldAsync();
            });
            await Task.WhenAny(initial.Entered.Task, work).WaitAsync(Budget); Assert.True(initial.Entered.Task.IsCompleted);
            await stop.Entered.Task.WaitAsync(Budget); cancel ??= bridge.CancelAsync();
            Assert.False(cancel.IsCompleted); Assert.False(work.IsCompleted); Assert.Equal(0, rig.Driver.PrepareCount);
            initial.Release.TrySetResult(); stop.Release.TrySetResult(); await fixture.FirstStopReturned.WaitAsync(Budget);
            Assert.False(work.IsCompleted); postfix.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); await cancel.WaitAsync(Budget);
            Assert.Empty(pilot.Snapshot.Runs); Assert.Equal(0, rig.Driver.LaunchCount); Assert.Equal(0, rig.Driver.ActionCalls);
            await bridge.CancelAsync();
            Assert.Equal(OwnedRunState.Passed, (await bridge.RunAsync(() => OwnedScenario.Positive, Notify)).State);
        }
        finally
        {
            initial.Release.TrySetResult(); stop.Release.TrySetResult(); postfix.Release.TrySetResult();
            if (work is not null) _ = await Record.ExceptionAsync(() => work);
            if (cancel is not null) _ = await Record.ExceptionAsync(() => cancel);
            await pilot.StopAsync(); await fixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(OwnedScenario.Positive, OwnedRunState.Passed, 4)]
    [InlineData(OwnedScenario.EmptyTitle, OwnedRunState.Passed, 4)]
    [InlineData(OwnedScenario.AssertionFailure, OwnedRunState.Failed, 3)]
    public async Task Bridge_runs_fixed_catalog_and_reads_actual_private_journal_frames(OwnedScenario scenario, OwnedRunState state, int results)
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var bridge = new NativeOwnedPilot(pilot, new(fixture.Controller, new()), new());
        try
        {
            var run = await bridge.RunAsync(() => scenario, () => throw new IOException("observer only"));
            Assert.Equal(state, run.State); Assert.Equal(results, run.Events.Count(e => e.Kind == OwnedEventKind.StepResult));
            Assert.Equal("AcceptanceFixture", run.Execution.Helper.Profile);
            foreach (var e in run.Events.Where(e => e.Kind == OwnedEventKind.StepResult))
            {
                Assert.Equal(OwnedLabTestRig.Bmp(), await pilot.ReadFrameAsync(run.Execution.RunId, e.Sequence));
                Assert.Equal("OwnedWindowPrintWindow", e.CaptureKind); Assert.True(e.SyntheticTarget);
            }
            Assert.Equal(scenario == OwnedScenario.EmptyTitle ? "0" : "1", run.Events.Last(e => e.State is not null).State!.CreatedCount);
        }
        finally { await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Retry_captures_parent_before_real_terminal_handoff_and_uses_current_helper()
    {
        var postfix = new AsyncTestGate(); var fixture = await TestingDomainFixture.CreateAsync(delegatePostfix: postfix);
        using var rig = new OwnedLabTestRig(); var pilot = rig.Controller(); await pilot.InitializeAsync();
        var bridge = new NativeOwnedPilot(pilot, new(fixture.Controller, new()), new()); Task<OwnedRunSnapshot>? retry = null;
        try
        {
            var parent = await bridge.RunAsync(() => OwnedScenario.Positive, Notify);
            var set = await fixture.ImportAndGenerateAsync(); await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await postfix.Entered.Task.WaitAsync(Budget); var id = parent.Execution.RunId; var captures = 0;
            retry = bridge.RetryAsync(() => { captures++; return id; }, Notify);
            Assert.False(retry.IsCompleted); Assert.Equal(1, captures); id = new('f', 32);
            rig.Driver.CurrentHelper = rig.Driver.CurrentHelper with { ImageSha = new('4', 64) };
            postfix.Release.TrySetResult(); var child = await retry.WaitAsync(Budget);
            Assert.Equal(parent.Execution.RunId, child.Execution.ParentRunId); Assert.NotEqual(parent.Execution.RunId, child.Execution.RunId);
            Assert.Equal(parent.Input.Bytes.ToArray(), child.Input.Bytes.ToArray()); Assert.Equal(new string('4', 64), child.Execution.Helper.ImageSha);
        }
        finally { postfix.Release.TrySetResult(); if (retry is not null) _ = await Record.ExceptionAsync(() => retry); await pilot.StopAsync(); await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Corrupt_parent_refuses_retry_before_helper_preparation()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var bridge = new NativeOwnedPilot(pilot, new(fixture.Controller, new()), new());
        try
        {
            var parent = await bridge.RunAsync(() => OwnedScenario.Positive, Notify); var prepared = rig.Driver.PrepareCount;
            await File.WriteAllTextAsync(Path.Combine(rig.Root, "attempts", parent.Execution.RunId, "input.json"), "corrupt");
            var error = await Assert.ThrowsAsync<OwnedLabException>(() => bridge.RetryAsync(() => parent.Execution.RunId, Notify));
            Assert.Equal("RecoveryNeeded", error.ErrorCode); Assert.Equal(prepared, rig.Driver.PrepareCount);
            await Assert.ThrowsAsync<OwnedLabException>(() => bridge.RunAsync(() => OwnedScenario.Positive, Notify));
        }
        finally { await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Owned_final_idle_keeps_all_shared_starts_refused_through_actual_notification_tail()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var source = new NativeRemoteOperation();
        var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var bridge = new NativeOwnedPilot(pilot, activity, owned); var final = new AsyncTestGate(); var calls = 0;
        var work = bridge.RunAsync(() => OwnedScenario.Positive, () => !activity.IsBusy ? final.HoldAsync() : Notify());
        try
        {
            await final.Entered.Task.WaitAsync(Budget);
            Assert.False(pilot.Snapshot.IsBusy); Assert.False(owned.IsBusy); Assert.False(activity.CanAdmit); Assert.False(work.IsCompleted);
            await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => activity.SourceAsync(() => { calls++; return 1; }, Task.FromResult, Notify));
            await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => activity.RemoteSourceAsync(() => { calls++; return 1; }, (x, _) => Task.FromResult(x), _ => Notify(), Notify));
            await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => activity.VirtualAsync(() => { calls++; return Task.FromResult(1); }, Notify));
            await Assert.ThrowsAsync<NativeActivityAdmissionException>(() => bridge.RunAsync(() => { calls++; return OwnedScenario.Positive; }, Notify));
            Assert.Equal(0, calls);
        }
        finally { final.Release.TrySetResult(); Assert.Equal(OwnedRunState.Passed, (await work).State); await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Scenario_is_frozen_before_the_retained_initial_notification_await()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var activity = new NativeActivityCoordinator(fixture.Controller, new());
        var bridge = new NativeOwnedPilot(pilot, activity, new()); var initial = new AsyncTestGate(); var selected = OwnedScenario.Positive; var captures = 0;
        var run = bridge.RunAsync(() => { captures++; return selected; }, () => bridge.IsPendingHandoff ? initial.HoldAsync() : Notify());
        try
        {
            await initial.Entered.Task.WaitAsync(Budget); Assert.Equal(1, captures); Assert.Equal(0, rig.Driver.PrepareCount);
            selected = OwnedScenario.EmptyTitle; initial.Release.TrySetResult();
            var result = await run; Assert.Equal(OwnedScenario.Positive, result.Input.Scenario);
            Assert.Equal("Owned pilot task", result.Events.Last(e => e.State is not null).State!.Title);
        }
        finally { initial.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => run); await pilot.StopAsync(); }
    }
}
