using Xunit;

namespace Workbench.Core.Tests;

// The worker minimum is process-global. Tests reserving callback headroom must
// not overlap other collections, even if assembly-wide parallelism is enabled later.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BlockingCallbackCollection
{
    public const string Name = "Controlled blocking callback budgets";
}

internal sealed class CallbackWorkerHeadroom : IDisposable
{
    private readonly int _workers;
    private readonly int _io;

    internal CallbackWorkerHeadroom()
    {
        ThreadPool.GetMinThreads(out _workers, out _io);
        // One deliberate blocking callback must not consume the sole cold worker
        // needed by its Stop, filesystem completions and the timeout observer.
        Assert.True(ThreadPool.SetMinThreads(Math.Max(4, _workers), _io));
    }

    public void Dispose() => Assert.True(ThreadPool.SetMinThreads(_workers, _io));
}
