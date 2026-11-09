using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Sources.Figma;
using Xunit;
using static Workbench.Core.Tests.PluginTestingContextTests;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

[Collection(BlockingCallbackCollection.Name)]
public sealed class FigmaControllerTests
{
    private const string Secret="synthetic-Figma-token-never-persist";
    private const string Private="synthetic-provider-private-detail";
    private static readonly TimeSpan Budget=TimeSpan.FromSeconds(5);
    private static Task<string?> Token(CancellationToken _)=>Task.FromResult<string?>(Secret);
    private static FigmaReadRequest Request=>new("Ab12",["2:3","1:2"],"v1");
    private static Dictionary<string,IReadOnlyList<string>> Criteria()=>new(StringComparer.Ordinal)
    {
        ["2:3"]=["  User criterion 🧪  ","=literal,\"quoted\"\r\nline"],
        ["1:2"]=["Other explicit criterion"]
    };

    // Removing retained import, losing bytes/provenance, or treating observed nodes as actions must fail this composition.
    [Fact]
    public async Task Import_publishes_exact_versioned_bytes_and_manual_criteria_in_the_captured_real_project_only()
    {
        var raw=Json(["2:3","1:2"]);
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(raw)));
        await using var first=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        await using var second=await TestingDomainFixture.CreateAsync();
        Assert.NotEqual(first.Disk.User,second.Disk.User);
        var outside=OutsideProjectFiles(first.Disk.User);
        var saved=await first.Controller.ImportFigmaAsync(Request,Criteria(),Token);
        var read=await new ProjectTestStore(first.Disk.User,"default").ReadSourceAsync(saved.Id);
        Assert.Equal(raw,read.OriginalBytes);Assert.Equal(Hash(raw),read.OriginalSha256);
        Assert.Equal(Hash(await File.ReadAllBytesAsync(SourcePath(first.Disk.User,saved.Id))),read.Revision);
        Assert.Equal(saved.Revision,read.Revision);Assert.Equal("default",read.ProjectId);Assert.Equal(Owner,read.ProducerPluginId);
        Assert.Equal(TestSourceKind.Figma,read.Kind);Assert.Equal("figma.rest.nodes.manual-criteria",read.ProviderId);
        Assert.Equal("1",read.ProviderVersion);Assert.Equal("Ab12@v1",read.DocumentId);Assert.Equal("Observed file 🧪",read.Title);
        Assert.Equal(new[]{"2:3","1:2"},read.Items.Select(i=>i.Locator));
        Assert.Equal(new[]{"  User criterion 🧪  ","=literal,\"quoted\"\r\nline"},read.Items[0].AcceptanceCriteria);
        Assert.Equal("Observed type: TEXT\nObserved characters: Observed text",read.Items[0].Description);
        Assert.All(read.Items,item=>Assert.Null(item.VirtualScenario));
        var summary=Assert.Single(first.Controller.Snapshot.Sources);
        Assert.Equal(read.Id,summary.Id);Assert.Equal(read.Revision,summary.Revision);Assert.Equal(read.OriginalSha256,summary.OriginalSha256);
        Assert.Equal(2,summary.ItemCount);
        Assert.Empty(await second.Context.Testing.ListSourcesAsync());Assert.Empty(second.Controller.Snapshot.Sources);
        Assert.Equal(outside,OutsideProjectFiles(first.Disk.User));
        Assert.DoesNotContain(Secret,Encoding.UTF8.GetString(await File.ReadAllBytesAsync(SourcePath(first.Disk.User,saved.Id))),StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal(("GET","https://api.figma.com/v1/files/Ab12/nodes?ids=2%3A3%2C1%3A2&depth=2&version=v1",Secret),handler.Requests[0]);

        var set=await first.Controller.GenerateAsync([saved.Id]);Assert.Equal(3,set.Cases.Count);
        var expected=new[]{("2:3",0,"  User criterion 🧪  "),("2:3",1,"=literal,\"quoted\"\r\nline"),("1:2",0,"Other explicit criterion")};
        for(var i=0;i<expected.Length;i++)
        {
            var item=set.Cases[i];var step=Assert.Single(item.Steps);var trace=Assert.Single(item.Traces);
            Assert.Equal(new TestTrace(saved.Id,saved.Revision,expected[i].Item1,expected[i].Item2),trace);
            Assert.Equal(trace,Assert.Single(step.Traces));Assert.Null(step.Action);Assert.Equal(expected[i].Item3,step.Expected);
            Assert.Equal("Rule template: manual verification required.",item.Prerequisites);
        }
        var csv=await first.Controller.ExportCsvAsync();Assert.Equal(new byte[]{239,187,191},csv[..3]);
        var rows=TestingSourceTests.ParseCsv(Encoding.UTF8.GetString(csv[3..]));Assert.Equal(7,rows.Count);
        for(var i=0;i<expected.Length;i++)
        for(var scope=0;scope<2;scope++)
        {
            var row=rows[1+i*2+scope];
            Assert.Equal("'"+expected[i].Item3,row[6]);Assert.Equal("'"+saved.Id,row[7]);Assert.Equal("'"+saved.Revision,row[8]);
            Assert.Equal("'"+expected[i].Item1,row[9]);Assert.Equal(expected[i].Item2.ToString(),row[10]);
            Assert.Equal(scope==0 ? "'case" : "'step",row[11]);Assert.Equal("'Manual",row[13]);Assert.Equal("'",row[14]);
        }
        Assert.Equal(outside,OutsideProjectFiles(first.Disk.User));
    }

    [Fact]
    public async Task Caller_inputs_are_captured_once_before_the_credential_prefix_can_mutate_them()
    {
        string[] ids=["2:3","1:2"];string[] criteria=["Exact original criterion"];
        var nodes=new OnceList<string>(ids);var values=new OnceList<string>(criteria);
        var dictionary=new Dictionary<string,IReadOnlyList<string>>{["2:3"]=values,["1:2"]=["Second"]};
        var supplied=new OnceDictionary(dictionary);
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var saved=await fixture.Controller.ImportFigmaAsync(new("Ab12",nodes,"v1"),supplied,_=>
        {
            Assert.Equal(1,nodes.Enumerations);Assert.Equal(1,values.Enumerations);Assert.Equal(1,supplied.Enumerations);
            ids[0]="9:9";criteria[0]="Mutated";dictionary.Clear();return Token(default);
        });
        Assert.Equal(new[]{"2:3","1:2"},saved.Items.Select(i=>i.Locator));
        Assert.Equal("Exact original criterion",Assert.Single(saved.Items[0].AcceptanceCriteria));
        Assert.Equal(1,nodes.Enumerations);Assert.Equal(1,values.Enumerations);Assert.Equal(1,supplied.Enumerations);
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("criteria")]
    public async Task Synchronous_caller_enumeration_is_owned_before_prefix_and_Stop_and_preparation_join_actual_capture(string stage)
    {
        using var workers=new CallbackWorkerHeadroom();var gate=new AsyncTestGate();
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var nodes=new OnceList<string>(["2:3","1:2"],stage=="nodes" ? ()=>gate.HoldAsync().GetAwaiter().GetResult() : null);
        var criteria=Criteria();if(stage=="criteria") criteria["2:3"]=new OnceList<string>(["Criterion"],()=>gate.HoldAsync().GetAwaiter().GetResult());
        Task<TestSourceSnapshot>? import=null;Task? stopping=null;Task<ProjectPreparationResult>? preparation=null;
        try
        {
            import=Task.Run(()=>fixture.Controller.ImportFigmaAsync(new("Ab12",nodes,"v1"),criteria,Token));
            await gate.Entered.Task.WaitAsync(Budget);
            preparation=fixture.Context.PrepareProjectLeaveAsync(CancellationToken.None);
            await UntilAsync(()=>fixture.Controller.Snapshot.IsPreparing);
            stopping=fixture.Controller.StopAsync();var drain=fixture.Context.DrainProjectWorkAsync();
            Assert.False(import.IsCompleted);Assert.False(stopping.IsCompleted);Assert.False(preparation.IsCompleted);Assert.False(drain.IsCompleted);
            Assert.True(fixture.Context.HasPendingProjectWork);Assert.Empty(handler.Requests);
            gate.Release.TrySetResult();
            await CodeAsync("TestingContextClosed",()=>import);await stopping.WaitAsync(Budget);
            Assert.False((await preparation.WaitAsync(Budget)).Succeeded);await drain.WaitAsync(Budget);
            Assert.Empty(await fixture.Context.Testing.ListSourcesAsync());Assert.Empty(handler.Requests);
        }
        finally { gate.Release.TrySetResult();if(import is not null) _=await Record.ExceptionAsync(()=>import); }
    }

    [Theory]
    [InlineData("credential",false)] [InlineData("transport",false)]
    [InlineData("credential",true)] [InlineData("transport",true)]
    public async Task Ignored_cancellation_keeps_actual_read_preparation_Stop_and_context_drain_pending_until_release(string stage,bool close)
    {
        var gate=new AsyncTestGate();CancellationToken observed=default;
        using var caller=new CancellationTokenSource();using var leave=new CancellationTokenSource();
        using var handler=new SyntheticHandler(async(_,ct)=>
        {
            if(stage=="transport") { observed=ct;await gate.HoldAsync(); }
            return Response(Json(["2:3","1:2"]));
        });
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        Task<string?> Credential(CancellationToken ct)
        {
            if(stage!="credential") return Token(ct);
            observed=ct;return HeldTokenAsync();
        }
        async Task<string?> HeldTokenAsync() { await gate.HoldAsync();return Secret; }
        var import=fixture.Controller.ImportFigmaAsync(Request,Criteria(),Credential,caller.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);
            var preparation=fixture.Context.PrepareProjectLeaveAsync(leave.Token);
            await UntilAsync(()=>fixture.Controller.Snapshot.IsPreparing);leave.Cancel();
            Task? stopping=null;
            if(close)
            {
                fixture.Context.Close(PluginStopReason.PluginDisabled);await fixture.Context.CancelLifetimeAsync();
                stopping=fixture.Controller.StopAsync();
            }
            else caller.Cancel();
            var drain=fixture.Context.DrainProjectWorkAsync();
            Assert.True(observed.IsCancellationRequested);Assert.False(import.IsCompleted);Assert.False(preparation.IsCompleted);Assert.False(drain.IsCompleted);
            Assert.True(fixture.Context.HasPendingProjectWork);if(stopping is not null) Assert.False(stopping.IsCompleted);
            gate.Release.TrySetResult();
            var error=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>import);
            Assert.Null(error.InnerException);Assert.DoesNotContain(Secret,error.ToString(),StringComparison.Ordinal);
            Assert.False((await preparation.WaitAsync(Budget)).Succeeded);await drain.WaitAsync(Budget);
            if(stopping is not null) await stopping.WaitAsync(Budget);
            Assert.Empty(await new ProjectTestStore(fixture.Disk.User,"default").ListSourcesAsync());
            if(!close) Assert.Equal(2,(await fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token)).Items.Count);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>import); }
    }

    [Fact]
    public async Task Before_leave_waits_for_the_owned_read_and_its_publication_even_after_the_leave_token_is_cancelled()
    {
        var gate=new AsyncTestGate();using var leave=new CancellationTokenSource();
        using var handler=new SyntheticHandler(async(_,_)=> { await gate.HoldAsync();return Response(Json(["2:3","1:2"])); });
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var import=fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);
            var preparation=fixture.Context.PrepareProjectLeaveAsync(leave.Token);await UntilAsync(()=>fixture.Controller.Snapshot.IsPreparing);
            leave.Cancel();Assert.False(preparation.IsCompleted);Assert.False(import.IsCompleted);
            gate.Release.TrySetResult();var saved=await import.WaitAsync(Budget);
            Assert.False((await preparation.WaitAsync(Budget)).Succeeded);
            Assert.Equal(saved.Id,Assert.Single(await fixture.Context.Testing.ListSourcesAsync()).Id);
            Assert.False(fixture.Controller.Snapshot.IsPreparing);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>import); }
    }

    [Fact]
    public async Task A_held_import_serializes_the_actual_other_authoring_operation_and_seals_plain_edits()
    {
        var gate=new AsyncTestGate();
        using var handler=new SyntheticHandler(async(_,_)=> { await gate.HoldAsync();return Response(Json(["2:3","1:2"])); });
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var old=await fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements);
        var import=fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token);Task<TestCaseSetSnapshot>? generating=null;
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);generating=fixture.Controller.GenerateAsync([old.Id]);
            Assert.True(fixture.Controller.Snapshot.IsAuthoringBusy);Assert.False(generating.IsCompleted);
            Assert.Equal("StoreBusy",Assert.Throws<PluginTestingException>(()=>fixture.Controller.CaptureSetName("Not admitted")).ErrorCode);
            Assert.Empty(await fixture.Context.Testing.ListCaseSetsAsync());
            gate.Release.TrySetResult();await import.WaitAsync(Budget);
            var set=await generating.WaitAsync(Budget);Assert.Equal(set.Id,fixture.Controller.Snapshot.Draft!.Id);
        }
        finally
        {
            gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>import);
            if(generating is not null) _=await Record.ExceptionAsync(()=>generating);
        }
    }

    [Fact]
    public async Task Standalone_Stop_winning_before_publication_refuses_a_write_while_the_Core_context_is_still_open()
    {
        var gate=new AsyncTestGate();
        using var handler=new SyntheticHandler(async(_,_)=> { await gate.HoldAsync();return Response(Json(["2:3","1:2"])); });
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var import=fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);var stopping=fixture.Controller.StopAsync();
            Assert.Equal(PluginStopReason.None,fixture.Context.StopReason);Assert.False(stopping.IsCompleted);
            gate.Release.TrySetResult();await CodeAsync("TestingContextClosed",()=>import);await stopping.WaitAsync(Budget);
            Assert.Empty(await fixture.Context.Testing.ListSourcesAsync());Assert.Empty(fixture.Controller.Snapshot.Sources);
        }
        finally { gate.Release.TrySetResult();_=await Record.ExceptionAsync(()=>import); }
    }

    [Fact]
    public async Task Actual_admitted_store_publication_may_commit_after_standalone_Stop_and_Stop_drains_its_real_settlement()
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        await fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements); // Real publication creates this project's store/lease path.
        using var lease=HoldLease(fixture.Disk.User);
        var import=fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token);
        try
        {
            await UntilAsync(()=>fixture.Context.HasPendingProjectWork);var stopping=fixture.Controller.StopAsync();
            var drain=fixture.Context.DrainProjectWorkAsync();
            Assert.Equal(PluginStopReason.None,fixture.Context.StopReason);Assert.False(import.IsCompleted);Assert.False(stopping.IsCompleted);Assert.False(drain.IsCompleted);
            lease.Dispose();var saved=await import.WaitAsync(Budget);await stopping.WaitAsync(Budget);await drain.WaitAsync(Budget);
            Assert.Equal(saved.Id,Assert.Single(await fixture.Context.Testing.ListSourcesAsync(),source=>source.Kind==TestSourceKind.Figma).Id);
            Assert.Equal(saved.Revision,Assert.Single(fixture.Controller.Snapshot.Sources,source=>source.Id==saved.Id).Revision);
        }
        finally { lease.Dispose();_=await Record.ExceptionAsync(()=>import); }
    }

    [Fact]
    public async Task Publication_ticket_precedes_the_real_facade_prefix_and_does_not_hold_the_controller_lock()
    {
        using var workers=new CallbackWorkerHeadroom();using var disk=new TestDirectory();
        using var context=Registration(new ProjectRuntimeScope("default","Default",disk.User));
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        var bridge=new PublicationContext(context);var controller=new TestingController(bridge,null,new(handler));
        Task? stopping=null;Task<TestSourceSnapshot>? import=null;
        try
        {
            await InitializeAsync(context,controller);
            bridge.BeforePublish=()=>
            {
                // A different worker must acquire the same close barrier from inside the synchronous facade prefix.
                var close=Task.Run(()=> { stopping=controller.StopAsync(); });
                Assert.True(close.Wait(Budget));Assert.NotNull(stopping);Assert.False(stopping.IsCompleted);
            };
            import=controller.ImportFigmaAsync(Request,Criteria(),Token);var saved=await import.WaitAsync(Budget);
            await stopping!.WaitAsync(Budget);
            Assert.Equal(PluginStopReason.None,context.StopReason);
            Assert.Equal(saved.Id,Assert.Single(await context.Testing.ListSourcesAsync()).Id);
            Assert.Equal(saved.Id,Assert.Single(controller.Snapshot.Sources).Id);Assert.Equal(1,bridge.Publications);
        }
        finally { await SettleAsync(context,controller,import); }
    }

    [Fact]
    public async Task Core_Close_remains_the_final_admission_barrier_after_the_controller_publication_ticket()
    {
        using var disk=new TestDirectory();using var context=Registration(new ProjectRuntimeScope("default","Default",disk.User));
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        var bridge=new PublicationContext(context) { BeforePublish=()=>context.Close(PluginStopReason.PluginDisabled) };
        var controller=new TestingController(bridge,null,new(handler));
        try
        {
            await InitializeAsync(context,controller);
            await CodeAsync("TestingContextClosed",()=>controller.ImportFigmaAsync(Request,Criteria(),Token));
            Assert.Equal(1,bridge.Publications);Assert.Null(bridge.Acknowledged);
            Assert.Empty(await new ProjectTestStore(disk.User,"default").ListSourcesAsync());Assert.Empty(controller.Snapshot.Sources);
        }
        finally { await SettleAsync(context,controller,null); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Genuine_acknowledgment_is_returned_and_summarized_despite_late_cancellation_close_and_unreadable_refresh(bool closeContext)
    {
        using var disk=new TestDirectory();using var context=Registration(new ProjectRuntimeScope("default","Default",disk.User));
        using var caller=new CancellationTokenSource();var gate=new AsyncTestGate();
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        var bridge=new PublicationContext(context) { AfterPublish=_=>gate.HoldAsync() };
        var controller=new TestingController(bridge,null,new(handler));Task<TestSourceSnapshot>? import=null;
        var header=Path.Combine(StoreRoot(disk.User),"store.json");byte[]? originalHeader=null;
        try
        {
            await InitializeAsync(context,controller);import=controller.ImportFigmaAsync(Request,Criteria(),Token,caller.Token);
            await gate.Entered.Task.WaitAsync(Budget);var committed=Assert.IsType<TestSourceSnapshot>(bridge.Acknowledged);
            Assert.True(File.Exists(SourcePath(disk.User,committed.Id)));
            originalHeader=await File.ReadAllBytesAsync(header);await File.WriteAllTextAsync(header,Private);
            await CodeAsync("TestStoreCorrupt",()=>new ProjectTestStore(disk.User,"default").ListSourcesAsync());
            caller.Cancel();
            if(closeContext) { context.Close(PluginStopReason.PluginDisabled);await context.CancelLifetimeAsync(); }
            var stopping=controller.StopAsync();Assert.False(stopping.IsCompleted);
            gate.Release.TrySetResult();var result=await import.WaitAsync(Budget);await stopping.WaitAsync(Budget);
            Assert.Equal(committed.Id,result.Id);Assert.Equal(committed.Revision,result.Revision);
            Assert.Equal(committed.OriginalBytes,result.OriginalBytes);
            var summary=Assert.Single(controller.Snapshot.Sources);Assert.Equal(committed.Id,summary.Id);Assert.Equal(committed.Revision,summary.Revision);
            Assert.Null(controller.Snapshot.ErrorCode);Assert.Equal(1,bridge.Publications);
        }
        finally
        {
            gate.Release.TrySetResult();if(originalHeader is not null) await File.WriteAllBytesAsync(header,originalHeader);
            await SettleAsync(context,controller,import);
        }
    }

    [Fact]
    public async Task Store_failure_after_actual_commit_preserves_the_bytes_without_retry_deletion_or_claiming_absence()
    {
        using var disk=new TestDirectory();using var context=Registration(new ProjectRuntimeScope("default","Default",disk.User));
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(["2:3","1:2"]))));
        var bridge=new PublicationContext(context) { AfterPublish=_=>throw new IOException(Private) };
        var controller=new TestingController(bridge,null,new(handler));
        try
        {
            await InitializeAsync(context,controller);
            await CodeAsync("StoreIoFailure",()=>controller.ImportFigmaAsync(Request,Criteria(),Token));
            var committed=Assert.IsType<TestSourceSnapshot>(bridge.Acknowledged);
            var reopened=await new ProjectTestStore(disk.User,"default").ReadSourceAsync(committed.Id);
            Assert.Equal(committed.Revision,reopened.Revision);Assert.Equal(committed.OriginalBytes,reopened.OriginalBytes);
            Assert.Equal(1,bridge.Publications);Assert.Single(handler.Requests);
            Assert.Empty(controller.Snapshot.Sources); // Failure supplies no acknowledgment to this controller; Refresh is explicit.
            await controller.RefreshAsync();Assert.Equal(committed.Id,Assert.Single(controller.Snapshot.Sources).Id);
        }
        finally { await SettleAsync(context,controller,null); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Closed_admission_never_enumerates_inputs_or_invokes_credentials_or_HTTP(bool closeContext)
    {
        using var handler=new SyntheticHandler((_,_)=>throw new InvalidOperationException(Private));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        if(closeContext) fixture.Context.Close(PluginStopReason.PluginDisabled);else await fixture.Controller.StopAsync();
        var nodes=new OnceList<string>(["1:2"],()=>throw new InvalidOperationException(Private));
        var criteria=new OnceDictionary(Criteria(),()=>throw new InvalidOperationException(Private));var tokens=0;
        await CodeAsync("TestingContextClosed",()=>fixture.Controller.ImportFigmaAsync(new("Ab12",nodes),criteria,_=> { tokens++;throw new InvalidOperationException(Private); }));
        Assert.Equal(0,nodes.Enumerations);Assert.Equal(0,criteria.Enumerations);Assert.Equal(0,tokens);Assert.Empty(handler.Requests);
        Assert.Empty(await new ProjectTestStore(fixture.Disk.User,"default").ListSourcesAsync());
    }

    [Fact]
    public async Task Aggregate_200_is_admitted_but_201_is_rejected_before_any_credential_or_HTTP_work()
    {
        var ids=Enumerable.Range(1,13).Select(i=>i+":2").ToArray();var tokens=0;
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Json(ids))));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        Task<string?> Credential(CancellationToken ct) { tokens++;return Token(ct); }
        var source=await fixture.Controller.ImportFigmaAsync(new("Ab12",ids,"v1"),ManyCriteria(ids,200),Credential);
        Assert.Equal(200,(await fixture.Controller.GenerateAsync([source.Id])).Cases.Count);
        await CodeAsync("InvalidTestSource",()=>fixture.Controller.ImportFigmaAsync(new("Ab12",ids,"v1"),ManyCriteria(ids,201),Credential));
        Assert.Equal(1,tokens);Assert.Single(handler.Requests);Assert.Single(await fixture.Context.Testing.ListSourcesAsync());
    }

    [Theory]
    [InlineData("request","InvalidTestSource")]
    [InlineData("criteria","InvalidTestSource")]
    [InlineData("collection","InvalidTestSource")]
    [InlineData("callback-null","InvalidTestSource")]
    [InlineData("credential","TestingFailed")]
    [InlineData("http","TestingFailed")]
    [InlineData("parse","InvalidTestSource")]
    [InlineData("builder","InvalidTestSource")]
    [InlineData("body-cap","StoreLimitExceeded")]
    [InlineData("io","StoreIoFailure")]
    [InlineData("envelope","StoreLimitExceeded")]
    public async Task Capture_remote_builder_and_store_failures_are_safe_and_preserve_old_source_bytes_and_dirty_draft(string stage,string code)
    {
        var ids=stage=="envelope" ? Enumerable.Range(1,13).Select(i=>i+":2").ToArray() : new[]{"2:3","1:2"};
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(stage switch
        {
            "http"=>new(HttpStatusCode.Unauthorized) { ReasonPhrase=Private,Content=new StringContent(Secret) },
            "parse"=>Response(Encoding.UTF8.GetBytes("{\"name\":\""+Private+"\"}")),
            "builder"=>Response(Json(ids,new string('x',4096))),
            "body-cap"=>Response(new byte[1024*1024+1]),
            _=>Response(Json(ids))
        }));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var set=await fixture.ImportAndGenerateAsync();var old=Assert.Single(await fixture.Context.Testing.ListSourcesAsync());
        var oldBytes=await File.ReadAllBytesAsync(SourcePath(fixture.Disk.User,old.Id));var caseBytes=await File.ReadAllBytesAsync(CasePath(fixture.Disk.User,set.Id));
        var dirty=new string('x',201);fixture.Controller.CaptureSetName(dirty);
        await CodeAsync("StoreLimitExceeded",()=>fixture.Controller.FlushAsync());
        var criteria=stage=="envelope" ? ManyCriteria(ids,200,new string('界',4096)) : Criteria();
        if(stage=="criteria") criteria["2:3"]=[];
        if(stage=="collection") criteria["2:3"]=new OnceList<string>(["a"],()=>throw new FigmaReadException(FigmaReadError.AuthenticationRefused));
        var request=new FigmaReadRequest(stage=="request" ? "https://"+Private : "Ab12",ids,"v1");
        var tokens=0;
        Task<string?> Credential(CancellationToken ct) { tokens++;if(stage=="credential") throw new InvalidOperationException(Secret+Private);return Token(ct); }
        using(var obstruction=stage=="io" ? ObstructPublicationIo(SourcePath(fixture.Disk.User,old.Id)) : null)
            await CodeAsync(code,()=>fixture.Controller.ImportFigmaAsync(request,criteria,stage=="callback-null" ? null! : Credential));
        Assert.Equal(oldBytes,await File.ReadAllBytesAsync(SourcePath(fixture.Disk.User,old.Id)));
        Assert.Equal(caseBytes,await File.ReadAllBytesAsync(CasePath(fixture.Disk.User,set.Id)));
        Assert.Single(await fixture.Context.Testing.ListSourcesAsync());
        var draft=Assert.IsType<CaseDraftView>(fixture.Controller.Snapshot.Draft);
        Assert.Equal(set.Id,draft.Id);Assert.Equal(set.Revision,draft.BaseRevision);Assert.Equal(dirty,draft.Content.Name);Assert.True(draft.IsDirty);
        Assert.Equal(old.Id,Assert.Single(fixture.Controller.Snapshot.Sources).Id);Assert.Equal(code,fixture.Controller.Snapshot.ErrorCode);
        if(stage is "request" or "criteria" or "collection" or "callback-null") { Assert.Equal(0,tokens);Assert.Empty(handler.Requests); }
        else { Assert.Equal(1,tokens);Assert.Equal(stage=="credential" ? 0 : 1,handler.Requests.Count); }
    }

    [Theory]
    [InlineData(FileShare.Read,true)]
    [InlineData(FileShare.None,false)]
    public async Task Core_validation_allows_shared_reads_but_refuses_exclusive_reads_before_new_Figma_publication(FileShare sharing,bool succeeds)
    {
        var raw=Json(["2:3","1:2"]);
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(raw)));
        await using var fixture=await TestingDomainFixture.CreateAsync(figmaReader:new(handler));
        var set=await fixture.ImportAndGenerateAsync();var old=Assert.Single(await fixture.Context.Testing.ListSourcesAsync());
        var path=SourcePath(fixture.Disk.User,old.Id);var oldBytes=await File.ReadAllBytesAsync(path);
        var caseBytes=await File.ReadAllBytesAsync(CasePath(fixture.Disk.User,set.Id));
        var dirty=new string('x',201);fixture.Controller.CaptureSetName(dirty);
        await CodeAsync("StoreLimitExceeded",()=>fixture.Controller.FlushAsync());
        TestSourceSnapshot? saved=null;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,sharing))
        {
            if(succeeds) saved=await fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token);
            else await CodeAsync("StoreIoFailure",()=>fixture.Controller.ImportFigmaAsync(Request,Criteria(),Token));
        }
        // Release the managed sharing guard before reading the preserved old file or validating the store again.
        Assert.Equal(oldBytes,await File.ReadAllBytesAsync(path));
        Assert.Equal(caseBytes,await File.ReadAllBytesAsync(CasePath(fixture.Disk.User,set.Id)));
        var draft=Assert.IsType<CaseDraftView>(fixture.Controller.Snapshot.Draft);
        Assert.Equal(set.Id,draft.Id);Assert.Equal(set.Revision,draft.BaseRevision);Assert.Equal(dirty,draft.Content.Name);Assert.True(draft.IsDirty);
        var sources=await fixture.Context.Testing.ListSourcesAsync();Assert.Single(handler.Requests);
        if(succeeds)
        {
            var published=Assert.IsType<TestSourceSnapshot>(saved);
            Assert.NotEqual(old.Id,published.Id);Assert.Equal(raw,published.OriginalBytes);Assert.Equal(Hash(raw),published.OriginalSha256);
            Assert.Equal(2,sources.Count);Assert.Contains(sources,source=>source.Id==old.Id);Assert.Contains(sources,source=>source.Id==published.Id);
            Assert.Equal(raw,(await fixture.Context.Testing.ReadSourceAsync(published.Id)).OriginalBytes);
            Assert.Equal(2,fixture.Controller.Snapshot.Sources.Count);Assert.Null(fixture.Controller.Snapshot.ErrorCode);
        }
        else
        {
            Assert.Null(saved);Assert.Equal(old.Id,Assert.Single(sources).Id);
            Assert.Equal(old.Id,Assert.Single(fixture.Controller.Snapshot.Sources).Id);Assert.Equal("StoreIoFailure",fixture.Controller.Snapshot.ErrorCode);
        }
    }

    // Windows refuses Core's old-source validation read; Unix retains the existing directory-write obstruction.
    private static IDisposable ObstructPublicationIo(string oldSourcePath)=>OperatingSystem.IsWindows()
        ? new FileStream(oldSourcePath,FileMode.Open,FileAccess.Read,FileShare.None)
        : TestingAuthoringTests.WriteObstruction.Create(oldSourcePath);

    private static string SourcePath(string root,string id)=>Path.Combine(StoreRoot(root),"sources",id+".json");
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string[] OutsideProjectFiles(string root)=>Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)
        .Where(path=>Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar)[0]!=".workbench-project")
        .Select(path=>Path.GetRelativePath(root,path)+":"+Hash(File.ReadAllBytes(path))).Order(StringComparer.Ordinal).ToArray();
    private static byte[] Json(string[] ids,string characters="Observed text")=>Encoding.UTF8.GetBytes(" \n"+JsonSerializer.Serialize(new
    {
        name="Observed file 🧪",version="v1",nodes=ids.ToDictionary(id=>id,id=>new { document=new { id,name="Observed "+id,type="TEXT",characters } })
    })+"\n");
    private static Dictionary<string,IReadOnlyList<string>> ManyCriteria(string[] ids,int total,string criterion="Explicit criterion")
    {
        var result=new Dictionary<string,IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach(var id in ids) { var count=Math.Min(16,total);result[id]=Enumerable.Repeat(criterion,count).ToArray();total-=count; }
        Assert.Equal(0,total);return result;
    }
    private static HttpResponseMessage Response(byte[] bytes)
    {
        var response=new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType=new MediaTypeHeaderValue("application/json");return response;
    }
    private static async Task CodeAsync(string code,Func<Task> action)
    {
        var error=await Assert.ThrowsAsync<PluginTestingException>(action);Assert.Equal(code,error.ErrorCode);
        Assert.Null(error.InnerException);Assert.DoesNotContain(Secret,error.ToString(),StringComparison.Ordinal);Assert.DoesNotContain(Private,error.ToString(),StringComparison.Ordinal);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var elapsed=Stopwatch.StartNew();
        while(!predicate()) { Assert.True(elapsed.Elapsed<Budget,"The actual retained boundary was not reached.");await Task.Delay(1); }
    }
    private static async Task InitializeAsync(PluginRegistrationContext context,TestingController controller)
    {
        await controller.InitializeAsync();context.RegisterBeforeProjectLeave(controller.PrepareLeaveAsync);
        Assert.True(await context.BackgroundTasks.PrepareCommitAsync(CancellationToken.None));context.Commit();
    }
    private static async Task SettleAsync(PluginRegistrationContext context,TestingController controller,Task? import)
    {
        context.Close(PluginStopReason.PluginDisabled);
        if(import is not null) _=await Record.ExceptionAsync(()=>import);
        await Task.WhenAll(context.CancelLifetimeAsync(),controller.StopAsync(),context.BackgroundTasks.StopAllAsync(),context.DrainProjectWorkAsync());
    }
    private sealed class SyntheticHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal List<(string Method,string Uri,string Token)> Requests { get; }=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method,request.RequestUri!.AbsoluteUri,request.Headers.GetValues("X-Figma-Token").Single()));
            return send(request,cancellationToken);
        }
    }
    private sealed class OnceList<T>(T[] values,Action? prefix=null) : IReadOnlyList<T>
    {
        internal int Enumerations;
        public int Count=>throw new InvalidOperationException("Caller Count must not be revisited.");
        public T this[int index]=>throw new InvalidOperationException("Caller index must not be revisited.");
        public IEnumerator<T> GetEnumerator()
        {
            Assert.Equal(1,Interlocked.Increment(ref Enumerations));prefix?.Invoke();return ((IEnumerable<T>)values).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class OnceDictionary(Dictionary<string,IReadOnlyList<string>> values,Action? prefix=null) : IReadOnlyDictionary<string,IReadOnlyList<string>>
    {
        internal int Enumerations;
        public int Count=>throw new InvalidOperationException();public IEnumerable<string> Keys=>throw new InvalidOperationException();
        public IEnumerable<IReadOnlyList<string>> Values=>throw new InvalidOperationException();public IReadOnlyList<string> this[string key]=>throw new InvalidOperationException();
        public bool ContainsKey(string key)=>throw new InvalidOperationException();public bool TryGetValue(string key,out IReadOnlyList<string> value)=>throw new InvalidOperationException();
        public IEnumerator<KeyValuePair<string,IReadOnlyList<string>>> GetEnumerator()
        {
            Assert.Equal(1,Interlocked.Increment(ref Enumerations));prefix?.Invoke();return values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }

    // Test-only boundary gates forward to the actual Core facade/store; a returned snapshot is never fabricated.
    private sealed class PublicationContext(IPluginTestingContext actual) : IPluginTestingContext,IProjectTestWorkspace
    {
        internal Action? BeforePublish;internal Func<TestSourceSnapshot,Task>? AfterPublish;
        internal TestSourceSnapshot? Acknowledged;internal int Publications;
        public Task<TestSourceSnapshot> PublishSourceAsync(TestSourceDraft draft,CancellationToken ct=default)
        { BeforePublish?.Invoke();Publications++;return PublishAsync(draft,ct); }
        private async Task<TestSourceSnapshot> PublishAsync(TestSourceDraft draft,CancellationToken ct)
        { var result=await actual.Testing.PublishSourceAsync(draft,ct);Acknowledged=result;if(AfterPublish is not null) await AfterPublish(result);return result; }
        public string PluginId=>actual.PluginId;public string DataDirectory=>actual.DataDirectory;public CancellationToken Lifetime=>actual.Lifetime;
        public IPluginSettings Settings=>actual.Settings;public IPluginBackgroundTasks BackgroundTasks=>actual.BackgroundTasks;public IPluginSecretStore Secrets=>actual.Secrets;
        public PluginStopReason StopReason=>actual.StopReason;public IProjectContext Project=>actual.Project;public IProjectTestWorkspace Testing=>this;
        public void RegisterBeforeProjectLeave(Func<CancellationToken,Task> hook)=>actual.RegisterBeforeProjectLeave(hook);
        public void RegisterView(IPluginView view)=>actual.RegisterView(view);public void RegisterCommand(PluginCommand command)=>actual.RegisterCommand(command);public void RegisterSetting(PluginSetting setting)=>actual.RegisterSetting(setting);
        public Task<IReadOnlyList<TestSourceSummary>> ListSourcesAsync(CancellationToken ct=default)=>actual.Testing.ListSourcesAsync(ct);
        public Task<TestSourceSnapshot> ReadSourceAsync(string id,CancellationToken ct=default)=>actual.Testing.ReadSourceAsync(id,ct);
        public Task<TestCaseSetSnapshot> CreateCaseSetAsync(TestCaseSetDraft draft,CancellationToken ct=default)=>actual.Testing.CreateCaseSetAsync(draft,ct);
        public Task<IReadOnlyList<TestCaseSetSummary>> ListCaseSetsAsync(CancellationToken ct=default)=>actual.Testing.ListCaseSetsAsync(ct);
        public Task<TestCaseSetSnapshot> ReadCaseSetAsync(string id,CancellationToken ct=default)=>actual.Testing.ReadCaseSetAsync(id,ct);
        public Task<TestCaseSetSnapshot> UpdateCaseSetAsync(string id,TestCaseSetContent content,string revision,CancellationToken ct=default)=>actual.Testing.UpdateCaseSetAsync(id,content,revision,ct);
        public Task<TestCaseSetSnapshot> ReadCaseRevisionAsync(string id,string revision,CancellationToken ct=default)=>actual.Testing.ReadCaseRevisionAsync(id,revision,ct);
        public Task<TestRunSnapshot> CreateRunAsync(string id,string revision,IReadOnlyList<string> selected,CancellationToken ct=default)=>actual.Testing.CreateRunAsync(id,revision,selected,ct);
        public Task<TestRunSnapshot> CreateRetryRunAsync(string id,CancellationToken ct=default)=>actual.Testing.CreateRetryRunAsync(id,ct);
        public Task<IReadOnlyList<TestRunSummary>> ListRunsAsync(CancellationToken ct=default)=>actual.Testing.ListRunsAsync(ct);
        public Task<TestRunSnapshot> ReadRunAsync(string id,CancellationToken ct=default)=>actual.Testing.ReadRunAsync(id,ct);
        public Task<TestRunSnapshot> AppendRunEventAsync(string id,long sequence,string head,TestRunEventDraft draft,CancellationToken ct=default)=>actual.Testing.AppendRunEventAsync(id,sequence,head,draft,ct);
        public Task<TestEvidenceReceipt> AddRunEvidenceAsync(string id,TestEvidenceDraft draft,CancellationToken ct=default)=>actual.Testing.AddRunEvidenceAsync(id,draft,ct);
        public Task<TestEvidence> ReadRunEvidenceAsync(string id,string evidence,CancellationToken ct=default)=>actual.Testing.ReadRunEvidenceAsync(id,evidence,ct);
    }
}
