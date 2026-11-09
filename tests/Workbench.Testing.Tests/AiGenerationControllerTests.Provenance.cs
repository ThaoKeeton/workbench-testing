using System.Text;
using System.Text.Json;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Generation.Ai;
using Xunit;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed partial class AiGenerationControllerTests
{
    [Fact]
    public async Task Real_Core_publication_preserves_exact_receipt_bytes_original_and_receipt_traces_manual_steps_CSV_and_project_isolation()
    {
        var raw=Raw("  Model setup 🧪  ");
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(raw)));
        await using var rig=await Rig.CreateAsync(handler);
        await using var other=await TestingDomainFixture.CreateAsync(aiGenerator:new(handler));
        var source=rig.Source;var oldBytes=await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,source.Id));
        var lists=rig.Bridge.ListReads;rig.Bridge.RefuseLists=true;
        var publication=await rig.Controller.GenerateWithAiAsync([source.Id],Key);
        Assert.Equal(AiGenerationPublicationState.Created,publication.State);Assert.Null(publication.ErrorCode);
        var receipt=publication.Receipt;var set=Assert.IsType<TestCaseSetSnapshot>(publication.CaseSet);
        Assert.Equal(TestSourceKind.Requirements,receipt.Kind);Assert.Equal("openai.responses.test-cases",receipt.ProviderId);
        Assert.Equal("1",receipt.ProviderVersion);Assert.Equal("resp_controller-1",receipt.DocumentId);
        Assert.Equal(source.ProjectId,receipt.ProjectId);Assert.Equal(source.ProducerPluginId,receipt.ProducerPluginId);
        Assert.Equal("AI 测试用例（待人工审核）",set.Name);
        Assert.Equal(Hash(receipt.OriginalBytes),receipt.OriginalSha256);
        using var json=JsonDocument.Parse(receipt.OriginalBytes);var root=json.RootElement;
        Assert.Equal(new[]{"inputSha256","inputUtf8Base64","observedModel","originalTraces","requestedModel","responseId","responseSha256","responseUtf8Base64","schemaVersion"},root.EnumerateObject().Select(p=>p.Name).Order());
        Assert.Equal(1,root.GetProperty("schemaVersion").GetInt32());Assert.Equal(Model,root.GetProperty("requestedModel").GetString());
        Assert.Equal(Model,root.GetProperty("observedModel").GetString());Assert.Equal("resp_controller-1",root.GetProperty("responseId").GetString());
        Assert.Equal(handler.Input,root.GetProperty("inputUtf8Base64").GetBytesFromBase64());
        Assert.Equal(Hash(handler.Input!),root.GetProperty("inputSha256").GetString());
        Assert.Equal(raw,root.GetProperty("responseUtf8Base64").GetBytesFromBase64());Assert.Equal(Hash(raw),root.GetProperty("responseSha256").GetString());
        var originalTraces=root.GetProperty("originalTraces").EnumerateArray().ToArray();Assert.Equal(2,originalTraces.Length);
        for(var index=0;index<2;index++)
        {
            var trace=originalTraces[index];Assert.Equal(source.Id,trace.GetProperty("snapshotId").GetString());
            Assert.Equal(source.Revision,trace.GetProperty("snapshotRevision").GetString());Assert.Equal("original/item",trace.GetProperty("locator").GetString());
            Assert.Equal(index,trace.GetProperty("criterionIndex").GetInt32());
            var item=set.Cases[index];var receiptItem=receipt.Items[index];
            var original=new TestTrace(source.Id,source.Revision,"original/item",1-index);
            Assert.Equal("case-"+(index+1),receiptItem.Locator);Assert.Equal(item.Title,receiptItem.Title);
            Assert.Contains("manual review",receiptItem.Description,StringComparison.OrdinalIgnoreCase);Assert.Null(receiptItem.VirtualScenario);
            Assert.Equal(new[]{original,new TestTrace(receipt.Id,receipt.Revision,receiptItem.Locator,0)},item.Traces);
            Assert.Equal(Prefix+"\n  Model setup 🧪  ",item.Prerequisites);
            for(var stepIndex=0;stepIndex<2;stepIndex++)
            {
                var step=item.Steps[stepIndex];Assert.Null(step.Action);
                Assert.Equal(new[]{original,new TestTrace(receipt.Id,receipt.Revision,receiptItem.Locator,stepIndex)},step.Traces);
                Assert.Equal(step.Expected,receiptItem.AcceptanceCriteria[stepIndex]);Assert.Equal("  Manual "+(1-index)+"/"+stepIndex+"  ",step.Instruction);
            }
        }
        var store=new ProjectTestStore(rig.Disk.User,"default");
        Assert.Equal(receipt.OriginalBytes,(await store.ReadSourceAsync(receipt.Id)).OriginalBytes);
        Assert.Equal(set.Revision,(await store.ReadCaseSetAsync(set.Id)).Revision);
        Assert.Equal(oldBytes,await File.ReadAllBytesAsync(SourcePath(rig.Disk.User,source.Id)));
        Assert.Equal(2,rig.Controller.Snapshot.Sources.Count);Assert.Equal(set.Id,rig.Controller.Snapshot.Draft!.Id);
        Assert.Equal(lists,rig.Bridge.ListReads);Assert.Equal(1,handler.Requests);Assert.False(handler.Disposed);
        Assert.Empty(await other.Context.Testing.ListSourcesAsync());Assert.Empty(await other.Context.Testing.ListCaseSetsAsync());
        var csv=await rig.Controller.ExportCsvAsync();Assert.Equal(new byte[]{239,187,191},csv[..3]);
        var rows=TestingSourceTests.ParseCsv(Encoding.UTF8.GetString(csv[3..]));Assert.Equal(17,rows.Count);
        for(var index=0;index<2;index++)
        for(var step=0;step<2;step++)
        for(var scope=0;scope<2;scope++)
        for(var trace=0;trace<2;trace++)
        {
            var row=rows[1+index*8+step*4+scope*2+trace];
            Assert.Equal("'"+(trace==0 ? source.Id : receipt.Id),row[7]);Assert.Equal("'"+(trace==0 ? source.Revision : receipt.Revision),row[8]);
            Assert.Equal((trace==0 ? 1-index : scope==0 ? 0 : step).ToString(),row[10]);Assert.Equal(scope==0 ? "'case" : "'step",row[11]);
            Assert.Equal("'Manual",row[13]);Assert.Equal("'",row[14]);
        }
        await CodeAsync("ManualStepUnsupported",()=>rig.Context.Testing.CreateRunAsync(set.Id,set.Revision,[set.Cases[0].Id]));
    }

    [Theory]
    [InlineData("empty","InvalidId")] [InlineData("duplicate","InvalidId")] [InlineData("invalid","InvalidId")]
    [InlineData("missing","TestRecordNotFound")] [InlineData("many","StoreLimitExceeded")]
    public async Task Invalid_or_missing_selected_IDs_refuse_before_credentials_or_HTTP(string kind,string code)
    {
        using var handler=new SyntheticHandler((_,_)=>Task.FromResult(Response(Raw())));
        await using var rig=await Rig.CreateAsync(handler);var calls=0;
        string[] ids=kind switch { "empty"=>[],"duplicate"=>[rig.Source.Id,rig.Source.Id],"invalid"=>["bad"],"many"=>Enumerable.Repeat(rig.Source.Id,9).ToArray(),_=>[new string('1',32)] };
        await CodeAsync(code,()=>rig.Controller.GenerateWithAiAsync(ids,_=> { calls++;return Key(default); }));
        Assert.Equal(0,calls);Assert.Equal(0,handler.Requests);Assert.Equal(0,rig.Bridge.SourceWrites);Assert.Equal(0,rig.Bridge.CaseWrites);
        Assert.Equal(kind=="missing" ? 1 : 0,rig.Bridge.Reads);
    }
}
