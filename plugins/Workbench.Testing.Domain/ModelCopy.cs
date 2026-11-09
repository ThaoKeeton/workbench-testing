using Workbench.Sdk;
namespace Workbench.Testing.Domain;

internal static class ModelCopy
{
    internal static IReadOnlyList<T> List<T>(IEnumerable<T> items)=>Array.AsReadOnly(items.ToArray());
    internal static TestCaseSetContent Content(TestCaseSetContent value)=>new(value.Name,List(value.Cases.Select(Case)));
    internal static TestCase Case(TestCase value)=>value with { Traces=List(value.Traces),Steps=List(value.Steps.Select(s=>s with { Traces=List(s.Traces) })) };
    internal static TestCaseSetSnapshot Set(TestCaseSetSnapshot value)=>value with { Cases=List(value.Cases.Select(Case)) };
    internal static TestRunSnapshot Run(TestRunSnapshot value)=>value with { Plan=value.Plan with { Cases=List(value.Plan.Cases.Select(Case)) }, Events=List(value.Events.Select(e=>e with { EvidenceIds=List(e.EvidenceIds) })) };
    internal static TestCaseSetDraft Draft(TestCaseSetContent value)=>new(value.Name,List(value.Cases.Select(c=>new TestCaseDraft(c.Title,c.Prerequisites,List(c.Traces),List(c.Steps.Select(s=>new TestStepDraft(s.Instruction,s.Expected,List(s.Traces),s.Action)))))));
}
