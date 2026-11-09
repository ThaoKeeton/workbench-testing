using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Workbench.Sdk;

namespace Workbench.Testing.SourceAcceptance;

// Fixture-private same-user convention. It grants no authority over arbitrary paths or provider traffic.
internal sealed class SourceAcceptanceReleaseBridge(string directory, string runId, TimeProvider timeProvider)
{
    private const string Setting = "source-acceptance-release-v1";
    private const string HeldFile = "source-acceptance-release-v1.held.json";
    private const string HeldStaging = "source-acceptance-release-v1.held.tmp";
    private const string RequestFile = "source-acceptance-release-v1.release.json";
    private const int MaximumBytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TimeProvider _timeProvider = timeProvider;

    internal static async Task InitializeAsync(IPluginTestingContext context, SourceAcceptanceScenarioLedger ledger,
        CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
    {
        // Capture revocable properties before the single settings await; never consult them from a monitor.
        var project = SourceAcceptanceProtocol.ValidateContext(context);
        var data = context.DataDirectory;
        var settings = context.Settings;
        var value = await settings.GetAsync(Setting, JsonSerializer.SerializeToElement(string.Empty), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (SourceAcceptanceProtocol.ValidateContext(context) != project || project != ledger.ProjectId) throw ConfigurationRefused();
        if (value.ValueKind != JsonValueKind.String) throw ConfigurationRefused();
        var run = value.GetString()!;
        SourceAcceptanceReleaseBridge? bridge = null;
        if (run.Length != 0)
        {
            if (!Hex32(run)) throw ConfigurationRefused();
            var root = Path.Combine(Path.GetTempPath(), "ProjectWorkbench-source-acceptance-probe", run);
            var expected = project == "default" ? Path.Combine(root, "data", SourceAcceptanceProtocol.FixtureId)
                : Path.Combine(root, ".workspaces", "projects", project, "data", SourceAcceptanceProtocol.FixtureId);
            try
            {
                if (!string.Equals(data, expected, StringComparison.Ordinal)) throw ConfigurationRefused();
                RequireDirectory(data);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { throw ConfigurationRefused(); }
            bridge = new(data, run, timeProvider ?? TimeProvider.System);
        }
        ledger.ConfigureReleaseBridge(bridge);
    }

    private static ArgumentException ConfigurationRefused() => new("Synthetic source release configuration refused.");
    private static bool Hex32(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Allocation only: the ledger calls this while holding its admission/seal barrier.
    internal Monitor Reserve(string instanceId, string projectId, SourceAcceptanceScenarioLedger.Ticket ticket, Action release)
        => new(this, new(runId, projectId, instanceId, ticket.Generation,
            ticket.Provider == SourceAcceptanceProvider.Figma ? "figma" : "ai", Guid.NewGuid().ToString("N")), ticket.Release.Task, release);

    internal sealed record Identity(string RunId, string ProjectId, string InstanceId, long Generation, string Provider, string ReleaseId);
    internal sealed class Monitor(SourceAcceptanceReleaseBridge bridge, Identity identity, Task released, Action release)
    {
        private int _started;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion => _completion.Task;
        internal void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) _ = CompleteAsync();
        }
        private async Task CompleteAsync()
        {
            ExceptionDispatchInfo? failure = null;
            try { bridge.PublishDescriptor(identity); }
            catch (Exception) { failure = ExceptionDispatchInfo.Capture(new IOException("Synthetic source release descriptor publication failed.")); }
            try
            {
                while (!released.IsCompleted)
                {
                    if (bridge.MatchingRequest(identity)) { release(); break; }
                    // This clock only observes files. Neither its timer nor its cancellation releases IO.
                    using var observation = new CancellationTokenSource();
                    var delay = Task.Delay(TimeSpan.FromMilliseconds(25), bridge._timeProvider, observation.Token);
                    await Task.WhenAny(released, delay).ConfigureAwait(false);
                    observation.Cancel();
                }
            }
            catch (Exception) { failure ??= ExceptionDispatchInfo.Capture(new IOException("Synthetic source release observation failed.")); }
            // A publication/observer failure still owns the exact gate until somebody explicitly releases it.
            await released.ConfigureAwait(false);
            try { failure?.Throw(); _completion.TrySetResult(); }
            catch (Exception error) { _completion.TrySetException(error); }
        }
    }

    private void PublishDescriptor(Identity identity)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            protocolVersion = 1, fixtureId = SourceAcceptanceProtocol.FixtureId, pluginId = SourceAcceptanceProtocol.FixtureId,
            runId = identity.RunId, projectId = identity.ProjectId, instanceId = identity.InstanceId,
            generation = identity.Generation, provider = identity.Provider, scenario = "hold", releaseId = identity.ReleaseId
        });
        if (bytes.Length > MaximumBytes) throw new IOException();
        RequireDirectory(directory);
        var target = Path.Combine(directory, HeldFile); var staging = Path.Combine(directory, HeldStaging);
        RequireOptionalRegularFile(target);
        RejectLinks(staging);
        // A leftover staging target is refused, never followed, truncated or silently removed.
        using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        RequireOptionalRegularFile(target); RejectLinks(staging);
        File.Move(staging, target, true);
    }

    private bool MatchingRequest(Identity expected)
    {
        try
        {
            RequireDirectory(directory);
            var path = Path.Combine(directory, RequestFile);
            if (!RequireOptionalRegularFile(path)) return false;
            var length = new FileInfo(path).Length;
            if (length is < 1 or > MaximumBytes) return false;
            // Inspect a bounded, nonempty regular target before opening (also refuses Unix FIFO/device/socket targets).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[MaximumBytes + 1]; var total = 0;
            while (total < bytes.Length)
            {
                var read = stream.Read(bytes, total, bytes.Length - total); if (read == 0) break; total += read;
            }
            if (total is < 1 or > MaximumBytes) return false;
            using var document = JsonDocument.Parse(StrictUtf8.GetString(bytes, 0, total), new() { MaxDepth = 8 });
            var root = document.RootElement;
            string[] names = ["protocolVersion", "fixtureId", "pluginId", "runId", "projectId", "instanceId", "generation", "provider", "scenario", "releaseId", "action"];
            if (root.ValueKind != JsonValueKind.Object) return false;
            var fields = root.EnumerateObject().ToArray();
            if (fields.Length != names.Length || fields.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length
                || fields.Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) return false;
            return root.GetProperty("protocolVersion") is { ValueKind: JsonValueKind.Number } version && version.TryGetInt32(out var v) && v == 1
                && root.GetProperty("generation") is { ValueKind: JsonValueKind.Number } generation && generation.TryGetInt64(out var g) && g > 0 && g == expected.Generation
                && Text("fixtureId", SourceAcceptanceProtocol.FixtureId) && Text("pluginId", SourceAcceptanceProtocol.FixtureId)
                && Text("runId", expected.RunId) && Text("projectId", expected.ProjectId) && Text("instanceId", expected.InstanceId)
                && Text("provider", expected.Provider) && Text("scenario", "hold") && Text("releaseId", expected.ReleaseId) && Text("action", "release");
            bool Text(string name, string value) => root.GetProperty(name) is { ValueKind: JsonValueKind.String } field && field.GetString() == value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or DecoderFallbackException)
        { return false; }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException(); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void RequireDirectory(string path)
    {
        RejectLinks(path);
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) throw new IOException();
    }
    private static bool RequireOptionalRegularFile(string path)
    {
        RejectLinks(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0 || new FileInfo(path).Length < 1) throw new IOException();
        return true;
    }
}
