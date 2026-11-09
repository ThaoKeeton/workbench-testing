using System.Security.Cryptography;
using Workbench.Sdk;
using Workbench.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Workbench.Core.Tests;

public sealed class TestingNativeEvidenceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Same_event_explicit_refresh_reads_persisted_frame_after_actual_file_obstruction_is_released()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var set = await fixture.ImportAndGenerateAsync();
        await fixture.Controller.RunAsync([set.Cases[0].Id]);
        var run = Assert.IsType<TestRunSnapshot>(await fixture.Controller.WaitForRunSettlementAsync());
        Assert.Equal(TestRunState.Completed, run.State);
        var record = run.Events.First(e => e.Kind == TestRunEventKind.StepFinished);
        var selection = Assert.IsType<NativeEventSelection>(NativeEventSelection.Resolve(run, run.Plan.Id, new(run.Plan.Id, record.Sequence)));
        var evidenceId = Assert.Single(record.EvidenceIds);
        var framePath = Path.Combine(fixture.Disk.User, ".workbench-project", "testing", "runs", run.Plan.Id, "evidence", evidenceId, "frame.bmp");
        var persisted = await File.ReadAllBytesAsync(framePath);
        var cache = new NativeEvidenceCache();
        var first = Assert.IsType<NativeEvidenceRequest>(cache.Begin(selection.Key, generation: 1));
        Assert.Null(cache.Begin(selection.Key, generation: 1, retryFailed: true)); // A pending read is never duplicated.
        using (var obstruction = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await Assert.ThrowsAsync<PluginTestingException>(() => fixture.Controller.ReadEvidenceAsync(run.Plan.Id, evidenceId));
            Assert.Equal("StoreIoFailure", failure.ErrorCode);
            Assert.True(cache.Failed(first));
            Assert.Null(cache.Begin(selection.Key, generation: 1)); // Notifications do not cause an automatic retry loop.
            output.WriteLine("First actual Core evidence read refused by real exclusive frame handle: " + failure.ErrorCode);
        }
        var retry = Assert.IsType<NativeEvidenceRequest>(cache.Begin(selection.Key, generation: 1, retryFailed: true));
        Assert.NotSame(first, retry);
        var loaded = await fixture.Controller.ReadEvidenceAsync(run.Plan.Id, evidenceId);
        Assert.True(cache.IsCurrent(retry));
        Assert.Equal(persisted, loaded.Bytes);
        Assert.Equal(172854, loaded.Bytes.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(persisted)).ToLowerInvariant(), loaded.Receipt.Sha256);
        Assert.Equal(selection.Key, retry.Key);
        Assert.Null(cache.Begin(selection.Key, generation: 1, retryFailed: true)); // Loaded success remains deduplicated.
        output.WriteLine("Released obstruction; SAME run/event refresh loaded actual persisted SIMULATED BMP: " + loaded.Receipt.Sha256);
    }

    [Fact]
    public void Stale_failure_cannot_invalidate_new_run_same_key_reselection_or_new_page_generation()
    {
        var cache = new NativeEvidenceCache();
        var oldA = cache.Begin("runA/3", 1)!;
        var b = cache.Begin("runB/3", 1)!;
        Assert.False(cache.Failed(oldA)); Assert.True(cache.IsCurrent(b));
        var newA = cache.Begin("runA/3", 1)!;
        Assert.False(cache.Failed(oldA)); Assert.True(cache.IsCurrent(newA));
        Assert.Null(cache.Begin("runA/3", 1, retryFailed: true));
        var newPageA = cache.Begin("runA/3", 2)!;
        Assert.False(cache.Failed(newA)); Assert.True(cache.IsCurrent(newPageA));
        cache.Clear();
        Assert.False(cache.Failed(newPageA)); Assert.False(cache.IsCurrent(newPageA));
    }
}
