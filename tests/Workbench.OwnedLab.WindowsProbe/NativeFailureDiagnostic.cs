using System.Text.Json;
using Workbench.Testing.Domain.OwnedLab;
using NativeFixture = Workbench.OwnedLab.FixtureLauncher.NativeFixture;
using FixtureReceipt = Workbench.OwnedLab.FixtureLauncher.OwnedNativeReceipt;

namespace Workbench.OwnedLab.WindowsProbe;

internal enum NativeFailurePhase { RunAndStop, ExpectedState, TerminalReceipt, NativeReceipt, OwnerLease, HelperLease, FixturePolicy, Record }

internal static class NativeFailureDiagnostic
{
    // Failure-only data, never a success observation. CreateNew retains the first failed attempt.
    internal static void TryWrite(string output, NativeFixture fixture, bool cancel, bool throwingCallback,
        NativeFailurePhase phase, OwnedRunSnapshot? run, IReadOnlyList<FixtureReceipt> retirements,
        int completedChecks, bool? leaseAvailable, bool? helperLeaseAvailable)
    {
        try
        {
            var terminal = run?.Events.LastOrDefault()?.Retirement;
            var native = retirements.FirstOrDefault();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                kind = "OwnedNativeFailure",
                fixture = Name(fixture), cancel, throwingCallback,
                phase = Name(phase), completedChecks,
                runState = run is null ? null : Name(run.State),
                errorCode = SafeError(run?.ErrorCode),
                eventCount = run?.Events.Count,
                stepResultCount = run?.Events.Count(e => e.Kind == OwnedEventKind.StepResult),
                terminalRetirement = terminal is null ? null : new
                {
                    wasLaunched = terminal.WasLaunched, brokerExitCode = terminal.BrokerExitCode,
                    targetExitCode = terminal.TargetExitCode, activeProcesses = terminal.ActiveProcesses
                },
                nativeRetirementCount = retirements.Count,
                firstNativeRetirement = native is null ? null : new
                {
                    brokerPid = native.BrokerPid, brokerCreationFileTime = native.BrokerCreationFileTime,
                    targetPid = native.TargetPid, targetCreationFileTime = native.TargetCreationFileTime,
                    brokerExitCode = native.BrokerExitCode, targetExitCode = native.TargetExitCode,
                    brokerExited = native.BrokerExited, targetExited = native.TargetExited,
                    activeProcesses = native.ActiveProcesses, allIoJoined = native.AllIoJoined,
                    callbacksJoined = native.CallbacksJoined, wasResumed = native.WasResumed
                },
                leaseAvailable, helperLeaseAvailable
            });
            if (bytes.Length > 65536) return;
            using var file = new FileStream(Path.Combine(output, "native-failure.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(bytes);
        }
        catch (Exception) { /* Diagnostic IO/collection must preserve the original matrix failure. */ }
    }

    private static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static string? SafeError(string? error) => error switch
    {
        null => null,
        "InvalidPlan" or "UnavailableHelper" or "ProtocolViolation" or "TargetUnavailable" or "DriverFailure" or "CaptureFailure"
            or "AssertionFailed" or "Cancelled" or "Expired" or "StoreIoFailure" or "RecoveryNeeded" or "AdmissionClosed" or "StoreLimitExceeded" => error,
        _ => "Other"
    };
}
