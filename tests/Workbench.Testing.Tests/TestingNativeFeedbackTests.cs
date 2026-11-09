using Workbench.Sdk;
using Workbench.Testing;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingNativeFeedbackTests
{
    [Fact]
    public async Task Current_async_failure_reaches_feedback_before_actual_owner_settlement_and_drain()
    {
        var owner = new NativeActionOwner();
        var feedback = new NativeInvocationFeedback();
        var request = feedback.Begin(7);
        var release = Gate();
        var failure = new PluginTestingException("StoreIoFailure");
        Exception? displayed = null;
        Task? callbackDrain = null;
        var settled = false;
        var work = owner.HandleUserAsync(async () => { await release.Task; throw failure; },
            () => { },
            () => { Assert.Same(failure, displayed); settled = true; },
            error =>
            {
                callbackDrain = owner.DrainAsync();
                Assert.False(callbackDrain.IsCompleted);
                if (feedback.IsCurrent(request, 7)) displayed = error;
            });
        var drain = owner.DrainAsync();
        Assert.False(work.IsCompleted);
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await work;
        await drain;
        await callbackDrain!;
        Assert.True(settled);
        Assert.Equal("StoreIoFailure", Assert.IsType<PluginTestingException>(displayed).ErrorCode);
    }

    [Fact]
    public async Task Newer_same_generation_failure_survives_the_older_actions_late_failure()
    {
        var owner = new NativeActionOwner();
        var feedback = new NativeInvocationFeedback();
        var releaseA = Gate();
        var releaseB = Gate();
        Exception? displayed = null;
        var requestA = feedback.Begin(3);
        var a = owner.HandleUserAsync(async () => { await releaseA.Task; throw new PluginTestingException("DriverFailure"); },
            () => { }, () => { }, error => { if (feedback.IsCurrent(requestA, 3)) displayed = error; });
        var requestB = feedback.Begin(3);
        var failureB = new PluginTestingException("StoreIoFailure");
        var b = owner.HandleUserAsync(async () => { await releaseB.Task; throw failureB; },
            () => { }, () => { }, error => { if (feedback.IsCurrent(requestB, 3)) displayed = error; });
        releaseB.SetResult();
        await b;
        try { Assert.Same(failureB, displayed); }
        finally
        {
            releaseA.SetResult();
            await a;
            await owner.DrainAsync();
        }
        Assert.Same(failureB, displayed);
    }

    [Fact]
    public void Same_generation_uses_exact_request_identity_instead_of_equal_record_values()
    {
        var feedback = new NativeInvocationFeedback();
        var first = feedback.Begin(5);
        var current = feedback.Begin(5);
        var foreign = new NativeInvocationRequest(5);
        Assert.Equal(first, current);
        Assert.Equal(current, foreign);
        Assert.False(feedback.IsCurrent(first, 5));
        Assert.False(feedback.IsCurrent(foreign, 5));
        Assert.True(feedback.IsCurrent(current, 5));
    }

    [Fact]
    public async Task Page_generation_change_suppresses_a_real_pending_actions_failure()
    {
        var owner = new NativeActionOwner();
        var feedback = new NativeInvocationFeedback();
        var request = feedback.Begin(11);
        long generation = 11;
        var release = Gate();
        var callbacks = 0;
        var displayed = false;
        var work = owner.HandleUserAsync(async () => { await release.Task; throw new IOException(); },
            () => { }, () => { }, error =>
            {
                callbacks++;
                if (feedback.IsCurrent(request, Interlocked.Read(ref generation))) displayed = true;
            });
        Interlocked.Increment(ref generation);
        release.SetResult();
        await work;
        Assert.Equal(1, callbacks);
        Assert.False(displayed);
    }

    [Fact]
    public async Task Retire_invalidates_pending_feedback_without_abandoning_the_owners_callback()
    {
        var owner = new NativeActionOwner();
        var feedback = new NativeInvocationFeedback();
        var request = feedback.Begin(2);
        var release = Gate();
        var callbacks = 0;
        var displayed = false;
        var work = owner.HandleUserAsync(async () => { await release.Task; throw new IOException(); },
            () => { }, () => { }, error =>
            {
                callbacks++;
                if (feedback.IsCurrent(request, 2)) displayed = true;
            });
        feedback.Retire();
        feedback.Retire();
        var drain = owner.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await work;
        await drain;
        Assert.Equal(1, callbacks);
        Assert.False(displayed);
    }

    [Fact]
    public void Reactivated_page_can_begin_new_feedback_after_retire()
    {
        var feedback = new NativeInvocationFeedback();
        var old = feedback.Begin(9);
        feedback.Retire();
        Assert.False(feedback.IsCurrent(old, 9));
        var current = feedback.Begin(10);
        Assert.True(feedback.IsCurrent(current, 10));
        Assert.False(feedback.IsCurrent(current, 9));
        Assert.False(feedback.IsCurrent(old, 10));
    }

    [Theory]
    [InlineData(false, "StoreBusy")]
    [InlineData(true, "TestingContextClosed")]
    public async Task Immediate_refused_attempt_supersedes_older_accepted_feedback(bool terminal, string expectedCode)
    {
        var owner = new NativeActionOwner();
        var feedback = new NativeInvocationFeedback();
        var release = Gate();
        string? displayed = null;
        var old = feedback.Begin(4);
        var accepted = owner.HandleUserAsync(async () => { await release.Task; throw new PluginTestingException("DriverFailure"); },
            () => { }, () => { }, error =>
            { if (feedback.IsCurrent(old, 4)) displayed = Assert.IsType<PluginTestingException>(error).ErrorCode; });
        using var seal = owner.Seal();
        if (terminal) owner.Terminal();
        var current = feedback.Begin(4);
        var prefixCalls = 0;
        var refused = owner.HandleUserAsync(() => { prefixCalls++; return Task.CompletedTask; },
            () => prefixCalls++, () => prefixCalls++, _ => prefixCalls++);
        Assert.True(refused.IsFaulted);
        var failure = await Assert.ThrowsAsync<PluginTestingException>(() => refused);
        Assert.Equal(expectedCode, failure.ErrorCode);
        if (feedback.IsCurrent(current, 4)) displayed = failure.ErrorCode;
        release.SetResult();
        await accepted;
        await owner.DrainAsync();
        Assert.Equal(0, prefixCalls);
        Assert.Equal(expectedCode, displayed);
    }

    [Fact]
    public async Task Current_settlement_failure_remains_in_the_same_retained_invocation()
    {
        var owner = new NativeActionOwner();
        var feedback = new NativeInvocationFeedback();
        var request = feedback.Begin(8);
        var release = Gate();
        var failure = new PluginTestingException("StoreIoFailure");
        Exception? displayed = null;
        var work = owner.HandleUserAsync(() => release.Task, () => { }, () => throw failure,
            error => { if (feedback.IsCurrent(request, 8)) displayed = error; });
        Assert.False(work.IsCompleted);
        release.SetResult();
        await work;
        await owner.DrainAsync();
        Assert.Same(failure, displayed);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
