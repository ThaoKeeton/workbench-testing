using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Workbench.Testing.Domain.OwnedLab;
#if OWNED_LAB_ACCEPTANCE_FIXTURE
namespace Workbench.OwnedLab.FixtureLauncher;
#else
namespace Workbench.Testing.Windows;
#endif

public sealed class Win32OwnedDriverFactory : IOwnedDriverFactory
{
    private readonly OwnedScope _scope;
    private readonly TimeProvider _time;
    private readonly OwnedDriverDescriptor _descriptor;
    private readonly string _profile;
    internal string HelperPath { get; }
    private readonly List<OwnedNativeReceipt> _retirements = [];
    internal IReadOnlyList<OwnedNativeReceipt> Retirements { get { lock (_retirements) return _retirements.ToArray(); } }
    private readonly TaskCompletionSource _brokerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task BrokerCreated => _brokerCreated.Task;
#if !OWNED_LAB_ACCEPTANCE_FIXTURE
    public Win32OwnedDriverFactory(OwnedScope scope, TimeProvider time)
    {
        _scope = scope with { DataDirectory = Path.GetFullPath(scope.DataDirectory) }; _time = time;
        _descriptor = OwnedDriverDescriptor.Current; _profile = "Production";
        HelperPath = Path.Combine(Path.GetDirectoryName(typeof(Win32OwnedDriverFactory).Assembly.Location)!, "Workbench.OwnedLab.exe");
    }
#else
    private readonly NativeFixture _fixture;
    private readonly TaskCompletionSource _resumeReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void ReleaseSuspendedFixture() => _resumeReleased.TrySetResult();
    private Win32OwnedDriverFactory(OwnedScope scope, TimeProvider time, NativeFixture fixture)
    {
        _scope = scope with { DataDirectory = Path.GetFullPath(scope.DataDirectory) }; _time = time; _profile = "AcceptanceFixture"; _fixture = fixture;
        var directory = Path.Combine(Path.GetDirectoryName(typeof(Win32OwnedDriverFactory).Assembly.Location)!, "native-fixtures", fixture.ToString());
        HelperPath = Path.Combine(directory, "Workbench.OwnedLab.exe");
        _descriptor = System.Text.Json.JsonSerializer.Deserialize<OwnedDriverDescriptor>(File.ReadAllBytes(Path.Combine(directory, "descriptor.json"))) ?? throw new InvalidDataException("Fixture descriptor missing.");
    }
    internal static Win32OwnedDriverFactory ForAcceptanceFixture(OwnedScope scope, TimeProvider time, NativeFixture fixture) => new(scope, time, fixture);
#endif
    private void Created()
    {
        _brokerCreated.TrySetResult();
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        if (_fixture == NativeFixture.SealedResume) _resumeReleased.Task.GetAwaiter().GetResult();
#endif
    }
    public async ValueTask<IOwnedPreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        var identity = _descriptor.Require(_profile);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new OwnedLabException("UnavailableHelper");
        FileStream? lease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(HelperPath);
            lease = new(HelperPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (lease.Length is < 256 or > 16777216) throw new OwnedLabException("UnavailableHelper");
            using (var pe = new PEReader(lease, PEStreamOptions.LeaveOpen))
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 || pe.PEHeaders.PEHeader?.Magic != PEMagic.PE32Plus || pe.PEHeaders.IsDll) throw new OwnedLabException("UnavailableHelper");
            lease.Position = 0;
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false));
            if (actual != identity.ImageSha) throw new OwnedLabException("UnavailableHelper");
            RejectLinks(HelperPath);
            return new Preparation(this, lease, identity);
        }
        catch (OperationCanceledException) { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception) { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); throw new OwnedLabException("UnavailableHelper"); }
    }
    private static void RejectLinks(string path)
    {
        var leaf = true;
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            var attributes = File.GetAttributes(part);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || leaf && (attributes & FileAttributes.Directory) != 0) throw new OwnedLabException("UnavailableHelper");
            leaf = false;
        }
    }
    private sealed class Preparation(Win32OwnedDriverFactory owner, FileStream lease, OwnedHelperIdentity helper) : IOwnedPreparation
    {
        private readonly object _sync = new();
        private Task<IOwnedSession>? _launch;
        private Task? _dispose;
        public OwnedHelperIdentity Helper => helper;
        public ValueTask<IOwnedSession> LaunchAsync(OwnedGrant grant, CancellationToken revocation)
        {
            lock (_sync)
            {
                if (_launch is not null || _dispose is not null) throw new OwnedLabException("AdmissionClosed");
                _launch = OwnedProcessSession.LaunchAsync(owner._scope, owner._time, owner.HelperPath, lease, helper, grant, revocation,
                    owner.Created, receipt => { lock (owner._retirements) owner._retirements.Add(receipt); }
#if OWNED_LAB_ACCEPTANCE_FIXTURE
                    , owner._fixture
#endif
                    );
                return new(_launch);
            }
        }
        public ValueTask DisposeAsync() { lock (_sync) return new(_dispose ??= DisposeCoreAsync()); }
        private async Task DisposeCoreAsync()
        {
            try { if (_launch is not null) { IOwnedSession? session = null; try { session = await _launch.ConfigureAwait(false); } catch (Exception) { } if (session is not null) await session.DisposeAsync().ConfigureAwait(false); } }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
#if OWNED_LAB_ACCEPTANCE_FIXTURE
internal enum NativeFixture { Missing, FakePe, WrongHash, Reparse, UiaBlock, GdiBlock, Hang, Crash, EarlyExit, DpiFailure, BadFrame, ChangedIdentity, StaleReady, NestedJob, SealedResume, PartialHeader, TruncatedInit, ExtraInit, OversizeInit, CrossSessionAction, OutOfOrderAction, InvalidClosedZeroExit }
#endif
