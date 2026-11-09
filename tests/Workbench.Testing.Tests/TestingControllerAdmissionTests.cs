using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Xunit;
using Xunit.Abstractions;
using static Workbench.Core.Tests.PluginTestingContextTests;

namespace Workbench.Core.Tests;

public sealed class TestingControllerAdmissionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("queued")]
    [InlineData("before-definition")]
    [InlineData("running")]
    [InlineData("disposal")]
    [InlineData("cancel")]
    [InlineData("failed-start")]
    public async Task CurrentAttemptRefusal_preserves_actual_live_journal_error_selection_and_accepted_edits(string stage)
    {
        var prefix=new AsyncTestGate();var adapter=new GatedVirtualAdapter();
        var fixture=await TestingDomainFixture.CreateAsync(adapter,delegatePrefix:stage=="before-definition" ? prefix : null);
        Task<TestRunSnapshot?>? cancel=null;
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var run=await fixture.Controller.QueueRunAsync([set.Cases[0].Id]);
            if(stage=="failed-start")
            {
                using var obstruction=TestingAuthoringTests.WriteObstruction.Create(fixture.SettingsPath);
                await fixture.Controller.StartQueuedRunAsync();
            }
            else if(stage!="queued")
            {
                await fixture.Controller.StartQueuedRunAsync();
                if(stage=="before-definition") await prefix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                else
                {
                    await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if(stage=="disposal") { adapter.ActionGate.Release.TrySetResult();await adapter.DisposeGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                    if(stage=="cancel")
                    {
                        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        fixture.Controller.Changed+=snapshot=> { if(snapshot.SelectedRun?.State==TestRunState.CancelRequested) requested.TrySetResult(); };
                        cancel=fixture.Controller.CancelAsync();await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
            if(stage!="failed-start")
                Assert.Equal("InvalidId",(await Assert.ThrowsAsync<PluginTestingException>(()=>fixture.Controller.GenerateAsync([]))).ErrorCode);
            var before=fixture.Controller.Snapshot;var committed=await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.False(await fixture.Controller.TryJoinPriorTerminalRunAsync());
            var after=fixture.Controller.Snapshot;var unchanged=await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.True(after.OwnsRun);Assert.Equal(before.ErrorCode,after.ErrorCode);Assert.Equal(before.SelectedRun?.Plan.Id,after.SelectedRun?.Plan.Id);
            Assert.Equal(committed.HeadHash,unchanged.HeadHash);Assert.Equal(committed.Sequence,unchanged.Sequence);
            fixture.Controller.CaptureSetName("Correction during owned virtual work");await fixture.Controller.FlushAsync();
            Assert.Equal("Correction during owned virtual work",(await fixture.Context.Testing.ReadCaseSetAsync(set.Id)).Name);
            Assert.Equal(run.Plan.Id,(await fixture.Controller.SelectRunAsync(run.Plan.Id)).Plan.Id);
        }
        finally
        {
            prefix.Release.TrySetResult();adapter.ReleaseAll();
            if(cancel is not null) _=await Record.ExceptionAsync(()=>cancel);
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task CurrentAttemptRefusal_ignores_selected_terminal_history_and_released_join_is_idempotent()
    {
        var adapter=new GatedVirtualAdapter(5);adapter.DisposeGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter);
        try
        {
            Assert.True(await fixture.Controller.TryJoinPriorTerminalRunAsync());
            var set=await fixture.ImportAndGenerateAsync();var first=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            Assert.Equal(TestRunState.Completed,Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync()).State);
            Assert.True(await fixture.Controller.TryJoinPriorTerminalRunAsync());
            Assert.True(await fixture.Controller.TryJoinPriorTerminalRunAsync());
            var second=await fixture.Controller.RunAsync([set.Cases[0].Id]);await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Controller.SelectRunAsync(first.Plan.Id);
            Assert.False(await fixture.Controller.TryJoinPriorTerminalRunAsync());
            Assert.Equal(first.Plan.Id,fixture.Controller.Snapshot.SelectedRun!.Plan.Id);
            Assert.Equal(TestRunState.Running,(await fixture.Context.Testing.ReadRunAsync(second.Plan.Id)).State);
            adapter.ActionGate.Release.TrySetResult();
            Assert.Equal(second.Plan.Id,Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync()).Plan.Id);
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Actual_definition_callback_completion_excludes_held_action_and_disposal_and_does_not_settle_supervisor_postfix()
    {
        var adapter=new GatedVirtualAdapter();var postfix=new AsyncTestGate();var stopPrefix=new AsyncTestGate();
        var fixture=await TestingDomainFixture.CreateAsync(adapter,firstStopPrefix:stopPrefix,delegatePostfix:postfix);var admitted=new List<Task>();
        try
        {
            var definitionCompletion=fixture.FirstDefinitionCallbackCompletion;
            var set=await fixture.ImportAndGenerateAsync();var run=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            var waiting=fixture.Controller.WaitForRunSettlementAsync();admitted.Add(waiting);
            await adapter.ActionGate.Entered.Task;
            Assert.False(definitionCompletion.IsCompleted);Assert.False(postfix.Entered.Task.IsCompleted);Assert.False(stopPrefix.Entered.Task.IsCompleted);
            Assert.False(waiting.IsCompleted);Assert.Equal(0,fixture.RetirementStopCalls);
            Assert.Equal(1,adapter.Actions);Assert.Equal(0,adapter.Disposals);
            var active=await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.Equal(TestRunState.Running,active.State);Assert.Equal(TestRunEventKind.StepStarted,active.Events[^1].Kind);
            Assert.Single(await fixture.Context.Testing.ListRunsAsync());
            adapter.ActionGate.Release.TrySetResult();
            await adapter.DisposeGate.Entered.Task;
            Assert.False(definitionCompletion.IsCompleted);Assert.False(postfix.Entered.Task.IsCompleted);Assert.False(stopPrefix.Entered.Task.IsCompleted);
            Assert.False(waiting.IsCompleted);Assert.Equal(0,fixture.RetirementStopCalls);
            Assert.Equal(4,adapter.Actions);Assert.Equal(0,adapter.Disposals);
            var disposing=await fixture.Context.Testing.ReadRunAsync(run.Plan.Id);
            Assert.Equal(TestRunState.Running,disposing.State);
            Assert.Equal(4,disposing.Events.Count(e=>e.Kind==TestRunEventKind.StepFinished && e.Outcome==TestStepOutcome.Passed));
            adapter.DisposeGate.Release.TrySetResult();
            await definitionCompletion;
            await postfix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopPrefix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(waiting.IsCompleted);Assert.Equal(1,fixture.RetirementStopCalls);Assert.True(fixture.Controller.Snapshot.OwnsRun);
            Assert.Equal(TestRunState.Completed,(await fixture.Context.Testing.ReadRunAsync(run.Plan.Id)).State);
            stopPrefix.Release.TrySetResult();
            Assert.False((await fixture.FirstStopReturned.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
            Assert.Equal(PluginBackgroundTaskState.Stopping,Assert.Single(fixture.Tasks.GetSnapshots()).State);
            Assert.False(waiting.IsCompleted);Assert.True(fixture.Controller.Snapshot.OwnsRun);
            Assert.Single(await fixture.Context.Testing.ListRunsAsync());
            postfix.Release.TrySetResult();
            var settled=Assert.IsType<TestRunSnapshot>(await waiting);
            Assert.Equal(run.Plan.Id,settled.Plan.Id);Assert.Equal(TestRunState.Completed,settled.State);
            Assert.Equal(4,settled.Events.Count(e=>e.Kind==TestRunEventKind.StepFinished && e.Outcome==TestStepOutcome.Passed));
            Assert.DoesNotContain(settled.Events,e=>e.Kind is TestRunEventKind.CancelRequested or TestRunEventKind.Cancelled);
            Assert.Equal(1,adapter.Disposals);Assert.Null(fixture.Controller.Snapshot.ErrorCode);
        }
        finally
        {
            adapter.ReleaseAll();postfix.Release.TrySetResult();stopPrefix.Release.TrySetResult();
            _=await Record.ExceptionAsync(()=>Task.WhenAll(admitted));await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Old_waiter_and_next_run_share_actual_retirement_before_the_shared_task_can_start_the_next_session()
    {
        var definitionPrefix=new AsyncTestGate();var stopPrefix=new AsyncTestGate();var adapter=new GatedVirtualAdapter(5);adapter.DisposeGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter,delegatePrefix:definitionPrefix,firstStopPrefix:stopPrefix);var admitted=new List<Task>();
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var runA=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            var waitingA=fixture.Controller.WaitForRunSettlementAsync();admitted.Add(waitingA);
            Assert.False(fixture.FirstDefinitionCallbackCompletion.IsCompleted);Assert.False(waitingA.IsCompleted);
            definitionPrefix.Release.TrySetResult();
            // Real producer IO is setup; the unchanged coordination fuse starts after the actual definition returns.
            await fixture.FirstDefinitionCallbackCompletion;
            try { await stopPrefix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch(TimeoutException)
            {
                try
                {
                    var snapshot=fixture.Controller.Snapshot;var tasks=fixture.Tasks.GetSnapshots();
                    var taskState=tasks.Count==1 ? tasks[0].State.ToString() : "unexpected-count";
                    output.WriteLine($"coordination=first-retirement-stop-after-definition;definition={fixture.FirstDefinitionCallbackCompletion.Status};waiter={waitingA.Status};run={snapshot.SelectedRun?.State};ownsRun={snapshot.OwnsRun};closed={snapshot.IsStopped};hasError={snapshot.ErrorCode is not null};taskCount={tasks.Count};task={taskState};stopCalls={fixture.RetirementStopCalls};actions={Volatile.Read(ref adapter.Actions)};disposals={Volatile.Read(ref adapter.Disposals)}");
                }
                catch { } // Read-only failure context must preserve the original timeout and cleanup.
                throw;
            }
            Assert.Equal(TestRunState.Completed,(await fixture.Context.Testing.ReadRunAsync(runA.Plan.Id)).State);
            var nextBusy=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Controller.Changed+=snapshot=> { if(snapshot.IsAuthoringBusy) nextBusy.TrySetResult(); };
            var startingB=fixture.Controller.RunAsync([set.Cases[0].Id]);admitted.Add(startingB);
            await Task.WhenAny(startingB,nextBusy.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(waitingA.IsCompleted);Assert.False(startingB.IsCompleted);
            stopPrefix.Release.TrySetResult();
            var runB=await startingB.WaitAsync(TimeSpan.FromSeconds(5));
            await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(runA.Plan.Id,Assert.IsType<TestRunSnapshot>(await waitingA).Plan.Id);
            Assert.Equal(1,fixture.RetirementStopCalls);
            var activeB=await fixture.Context.Testing.ReadRunAsync(runB.Plan.Id);
            Assert.Equal(TestRunState.Running,activeB.State);Assert.Equal(TestRunEventKind.StepStarted,activeB.Events[^1].Kind);
            Assert.Null(fixture.Controller.Snapshot.ErrorCode);
            adapter.ActionGate.Release.TrySetResult();
            var settledB=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
            Assert.Equal(runB.Plan.Id,settledB.Plan.Id);Assert.Equal(TestRunState.Completed,settledB.State);
            Assert.Equal(4,settledB.Events.Count(e=>e.Kind==TestRunEventKind.StepFinished));
            Assert.All(settledB.Events.Where(e=>e.Kind==TestRunEventKind.StepFinished),e=>Assert.Equal(TestStepOutcome.Passed,e.Outcome));
            Assert.DoesNotContain(settledB.Events,e=>e.Kind is TestRunEventKind.CancelRequested or TestRunEventKind.Cancelled);
            Assert.Equal(2,fixture.RetirementStopCalls);
        }
        finally
        {
            definitionPrefix.Release.TrySetResult();stopPrefix.Release.TrySetResult();adapter.ReleaseAll();
            _=await Record.ExceptionAsync(()=>Task.WhenAll(admitted));await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Released_attempt_wait_and_cancel_tails_are_idempotent_before_a_new_actual_session_completes()
    {
        var stopPrefix=new AsyncTestGate();stopPrefix.Release.TrySetResult();
        var adapter=new GatedVirtualAdapter(5);adapter.DisposeGate.Release.TrySetResult();
        var fixture=await TestingDomainFixture.CreateAsync(adapter,firstStopPrefix:stopPrefix);
        try
        {
            var set=await fixture.ImportAndGenerateAsync();var runA=await fixture.Controller.RunAsync([set.Cases[0].Id]);
            Assert.Equal(TestRunState.Completed,Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync()).State);
            var again=await Task.WhenAll(fixture.Controller.WaitForRunSettlementAsync(),fixture.Controller.WaitForRunSettlementAsync(),fixture.Controller.CancelAsync());
            Assert.All(again,r=> { Assert.Equal(runA.Plan.Id,Assert.IsType<TestRunSnapshot>(r).Plan.Id);Assert.Equal(TestRunState.Completed,r.State); });
            Assert.Equal(1,fixture.RetirementStopCalls);Assert.False(fixture.Controller.Snapshot.OwnsRun);
            var runB=await fixture.Controller.RunAsync([set.Cases[0].Id]);await adapter.ActionGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1,fixture.RetirementStopCalls);
            adapter.ActionGate.Release.TrySetResult();
            var settled=Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
            Assert.Equal(runB.Plan.Id,settled.Plan.Id);Assert.Equal(TestRunState.Completed,settled.State);
            Assert.Equal(2,fixture.RetirementStopCalls);
        }
        finally { adapter.ReleaseAll();await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_awaits_real_service_barrier_before_registering_and_close_prevents_late_contribution(bool close)
    {
        using var disk=new TestDirectory();var store=new ProjectTestStore(disk.User,"default");await Seed(store);
        using var context=Registration(new ProjectRuntimeScope("default","Default",disk.User));
        var controller=new TestingController(context);using var lease=HoldLease(disk.User);
        var initializing=controller.InitializeAsync();
        Assert.Empty(context.BackgroundTasks.GetSnapshots());Assert.False(initializing.IsCompleted);
        try
        {
            Task? stopping=null;
            if(close) { context.Close(PluginStopReason.PluginDisabled);stopping=controller.StopAsync();Assert.False(stopping.IsCompleted); }
            lease.Dispose();
            if(close)
            {
                Assert.Equal("TestingContextClosed",(await Assert.ThrowsAsync<PluginTestingException>(()=>initializing)).ErrorCode);
                await stopping!;Assert.Empty(context.BackgroundTasks.GetSnapshots());
            }
            else
            {
                await initializing;Assert.Single(context.BackgroundTasks.GetSnapshots());
                context.RegisterBeforeProjectLeave(controller.PrepareLeaveAsync);context.Commit();
                Assert.True((await context.PrepareProjectLeaveAsync(CancellationToken.None)).Succeeded);
            }
        }
        finally { lease.Dispose();context.Close(PluginStopReason.PluginDisabled);await Task.WhenAll(controller.StopAsync(),context.DrainProjectWorkAsync(),context.BackgroundTasks.StopAllAsync()); }
    }

    [Fact]
    public async Task Held_real_caller_capture_is_published_before_prefix_and_terminal_stop_joins_it_without_enumerating_fresh_closed_calls()
    {
        using var workers=new CallbackWorkerHeadroom();await using var fixture=await TestingDomainFixture.CreateAsync();var set=await fixture.ImportAndGenerateAsync();
        var source=Assert.Single(await fixture.Context.Testing.ListSourcesAsync());
        using var held=new HeldList<string>([source.Id]);
        Task<TestCaseSetSnapshot>? generating=null;
        try
        {
            generating=Task.Run(()=>fixture.Controller.GenerateAsync(held));await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Context.Close(PluginStopReason.PluginDisabled);var stopping=fixture.Controller.StopAsync();
            Assert.False(stopping.IsCompleted);Assert.False(generating.IsCompleted);
            held.Release.TrySetResult();
            Assert.Equal("TestingContextClosed",(await Assert.ThrowsAsync<PluginTestingException>(()=>generating)).ErrorCode);
            await stopping;Assert.Equal(set.Id,fixture.Controller.Snapshot.Draft!.Id);
        }
        finally { held.Release.TrySetResult();if(generating is not null) _=await Record.ExceptionAsync(()=>generating); }
    }

    [Fact]
    public async Task Terminal_close_joins_retained_plain_notification_callback_and_never_invokes_it_under_controller_lock()
    {
        using var workers=new CallbackWorkerHeadroom();await using var fixture=await TestingDomainFixture.CreateAsync();await fixture.ImportAndGenerateAsync();
        using var released=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.Changed+=snapshot=>
        {
            if(snapshot.Draft?.Content.Name!="Notification held" || !entered.TrySetResult()) return;
            _=fixture.Controller.Snapshot; // Reentrant plain read cannot require the publishing controller lock.
            released.Wait();
        };
        try
        {
            fixture.Controller.CaptureSetName("Notification held");await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop=fixture.Controller.StopAsync();Assert.False(stop.IsCompleted);
            released.Set();await stop;
        }
        finally { released.Set(); }
    }
    [Fact]
    public async Task Pending_plain_notifications_coalesce_while_a_callback_is_held_and_retain_the_final_accepted_model()
    {
        using var workers=new CallbackWorkerHeadroom();await using var fixture=await TestingDomainFixture.CreateAsync();await fixture.ImportAndGenerateAsync();
        using var released=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var names=new System.Collections.Concurrent.ConcurrentQueue<string>();
        fixture.Controller.Changed+=snapshot=>
        {
            var name=snapshot.Draft?.Content.Name;if(name is null) return;names.Enqueue(name);
            if(name=="Held A" && entered.TrySetResult()) released.Wait();
        };
        try
        {
            fixture.Controller.CaptureSetName("Held A");await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Controller.CaptureSetName("Intermediate B");fixture.Controller.CaptureSetName("Final C");
            var preparation=fixture.Controller.PrepareLeaveAsync();released.Set();await preparation;
            Assert.DoesNotContain("Intermediate B",names);Assert.Contains("Final C",names);Assert.Equal("Final C",fixture.Controller.Snapshot.Draft!.Content.Name);
        }
        finally { released.Set(); }
    }

    [Fact]
    public async Task Preparation_releases_private_admission_only_after_its_final_plain_notification_has_settled()
    {
        await using var fixture=await TestingDomainFixture.CreateAsync();await fixture.ImportAndGenerateAsync();
        var seenPreparing=false;var attempted=false;PluginTestingException? refusal=null;
        var preparing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Controller.Changed+=snapshot=>
        {
            if(snapshot.IsPreparing) { seenPreparing=true;preparing.TrySetResult(); }
            else if(seenPreparing && !attempted)
            {
                attempted=true;
                try { fixture.Controller.CaptureSetName("Too early from completion notification"); }
                catch(PluginTestingException error) { refusal=error; }
            }
        };
        using var lease=HoldLease(fixture.Disk.User);
        fixture.Controller.CaptureSetName("Final input before held preparation");
        var preparation=fixture.Controller.PrepareLeaveAsync();
        try { await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { lease.Dispose(); }
        await preparation;
        Assert.True(attempted);Assert.NotNull(refusal);Assert.Equal("StoreBusy",refusal.ErrorCode);
        fixture.Controller.CaptureSetName("Accepted after settled hook");await fixture.Controller.FlushAsync();
    }

}
