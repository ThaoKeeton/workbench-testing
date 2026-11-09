using Workbench.Sdk;

namespace Workbench.Testing.Domain.OwnedLab;

public enum OwnedScenario : ushort { Positive = 1, EmptyTitle = 2, AssertionFailure = 3 }
public enum OwnedWireKind : ushort { Init = 1, SetTitle = 2, ClickSave = 3, Observe = 4, Close = 5, TargetReady = 32768, Ready = 32769, Result = 32770, Closed = 32773, Error = 65535 }
public enum OwnedEventKind { Queued = 0, Started = 1, StepStarted = 2, StepResult = 3, Terminal = 4 }
public enum OwnedOutcome { Passed, Failed, Cancelled }
public enum OwnedRunState { Queued, Running, Passed, Failed, Cancelled, Interrupted, RecoveryNeeded }
public enum OwnedRetirementReason { Completed, Cancelled, Failed, Expired, Revoked }

public sealed record OwnedStep(string Id, TestAction Action);
public sealed record OwnedInputPlan(string TargetId, ushort Protocol, OwnedScenario Scenario, IReadOnlyList<OwnedStep> Steps, ReadOnlyMemory<byte> Bytes, string Sha256);
public sealed record OwnedHelperIdentity(string ImageSha, string NativeSourceSha, string SourceCommit, string Msvc, string Sdk, string Profile);
public sealed record OwnedExecution(int Schema, string RunId, string SessionId, string ProjectId, string PluginId, string? ParentRunId, string? ParentInputSha, string InputSha, string TargetId, ushort Protocol, OwnedHelperIdentity Helper, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);
public sealed record OwnedGrant(OwnedExecution Execution, ReadOnlyMemory<byte> ExecutionBytes, string ExecutionSha, long DeadlineTimestamp)
{
    public OwnedLaunchGate LaunchGate { get; internal init; } = new();
}
public sealed record OwnedIdentity(uint BrokerPid, ulong BrokerCreationFileTime, uint TargetPid, ulong TargetCreationFileTime, ulong Hwnd, string ImageSha);
public sealed record OwnedState(string Title, string Toast, string Validation, string CreatedCount);
public sealed record OwnedObservation(OwnedIdentity Identity, OwnedState State, byte[] Bmp);
public sealed record OwnedRetirement(bool WasLaunched, int? BrokerExitCode, int? TargetExitCode, uint ActiveProcesses);
public sealed record OwnedEvent(int Schema, int Sequence, string PriorHash, string ExecutionSha, DateTimeOffset Utc, OwnedEventKind Kind, int? StepIndex, OwnedOutcome? Outcome, string? ErrorCode, OwnedIdentity? Identity, OwnedState? State, string? FrameSha, string? CaptureKind, bool? SyntheticTarget, OwnedRetirement? Retirement, bool? UncertainStep);
public sealed record OwnedEventDraft(OwnedEventKind Kind, int? StepIndex = null, OwnedOutcome? Outcome = null, string? ErrorCode = null, OwnedIdentity? Identity = null, OwnedState? State = null, byte[]? Bmp = null, OwnedRetirement? Retirement = null, bool? UncertainStep = null);
public sealed record OwnedRunSnapshot(OwnedInputPlan Input, OwnedExecution Execution, string ExecutionSha, IReadOnlyList<OwnedEvent> Events, OwnedRunState State, int Sequence, string HeadSha, bool RecoveryNeeded, string? ErrorCode);
public sealed record OwnedRunSummary(OwnedExecution Execution, OwnedScenario Scenario, OwnedRunState State, int Sequence, string HeadSha);
public sealed record OwnedPilotSnapshot(IReadOnlyList<OwnedRunSummary> Runs, OwnedRunSnapshot? SelectedRun, bool IsBusy, bool AdmissionClosed, string? ErrorCode);
public sealed record OwnedScope(string ProjectId, string PluginId, string DataDirectory);
public sealed record OwnedPacket(OwnedWireKind Kind, uint Sequence, string Session, byte[] Body);

public interface IOwnedDriverFactory
{
    ValueTask<IOwnedPreparation> PrepareAsync(CancellationToken cancellationToken);
}
public interface IOwnedPreparation : IAsyncDisposable
{
    OwnedHelperIdentity Helper { get; }
    ValueTask<IOwnedSession> LaunchAsync(OwnedGrant grant, CancellationToken revocation);
}
public interface IOwnedSession : IAsyncDisposable
{
    OwnedIdentity Identity { get; }
    OwnedState InitialState { get; }
    ValueTask<OwnedObservation> ExecuteAsync(TestAction action, CancellationToken cancellationToken);
    ValueTask<OwnedRetirement> RetireAsync(OwnedRetirementReason reason);
}

public class OwnedLabException : Exception
{
    private static readonly HashSet<string> Codes = ["InvalidPlan", "UnavailableHelper", "ProtocolViolation", "TargetUnavailable", "DriverFailure", "CaptureFailure", "AssertionFailed", "Cancelled", "Expired", "StoreIoFailure", "RecoveryNeeded", "AdmissionClosed", "StoreLimitExceeded"];
    public string ErrorCode { get; }
    public OwnedLabException(string errorCode) : base(Codes.Contains(errorCode) ? errorCode : "DriverFailure") => ErrorCode = Message;
}

/// <summary>A pre-session failure reported only after actual owned process, job and IO retirement.</summary>
public sealed class OwnedLaunchException(string errorCode, OwnedRetirement retirement) : OwnedLabException(errorCode)
{
    public OwnedRetirement Retirement { get; } = retirement;
}

/// <summary>A session failure reported only after actual owned process, job, IO and callback retirement.</summary>
public sealed class OwnedRetirementException(string errorCode, OwnedRetirement retirement) : OwnedLabException(errorCode)
{
    public OwnedRetirement Retirement { get; } = retirement;
}
