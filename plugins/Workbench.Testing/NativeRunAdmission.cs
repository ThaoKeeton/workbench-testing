using Workbench.Sdk;
using Workbench.Testing.Domain;

namespace Workbench.Testing;

internal static class NativeRunAdmission
{
    // A terminal journal may precede supervisor retirement. The controller's next Queue/Retry
    // joins that actual retirement itself; never keep Run disabled forever on OwnsRun alone.
    internal static bool CanStart(TestingControllerSnapshot snapshot, string? ownedRunId)
        => !snapshot.OwnsRun || snapshot.Runs.Any(r => r.Id == ownedRunId && r.State is
            TestRunState.Completed or TestRunState.Failed or TestRunState.Cancelled or TestRunState.Interrupted);
}
