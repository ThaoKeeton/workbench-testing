using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed class TestingControllerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_registered_supervised_runner_completes_positive_and_empty_title_cases_with_anchored_observations_and_receipts()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();
        var definition=Assert.Single(fixture.Tasks.GetSnapshots());
        Assert.True(definition.RequiresPluginStart);Assert.False(definition.ResumeOnStartup);
        Assert.False((await fixture.Tasks.StartFromHostAsync(TestingController.BackgroundTaskId,CancellationToken.None)).Succeeded);
        Assert.False((await fixture.Tasks.SetResumeOnStartupAsync(TestingController.BackgroundTaskId,true)).Succeeded);
        var set=await fixture.ImportAndGenerateAsync();
        await fixture.Controller.RunAsync(set.Cases.Take(2).Select(c=>c.Id).ToArray());
        var finished=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
        Assert.Equal(TestRunState.Completed,finished.State);
        Assert.Equal(8,finished.Events.Count(x=>x.Kind==TestRunEventKind.StepFinished));
        var reopened=await new ProjectTestStore(fixture.Disk.User,"default").ReadRunAsync(finished.Plan.Id);
        Assert.Equal(finished.HeadHash,reopened.HeadHash);Assert.Equal(finished.Sequence,reopened.Sequence);
        foreach(var result in reopened.Events.Where(x=>x.Kind==TestRunEventKind.StepFinished))
        {
            Assert.Equal(TestStepOutcome.Passed,result.Outcome);Assert.Contains("SIMULATED",result.NativePlainTextState!,StringComparison.Ordinal);
            var receipt=Assert.Single(result.EvidenceIds);
            var evidence=await fixture.Controller.ReadEvidenceAsync(finished.Plan.Id,receipt);
            Assert.True(evidence.Receipt.IsSimulated);Assert.Equal(172854,evidence.Bytes.Length);
            Assert.True(File.Exists(Path.Combine(StoreRoot(fixture.Disk.User),"runs",finished.Plan.Id,"evidence",receipt,"receipt.json")));
            Assert.Equal(TestRunEventKind.StepStarted,reopened.Events[(int)result.Sequence-1].Kind);
        }
        Assert.Contains("Create a task 🧪\r\n\t多语言",reopened.Events.First(x=>x.Kind==TestRunEventKind.StepFinished).NativePlainTextState!,StringComparison.Ordinal);
        var retainedUser=Path.Combine(Path.GetTempPath(),"workbench-testing-task4-20261107","completed-"+finished.Plan.Id);
        CopySyntheticStore(StoreRoot(fixture.Disk.User),StoreRoot(retainedUser));
        var copied=await new ProjectTestStore(retainedUser,"default").ReadRunAsync(finished.Plan.Id);
        Assert.Equal(finished.HeadHash,copied.HeadHash);
        output.WriteLine($"Retained actual synthetic store user root: {retainedUser}");
        output.WriteLine($"Actual run {finished.Plan.Id}: {finished.State}, sequence {finished.Sequence}, head {finished.HeadHash}, 8 durable simulated BMP receipts.");
    }

    [Fact]
    public async Task Manual_selection_refuses_and_deliberate_assertion_failure_uses_actual_failed_result_and_stops_the_plan()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        Assert.Equal("ManualStepUnsupported",(await Assert.ThrowsAsync<PluginTestingException>(()=>fixture.Controller.QueueRunAsync([set.Cases[3].Id]))).ErrorCode);
        await fixture.Controller.RunAsync([set.Cases[2].Id]);
        var run=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
        Assert.Equal(TestRunState.Failed,run.State);
        var failed=Assert.Single(run.Events,x=>x.Outcome==TestStepOutcome.Failed);
        Assert.Equal("AssertionFailed",failed.ErrorCode);Assert.Contains("Toast: Saved",failed.NativePlainTextState!,StringComparison.Ordinal);
        Assert.Equal(3,run.Events.Count(x=>x.Kind==TestRunEventKind.StepFinished));
    }

    [Fact]
    public async Task Committed_StepStarted_precedes_real_action_and_cancel_joins_action_and_disposal_before_terminal_then_retry_uses_frozen_cases()
    {
        var adapter=new GatedVirtualAdapter();
        var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var queued=await fixture.Controller.QueueRunAsync([set.Cases[0].Id]);
            await fixture.Controller.StartQueuedRunAsync();await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var inFlight=await fixture.Context.Testing.ReadRunAsync(queued.Plan.Id);
            Assert.Equal(TestRunEventKind.StepStarted,inFlight.Events[^1].Kind);Assert.Equal(set.Cases[0].Steps[0].Id,inFlight.Events[^1].StepId);
            var requested=ObserveCancellation(fixture.Controller);var cancel=fixture.Controller.CancelAsync();
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cancel.IsCompleted);Assert.Equal(TestRunState.CancelRequested,(await fixture.Context.Testing.ReadRunAsync(queued.Plan.Id)).State);
            adapter.ActionGate.Release.TrySetResult();await adapter.DisposeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cancel.IsCompleted);Assert.Equal(0,adapter.Disposals);
            var disposing=await fixture.Context.Testing.ReadRunAsync(queued.Plan.Id);
            Assert.Equal(TestRunState.CancelRequested,disposing.State);Assert.Equal(TestRunEventKind.StepFinished,disposing.Events[^1].Kind);
            var evidenceId=Assert.Single(disposing.Events[^1].EvidenceIds);
            Assert.Equal(172854,(await fixture.Context.Testing.ReadRunEvidenceAsync(queued.Plan.Id,evidenceId)).Bytes.Length);
            adapter.DisposeGate.Release.TrySetResult();var cancelled=Assert.IsType<TestRunSnapshot>(await cancel);
            Assert.Equal(1,adapter.Disposals);Assert.Equal(TestRunState.Cancelled,cancelled.State);Assert.Equal(1,adapter.Actions);
            fixture.Controller.CaptureStepText(set.Cases[0].Id,set.Cases[0].Steps[0].Id,"Edited to manual after original attempt",set.Cases[0].Steps[0].Expected);
            await fixture.Controller.FlushAsync();
            var retry=await fixture.Controller.RetryAsync(queued.Plan.Id);
            var retried=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
            Assert.Equal(TestRunState.Completed,retried.State);Assert.NotEqual(cancelled.Plan.SessionId,retry.Plan.SessionId);
            Assert.Equal(cancelled.Plan.Id,retry.Plan.ParentRunId);Assert.Equal(cancelled.Plan.CaseRevision,retry.Plan.CaseRevision);
            Assert.Equal(set.Cases[0].Steps[0].Instruction,retry.Plan.Cases[0].Steps[0].Instruction);
            Assert.Equal(TestRunState.Cancelled,(await fixture.Context.Testing.ReadRunAsync(cancelled.Plan.Id)).State);
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Success_waits_for_actual_disposal_and_full_actual_returned_state_survives_reopen_instead_of_reconstructing_planned_input()
    {
        var adapter=new GatedVirtualAdapter { ObservedOverride="Observed adapter 🧪\r\n\t"+new string('界',8170) };
        adapter.ActionGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            Assert.Equal(8192,adapter.ObservedOverride.Length);
            var set=await fixture.ImportAndGenerateAsync();var queued=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await adapter.DisposeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var prior=await fixture.Context.Testing.ReadRunAsync(queued.Plan.Id);
            Assert.Equal(TestRunState.Running,prior.State);Assert.Equal(4,prior.Events.Count(e=>e.Kind==TestRunEventKind.StepFinished));
            adapter.DisposeGate.Release.TrySetResult();
            var settled=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
            Assert.Equal(TestRunState.Completed,settled.State);
            var reopened=await new ProjectTestStore(fixture.Disk.User,"default").ReadRunAsync(settled.Plan.Id);
            Assert.All(reopened.Events.Where(e=>e.Kind==TestRunEventKind.StepFinished),e=>Assert.Equal(adapter.ObservedOverride,e.NativePlainTextState));
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Failed_step_remains_Failed_when_cancel_arrives_during_disposal()
    {
        var adapter=new GatedVirtualAdapter(3);adapter.ActionGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var run=await fixture.Controller.RunAsync([set.Cases[2].Id]);
            await adapter.DisposeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TestStepOutcome.Failed,(await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).Events[^1].Outcome);
            var cancel=fixture.Controller.CancelAsync();Assert.False(cancel.IsCompleted);
            adapter.DisposeGate.Release.TrySetResult();
            Assert.Equal(TestRunState.Failed,Assert.IsType<TestRunSnapshot>(await cancel).State);
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Host_start_success_before_controller_delegate_entry_remains_owned_until_the_delayed_actual_delegate_joins()
    {
        var prefix=new AsyncTestGate();var adapter=new GatedVirtualAdapter();adapter.ReleaseAll();
        var fixture=await TestingDomainFixture.CreateAsync(adapter,delegatePrefix:prefix);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var run=await fixture.Controller.QueueRunAsync([set.Cases[0].Id]);
            await fixture.Controller.StartQueuedRunAsync();await prefix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var requested=ObserveCancellation(fixture.Controller);var cancel=fixture.Controller.CancelAsync();await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(cancel.IsCompleted);Assert.Equal(0,adapter.Actions);
            Assert.Equal(TestRunState.CancelRequested,(await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).State);
            prefix.Release.TrySetResult();
            Assert.Equal(TestRunState.Cancelled,Assert.IsType<TestRunSnapshot>(await cancel).State);Assert.Equal(0,adapter.Actions);
        }
        finally { prefix.Release.TrySetResult();adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Close_while_Start_is_pending_retains_real_host_persistence_and_does_not_publish_fresh_closed_service_checkpoint()
    {
        var persist=new AsyncTestGate();var fixture=await TestingDomainFixture.CreateAsync(startPersist:persist);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var queued=await fixture.Controller.QueueRunAsync([set.Cases[0].Id]);
            var start=fixture.Controller.StartQueuedRunAsync();await persist.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Context.Close(PluginStopReason.PluginDisabled);var lifetime=fixture.Context.CancelLifetimeAsync();var stop=fixture.Controller.StopAsync();
            Assert.False(stop.IsCompleted);persist.Release.TrySetResult();
            await start;await Task.WhenAll(lifetime,stop);
            var durable=await new ProjectTestStore(fixture.Disk.User,"default").ReadRunAsync(queued.Plan.Id);
            Assert.Equal(TestRunState.Queued,durable.State);Assert.Single(durable.Events);
            Assert.True(fixture.Controller.Snapshot.IsStopped);
            Assert.Equal("TestingContextClosed",(await Assert.ThrowsAsync<PluginTestingException>(()=>fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements))).ErrorCode);
        }
        finally { persist.Release.TrySetResult();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Failed_actual_host_Start_keeps_Queued_with_safe_error_and_no_adapter_action()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        var queued=await fixture.Controller.QueueRunAsync([set.Cases[0].Id]);
        TestRunSnapshot result;using(var obstruction=TestingAuthoringTests.WriteObstruction.Create(fixture.SettingsPath)) result=await fixture.Controller.StartQueuedRunAsync();
        Assert.Equal(TestRunState.Queued,result.State);Assert.Equal("TestingFailed",fixture.Controller.Snapshot.ErrorCode);
        Assert.Single((await fixture.Context.Testing.ReadRunAsync(queued.Plan.Id)).Events);
        await fixture.Controller.CancelAsync();
    }

    [Fact]
    public async Task Before_leave_flushes_final_input_and_temporarily_seals_actions_then_same_page_remains_usable()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        using var held=PluginTestingContextTests.HoldLease(fixture.Disk.User);
        fixture.Controller.CaptureSetName("Final input immediately before leave 🧪");
        var prepare=fixture.Controller.PrepareLeaveAsync();
        Assert.Equal("StoreBusy",Assert.Throws<PluginTestingException>(()=>fixture.Controller.CaptureSetName("Rejected during prepare")).ErrorCode);
        Assert.False(prepare.IsCompleted);held.Dispose();await prepare;
        Assert.Equal("Final input immediately before leave 🧪",(await fixture.Context.Testing.ReadCaseSetAsync(set.Id)).Name);
        fixture.Controller.CaptureSetName("Same page usable after another participant might fail");await fixture.Controller.FlushAsync();
        Assert.Equal("Same page usable after another participant might fail",fixture.Controller.Snapshot.Draft!.Content.Name);
    }

    [Fact]
    public async Task Real_draft_IO_failure_blocks_leave_export_run_and_selection_without_dropping_dirty_model()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        using(var held=TestingAuthoringTests.WriteObstruction.Create(CasePath(fixture.Disk.User,set.Id)))
        {
            fixture.Controller.CaptureSetName("Unsaved exact 🧪");
            foreach(var action in new Func<Task>[] { ()=>fixture.Controller.PrepareLeaveAsync(),()=>fixture.Controller.ExportCsvAsync(),()=>fixture.Controller.QueueRunAsync([set.Cases[0].Id]),()=>fixture.Controller.SelectCaseSetAsync(set.Id) })
                Assert.Equal("StoreIoFailure",(await Assert.ThrowsAsync<PluginTestingException>(action)).ErrorCode);
            Assert.Equal("Unsaved exact 🧪",fixture.Controller.Snapshot.Draft!.Content.Name);Assert.True(fixture.Controller.Snapshot.Draft.IsDirty);
        }
        fixture.Controller.CaptureSetName("Corrected accepted text");await fixture.Controller.FlushAsync();await fixture.Controller.PrepareLeaveAsync();
    }

    public static IEnumerable<object[]> StepStartedWriteFailureBoundaries()
    {
        yield return [true]; // Deny head publication after the event on both real filesystems.
        if(!OperatingSystem.IsWindows()) yield return [false]; // Also retain the existing chmod event-directory boundary.
    }

    [Theory]
    [MemberData(nameof(StepStartedWriteFailureBoundaries))]
    public async Task Actual_StepStarted_write_failure_never_calls_adapter_and_failed_terminal_write_does_not_fabricate_settlement(bool failHeadPublication)
    {
        var adapter=new GatedVirtualAdapter { HoldReset=true };adapter.ActionGate.Release.TrySetResult();adapter.DisposeGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var run=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await adapter.ResetGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var runRoot=Path.Combine(StoreRoot(fixture.Disk.User),"runs",run.Plan.Id);
            var evidenceRoot=Path.Combine(Path.GetTempPath(),"workbench-testing-windows-integration-fix1-20261107",(failHeadPublication ? "head-" : "event-")+run.Plan.Id);
            var before=CaptureRunPublicationFailure(runRoot,evidenceRoot,"before");
            var obstructionPath=failHeadPublication ? Path.Combine(runRoot,"head.json") : Path.Combine(runRoot,"events","0000000002.json");
            Dictionary<string,byte[]> failed;
            using(var obstruction=TestingAuthoringTests.WriteObstruction.Create(obstructionPath))
            {
                adapter.ResetGate.Release.TrySetResult();var actual=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
                failed=CaptureRunPublicationFailure(runRoot,evidenceRoot,"failed");
                File.WriteAllText(Path.Combine(evidenceRoot,"diagnostics.json"),JsonSerializer.Serialize(new
                { runId=run.Plan.Id,failHeadPublication,os=OperatingSystem.IsWindows() ? "Windows" : "Linux",actual.State,actual.Sequence,actual.HeadHash,errorCode=fixture.Controller.Snapshot.ErrorCode,adapter.Actions }));
                Assert.Equal(before["head.json"],failed["head.json"]);
                Assert.Equal(new[]{TestRunEventKind.Queued,TestRunEventKind.RunStarted},actual.Events.Select(e=>e.Kind));
                Assert.Equal(1,actual.Sequence);
                using var head=JsonDocument.Parse(failed["head.json"]);
                Assert.Equal(1,head.RootElement.GetProperty("sequence").GetInt64());Assert.Equal(actual.HeadHash,head.RootElement.GetProperty("eventHash").GetString());
                Assert.Equal(failHeadPublication ? 5 : 4,failed.Count);
                Assert.Equal(failHeadPublication ? 3 : 2,failed.Keys.Count(p=>p.StartsWith("events/",StringComparison.Ordinal)));
                if(failHeadPublication)
                {
                    using var tail=JsonDocument.Parse(failed["events/0000000002.json"]);
                    Assert.Equal("StepStarted",tail.RootElement.GetProperty("kind").GetString());Assert.Equal(2,tail.RootElement.GetProperty("sequence").GetInt64());
                    Assert.Equal(actual.HeadHash,tail.RootElement.GetProperty("priorHash").GetString());
                }
                foreach(var entry in before) Assert.Equal(entry.Value,failed[entry.Key]);
                // Once the immutable event exists, the terminal checkpoint must refuse its unanchored tail.
                Assert.Equal(0,adapter.Actions);Assert.Equal(TestRunState.Running,actual.State);
                Assert.Equal(failHeadPublication ? "RecoveryRequired" : "StoreIoFailure",fixture.Controller.Snapshot.ErrorCode);
            }
            if(failHeadPublication)
            {
                Assert.Equal("RecoveryRequired",(await Assert.ThrowsAsync<PluginTestingException>(()=>fixture.Context.Testing.ReadRunAsync(run.Plan.Id))).ErrorCode);
                var retained=CaptureRunPublicationFailure(runRoot,evidenceRoot,"after-refusal");
                Assert.Equal(failed.Keys.Order(),retained.Keys.Order());foreach(var entry in failed) Assert.Equal(entry.Value,retained[entry.Key]);
            }
            else
            {
                var actual=await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);Assert.Equal(TestRunEventKind.RunStarted,actual.Events[^1].Kind);
                await fixture.Controller.PrepareLeaveAsync();Assert.Equal(TestRunState.Failed,(await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).State);
                CaptureRunPublicationFailure(runRoot,evidenceRoot,"after-settlement");Assert.Equal(0,adapter.Actions);
            }
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }
    [Fact]
    public async Task Preparation_requests_cancellation_before_joining_an_admitted_wait_for_run_completion()
    {
        var adapter=new GatedVirtualAdapter();adapter.DisposeGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiting=fixture.Controller.WaitForRunSettlementAsync();
            var requested=ObserveCancellation(fixture.Controller);var prepare=fixture.Controller.PrepareLeaveAsync();
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(prepare.IsCompleted);Assert.False(waiting.IsCompleted);
            adapter.ActionGate.Release.TrySetResult();await Task.WhenAll(prepare,waiting);
            Assert.Equal(TestRunState.Interrupted,fixture.Controller.Snapshot.SelectedRun!.State);
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Autosave_completion_and_failure_publish_plain_snapshots_without_an_extra_user_operation()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        var saved=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.Changed+=snapshot=>
        {
            if(snapshot.Draft is { IsDirty:false,Content.Name:"Autosaved" }) saved.TrySetResult();
            if(snapshot.Draft?.ErrorCode=="StoreIoFailure") failed.TrySetResult();
        };
        fixture.Controller.CaptureSetName("Autosaved");await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using(var obstruction=TestingAuthoringTests.WriteObstruction.Create(CasePath(fixture.Disk.User,set.Id)))
        {
            fixture.Controller.CaptureSetName("Retained failed text");await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Retained failed text",fixture.Controller.Snapshot.Draft!.Content.Name);
        }
        fixture.Controller.CaptureSetName("Corrected after failure");await fixture.Controller.FlushAsync();
    }

    [Fact]
    public async Task Closed_admission_precedes_even_the_bundled_source_prefix()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();await fixture.Controller.StopAsync();
        var error=await Assert.ThrowsAsync<PluginTestingException>(()=>fixture.Controller.ImportExampleAsync((TestSourceKind)999));
        Assert.Equal("TestingContextClosed",error.ErrorCode);
    }

    [Fact]
    public async Task Selection_seals_new_text_capture_while_flushing_and_loading_then_accepts_edits_on_the_same_model_afterward()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        var busy=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.Changed+=snapshot=> { if(snapshot.IsAuthoringBusy) busy.TrySetResult(); };
        using var held=PluginTestingContextTests.HoldLease(fixture.Disk.User);
        var selecting=fixture.Controller.SelectCaseSetAsync(set.Id);
        try
        {
            await busy.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("StoreBusy",Assert.Throws<PluginTestingException>(()=>fixture.Controller.CaptureSetName("Refused while selecting")).ErrorCode);
            held.Dispose();await selecting;
            fixture.Controller.CaptureSetName("Accepted after selection");await fixture.Controller.FlushAsync();
            Assert.Equal("Accepted after selection",fixture.Controller.Snapshot.Draft!.Content.Name);
        }
        finally { held.Dispose();await selecting; }
    }

    [Fact]
    public async Task Unexpected_context_close_joins_real_action_and_disposal_without_checkpoint_then_new_context_recovers_and_retries_from_step_one()
    {
        var adapter=new GatedVirtualAdapter();var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var original=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Context.Close(PluginStopReason.PluginDisabled);var lifetime=fixture.Context.CancelLifetimeAsync();var stopped=fixture.Controller.StopAsync();
            Assert.False(stopped.IsCompleted);adapter.ActionGate.Release.TrySetResult();await adapter.DisposeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopped.IsCompleted);adapter.DisposeGate.Release.TrySetResult();
            await Task.WhenAll(stopped,lifetime,fixture.Tasks.StopAllAsync(),fixture.Context.DrainProjectWorkAsync());
            Assert.Equal(1,adapter.Actions);Assert.Equal(1,adapter.Disposals);
            var uncertain=await new ProjectTestStore(fixture.Disk.User,"default").ReadRunAsync(original.Plan.Id);
            Assert.Equal(TestRunState.Running,uncertain.State);Assert.Equal(TestRunEventKind.StepStarted,uncertain.Events[^1].Kind);
            Assert.DoesNotContain(uncertain.Events,e=>e.Kind is TestRunEventKind.CancelRequested or TestRunEventKind.StepFinished or TestRunEventKind.Interrupted);
            using var fresh=PluginTestingContextTests.Registration(new("default","Default",fixture.Disk.User));var next=new TestingController(fresh);
            try
            {
                await next.InitializeAsync();fresh.RegisterBeforeProjectLeave(next.PrepareLeaveAsync);fresh.Commit();
                var recovered=await fresh.Testing.ReadRunAsync(original.Plan.Id);Assert.Equal(TestRunState.Interrupted,recovered.State);
                var retry=await next.RetryAsync(original.Plan.Id);var completed=Assert.IsType<TestRunSnapshot>(await next.WaitForRunSettlementAsync());
                Assert.Equal(TestRunState.Completed,completed.State);Assert.Equal(original.Plan.Id,retry.Plan.ParentRunId);
                Assert.NotEqual(original.Plan.SessionId,retry.Plan.SessionId);Assert.Equal(original.Plan.CaseRevision,retry.Plan.CaseRevision);
                Assert.Equal(4,completed.Events.Count(e=>e.Kind==TestRunEventKind.StepFinished));
            }
            finally { fresh.Close(PluginStopReason.PluginDisabled);await Task.WhenAll(next.StopAsync(),fresh.CancelLifetimeAsync(),fresh.BackgroundTasks.StopAllAsync(),fresh.DrainProjectWorkAsync()); }
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Pending_real_supervisor_stop_keeps_retirement_owned_until_the_actual_delegate_postfix_settles()
    {
        var postfix=new AsyncTestGate();var fixture=await TestingDomainFixture.CreateAsync(delegatePostfix:postfix);var admitted=new List<Task>();
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var runA=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            await postfix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waitingA=fixture.Controller.WaitForRunSettlementAsync();admitted.Add(waitingA);
            var pending=await fixture.FirstStopReturned.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.Succeeded);Assert.Equal(PluginBackgroundTaskState.Stopping,Assert.Single(fixture.Tasks.GetSnapshots()).State);
            var nextBusy=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Controller.Changed+=snapshot=> { if(snapshot.IsAuthoringBusy) nextBusy.TrySetResult(); };
            var startingB=fixture.Controller.RunAsync([set.Cases[0].Id]);admitted.Add(startingB);
            await Task.WhenAny(startingB,nextBusy.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(waitingA.IsCompleted);Assert.False(startingB.IsCompleted);Assert.True(fixture.Controller.Snapshot.OwnsRun);
            Assert.Single(await fixture.Context.Testing.ListRunsAsync());
            postfix.Release.TrySetResult();
            Assert.Equal(runA.Plan.Id,Assert.IsType<TestRunSnapshot>(await waitingA).Plan.Id);
            var runB=await startingB;
            var settledB=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
            Assert.Equal(runB.Plan.Id,settledB.Plan.Id);Assert.Equal(TestRunState.Completed,settledB.State);
        }
        finally
        {
            postfix.Release.TrySetResult();_=await Record.ExceptionAsync(()=>Task.WhenAll(admitted));await fixture.DisposeAsync();
        }
    }

    private Dictionary<string,byte[]> CaptureRunPublicationFailure(string runRoot,string evidenceRoot,string phase)
    {
        var files=Directory.EnumerateFiles(runRoot,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(runRoot,p).Replace('\\','/'),File.ReadAllBytes);
        foreach(var (path,bytes) in files.OrderBy(p=>p.Key,StringComparer.Ordinal))
        {
            var destination=Path.Combine(evidenceRoot,phase,path);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllBytes(destination,bytes);
            output.WriteLine($"{phase} {path} {bytes.Length} bytes SHA256={Convert.ToHexStringLower(SHA256.HashData(bytes))}");
            if(path=="head.json" || path.StartsWith("events/",StringComparison.Ordinal)) output.WriteLine("exact UTF8 bytes (base64): "+Convert.ToBase64String(bytes));
        }
        output.WriteLine("Actual publication evidence: "+evidenceRoot);return files;
    }

    private static void CopySyntheticStore(string source,string destination)
    {
        Directory.CreateDirectory(destination);
        foreach(var file in Directory.EnumerateFiles(source)) File.Copy(file,Path.Combine(destination,Path.GetFileName(file)));
        foreach(var directory in Directory.EnumerateDirectories(source)) CopySyntheticStore(directory,Path.Combine(destination,Path.GetFileName(directory)));
    }

    private static TaskCompletionSource ObserveCancellation(TestingController controller)
    {
        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed+=snapshot=> { if(snapshot.SelectedRun?.State==TestRunState.CancelRequested) requested.TrySetResult(); };
        return requested;
    }

}
