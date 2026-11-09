using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class OwnedAdmissionTests
{
    [Fact]
    public async Task Final_idle_notification_keeps_admission_task_and_root_ownership_until_delivery_retires()
    {
        using var rig = new OwnedLabTestRig(); var idle = new AsyncGate(); var controller = rig.Controller();
        controller.Changed += snapshot =>
        {
            if (snapshot.IsBusy || snapshot.SelectedRun?.State != OwnedRunState.Passed) return;
            idle.Entered.TrySetResult(); idle.Released.Task.GetAwaiter().GetResult();
        };
        await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await idle.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(controller.Snapshot.IsBusy); Assert.False(run.IsCompleted);
        var next = controller.RunAsync(OwnedScenario.Positive); var refused = next.IsFaulted;
        var leave = controller.PrepareLeaveAsync(); var stop = controller.StopAsync();
        var leaveHeld = !leave.IsCompleted; var stopHeld = !stop.IsCompleted;
        var lockError = await Record.ExceptionAsync(() => OwnedJournal.OpenAsync(rig.Scope));
        idle.Release(); var result = await run;
        try { await next; } catch (OwnedLabException) { }
        await leave; await stop;
        Assert.True(refused); Assert.True(leaveHeld); Assert.True(stopHeld); Assert.IsType<OwnedLabException>(lockError);
        Assert.Equal(OwnedRunState.Passed, result.State); Assert.Equal(1, rig.Driver.LaunchCount);
    }

    [Fact]
    public async Task Throwing_final_idle_notification_does_not_change_committed_outcome()
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller();
        controller.Changed += snapshot => { if (!snapshot.IsBusy && snapshot.SelectedRun?.State == OwnedRunState.Passed) throw new InvalidOperationException("informational callback"); };
        await controller.InitializeAsync(); var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.Passed, result.State); Assert.Null(result.ErrorCode); Assert.Equal(OwnedOutcome.Passed, result.Events[^1].Outcome);
        await controller.StopAsync(); await using var restored = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.Equal(OwnedRunState.Passed, restored.Read(result.Execution.RunId).State);
    }

    [Fact]
    public async Task Seal_linearizes_with_actual_resume_after_suspended_creation_returns()
    {
        using var rig = new OwnedLabTestRig(); var suspended = new AsyncGate(); rig.Driver.LaunchGate = suspended;
        var controller = rig.Controller(); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await suspended.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var seal = controller.SealAdmission(); var stop = controller.StopAsync();
        Assert.False(stop.IsCompleted); Assert.Equal(0, rig.Driver.ResumeCount); suspended.Release();
        var result = await run; await stop; Assert.Equal(0, rig.Driver.ResumeCount); Assert.Equal(OwnedRunState.Cancelled, result.State);
        Assert.Equal(new OwnedRetirement(true, -1, null, 0), result.Events[^1].Retirement);
        Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Started);
    }

    [Fact]
    public async Task Unknown_launch_failure_joins_preparation_and_never_invents_retirement()
    {
        using var rig = new OwnedLabTestRig(); rig.Driver.UnknownLaunchFailure = true;
        var controller = rig.Controller(); await controller.InitializeAsync(); var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Terminal);
        Assert.Equal(1, rig.Driver.PreparationDisposals); await controller.StopAsync();
    }

    [Fact]
    public void Launch_gate_is_fail_closed_expiry_bound_and_consumed_even_when_resume_throws()
    {
        var clock = new TestClock(); var unbound = new OwnedLaunchGate(); Assert.False(unbound.TryResume(() => throw new Exception()));
        var gate = new OwnedLaunchGate(); gate.Bind(clock, TimeSpan.TicksPerSecond * 60);
        Assert.Throws<IOException>(() => gate.TryResume(() => throw new IOException()));
        Assert.False(gate.TryResume(() => throw new Exception()));
        var expired = new OwnedLaunchGate(); expired.Bind(clock, TimeSpan.TicksPerSecond * 60); clock.Advance(TimeSpan.FromSeconds(60));
        Assert.False(expired.TryResume(() => throw new Exception()));
        var revoked = new OwnedLaunchGate(); revoked.Bind(clock, TimeSpan.TicksPerSecond * 120); revoked.Revoke();
        Assert.False(revoked.TryResume(() => throw new Exception()));
    }

    [Fact]
    public async Task Seal_during_actual_preparation_forbids_launch_and_leave_keeps_external_seal()
    {
        using var rig = new OwnedLabTestRig(); var prep = new AsyncGate(); rig.Driver.PreparationGate = prep;
        var controller = rig.Controller(); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await prep.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var seal = controller.SealAdmission(); var leave = controller.PrepareLeaveAsync();
        Assert.False(leave.IsCompleted); Assert.True(controller.Snapshot.IsBusy); prep.Release();
        await Assert.ThrowsAsync<OwnedLabException>(() => run); await leave;
        Assert.Equal(0, rig.Driver.LaunchCount); Assert.Equal(1, rig.Driver.PreparationDisposals); Assert.True(controller.Snapshot.AdmissionClosed);
        await Assert.ThrowsAsync<OwnedLabException>(() => controller.RunAsync(OwnedScenario.Positive)); await controller.StopAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Seal_during_Queued_publication_never_launches_or_abandons_accepted_IO(bool after)
    {
        using var rig = new OwnedLabTestRig(); var gate = new AsyncGate();
        var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal) && Path.GetFileName(p) == "00000000.json", Before = after ? null : gate, After = after ? gate : null };
        var controller = rig.Controller(files); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var seal = controller.SealAdmission(); var drain = controller.CancelAndDrainAsync();
        Assert.False(drain.IsCompleted); Assert.True(controller.Snapshot.IsBusy); gate.Release();
        var result = await run; await drain; Assert.Equal(0, rig.Driver.LaunchCount); Assert.Equal(OwnedRunState.Cancelled, result.State);
        Assert.False(result.Events[^1].Retirement!.WasLaunched); Assert.True(controller.Snapshot.AdmissionClosed); await controller.StopAsync();
    }

    [Fact]
    public async Task Uncertain_Queued_never_launches_and_does_not_infer_marker_acknowledgment()
    {
        using var rig = new OwnedLabTestRig(); var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal), ThrowAfter = true };
        var controller = rig.Controller(files); await controller.InitializeAsync(); var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(0, rig.Driver.LaunchCount); Assert.Equal(-1, result.Sequence); Assert.Equal(OwnedRunState.RecoveryNeeded, result.State);
        Assert.True(File.Exists(Path.Combine(rig.Root, "attempts", result.Execution.RunId, "commits", "00000000.json"))); await controller.StopAsync();
    }

    [Fact]
    public async Task Queued_IO_consumes_monotonic_grant_lifetime_before_launch()
    {
        using var rig = new OwnedLabTestRig(); var gate = new AsyncGate(); var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal) && Path.GetFileName(p) == "00000000.json", Before = gate };
        var controller = rig.Controller(files); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); rig.Clock.Advance(TimeSpan.FromSeconds(60)); gate.Release();
        var result = await run; Assert.Equal(0, rig.Driver.LaunchCount); Assert.Equal("Expired", result.ErrorCode); Assert.Equal(OwnedRunState.Failed, result.State); await controller.StopAsync();
    }

    [Fact]
    public async Task Stop_joins_held_throwing_preparation_and_retains_root_lock()
    {
        using var rig = new OwnedLabTestRig(); var gate = new AsyncGate(); rig.Driver.PreparationGate = gate; rig.Driver.ThrowPreparation = true;
        var controller = rig.Controller(); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var stop = controller.StopAsync(); Assert.False(stop.IsCompleted);
        await Assert.ThrowsAsync<OwnedLabException>(() => OwnedJournal.OpenAsync(rig.Scope)); gate.Release();
        Assert.Equal("DriverFailure", (await Assert.ThrowsAsync<OwnedLabException>(() => run)).ErrorCode); await stop;
        await using var released = await OwnedJournal.OpenAsync(rig.Scope);
    }

    [Fact]
    public async Task Changed_callbacks_are_retained_outside_locks_even_if_they_throw()
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller(); var gate = new AsyncGate();
        controller.Changed += snapshot => { if (!snapshot.IsBusy) return; gate.Entered.TrySetResult(); gate.Released.Task.GetAwaiter().GetResult(); throw new InvalidOperationException("consumer"); };
        await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var stop = controller.StopAsync(); Assert.False(stop.IsCompleted);
        gate.Release(); try { await run; } catch (OwnedLabException) { } await stop;
        Assert.False(controller.Snapshot.IsBusy);
    }
}
