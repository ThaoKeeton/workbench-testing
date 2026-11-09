using System.Security.Cryptography;
using Workbench.Testing;
using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingNativeOwnedEvidenceTests
{
    [Fact]
    public async Task Persisted_selection_binds_exact_run_sequence_and_hash_and_rejects_cross_run_items()
    {
        using var rig = new OwnedLabTestRig(); var pilot = rig.Controller(); await pilot.InitializeAsync();
        try
        {
            var a = await pilot.RunAsync(OwnedScenario.Positive); var b = await pilot.RunAsync(OwnedScenario.EmptyTitle);
            var e = a.Events.First(e => e.Kind == OwnedEventKind.StepResult);
            var selected = Assert.IsType<NativeOwnedSelection>(NativeOwnedEvidence.Resolve(a, a.Execution.RunId, new(a.Execution.RunId, e.Sequence)));
            var bytes = await pilot.ReadFrameAsync(selected.RunId, selected.Event.Sequence);
            Assert.Equal(e.FrameSha, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.Equal("owned/" + a.Execution.RunId + "/" + e.Sequence + "/" + e.FrameSha, selected.Key);
            Assert.Equal(e.State, selected.Event.State);
            Assert.Null(NativeOwnedEvidence.Resolve(b, b.Execution.RunId, new(a.Execution.RunId, e.Sequence)));
            Assert.Null(NativeOwnedEvidence.Resolve(a, b.Execution.RunId, new(a.Execution.RunId, e.Sequence)));
            Assert.Null(NativeOwnedEvidence.Resolve(a, a.Execution.RunId, new(a.Execution.RunId, 999)));
        }
        finally { await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Old_frame_success_and_failure_cannot_replace_B_reselection_or_new_page_generation()
    {
        using var rig = new OwnedLabTestRig(); var pilot = rig.Controller(); await pilot.InitializeAsync();
        try
        {
            var a = await pilot.RunAsync(OwnedScenario.Positive); var b = await pilot.RunAsync(OwnedScenario.EmptyTitle);
            NativeOwnedSelection Pick(OwnedRunSnapshot run)
            { var e = run.Events.First(e => e.Kind == OwnedEventKind.StepResult); return Assert.IsType<NativeOwnedSelection>(NativeOwnedEvidence.Resolve(run, run.Execution.RunId, new(run.Execution.RunId, e.Sequence))); }
            var sa = Pick(a); var sb = Pick(b); Assert.Equal(sa.Event.Sequence, sb.Event.Sequence);
            var cache = new NativeEvidenceCache(); var old = cache.Begin(sa.Key, 1)!; var currentB = cache.Begin(sb.Key, 1)!;
            _ = await pilot.ReadFrameAsync(sa.RunId, sa.Event.Sequence);
            Assert.False(cache.IsCurrent(old)); Assert.False(cache.Failed(old)); Assert.True(cache.IsCurrent(currentB));
            var newA = cache.Begin(sa.Key, 1)!; Assert.False(cache.IsCurrent(old)); Assert.False(cache.Failed(old));
            var newPage = cache.Begin(sa.Key, 2)!; Assert.False(cache.IsCurrent(newA)); Assert.False(cache.Failed(newA));
            Assert.True(cache.IsCurrent(newPage)); cache.Clear(); Assert.False(cache.IsCurrent(newPage));
        }
        finally { await pilot.StopAsync(); }
    }
}
