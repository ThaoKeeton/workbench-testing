using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class OwnedRunnerTests
{
    [Fact]
    public async Task Typed_close_failure_keeps_zero_exit_proof_and_four_results_but_never_passes()
    {
        using var rig = new OwnedLabTestRig(); var retirement = new AsyncGate();
        rig.Driver.RetirementGate = retirement;
        var receipt = new OwnedRetirement(true, 0, 0, 0);
        rig.Driver.RetirementFailure = new OwnedRetirementException("ProtocolViolation", receipt);
        var controller = rig.Controller(); await controller.InitializeAsync(); var actual = controller.RunAsync(OwnedScenario.Positive);
        try
        {
            await retirement.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(controller.Snapshot.IsBusy); Assert.False(actual.IsCompleted);
            retirement.Release(); var result = await actual;
            Assert.Equal(OwnedRunState.Failed, result.State); Assert.Equal("ProtocolViolation", result.ErrorCode);
            var steps = result.Events.Where(e => e.Kind == OwnedEventKind.StepResult).ToArray();
            Assert.Equal(4, steps.Length); Assert.All(steps, step => Assert.Equal(OwnedOutcome.Passed, step.Outcome));
            Assert.Equal(receipt, result.Events[^1].Retirement); Assert.Equal(OwnedOutcome.Failed, result.Events[^1].Outcome);
            Assert.False(result.Events[^1].UncertainStep);
            Assert.Equal(1, rig.Driver.RetireCount); Assert.Equal(1, rig.Driver.SessionDisposals); Assert.Equal(1, rig.Driver.PreparationDisposals);
            foreach (var step in steps) Assert.Equal(OwnedLabTestRig.Bmp(), await controller.ReadFrameAsync(result.Execution.RunId, step.Sequence));
            await controller.StopAsync(); await controller.StopAsync();
            Assert.Equal(1, rig.Driver.RetireCount); Assert.Equal(1, rig.Driver.SessionDisposals); Assert.Equal(1, rig.Driver.PreparationDisposals);
        }
        finally { retirement.Release(); await controller.StopAsync(); }
    }

    [Fact]
    public async Task Typed_retirement_failure_preserves_prior_assertion_and_observed_termination_exits()
    {
        using var rig = new OwnedLabTestRig(); var receipt = new OwnedRetirement(true, 0x20000001, 0x20000001, 0);
        rig.Driver.RetirementFailure = new OwnedRetirementException("ProtocolViolation", receipt);
        var controller = rig.Controller(); await controller.InitializeAsync();
        try
        {
            var result = await controller.RunAsync(OwnedScenario.AssertionFailure);
            Assert.Equal(OwnedRunState.Failed, result.State); Assert.Equal("AssertionFailed", result.ErrorCode);
            Assert.Equal(3, result.Events.Count(e => e.Kind == OwnedEventKind.StepResult)); Assert.Equal(receipt, result.Events[^1].Retirement);
        }
        finally { await controller.StopAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Typed_close_failure_preserves_owner_cancellation_or_expiry_observed_during_join(bool expire)
    {
        using var rig = new OwnedLabTestRig(); var retirement = new AsyncGate(); rig.Driver.RetirementGate = retirement;
        rig.Driver.RetirementFailure = new OwnedRetirementException("ProtocolViolation", new(true, 0, 0, 0));
        var controller = rig.Controller(); await controller.InitializeAsync(); var actual = controller.RunAsync(OwnedScenario.Positive);
        Task? drain = null;
        try
        {
            await retirement.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (expire) rig.Clock.Advance(TimeSpan.FromSeconds(60)); else drain = controller.CancelAndDrainAsync();
            Assert.False(actual.IsCompleted); Assert.True(controller.Snapshot.IsBusy);
            retirement.Release(); var result = await actual; if (drain is not null) await drain;
            Assert.Equal(expire ? OwnedRunState.Failed : OwnedRunState.Cancelled, result.State);
            Assert.Equal(expire ? "Expired" : "Cancelled", result.ErrorCode);
            Assert.Equal(new OwnedRetirement(true, 0, 0, 0), result.Events[^1].Retirement);
            Assert.Equal(4, result.Events.Count(e => e.Kind == OwnedEventKind.StepResult));
        }
        finally { retirement.Release(); await controller.StopAsync(); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Invalid_typed_retirement_receipt_retains_recovery_and_joins_cleanup(int mode)
    {
        using var rig = new OwnedLabTestRig();
        OwnedRetirement? receipt = mode switch { 0 => null, 1 => new(false, null, null, 0), 2 => new(true, 0, 0, 1), 3 => new(true, null, 0, 0), _ => new(true, 0, null, 0) };
        rig.Driver.RetirementFailure = new OwnedRetirementException("ProtocolViolation", receipt!);
        var controller = rig.Controller(); await controller.InitializeAsync();
        try
        {
            var result = await controller.RunAsync(OwnedScenario.Positive);
            Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Terminal);
            Assert.Equal(1, rig.Driver.SessionDisposals); Assert.Equal(1, rig.Driver.PreparationDisposals);
        }
        finally { await controller.StopAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Typed_retirement_proof_does_not_hide_unrelated_cleanup_failure(bool preparation)
    {
        using var rig = new OwnedLabTestRig();
        rig.Driver.RetirementFailure = new OwnedRetirementException("ProtocolViolation", new(true, 0, 0, 0));
        rig.Driver.ThrowSessionDisposal = !preparation; rig.Driver.ThrowPreparationDisposal = preparation;
        var controller = rig.Controller(); await controller.InitializeAsync();
        try
        {
            var result = await controller.RunAsync(OwnedScenario.Positive);
            Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Terminal);
            Assert.Equal(1, rig.Driver.SessionDisposals); Assert.Equal(1, rig.Driver.PreparationDisposals);
        }
        finally { await controller.StopAsync(); }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public async Task Delayed_timer_delivery_cannot_erase_command_expiry_or_discard_late_result(int elapsedSeconds)
    {
        using var rig = new OwnedLabTestRig(); var action = new AsyncGate();
        rig.Clock.DeliverTimerCallbacks = false; rig.Driver.ActionGate = action;
        var controller = rig.Controller(); await controller.InitializeAsync(); var running = controller.RunAsync(OwnedScenario.Positive);
        try
        {
            await action.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rig.Clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            Assert.False(running.IsCompleted); Assert.True(controller.Snapshot.IsBusy);
            action.Release(); var result = await running;
            Assert.Equal(OwnedRunState.Failed, result.State); Assert.Equal("Expired", result.ErrorCode);
            Assert.Equal(1, rig.Driver.ActionCalls); var observation = Assert.Single(result.Events, e => e.Kind == OwnedEventKind.StepResult);
            Assert.Equal(OwnedOutcome.Passed, observation.Outcome); Assert.Equal("Owned pilot task", observation.State!.Title);
            Assert.Equal(OwnedLabTestRig.Bmp(), await controller.ReadFrameAsync(result.Execution.RunId, observation.Sequence));
            Assert.Equal(OwnedOutcome.Failed, result.Events[^1].Outcome); Assert.Equal("Expired", result.Events[^1].ErrorCode);
            Assert.False(result.Events[^1].UncertainStep); Assert.Equal(new OwnedRetirement(true, 0, 0, 0), result.Events[^1].Retirement);
        }
        finally { action.Release(); await controller.StopAsync(); }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(21072)] // First calibration center: a reused buffer must not invalidate the already returned frame.
    public async Task Returned_BMP_is_frozen_before_joining_asynchronous_command_timer_disposal(int mutationOffset)
    {
        using var rig = new OwnedLabTestRig(); var disposal = new AsyncGate(); rig.Clock.FirstCommandDisposalGate = disposal;
        var controller = rig.Controller(); await controller.InitializeAsync(); var running = controller.RunAsync(OwnedScenario.Positive);
        var expected = OwnedLabTestRig.Bmp();
        var expectedSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(expected));
        try
        {
            await disposal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(running.IsCompleted); Assert.True(controller.Snapshot.IsBusy); Assert.Equal(1, rig.Driver.ActionCalls);
            Assert.NotNull(rig.Driver.FirstBmp); rig.Driver.FirstBmp[mutationOffset] = 0x7a;
            disposal.Release(); var result = await running;
            Assert.Equal(OwnedRunState.Passed, result.State); Assert.Equal(4, rig.Driver.ActionCalls);
            var observation = result.Events.First(e => e.Kind == OwnedEventKind.StepResult);
            var retained = await controller.ReadFrameAsync(result.Execution.RunId, observation.Sequence);
            Assert.Equal(expected, retained); Assert.Equal(expectedSha, observation.FrameSha);
            Assert.Equal(expectedSha, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(retained)));
        }
        finally { disposal.Release(); await controller.StopAsync(); }
    }

    [Theory]
    [InlineData(1, "DriverFailure")]
    [InlineData(2, "ProtocolViolation")]
    [InlineData(3, "CaptureFailure")]
    public async Task Uncommitted_command_failure_retains_uncertainty_and_never_reissues_action(int mode, string error)
    {
        using var rig = new OwnedLabTestRig(); rig.Driver.ObservationFault = mode;
        var controller = rig.Controller(); await controller.InitializeAsync(); var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.Failed, result.State); Assert.Equal(error, result.ErrorCode); Assert.True(result.Events[^1].UncertainStep);
        Assert.Equal(1, rig.Driver.ActionCalls); Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.StepResult);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "attempts", result.Execution.RunId, "frames")));
        Assert.Equal(new OwnedRetirement(true, 0, 0, 0), result.Events[^1].Retirement); await controller.StopAsync();
    }

    [Fact]
    public async Task Throwing_retirement_still_joins_session_and_preparation_without_false_terminal()
    {
        using var rig = new OwnedLabTestRig(); rig.Driver.ThrowRetirement = true;
        var controller = rig.Controller(); await controller.InitializeAsync(); var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.Equal(1, rig.Driver.SessionDisposals); Assert.Equal(1, rig.Driver.PreparationDisposals);
        Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Terminal); await controller.StopAsync();
    }

    [Fact]
    public async Task Cancellation_during_StepStarted_publication_retires_without_issuing_command()
    {
        using var rig = new OwnedLabTestRig(); var gate = new AsyncGate();
        var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal) && Path.GetFileName(p) == "00000002.json", Before = gate };
        var controller = rig.Controller(files); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var drain = controller.CancelAndDrainAsync(); gate.Release();
        var result = await run; await drain; Assert.Equal(0, rig.Driver.ActionCalls); Assert.Equal(OwnedRunState.Cancelled, result.State);
        Assert.True(result.Events[^1].UncertainStep); Assert.Equal(1, rig.Driver.RetireCount); await controller.StopAsync();
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Invalid_launch_receipt_never_skips_joined_preparation_or_claims_retirement(int mode)
    {
        using var rig = new OwnedLabTestRig(); rig.Driver.InvalidLaunchReceipt = mode;
        var controller = rig.Controller(); await controller.InitializeAsync();
        try
        {
            var result = await controller.RunAsync(OwnedScenario.Positive);
            Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Terminal);
            Assert.Equal(1, rig.Driver.PreparationDisposals);
        }
        finally { await controller.StopAsync(); }
    }

    [Fact]
    public async Task Retry_reverifies_original_files_before_preparing_current_helper()
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller(); await controller.InitializeAsync();
        var original = await controller.RunAsync(OwnedScenario.Positive);
        var path = Path.Combine(rig.Root, "attempts", original.Execution.RunId, "frames", "00000003.bmp");
        var bytes = await File.ReadAllBytesAsync(path); bytes[60] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal("RecoveryNeeded", (await Assert.ThrowsAsync<OwnedLabException>(() => controller.RetryAsync(original.Execution.RunId))).ErrorCode);
        Assert.Equal(1, rig.Driver.PrepareCount); Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); await controller.StopAsync();
    }

    [Fact]
    public async Task Clean_process_interruption_retries_from_step_one_without_modifying_parent()
    {
        using var rig = new OwnedLabTestRig(); var journal = await OwnedJournal.OpenAsync(rig.Scope);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued)); await journal.DisposeAsync();
        var parentPath = Path.Combine(rig.Root, "attempts", grant.Execution.RunId, "events", "00000000.json"); var bytes = await File.ReadAllBytesAsync(parentPath);
        var controller = rig.Controller(); await controller.InitializeAsync(); var retry = await controller.RetryAsync(grant.Execution.RunId);
        Assert.Equal(OwnedRunState.Passed, retry.State); Assert.Equal(4, rig.Driver.ActionCalls); Assert.Equal(grant.Execution.RunId, retry.Execution.ParentRunId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(parentPath)); await controller.StopAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_at_result_marker_keeps_received_result_and_issues_no_next_action(bool after)
    {
        using var rig = new OwnedLabTestRig(); var gate = new AsyncGate();
        var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal) && Path.GetFileName(p) == "00000003.json", Before = after ? null : gate, After = after ? gate : null };
        var controller = rig.Controller(files); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var drain = controller.CancelAndDrainAsync(); Assert.False(drain.IsCompleted); gate.Release();
        var result = await run; await drain; Assert.Equal(OwnedRunState.Cancelled, result.State); Assert.Equal(1, rig.Driver.ActionCalls);
        Assert.Single(result.Events, e => e.Kind == OwnedEventKind.StepResult); Assert.False(result.Events[^1].UncertainStep); await controller.StopAsync();
    }

    [Theory]
    [InlineData(OwnedScenario.Positive, OwnedRunState.Passed, 4)]
    [InlineData(OwnedScenario.EmptyTitle, OwnedRunState.Passed, 4)]
    [InlineData(OwnedScenario.AssertionFailure, OwnedRunState.Failed, 3)]
    public async Task Fixed_scenarios_retain_actual_observations_and_retire_before_terminal(OwnedScenario scenario, OwnedRunState state, int results)
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller(); await controller.InitializeAsync();
        var result = await controller.RunAsync(scenario);
        Assert.Equal(state, result.State); Assert.Equal(results, rig.Driver.ActionCalls);
        var observations = result.Events.Where(e => e.Kind == OwnedEventKind.StepResult).ToArray(); Assert.Equal(results, observations.Length);
        Assert.All(observations, e => { Assert.Equal("OwnedWindowPrintWindow", e.CaptureKind); Assert.Equal(true, e.SyntheticTarget); Assert.NotNull(e.State); Assert.NotNull(e.Identity); });
        Assert.Equal(new OwnedRetirement(true, 0, 0, 0), result.Events[^1].Retirement);
        Assert.Equal(1, rig.Driver.RetireCount); Assert.Equal(1, rig.Driver.SessionDisposals); Assert.Equal(1, rig.Driver.PreparationDisposals);
        Assert.Equal(OwnedLabTestRig.Bmp(), await controller.ReadFrameAsync(result.Execution.RunId, observations[0].Sequence));
        if (state == OwnedRunState.Failed) { Assert.Equal("AssertionFailed", result.ErrorCode); Assert.Equal(OwnedOutcome.Failed, observations[^1].Outcome); }
        await controller.StopAsync();
        var restored = rig.Controller(); await restored.InitializeAsync();
        Assert.Equal(state, (await restored.SelectAsync(result.Execution.RunId)).State); Assert.Equal(1, rig.Driver.LaunchCount);
        await restored.StopAsync();
    }

    [Fact]
    public async Task Cancellation_retains_late_valid_result_and_waits_actual_retirement_and_callbacks()
    {
        using var rig = new OwnedLabTestRig(); var action = new AsyncGate(); var retirement = new AsyncGate(); var callback = new AsyncGate();
        rig.Driver.ActionGate = action; rig.Driver.RetirementGate = retirement; rig.Driver.CallbackGate = callback;
        var controller = rig.Controller(); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await action.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var stop = controller.StopAsync();
        await callback.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(stop.IsCompleted); Assert.True(controller.Snapshot.IsBusy);
        action.Release(); await retirement.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(run.IsCompleted); Assert.False(stop.IsCompleted);
        retirement.Release(); Assert.False(stop.IsCompleted); callback.Release();
        var result = await run; await stop;
        Assert.Equal(OwnedRunState.Cancelled, result.State); Assert.Single(result.Events, e => e.Kind == OwnedEventKind.StepResult);
        Assert.False(result.Events[^1].UncertainStep); Assert.Equal(1, rig.Driver.ActionCalls);
        Assert.False(controller.Snapshot.IsBusy);
    }

    [Fact]
    public async Task Retry_copies_original_input_from_step_one_and_uses_current_helper()
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller(); await controller.InitializeAsync();
        var original = await controller.RunAsync(OwnedScenario.AssertionFailure);
        rig.Driver.CurrentHelper = rig.Driver.CurrentHelper with { ImageSha = new('4', 64) };
        var retry = await controller.RetryAsync(original.Execution.RunId);
        Assert.Equal(original.Input.Bytes.ToArray(), retry.Input.Bytes.ToArray()); Assert.NotEqual(original.Execution.RunId, retry.Execution.RunId);
        Assert.NotEqual(original.Execution.SessionId, retry.Execution.SessionId); Assert.Equal(original.Execution.RunId, retry.Execution.ParentRunId);
        Assert.Equal(original.Input.Sha256, retry.Execution.ParentInputSha); Assert.Equal(new string('4', 64), retry.Execution.Helper.ImageSha);
        Assert.Equal(6, rig.Driver.ActionCalls); await controller.StopAsync();
    }

    [Fact]
    public async Task Command_deadline_revokes_authority_but_keeps_busy_until_command_returns()
    {
        using var rig = new OwnedLabTestRig(); var action = new AsyncGate(); rig.Driver.ActionGate = action;
        var controller = rig.Controller(); await controller.InitializeAsync(); var run = controller.RunAsync(OwnedScenario.Positive);
        await action.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); rig.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(run.IsCompleted); Assert.True(controller.Snapshot.IsBusy); action.Release();
        var result = await run; Assert.Equal(OwnedRunState.Failed, result.State); Assert.Equal("Expired", result.ErrorCode);
        Assert.Equal(1, rig.Driver.ActionCalls); Assert.Single(result.Events, e => e.Kind == OwnedEventKind.StepResult);
        await controller.StopAsync();
    }

    [Fact]
    public async Task Retirement_without_zero_job_proof_never_publishes_a_success_terminal()
    {
        using var rig = new OwnedLabTestRig(); rig.Driver.Retirement = new(true, 0, null, 1);
        var controller = rig.Controller(); await controller.InitializeAsync(); var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.DoesNotContain(result.Events, e => e.Kind == OwnedEventKind.Terminal);
        await controller.StopAsync();
    }
}
