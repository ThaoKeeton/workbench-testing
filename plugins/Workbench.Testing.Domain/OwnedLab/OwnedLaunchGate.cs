namespace Workbench.Testing.Domain.OwnedLab;

/// <summary>Linearizes synchronous revocation with the one fast native ResumeThread call.</summary>
public sealed class OwnedLaunchGate
{
    private readonly object _sync = new();
    private TimeProvider? _clock;
    private long _deadline;
    private bool _bound, _revoked, _consumed;
    internal OwnedLaunchGate() { }
    internal void Bind(TimeProvider time, long deadline)
    {
        lock (_sync)
        {
            if (_bound) throw new InvalidOperationException("Launch grant already bound.");
            _bound = true; _clock = time; _deadline = deadline;
        }
    }
    internal bool IsRevoked { get { lock (_sync) return _revoked; } }
    public bool TryResume(Action resume)
    {
        ArgumentNullException.ThrowIfNull(resume);
        lock (_sync)
        {
            if (!_bound || _revoked || _consumed || _clock!.GetTimestamp() >= _deadline) return false;
            _consumed = true;
            // Slow process creation, IO, and cancellation callbacks must remain outside this lock.
            resume(); return true;
        }
    }
    public void Revoke() { lock (_sync) _revoked = true; }
}
