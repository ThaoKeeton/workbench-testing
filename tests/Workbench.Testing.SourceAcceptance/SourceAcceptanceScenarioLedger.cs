using System.Text;
using System.Text.Json;

namespace Workbench.Testing.SourceAcceptance;

internal enum SourceAcceptanceProvider { None, Figma, Ai }
internal enum SourceAcceptanceScenario { None, Happy, HttpFailure, Malformed, Partial, Hold }
internal sealed class SourceAcceptanceScenarioLedger
{
    private readonly object _sync = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly Counts _figma = new(), _ai = new();
    private long _generation, _accepted;
    private SourceAcceptanceProvider _provider;
    private SourceAcceptanceScenario _scenario;
    private string _phase = "idle", _outcome = "none";
    private bool _armed, _closed;
    private Ticket? _active;
    private SourceAcceptanceReleaseBridge? _releaseBridge;
    private bool _releaseConfigured;
    private readonly List<SourceAcceptanceReleaseBridge.Monitor> _releaseMonitors = [];
    internal string ProjectId { get; }
    internal bool CommandsOpen { get { lock (_sync) return !_closed; } }

    internal SourceAcceptanceScenarioLedger(string projectId)
    {
        if (projectId != "default" && (projectId is not { Length: 32 } || projectId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ArgumentException("A canonical synthetic project identity is required.");
        ProjectId = projectId;
    }
    private sealed class Counts
    {
        internal long Entered, Accepted, Settled, Unexpected;
        internal bool LimitExceeded, CancellationObserved;
        internal object Snapshot() => new { entered = Entered, accepted = Accepted, settled = Settled, unexpected = Unexpected,
            limitExceeded = LimitExceeded, cancellationObserved = CancellationObserved };
    }
    internal sealed class Ticket(SourceAcceptanceProvider provider, SourceAcceptanceScenario scenario, long generation, bool planned)
    {
        internal SourceAcceptanceProvider Provider { get; } = provider;
        internal SourceAcceptanceScenario Scenario { get; } = scenario;
        internal long Generation { get; } = generation;
        internal bool Planned { get; } = planned;
        internal bool Accepted, Settled;
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal SourceAcceptanceReleaseBridge.Monitor? Monitor;
    }
    private Counts For(SourceAcceptanceProvider provider) => provider == SourceAcceptanceProvider.Figma ? _figma : _ai;
    internal string StateJson()
    {
        object snapshot;
        lock (_sync)
            snapshot = new { schemaVersion = 1, fixtureId = SourceAcceptanceProtocol.FixtureId, pluginId = SourceAcceptanceProtocol.FixtureId,
                instanceId = _instanceId, projectId = ProjectId, generation = _generation, provider = _provider.ToString().ToLowerInvariant(),
                scenario = _scenario == SourceAcceptanceScenario.HttpFailure ? "http-failure" : _scenario.ToString().ToLowerInvariant(),
                phase = _phase, outcome = _outcome, figma = _figma.Snapshot(), ai = _ai.Snapshot() };
        var json = JsonSerializer.Serialize(snapshot);
        if (Encoding.UTF8.GetByteCount(json) > 8192) throw new InvalidOperationException("Synthetic state limit exceeded.");
        return json;
    }
    internal bool TryArm(SourceAcceptanceProvider provider, SourceAcceptanceScenario scenario)
    {
        if (!(provider == SourceAcceptanceProvider.Figma && scenario is SourceAcceptanceScenario.Happy or SourceAcceptanceScenario.HttpFailure or SourceAcceptanceScenario.Malformed or SourceAcceptanceScenario.Hold
            || provider == SourceAcceptanceProvider.Ai && scenario is SourceAcceptanceScenario.Happy or SourceAcceptanceScenario.Malformed or SourceAcceptanceScenario.Partial or SourceAcceptanceScenario.Hold)) return false;
        lock (_sync)
        {
            if (_closed || _armed || _active is not null) return false;
            if (_accepted >= 32) { For(provider).LimitExceeded = true; return false; }
            _generation = checked(_generation + 1); _provider = provider; _scenario = scenario;
            _armed = true; _phase = "armed"; _outcome = "none"; return true;
        }
    }
    internal Ticket Enter(SourceAcceptanceProvider provider)
    {
        lock (_sync)
        {
            var counts = For(provider); counts.Entered = checked(counts.Entered + 1);
            if (_accepted >= 32) counts.LimitExceeded = true;
            var planned = !_closed && _armed && _active is null && _provider == provider && _accepted < 32;
            var ticket = new Ticket(provider, planned ? _scenario : SourceAcceptanceScenario.None, _generation, planned);
            if (planned) { _armed = false; _active = ticket; _phase = "entered"; }
            return ticket;
        }
    }
    internal bool Accept(Ticket ticket)
    {
        lock (_sync)
        {
            if (_closed || !ReferenceEquals(_active, ticket) || !ticket.Planned || ticket.Accepted || _accepted >= 32) return false;
            _accepted = checked(_accepted + 1);
            var counts = For(ticket.Provider); counts.Accepted = checked(counts.Accepted + 1); ticket.Accepted = true;
            if (ticket.Scenario == SourceAcceptanceScenario.Hold && _releaseBridge is not null)
            {
                ticket.Monitor = _releaseBridge.Reserve(_instanceId, ProjectId, ticket, () => ReleaseTicket(ticket));
                _releaseMonitors.Add(ticket.Monitor);
            }
            _phase = ticket.Scenario == SourceAcceptanceScenario.Hold ? "held" : "entered"; return true;
        }
    }
    internal void ObserveCancellation(Ticket ticket)
    {
        lock (_sync)
            if (ReferenceEquals(_active, ticket) && ticket.Accepted && !ticket.Settled) For(ticket.Provider).CancellationObserved = true;
    }
    internal void Settle(Ticket ticket, string outcome)
    {
        lock (_sync)
        {
            if (ticket.Settled) return;
            var counts = For(ticket.Provider); counts.Settled = checked(counts.Settled + 1);
            if (outcome == "rejected") counts.Unexpected = checked(counts.Unexpected + 1);
            ticket.Settled = true;
            if (ReferenceEquals(_active, ticket))
            { _active = null; _phase = _closed ? "closed" : "settled"; _outcome = outcome; }
            else if (_active is null && !_armed) { _phase = _closed ? "closed" : "settled"; _outcome = outcome; }
        }
    }
    // Capturing and completing are deliberately separate: a delayed release cannot retarget another generation.
    internal Action? CaptureRelease()
    {
        Ticket ticket;
        lock (_sync)
        {
            if (_active is not { Accepted: true, Settled: false, Scenario: SourceAcceptanceScenario.Hold } active) return null;
            ticket = active;
        }
        return () => ReleaseTicket(ticket);
    }
    private void ReleaseTicket(Ticket ticket)
    {
        lock (_sync)
            if (ReferenceEquals(_active, ticket) && !ticket.Settled && !_closed) _phase = "released";
        ticket.Release.TrySetResult();
    }
    internal bool ReleasePending()
    { var release = CaptureRelease(); if (release is null) return false; release(); return true; }
    internal void ConfigureReleaseBridge(SourceAcceptanceReleaseBridge? bridge)
    {
        lock (_sync)
        {
            if (_closed || _releaseConfigured || _generation != 0) throw new ArgumentException("Synthetic source release configuration refused.");
            _releaseConfigured = true; _releaseBridge = bridge;
        }
    }
    internal Task JoinReleaseMonitorsAsync()
    {
        Task[] pending;
        lock (_sync) pending = _releaseMonitors.Select(monitor => monitor.Completion).ToArray();
        return Task.WhenAll(pending);
    }
    internal void StartReleaseMonitor(Ticket ticket) => ticket.Monitor?.Start();
    internal void SealCommands() { lock (_sync) { _closed = true; _armed = false; _phase = "closed"; } }
}
