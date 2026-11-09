using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.OwnedLab;

namespace Workbench.Testing;

internal sealed partial class TestingPage
{
    private void Changed(TestingControllerSnapshot snapshot) => _ = NotifyAsync();
    private void OwnedChanged(OwnedPilotSnapshot snapshot) => _ = NotifyAsync();
    internal Task NotifyAsync(NativeReopening? reopening = null, bool retryEvidence = false) => _plugin.Owner.InternalAsync(async () =>
    {
        try
        {
            if (!_active || _plugin.PageProducersClosed) return;
            await DispatchAsync(() =>
            {
                try { if (_active && !_plugin.PageProducersClosed) Render(reopening, retryEvidence); }
                catch (Exception error) { ShowError(error); }
                return Task.CompletedTask;
            });
        }
        catch (Exception) { /* A retired/unavailable dispatcher cannot mutate a later page generation. */ }
    });
    internal Task RetireAsync()
    {
        Detach();
        return _plugin.Owner.InternalAsync(() => DispatchAsync(() => { _frame.Source = null; _ownedPanel.ClearFrame(); return Task.CompletedTask; }));
    }
    private void Retire() => Detach();
    internal void Detach()
    {
        lock (_subscriptionsSync)
        {
            _active = false; Interlocked.Increment(ref _generation);
            Controller.Changed -= Changed; _plugin.Pilot.Changed -= OwnedChanged;
            _ownedPanel.Detach();
        }
        _plugin.Detach(this);
    }
    private Task DispatchAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess) return action();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(); done.TrySetResult(); }
            catch (Exception error) { done.TrySetException(error); }
        })) done.TrySetException(new InvalidOperationException("Native dispatcher unavailable."));
        return done.Task;
    }
    private void Render(NativeReopening? reopening = null, bool retryEvidence = false)
    {
        if (!_active) return;
        var snapshot = Controller.Snapshot; // Always latest; a queued old Changed value must never restore stale text.
        var current = snapshot.SelectedRun;
        _rendering = true;
        try
        {
            var sourceIds = _plugin.SourceDraft.Reopen().SelectedSourceIds.ToHashSet(StringComparer.Ordinal);
            _sources.Items.Clear();
            foreach (var source in snapshot.Sources)
            {
                var item = new ListViewItem { Content = $"{source.Kind}: {source.Title} · {source.ItemCount} 项 · {source.Id}", Tag = source.Id };
                _sources.Items.Add(item); if (sourceIds.Contains(source.Id)) _sources.SelectedItems.Add(item);
            }
            _sets.Items.Clear();
            foreach (var set in snapshot.CaseSets)
            {
                var item = new ComboBoxItem { Content = $"{set.Name} · {set.CaseCount} 用例", Tag = set.Id };
                _sets.Items.Add(item); if (set.Id == snapshot.Draft?.Id) _sets.SelectedItem = item;
            }
            var draft = snapshot.Draft;
            if (_setId != draft?.Id) { _setId = draft?.Id; _caseId = null; _stepId = null; }
            _status.Text = draft is null ? "尚未选择用例集" : $"{(draft.IsDirty ? "有未保存编辑" : "已自动保存")} · 输入版本 {draft.Version} / 已保存 {draft.SavedVersion} · {(draft.IsSaving ? "正在写入" : draft.ErrorCode ?? snapshot.ErrorCode ?? "就绪")}";
            Set(_setName, draft?.Content.Name ?? "");
            _cases.Items.Clear();
            var cases = draft?.Content.Cases ?? [];
            var selected = cases.FirstOrDefault(c => c.Id == _caseId) ?? cases.FirstOrDefault();
            _caseId = selected?.Id;
            foreach (var c in cases)
            {
                var item = new ListViewItem { Content = $"{c.Title} · {(c.Steps.All(s => s.Action is not null) ? "虚拟映射" : "含手动步骤")}", Tag = c.Id };
                _cases.Items.Add(item); if (c.Id == _caseId) _cases.SelectedItem = item;
            }
            Set(_title, selected?.Title ?? ""); Set(_prerequisites, selected?.Prerequisites ?? "");
            var step = selected?.Steps.FirstOrDefault(s => s.Id == _stepId) ?? selected?.Steps.FirstOrDefault();
            _stepId = step?.Id; _steps.Items.Clear();
            if (selected is not null) foreach (var s in selected.Steps)
            {
                var item = new ComboBoxItem { Content = $"{selected.Steps.ToList().IndexOf(s) + 1}. {s.Instruction}", Tag = s.Id };
                _steps.Items.Add(item); if (s.Id == _stepId) _steps.SelectedItem = item;
            }
            Set(_instruction, step?.Instruction ?? ""); Set(_expected, step?.Expected ?? "");
            _mapping.Text = step is null ? "" : (step.Action is null ? "手动：不会自动通过" : $"SIMULATED {step.Action.Kind} {step.Action.ElementId} 值：{step.Action.Value}")
                + "\n" + string.Join("\n", step.Traces.Select(t => $"来源 {t.SnapshotId}@{t.SnapshotRevision} · 定位 {t.Locator} · 条件 {t.CriterionIndex}"));
            var admission = _plugin.Lifecycle.ReadAdmission(reopening);
            var editable = admission.UserActions && !_busy && !_plugin.SourceBusy && !snapshot.IsAuthoringBusy && !snapshot.IsPreparing && !snapshot.IsStopped;
            _sourcePanel.Render(editable, admission);
            foreach (var editor in new[] { _setName, _title, _prerequisites, _instruction, _expected }) editor.IsEnabled = editable && draft is not null;
            foreach (var button in _authorActions) button.IsEnabled = editable;
            foreach (var button in _activityActions) button.IsEnabled = editable && _plugin.CanStart(snapshot, admission);
            _sets.IsEnabled = _cases.IsEnabled = _steps.IsEnabled = _sources.IsEnabled = editable;
            _runs.IsEnabled = _events.IsEnabled = editable;
            _run.IsEnabled = editable && selected is not null && selected.Steps.All(s => s.Action is not null) && _plugin.CanStart(snapshot, admission);
            _cancel.IsEnabled = admission.UserActions && snapshot.OwnsRun && !snapshot.IsStopped;
            _retryDraft.Visibility = draft is { IsDirty: true, ErrorCode: not null } ? Visibility.Visible : Visibility.Collapsed;
            _preserveDraft.Visibility = draft is { IsDirty: true, ErrorCode: "RevisionConflict" } ? Visibility.Visible : Visibility.Collapsed;
            _retryRun.IsEnabled = editable && _plugin.CanStart(snapshot, admission) && snapshot.SelectedRun?.State is TestRunState.Failed or TestRunState.Cancelled or TestRunState.Interrupted;
            _ownedPanel.Render(admission, retryEvidence || reopening is not null);
            _runs.Items.Clear();
            foreach (var run in snapshot.Runs)
            {
                var item = new ComboBoxItem { Content = $"{run.State} · {run.Id} · 父 {run.ParentRunId ?? "无"}", Tag = run.Id };
                _runs.Items.Add(item); if (run.Id == snapshot.SelectedRun?.Plan.Id) _runs.SelectedItem = item;
            }
            _runDetails.Text = current is null ? "尚未选择执行" : $"SIMULATED {current.State} · run {current.Plan.Id} · 序列 {current.Sequence} · session {current.Plan.SessionId} · 冻结版本 {current.Plan.CaseRevision}\nhead {current.HeadHash}\n错误 {snapshot.ErrorCode ?? "无"}";
            var oldEvent = SelectedEventId();
            long? eventSequence = oldEvent?.RunId == current?.Plan.Id ? oldEvent?.Sequence : null;
            if (_runId != current?.Plan.Id) { _runId = current?.Plan.Id; eventSequence = null; _evidenceCache.Clear(); _frame.Source = null; }
            _displayedRun = current;
            _events.Items.Clear();
            if (current is not null) foreach (var e in current.Events)
            {
                var item = new ComboBoxItem { Content = $"{e.Sequence}: {e.Kind} · {e.Outcome} · {e.ErrorCode}", Tag = new KeyValuePair<string, long>(current.Plan.Id, e.Sequence) };
                _events.Items.Add(item); if (e.Sequence == eventSequence) _events.SelectedItem = item;
            }
            if (_events.SelectedItem is null && current is not null)
            {
                var last = current.Events.LastOrDefault(e => e.Kind == TestRunEventKind.StepFinished) ?? current.Events.LastOrDefault();
                _events.SelectedItem = _events.Items.OfType<ComboBoxItem>().LastOrDefault(i => i.Tag is KeyValuePair<string, long> key && key.Key == current.Plan.Id && key.Value == last?.Sequence);
            }
        }
        finally { _rendering = false; }
        SelectEvidence(current, current?.Plan.Id, SelectedEventId(), retryEvidence || reopening is not null);
    }
    private static void Set(TextBox editor, string value) { if (editor.Text != value) editor.Text = value; }
    private NativeEventId? SelectedEventId() => _events.SelectedItem is ComboBoxItem { Tag: KeyValuePair<string, long> key }
        ? new(key.Key, key.Value) : null;
    private void SelectEvidence(TestRunSnapshot? displayedRun, string? displayedRunId, NativeEventId? item, bool retryFailed = false)
    {
        // Use the exact projection that populated the visible list, never an independently newer controller run.
        var selection = NativeEventSelection.Resolve(displayedRun, displayedRunId, item);
        if (selection is null)
        {
            _evidenceCache.Clear(); _observed.Text = "尚未选择当前执行的事件"; _frame.Source = null;
            _frameStatus.Text = "SIMULATED — 等待当前执行的事件";
            if (item is not null) _ = NotifyAsync(); // Stale/missing item: defer to a coherent render, without a throwing lookup.
            return;
        }
        var record = selection.Event;
        _observed.Text = record.NativePlainTextState ?? $"{record.Kind} · {record.ErrorCode ?? ""}";
        var key = selection.Key;
        var request = _evidenceCache.Begin(key, _generation, retryFailed);
        if (request is null) return;
        _frame.Source = null;
        _frameStatus.Text = "SIMULATED — 正在读取该事件的真实证据";
        if (record.EvidenceIds.Count == 0) { _frame.Source = null; _frameStatus.Text = "SIMULATED — 此事件没有证据"; return; }
        var generation = _generation;
        _ = _plugin.Owner.InternalAsync(async () =>
        {
            try
            {
                var evidence = await Controller.ReadEvidenceAsync(selection.RunId, record.EvidenceIds[0]).ConfigureAwait(false);
                await DispatchAsync(async () =>
                {
                    if (!_active || generation != _generation || !_evidenceCache.IsCurrent(request)) return;
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    { writer.WriteBytes(evidence.Bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
                    stream.Seek(0);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    if (!_active || generation != _generation || !_evidenceCache.IsCurrent(request)) return;
                    _frame.Source = bitmap;
                    _frameStatus.Text = $"SIMULATED · 已解码 {bitmap.PixelWidth}×{bitmap.PixelHeight} · {evidence.Receipt.Length} 字节 · SHA256 {evidence.Receipt.Sha256}";
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                try
                {
                    await DispatchAsync(() =>
                    {
                        if (_active && generation == _generation && _evidenceCache.Failed(request))
                        {
                            _frameStatus.Text = "SIMULATED — 证据加载失败，可刷新重试：" + (error is PluginTestingException known ? SafeCode(known.ErrorCode) : "LocalFileOrTestingFailure");
                            ShowError(error);
                        }
                        return Task.CompletedTask;
                    });
                }
                catch (Exception) { /* The original owned dispatcher operation settled; a retired dispatcher cannot show an error. */ }
            }
        });
    }
}
