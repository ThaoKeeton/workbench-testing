namespace Workbench.Testing;

internal sealed record NativeInvocationRequest(long Generation);

/// <summary>Identifies the invocation whose feedback still belongs to the current page.</summary>
internal sealed class NativeInvocationFeedback
{
    private NativeInvocationRequest? _current;

    internal NativeInvocationRequest Begin(long generation)
    {
        var request = new NativeInvocationRequest(generation);
        Interlocked.Exchange(ref _current, request);
        return request;
    }
    internal bool IsCurrent(NativeInvocationRequest request, long generation) =>
        request.Generation == generation && ReferenceEquals(Volatile.Read(ref _current), request);
    internal void Retire() => Interlocked.Exchange(ref _current, null);
}
