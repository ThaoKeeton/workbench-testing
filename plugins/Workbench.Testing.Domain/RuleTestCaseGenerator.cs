using Workbench.Sdk;
namespace Workbench.Testing.Domain;

/// <summary>Explicit rule templates only. Unmapped criteria always remain manual.</summary>
public sealed class RuleTestCaseGenerator : ITestCaseGenerator
{
    public TestCaseSetDraft Generate(IReadOnlyList<TestSourceSnapshot> sources)
    {
        if(sources.Count==0) throw DomainErrors.Error("InvalidTestSource");
        var cases=new List<TestCaseDraft>();
        foreach(var source in sources)
        foreach(var item in source.Items)
        for(var index=0;index<item.AcceptanceCriteria.Count;index++)
        {
            if(cases.Count==200) throw DomainErrors.Error("StoreLimitExceeded");
            IReadOnlyList<TestTrace> traces=Array.AsReadOnly(new[] { new TestTrace(source.Id,source.Revision,item.Locator,index) });
            var steps=new List<TestStepDraft>();
            if(item.VirtualScenario is null)
                steps.Add(new(string.IsNullOrWhiteSpace(item.Description) ? item.Title : item.Description,item.AcceptanceCriteria[index],traces,null));
            else
            {
                var empty=item.VirtualScenario==TestVirtualScenario.TaskFormEmptyTitle;
                var deliberate=item.VirtualScenario==TestVirtualScenario.TaskFormAssertionFailure;
                steps.Add(new("Set the simulated task title.",empty ? "Title is empty." : "Title is set.",traces,new(TestActionKind.SetText,"task.title",empty ? "" : item.Title)));
                steps.Add(new("Click the simulated Save button.",empty ? "Empty title is rejected." : "A task is saved.",traces,new(TestActionKind.Click,"task.save",null)));
                steps.Add(new(deliberate ? "DELIBERATE ASSERTION FAILURE: expect an incorrect toast." : "Assert the simulated response.",
                    empty ? "Title required" : deliberate ? "DELIBERATELY WRONG TOAST" : "Saved",traces,
                    new(TestActionKind.AssertText,empty ? "task.validation" : "task.toast",empty ? "Title required" : deliberate ? "DELIBERATELY WRONG TOAST" : "Saved")));
                steps.Add(new("Assert the actual created task count.",empty ? "0" : "1",traces,new(TestActionKind.AssertText,"task.createdCount",empty ? "0" : "1")));
            }
            cases.Add(new(item.Title,item.VirtualScenario is null ? "Rule template: manual verification required." : item.VirtualScenario==TestVirtualScenario.TaskFormAssertionFailure
                ? "SIMULATED rule template: deliberately failing assertion example." : "SIMULATED rule template: each case starts with an empty virtual task form.",traces,steps.AsReadOnly()));
        }
        if(cases.Count==0) throw DomainErrors.Error("InvalidCaseSet");
        return new("Rule-generated cases",cases.AsReadOnly());
    }
}
