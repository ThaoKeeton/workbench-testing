using System.Security.Cryptography;
using System.Text.Json;

namespace Workbench.Testing.Domain.OwnedLab;

public sealed class OwnedJournal : IAsyncDisposable
{
    private readonly OwnedScope _scope;
    private readonly string _root;
    private readonly OwnedAtomicFiles _files;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _runs = new(StringComparer.Ordinal);
    private bool _readOnly, _disposed;
    private Task? _disposal;
    private sealed record Entry(OwnedRunSnapshot Snapshot, TimeProvider Clock, bool Restored = false);

    private OwnedJournal(OwnedScope scope, string root, OwnedAtomicFiles files, FileStream lease)
    { _scope = scope; _root = root; _files = files; _lease = lease; }

    public static Task<OwnedJournal> OpenAsync(OwnedScope scope, CancellationToken cancellationToken = default) => OpenAsync(scope, new OwnedAtomicFiles(), cancellationToken);
    internal static async Task<OwnedJournal> OpenAsync(OwnedScope scope, OwnedAtomicFiles files, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream? lease = null;
        try
        {
            var captured = scope with { DataDirectory = Path.GetFullPath(scope.DataDirectory) };
            var root = Path.Combine(captured.DataDirectory, "owned-lab-v1");
            NoLinks(root); Directory.CreateDirectory(root); NoLinks(root);
            var lockPath = Path.Combine(root, "owner.lock"); NoLinks(lockPath);
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journal = new OwnedJournal(captured, root, files, lease); lease = null;
            try { await journal.RestoreAsync().ConfigureAwait(false); return journal; }
            catch { await journal.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new OwnedLabException("StoreIoFailure"); }
        finally { lease?.Dispose(); }
    }

    internal IReadOnlyList<OwnedRunSnapshot> Runs
    { get { lock (_sync) return Array.AsReadOnly(_runs.Values.Select(e => Copy(e.Snapshot)).ToArray()); } }
    internal bool IsReadOnly { get { lock (_sync) return _readOnly; } }
    internal OwnedRunSnapshot Read(string runId)
    {
        CheckId(runId); lock (_sync) return _runs.TryGetValue(runId, out var entry) ? Copy(entry.Snapshot) : throw new OwnedLabException("InvalidPlan");
    }

    internal async Task<OwnedGrant> CreateAsync(OwnedInputPlan input, OwnedHelperIdentity helper, TimeProvider time, OwnedRunSnapshot? parent = null)
    {
        // Capture untrusted memory before the first await; records do not make their backing memory immutable.
        var frozen = FixedOwnedPlans.ParseFrozen(input.Bytes);
        OwnedJournalFormat.ValidateHelper(helper);
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            Writable();
            var attempts = Path.Combine(_root, "attempts"); NoLinks(attempts);
            if (Directory.EnumerateFileSystemEntries(attempts).Count() >= 32) throw new OwnedLabException("StoreLimitExceeded");
            var now = time.GetUtcNow().ToUniversalTime();
            var execution = new OwnedExecution(1, NewId(), NewId(), _scope.ProjectId, _scope.PluginId, parent?.Execution.RunId, parent?.Input.Sha256, frozen.Sha256, frozen.TargetId, frozen.Protocol, helper, now, now.AddSeconds(60));
            OwnedJournalFormat.ValidateExecution(execution, _scope, frozen);
            var bytes = OwnedJournalFormat.ExecutionBytes(execution); var sha = OwnedJson.Hash(bytes);
            var deadline = checked(time.GetTimestamp() + checked(time.TimestampFrequency * 60));
            var run = new OwnedRunSnapshot(frozen, execution, sha, Array.AsReadOnly(Array.Empty<OwnedEvent>()), OwnedRunState.Queued, -1, OwnedJournalFormat.ZeroHash, false, null);
            lock (_sync) _runs.Add(execution.RunId, new(run, time));
            try
            {
                var path = Attempt(execution.RunId); Directory.CreateDirectory(path);
                foreach (var child in new[] { "events", "commits", "frames" }) Directory.CreateDirectory(Path.Combine(path, child));
                await _files.PublishNewAsync(Path.Combine(path, "input.json"), frozen.Bytes).ConfigureAwait(false);
                await _files.PublishNewAsync(Path.Combine(path, "execution.json"), bytes).ConfigureAwait(false);
                return new(execution, bytes.ToArray(), sha, deadline);
            }
            catch (Exception error) when (StorageFailure(error)) { Freeze(); throw new OwnedLabException("StoreIoFailure"); }
        }
        finally { _io.Release(); }
    }

    public async Task<OwnedRunSnapshot> AppendAsync(string runId, OwnedEventDraft draft)
    {
        CheckId(runId); var frozen = draft with { Bmp = draft.Bmp?.ToArray() };
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            Writable(); Entry entry; lock (_sync) entry = _runs.TryGetValue(runId, out var found) ? found : throw new OwnedLabException("InvalidPlan");
            if (entry.Restored) throw new OwnedLabException("AdmissionClosed");
            var prior = entry.Snapshot;
            var evt = new OwnedEvent(1, prior.Sequence + 1, prior.HeadSha, prior.ExecutionSha, entry.Clock.GetUtcNow().ToUniversalTime(), frozen.Kind, frozen.StepIndex, frozen.Outcome, frozen.ErrorCode, frozen.Identity, frozen.State, frozen.Bmp is null ? null : OwnedJson.Hash(frozen.Bmp), frozen.Kind == OwnedEventKind.StepResult ? "OwnedWindowPrintWindow" : null, frozen.Kind == OwnedEventKind.StepResult ? true : null, frozen.Retirement, frozen.UncertainStep);
            OwnedJournalFormat.ValidateNext(prior, evt);
            if (frozen.Bmp is not null) OwnedWireCodec.ValidateBmp(frozen.Bmp);
            var bytes = OwnedJournalFormat.EventBytes(evt); var sha = OwnedJson.Hash(bytes); var commit = OwnedJournalFormat.CommitBytes(evt, sha);
            var path = Attempt(runId); var index = evt.Sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            if (bytes.Length > 16384 || TreeBytes(Path.Combine(path, "events")) + TreeBytes(Path.Combine(path, "commits")) + bytes.Length + commit.Length > 1024 * 1024 || Directory.EnumerateFiles(Path.Combine(path, "frames")).Count() + (frozen.Bmp is null ? 0 : 1) > 20) throw new OwnedLabException("StoreLimitExceeded");
            try
            {
                NoLinks(path);
                if (frozen.Bmp is not null) await _files.PublishNewAsync(Path.Combine(path, "frames", index + ".bmp"), frozen.Bmp).ConfigureAwait(false);
                await _files.PublishNewAsync(Path.Combine(path, "events", index + ".json"), bytes).ConfigureAwait(false);
                await _files.PublishNewAsync(Path.Combine(path, "commits", index + ".json"), commit).ConfigureAwait(false);
                // Only the marker publisher's successful return acknowledges the new immutable prefix.
                var events = Array.AsReadOnly(prior.Events.Append(evt).ToArray());
                var updated = prior with { Events = events, Sequence = evt.Sequence, HeadSha = sha, State = OwnedJournalFormat.Project(events, false), ErrorCode = evt.Kind == OwnedEventKind.Terminal ? evt.ErrorCode : null };
                lock (_sync) _runs[runId] = entry with { Snapshot = updated }; return Copy(updated);
            }
            catch (Exception error) when (StorageFailure(error)) { Freeze(); throw new OwnedLabException("StoreIoFailure"); }
        }
        finally { _io.Release(); }
    }

    internal async Task<byte[]> ReadFrameAsync(string runId, int sequence)
    {
        CheckId(runId); await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) throw new OwnedLabException("AdmissionClosed");
            var run = Read(runId); var evt = run.Events.SingleOrDefault(e => e.Sequence == sequence && e.Kind == OwnedEventKind.StepResult) ?? throw new OwnedLabException("InvalidPlan");
            var path = Path.Combine(Attempt(runId), "frames", sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".bmp");
            try
            {
                var bmp = await ReadBoundedAsync(path, OwnedWireCodec.BmpLength).ConfigureAwait(false); OwnedWireCodec.ValidateBmp(bmp);
                if (OwnedJson.Hash(bmp) != evt.FrameSha) throw new IOException(); return bmp;
            }
            catch (Exception error) when (Corruption(error)) { Freeze(); throw new OwnedLabException("RecoveryNeeded"); }
        }
        finally { _io.Release(); }
    }

    internal async Task<OwnedRunSnapshot> VerifyRetryAsync(string runId)
    {
        CheckId(runId); await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            Writable();
            var existing = Read(runId);
            if (existing.State is not (OwnedRunState.Passed or OwnedRunState.Failed or OwnedRunState.Cancelled or OwnedRunState.Interrupted)) throw new OwnedLabException("AdmissionClosed");
            try { await RestoreAttemptAsync(Attempt(runId)).ConfigureAwait(false); return Read(runId); }
            catch (Exception error) when (Corruption(error)) { Freeze(); throw new OwnedLabException("RecoveryNeeded"); }
        }
        finally { _io.Release(); }
    }

    internal void Freeze()
    {
        lock (_sync)
        {
            _readOnly = true;
            foreach (var key in _runs.Keys.ToArray()) _runs[key] = _runs[key] with { Snapshot = _runs[key].Snapshot with { RecoveryNeeded = true, State = OwnedRunState.RecoveryNeeded, ErrorCode = "RecoveryNeeded" } };
        }
    }

    private async Task RestoreAsync()
    {
        var attempts = Path.Combine(_root, "attempts");
        try
        {
            NoLinks(attempts); Directory.CreateDirectory(attempts);
            if (Directory.EnumerateFileSystemEntries(_root).Any(p => Path.GetFileName(p) is not ("owner.lock" or "attempts"))) Freeze();
            var directories = Directory.GetFileSystemEntries(attempts);
            if (directories.Length > 32) Freeze();
            foreach (var directory in directories)
            {
                try { await RestoreAttemptAsync(directory).ConfigureAwait(false); }
                catch (Exception error) when (Corruption(error)) { Freeze(); }
            }
            if (IsReadOnly) Freeze(); // An earlier corrupt attempt also taints later valid restored prefixes.
        }
        catch (Exception error) when (Corruption(error)) { Freeze(); }
    }

    private async Task RestoreAttemptAsync(string path)
    {
        NoLinks(path); var id = Path.GetFileName(path); CheckId(id);
        if (!Directory.Exists(path)) throw new IOException();
        var inputBytes = await ReadBoundedAsync(Path.Combine(path, "input.json"), 4 * 1024 * 1024).ConfigureAwait(false);
        var executionBytes = await ReadBoundedAsync(Path.Combine(path, "execution.json"), 4 * 1024 * 1024 - inputBytes.Length).ConfigureAwait(false);
        var input = FixedOwnedPlans.ParseFrozen(inputBytes); var execution = OwnedJournalFormat.ReadExecution(executionBytes, _scope, input, id);
        var run = new OwnedRunSnapshot(input, execution, OwnedJson.Hash(executionBytes), Array.AsReadOnly(Array.Empty<OwnedEvent>()), OwnedRunState.Interrupted, -1, OwnedJournalFormat.ZeroHash, false, null);
        lock (_sync) _runs[id] = new(run, TimeProvider.System, true);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "input.json", "execution.json" };
        foreach (var name in new[] { "events", "commits", "frames" }) { NoLinks(Path.Combine(path, name)); if (!Directory.Exists(Path.Combine(path, name))) throw new IOException(); }
        if (TreeBytes(Path.Combine(path, "events")) + TreeBytes(Path.Combine(path, "commits")) > 1024 * 1024 || Directory.GetFiles(Path.Combine(path, "frames")).Length > 20) throw new IOException();
        for (var sequence = 0; ; sequence++)
        {
            var index = sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
            var marker = Path.Combine(path, "commits", index + ".json");
            if (!File.Exists(marker)) break;
            var eventPath = Path.Combine(path, "events", index + ".json");
            var bytes = await ReadBoundedAsync(eventPath, 16384).ConfigureAwait(false); var evt = OwnedJournalFormat.ReadEvent(bytes);
            OwnedJournalFormat.ValidateNext(run, evt); var sha = OwnedJson.Hash(bytes);
            await VerifyFrameAsync(path, evt).ConfigureAwait(false);
            OwnedJournalFormat.VerifyCommit(await ReadBoundedAsync(marker, 16384).ConfigureAwait(false), evt, sha);
            expected.Add(Path.Combine("events", index + ".json")); expected.Add(Path.Combine("commits", index + ".json"));
            if (evt.FrameSha is not null) expected.Add(Path.Combine("frames", index + ".bmp"));
            var events = Array.AsReadOnly(run.Events.Append(evt).ToArray());
            run = run with { Events = events, Sequence = sequence, HeadSha = sha, State = OwnedJournalFormat.Project(events, true), ErrorCode = evt.Kind == OwnedEventKind.Terminal ? evt.ErrorCode : null };
            lock (_sync) _runs[id] = new(run, TimeProvider.System, true);
        }
        if (run.Sequence < 0) throw new IOException();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            NoLinks(child);
            if (Directory.Exists(child))
            {
                if (Path.GetFileName(child) is not ("events" or "commits" or "frames")) throw new IOException();
                foreach (var file in Directory.EnumerateFileSystemEntries(child)) { NoLinks(file); if (Directory.Exists(file) || !expected.Remove(Path.GetRelativePath(path, file))) throw new IOException(); }
            }
            else if (!expected.Remove(Path.GetFileName(child))) throw new IOException();
        }
        if (expected.Count != 0) throw new IOException();
    }

    private static async Task VerifyFrameAsync(string path, OwnedEvent evt)
    {
        if (evt.FrameSha is null) return;
        var bmp = await ReadBoundedAsync(Path.Combine(path, "frames", evt.Sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".bmp"), OwnedWireCodec.BmpLength).ConfigureAwait(false);
        OwnedWireCodec.ValidateBmp(bmp); if (OwnedJson.Hash(bmp) != evt.FrameSha) throw new IOException();
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int max)
    {
        NoLinks(path); var info = new FileInfo(path); if (max < 0 || info.Length > max) throw new IOException();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > max) throw new IOException(); var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false); if (stream.ReadByte() != -1) throw new IOException(); return bytes;
    }
    private static long TreeBytes(string directory)
    {
        NoLinks(directory); long size = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory)) { NoLinks(path); if (Directory.Exists(path)) throw new IOException(); size = checked(size + new FileInfo(path).Length); }
        return size;
    }
    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse path refused.");
        }
    }
    private void Writable() { if (_disposed) throw new OwnedLabException("AdmissionClosed"); if (IsReadOnly) throw new OwnedLabException("RecoveryNeeded"); }
    private string Attempt(string runId) => Path.Combine(_root, "attempts", runId);
    private static void CheckId(string runId) { if (!OwnedJson.Hex(runId, 32)) throw new OwnedLabException("InvalidPlan"); }
    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    internal static OwnedRunSnapshot Copy(OwnedRunSnapshot run) => run with { Input = run.Input with { Bytes = run.Input.Bytes.ToArray(), Steps = Array.AsReadOnly(run.Input.Steps.ToArray()) }, Events = Array.AsReadOnly(run.Events.ToArray()) };
    private static bool StorageFailure(Exception error) => error is IOException or UnauthorizedAccessException;
    private static bool Corruption(Exception error) => StorageFailure(error) || error is OwnedLabException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException;
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; await _lease.DisposeAsync().ConfigureAwait(false); }
        finally { _io.Release(); }
    }
}
