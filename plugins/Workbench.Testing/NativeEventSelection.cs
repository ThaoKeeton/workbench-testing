using Workbench.Sdk;

namespace Workbench.Testing;

internal sealed record NativeEventId(string RunId, long Sequence);
internal sealed record NativeEventSelection(string RunId, TestRunEvent Event)
{
    internal string Key => RunId + "/" + Event.Sequence;

    internal static NativeEventSelection? Resolve(TestRunSnapshot? run, string? displayedRunId, NativeEventId? item)
    {
        if (run is null || item is null || run.Plan.Id != displayedRunId || item.RunId != displayedRunId) return null;
        var record = run.Events.FirstOrDefault(e => e.Sequence == item.Sequence);
        return record is null ? null : new(run.Plan.Id, record);
    }
}
