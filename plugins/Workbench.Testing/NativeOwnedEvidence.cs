using Workbench.Testing.Domain.OwnedLab;

namespace Workbench.Testing;

internal sealed record NativeOwnedEventId(string RunId, int Sequence);
internal sealed record NativeOwnedSelection(string RunId, OwnedEvent Event, string Key);
internal static class NativeOwnedEvidence
{
    internal static NativeOwnedSelection? Resolve(OwnedRunSnapshot? displayedRun, string? displayedRunId, NativeOwnedEventId? selected)
    {
        if (displayedRun is null || selected is null || displayedRun.Execution.RunId != displayedRunId || selected.RunId != displayedRunId) return null;
        var record = displayedRun.Events.FirstOrDefault(e => e.Sequence == selected.Sequence);
        return record is null ? null : new(displayedRun.Execution.RunId, record,
            "owned/" + displayedRun.Execution.RunId + "/" + record.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + (record.FrameSha ?? "none"));
    }
}
