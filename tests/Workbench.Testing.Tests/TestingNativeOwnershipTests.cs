using Workbench.Testing;
using Workbench.Sdk;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingNativeOwnershipTests
{
    [Fact]
    public void Terminal_attempt_can_start_again_while_an_unrelated_selected_history_cannot_unlock_a_live_attempt()
    {
        var running = new TestRunSummary(new string('a', 32), new string('b', 32), "default", "com.projectworkbench.testing", DateTimeOffset.UtcNow, null, new string('c', 32), new string('d', 64), TestRunState.Running, 1, new string('e', 64));
        var snapshot = new Workbench.Testing.Domain.TestingControllerSnapshot("default", [], [], [running], null, null, null, false, false, true);
        Assert.False(NativeRunAdmission.CanStart(snapshot, running.Id));
        Assert.True(NativeRunAdmission.CanStart(snapshot with { Runs = [running with { State = TestRunState.Completed }] }, running.Id));
        Assert.True(NativeRunAdmission.CanStart(snapshot with { OwnsRun = false }, null));
        Assert.False(NativeRunAdmission.CanStart(snapshot, new string('f', 32)));
    }
    [Fact]
    public async Task Synchronous_prefix_is_owned_before_it_can_seal_and_terminal_refuses_without_invoking()
    {
        var owner = new NativeActionOwner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? seal = null;
        Task? drain = null;
        var actual = owner.UserAsync(async () =>
        {
            seal = owner.Seal();
            drain = owner.DrainAsync();
            Assert.False(drain.IsCompleted);
            await release.Task;
        });
        Assert.False(actual.IsCompleted);
        await Assert.ThrowsAsync<PluginTestingException>(() => owner.UserAsync(() => Task.CompletedTask));
        release.SetResult();
        await actual;
        await drain!;
        seal!.Dispose();
        await owner.UserAsync(() => Task.CompletedTask);
        owner.Terminal();
        var invoked = false;
        await Assert.ThrowsAsync<PluginTestingException>(() => owner.UserAsync(() => { invoked = true; return Task.CompletedTask; }));
        Assert.False(invoked);
    }

    [Fact]
    public async Task Temporary_seal_preserves_internal_dispatch_and_drain_joins_its_actual_postfix()
    {
        var owner = new NativeActionOwner();
        using (owner.Seal())
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatched = owner.InternalAsync(() => release.Task);
            var drain = owner.DrainAsync();
            Assert.False(drain.IsCompleted);
            release.SetResult();
            await dispatched;
            await drain;
        }
        var called = false;
        await owner.UserAsync(() => { called = true; return Task.CompletedTask; });
        Assert.True(called);
    }

    [Fact]
    public async Task Callback_failure_is_observed_and_does_not_poison_later_admission_or_drain()
    {
        var owner = new NativeActionOwner();
        await Assert.ThrowsAsync<IOException>(() => owner.UserAsync(() => throw new IOException()));
        using (owner.Seal()) await owner.DrainAsync();
        Assert.Equal(42, await owner.UserAsync(() => Task.FromResult(42)));
    }
}
