namespace Workbench.Testing;

internal sealed record NativeEvidenceRequest(string Key, long Generation);

/// <summary>UI-thread request identity: deduplicates pending/loaded evidence and bounds explicit failed retries.</summary>
internal sealed class NativeEvidenceCache
{
    private NativeEvidenceRequest? _current;
    private bool _failed;

    internal NativeEvidenceRequest? Begin(string key, long generation, bool retryFailed = false)
    {
        if (_current is { } current && current.Key == key && current.Generation == generation && (!_failed || !retryFailed)) return null;
        _failed = false;
        return _current = new(key, generation);
    }
    internal bool IsCurrent(NativeEvidenceRequest request) => ReferenceEquals(_current, request);
    internal bool Failed(NativeEvidenceRequest request)
    {
        if (!IsCurrent(request)) return false;
        _failed = true;
        return true;
    }
    internal void Clear() { _current = null; _failed = false; }
}
