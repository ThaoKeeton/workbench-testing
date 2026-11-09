using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Testing.Domain.OwnedLab;
using Workbench.Testing.Windows;
using FixtureFactory = Workbench.OwnedLab.FixtureLauncher.Win32OwnedDriverFactory;
using NativeFixture = Workbench.OwnedLab.FixtureLauncher.NativeFixture;

namespace Workbench.OwnedLab.WindowsProbe;

internal sealed class NativeChecks(string output, string zipSha)
{
    internal static void FixtureStagingChecks()
    {
        var scope = new OwnedScope("fixture-staging", "com.projectworkbench.testing", AppContext.BaseDirectory);
        foreach (var fixture in Enum.GetValues<NativeFixture>())
        {
            var factory = FixtureFactory.ForAcceptanceFixture(scope, TimeProvider.System, fixture);
            var expected = Path.Combine(AppContext.BaseDirectory, "native-fixtures", fixture.ToString(), "Workbench.OwnedLab.exe");
            Require(factory.HelperPath == expected, "Fixture launcher must resolve its own staged fixture.");
            Require(File.Exists(factory.HelperPath) == (fixture != NativeFixture.Missing), "Staged helper presence must match the closed fixture selector.");
        }
        Require(!typeof(Win32OwnedDriverFactory).Assembly.GetTypes().Any(t => t.Name == "NativeFixture" || t.Namespace == "Workbench.OwnedLab.FixtureLauncher"), "Fixture selectors must be physically absent from the shipping launcher.");
        Console.WriteLine("All 22 separately compiled fixture selectors resolved staged descriptors/helpers; shipping launcher has no fixture selector. Data only, no native execution.");
    }

    internal static void ScenarioPolicyChecks()
    {
        static OwnedNativeReceipt Observed(int exit) => new(10, 100, 20, 200, exit, exit, true, true, 0, true, true, true);
        var clean = new OwnedRetirement(true, 0, 0, 0); var aborted = new OwnedRetirement(true, 0x20000001, 0x20000001, 0);
        ValidateScenarioRetirement(OwnedScenario.Positive, OwnedRunState.Passed, null, 4, clean, Observed(0));
        ValidateScenarioRetirement(OwnedScenario.EmptyTitle, OwnedRunState.Passed, null, 4, clean, Observed(0));
        ValidateScenarioRetirement(OwnedScenario.AssertionFailure, OwnedRunState.Failed, "AssertionFailed", 3, aborted, Observed(0x20000001));
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidDataException("Scenario policy accepted invalid result."); }
        Reject(() => ValidateScenarioRetirement(OwnedScenario.Positive, OwnedRunState.Passed, null, 4, aborted, Observed(0x20000001)));
        Reject(() => ValidateScenarioRetirement(OwnedScenario.AssertionFailure, OwnedRunState.Passed, null, 3, clean, Observed(0)));
        Reject(() => ValidateScenarioRetirement(OwnedScenario.AssertionFailure, OwnedRunState.Failed, "DriverFailure", 3, aborted, Observed(0x20000001)));
        Reject(() => ValidateScenarioRetirement(OwnedScenario.AssertionFailure, OwnedRunState.Failed, "AssertionFailed", 4, aborted, Observed(0x20000001)));
        Reject(() => ValidateScenarioRetirement(OwnedScenario.AssertionFailure, OwnedRunState.Failed, "AssertionFailed", 3, clean, Observed(0x20000001)));
        Console.WriteLine("Scenario retirement policy checks passed; data-only checks, no native execution.");
    }
    internal static async Task ReferenceOnlyAsync()
    {
        var descriptor = OwnedDriverDescriptor.Current;
        Require(descriptor is { Available: false, Profile: "ReferenceOnly", ImageSha: null, NativeSourceSha: null, SourceCommit: null, Msvc: null, Sdk: null }, "Reference-only descriptor must be explicitly unavailable.");
        var scope = new OwnedScope("reference", "com.projectworkbench.testing", Path.Combine(Path.GetTempPath(), "PWB-owned-reference-" + Guid.NewGuid().ToString("N")));
        var factory = new Win32OwnedDriverFactory(scope, TimeProvider.System); var controller = new OwnedPilotController(scope, factory, TimeProvider.System);
        try
        {
            await controller.InitializeAsync();
            try { await controller.RunAsync(OwnedScenario.Positive); throw new InvalidDataException("Reference-only helper admitted."); }
            catch (OwnedLabException error) { Require(error.ErrorCode == "UnavailableHelper", "Reference-only safe error"); }
            Require(controller.Snapshot.Runs.Count == 0 && factory.Retirements.Count == 0, "Reference-only refused before Queued/process creation.");
        }
        finally { await controller.StopAsync(); Directory.Delete(scope.DataDirectory, true); }
        Require(typeof(Win32OwnedDriverFactory).Assembly.GetType("Workbench.Testing.Windows.NativeFixture") is null, "Production assembly contains fixture selector.");
        Console.WriteLine("ReferenceOnly unavailable descriptor/refusal before queue/process and shipping fixture-selector absence passed; no native execution or ZIP.");
    }
    internal List<object> Results { get; } = [];
    private static void Require(bool value, string requirement) { if (!value) throw new InvalidDataException(requirement); }
    private void Record(object result) { Results.Add(result); Console.WriteLine(JsonSerializer.Serialize(result)); }
    private OwnedScope Scope(string name) => new(Guid.NewGuid().ToString("N"), "com.projectworkbench.testing", Path.Combine(output, name));

    internal async Task RunAsync()
    {
        await HelperIdentityAsync();
        foreach (var scenario in new[] { OwnedScenario.Positive, OwnedScenario.EmptyTitle, OwnedScenario.AssertionFailure }) await ScenarioAsync(scenario);
        await InvalidClosedZeroExitAsync();
        foreach (var fixture in new[] { NativeFixture.UiaBlock, NativeFixture.GdiBlock, NativeFixture.Hang, NativeFixture.Crash,
            NativeFixture.EarlyExit, NativeFixture.DpiFailure, NativeFixture.BadFrame, NativeFixture.ChangedIdentity, NativeFixture.StaleReady,
            NativeFixture.PartialHeader, NativeFixture.TruncatedInit, NativeFixture.ExtraInit, NativeFixture.OversizeInit, NativeFixture.CrossSessionAction, NativeFixture.OutOfOrderAction }) await FailureAsync(fixture, false, false);
        await FailureAsync(NativeFixture.Hang, true, false);
        await FailureAsync(NativeFixture.Hang, true, true);
        await SealedResumeAsync();
        await NestedJobAsync();
    }

    private async Task HelperIdentityAsync()
    {
        foreach (var fixture in new[] { NativeFixture.Missing, NativeFixture.FakePe, NativeFixture.WrongHash, NativeFixture.Reparse })
        {
            var scope = Scope(fixture.ToString());
            var factory = FixtureFactory.ForAcceptanceFixture(scope, TimeProvider.System, fixture);
            var controller = new OwnedPilotController(scope, factory, TimeProvider.System);
            await controller.InitializeAsync();
            try { await controller.RunAsync(OwnedScenario.Positive); throw new InvalidDataException("HelperIdentity accepted invalid helper."); }
            catch (OwnedLabException e) { Require(e.ErrorCode == "UnavailableHelper", "HelperIdentity safe code"); }
            finally { await controller.StopAsync(); }
            Require(controller.Snapshot.Runs.Count == 0 && factory.Retirements.Count == 0, "HelperIdentity must fail before Queued/process creation.");
            Record(new { check = "HelperIdentity", fixture = fixture.ToString(), succeeded = true, queued = 0, created = 0, zipSha });
        }
    }

    private async Task ScenarioAsync(OwnedScenario scenario)
    {
        var scope = Scope(scenario.ToString());
        var factory = new Win32OwnedDriverFactory(scope, TimeProvider.System);
        var controller = new OwnedPilotController(scope, factory, TimeProvider.System);
        OwnedRunSnapshot run;
        try
        {
            await controller.InitializeAsync();
            run = await controller.RunAsync(scenario);
            var steps = run.Events.Where(e => e.Kind == OwnedEventKind.StepResult).ToArray();
            Require(steps.Length == (scenario == OwnedScenario.AssertionFailure ? 3 : 4), "PositiveAndNegativeFrames 4/4/3");
            Require(run.State == (scenario == OwnedScenario.AssertionFailure ? OwnedRunState.Failed : OwnedRunState.Passed), "Actual native scenario outcome");
            Require(steps[^1].State == (scenario == OwnedScenario.EmptyTitle ? new OwnedState("", "", "Title required", "0") : new OwnedState("Owned pilot task", "Saved", "", "1")), "Actual raw UIA state");
            foreach (var step in steps)
            {
                var frame = await controller.ReadFrameAsync(run.Execution.RunId, step.Sequence);
                Require(frame.Length == 172854 && step.Identity is { TargetPid: > 0 }, "Actual identity and BMP length");
                Require(step.FrameSha == Convert.ToHexStringLower(SHA256.HashData(frame)), "Exact retained BMP hash");
                // Domain rechecks header and both calibration centers during journal read.
            }
        }
        finally { await controller.StopAsync(); }
        using (File.Open(Path.Combine(scope.DataDirectory, "owned-lab-v1", "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(factory.HelperPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        var retirement = factory.Retirements.Single(); CheckReceipt(retirement, requireTarget: true);
        ValidateScenarioRetirement(scenario, run.State, run.ErrorCode, results: run.Events.Count(e => e.Kind == OwnedEventKind.StepResult), run.Events[^1].Retirement!, retirement);
        var results = run.Events.Where(e => e.Kind == OwnedEventKind.StepResult).Select(e => new { e.Sequence, e.Outcome, e.State, e.Identity, e.FrameSha, frameLength = 172854, calibration = true }).ToArray();
        Record(new { check = "PositiveAndNegativeFrames", scenario = scenario.ToString(), succeeded = true, run.Execution.RunId, run.Execution.SessionId, run.Execution.InputSha, run.ExecutionSha, run.Execution.Helper, run.State, retirement, allIoJoined = retirement.AllIoJoined, callbacksJoined = retirement.CallbacksJoined, leaseAvailable = true, zipSha, results });
    }

    private static void CheckReceipt(OwnedNativeReceipt receipt, bool requireTarget)
    {
        Require(receipt.ActiveProcesses == 0 && receipt.BrokerExited && receipt.AllIoJoined && receipt.CallbacksJoined, "NativeFailureMatrix actual job0/process/IO/callback retirement");
        Require(receipt.BrokerPid > 0 && receipt.BrokerCreationFileTime > 0, "Actual broker creation identity");
        if (requireTarget) Require(receipt.TargetPid > 0 && receipt.TargetCreationFileTime > 0 && receipt.TargetExited, "Reported actual target exit");
    }

    private static void ValidateScenarioRetirement(OwnedScenario scenario, OwnedRunState state, string? errorCode, int results, OwnedRetirement receipt, OwnedNativeReceipt observed)
    {
        CheckReceipt(observed, requireTarget: true);
        Require(receipt is { WasLaunched: true, BrokerExitCode: not null, TargetExitCode: not null, ActiveProcesses: 0 }
            && receipt.BrokerExitCode == observed.BrokerExitCode && receipt.TargetExitCode == observed.TargetExitCode, "Scenario must retain the exact observed joined process exits.");
        if (scenario == OwnedScenario.AssertionFailure)
            Require(state == OwnedRunState.Failed && errorCode == "AssertionFailed" && results == 3, "Deliberate assertion failure keeps three results and actual failure-policy retirement.");
        else
            Require(scenario is OwnedScenario.Positive or OwnedScenario.EmptyTitle && state == OwnedRunState.Passed && errorCode is null && results == 4
                && receipt is { BrokerExitCode: 0, TargetExitCode: 0 }, "Passing scenario requires normal Close and actual clean exits.");
    }

    private async Task FailureAsync(NativeFixture fixture, bool cancel, bool throwingCallback)
    {
        var scope = Scope(fixture + (cancel ? "Cancel" : "Failure") + (throwingCallback ? "Callback" : ""));
        var factory = FixtureFactory.ForAcceptanceFixture(scope, TimeProvider.System, fixture);
        var controller = new OwnedPilotController(scope, factory, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        using var callback = throwingCallback ? cancellation.Token.Register(() => throw new InvalidOperationException("Synthetic callback")) : default;
        OwnedRunSnapshot? run = null;
        var phase = NativeFailurePhase.RunAndStop;
        bool? leaseAvailable = null, helperLeaseAvailable = null;
        void PreserveFailure()
        {
            // Even collecting the actual factory snapshot may fail; keep the primary failure.
            try { NativeFailureDiagnostic.TryWrite(output, fixture, cancel, throwingCallback, phase, run, factory.Retirements, Results.Count, leaseAvailable, helperLeaseAvailable); }
            catch (Exception) { }
        }
        try
        {
            await controller.InitializeAsync();
            var work = controller.RunAsync(OwnedScenario.Positive, cancellation.Token);
            if (cancel)
            {
                await factory.BrokerCreated;
                try { cancellation.Cancel(); } catch (AggregateException) { }
            }
            try { run = await work; }
            finally { await controller.StopAsync(); }
            phase = NativeFailurePhase.ExpectedState;
            if (run.State is not (OwnedRunState.Failed or OwnedRunState.Cancelled)) PreserveFailure();
            Require(run.State is OwnedRunState.Failed or OwnedRunState.Cancelled, "NativeFailureMatrix must fail closed with actual retirement");
            phase = NativeFailurePhase.TerminalReceipt;
            Require(run.Events[^1].Retirement is { ActiveProcesses: 0 }, "NativeFailureMatrix terminal receipt");
            phase = NativeFailurePhase.NativeReceipt;
            var fixtureReceipt = factory.Retirements.Single();
            var receipt = new OwnedNativeReceipt(fixtureReceipt.BrokerPid, fixtureReceipt.BrokerCreationFileTime, fixtureReceipt.TargetPid, fixtureReceipt.TargetCreationFileTime,
                fixtureReceipt.BrokerExitCode, fixtureReceipt.TargetExitCode, fixtureReceipt.BrokerExited, fixtureReceipt.TargetExited, fixtureReceipt.ActiveProcesses, fixtureReceipt.AllIoJoined, fixtureReceipt.CallbacksJoined, fixtureReceipt.WasResumed);
            CheckReceipt(receipt, receipt.TargetPid is > 0);
            phase = NativeFailurePhase.OwnerLease; leaseAvailable = false;
            using (File.Open(Path.Combine(scope.DataDirectory, "owned-lab-v1", "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            leaseAvailable = true;
            phase = NativeFailurePhase.HelperLease; helperLeaseAvailable = false;
            using (File.Open(factory.HelperPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            helperLeaseAvailable = true;
            phase = NativeFailurePhase.FixturePolicy;
            var badWire = fixture is NativeFixture.BadFrame or NativeFixture.PartialHeader or NativeFixture.TruncatedInit or NativeFixture.ExtraInit or NativeFixture.OversizeInit or NativeFixture.CrossSessionAction or NativeFixture.OutOfOrderAction;
            if (badWire) Require(run.Events.All(e => e.Kind != OwnedEventKind.StepResult), "Malformed wire must reject before accepting any action result.");
            if (fixture == NativeFixture.StaleReady) Require(receipt.TargetPid is null && run.Events.All(e => e.Kind != OwnedEventKind.Started), "Stale birth must never publish/join an unverified target handle.");
            if (fixture == NativeFixture.GdiBlock) Require(receipt.TargetPid is > 0 && run.Events.Any(e => e.Kind == OwnedEventKind.Started)
                && run.Events.Any(e => e.Kind == OwnedEventKind.StepStarted && e.StepIndex == 0) && run.Events.All(e => e.Kind != OwnedEventKind.StepResult),
                "Synthetic capture-stage hang must retire a verified target during the first step without an accepted result.");
            phase = NativeFailurePhase.Record;
            Record(new { check = badWire ? "NativeBadFrame" : fixture is NativeFixture.ChangedIdentity or NativeFixture.StaleReady ? "ChangedIdentity" : "NativeFailureMatrix", fixture = fixture.ToString(), cancel, throwingCallback, succeeded = true, run.Execution.RunId, run.Execution.SessionId, run.Execution.InputSha, run.ExecutionSha, run.Execution.Helper, run.State, run.ErrorCode, receipt, leaseAvailable = true, zipSha });
        }
        catch
        {
            PreserveFailure();
            throw;
        }
    }

    private async Task NestedJobAsync()
    {
        using var outer = Workbench.OwnedLab.FixtureLauncher.OwnedNativeMethods.EnterOuterProbeJob();
        var scope = Scope("NestedJob"); var factory = FixtureFactory.ForAcceptanceFixture(scope, TimeProvider.System, NativeFixture.NestedJob);
        var controller = new OwnedPilotController(scope, factory, TimeProvider.System);
        OwnedRunSnapshot run;
        try { await controller.InitializeAsync(); run = await controller.RunAsync(OwnedScenario.Positive); }
        finally { await controller.StopAsync(); }
        Require(run.State is OwnedRunState.Passed or OwnedRunState.Failed && run.Events[^1].Retirement is { ActiveProcesses: 0 }, "NestedJob must succeed under containment or fail with actual retirement");
        var receipt = factory.Retirements.Single();
        Require(receipt.ActiveProcesses == 0 && receipt.AllIoJoined && receipt.CallbacksJoined, "Nested job retirement");
        Record(new { check = "NativeFailureMatrix", fixture = "NestedJob", succeeded = true, run.Execution.RunId, run.Execution.SessionId, run.Execution.InputSha, run.ExecutionSha, run.Execution.Helper, run.State, receipt, zipSha });
    }

    private async Task SealedResumeAsync()
    {
        var scope = Scope("SealedResume"); var factory = FixtureFactory.ForAcceptanceFixture(scope, TimeProvider.System, NativeFixture.SealedResume);
        var controller = new OwnedPilotController(scope, factory, TimeProvider.System);
        await controller.InitializeAsync(); var actual = controller.RunAsync(OwnedScenario.Positive);
        await factory.BrokerCreated;
        using var seal = controller.SealAdmission(); factory.ReleaseSuspendedFixture();
        OwnedRunSnapshot run;
        try { run = await actual; }
        finally { factory.ReleaseSuspendedFixture(); await controller.StopAsync(); }
        var receipt = factory.Retirements.Single();
        Require(run.State == OwnedRunState.Cancelled && run.Events.All(e => e.Kind != OwnedEventKind.Started) && receipt.BrokerPid > 0 && receipt.BrokerExited && !receipt.WasResumed && receipt.TargetPid is null && receipt.ActiveProcesses == 0 && receipt.AllIoJoined && receipt.CallbacksJoined, "Actual suspended creation must retire after synchronous seal without ResumeThread");
        using (File.Open(factory.HelperPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Record(new { check = "NativeFailureMatrix", fixture = "SealedResume", succeeded = true, run.Execution.RunId, run.Execution.SessionId, run.Execution.InputSha, run.ExecutionSha, run.Execution.Helper, run.State, receipt, leaseAvailable = true, zipSha });
    }

    private async Task InvalidClosedZeroExitAsync()
    {
        var scope = Scope("InvalidClosedZeroExit");
        var factory = FixtureFactory.ForAcceptanceFixture(scope, TimeProvider.System, NativeFixture.InvalidClosedZeroExit);
        var tracking = new TrackingFactory(factory);
        var controller = new OwnedPilotController(scope, tracking, TimeProvider.System);
        OwnedRunSnapshot run;
        try
        {
            await controller.InitializeAsync(); run = await controller.RunAsync(OwnedScenario.Positive);
            Require(run.State == OwnedRunState.Failed && run.ErrorCode == "ProtocolViolation", "Malformed Closed with actual zero exits must never become Passed.");
            var steps = run.Events.Where(e => e.Kind == OwnedEventKind.StepResult).ToArray();
            Require(steps.Length == 4 && steps.All(e => e.Outcome == OwnedOutcome.Passed), "Close failure must retain all four valid results.");
            foreach (var step in steps) Require((await controller.ReadFrameAsync(run.Execution.RunId, step.Sequence)).Length == 172854, "Close failure lost an actual frame.");
            Require(run.Events[^1].Retirement == new OwnedRetirement(true, 0, 0, 0), "Close failure must preserve actual zero/zero/job0 receipt.");
        }
        finally { await controller.StopAsync(); }
        var session = tracking.Session ?? throw new InvalidDataException("Actual fixture session was not returned.");
        static async Task<OwnedRetirementException> Failure(IOwnedSession actual)
        {
            try { await actual.RetireAsync(OwnedRetirementReason.Completed); }
            catch (OwnedRetirementException error) { return error; }
            throw new InvalidDataException("Repeated retirement erased the known Closed failure.");
        }
        var first = await Failure(session); var second = await Failure(session);
        Require(ReferenceEquals(first, second) && first.ErrorCode == "ProtocolViolation" && first.Retirement == run.Events[^1].Retirement, "Retirement failure/actual receipt must be stable across repeated calls.");
        await session.DisposeAsync(); await session.DisposeAsync();
        var receipt = factory.Retirements.Single();
        Require(receipt.BrokerExitCode == 0 && receipt.TargetExitCode == 0 && receipt.BrokerExited && receipt.TargetExited && receipt.ActiveProcesses == 0 && receipt.AllIoJoined && receipt.CallbacksJoined, "Invalid Closed fixture requires actual clean process exits and all retirement joins.");
        using (File.Open(factory.HelperPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(Path.Combine(scope.DataDirectory, "owned-lab-v1", "owner.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Record(new { check = "NativeFailureMatrix", fixture = "InvalidClosedZeroExit", succeeded = true, run.Execution.RunId, run.Execution.SessionId, run.Execution.InputSha, run.ExecutionSha, run.Execution.Helper, run.State, run.ErrorCode, receipt, repeatedRetirementFailureStable = true, disposalIdempotent = true, retirementReportCount = factory.Retirements.Count, leaseAvailable = true, zipSha });
    }

    private sealed class TrackingFactory(IOwnedDriverFactory inner) : IOwnedDriverFactory
    {
        internal IOwnedSession? Session;
        public async ValueTask<IOwnedPreparation> PrepareAsync(CancellationToken cancellationToken) => new TrackingPreparation(this, await inner.PrepareAsync(cancellationToken));
        private sealed class TrackingPreparation(TrackingFactory owner, IOwnedPreparation inner) : IOwnedPreparation
        {
            public OwnedHelperIdentity Helper => inner.Helper;
            public async ValueTask<IOwnedSession> LaunchAsync(OwnedGrant grant, CancellationToken revocation) => owner.Session = await inner.LaunchAsync(grant, revocation);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
