using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Workbench.Sdk;
using Workbench.Testing.Domain.OwnedLab;
#if OWNED_LAB_ACCEPTANCE_FIXTURE
namespace Workbench.OwnedLab.FixtureLauncher;
#else
namespace Workbench.Testing.Windows;
#endif
using Native = OwnedNativeMethods;

internal sealed class OwnedProcessSession : IOwnedSession
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _io = [];
    private readonly OwnedScope _scope;
    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly OwnedHelperIdentity _helper;
    private readonly OwnedGrant _grant;
    private readonly CancellationToken _revocation;
    private readonly Action _created;
    private readonly Action<OwnedNativeReceipt> _record;
    private Native.KernelHandle? _job, _port, _broker, _thread, _target;
    private NamedPipeServerStream? _tx, _rx;
    private CancellationTokenRegistration _registration;
    private ITimer? _deadline;
    private Task? _operation;
    private Task<OwnedRetirement>? _retirement;
    private OwnedRetirementException? _retirementFailure;
    private Task? _disposal;
    private volatile bool _aborted;
    private volatile bool _brokerInJob;
    private bool _targetReported, _callbacksJoined, _wasResumed;
    private uint _brokerPid, _sequence;
    private ulong _brokerBirth;
    private OwnedIdentity? _identity;
    private OwnedState? _initial;
#if OWNED_LAB_ACCEPTANCE_FIXTURE
    private readonly NativeFixture _fixture;
#endif
    public OwnedIdentity Identity => _identity is { } identity ? identity with { } : throw new OwnedLabException("TargetUnavailable");
    public OwnedState InitialState => _initial is { } state ? state with { } : throw new OwnedLabException("TargetUnavailable");

    private OwnedProcessSession(OwnedScope scope, TimeProvider time, string path, FileStream lease, OwnedHelperIdentity helper,
        OwnedGrant grant, CancellationToken revocation, Action created, Action<OwnedNativeReceipt> record
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        , NativeFixture fixture
#endif
        )
    {
        _scope = scope; _time = time; _path = path; _lease = lease; _helper = helper; _grant = grant; _revocation = revocation; _created = created; _record = record;
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        _fixture = fixture;
#endif
    }

    internal static async Task<IOwnedSession> LaunchAsync(OwnedScope scope, TimeProvider time, string path, FileStream lease,
        OwnedHelperIdentity helper, OwnedGrant grant, CancellationToken revocation, Action created, Action<OwnedNativeReceipt> record
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        , NativeFixture fixture
#endif
        )
    {
        var session = new OwnedProcessSession(scope, time, path, lease, helper, grant, revocation, created, record
#if OWNED_LAB_ACCEPTANCE_FIXTURE
            , fixture
#endif
            );
        try
        {
            // Retain the real creation task before it can yield or create a suspended process.
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session._operation = session.InitializeAsync(start.Task); start.SetResult();
            await session._operation.ConfigureAwait(false); return session;
        }
        catch (Exception error)
        {
            var code = error is OwnedLabException owned ? owned.ErrorCode : revocation.IsCancellationRequested ? "Cancelled" : "DriverFailure";
            // No typed launch failure exists until actual creation, IO, callbacks and job retirement finish.
            OwnedRetirement receipt;
            try { receipt = await session.RetireAsync(OwnedRetirementReason.Failed).ConfigureAwait(false); }
            finally { await session.DisposeAsync().ConfigureAwait(false); }
            throw new OwnedLaunchException(code, receipt);
        }
    }

    private TimeSpan Remaining()
    {
        var now = _time.GetTimestamp();
        if (now >= _grant.DeadlineTimestamp || _aborted || _revocation.IsCancellationRequested) throw new OwnedLabException(_revocation.IsCancellationRequested ? "Cancelled" : "Expired");
        return _time.GetElapsedTime(now, _grant.DeadlineTimestamp);
    }
    private async Task InitializeAsync(Task admission)
    {
        await admission.ConfigureAwait(false);
        if (_grant.Execution.Helper != _helper || _grant.Execution.ProjectId != _scope.ProjectId || _grant.Execution.PluginId != _scope.PluginId
            || _grant.Execution.TargetId != FixedOwnedPlans.TargetId || _grant.Execution.Protocol != 1 || _grant.ExecutionSha != Convert.ToHexStringLower(SHA256.HashData(_grant.ExecutionBytes.Span))) throw new OwnedLabException("ProtocolViolation");
        var remaining = Remaining();
        _registration = _revocation.UnsafeRegister(static state => ((OwnedProcessSession)state!).Abort(), this);
        _deadline = _time.CreateTimer(static state => ((OwnedProcessSession)state!).Abort(), this, remaining, Timeout.InfiniteTimeSpan);
        _job = Native.CreateJobObjectW(0, null); if (_job.IsInvalid) throw new OwnedLabException("DriverFailure");
        _port = Native.CreateIoCompletionPort(-1, 0, 0, 1); if (_port.IsInvalid) throw new OwnedLabException("DriverFailure");
        Native.ConfigureJob(_job, _port);
        var transmit = await Native.PipeAsync(hostWrites: true).ConfigureAwait(false); _tx = transmit.Host;
        using var input = transmit.Child;
        var receive = await Native.PipeAsync(hostWrites: false).ConfigureAwait(false); _rx = receive.Host;
        using var output = receive.Child;
        using var error = Native.Null(write: true);
        // Slow suspended creation is actual retained work outside the launch-gate lock.
        await Task.Run(() => CreateSuspended(input, output, error)).ConfigureAwait(false);
        input.Dispose(); output.Dispose(); error.Dispose(); // only the child's three copies survive creation.
        _created();
        if (_aborted || ! _grant.LaunchGate.TryResume(() =>
        {
            var prior = Native.ResumeThread(_thread!);
            if (prior == uint.MaxValue) throw new OwnedLabException("DriverFailure");
            _wasResumed = true;
            if (prior != 1) throw new OwnedLabException("DriverFailure");
        })) { Abort(); throw new OwnedLabException("Cancelled"); }
        var timer = _time.CreateTimer(static state => ((OwnedProcessSession)state!).Abort(), this, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        try
        {
            var response = await ExchangeAsync(OwnedWireKind.Init, InitBody()).ConfigureAwait(false);
            var (identity, state) = OwnedWireCodec.ReadReady(response);
            if (identity.BrokerPid != _brokerPid || identity.BrokerCreationFileTime != _brokerBirth || identity.ImageSha != _helper.ImageSha) throw new OwnedLabException("ProtocolViolation");
            _identity = identity with { };
            Native.KernelHandle? candidate = Native.OpenProcess(Native.QueryLimited | Native.Synchronize, false, identity.TargetPid);
            try
            {
                // A reported numeric PID is untrusted until its birth/image/job membership match.
                // Never publish or join a recycled/unowned process handle during retirement.
                if (candidate.IsInvalid || Native.GetProcessId(candidate) != identity.TargetPid || Native.Birth(candidate) != identity.TargetCreationFileTime
                    || !StringComparer.OrdinalIgnoreCase.Equals(Native.ProcessPath(candidate), _path)) throw new OwnedLabException("TargetUnavailable");
                Native.Check(Native.IsProcessInJob(candidate, _job!, out var owned));
                if (!owned) throw new OwnedLabException("TargetUnavailable");
                _target = candidate; candidate = null; _targetReported = true;
            }
            finally { candidate?.Dispose(); }
            await VerifyIdentityAsync().ConfigureAwait(false);
            if (state != new OwnedState("", "", "", "0")) throw new OwnedLabException("ProtocolViolation");
            _initial = state with { };
        }
        finally { await timer.DisposeAsync().ConfigureAwait(false); }
    }
    private void CreateSuspended(SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        Remaining();
        using var attributes = new Native.Attributes(_job!, input.DangerousGetHandle(), output.DangerousGetHandle(), error.DangerousGetHandle());
        var startup = new Native.StartupEx { Startup = new Native.Startup { Size = (uint)Marshal.SizeOf<Native.StartupEx>(), Flags = 0x100,
            Input = input.DangerousGetHandle(), Output = output.DangerousGetHandle(), Error = error.DangerousGetHandle() }, Attributes = attributes.List };
        var directory = Native.WindowsDirectory();
        var environment = Marshal.StringToHGlobalUni($"SystemRoot={directory}\0WINDIR={directory}\0\0");
        try
        {
            Native.Check(Native.CreateProcessW(_path, new StringBuilder($"\"{_path}\" --broker"), 0, 0, true, 0x00080000 | 0x00000400 | 0x00000004,
                environment, directory, ref startup, out var info));
            // Publish all actual handles before gate/resume; the job was attached at creation.
            _broker = new(info.Process); _thread = new(info.Thread); _brokerPid = info.Pid; _brokerBirth = Native.Birth(_broker);
            if (Native.GetProcessId(_broker) != _brokerPid || !StringComparer.OrdinalIgnoreCase.Equals(Native.ProcessPath(_broker), _path)) throw new OwnedLabException("TargetUnavailable");
            Native.Check(Native.IsProcessInJob(_broker, _job!, out var owned)); if (!owned) throw new OwnedLabException("TargetUnavailable"); _brokerInJob = true;
            if (_aborted) Abort();
        }
        finally { Marshal.FreeHGlobal(environment); }
    }
    private byte[] InitBody()
    {
        using var bytes = new MemoryStream(); using var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true);
        static void Text(BinaryWriter writer, string value) { var encoded = new UTF8Encoding(false, true).GetBytes(value); writer.Write((uint)encoded.Length); writer.Write(encoded); }
        writer.Write(Encoding.ASCII.GetBytes(_grant.Execution.RunId)); Text(writer, _scope.ProjectId); Text(writer, _scope.PluginId); Text(writer, FixedOwnedPlans.TargetId);
        writer.Write(Convert.FromHexString(_grant.Execution.InputSha)); writer.Write(Convert.FromHexString(_grant.ExecutionSha)); writer.Write(Convert.FromHexString(_helper.ImageSha));
        writer.Write(_grant.Execution.ExpiresUtc.ToUnixTimeMilliseconds()); writer.Write((uint)Math.Clamp(Math.Floor(Remaining().TotalMilliseconds), 1, 60000));
        return bytes.ToArray();
    }
    private void Abort()
    {
        _grant.LaunchGate.Revoke(); _aborted = true;
        // Only finite OS cancellation requests run in callbacks. Retirement owns their completion.
        if (_job is { IsInvalid: false, IsClosed: false } job) Native.TerminateJobObject(job, 0x20000001);
        // This exact handle came from our suspended CreateProcess, never a reported numeric PID.
        // If containment verification itself failed, retire that still-owned broker handle too.
        if (!_brokerInJob && _broker is { IsInvalid: false, IsClosed: false } broker) Native.TerminateProcess(broker, 0x20000001);
        static void Cancel(NamedPipeServerStream? pipe) { try { if (pipe is not null && !pipe.SafePipeHandle.IsClosed) Native.CancelIoEx(pipe.SafePipeHandle, 0); } catch (ObjectDisposedException) { /* Already closed IO is still joined by its owner. */ } }
        Cancel(_tx); Cancel(_rx);
    }
    private async Task TrackAsync(Task actual)
    {
        lock (_io) _io.Add(actual);
        try { await actual.ConfigureAwait(false); }
        finally { lock (_io) _io.Remove(actual); }
    }
    private async Task ExactAsync(Memory<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var actual = _rx!.ReadAsync(bytes, CancellationToken.None).AsTask();
            await TrackAsync(actual).ConfigureAwait(false); var count = actual.Result;
            if (count == 0) throw new OwnedLabException("ProtocolViolation"); bytes = bytes[count..];
        }
    }
    private async Task<OwnedPacket> ExchangeAsync(OwnedWireKind kind, byte[] body)
    {
        Remaining();
        if (_sequence == uint.MaxValue) throw new OwnedLabException("ProtocolViolation");
        var sequence = ++_sequence;
        var frame = OwnedWireCodec.Encode(new(kind, sequence, _grant.Execution.SessionId, body));
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        // Compiled only in the separately identified fixture launcher, never the shipping DLL.
        var truncate = false;
        if (kind == OwnedWireKind.Init)
        {
            if (_fixture == NativeFixture.PartialHeader) { frame = frame[..51]; truncate = true; }
            else if (_fixture == NativeFixture.TruncatedInit) { BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), (uint)body.Length + 1); truncate = true; }
            else if (_fixture == NativeFixture.ExtraInit) { frame = [.. frame, 0]; BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), (uint)body.Length + 1); }
            else if (_fixture == NativeFixture.OversizeInit) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(16), OwnedWireCodec.MaximumBodyLength + 1);
        }
        else if (kind == OwnedWireKind.SetTitle)
        {
            if (_fixture == NativeFixture.CrossSessionAction) frame[20] = frame[20] == (byte)'0' ? (byte)'1' : (byte)'0';
            else if (_fixture == NativeFixture.OutOfOrderAction) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), sequence + 1);
        }
#endif
        await TrackAsync(_tx!.WriteAsync(frame, CancellationToken.None).AsTask()).ConfigureAwait(false);
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        if (truncate) { var closed = _tx; _tx = null; closed!.Dispose(); }
#endif
        var header = new byte[OwnedWireCodec.HeaderLength]; await ExactAsync(header).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var responseKind = (OwnedWireKind)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        var expected = kind == OwnedWireKind.Init ? OwnedWireKind.Ready : kind == OwnedWireKind.Close ? OwnedWireKind.Closed : OwnedWireKind.Result;
        if (!header.AsSpan(0, 8).SequenceEqual("PWB-LAB1"u8) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != sequence || length > OwnedWireCodec.MaximumBodyLength
            || !header.AsSpan(20, 32).SequenceEqual(Encoding.ASCII.GetBytes(_grant.Execution.SessionId)) || responseKind != expected && responseKind != OwnedWireKind.Error) throw new OwnedLabException("ProtocolViolation");
        var response = new byte[OwnedWireCodec.HeaderLength + (int)length]; header.CopyTo(response, 0);
        await ExactAsync(response.AsMemory(OwnedWireCodec.HeaderLength)).ConfigureAwait(false);
#if OWNED_LAB_ACCEPTANCE_FIXTURE
        if (_fixture == NativeFixture.InvalidClosedZeroExit && kind == OwnedWireKind.Close)
        {
            // The separate fault launcher lets its retained broker actually exit before shared
            // decoding rejects malformed Closed. Abort therefore cannot change either zero exit.
            await Task.Run(() => Native.Check(Native.WaitForSingleObject(_broker!, Native.Infinite) == 0)).ConfigureAwait(false);
        }
#endif
        var packet = OwnedWireCodec.Decode(response, _grant.Execution.SessionId, sequence);
        if (packet.Kind == OwnedWireKind.Error) throw new OwnedLabException(OwnedWireCodec.ReadError(packet, kind));
        return packet;
    }
    private async Task VerifyIdentityAsync()
    {
        var identity = Identity;
        static void Process(Native.KernelHandle? process, uint pid, ulong birth, string path)
        {
            if (process is null || process.IsInvalid || Native.GetProcessId(process) != pid || Native.WaitForSingleObject(process, 0) != 258 || Native.Birth(process) != birth
                || !StringComparer.OrdinalIgnoreCase.Equals(Native.ProcessPath(process), path)) throw new OwnedLabException("TargetUnavailable");
        }
        if (identity.BrokerPid != _brokerPid || identity.BrokerCreationFileTime != _brokerBirth || identity.ImageSha != _helper.ImageSha) throw new OwnedLabException("ProtocolViolation");
        Process(_broker, identity.BrokerPid, identity.BrokerCreationFileTime, _path); Process(_target, identity.TargetPid, identity.TargetCreationFileTime, _path);
        Native.Check(Native.IsProcessInJob(_target!, _job!, out var inJob)); if (!inJob) throw new OwnedLabException("TargetUnavailable");
        Native.Check(Native.IsProcessInJob(_broker!, _job!, out inJob)); if (!inJob) throw new OwnedLabException("TargetUnavailable");
        var window = checked((nint)identity.Hwnd); var name = new StringBuilder(64);
        if (Native.GetWindowThreadProcessId(window, out var pid) == 0 || pid != identity.TargetPid || Native.GetClassNameW(window, name, 64) == 0 || name.ToString() != "PWB.OwnedLab.TaskForm.v1"
            || !Native.GetClientRect(window, out var client) || client.Left != 0 || client.Top != 0 || client.Right != 320 || client.Bottom != 180) throw new OwnedLabException("TargetUnavailable");
        for (var id = 1001; id <= 1005; id++)
        {
            var control = Native.GetDlgItem(window, id);
            if (control == 0 || Native.GetWindowThreadProcessId(control, out pid) == 0 || pid != identity.TargetPid || Native.GetParent(control) != window) throw new OwnedLabException("TargetUnavailable");
        }
        _lease.Position = 0;
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(_lease, CancellationToken.None).ConfigureAwait(false)) != _helper.ImageSha) throw new OwnedLabException("TargetUnavailable");
    }
    public ValueTask<OwnedObservation> ExecuteAsync(TestAction action, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_retirement is not null || _operation is { IsCompleted: false }) throw new OwnedLabException("AdmissionClosed");
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var actual = ExecuteCoreAsync(start.Task, action, cancellationToken); _operation = actual; start.SetResult(); return new(actual);
        }
    }
    private async Task<OwnedObservation> ExecuteCoreAsync(Task admission, TestAction action, CancellationToken cancellationToken)
    {
        await admission.ConfigureAwait(false);
        // Fixed action boundary; assertion expectation is never serialized to the native process.
        OwnedWireKind kind; byte[] body = [];
        if (action.Kind == TestActionKind.SetText && action.ElementId == "task.title" && action.Value is not null)
        {
            kind = OwnedWireKind.SetTitle; var text = new UTF8Encoding(false, true).GetBytes(action.Value); body = new byte[4 + text.Length]; BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)text.Length); text.CopyTo(body, 4);
        }
        else if (action.Kind == TestActionKind.Click && action.ElementId == "task.save" && action.Value is null) kind = OwnedWireKind.ClickSave;
        else if (action.Kind == TestActionKind.AssertText && action.ElementId is "task.toast" or "task.validation" or "task.createdCount" && action.Value is not null) kind = OwnedWireKind.Observe;
        else throw new OwnedLabException("InvalidPlan");
        // Validate the fixed body before scheduling any native action.
        OwnedWireCodec.Encode(new(kind, 1, _grant.Execution.SessionId, body));
        var remaining = Remaining();
        var timer = _time.CreateTimer(static state => ((OwnedProcessSession)state!).Abort(), this, remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        var registration = cancellationToken.UnsafeRegister(static state => ((OwnedProcessSession)state!).Abort(), this);
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); await VerifyIdentityAsync().ConfigureAwait(false);
            var response = await ExchangeAsync(kind, body).ConfigureAwait(false);
            var observation = OwnedWireCodec.ReadResult(response, kind);
            if (observation.Identity != Identity) throw new OwnedLabException("ProtocolViolation");
            // Broker rechecks its retained process/HWND/control identity immediately before and
            // after capture. A fully received validated result remains usable if cancellation
            // retires those same processes now; a later liveness check would discard that result.
            // Freeze observations before asynchronous timer/registration cleanup can yield.
            return observation with { Identity = observation.Identity with { }, State = observation.State with { }, Bmp = observation.Bmp.ToArray() };
        }
        catch { Abort(); throw; }
        finally { await timer.DisposeAsync().ConfigureAwait(false); await registration.DisposeAsync().ConfigureAwait(false); }
    }
    public ValueTask<OwnedRetirement> RetireAsync(OwnedRetirementReason reason)
    {
        lock (_sync)
        {
            if (_retirement is not null) return new(_retirement);
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = RetireCoreAsync(start.Task, _operation, reason); start.SetResult(); return new(_retirement);
        }
    }
    private async Task<OwnedRetirement> RetireCoreAsync(Task admission, Task? operation, OwnedRetirementReason reason)
    {
        await admission.ConfigureAwait(false);
        string? closeError = null;
        if (reason != OwnedRetirementReason.Completed) Abort();
        if (operation is not null) try { await operation.ConfigureAwait(false); } catch (Exception) { }
        if (reason == OwnedRetirementReason.Completed && !_aborted && _identity is not null)
        {
            var timer = _time.CreateTimer(static state => ((OwnedProcessSession)state!).Abort(), this, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
            try { await VerifyIdentityAsync().ConfigureAwait(false); await ExchangeAsync(OwnedWireKind.Close, []).ConfigureAwait(false); }
            catch (Exception error)
            {
                closeError = error is OwnedLabException owned ? owned.ErrorCode : error is OperationCanceledException ? "Cancelled" : "DriverFailure";
                Abort();
            }
            finally { await timer.DisposeAsync().ConfigureAwait(false); }
        }
        else
        {
            if (reason == OwnedRetirementReason.Completed) closeError = _revocation.IsCancellationRequested ? "Cancelled" : "DriverFailure";
            Abort();
        }
        // OS retirement may exceed the command budget. Failed OS observations retain ownership
        // and repeat the observation; a failed query is never fabricated into zero/exit.
        async Task JoinProcess(Native.KernelHandle process)
        {
            while (await Task.Run(() => Native.WaitForSingleObject(process, Native.Infinite)).ConfigureAwait(false) != 0) { Abort(); await Task.Delay(10).ConfigureAwait(false); }
        }
        if (_broker is not null) await JoinProcess(_broker).ConfigureAwait(false);
        if (_target is { IsInvalid: false }) await JoinProcess(_target).ConfigureAwait(false);
        if (_job is { IsInvalid: false })
            while (true)
            {
                try { if (Native.ActiveProcesses(_job) == 0) break; } catch (System.ComponentModel.Win32Exception) { }
                Abort(); await Task.Delay(10).ConfigureAwait(false);
            }
        if (_tx is not null) Native.CancelIoEx(_tx.SafePipeHandle, 0);
        if (_rx is not null) Native.CancelIoEx(_rx.SafePipeHandle, 0);
        Task[] io; lock (_io) io = _io.ToArray(); try { await Task.WhenAll(io).ConfigureAwait(false); } catch (Exception) { }
        if (_deadline is not null) await _deadline.DisposeAsync().ConfigureAwait(false);
        await _registration.DisposeAsync().ConfigureAwait(false); _callbacksJoined = true;
        int? brokerExit = null, targetExit = null;
        if (_broker is not null) { Native.Check(Native.GetExitCodeProcess(_broker, out var exit)); brokerExit = exit; }
        if (_target is { IsInvalid: false }) { Native.Check(Native.GetExitCodeProcess(_target, out var exit)); targetExit = exit; }
        if (_targetReported && targetExit is null) throw new OwnedLabException("RecoveryNeeded");
        var active = _job is { IsInvalid: false } ? Native.ActiveProcesses(_job) : 0;
        bool allIoJoined; lock (_io) allIoJoined = _io.Count == 0 && io.All(t => t.IsCompleted);
        if (!allIoJoined || active != 0) throw new OwnedLabException("RecoveryNeeded");
        var receipt = new OwnedRetirement(_broker is not null, brokerExit, targetExit, active);
        _record(new(_brokerPid, _brokerBirth, _targetReported ? _identity?.TargetPid : null, _targetReported ? _identity?.TargetCreationFileTime : null, brokerExit, targetExit,
            _broker is not null && Native.WaitForSingleObject(_broker, 0) == 0, _target is { IsInvalid: false } && Native.WaitForSingleObject(_target, 0) == 0, active, allIoJoined, _callbacksJoined, _wasResumed));
        if (closeError is not null)
        {
            // Exact actual receipt has been recorded only after process/job/IO/callback joins.
            _retirementFailure = new(closeError, receipt);
            throw _retirementFailure;
        }
        return receipt;
    }
    public ValueTask DisposeAsync() { lock (_sync) return new(_disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        try { await RetireAsync(OwnedRetirementReason.Failed).ConfigureAwait(false); }
        catch (OwnedRetirementException known) when (ReferenceEquals(known, _retirementFailure))
        {
            // Retire remains repeatably faulted; disposal only releases this fully retired session.
        }
        finally
        {
            // A failed proof still joins actual work; it must never strand handles/leases behind a timeout.
            if (_deadline is not null) await _deadline.DisposeAsync().ConfigureAwait(false);
            await _registration.DisposeAsync().ConfigureAwait(false);
            _tx?.Dispose(); _rx?.Dispose(); _target?.Dispose(); _thread?.Dispose(); _broker?.Dispose(); _job?.Dispose(); _port?.Dispose();
        }
    }
}
