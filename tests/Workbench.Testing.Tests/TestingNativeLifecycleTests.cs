using Workbench.Sdk;
using Workbench.Testing;
using Workbench.Testing.Domain.OwnedLab;
using Xunit;

namespace Workbench.Core.Tests;

[Collection(BlockingCallbackCollection.Name)]
public sealed class TestingNativeLifecycleTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static Task Notify() => Task.CompletedTask;

    [Fact]
    public async Task Command_path_retains_a_fresh_render_after_actual_reservation_release()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var owner = new NativeActionOwner();
        var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var final = new AsyncTestGate(); var fresh = new AsyncTestGate(); NativeTestingLifecycle? life = null;
        life = new(owner, activity, source, owned, fixture.Controller, pilot, async _ => { Assert.True(activity.CanAdmit); Assert.True(life!.ReadAdmission().ActivityStarts); await fresh.HoldAsync(); }, Notify);
        var work = life.RetainActivityAsync(() => activity.SourceAsync(() => 5, Task.FromResult, async () => { Assert.False(activity.CanAdmit); await final.HoldAsync(); }));
        try
        {
            await Task.WhenAny(final.Entered.Task, work).WaitAsync(Budget); Assert.True(final.Entered.Task.IsCompleted); Assert.False(work.IsCompleted); final.Release.TrySetResult();
            await Task.WhenAny(fresh.Entered.Task, work).WaitAsync(Budget); Assert.True(fresh.Entered.Task.IsCompleted);
            Assert.False(work.IsCompleted); Assert.False(owner.DrainAsync().IsCompleted);
        }
        finally { final.Release.TrySetResult(); fresh.Release.TrySetResult(); await work; await life.StopAsync(); await pilot.StopAsync(); }
    }

    [Theory]
    [InlineData(0, true)] [InlineData(1, false)] [InlineData(2, false)]
    public async Task Leave_reopening_ticket_discounts_only_its_own_seals_on_fresh_dispatch(int barrier, bool allowed)
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var owner = new NativeActionOwner();
        var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        NativeTestingLifecycle? life = null; NativeReopening? saved = null; bool? projection = null; IDisposable? nested = null;
        life = new(owner, activity, source, owned, fixture.Controller, pilot, ticket =>
        {
            if (ticket is null) return Notify(); saved = ticket;
            if (barrier == 1) nested = activity.Seal(); if (barrier == 2) life!.SealLifetime();
            projection = life!.ReadAdmission(ticket).ActivityStarts; return Notify();
        }, Notify);
        try
        {
            await life.PrepareLeaveAsync(); Assert.NotNull(saved); Assert.Equal(allowed, projection);
            if (barrier == 1) Assert.False(life.ReadAdmission(saved).ActivityStarts);
            nested?.Dispose(); nested = null;
            if (barrier != 2) Assert.True(life.ReadAdmission(saved).ActivityStarts);
        }
        finally { nested?.Dispose(); await life.StopAsync(); await pilot.StopAsync(); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task Production_Stop_holds_journal_through_owned_preparation_launch_retirement_and_UI_retirement(int phase)
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig(); var gate = new AsyncGate();
        if (phase == 0) rig.Driver.PreparationGate = gate; else if (phase == 1) rig.Driver.LaunchGate = gate; else rig.Driver.RetirementGate = gate;
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var owner = new NativeActionOwner();
        var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var retire = new AsyncTestGate(); var life = new NativeTestingLifecycle(owner, activity, source, owned, fixture.Controller, pilot, _ => Notify(), retire.HoldAsync);
        var bridge = new NativeOwnedPilot(pilot, activity, owned);
        var work = owner.UserAsync(() => life.RetainActivityAsync(() => bridge.RunAsync(() => OwnedScenario.Positive, Notify))); Task? stop = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Budget); stop = life.StopAsync(); Assert.False(stop.IsCompleted);
            Assert.IsType<OwnedLabException>(await Record.ExceptionAsync(() => OwnedJournal.OpenAsync(rig.Scope)));
            gate.Release(); await Task.WhenAny(retire.Entered.Task, stop).WaitAsync(Budget); Assert.True(retire.Entered.Task.IsCompleted);
            Assert.False(stop.IsCompleted); Assert.True(fixture.Controller.Snapshot.IsStopped);
            Assert.IsType<OwnedLabException>(await Record.ExceptionAsync(() => OwnedJournal.OpenAsync(rig.Scope)));
            retire.Release.TrySetResult(); await stop.WaitAsync(Budget);
            if (phase < 2) Assert.Equal(0, rig.Driver.ResumeCount);
            await using var reopened = await OwnedJournal.OpenAsync(rig.Scope);
        }
        finally
        {
            gate.Release(); retire.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => work);
            if (stop is not null) _ = await Record.ExceptionAsync(() => stop); await pilot.StopAsync();
        }
    }

    [Fact]
    public async Task Throwing_source_cancel_still_joins_action_virtual_cleanup_and_last_bitmap_before_journal_release()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var owner = new NativeActionOwner();
        var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var action = new AsyncTestGate(); var callback = new AsyncTestGate(); var bitmap = new AsyncTestGate(); var retired = false;
        var life = new NativeTestingLifecycle(owner, activity, source, owned, fixture.Controller, pilot, _ => Notify(), () => { retired = true; return Notify(); });
        var decode = owner.InternalAsync(bitmap.HoldAsync);
        var work = owner.UserAsync(() => life.RetainActivityAsync(() => activity.RemoteSourceAsync(() => 1, async (value, token) =>
        {
            using var registration = token.Register(() => { callback.HoldAsync().GetAwaiter().GetResult(); throw new InvalidOperationException("synthetic callback"); });
            await action.HoldAsync(); return value;
        }, _ => Notify(), Notify))); Task? stop = null;
        try
        {
            await action.Entered.Task.WaitAsync(Budget); stop = life.StopAsync();
            await Task.WhenAny(callback.Entered.Task, stop).WaitAsync(Budget); Assert.True(callback.Entered.Task.IsCompleted);
            action.Release.TrySetResult(); Assert.False(stop.IsCompleted); callback.Release.TrySetResult();
            Assert.False(stop.IsCompleted); Assert.IsType<OwnedLabException>(await Record.ExceptionAsync(() => OwnedJournal.OpenAsync(rig.Scope)));
            bitmap.Release.TrySetResult(); await decode;
            Assert.NotNull(await Record.ExceptionAsync(() => stop.WaitAsync(Budget)));
            Assert.True(retired); Assert.True(fixture.Controller.Snapshot.IsStopped);
            await using var reopened = await OwnedJournal.OpenAsync(rig.Scope);
        }
        finally { action.Release.TrySetResult(); callback.Release.TrySetResult(); bitmap.Release.TrySetResult(); await decode; _ = await Record.ExceptionAsync(() => work); if (stop is not null) _ = await Record.ExceptionAsync(() => stop); await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Leave_joins_the_notification_created_by_external_pilot_seal_disposal()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var owner = new NativeActionOwner();
        var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var ui = new AsyncTestGate(); var seenClosed = false; var reopening = false;
        void Changed(OwnedPilotSnapshot snapshot)
        {
            if (snapshot.AdmissionClosed) seenClosed = true;
            else if (seenClosed) _ = owner.InternalAsync(ui.HoldAsync);
        }
        pilot.Changed += Changed;
        var life = new NativeTestingLifecycle(owner, activity, source, owned, fixture.Controller, pilot, ticket => { if (ticket is not null) reopening = true; return Notify(); }, () => { pilot.Changed -= Changed; return Notify(); });
        var leave = life.PrepareLeaveAsync();
        try
        {
            await Task.WhenAny(ui.Entered.Task, leave).WaitAsync(Budget); Assert.True(ui.Entered.Task.IsCompleted);
            Assert.False(leave.IsCompleted); Assert.False(reopening); Assert.False(life.ReadAdmission().UserActions);
            ui.Release.TrySetResult(); await leave.WaitAsync(Budget); Assert.True(reopening);
        }
        finally { ui.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => leave); pilot.Changed -= Changed; await life.StopAsync(); await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Partial_and_uninitialized_Stop_revokes_lifetime_without_requiring_a_journal()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); var owner = new NativeActionOwner(); var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation();
        var life = new NativeTestingLifecycle(owner, null, source, owned, fixture.Controller, pilot, _ => Notify(), Notify);
        life.SealLifetime(); Assert.False(owner.AdmitsUsers);
        await Assert.ThrowsAsync<OwnedLabException>(() => pilot.InitializeAsync());
        await life.StopAsync(); Assert.True(fixture.Controller.Snapshot.IsStopped);
        await Assert.ThrowsAsync<PluginTestingException>(() => owner.UserAsync(Notify)); await pilot.StopAsync();
        var empty = new NativeTestingLifecycle(new(), null, new(), new(), null, null, _ => Notify(), Notify); await empty.StopAsync();
    }

    [Fact]
    public async Task Exact_cancel_and_drain_waits_for_original_entry_and_never_cancels_a_later_one()
    {
        var operation = new NativeRemoteOperation(); var gate = new AsyncTestGate(); var callback = new AsyncTestGate();
        var original = new InvalidOperationException("original invocation");
        var first = operation.RunAsync<int>(async token =>
        {
            using var registration = token.Register(() => { callback.HoldAsync().GetAwaiter().GetResult(); throw new IOException("cancel callback"); });
            await gate.HoldAsync(); throw original;
        });
        await gate.Entered.Task.WaitAsync(Budget); var cancel = operation.RequestCancellationAndDrainAsync();
        try
        {
            await callback.Entered.Task.WaitAsync(Budget); gate.Release.TrySetResult(); Assert.False(cancel.IsCompleted);
            callback.Release.TrySetResult(); Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
            Assert.NotNull(await Record.ExceptionAsync(() => cancel));
            Assert.True(await operation.RunAsync(token => Task.FromResult(!token.IsCancellationRequested)));
        }
        finally { gate.Release.TrySetResult(); callback.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => first); _ = await Record.ExceptionAsync(() => cancel); }
    }

    [Fact]
    public async Task Cancel_and_drain_joins_closed_cancellation_admission_through_actual_settlement()
    {
        var operation = new NativeRemoteOperation(); var ui = new AsyncTestGate();
        var work = operation.RunAsync(_ => Task.FromResult(42), _ => ui.HoldAsync());
        await ui.Entered.Task.WaitAsync(Budget); var cancel = operation.RequestCancellationAndDrainAsync();
        try { Assert.False(cancel.IsCompleted); Assert.False(work.IsCompleted); Assert.True(operation.IsBusy); }
        finally { ui.Release.TrySetResult(); await cancel; Assert.Equal(42, await work); }
        Assert.True(await operation.RunAsync(token => Task.FromResult(!token.IsCancellationRequested)));
    }

    [Fact]
    public async Task Rendering_never_discounts_foreign_disposed_nested_active_or_terminal_barriers()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var owner = new NativeActionOwner(); var remote = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, remote);
        using var own = owner.Seal(); using var foreign = new NativeActionOwner().Seal();
        Assert.True(owner.CanAdmitForRender(own)); Assert.False(owner.CanAdmitForRender(foreign));
        var disposed = owner.Seal(); disposed.Dispose(); Assert.False(owner.CanAdmitForRender(disposed));
        using (var nested = owner.Seal()) Assert.False(owner.CanAdmitForRender(own));
        using var activitySeal = activity.Seal(); using var remoteSeal = remote.Seal();
        Assert.True(activity.CanAdmitForRender(activitySeal)); Assert.False(activity.CanAdmitForRender(own));
        Assert.True(remote.CanAdmitForRender(remoteSeal)); Assert.False(remote.CanAdmitForRender(activitySeal));
        activitySeal.Dispose(); remoteSeal.Dispose();
        var gate = new AsyncTestGate(); var work = activity.RemoteSourceAsync(() => 1, async (x, _) => { await gate.HoldAsync(); return x; }, _ => Notify(), Notify);
        try
        {
            await gate.Entered.Task.WaitAsync(Budget);
            using var during = activity.Seal(); using var duringRemote = remote.Seal();
            Assert.False(activity.CanAdmitForRender(during)); Assert.False(remote.CanAdmitForRender(duringRemote));
            owner.Terminal(); activity.Terminal(); remote.Terminal();
            Assert.False(owner.CanAdmitForRender(own)); Assert.False(activity.CanAdmitForRender(during)); Assert.False(remote.CanAdmitForRender(duringRemote));
        }
        finally { gate.Release.TrySetResult(); await work; }
    }

    [Fact]
    public async Task Stop_joins_in_progress_Leave_and_permanently_closes_its_queued_reopening_render()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        var pilot = rig.Controller(); await pilot.InitializeAsync(); var owner = new NativeActionOwner();
        var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation(); var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var render = new AsyncTestGate(); var retired = false; NativeTestingLifecycle? life = null;
        life = new(owner, activity, source, owned, fixture.Controller, pilot, async ticket =>
        {
            if (ticket is null) return;
            await render.HoldAsync(); Assert.False(life!.ReadAdmission(ticket).UserActions);
        }, () => { retired = true; return Notify(); });
        var leave = life.PrepareLeaveAsync(); Task? stop = null;
        try
        {
            await render.Entered.Task.WaitAsync(Budget); stop = life.StopAsync();
            Assert.False(stop.IsCompleted); Assert.False(retired); Assert.False(owner.AdmitsUsers);
            Assert.IsType<OwnedLabException>(await Record.ExceptionAsync(() => OwnedJournal.OpenAsync(rig.Scope)));
            render.Release.TrySetResult(); await leave.WaitAsync(Budget); await stop.WaitAsync(Budget); Assert.True(retired);
            await using var reopened = await OwnedJournal.OpenAsync(rig.Scope);
        }
        finally { render.Release.TrySetResult(); _ = await Record.ExceptionAsync(() => leave); if (stop is not null) _ = await Record.ExceptionAsync(() => stop); await pilot.StopAsync(); }
    }

    [Fact]
    public async Task Failed_pilot_initialization_still_stops_the_initialized_virtual_controller()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync(); using var rig = new OwnedLabTestRig();
        Directory.CreateDirectory(rig.Root);
        using var obstruction = new FileStream(Path.Combine(rig.Root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pilot = rig.Controller(); var owner = new NativeActionOwner(); var source = new NativeRemoteOperation(); var owned = new NativeRemoteOperation();
        var activity = new NativeActivityCoordinator(fixture.Controller, source);
        var life = new NativeTestingLifecycle(owner, activity, source, owned, fixture.Controller, pilot, _ => Notify(), Notify);
        Assert.Equal("StoreIoFailure", (await Assert.ThrowsAsync<OwnedLabException>(() => pilot.InitializeAsync())).ErrorCode);
        await life.StopAsync(); Assert.True(fixture.Controller.Snapshot.IsStopped); Assert.False(owner.AdmitsUsers); Assert.Equal(0, rig.Driver.PrepareCount);
    }
}
