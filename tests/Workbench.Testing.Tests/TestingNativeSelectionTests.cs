using Workbench.Sdk;
using Workbench.Testing;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingNativeSelectionTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static TestRunSnapshot Run(string id, params long[] sequences)
    {
        var plan = new TestRunPlan(id, id, "default", "com.projectworkbench.testing", DateTimeOffset.UtcNow, null,
            new string('c', 32), new string('d', 64), [], "virtual.task-form.v1", new string('e', 64));
        return new(plan, sequences.Select(n => new TestRunEvent(n, "prior", plan.PlanHash, DateTimeOffset.UtcNow,
            TestRunEventKind.StepFinished, "case", "step", TestStepOutcome.Passed, null, [id + "-evidence"], "hash")
        { NativePlainTextState = "SIMULATED observed " + id + "/" + n }).ToArray(), TestRunState.Completed, sequences.Last(), "head");
    }

    [Theory]
    [InlineData(7)] // Sequence absent from new controller run B.
    [InlineData(1)] // Same sequence in B must never return B's text/receipt under A's displayed item.
    public void Stale_A_item_cannot_resolve_against_B_projection_even_when_sequence_coincides(long sequence)
    {
        var latestB = Run(B, 1, 2);
        Assert.Null(NativeEventSelection.Resolve(latestB, A, new(A, sequence)));
        Assert.Null(NativeEventSelection.Resolve(latestB, B, new(A, sequence)));
    }

    [Fact]
    public void Displayed_A_projection_keeps_exact_A_text_and_receipt_when_controller_has_advanced_to_B()
    {
        var displayedA = Run(A, 1, 7);
        var controllerLatest = Run(B, 1, 2);
        Assert.NotEqual(displayedA.Plan.Id, controllerLatest.Plan.Id);
        var selected = Assert.IsType<NativeEventSelection>(NativeEventSelection.Resolve(displayedA, A, new(A, 7)));
        Assert.Equal(A, selected.RunId); Assert.Equal(A + "/7", selected.Key);
        Assert.Same(displayedA.Events[1], selected.Event);
        Assert.Equal("SIMULATED observed " + A + "/7", selected.Event.NativePlainTextState);
        Assert.Equal(A + "-evidence", Assert.Single(selected.Event.EvidenceIds));
    }

    [Fact]
    public void Missing_event_or_projection_is_a_safe_deferred_selection()
    {
        var displayed = Run(A, 1, 7);
        Assert.Null(NativeEventSelection.Resolve(displayed, A, new(A, 99)));
        Assert.Null(NativeEventSelection.Resolve(displayed, B, new(A, 1)));
        Assert.Null(NativeEventSelection.Resolve(displayed, A, null));
        Assert.Null(NativeEventSelection.Resolve(null, A, new(A, 1)));
    }

    [Fact]
    public async Task Initial_and_final_render_failures_are_contained_and_restore_busy_before_settlement()
    {
        var owner = new NativeActionOwner(); var busy = false; var actions = 0; var errors = new List<Exception>();
        await owner.HandleUserAsync(() => { actions++; return Task.CompletedTask; },
            () => { busy = true; throw new IOException("initial render"); },
            () => { busy = false; throw new InvalidOperationException("final render"); }, errors.Add);
        Assert.False(busy); Assert.Equal(0, actions);
        Assert.Collection(errors, e => Assert.IsType<IOException>(e), e => Assert.IsType<InvalidOperationException>(e));
        using (owner.Seal()) await owner.DrainAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_user_selection_starts_no_lookup_render_or_cleanup_under_temporary_or_terminal_seal(bool terminal)
    {
        var owner = new NativeActionOwner(); using var seal = owner.Seal(); if (terminal) owner.Terminal();
        var calls = 0;
        var error = await Assert.ThrowsAsync<PluginTestingException>(() => owner.HandleUserAsync(
            () => { calls++; return Task.CompletedTask; }, () => calls++, () => calls++, _ => calls++));
        Assert.Equal(terminal ? "TestingContextClosed" : "StoreBusy", error.ErrorCode);
        Assert.Equal(0, calls); await owner.DrainAsync();
    }
}
