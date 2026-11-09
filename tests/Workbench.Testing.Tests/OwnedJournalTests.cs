using System.Text.Json;
using System.Diagnostics;
using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class OwnedJournalTests
{
    [Fact]
    public async Task Actual_process_crash_releases_owner_lock_and_restores_only_verified_interruption()
    {
        const string rootVariable = "PWB_OWNED_JOURNAL_CRASH_ROOT";
        const string signalVariable = "PWB_OWNED_JOURNAL_CRASH_SIGNAL";
        if (Environment.GetEnvironmentVariable(rootVariable) is { Length: > 0 } childRoot)
        {
            var scope = new OwnedScope("project", "com.projectworkbench.testing", childRoot);
            var journal = await OwnedJournal.OpenAsync(scope);
            var helper = new ScriptedDriver().Helper;
            var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), helper, TimeProvider.System);
            await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued));
            await File.WriteAllTextAsync(Environment.GetEnvironmentVariable(signalVariable)!, grant.Execution.RunId);
            Process.GetCurrentProcess().Kill(); // Deliberately bypass managed disposal of the actual owner.lock.
            throw new InvalidOperationException("Owned crash worker unexpectedly survived.");
        }
        using var rig = new OwnedLabTestRig(); var signal = Path.Combine(rig.Scope.DataDirectory, "crash-published");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(OwnedJournalTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=Workbench.Core.Tests.OwnedJournalTests.Actual_process_crash_releases_owner_lock_and_restores_only_verified_interruption");
        start.Environment[rootVariable] = rig.Scope.DataDirectory; start.Environment[signalVariable] = signal;
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Owned crash worker did not start.");
        var output = worker.StandardOutput.ReadToEndAsync(); var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await worker.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(); await Task.WhenAll(output, errors);
        }
        Assert.True(File.Exists(signal), (await output) + (await errors)); Assert.NotEqual(0, worker.ExitCode);
        var runId = await File.ReadAllTextAsync(signal);
        var path = Path.Combine(rig.Root, "attempts", runId, "events", "00000000.json"); var bytes = await File.ReadAllBytesAsync(path);
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.False(restored.IsReadOnly); Assert.Equal(OwnedRunState.Interrupted, restored.Read(runId).State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Equal(0, rig.Driver.PrepareCount);
    }

    [Fact]
    public async Task Preparation_input_and_received_BMP_are_copied_before_accepted_publication_awaits()
    {
        using var rig = new OwnedLabTestRig(); var inputGate = new AsyncGate();
        var files = new PublicationFiles { Match = p => Path.GetFileName(p) == "input.json", Before = inputGate };
        await using var journal = await OwnedJournal.OpenAsync(rig.Scope, files);
        var input = FixedOwnedPlans.Create(OwnedScenario.Positive); var bytes = input.Bytes.ToArray(); var expected = bytes.ToArray();
        var creating = journal.CreateAsync(input with { Bytes = bytes }, rig.Driver.Helper, rig.Clock);
        await inputGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); bytes[0] = 0; inputGate.Release(); var grant = await creating;
        Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(rig.Root, "attempts", grant.Execution.RunId, "input.json")));
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued));
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Started, Identity: rig.Driver.Identity, State: rig.Driver.InitialState));
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.StepStarted, StepIndex: 0));
        var frameGate = new AsyncGate(); files.Match = p => p.EndsWith(".bmp", StringComparison.Ordinal); files.Before = frameGate;
        var bmp = OwnedLabTestRig.Bmp(); var append = journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.StepResult, 0, OwnedOutcome.Passed, Identity: rig.Driver.Identity, State: new("Owned pilot task", "", "", "0"), Bmp: bmp));
        await frameGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); bmp[60] = 99; frameGate.Release();
        var result = await append; Assert.Equal(OwnedLabTestRig.Bmp(), await journal.ReadFrameAsync(grant.Execution.RunId, result.Sequence));
    }

    [Theory]
    [InlineData("input.json", 4194305)]
    [InlineData("execution.json", 4194304)]
    [InlineData("events/00000000.json", 16385)]
    [InlineData("commits/00000000.json", 1048577)]
    public async Task Oversize_stored_files_are_refused_without_repair_or_eviction(string relative, int length)
    {
        using var rig = new OwnedLabTestRig(); var journal = await OwnedJournal.OpenAsync(rig.Scope);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued)); await journal.DisposeAsync();
        var path = Path.Combine(rig.Root, "attempts", grant.Execution.RunId, relative);
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) stream.SetLength(length);
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope); Assert.True(restored.IsReadOnly);
        Assert.Equal(length, new FileInfo(path).Length); Assert.Single(Directory.GetDirectories(Path.Combine(rig.Root, "attempts")));
    }

    [Fact]
    public async Task Accepted_append_and_owner_disposal_join_actual_marker_IO()
    {
        using var rig = new OwnedLabTestRig(); var gate = new AsyncGate();
        var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal), Before = gate };
        var journal = await OwnedJournal.OpenAsync(rig.Scope, files);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        var append = journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var disposal = journal.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted); await Assert.ThrowsAsync<OwnedLabException>(() => OwnedJournal.OpenAsync(rig.Scope));
        gate.Release(); Assert.Equal(0, (await append).Sequence); await disposal;
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope); Assert.Equal(OwnedRunState.Interrupted, restored.Read(grant.Execution.RunId).State);
    }

    [Fact]
    public async Task Clean_interrupted_prefix_cannot_be_continued_in_place()
    {
        using var rig = new OwnedLabTestRig(); var journal = await OwnedJournal.OpenAsync(rig.Scope);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued)); await journal.DisposeAsync();
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.Equal("AdmissionClosed", (await Assert.ThrowsAsync<OwnedLabException>(() => restored.AppendAsync(grant.Execution.RunId,
            new(OwnedEventKind.Started, Identity: rig.Driver.Identity, State: rig.Driver.InitialState)))).ErrorCode);
        Assert.Equal(0, restored.Read(grant.Execution.RunId).Sequence);
    }

    [Fact]
    public async Task Unacknowledged_terminal_marker_recovers_as_stored_data_without_reexecuting()
    {
        using var rig = new OwnedLabTestRig(); var files = new PublicationFiles { Match = p => p.Contains("commits", StringComparison.Ordinal) && Path.GetFileName(p) == "00000010.json", ThrowAfter = true };
        var controller = rig.Controller(files); await controller.InitializeAsync(); var uncertain = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.RecoveryNeeded, uncertain.State); Assert.Equal(9, uncertain.Sequence); await controller.StopAsync();
        var restored = rig.Controller(); await restored.InitializeAsync(); var stored = await restored.SelectAsync(uncertain.Execution.RunId);
        Assert.Equal(OwnedRunState.Passed, stored.State); Assert.Equal(10, stored.Sequence); Assert.Equal(1, rig.Driver.LaunchCount);
        Assert.Equal(OwnedRunState.RecoveryNeeded, uncertain.State); Assert.Equal(9, uncertain.Sequence);
        await restored.StopAsync();
    }

    [Theory]
    [InlineData("frames/00000003.bmp")]
    [InlineData("events/00000003.json")]
    [InlineData("commits/00000003.json")]
    [InlineData("execution.json")]
    public async Task Corruption_stops_at_verified_prefix_and_keeps_corrupt_bytes(string relative)
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller(); await controller.InitializeAsync();
        var original = await controller.RunAsync(OwnedScenario.Positive); await controller.StopAsync();
        var path = Path.Combine(rig.Root, "attempts", original.Execution.RunId, relative);
        var bytes = await File.ReadAllBytesAsync(path); bytes[bytes.Length / 2] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope); Assert.True(restored.IsReadOnly);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        if (relative != "execution.json") { var prefix = restored.Read(original.Execution.RunId); Assert.Equal(2, prefix.Sequence); Assert.Equal(OwnedRunState.RecoveryNeeded, prefix.State); }
    }

    [Fact]
    public async Task Missing_referenced_frame_prevents_terminal_restore_and_retry()
    {
        using var rig = new OwnedLabTestRig(); var controller = rig.Controller(); await controller.InitializeAsync();
        var original = await controller.RunAsync(OwnedScenario.Positive); await controller.StopAsync();
        File.Delete(Path.Combine(rig.Root, "attempts", original.Execution.RunId, "frames", "00000003.bmp"));
        var restored = rig.Controller(); await restored.InitializeAsync();
        Assert.Equal(OwnedRunState.RecoveryNeeded, (await restored.SelectAsync(original.Execution.RunId)).State);
        Assert.Equal("RecoveryNeeded", (await Assert.ThrowsAsync<OwnedLabException>(() => restored.RetryAsync(original.Execution.RunId))).ErrorCode);
        Assert.Equal(1, rig.Driver.LaunchCount); await restored.StopAsync();
    }

    [Fact]
    public async Task Exclusive_owner_lock_is_held_until_actual_disposal()
    {
        using var rig = new OwnedLabTestRig();
        var journal = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.Equal("StoreIoFailure", (await Assert.ThrowsAsync<OwnedLabException>(() => OwnedJournal.OpenAsync(rig.Scope))).ErrorCode);
        await journal.DisposeAsync();
        await using var next = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.Empty(next.Runs);
    }

    [Fact]
    public async Task Queued_has_exact_immutable_prefix_and_clean_restart_is_interrupted_without_execution()
    {
        using var rig = new OwnedLabTestRig();
        var journal = await OwnedJournal.OpenAsync(rig.Scope);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        var queued = await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued));
        var attempt = Path.Combine(rig.Root, "attempts", grant.Execution.RunId);
        var eventBytes = await File.ReadAllBytesAsync(Path.Combine(attempt, "events", "00000000.json"));
        using var evt = JsonDocument.Parse(eventBytes);
        Assert.Equal(new[] { "schema", "sequence", "priorHash", "executionSha", "utc", "kind", "stepIndex", "outcome", "errorCode", "identity", "state", "frameSha", "captureKind", "syntheticTarget", "retirement", "uncertainStep" }, evt.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new string('0', 64), evt.RootElement.GetProperty("priorHash").GetString());
        Assert.Equal(0, queued.Sequence);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(eventBytes)), queued.HeadSha);
        Assert.Equal(grant.ExecutionBytes.ToArray(), await File.ReadAllBytesAsync(Path.Combine(attempt, "execution.json")));
        await journal.DisposeAsync();
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.Equal(OwnedRunState.Interrupted, restored.Read(grant.Execution.RunId).State);
        Assert.False(restored.IsReadOnly); Assert.Equal(0, rig.Driver.LaunchCount);
        Assert.Equal(eventBytes, await File.ReadAllBytesAsync(Path.Combine(attempt, "events", "00000000.json")));
    }

    [Theory]
    [InlineData(".pending-0123456789abcdef0123456789abcdef")]
    [InlineData("events/00000002.json")]
    [InlineData("commits/00000002.json")]
    [InlineData("frames/00000003.bmp")]
    public async Task Orphans_and_gaps_make_entire_root_read_only_and_preserve_all_bytes(string orphan)
    {
        using var rig = new OwnedLabTestRig();
        var journal = await OwnedJournal.OpenAsync(rig.Scope);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued)); await journal.DisposeAsync();
        var path = Path.Combine(rig.Root, "attempts", grant.Execution.RunId, orphan);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, [9, 8, 7]);
        await using var restored = await OwnedJournal.OpenAsync(rig.Scope);
        Assert.True(restored.IsReadOnly); Assert.True(restored.Read(grant.Execution.RunId).RecoveryNeeded);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(path));
        Assert.Equal("RecoveryNeeded", (await Assert.ThrowsAsync<OwnedLabException>(() => restored.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock))).ErrorCode);
    }

    [Theory]
    [InlineData("frames", false)] [InlineData("frames", true)]
    [InlineData("events", false)] [InlineData("events", true)]
    [InlineData("commits", false)] [InlineData("commits", true)]
    public async Task Publication_cuts_preserve_bytes_and_freeze_at_last_acknowledged_prefix(string stage, bool after)
    {
        using var rig = new OwnedLabTestRig();
        var files = new PublicationFiles { Match = p => p.Contains(Path.DirectorySeparatorChar + stage + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(p) == "00000003", ThrowBefore = !after, ThrowAfter = after };
        var controller = rig.Controller(files); await controller.InitializeAsync();
        var result = await controller.RunAsync(OwnedScenario.Positive);
        Assert.Equal(OwnedRunState.RecoveryNeeded, result.State); Assert.Equal(2, result.Sequence);
        Assert.Equal(1, rig.Driver.ActionCalls); Assert.Equal(1, rig.Driver.RetireCount);
        var attempt = Path.Combine(rig.Root, "attempts", result.Execution.RunId);
        Assert.Equal(stage != "frames" || after, File.Exists(Path.Combine(attempt, "frames", "00000003.bmp")));
        Assert.Equal(stage == "commits" && after, File.Exists(Path.Combine(attempt, "commits", "00000003.json")));
        var retained = Directory.GetFiles(attempt, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal("RecoveryNeeded", (await Assert.ThrowsAsync<OwnedLabException>(() => controller.RetryAsync(result.Execution.RunId))).ErrorCode);
        await controller.StopAsync();
        foreach (var item in retained) Assert.Equal(item.Value, await File.ReadAllBytesAsync(item.Key));
    }

    [Fact]
    public async Task No_overwrite_publication_keeps_existing_file_and_pending_bytes()
    {
        using var rig = new OwnedLabTestRig(); Directory.CreateDirectory(rig.Scope.DataDirectory);
        var path = Path.Combine(rig.Scope.DataDirectory, "immutable"); var files = new OwnedAtomicFiles();
        await files.PublishNewAsync(path, new byte[] { 1, 2 });
        await Assert.ThrowsAsync<IOException>(() => files.PublishNewAsync(path, new byte[] { 3, 4 }));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(path));
        var pending = Assert.Single(Directory.GetFiles(rig.Scope.DataDirectory, ".pending-*"));
        Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(pending));
    }

    [Fact]
    public async Task Attempt_limit_counts_all_32_directories_without_eviction()
    {
        using var rig = new OwnedLabTestRig(); await using var journal = await OwnedJournal.OpenAsync(rig.Scope);
        for (var i = 0; i < 32; i++)
        {
            var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
            await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued));
        }
        Assert.Equal("StoreLimitExceeded", (await Assert.ThrowsAsync<OwnedLabException>(() => journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock))).ErrorCode);
        Assert.Equal(32, Directory.GetDirectories(Path.Combine(rig.Root, "attempts")).Length);
    }

    [Fact]
    public async Task Illegal_terminal_and_reordered_event_are_rejected_before_publication()
    {
        using var rig = new OwnedLabTestRig(); await using var journal = await OwnedJournal.OpenAsync(rig.Scope);
        var grant = await journal.CreateAsync(FixedOwnedPlans.Create(OwnedScenario.Positive), rig.Driver.Helper, rig.Clock);
        await journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Queued));
        await Assert.ThrowsAsync<OwnedLabException>(() => journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Terminal, Outcome: OwnedOutcome.Passed, Retirement: new(false, null, null, 0), UncertainStep: false)));
        await Assert.ThrowsAsync<OwnedLabException>(() => journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.StepStarted, StepIndex: 1)));
        await Assert.ThrowsAsync<OwnedLabException>(() => journal.AppendAsync(grant.Execution.RunId, new(OwnedEventKind.Terminal, Outcome: (OwnedOutcome)99, Retirement: new(false, null, null, 0), UncertainStep: false)));
        Assert.Equal(0, journal.Read(grant.Execution.RunId).Sequence); Assert.False(journal.IsReadOnly);
    }
}
