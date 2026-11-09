using System.Buffers.Binary;
using Workbench.Sdk;
using Workbench.Testing.Domain.OwnedLab;

namespace Workbench.Core.Tests;

internal sealed class OwnedLabTestRig : IDisposable
{
    private readonly TestDirectory _directory = new();
    internal OwnedScope Scope { get; }
    internal ScriptedDriver Driver { get; } = new();
    internal TestClock Clock { get; } = new();
    internal string Root => Path.Combine(Scope.DataDirectory, "owned-lab-v1");
    internal OwnedLabTestRig() => Scope = new("project", "com.projectworkbench.testing", _directory.User);
    internal OwnedPilotController Controller(OwnedAtomicFiles? files = null) => files is null ? new(Scope, Driver, Clock) : new(Scope, Driver, Clock, files);
    public void Dispose() => _directory.Dispose();

    internal static byte[] Bmp()
    {
        var bytes = new byte[172854]; bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        void I32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);
        I32(2, 172854); I32(10, 54); I32(14, 40); I32(18, 320); I32(22, 180); I32(34, 172800);
        bytes[26] = 1; bytes[28] = 24;
        for (var y = 152; y < 164; y++) for (var x = 280; x < 308; x++)
        {
            if (x is >= 292 and < 296) continue;
            var pixel = 54 + (179 - y) * 960 + x * 3;
            (x < 292 ? new byte[] { 113, 57, 17 } : new byte[] { 31, 73, 149 }).CopyTo(bytes, pixel);
        }
        return bytes;
    }
}

internal sealed class AsyncGate
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal async Task HoldAsync() { Entered.TrySetResult(); await Released.Task; }
    internal void Release() => Released.TrySetResult();
}

internal sealed class ScriptedDriver : IOwnedDriverFactory, IOwnedSession
{
    internal int PrepareCount, LaunchCount, ResumeCount, ActionCalls, RetireCount, PreparationDisposals, SessionDisposals;
    internal AsyncGate? PreparationGate, LaunchGate = null, ActionGate, RetirementGate, CallbackGate;
    internal bool ThrowPreparation, ThrowRetirement = false, UnknownLaunchFailure = false;
    internal Exception? RetirementFailure;
    internal bool ThrowSessionDisposal, ThrowPreparationDisposal;
    internal int InvalidLaunchReceipt = 0;
    internal int ObservationFault = 0;
    internal OwnedRetirement Retirement = new(true, 0, 0, 0);
    internal OwnedGrant? Grant;
    internal byte[]? FirstBmp;
    internal OwnedHelperIdentity CurrentHelper = new(new('1', 64), new('2', 64), new('3', 40), "scripted-msvc", "scripted-sdk", "AcceptanceFixture");
    private OwnedState _state = new("", "", "", "0");
    private CancellationTokenRegistration _registration;
    public OwnedHelperIdentity Helper => CurrentHelper;
    public OwnedIdentity Identity => new(10, 100, 20, 200, 300, CurrentHelper.ImageSha);
    public OwnedState InitialState => _state;
    public async ValueTask<IOwnedPreparation> PrepareAsync(CancellationToken cancellationToken)
    {
        PrepareCount++; if (PreparationGate is not null) await PreparationGate.HoldAsync();
        if (ThrowPreparation) throw new IOException("private scripted failure");
        return new Prepared(this);
    }
    public async ValueTask<IOwnedSession> LaunchAsync(OwnedGrant grant, CancellationToken revocation)
    {
        LaunchCount++; Grant = grant; _state = new("", "", "", "0");
        if (CallbackGate is not null) _registration = revocation.Register(() => { CallbackGate.Entered.TrySetResult(); CallbackGate.Released.Task.GetAwaiter().GetResult(); throw new InvalidOperationException("callback"); });
        if (LaunchGate is not null) await LaunchGate.HoldAsync();
        if (InvalidLaunchReceipt != 0) throw new OwnedLaunchException("DriverFailure", InvalidLaunchReceipt == 1 ? new(true, 0, null, 1) : null!);
        if (UnknownLaunchFailure) throw new IOException("No retirement receipt");
        if (!grant.LaunchGate.TryResume(() => ResumeCount++)) throw new OwnedLaunchException("Cancelled", new(true, -1, null, 0));
        return this;
    }
    public async ValueTask<OwnedObservation> ExecuteAsync(TestAction action, CancellationToken cancellationToken)
    {
        ActionCalls++;
        if (ActionGate is not null) { var gate = ActionGate; ActionGate = null; await gate.HoldAsync(); }
        if (action.Kind == TestActionKind.SetText) _state = _state with { Title = action.Value!, Toast = "", Validation = "" };
        else if (action.Kind == TestActionKind.Click)
            _state = _state.Title.Length == 0 ? _state with { Validation = "Title required", Toast = "" } : _state with { Toast = "Saved", Validation = "", CreatedCount = "1" };
        if (ObservationFault == 1) throw new OwnedLabException("DriverFailure");
        var identity = ObservationFault == 2 ? Identity with { TargetPid = 21 } : Identity;
        var bmp = OwnedLabTestRig.Bmp(); if (ObservationFault == 3) bmp[54 + (179 - 158) * 960 + 286 * 3] = 0;
        if (ActionCalls == 1) FirstBmp = bmp;
        return new(identity, _state, bmp);
    }
    public async ValueTask<OwnedRetirement> RetireAsync(OwnedRetirementReason reason)
    {
        RetireCount++; if (RetirementGate is not null) await RetirementGate.HoldAsync();
        if (RetirementFailure is not null) throw RetirementFailure;
        if (ThrowRetirement) throw new IOException("retirement proof unavailable"); return Retirement;
    }
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        SessionDisposals++; _registration.Dispose();
        if (ThrowSessionDisposal) throw new IOException("unrelated session cleanup failure");
        return ValueTask.CompletedTask;
    }
    private sealed class Prepared(ScriptedDriver owner) : IOwnedPreparation
    {
        public OwnedHelperIdentity Helper => owner.Helper;
        public ValueTask<IOwnedSession> LaunchAsync(OwnedGrant grant, CancellationToken revocation) => owner.LaunchAsync(grant, revocation);
        public ValueTask DisposeAsync() { owner.PreparationDisposals++; owner._registration.Dispose(); if (owner.ThrowPreparationDisposal) throw new IOException("unrelated preparation cleanup failure"); return ValueTask.CompletedTask; }
    }
}

internal sealed class PublicationFiles : OwnedAtomicFiles
{
    internal Func<string, bool>? Match;
    internal AsyncGate? Before, After;
    internal bool ThrowBefore, ThrowAfter;
    internal readonly List<string> Published = [];
    internal override async Task PublishNewAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        var cut = Match?.Invoke(path) == true;
        if (cut && Before is not null) await Before.HoldAsync();
        if (cut && ThrowBefore) throw new IOException("before publication");
        await base.PublishNewAsync(path, bytes); Published.Add(path);
        if (cut && After is not null) await After.HoldAsync();
        if (cut && ThrowAfter) throw new IOException("after publication");
    }
}

internal sealed class TestClock : TimeProvider
{
    private long _ticks;
    private int _timerCount;
    private readonly List<ClockTimer> _timers = [];
    internal bool DeliverTimerCallbacks = true;
    internal AsyncGate? FirstCommandDisposalGate;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var disposalGate = Interlocked.Increment(ref _timerCount) == 2 ? FirstCommandDisposalGate : null;
        var timer = new ClockTimer(this, callback, state, disposalGate); lock (_timers) _timers.Add(timer); timer.Change(dueTime, period); return timer;
    }
    internal void Advance(TimeSpan span)
    {
        Interlocked.Add(ref _ticks, span.Ticks); ClockTimer[] timers; lock (_timers) timers = _timers.ToArray();
        if (DeliverTimerCallbacks) foreach (var timer in timers) timer.Fire();
    }
    private sealed class ClockTimer(TestClock clock, TimerCallback callback, object? state, AsyncGate? disposalGate) : ITimer
    {
        private long _due = long.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period) { _due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks; return true; }
        internal void Fire() { if (clock.GetTimestamp() < _due) return; _due = long.MaxValue; callback(state); }
        public void Dispose() => _due = long.MaxValue;
        public async ValueTask DisposeAsync() { Dispose(); if (disposalGate is not null) await disposalGate.HoldAsync(); }
    }
}
