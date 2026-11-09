using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing;
using Workbench.Testing.SourceAcceptance;
using Xunit;

namespace Workbench.Core.Tests;

public sealed partial class SourceAcceptanceTests
{
    private const string ReleaseSetting = "source-acceptance-release-v1";
    private const string HeldFile = "source-acceptance-release-v1.held.json";
    private const string ReleaseFile = "source-acceptance-release-v1.release.json";

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ReleaseBridge_actual_IO_remains_owned_after_close_cancel_Stop_and_notification_tail(bool ai, bool cancelCaller)
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        using var disk = new ReleaseDirectory(); var clock = new ReleaseClock();
        await EnableReleaseAsync(rig, disk, clock);
        TestSourceSnapshot? source = null;
        if (ai)
        {
            Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Happy)); source = await rig.ImportFigma();
            Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully); Assert.False(File.Exists(disk.Held));
        }
        var tail = new AsyncTestGate(); rig.Notify = tail.HoldAsync;
        Assert.True((await rig.Command("source-acceptance-arm-" + (ai ? "ai" : "figma") + "-hold")).Succeeded);
        var work = rig.Life.RetainActivityAsync(() => rig.Activity.RemoteSourceAsync(() => source?.Id, async (id, token) =>
        {
            if (ai) await rig.Controller.GenerateWithAiAsync([id!], NativeSourceCredentials.ForAction(rig.Context.Secrets, "openai-api-key"), token);
            else await rig.Controller.ImportFigmaAsync(Request, Criteria(), NativeSourceCredentials.ForAction(rig.Context.Secrets, "figma-token"), token);
            return true;
        }, _ => Task.CompletedTask, () => Task.CompletedTask));
        var owner = new SourceAcceptanceOwnership(rig.Ledger, rig.Figma, rig.Ai, rig.Life.StopAsync);
        Task? stop = null; byte[]? request = null;
        using var caller = new CancellationTokenSource();
        try
        {
            await clock.NextObservation(); request = ReleaseRequest(disk);
            AssertDescriptor(rig.Ledger, disk, ai ? "ai" : "figma");
            var savedSdkRelease = rig.Context.FindCommand("source-acceptance-release")!;
            await rig.Remote.RequestCancellationAsync(); rig.Context.Close(PluginStopReason.PluginDisabled);
            await rig.Context.CancelLifetimeAsync(); if (cancelCaller) caller.Cancel();
            stop = owner.StopAsync(caller.Token);
            Assert.Same(stop, owner.StopAsync()); Assert.False((await savedSdkRelease.ExecuteAsync(CancellationToken.None)).Succeeded);
            Assert.False(work.IsCompleted); Assert.False(stop.IsCompleted); Assert.False(rig.Owner.DrainAsync().IsCompleted);
            Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed);
            using (var state = State(rig.Ledger)) Assert.True(state.RootElement.GetProperty(ai ? "ai" : "figma").GetProperty("cancellationObserved").GetBoolean());
            using var observer = new CancellationTokenSource(); observer.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop.WaitAsync(observer.Token));
            clock.Advance(); await clock.NextObservation();
            Assert.False(work.IsCompleted); Assert.False(stop.IsCompleted);
            PublishRelease(disk, request); clock.Advance();
            await tail.Entered.Task.WaitAsync(Budget);
            Assert.False(stop.IsCompleted); Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed);
            tail.Release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            if (cancelCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            else await stop.WaitAsync(Budget);
            Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully);
            Assert.True(rig.Figma.IsDisposed); Assert.True(rig.Ai.IsDisposed);
            Assert.Equal(ai ? 1 : 0, (await new ProjectTestStore(rig.Disk.User, "default").ListSourcesAsync()).Count);
            Assert.Empty(await new ProjectTestStore(rig.Disk.User, "default").ListCaseSetsAsync());
        }
        finally
        {
            if (request is not null) { PublishRelease(disk, request); clock.Advance(); }
            rig.Ledger.ReleasePending(); tail.Release.TrySetResult();
            _ = await Record.ExceptionAsync(() => work);
            if (stop is not null) _ = await Record.ExceptionAsync(() => stop);
        }
    }

    [Fact]
    public async Task ReleaseBridge_disabled_is_one_typed_read_with_no_companion_IO_or_schema_command_change()
    {
        await using var rig = await Rig.CreateAsync();
        using var disk = new ReleaseDirectory(create: false);
        var settings = new ReleaseSettings(rig.Context.Settings);
        await SourceAcceptanceReleaseBridge.InitializeAsync(new ReleaseContext(rig.Context, disk.Data, settings), rig.Ledger);
        Assert.Equal(1, settings.Reads); Assert.Equal(typeof(JsonElement), settings.ReadType);
        Assert.Equal(ReleaseSetting, settings.ReadKey); Assert.Equal(JsonValueKind.String, settings.Default.ValueKind); Assert.Equal("", settings.Default.GetString());
        Assert.False(Directory.Exists(disk.Root)); Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully);
        Assert.Equal(10, rig.Context.Snapshot().Commands.Count);
        using var state = State(rig.Ledger);
        Assert.Equal(new[] { "ai", "figma", "fixtureId", "generation", "instanceId", "outcome", "phase", "pluginId", "projectId", "provider", "scenario", "schemaVersion" },
            state.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, state.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, Count(rig.Ledger, "figma", "entered")); Assert.Equal(0, Count(rig.Ledger, "ai", "entered")); Assert.Equal(0, rig.Protector.Reads);
        await rig.SeedSecrets(); Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); var held = rig.ImportFigma();
        try
        {
            await Until(() => Count(rig.Ledger, "figma", "accepted") == 1);
            Assert.False(held.IsCompleted); Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully); Assert.False(Directory.Exists(disk.Root));
            Assert.True((await rig.Command("source-acceptance-release")).Succeeded); await held.WaitAsync(Budget);
        }
        finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => held); }
    }

    [Theory]
    [InlineData("default")] [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task ReleaseBridge_captures_default_or_project_root_and_setting_once_before_await(string project)
    {
        await using var rig = await Rig.CreateAsync(project); await rig.SeedSecrets(); using var disk = new ReleaseDirectory(project); var clock = new ReleaseClock();
        await rig.Context.Settings.SetAsync(ReleaseSetting, disk.RunId);
        var gate = new AsyncTestGate(); var settings = new ReleaseSettings(rig.Context.Settings, gate);
        var context = new ReleaseContext(rig.Context, disk.Data, settings);
        var initializing = SourceAcceptanceReleaseBridge.InitializeAsync(context, rig.Ledger, timeProvider: clock);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget); context.Directory = Path.Combine(rig.Disk.User, "later-foreign-path");
            gate.Release.TrySetResult(); await initializing;
            await rig.Context.Settings.SetAsync(ReleaseSetting, "foreign-later-configuration");
            Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); var work = rig.ImportFigma();
            try
            {
                await clock.NextObservation(); AssertDescriptor(rig.Ledger, disk, "figma");
                Assert.Equal(1, settings.Reads); Assert.Equal(1, context.DataReads); Assert.False(Directory.Exists(context.Directory));
                PublishRelease(disk, ReleaseRequest(disk)); clock.Advance(); await work.WaitAsync(Budget); await rig.Ledger.JoinReleaseMonitorsAsync().WaitAsync(Budget);
            }
            finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => work); }
        }
        finally { gate.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => initializing); }
    }

    [Theory]
    [InlineData("null")] [InlineData("true")] [InlineData("3")] [InlineData("{}")] [InlineData("[]")]
    [InlineData("\"ABCDEF0123456789abcdef0123456789\"")] [InlineData("\"not-a-run\"")] [InlineData("\" \"")]
    public async Task ReleaseBridge_malformed_configured_type_or_run_fails_with_fixed_initialization_error(string json)
    {
        await using var rig = await Rig.CreateAsync(); using var disk = new ReleaseDirectory();
        await rig.Context.Settings.SetAsync(ReleaseSetting, JsonSerializer.Deserialize<JsonElement>(json));
        var settings = new ReleaseSettings(rig.Context.Settings);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => SourceAcceptanceReleaseBridge.InitializeAsync(new ReleaseContext(rig.Context, disk.Data, settings), rig.Ledger));
        Assert.Equal("Synthetic source release configuration refused.", error.Message); Assert.Equal(1, settings.Reads);
        Assert.False(File.Exists(disk.Held)); Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReleaseBridge_late_settings_completion_after_reentrant_Stop_cannot_enable_admission()
    {
        await using var rig = await Rig.CreateAsync(); using var disk = new ReleaseDirectory();
        await rig.Context.Settings.SetAsync(ReleaseSetting, disk.RunId);
        var gate = new AsyncTestGate(); var settings = new ReleaseSettings(rig.Context.Settings, gate);
        var initializing = SourceAcceptanceReleaseBridge.InitializeAsync(new ReleaseContext(rig.Context, disk.Data, settings), rig.Ledger);
        var owner = new SourceAcceptanceOwnership(rig.Ledger, rig.Figma, rig.Ai, rig.Life.StopAsync);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget); rig.Context.Close(PluginStopReason.PluginDisabled);
            await owner.StopAsync(); gate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<ArgumentException>(() => initializing);
            Assert.False(rig.Ledger.CommandsOpen); Assert.True(rig.Figma.IsDisposed); Assert.True(rig.Ai.IsDisposed);
            Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully); Assert.False(File.Exists(disk.Held));
        }
        finally { gate.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => initializing); }
    }

    [Theory]
    [InlineData("wrong-parent")] [InlineData("wrong-run")] [InlineData("wrong-project")] [InlineData("wrong-fixture")]
    [InlineData("extra-level")] [InlineData("linked-run")] [InlineData("linked-data")] [InlineData("dangling-data")]
    public async Task ReleaseBridge_refuses_nonexact_or_linked_dedicated_root_before_companion_IO(string mutation)
    {
        await using var rig = await Rig.CreateAsync(); using var disk = new ReleaseDirectory();
        await rig.Context.Settings.SetAsync(ReleaseSetting, disk.RunId);
        var path = disk.Data;
        if (mutation == "wrong-parent") path = path.Replace("ProjectWorkbench-source-acceptance-probe", "ProjectWorkbench-testing-probe", StringComparison.Ordinal);
        if (mutation == "wrong-run") path = path.Replace(disk.RunId, new string('a', 32), StringComparison.Ordinal);
        if (mutation == "wrong-project") path = Path.Combine(disk.Root, ".workspaces", "projects", new string('a', 32), "data", FixtureId);
        if (mutation == "wrong-fixture") path = Path.Combine(disk.Root, "data", "foreign.fixture");
        if (mutation == "extra-level") path = Path.Combine(disk.Data, "nested");
        if (mutation == "linked-run") { Directory.Delete(disk.Root, true); Directory.CreateSymbolicLink(disk.Root, rig.Disk.User); }
        if (mutation is "linked-data" or "dangling-data") { Directory.Delete(Path.GetDirectoryName(disk.Data)!, true); Directory.CreateSymbolicLink(Path.GetDirectoryName(disk.Data)!, mutation == "linked-data" ? rig.Disk.User : Path.Combine(rig.Disk.User, "absent")); }
        await Assert.ThrowsAsync<ArgumentException>(() => SourceAcceptanceReleaseBridge.InitializeAsync(new ReleaseContext(rig.Context, path, rig.Context.Settings), rig.Ledger));
        Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully);
        Assert.False(File.Exists(Path.Combine(rig.Disk.User, HeldFile)));
    }

    public static IEnumerable<object[]> InvalidReleaseSignals()
    {
        foreach (var field in new[] { "protocolVersion", "fixtureId", "pluginId", "runId", "projectId", "instanceId", "generation", "provider", "scenario", "releaseId", "action" })
        {
            yield return ["missing:" + field]; yield return ["type:" + field]; yield return ["wrong:" + field];
        }
        foreach (var mutation in new[] { "duplicate", "case-duplicate", "unknown", "oversize", "trailing", "depth", "invalid-utf8", "bom", "zero-generation", "overflow-generation", "fraction-generation", "linked-request", "dangling-request", "directory-request" }) yield return [mutation];
    }

    [Theory]
    [MemberData(nameof(InvalidReleaseSignals))]
    public async Task ReleaseBridge_actual_monitor_rejects_each_invalid_signal_without_releasing_held_IO(string mutation)
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets(); using var disk = new ReleaseDirectory(); var clock = new ReleaseClock();
        await EnableReleaseAsync(rig, disk, clock); Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold));
        var work = rig.ImportFigma();
        try
        {
            await clock.NextObservation(); var valid = ReleaseRequest(disk); var bytes = MutateRelease(valid, mutation);
            if (mutation is "linked-request" or "dangling-request")
            {
                var target = Path.Combine(rig.Disk.User, "request.json"); if (mutation == "linked-request") await File.WriteAllBytesAsync(target, valid);
                File.CreateSymbolicLink(disk.Request, target);
            }
            else if (mutation == "directory-request") Directory.CreateDirectory(disk.Request);
            else PublishRelease(disk, bytes);
            clock.Advance(); await clock.NextObservation();
            Assert.False(work.IsCompleted); Assert.False(rig.Ledger.JoinReleaseMonitorsAsync().IsCompleted);
            Assert.Equal(0, Count(rig.Ledger, "figma", "settled")); Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed);
            if (mutation == "directory-request") Directory.Delete(disk.Request); else File.Delete(disk.Request);
            PublishRelease(disk, valid); clock.Advance(); await work.WaitAsync(Budget); await rig.Ledger.JoinReleaseMonitorsAsync().WaitAsync(Budget);
            PublishRelease(disk, valid); Assert.Equal(1, Count(rig.Ledger, "figma", "settled"));
        }
        finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => work); _ = await Record.ExceptionAsync(rig.Ledger.JoinReleaseMonitorsAsync); }
    }

    [Fact]
    public async Task ReleaseBridge_old_generation_and_instance_nonce_cannot_retarget_and_open_SDK_release_joins_monitor()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets(); using var disk = new ReleaseDirectory(); var clock = new ReleaseClock();
        await EnableReleaseAsync(rig, disk, clock); Task<TestSourceSnapshot>? second = null;
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); var first = rig.ImportFigma();
        try
        {
            await clock.NextObservation(); var old = ReleaseRequest(disk);
            Assert.True((await rig.Command("source-acceptance-release")).Succeeded); await first.WaitAsync(Budget); await rig.Ledger.JoinReleaseMonitorsAsync().WaitAsync(Budget);
            Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); second = rig.ImportFigma();
            await clock.NextObservation(); var current = ReleaseRequest(disk); Assert.NotEqual(Encoding.UTF8.GetString(old), Encoding.UTF8.GetString(current));
            PublishRelease(disk, old); clock.Advance(); await clock.NextObservation(); Assert.False(second.IsCompleted);
            PublishRelease(disk, current); clock.Advance(); await second.WaitAsync(Budget); await rig.Ledger.JoinReleaseMonitorsAsync().WaitAsync(Budget);
            Assert.Equal(2, Count(rig.Ledger, "figma", "accepted"));
        }
        finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => first); if (second is not null) _ = await Record.ExceptionAsync(() => second); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReleaseBridge_acceptance_reserves_monitor_before_start_and_Stop_joins_that_exact_placeholder(bool innerFails)
    {
        await using var rig = await Rig.CreateAsync(); using var disk = new ReleaseDirectory(); var clock = new ReleaseClock();
        await EnableReleaseAsync(rig, disk, clock); Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold));
        var ticket = rig.Ledger.Enter(SourceAcceptanceProvider.Figma); Assert.True(rig.Ledger.Accept(ticket));
        var actualStop = new AsyncTestGate(); var disposals = 0;
        using var figma = new DisposalHandler(() => disposals++); using var ai = new DisposalHandler(() => disposals++);
        var first = new InvalidOperationException("Synthetic first inner Stop failure");
        var owner = new SourceAcceptanceOwnership(rig.Ledger, figma, ai, async _ => { await actualStop.HoldAsync(); if (innerFails) throw first; }); var stop = owner.StopAsync();
        try
        {
            await actualStop.Entered.Task.WaitAsync(Budget); actualStop.Release.TrySetResult();
            Assert.False(stop.IsCompleted); Assert.False(rig.Ledger.JoinReleaseMonitorsAsync().IsCompleted); Assert.Equal(0, disposals); Assert.False(File.Exists(disk.Held));
            rig.Ledger.StartReleaseMonitor(ticket); await clock.NextObservation();
            Assert.False(stop.IsCompleted); PublishRelease(disk, ReleaseRequest(disk)); clock.Advance();
            if (innerFails) Assert.Same(first, await Assert.ThrowsAsync<InvalidOperationException>(() => stop)); else await stop.WaitAsync(Budget);
            Assert.Equal(2, disposals); Assert.True(ticket.Release.Task.IsCompletedSuccessfully);
            rig.Ledger.Settle(ticket, "replied");
        }
        finally { actualStop.Release.TrySetResult(); rig.Ledger.StartReleaseMonitor(ticket); ticket.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => stop); }
    }

    [Fact]
    public async Task ReleaseBridge_actual_monitor_start_prefix_runs_outside_seal_lock_and_is_joined_by_Stop()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets(); using var disk = new ReleaseDirectory();
        var prefix = new AsyncTestGate(); var clock = new ReleaseClock { StartGate = prefix };
        await EnableReleaseAsync(rig, disk, clock); Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold));
        var work = Task.Run(() => rig.Life.RetainActivityAsync(() => rig.Activity.RemoteSourceAsync(() => true, async (_, token) =>
        {
            await rig.Controller.ImportFigmaAsync(Request, Criteria(), NativeSourceCredentials.ForAction(rig.Context.Secrets, "figma-token"), token); return true;
        }, _ => Task.CompletedTask, () => Task.CompletedTask)));
        var owner = new SourceAcceptanceOwnership(rig.Ledger, rig.Figma, rig.Ai, rig.Life.StopAsync);
        Task? stop = null; Task? invoking = null;
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await prefix.Entered.Task.WaitAsync(Budget);
            invoking = Task.Run(() => returned.TrySetResult(owner.StopAsync())); await invoking.WaitAsync(Budget); stop = await returned.Task;
            Assert.False(rig.Ledger.CommandsOpen); Assert.False(stop.IsCompleted); Assert.False(work.IsCompleted);
            Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed);
            prefix.Release.TrySetResult(); await clock.NextObservation(); PublishRelease(disk, ReleaseRequest(disk)); clock.Advance();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); await stop.WaitAsync(Budget);
            Assert.True(rig.Figma.IsDisposed); Assert.True(rig.Ai.IsDisposed);
        }
        finally
        {
            prefix.Release.TrySetResult(); rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => work);
            if (invoking is not null) _ = await Record.ExceptionAsync(() => invoking);
            if (returned.Task.IsCompletedSuccessfully) _ = await Record.ExceptionAsync(() => returned.Task.Result);
        }
    }

    [Fact]
    public async Task ReleaseBridge_observer_failure_and_handler_Dispose_never_release_actual_held_IO()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets(); using var disk = new ReleaseDirectory();
        var clock = new ReleaseClock { FailObservation = true }; await EnableReleaseAsync(rig, disk, clock);
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); var work = rig.ImportFigma();
        try
        {
            await clock.NextObservation(); rig.Figma.Dispose();
            Assert.False(work.IsCompleted); Assert.False(rig.Ledger.JoinReleaseMonitorsAsync().IsCompleted); Assert.Equal(0, Count(rig.Ledger, "figma", "settled"));
            Assert.True((await rig.Command("source-acceptance-release")).Succeeded); await work.WaitAsync(Budget);
            var error = await Assert.ThrowsAsync<IOException>(rig.Ledger.JoinReleaseMonitorsAsync);
            Assert.Equal("Synthetic source release observation failed.", error.Message);
            var owner = new SourceAcceptanceOwnership(rig.Ledger, rig.Figma, rig.Ai, rig.Life.StopAsync);
            Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => owner.StopAsync())); Assert.True(rig.Ai.IsDisposed);
        }
        finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => work); }
    }

    [Fact]
    public async Task ReleaseBridge_Stop_winner_refuses_new_hold_monitor_and_unvalidated_transport_cannot_publish()
    {
        await using var rig = await Rig.CreateAsync(); using var disk = new ReleaseDirectory(); var clock = new ReleaseClock();
        await EnableReleaseAsync(rig, disk, clock); Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold));
        using var client = new HttpClient(rig.Figma, false); using var wrong = new HttpRequestMessage(HttpMethod.Get, "https://synthetic.invalid/refused");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(wrong)); Assert.False(File.Exists(disk.Held));
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); var ticket = rig.Ledger.Enter(SourceAcceptanceProvider.Figma);
        var owner = new SourceAcceptanceOwnership(rig.Ledger, rig.Figma, rig.Ai, rig.Life.StopAsync); await owner.StopAsync();
        Assert.False(rig.Ledger.Accept(ticket)); rig.Ledger.StartReleaseMonitor(ticket);
        Assert.True(rig.Ledger.JoinReleaseMonitorsAsync().IsCompletedSuccessfully); Assert.False(File.Exists(disk.Held)); Assert.Equal(0, clock.Observations);
        rig.Ledger.Settle(ticket, "rejected");
    }

    [Theory]
    [InlineData(false, "directory-held")] [InlineData(true, "directory-held")]
    [InlineData(false, "linked-held")] [InlineData(false, "dangling-held")]
    [InlineData(false, "linked-staging")] [InlineData(false, "directory-staging")] [InlineData(false, "stale-staging")]
    public async Task ReleaseBridge_descriptor_failure_is_retained_until_explicit_open_context_cleanup_and_preserves_first_failure(bool innerFails, string target)
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets(); using var disk = new ReleaseDirectory(); var clock = new ReleaseClock();
        await EnableReleaseAsync(rig, disk, clock);
        var staging = Path.Combine(disk.Data, "source-acceptance-release-v1.held.tmp"); var outside = Path.Combine(rig.Disk.User, "held-outside.json");
        var preserved = Encoding.UTF8.GetBytes("Preserved synthetic marker"); await File.WriteAllBytesAsync(outside, preserved);
        if (target == "directory-held") Directory.CreateDirectory(disk.Held);
        else if (target == "directory-staging") Directory.CreateDirectory(staging);
        else if (target == "stale-staging") await File.WriteAllBytesAsync(staging, preserved);
        else File.CreateSymbolicLink(target == "linked-staging" ? staging : disk.Held, target == "dangling-held" ? outside + ".absent" : outside);
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Hold)); var work = rig.ImportFigma();
        var first = new InvalidOperationException("Synthetic inner Stop failure"); var disposalCount = 0;
        using var figma = new DisposalHandler(() => { disposalCount++; throw new IOException("Synthetic disposal failure"); });
        using var ai = new DisposalHandler(() => disposalCount++);
        var owner = new SourceAcceptanceOwnership(rig.Ledger, figma, ai, async _ => { await rig.Life.StopAsync(); if (innerFails) throw first; });
        Task? stop = null;
        try
        {
            await clock.NextObservation(); using var observer = new CancellationTokenSource(); observer.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(observer.Token));
            Assert.False(work.IsCompleted); Assert.False(rig.Ledger.JoinReleaseMonitorsAsync().IsCompleted);
            Assert.True((await rig.Command("source-acceptance-release")).Succeeded); await work.WaitAsync(Budget);
            var monitorError = await Assert.ThrowsAsync<IOException>(rig.Ledger.JoinReleaseMonitorsAsync);
            Assert.Equal("Synthetic source release descriptor publication failed.", monitorError.Message);
            stop = owner.StopAsync(observer.Token); var error = await Record.ExceptionAsync(() => stop);
            if (innerFails) Assert.Same(first, error); else Assert.Same(monitorError, error);
            Assert.Equal(2, disposalCount); Assert.Same(stop, owner.StopAsync());
            Assert.Equal(preserved, await File.ReadAllBytesAsync(outside));
            if (target == "stale-staging") Assert.Equal(preserved, await File.ReadAllBytesAsync(staging));
        }
        finally { rig.Ledger.ReleasePending(); _ = await Record.ExceptionAsync(() => work); if (stop is not null) _ = await Record.ExceptionAsync(() => stop); }
    }

    private static async Task EnableReleaseAsync(Rig rig, ReleaseDirectory disk, ReleaseClock clock)
    {
        await rig.Context.Settings.SetAsync(ReleaseSetting, disk.RunId);
        await SourceAcceptanceReleaseBridge.InitializeAsync(new ReleaseContext(rig.Context, disk.Data, rig.Context.Settings), rig.Ledger, timeProvider: clock);
    }
    private static void AssertDescriptor(SourceAcceptanceScenarioLedger ledger, ReleaseDirectory disk, string provider)
    {
        var bytes = File.ReadAllBytes(disk.Held); Assert.InRange(bytes.Length, 1, 2048); using var document = JsonDocument.Parse(bytes); using var state = State(ledger);
        var root = document.RootElement;
        Assert.Equal(new[] { "fixtureId", "generation", "instanceId", "pluginId", "projectId", "protocolVersion", "provider", "releaseId", "runId", "scenario" }, root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32()); Assert.Equal(FixtureId, root.GetProperty("fixtureId").GetString()); Assert.Equal(FixtureId, root.GetProperty("pluginId").GetString());
        Assert.Equal(disk.RunId, root.GetProperty("runId").GetString()); Assert.Equal(disk.Project, root.GetProperty("projectId").GetString());
        Assert.Equal(state.RootElement.GetProperty("instanceId").GetString(), root.GetProperty("instanceId").GetString());
        Assert.Equal(state.RootElement.GetProperty("generation").GetInt64(), root.GetProperty("generation").GetInt64());
        Assert.Equal(provider, root.GetProperty("provider").GetString()); Assert.Equal("hold", root.GetProperty("scenario").GetString()); Assert.Matches("^[0-9a-f]{32}$", root.GetProperty("releaseId").GetString()!);
        Assert.False(File.Exists(Path.Combine(disk.Data, "source-acceptance-release-v1.held.tmp")));
    }
    private static byte[] ReleaseRequest(ReleaseDirectory disk)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllBytes(disk.Held))!;
        values.Add("action", JsonSerializer.SerializeToElement("release")); return JsonSerializer.SerializeToUtf8Bytes(values);
    }
    private static byte[] MutateRelease(byte[] valid, string mutation)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valid)!;
        if (mutation.StartsWith("missing:", StringComparison.Ordinal)) values.Remove(mutation[8..]);
        else if (mutation.StartsWith("type:", StringComparison.Ordinal)) values[mutation[5..]] = JsonSerializer.SerializeToElement(true);
        else if (mutation.StartsWith("wrong:", StringComparison.Ordinal))
        {
            var field = mutation[6..]; values[field] = field is "generation" or "protocolVersion" ? JsonSerializer.SerializeToElement(999) : JsonSerializer.SerializeToElement(field switch
            {
                "instanceId" or "runId" or "releaseId" => new string('a', 32),
                "provider" => "ai", "projectId" => new string('b', 32), _ => "foreign"
            });
        }
        else if (mutation == "zero-generation") values["generation"] = JsonSerializer.SerializeToElement(0);
        else if (mutation == "overflow-generation") values["generation"] = JsonSerializer.Deserialize<JsonElement>("9223372036854775808");
        else if (mutation == "fraction-generation") values["generation"] = JsonSerializer.SerializeToElement(1.5);
        else if (mutation == "unknown") values.Add("unknown", JsonSerializer.SerializeToElement("value"));
        else if (mutation == "depth") values.Add("unknown", JsonSerializer.Deserialize<JsonElement>("[[[[[[[[[0]]]]]]]]]"));
        else if (mutation == "oversize") return Encoding.UTF8.GetBytes(new string(' ', 2049));
        else if (mutation == "invalid-utf8") return [0xc3, 0x28];
        else if (mutation == "bom") return [0xef, 0xbb, 0xbf, .. valid];
        else if (mutation == "trailing") return [.. valid, .. Encoding.UTF8.GetBytes("{}")];
        else if (mutation is "duplicate" or "case-duplicate") return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid)[..^1] + ",\"" + (mutation == "duplicate" ? "runId" : "RUNID") + "\":\"" + values["runId"].GetString() + "\"}");
        return JsonSerializer.SerializeToUtf8Bytes(values);
    }
    private static void PublishRelease(ReleaseDirectory disk, byte[] bytes)
    {
        var staging = Path.Combine(disk.Data, "source-acceptance-release-v1.release.tmp");
        using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
        File.Move(staging, disk.Request, true);
    }
    private sealed class ReleaseDirectory : IDisposable
    {
        internal string RunId { get; } = Guid.NewGuid().ToString("N");
        internal string Project { get; }
        internal string Root => Path.Combine(Path.GetTempPath(), "ProjectWorkbench-source-acceptance-probe", RunId);
        internal string Data => Project == "default" ? Path.Combine(Root, "data", FixtureId) : Path.Combine(Root, ".workspaces", "projects", Project, "data", FixtureId);
        internal string Held => Path.Combine(Data, HeldFile);
        internal string Request => Path.Combine(Data, ReleaseFile);
        internal ReleaseDirectory(string project = "default", bool create = true) { Project = project; if (create) Directory.CreateDirectory(Data); }
        public void Dispose() { if (Directory.Exists(Root) || new DirectoryInfo(Root).LinkTarget is not null) Directory.Delete(Root, true); }
    }
    private sealed class ReleaseClock : TimeProvider
    {
        private readonly FakeTimeProvider _actual = new();
        private readonly Channel<int> _observations = Channel.CreateUnbounded<int>();
        internal int Observations;
        internal AsyncTestGate? StartGate;
        internal bool FailObservation;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(25), dueTime);
            if (StartGate is { } prefix) { prefix.Entered.TrySetResult(); prefix.Release.Task.GetAwaiter().GetResult(); }
            if (FailObservation) { _observations.Writer.TryWrite(Interlocked.Increment(ref Observations)); throw new IOException("Synthetic observation failure"); }
            var timer = _actual.CreateTimer(callback, state, dueTime, period);
            _observations.Writer.TryWrite(Interlocked.Increment(ref Observations)); return timer;
        }
        internal async Task NextObservation() => _ = await _observations.Reader.ReadAsync().AsTask().WaitAsync(Budget);
        internal void Advance() => _actual.Advance(TimeSpan.FromMilliseconds(25));
    }
    private sealed class ReleaseSettings(IPluginSettings actual, AsyncTestGate? gate = null) : IPluginSettings
    {
        internal int Reads; internal Type? ReadType; internal string? ReadKey; internal JsonElement Default;
        public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default)
        {
            Reads++; ReadType = typeof(T); ReadKey = key; if (defaultValue is JsonElement json) Default = json;
            var value = await actual.GetAsync(key, defaultValue, cancellationToken); if (gate is not null) await gate.HoldAsync(); return value;
        }
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) => actual.SetAsync(key, value, cancellationToken);
    }
    private sealed class ReleaseContext(IPluginTestingContext actual, string directory, IPluginSettings settings) : IPluginTestingContext
    {
        internal string Directory = directory; internal int DataReads;
        public string PluginId => actual.PluginId; public string DataDirectory { get { DataReads++; return Directory; } } public CancellationToken Lifetime => actual.Lifetime;
        public IPluginSettings Settings => settings; public IPluginBackgroundTasks BackgroundTasks => actual.BackgroundTasks; public IPluginSecretStore Secrets => actual.Secrets;
        public PluginStopReason StopReason => actual.StopReason; public IProjectContext Project => actual.Project; public IProjectTestWorkspace Testing => actual.Testing;
        public void RegisterBeforeProjectLeave(Func<CancellationToken, Task> hook) => actual.RegisterBeforeProjectLeave(hook);
        public void RegisterView(IPluginView view) => actual.RegisterView(view); public void RegisterCommand(PluginCommand command) => actual.RegisterCommand(command); public void RegisterSetting(PluginSetting setting) => actual.RegisterSetting(setting);
    }
}
