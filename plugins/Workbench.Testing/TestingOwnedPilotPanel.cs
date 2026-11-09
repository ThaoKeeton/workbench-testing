using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Workbench.Sdk;
using Workbench.Testing.Domain.OwnedLab;

namespace Workbench.Testing;

/// <summary>Production controls over the private owned journal; never a source-to-action adapter.</summary>
internal sealed class TestingOwnedPilotPanel : StackPanel
{
    private const string RecoveryCopy = "执行记录或资源状态尚未确认，已暂停新增执行并保留原始数据。当前版本暂不支持修复或重试。";
    private readonly TestingPlugin _plugin;
    private readonly ComboBox _scenario = Id(new ComboBox { Header = "固定合成场景", HorizontalAlignment = HorizontalAlignment.Stretch }, "scenario");
    private readonly Button _run = Id(new Button { Content = "运行固定场景" }, "run");
    private readonly Button _cancel = Id(new Button { Content = "取消并等待结束" }, "cancel");
    private readonly Button _retry = Id(new Button { Content = "使用当前已安装的驱动，从第 1 步开始新执行" }, "retry");
    private readonly ComboBox _history = Id(new ComboBox { Header = "受控执行历史", HorizontalAlignment = HorizontalAlignment.Stretch }, "history");
    private readonly ComboBox _events = Id(new ComboBox { Header = "已提交事件", HorizontalAlignment = HorizontalAlignment.Stretch }, "events");
    private readonly TextBlock _state = Id(Text("尚未选择事件"), "state");
    private readonly TextBlock _status = Id(Text("尚无受控执行"), "status");
    private readonly TextBlock _details = Text("");
    private readonly TextBlock _message = Text("");
    private readonly TextBlock _frameStatus = Text("尚未选择实际窗口帧");
    private readonly Image _frame = Id(new Image { Width = 640, Height = 360, HorizontalAlignment = HorizontalAlignment.Left }, "frame");
    private readonly NativeEvidenceCache _cache = new();
    private readonly NativeInvocationFeedback _feedback = new();
    private NativeInvocationRequest? _messageRequest;
    private bool _rendering;
    private volatile bool _active;
    private long _generation;
    private string? _runId;
    private OwnedRunSnapshot? _displayed;
    private OwnedPilotController Pilot => _plugin.Pilot;

    internal TestingOwnedPilotPanel(TestingPlugin plugin)
    {
        _plugin = plugin; Spacing = 10;
        Children.Add(Text("受控 Windows 试验 · 仅操作自带的合成任务窗口", 22));
        Children.Add(Text("仅运行下列固定场景。这里展示自带合成任务窗口的实际观察与窗口帧；上方虚拟执行历史和 SIMULATED 帧另行保存。"));
        foreach (var (value, label) in new[]
        {
            (OwnedScenario.Positive, "正常保存"), (OwnedScenario.EmptyTitle, "空标题校验"), (OwnedScenario.AssertionFailure, "故意失败的断言")
        }) _scenario.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _scenario.SelectedIndex = 0;
        Children.Add(_scenario);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_run); actions.Children.Add(_cancel); Children.Add(actions); Children.Add(_retry);
        Children.Add(_status); Children.Add(_message); Children.Add(_history);
        Children.Add(new Expander { Header = "执行身份与校验信息", Content = _details, HorizontalAlignment = HorizontalAlignment.Stretch });
        Children.Add(_events); Children.Add(_state);
        Children.Add(Text("自带合成目标的实际窗口帧", 18)); Children.Add(_frame); Children.Add(_frameStatus);
        _run.Click += (_, _) => Perform(() => _plugin.RunOwnedAsync(() =>
            _scenario.SelectedItem is ComboBoxItem { Tag: OwnedScenario scenario } ? scenario : throw new OwnedLabException("InvalidPlan")));
        _retry.Click += (_, _) => Perform(() => _plugin.RetryOwnedAsync(() =>
            _history.SelectedItem is ComboBoxItem { Tag: string id } ? id : throw new OwnedLabException("InvalidPlan")));
        _cancel.Click += (_, _) => Perform(_plugin.Owned.CancelAsync);
        _history.SelectionChanged += (_, _) =>
        {
            if (_rendering || !_active || _history.SelectedItem is not ComboBoxItem { Tag: string id } || id == Pilot.Snapshot.SelectedRun?.Execution.RunId) return;
            // Capture the exact visible history identity before the first await.
            Perform(async () => { await Pilot.SelectAsync(id); });
        };
        _events.SelectionChanged += (_, _) =>
        {
            if (_rendering || !_active) return;
            Perform(() => { SelectEvidence(SelectedEvent()); return Task.CompletedTask; });
        };
    }

    internal void Activate() { Interlocked.Increment(ref _generation); _active = true; }
    internal void Detach() { _active = false; Interlocked.Increment(ref _generation); _feedback.Retire(); }
    internal void ClearFrame() { _cache.Clear(); _frame.Source = null; }

    private void Perform(Func<Task> action)
    {
        if (!_active || _plugin.PageProducersClosed) return;
        var request = _feedback.Begin(Interlocked.Read(ref _generation));
        bool IsCurrent() => _active && !_plugin.PageProducersClosed && _feedback.IsCurrent(request, Interlocked.Read(ref _generation));
        // Owned Run/Retry never set the whole page's author busy flag: Cancel/history stay usable.
        var work = _plugin.Owner.HandleUserAsync(action,
            () => { if (IsCurrent()) { _messageRequest = null; _message.Text = ""; Render(_plugin.Lifecycle.ReadAdmission()); } },
            () => { if (_active && !_plugin.PageProducersClosed && request.Generation == Interlocked.Read(ref _generation)) Render(_plugin.Lifecycle.ReadAdmission()); },
            error =>
            {
                if (IsCurrent())
                {
                    _messageRequest = request;
                    _message.Text = "操作未完成：" + SafeCode(error) + "。执行状态以已提交记录为准。";
                }
            });
        if (work.IsFaulted && IsCurrent())
        {
            _messageRequest = request;
            _message.Text = "操作未接收：" + SafeCode(work.Exception!.InnerException!) + "。";
        }
    }

    internal void Render(NativeTestingAdmission admission, bool retryFrame = false)
    {
        if (!_active || _plugin.PageProducersClosed) return;
        var snapshot = Pilot.Snapshot;
        var current = snapshot.SelectedRun;
        _rendering = true;
        try
        {
            var recovery = snapshot.ErrorCode == "RecoveryNeeded" || current?.RecoveryNeeded == true;
            // A current Cancel/Select failure can arrive while execution is still busy. Latest model renders must retain it.
            if (_messageRequest is not { } messageRequest || !_feedback.IsCurrent(messageRequest, Interlocked.Read(ref _generation))) _message.Text = "";
            var canStart = admission.ActivityStarts && _plugin.CanStart(_plugin.Controller.Snapshot, admission)
                && !snapshot.AdmissionClosed && !snapshot.IsBusy && !recovery;
            _run.IsEnabled = canStart;
            _scenario.IsEnabled = admission.UserActions;
            _cancel.IsEnabled = admission.UserActions && _plugin.Owned.IsBusy;
            _history.IsEnabled = _events.IsEnabled = admission.UserActions;
            _retry.IsEnabled = canStart && current?.State is OwnedRunState.Passed or OwnedRunState.Failed or OwnedRunState.Cancelled or OwnedRunState.Interrupted;
            _status.Text = recovery ? RecoveryCopy : _plugin.Owned.IsPendingHandoff ? "正在等待上一轮虚拟执行释放资源；尚未开始本轮受控执行。"
                : snapshot.IsBusy ? "受控执行正在运行或等待实际资源清理；结束以已提交记录为准。"
                : snapshot.ErrorCode is not null ? "受控执行状态：" + SafeCode(new OwnedLabException(snapshot.ErrorCode))
                : current is null ? "请选择固定场景，或查看已保存的受控执行。" : "受控执行记录：" + current.State;
            _history.Items.Clear();
            foreach (var run in snapshot.Runs)
            {
                var item = new ComboBoxItem { Content = $"{run.State} · {run.Scenario} · {run.Execution.RunId}", Tag = run.Execution.RunId };
                _history.Items.Add(item); if (run.Execution.RunId == current?.Execution.RunId) _history.SelectedItem = item;
            }
            _details.Text = current is null ? "尚无选中的执行记录" : $"执行 {current.Execution.RunId}\n会话 {current.Execution.SessionId} · 场景 {current.Input.Scenario} · 父执行 {current.Execution.ParentRunId ?? "无"}\n输入 SHA256 {current.Input.Sha256}\n执行 SHA256 {current.ExecutionSha}\n已确认序列 {current.Sequence} · head {current.HeadSha}";
            var old = SelectedEvent();
            int? sequence = old is not null && old.RunId == current?.Execution.RunId ? old.Sequence : null;
            if (_runId != current?.Execution.RunId) { _runId = current?.Execution.RunId; sequence = null; ClearFrame(); }
            _displayed = current; _events.Items.Clear();
            if (current is not null)
            {
                foreach (var e in current.Events)
                {
                    var item = new ComboBoxItem { Content = $"{e.Sequence}: {e.Kind} · {e.Outcome} · {e.ErrorCode}", Tag = new NativeOwnedEventId(current.Execution.RunId, e.Sequence) };
                    _events.Items.Add(item); if (e.Sequence == sequence) _events.SelectedItem = item;
                }
                if (_events.SelectedItem is null)
                {
                    var last = current.Events.LastOrDefault(e => e.Kind == OwnedEventKind.StepResult) ?? current.Events.LastOrDefault();
                    _events.SelectedItem = _events.Items.OfType<ComboBoxItem>().LastOrDefault(i => i.Tag is NativeOwnedEventId key && key.Sequence == last?.Sequence);
                }
            }
        }
        finally { _rendering = false; }
        SelectEvidence(SelectedEvent(), retryFrame);
    }

    private NativeOwnedEventId? SelectedEvent() => (_events.SelectedItem as ComboBoxItem)?.Tag as NativeOwnedEventId;
    private void SelectEvidence(NativeOwnedEventId? selected, bool retry = false)
    {
        var selection = NativeOwnedEvidence.Resolve(_displayed, _runId, selected);
        if (selection is null) { ClearFrame(); _state.Text = "尚未选择当前执行的事件"; _frameStatus.Text = "尚未选择实际窗口帧"; return; }
        var e = selection.Event;
        _state.Text = e.State is { } state ? $"标题：{state.Title}\n提示：{state.Toast}\n校验：{state.Validation}\n已创建数量：{state.CreatedCount}"
            : $"{e.Kind} · {e.ErrorCode ?? "无观察状态"}";
        var request = _cache.Begin(selection.Key, _generation, retry);
        if (request is null) return;
        _frame.Source = null;
        if (e.Kind != OwnedEventKind.StepResult || e.FrameSha is null) { _frameStatus.Text = "此事件没有窗口帧"; return; }
        _frameStatus.Text = "正在读取该已提交事件的实际窗口帧";
        var generation = _generation;
        _ = _plugin.Owner.InternalAsync(async () =>
        {
            try
            {
                var bytes = await Pilot.ReadFrameAsync(selection.RunId, e.Sequence).ConfigureAwait(false);
                await DispatchAsync(async () =>
                {
                    if (!_active || generation != _generation || !_cache.IsCurrent(request)) return;
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    { writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
                    stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                    if (!_active || generation != _generation || !_cache.IsCurrent(request)) return;
                    _frame.Source = bitmap;
                    _frameStatus.Text = $"自带合成目标的实际窗口帧 · {bitmap.PixelWidth}×{bitmap.PixelHeight} · {bytes.Length} 字节 · SHA256 {e.FrameSha}";
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                try
                {
                    await DispatchAsync(() =>
                    {
                        if (_active && generation == _generation && _cache.Failed(request))
                        {
                            // A verified frame failure can freeze the journal. Refresh its actual recovery projection without retrying the failed key.
                            Render(_plugin.Lifecycle.ReadAdmission());
                            if (_active && generation == _generation && _cache.IsCurrent(request))
                                _frameStatus.Text = "窗口帧读取失败，可刷新重试：" + SafeCode(error);
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (Exception) { /* Original read/dispatcher work is joined; a retired view has no error postfix. */ }
            }
        });
    }

    private Task DispatchAsync(Func<Task> action)
    {
        if (DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new InvalidOperationException("Native dispatcher unavailable."));
        return completion.Task;
    }
    private static string SafeCode(Exception error)
    {
        var code = error switch { OwnedLabException owned => owned.ErrorCode, NativeActivityAdmissionException refused => refused.ErrorCode, PluginTestingException testing => testing.ErrorCode, OperationCanceledException => "Cancelled", _ => "TestingFailed" };
        return code is "InvalidPlan" or "UnavailableHelper" or "ProtocolViolation" or "TargetUnavailable" or "DriverFailure" or "CaptureFailure" or "AssertionFailed" or "Cancelled" or "Expired" or "StoreIoFailure" or "RecoveryNeeded" or "AdmissionClosed" or "StoreLimitExceeded" or "StoreBusy" or "TestingContextClosed" ? code : "TestingFailed";
    }
    private static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static T Id<T>(T element, string suffix) where T : DependencyObject
    { AutomationProperties.SetAutomationId(element, "owned-pilot-" + suffix); return element; }
}
