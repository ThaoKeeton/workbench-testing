using System.Net;
using System.Text;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Xunit;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed partial class AiGenerationControllerTests
{
    [Theory]
    [InlineData("credential","TestingFailed")] [InlineData("http","TestingFailed")]
    [InlineData("malformed","InvalidTestSource")] [InlineData("body-cap","StoreLimitExceeded")]
    [InlineData("callback-null","InvalidTestSource")]
    public async Task Failures_before_receipt_acknowledgment_throw_only_safe_existing_errors_and_preserve_old_records(string stage,string code)
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(stage switch
        {
            "http"=>new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content=new StringContent(Secret+Private) },
            "malformed"=>Response(Encoding.UTF8.GetBytes("{\"private\":\""+Private+"\"}")),
            "body-cap"=>Response(new byte[256*1024+1]),_=>Response(Raw())
        }));
        await using var rig=await Rig.CreateAsync(handler);var original=await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,rig.Source.Id));var keys=0;
        Task<string?> Credential(CancellationToken ct) { keys++;if(stage=="credential") throw new IOException(Secret+Private);return Key(ct); }
        await CodeAsync(code,()=>rig.Controller.GenerateWithAiAsync([rig.Source.Id],stage=="callback-null" ? null! : Credential));
        Assert.Equal(original,await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,rig.Source.Id)));
        Assert.Single(await rig.Context.Testing.ListSourcesAsync());Assert.Empty(await rig.Context.Testing.ListCaseSetsAsync());
        Assert.Equal(0,rig.Bridge.SourceWrites);Assert.Equal(0,rig.Bridge.CaseWrites);Assert.Equal(code,rig.Controller.Snapshot.ErrorCode);
        Assert.Equal(stage=="callback-null" ? 0 : 1,keys);Assert.Equal(stage is "callback-null" or "credential" ? 0 : 1,handler.Requests);
    }

    [Theory]
    [InlineData("cancel",AiGenerationPublicationState.Cancelled,null)]
    [InlineData("stop",AiGenerationPublicationState.Closed,"TestingContextClosed")]
    [InlineData("close",AiGenerationPublicationState.Closed,"TestingContextClosed")]
    [InlineData("io",AiGenerationPublicationState.Failed,"StoreIoFailure")]
    [InlineData("unknown",AiGenerationPublicationState.Failed,"TestingFailed")]
    [InlineData("overcap",AiGenerationPublicationState.Failed,"StoreLimitExceeded")]
    public async Task Receipt_acknowledgment_survives_case_failure_cancellation_or_closure_as_an_explicit_partial_result(string stage,AiGenerationPublicationState state,string? code)
    {
        using var caller=new CancellationTokenSource();
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw(stage=="overcap" ? new string('p',4096) : ""))));
        await using var rig=await Rig.CreateAsync(handler);var oldSet=await rig.Controller.GenerateAsync([rig.Source.Id]);
        var lists=rig.Bridge.ListReads;var originalCases=rig.Bridge.CaseWrites;rig.Bridge.RefuseLists=true;
        rig.Bridge.SourcePostfix=receipt=>
        {
            if(stage=="cancel") caller.Cancel();
            if(stage=="stop") _=rig.Controller.StopAsync();
            if(stage=="close") rig.Context.Close(PluginStopReason.PluginDisabled);
            return Task.CompletedTask;
        };
        rig.Bridge.CasePrefix=()=> { if(stage=="io") throw new IOException(Secret+Private);if(stage=="unknown") throw new InvalidOperationException(Secret+Private); };
        var result=await rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key,caller.Token);
        Assert.Equal(state,result.State);Assert.Equal(code,result.ErrorCode);Assert.Null(result.CaseSet);
        Assert.Equal(rig.Bridge.CommittedSource!.Id,result.Receipt.Id);
        Assert.Equal(result.Receipt.OriginalBytes,(await new ProjectTestStore(rig.Disk.User,"default").ReadSourceAsync(result.Receipt.Id)).OriginalBytes);
        Assert.Contains(rig.Controller.Snapshot.Sources,item=>item.Id==result.Receipt.Id);
        Assert.Equal(oldSet.Id,rig.Controller.Snapshot.Draft!.Id);Assert.Single(rig.Controller.Snapshot.CaseSets);
        Assert.Equal(lists,rig.Bridge.ListReads);Assert.Equal(1,rig.Bridge.SourceWrites);Assert.Equal(originalCases,rig.Bridge.CaseWrites);Assert.Equal(1,handler.Requests);
        Assert.Equal(code,rig.Controller.Snapshot.ErrorCode);
    }

    [Theory]
    [InlineData("receipt")] [InlineData("case")]
    public async Task Unacknowledged_actual_commits_keep_exact_bytes_without_retry_or_inferred_success_until_explicit_refresh(string stage)
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));
        await using var rig=await Rig.CreateAsync(handler);
        if(stage=="receipt") rig.Bridge.SourcePostfix=_=>throw new IOException(Secret+Private);
        else rig.Bridge.CasePostfix=_=>throw new IOException(Secret+Private);
        var lists=rig.Bridge.ListReads;
        if(stage=="receipt") await CodeAsync("StoreIoFailure",()=>rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key));
        else
        {
            var result=await rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
            Assert.Equal(AiGenerationPublicationState.Failed,result.State);Assert.Equal("StoreIoFailure",result.ErrorCode);Assert.Null(result.CaseSet);
            Assert.Equal(rig.Bridge.CommittedSource!.Id,result.Receipt.Id);
        }
        var receipt=Assert.IsType<TestSourceSnapshot>(rig.Bridge.CommittedSource);
        var bytes=await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,receipt.Id));
        var reopened=await new ProjectTestStore(rig.Disk.User,"default").ReadSourceAsync(receipt.Id);
        Assert.Equal(receipt.Revision,reopened.Revision);Assert.Equal(receipt.OriginalBytes,reopened.OriginalBytes);
        Assert.Equal(stage=="receipt" ? 1 : 2,rig.Controller.Snapshot.Sources.Count);Assert.Empty(rig.Controller.Snapshot.CaseSets);Assert.Null(rig.Controller.Snapshot.Draft);
        Assert.Equal(lists,rig.Bridge.ListReads);Assert.Equal(1,rig.Bridge.SourceWrites);Assert.Equal(stage=="receipt" ? 0 : 1,rig.Bridge.CaseWrites);Assert.Equal(1,handler.Requests);
        byte[]? caseBytes=null;
        if(stage=="case")
        {
            var committed=Assert.IsType<TestCaseSetSnapshot>(rig.Bridge.CommittedCase);
            caseBytes=await File.ReadAllBytesAsync(CasePath(rig.Disk.User,committed.Id));
            Assert.Equal(committed.Revision,(await new ProjectTestStore(rig.Disk.User,"default").ReadCaseSetAsync(committed.Id)).Revision);
        }
        await rig.Controller.RefreshAsync();Assert.Equal(2,rig.Controller.Snapshot.Sources.Count);
        Assert.Equal(stage=="case" ? 1 : 0,rig.Controller.Snapshot.CaseSets.Count);Assert.Null(rig.Controller.Snapshot.Draft);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,receipt.Id)));
        if(caseBytes is not null) Assert.Equal(caseBytes,await File.ReadAllBytesAsync(CasePath(rig.Disk.User,rig.Bridge.CommittedCase!.Id)));
        Assert.Equal(1,handler.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Case_acknowledgment_is_Created_despite_late_cancellation_Stop_or_a_corrupt_store_that_would_fail_a_reread(bool close)
    {
        using var caller=new CancellationTokenSource();using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));
        await using var rig=await Rig.CreateAsync(handler);var header=Path.Combine(StoreRoot(rig.Disk.User),"store.json");var bytes=await File.ReadAllBytesAsync(header);
        var lists=rig.Bridge.ListReads;rig.Bridge.RefuseLists=true;
        rig.Bridge.CasePostfix=async saved=>
        {
            await File.WriteAllTextAsync(header,Private);caller.Cancel();
            if(close) rig.Context.Close(PluginStopReason.PluginDisabled);
            _=rig.Controller.StopAsync();
        };
        try
        {
            var result=await rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key,caller.Token);
            Assert.Equal(AiGenerationPublicationState.Created,result.State);Assert.Null(result.ErrorCode);
            Assert.Equal(rig.Bridge.CommittedCase!.Revision,result.CaseSet!.Revision);
            Assert.Equal(result.CaseSet.Id,rig.Controller.Snapshot.Draft!.Id);Assert.Equal(result.CaseSet.Id,Assert.Single(rig.Controller.Snapshot.CaseSets).Id);
            Assert.Equal(lists,rig.Bridge.ListReads);Assert.Null(rig.Controller.Snapshot.ErrorCode);Assert.Equal(1,handler.Requests);
        }
        finally { await File.WriteAllBytesAsync(header,bytes); }
    }

    [Theory]
    [InlineData("receipt")] [InlineData("case")]
    public async Task Real_Core_record_caps_preserve_originals_and_return_partial_only_after_a_receipt_ack(string stage)
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));await using var rig=await Rig.CreateAsync(handler);
        if(stage=="receipt")
            for(var i=1;i<64;i++) await rig.Context.Testing.PublishSourceAsync(Original("extra-"+i));
        else
            for(var i=0;i<128;i++) await rig.Context.Testing.CreateCaseSetAsync(new("Existing",[new("Existing","",[new(rig.Source.Id,rig.Source.Revision,"original/item",0)],[new("Manual","Expected",[new(rig.Source.Id,rig.Source.Revision,"original/item",0)],null)])]));
        await rig.Controller.RefreshAsync();
        if(stage=="receipt") await CodeAsync("StoreLimitExceeded",()=>rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key));
        else
        {
            var result=await rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
            Assert.Equal(AiGenerationPublicationState.Failed,result.State);Assert.Equal("StoreLimitExceeded",result.ErrorCode);Assert.Null(result.CaseSet);
            Assert.Equal(2,(await rig.Context.Testing.ListSourcesAsync()).Count);
        }
        Assert.Equal(stage=="receipt" ? 64 : 2,(await rig.Context.Testing.ListSourcesAsync()).Count);
        Assert.Equal(stage=="case" ? 128 : 0,(await rig.Context.Testing.ListCaseSetsAsync()).Count);
        Assert.Equal(1,handler.Requests);Assert.Equal(rig.Source.OriginalBytes,(await rig.Context.Testing.ReadSourceAsync(rig.Source.Id)).OriginalBytes);
    }

    [Theory]
    [InlineData("receipt")] [InlineData("case")]
    public async Task Actual_Core_IO_failure_preserves_records_and_distinguishes_the_receipt_ack_boundary(string stage)
    {
        FileStream? obstruction=null;Rig? current=null;
        using var handler=new SyntheticHandler((_,_)=>
        {
            if(stage=="receipt") obstruction=new(SourcePath(current!.Disk.User,current.Source.Id),FileMode.Open,FileAccess.Read,FileShare.None);
            return Task.FromResult(Response(Raw()));
        });
        await using var rig=await Rig.CreateAsync(handler);current=rig;
        if(stage=="case") rig.Bridge.SourcePostfix=_=> { obstruction=new(SourcePath(rig.Disk.User,rig.Source.Id),FileMode.Open,FileAccess.Read,FileShare.None);return Task.CompletedTask; };
        var original=await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,rig.Source.Id));
        try
        {
            if(stage=="receipt") await CodeAsync("StoreIoFailure",()=>rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key));
            else
            {
                var result=await rig.Controller.GenerateWithAiAsync([rig.Source.Id],Key);
                Assert.Equal(AiGenerationPublicationState.Failed,result.State);Assert.Equal("StoreIoFailure",result.ErrorCode);Assert.Null(result.CaseSet);
            }
        }
        finally { obstruction?.Dispose(); }
        Assert.Equal(original,await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,rig.Source.Id)));
        Assert.Equal(stage=="receipt" ? 1 : 2,(await rig.Context.Testing.ListSourcesAsync()).Count);Assert.Empty(await rig.Context.Testing.ListCaseSetsAsync());Assert.Equal(1,handler.Requests);
    }
}
