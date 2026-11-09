using Workbench.Sdk;
using Workbench.Testing;
using Xunit;

namespace Workbench.Core.Tests;

[Collection(BlockingCallbackCollection.Name)]
public sealed class TestingNativeRemoteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leave_or_stop_refuses_a_remote_prefix_admitted_by_the_native_owner_before_the_barrier(bool stopping)
    {
        var owner = new NativeActionOwner(); var remote = new NativeRemoteOperation();
        var prefix = new AsyncTestGate(); var requests = 0;
        var accepted = owner.UserAsync(async () =>
        {
            await prefix.HoldAsync();
            return await remote.RunAsync(_ => { requests++; return Task.FromResult(1); });
        });
        await prefix.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var nativeSeal = owner.Seal();
        var remoteSeal = remote.Seal();
        if (stopping) { owner.Terminal(); remote.Terminal(); }
        await remote.RequestCancellationAsync(); // There is no remote CTS yet; the delayed prefix must be refused.
        var drain = owner.DrainAsync(); Assert.False(drain.IsCompleted);
        prefix.Release.SetResult();
        var error = await Assert.ThrowsAsync<PluginTestingException>(() => accepted);
        Assert.Equal(stopping ? "TestingContextClosed" : "StoreBusy", error.ErrorCode);
        await drain; Assert.Equal(0, requests); Assert.False(remote.IsBusy);
        remoteSeal.Dispose();
        if (stopping)
            Assert.Throws<PluginTestingException>(() => { _ = remote.RunAsync(_ => Task.FromResult(2)); });
        else
            Assert.Equal(2, await remote.RunAsync(_ => Task.FromResult(2)));
    }

    [Fact]
    public async Task Cancel_does_not_release_busy_or_owner_while_actual_work_ignores_cancellation()
    {
        var owner = new NativeActionOwner(); var remote = new NativeRemoteOperation();
        var actual = new AsyncTestGate(); var ui = new AsyncTestGate();
        CancellationToken captured = default;
        var work = owner.UserAsync(() => remote.RunAsync(async token =>
        { captured = token; await actual.HoldAsync(); await ui.HoldAsync(); return 42; }));
        await actual.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var seal = owner.Seal();
        using var remoteSeal = remote.Seal();
        await remote.RequestCancellationAsync();
        var drain = owner.DrainAsync();
        Assert.True(captured.IsCancellationRequested); Assert.True(remote.IsBusy); Assert.False(drain.IsCompleted); Assert.False(work.IsCompleted);
        Assert.Throws<PluginTestingException>(() => { _ = remote.RunAsync(_ => Task.FromResult(0)); });
        actual.Release.SetResult(); await ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(remote.IsBusy); Assert.False(drain.IsCompleted);
        ui.Release.SetResult(); await work; await drain;
        Assert.False(remote.IsBusy);
    }

    [Fact]
    public async Task Cancellation_callback_runs_off_the_request_thread_and_is_joined_after_operation_completion()
    {
        var owner = new NativeActionOwner(); var remote = new NativeRemoteOperation();
        var actual = new AsyncTestGate(); var callback = new AsyncTestGate();
        var callbackThread = 0;
        var work = owner.UserAsync(() => remote.RunAsync(async token =>
        {
            using var registration = token.Register(() =>
            { callbackThread = Environment.CurrentManagedThreadId; callback.HoldAsync().GetAwaiter().GetResult(); });
            await actual.HoldAsync(); return true;
        }));
        await actual.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requestingThread = Environment.CurrentManagedThreadId;
        var cancel = remote.RequestCancellationAsync(); // Must return without synchronously running the held callback.
        await callback.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(requestingThread, callbackThread); Assert.False(cancel.IsCompleted);
        Assert.Same(cancel, remote.RequestCancellationAsync());
        owner.Terminal(); var drain = owner.DrainAsync();
        actual.Release.SetResult();
        Assert.True(remote.IsBusy); Assert.False(work.IsCompleted); Assert.False(drain.IsCompleted);
        callback.Release.SetResult(); await cancel; await work; await drain;
        Assert.False(remote.IsBusy); await remote.RequestCancellationAsync();
    }

    [Fact]
    public async Task Throwing_cancel_callback_keeps_actual_action_owned_and_drain_still_joins_it()
    {
        var owner = new NativeActionOwner(); var remote = new NativeRemoteOperation();
        var actual = new AsyncTestGate(); var ui = new AsyncTestGate();
        var work = owner.UserAsync(() => remote.RunAsync(async token =>
        {
            using var registration = token.Register(() => throw new InvalidOperationException("synthetic callback fault"));
            await actual.HoldAsync(); return true;
        }, async failure => { Assert.NotNull(failure); await ui.HoldAsync(); }));
        await actual.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => remote.RequestCancellationAsync());
        using var seal = owner.Seal(); var drain = owner.DrainAsync();
        Assert.True(remote.IsBusy); Assert.False(drain.IsCompleted);
        actual.Release.SetResult();
        await ui.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(remote.IsBusy); Assert.False(drain.IsCompleted); ui.Release.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => work); await drain;
        Assert.False(remote.IsBusy);
    }

    [Fact]
    public async Task Settlement_retains_busy_until_the_actual_ui_continuation_has_observed_the_outcome()
    {
        var remote = new NativeRemoteOperation(); var ui = new AsyncTestGate();
        var work = remote.RunAsync(_ => Task.FromResult(5), async failure =>
        { Assert.Null(failure); await ui.HoldAsync(); });
        try
        {
            await Task.WhenAny(ui.Entered.Task, work).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ui.Entered.Task.IsCompleted, "Actual outcome/UI settlement must run before completion.");
            Assert.True(remote.IsBusy); Assert.False(work.IsCompleted);
            await remote.RequestCancellationAsync(); // IO has settled; a cancel racing final UI must not touch a disposed source.
        }
        finally { ui.Release.TrySetResult(); }
        Assert.Equal(5, await work); Assert.False(remote.IsBusy);
    }

    [Fact]
    public async Task Completion_then_cancel_is_safe_and_later_operations_get_their_own_live_token()
    {
        var remote = new NativeRemoteOperation();
        Assert.Equal(7, await remote.RunAsync(_ => Task.FromResult(7)));
        await remote.RequestCancellationAsync();
        var gate = new AsyncTestGate(); CancellationToken current = default;
        var work = remote.RunAsync(async token => { current = token; await gate.HoldAsync(); return 9; });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(current.IsCancellationRequested);
        var cancel = remote.RequestCancellationAsync(); gate.Release.SetResult();
        await cancel; Assert.Equal(9, await work); Assert.False(remote.IsBusy);
    }
}
