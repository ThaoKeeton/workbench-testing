using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Sdk;

namespace Workbench.Testing.Domain.Generation.Ai;

// Consumes only an immutable, validated capture and an acknowledged Core receipt identity.
internal static class AiPublicationBuilder
{
    private const string Review="AI generated; manual review required.";
    private const string CaseSetName="AI 测试用例（待人工审核）";

    internal static TestSourceDraft Receipt(AiGenerationCapture capture)
    {
        var response=capture.ResponseJson;
        var original=JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion=1,requestedModel=AiTestCaseGenerator.Model,observedModel=capture.Model,responseId=capture.ResponseId,
            inputUtf8Base64=capture.InputJson,inputSha256=capture.InputSha256,
            responseUtf8Base64=response,responseSha256=Convert.ToHexStringLower(SHA256.HashData(response)),
            originalTraces=capture.OriginalTraces.Select(trace=>new
            { snapshotId=trace.SnapshotId,snapshotRevision=trace.SnapshotRevision,locator=trace.Locator,criterionIndex=trace.CriterionIndex })
        });
        if(original.Length>1024*1024) throw DomainErrors.Error("StoreLimitExceeded");
        var items=capture.Cases.Select((item,index)=>new TestSourceItem("case-"+(index+1),item.Title,Review,
            Array.AsReadOnly(item.Steps.Select(step=>step.Expected).ToArray()),null)).ToArray();
        return new(TestSourceKind.Requirements,"openai.responses.test-cases","1",capture.ResponseId,
            "AI generation receipt (manual review required)",original,Array.AsReadOnly(items));
    }

    internal static TestCaseSetDraft CaseSet(AiGenerationCapture capture,TestSourceSnapshot receipt)
    {
        var cases=capture.Cases.Select((item,index)=>
        {
            var original=capture.OriginalTraces[item.CriterionIndex];
            var prerequisites=Review+" Model: "+AiTestCaseGenerator.Model+"."+(item.Prerequisites.Length==0 ? "" : "\n"+item.Prerequisites);
            if(prerequisites.Length>4096) throw DomainErrors.Error("StoreLimitExceeded");
            var locator=receipt.Items[index].Locator;
            return new TestCaseDraft(item.Title,prerequisites,
                Array.AsReadOnly(new[] { original,new TestTrace(receipt.Id,receipt.Revision,locator,0) }),
                Array.AsReadOnly(item.Steps.Select((step,stepIndex)=>new TestStepDraft(step.Instruction,step.Expected,
                    Array.AsReadOnly(new[] { original,new TestTrace(receipt.Id,receipt.Revision,locator,stepIndex) }),null)).ToArray()));
        }).ToArray();
        return new(CaseSetName,Array.AsReadOnly(cases));
    }
}
