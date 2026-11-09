using System.Globalization;
using System.Text;
using Workbench.Sdk;
namespace Workbench.Testing.Domain;

public sealed class CsvTestCaseExporter
{
    private static readonly string[] Headers=["caseId","caseTitle","prerequisites","stepId","stepOrdinal","instruction","expected","sourceId","sourceRevision","sourceLocator","criterionIndex","traceScope","traceOrdinal","automation","actionKind","elementId","actionValue"];
    /// <summary>Every text data cell gains an apostrophe; this intentionally changes CSV values, never stored text.</summary>
    public byte[] Export(TestCaseSetSnapshot set)
    {
        var output=new StringBuilder();
        Row(output,Headers);
        foreach(var item in set.Cases)
        for(var i=0;i<item.Steps.Count;i++)
        {
            var step=item.Steps[i];
            foreach(var (scope,traces) in new[] { ("case",item.Traces),("step",step.Traces) })
            for(var traceIndex=0;traceIndex<traces.Count;traceIndex++)
            {
                var trace=traces[traceIndex];
                if(trace.CriterionIndex<0) throw DomainErrors.Error("InvalidCaseSet");
                Row(output,[T(item.Id),T(item.Title),T(item.Prerequisites),T(step.Id),(i+1).ToString(CultureInfo.InvariantCulture),T(step.Instruction),T(step.Expected),
                    T(trace.SnapshotId),T(trace.SnapshotRevision),T(trace.Locator),trace.CriterionIndex.ToString(CultureInfo.InvariantCulture),T(scope),checked(traceIndex+1).ToString(CultureInfo.InvariantCulture),
                    T(step.Action is null ? "Manual" : "virtual.task-form.v1"),T(step.Action?.Kind.ToString() ?? ""),T(step.Action?.ElementId ?? ""),T(step.Action?.Value ?? "")]);
            }
        }
        return new byte[] {239,187,191}.Concat(new UTF8Encoding(false,true).GetBytes(output.ToString())).ToArray();
    }
    private static string T(string value)=>"'"+value;
    private static void Row(StringBuilder output,IEnumerable<string> cells) => output.AppendJoin(',',cells.Select(x=>"\""+x.Replace("\"","\"\"",StringComparison.Ordinal)+"\"")).Append("\r\n");
}
