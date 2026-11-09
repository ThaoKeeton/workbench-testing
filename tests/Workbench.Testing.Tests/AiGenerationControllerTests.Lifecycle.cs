using System.Collections;
using System.Net;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Xunit;
using static Workbench.Core.Tests.PluginTestingContextTests;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed partial class AiGenerationControllerTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Closed_admission_does_not_touch_selected_IDs_credentials_or_HTTP(bool closeContext)
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));await using var rig=await Rig.CreateAsync(handler);
        if(closeContext) rig.Context.Close(PluginStopReason.PluginDisabled);else await rig.Controller.StopAsync();
        var ids=new ForbiddenIds();var keys=0;
        await CodeAsync("TestingContextClosed",()=>rig.Controller.GenerateWithAiAsync(ids,_=> { keys++;return Key(default); }));
        Assert.Equal(0,ids.Accesses);Assert.Equal(0,keys);Assert.Equal(0,handler.Requests);Assert.Equal(0,rig.Bridge.Reads);
    }

    [Fact]
    public async Task Selected_IDs_are_captured_before_return_and_plain_edits_and_other_authoring_wait_for_the_actual_operation()
    {
        var gate=new AsyncTestGate();using var handler=new SyntheticHandler(async(_,_)=> { await gate.HoldAsync();return Response(Raw()); });
        await using var rig=await Rig.CreateAsync(handler);string[] ids=[rig.Source.Id];
        var generation=rig.Controller.GenerateWithAiAsync(ids,Key);Task<TestCaseSetSnapshot>? other=null;
        try
        {
            ids[0]=new string('f',32);await gate.Entered.Task.WaitAsync(Budget);
            other=rig.Controller.GenerateAsync([rig.Source.Id]);
            Assert.True(rig.Controller.Snapshot.IsAuthoringBusy);Assert.False(other.IsCompleted);
            Assert.Equal("StoreBusy",Assert.Throws<PluginTestingException>(()=>rig.Controller.CaptureSetName("Blocked")).ErrorCode);
            gate.Release.TrySetResult();var result=await generation.WaitAsync(Budget);
            Assert.Equal(rig.Source.Id,result.CaseSet!.Cases[0].Traces[0].SnapshotId);
            var next=await other.WaitAsync(Budget);Assert.Equal(next.Id,rig.Controller.Snapshot.Draft!.Id);Assert.Equal(1,handler.Requests);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>generation);if(other is not null) _=await Record.ExceptionAsync(()=>other); }
    }

    [Fact]
    public async Task Selected_draft_is_flushed_before_any_source_read_key_or_HTTP_and_a_flush_failure_preserves_dirty_selection()
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));await using var rig=await Rig.CreateAsync(handler);
        var selected=await rig.Controller.GenerateAsync([rig.Source.Id]);var reads=rig.Bridge.Reads;var writes=rig.Bridge.CaseWrites;
        rig.Controller.CaptureSetName(new string('x',201));await CodeAsync("StoreLimitExceeded",()=>rig.Controller.FlushAsync());
        await CodeAsync("StoreLimitExceeded",()=>rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key));
        Assert.Equal(reads,rig.Bridge.Reads);Assert.Equal(writes,rig.Bridge.CaseWrites);Assert.Equal(0,handler.Requests);
        Assert.Equal(selected.Id,rig.Controller.Snapshot.Draft!.Id);Assert.True(rig.Controller.Snapshot.Draft.IsDirty);
        using var lease=HoldLease(rig.Disk.User);rig.Controller.CaptureSetName("Saved before sending");
        var keys=0;var generation=rig.Controller.GenerateWithAiAsync([rig.Source.Id],async ct=>
        {
            keys++;Assert.False(rig.Controller.Snapshot.Draft!.IsDirty);
            Assert.Equal("Saved before sending",(await rig.Context.Testing.ReadCaseSetAsync(selected.Id)).Name);return await Key(ct);
        });
        try
        {
            Assert.False(generation.IsCompleted);Assert.Equal(0,keys);Assert.Equal(reads,rig.Bridge.Reads);Assert.Equal(0,handler.Requests);
            lease.Dispose();var result=await generation.WaitAsync(Budget);
            Assert.Equal(result.CaseSet!.Id,rig.Controller.Snapshot.Draft!.Id);Assert.Equal(1,keys);
        }
        finally { lease.Dispose();_=await Record.ExceptionAsync(()=>generation); }
    }

    [Theory]
    [InlineData("credential",false)] [InlineData("transport",false)] [InlineData("body",false)] [InlineData("read",false)]
    [InlineData("credential",true)] [InlineData("transport",true)] [InlineData("body",true)] [InlineData("read",true)]
    public async Task Ignored_cancellation_retains_actual_read_key_HTTP_or_body_until_preparation_Stop_and_context_drain_can_settle(string stage,bool close)
    {
        var gate=new AsyncTestGate();CancellationToken observed=default;
        using var caller=new CancellationTokenSource();using var leave=new CancellationTokenSource();
        using var handler=new SyntheticHandler(async(_,ct)=>
        {
            if(stage=="transport") { observed=ct;await gate.HoldAsync(); }
            return stage=="body" ? ContentResponse(new StreamContent(new HeldBody(Raw(),gate,ct=>observed=ct))) : Response(Raw());
        });
        await using var rig=await Rig.CreateAsync(handler);
        if(stage=="read") rig.Bridge.ReadPrefix=async ct=> { observed=ct;await gate.HoldAsync(); };
        async Task<string?> Credential(CancellationToken ct)
        {
            if(stage=="credential") { observed=ct;await gate.HoldAsync(); }
            return await Key(ct);
        }
        var generation=rig.Controller.GenerateWithAiAsync([rig.Source.Id],Credential,caller.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);
            var preparation=rig.Context.PrepareProjectLeaveAsync(leave.Token);await UntilAsync(()=>rig.Controller.Snapshot.IsPreparing);leave.Cancel();
            if(close) { rig.Context.Close(PluginStopReason.PluginDisabled);await rig.Context.CancelLifetimeAsync(); }
            else caller.Cancel();
            var stopping=rig.Controller.StopAsync();var drain=rig.Context.DrainProjectWorkAsync();
            Assert.True(observed.IsCancellationRequested);Assert.False(generation.IsCompleted);Assert.False(preparation.IsCompleted);Assert.False(stopping.IsCompleted);Assert.False(drain.IsCompleted);
            Assert.True(rig.Context.HasPendingProjectWork);Assert.True(rig.Controller.Snapshot.IsAuthoringBusy);
            gate.Release.TrySetResult();
            if(stage=="read" && close) await CodeAsync("TestingContextClosed",()=>generation);
            else
            {
                var error=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>generation);
                Assert.Null(error.InnerException);Assert.DoesNotContain(Secret,error.ToString(),StringComparison.Ordinal);
            }
            Assert.False((await preparation.WaitAsync(Budget)).Succeeded);await stopping.WaitAsync(Budget);await drain.WaitAsync(Budget);
            Assert.Single(await new ProjectTestStore(rig.Disk.User,"default").ListSourcesAsync());Assert.Equal(0,rig.Bridge.SourceWrites);Assert.Equal(0,rig.Bridge.CaseWrites);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>generation); }
    }

    [Fact]
    public async Task Cancelled_leave_still_joins_remote_work_and_both_publications_then_reopens_admission()
    {
        var gate=new AsyncTestGate();using var leave=new CancellationTokenSource();
        using var handler=new SyntheticHandler(async(_,_)=> { await gate.HoldAsync();return Response(Raw()); });
        await using var rig=await Rig.CreateAsync(handler);var generation=rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);var preparation=rig.Context.PrepareProjectLeaveAsync(leave.Token);
            await UntilAsync(()=>rig.Controller.Snapshot.IsPreparing);leave.Cancel();var ids=new ForbiddenIds();
            await CodeAsync("StoreBusy",()=>rig.Controller.GenerateWithAiAsync(ids,Key));Assert.Equal(0,ids.Accesses);Assert.False(preparation.IsCompleted);
            gate.Release.TrySetResult();var result=await generation.WaitAsync(Budget);
            Assert.Equal(AiGenerationPublicationState.Created,result.State);Assert.False((await preparation.WaitAsync(Budget)).Succeeded);
            Assert.False(rig.Controller.Snapshot.IsPreparing);await rig.Controller.RefreshAsync();Assert.Single(rig.Controller.Snapshot.CaseSets);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>generation); }
    }

    [Fact]
    public async Task Standalone_Stop_winning_before_the_receipt_ticket_refuses_publication_even_with_an_open_Core_context()
    {
        var gate=new AsyncTestGate();using var handler=new SyntheticHandler(async(_,_)=> { await gate.HoldAsync();return Response(Raw()); });
        await using var rig=await Rig.CreateAsync(handler);var generation=rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);var stopping=rig.Controller.StopAsync();Assert.False(stopping.IsCompleted);
            gate.Release.TrySetResult();await CodeAsync("TestingContextClosed",()=>generation);await stopping.WaitAsync(Budget);
            Assert.Equal(PluginStopReason.None,rig.Context.StopReason);Assert.Equal(0,rig.Bridge.SourceWrites);Assert.Single(await rig.Context.Testing.ListSourcesAsync());
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>generation); }
    }

    [Theory]
    [InlineData("receipt",false)] [InlineData("receipt",true)]
    [InlineData("case",false)] [InlineData("case",true)]
    public async Task An_admitted_publication_calls_the_actual_facade_outside_the_controller_lock_and_Stop_joins_its_actual_settlement(string stage,bool closeContext)
    {
        using var workers=new CallbackWorkerHeadroom();var gate=new AsyncTestGate();using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));
        await using var rig=await Rig.CreateAsync(handler);
        Action prefix=()=>gate.HoldAsync().GetAwaiter().GetResult();
        if(stage=="receipt") rig.Bridge.SourcePrefix=prefix;else rig.Bridge.CasePrefix=prefix;
        var generation=rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);
            if(closeContext) rig.Context.Close(PluginStopReason.PluginDisabled);
            var stopping=rig.Controller.StopAsync();Assert.False(stopping.IsCompleted);Assert.True(rig.Controller.Snapshot.IsStopped);
            gate.Release.TrySetResult();
            if(stage=="receipt" && closeContext) await CodeAsync("TestingContextClosed",()=>generation);
            else
            {
                var result=await generation.WaitAsync(Budget);
                Assert.Equal(stage=="case" && !closeContext ? AiGenerationPublicationState.Created : AiGenerationPublicationState.Closed,result.State);
                if(result.State==AiGenerationPublicationState.Closed) { Assert.Null(result.CaseSet);Assert.Equal("TestingContextClosed",result.ErrorCode); }
            }
            await stopping.WaitAsync(Budget);
            var store=new ProjectTestStore(rig.Disk.User,"default");
            Assert.Equal(stage=="receipt" && closeContext ? 1 : 2,(await store.ListSourcesAsync()).Count);
            Assert.Equal(stage=="case" && !closeContext ? 1 : 0,(await store.ListCaseSetsAsync()).Count);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>generation); }
    }

    [Fact]
    public async Task Held_plain_notification_after_publication_remains_owned_by_preparation_and_Stop()
    {
        using var workers=new CallbackWorkerHeadroom();var gate=new AsyncTestGate();using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));
        await using var rig=await Rig.CreateAsync(handler);
        rig.Controller.Changed+=snapshot=> { if(snapshot.Draft?.Content.Name=="AI 测试用例（待人工审核）") gate.HoldAsync().GetAwaiter().GetResult(); };
        var result=await rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
        try
        {
            Assert.Equal(AiGenerationPublicationState.Created,result.State);await gate.Entered.Task.WaitAsync(Budget);
            var preparation=rig.Controller.PrepareLeaveAsync();var stopping=rig.Controller.StopAsync();
            Assert.False(preparation.IsCompleted);Assert.False(stopping.IsCompleted);gate.Release.TrySetResult();
            _=await Record.ExceptionAsync(()=>preparation);await stopping.WaitAsync(Budget);
        }
        finally { gate.Release.TrySetResult(); }
    }

    private sealed class ForbiddenIds : IReadOnlyList<string>
    {
        internal int Accesses;
        public int Count { get { Interlocked.Increment(ref Accesses);throw new InvalidOperationException(Private); } }
        public string this[int index] { get { Interlocked.Increment(ref Accesses);throw new InvalidOperationException(Private); } }
        public IEnumerator<string> GetEnumerator() { Interlocked.Increment(ref Accesses);throw new InvalidOperationException(Private); }
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class HeldBody(byte[] bytes,AsyncTestGate gate,Action<CancellationToken> observe) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        { observe(ct);await gate.HoldAsync();return await base.ReadAsync(buffer,CancellationToken.None); }
    }
}
