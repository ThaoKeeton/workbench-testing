using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.OwnedLab;

namespace Workbench.Testing;

/// <summary>Code controls in the host's current window. No plugin XAML, standalone window or WebView.</summary>
internal sealed partial class TestingPage : UserControl
{
    private readonly TestingPlugin _plugin;
    private TestingController Controller => _plugin.Controller;
    private readonly StackPanel _panel = new() { Spacing = 12, Padding = new Thickness(24) };
    private readonly TextBlock _status = Text("");
    private readonly TextBlock _message = Text("");
    private readonly ListView _sources = Named(new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 140 }, "测试来源列表");
    private readonly ComboBox _sets = Named(new ComboBox { Header = "用例集", HorizontalAlignment = HorizontalAlignment.Stretch }, "测试用例集");
    private readonly ListView _cases = Named(new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 220 }, "测试用例列表");
    private readonly TextBox _setName = Editor("用例集名称", "测试用例集名称");
    private readonly TextBox _title = Editor("用例标题", "测试用例标题");
    private readonly TextBox _prerequisites = Editor("前置条件", "测试前置条件");
    private readonly ComboBox _steps = Named(new ComboBox { Header = "步骤", HorizontalAlignment = HorizontalAlignment.Stretch }, "测试步骤选择");
    private readonly TextBox _instruction = Editor("步骤说明（修改后该步骤转为手动）", "测试步骤说明");
    private readonly TextBox _expected = Editor("预期结果（修改后该步骤转为手动）", "测试预期结果");
    private readonly TextBlock _mapping = Named(Text(""), "测试映射与追溯");
    private readonly ComboBox _runs = Named(new ComboBox { Header = "已提交执行", HorizontalAlignment = HorizontalAlignment.Stretch }, "测试执行列表");
    private readonly ComboBox _events = Named(new ComboBox { Header = "已提交事件", HorizontalAlignment = HorizontalAlignment.Stretch }, "测试执行事件");
    private readonly TextBlock _runDetails = Named(Text(""), "测试执行详情");
    private readonly TextBlock _observed = Named(Text(""), "测试实际观察文本");
    private readonly Image _frame = Named(new Image { Width = 640, Height = 360, HorizontalAlignment = HorizontalAlignment.Left }, "SIMULATED 测试帧");
    private readonly TextBlock _frameStatus = Named(Text("SIMULATED — 尚未选择证据"), "测试帧状态");
    private readonly List<Button> _authorActions = [];
    private readonly List<Button> _activityActions = [];
    private readonly TestingSourcePanel _sourcePanel;
    private readonly TestingOwnedPilotPanel _ownedPanel;
    private Button _retryDraft = null!, _preserveDraft = null!, _run = null!, _cancel = null!, _retryRun = null!;
    private bool _rendering, _busy;
    private volatile bool _active;
    private readonly object _subscriptionsSync = new();
    private long _generation;
    private string? _caseId, _stepId, _setId, _runId;
    private readonly NativeEvidenceCache _evidenceCache = new();
    private TestRunSnapshot? _displayedRun;

    internal TestingPage(TestingPlugin plugin)
    {
        _plugin = plugin;
        Content = new ScrollViewer { Content = _panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _panel.Children.Add(Text("测试设计", 28));
        _panel.Children.Add(Text("本地 JSON 与明确发起的远程 Figma / OpenAI 创作。下方虚拟目标 virtual.task-form.v1 的执行与帧均为 SIMULATED。手动步骤不自动通过；AI 生成不会启动自动化。"));
        var imports = Row();
        imports.Children.Add(Action("导入需求 JSON", "导入本地需求 JSON", () => PickImportAsync(TestSourceKind.Requirements), activity: true));
        imports.Children.Add(Action("导入 Figma 快照 JSON", "导入本地 Figma JSON", () => PickImportAsync(TestSourceKind.Figma), activity: true));
        imports.Children.Add(Action("打包需求示例（含故意失败）", "导入打包需求示例", () => _plugin.ImportBundledAsync(TestSourceKind.Requirements, _plugin.Lifetime), activity: true));
        imports.Children.Add(Action("打包 Figma 示例", "导入打包 Figma 示例", () => _plugin.ImportBundledAsync(TestSourceKind.Figma, _plugin.Lifetime), activity: true));
        _panel.Children.Add(imports); _panel.Children.Add(_sources);
        _panel.Children.Add(Action("从选中来源生成（未选则使用全部）", "生成测试用例", () => _plugin.GenerateAsync(() =>
        {
            var ids = _sources.SelectedItems.OfType<ListViewItem>().Select(i => (string)i.Tag).ToArray();
            if (ids.Length == 0) ids = Controller.Snapshot.Sources.Select(s => s.Id).ToArray();
            return ids;
        }), activity: true));
        _sourcePanel = new(plugin, Capture, action => ObserveUser(action, announce: false),
            action => ObserveUser(action, author: false, announce: false),
            () => _sources.SelectedItems.OfType<ListViewItem>().Select(i => (string)i.Tag).ToArray());
        _panel.Children.Add(_sourcePanel);
        _panel.Children.Add(_sets); _panel.Children.Add(_setName); _panel.Children.Add(Named(_status, "测试自动保存状态"));
        var recovery = Row();
        _retryDraft = Action("重试草稿写入", "重试测试草稿", async () => { await Controller.RetryDraftFlushAsync(); });
        _preserveDraft = Action("保留草稿为新集并重新加载", "保留测试草稿并重新加载", async () => { await Controller.PreserveDraftAsNewAndReloadAsync(); });
        recovery.Children.Add(_retryDraft); recovery.Children.Add(_preserveDraft); _panel.Children.Add(recovery);
        _panel.Children.Add(_cases); _panel.Children.Add(_title); _panel.Children.Add(_prerequisites);
        _panel.Children.Add(_steps); _panel.Children.Add(_instruction); _panel.Children.Add(_expected); _panel.Children.Add(_mapping);
        var execution = Row();
        execution.Children.Add(Action("导出 CSV…", "导出测试 CSV", PickExportAsync));
        _run = Action("运行选中用例（虚拟）", "运行选中测试用例", () => _plugin.RunAsync(() =>
        {
            var id = _caseId ?? throw new PluginTestingException("InvalidCaseSet");
            return [id];
        }), activity: true);
        _cancel = Action("取消并等待结束", "取消测试执行", async () => { await Controller.CancelAsync(); }, author: false);
        _retryRun = Action("从第一步重试（新执行）", "重试测试执行", async () =>
        { await _plugin.RetryAsync(() => Controller.Snapshot.SelectedRun?.Plan.Id ?? throw new PluginTestingException("InvalidRun")); }, activity: true);
        execution.Children.Add(_run); execution.Children.Add(_cancel); execution.Children.Add(_retryRun); _panel.Children.Add(execution);
        _panel.Children.Add(_runs); _panel.Children.Add(_runDetails); _panel.Children.Add(_events); _panel.Children.Add(_observed);
        _panel.Children.Add(Text("SIMULATED — 内存虚拟表单的被动 BMP，非真实桌面截图", 18));
        _panel.Children.Add(_frame); _panel.Children.Add(_frameStatus); _panel.Children.Add(_message);
        _ownedPanel = new(plugin);
        _panel.Children.Add(_ownedPanel);
        _setName.TextChanging += (_, _) => Capture(() => Controller.CaptureSetName(_setName.Text));
        _title.TextChanging += (_, _) => CaptureMetadata(); _prerequisites.TextChanging += (_, _) => CaptureMetadata();
        _instruction.TextChanging += (_, _) => CaptureStep(); _expected.TextChanging += (_, _) => CaptureStep();
        _sets.SelectionChanged += (_, _) =>
        {
            if (_rendering || !_active || _sets.SelectedItem is not ComboBoxItem { Tag: string id } || id == Controller.Snapshot.Draft?.Id) return;
            ObserveUser(async () => { await Controller.SelectCaseSetAsync(id); });
        };
        _cases.SelectionChanged += (_, _) =>
        {
            if (_rendering || !_active) return;
            Capture(() => { _caseId = (_cases.SelectedItem as ListViewItem)?.Tag as string; _stepId = null; Render(); });
        };
        _steps.SelectionChanged += (_, _) =>
        {
            if (_rendering || !_active) return;
            Capture(() => { _stepId = (_steps.SelectedItem as ComboBoxItem)?.Tag as string; Render(); });
        };
        _runs.SelectionChanged += (_, _) =>
        {
            if (_rendering || !_active || _runs.SelectedItem is not ComboBoxItem { Tag: string id } || id == Controller.Snapshot.SelectedRun?.Plan.Id) return;
            ObserveUser(async () => { await Controller.SelectRunAsync(id); });
        };
        _events.SelectionChanged += (_, _) => Capture(() => SelectEvidence(_displayedRun, _runId, SelectedEventId()));
        _sources.SelectionChanged += (_, _) => Capture(() =>
        {
            _sourcePanel.CaptureControls();
            _sourcePanel.Render(_sources.IsEnabled, _plugin.Lifecycle.ReadAdmission());
        });
        Loaded += (_, _) =>
        {
            if (_active) return;
            if (_plugin.Attach(this) && AttachControllers()) _ = NotifyAsync();
        };
        Unloaded += (_, _) => Retire();
    }
    // No control delegate executes under the plugin's page lock. A cutoff snapshot includes us before this guarded subscription prefix.
    private bool AttachControllers()
    {
        lock (_subscriptionsSync)
        {
            if (_plugin.PageProducersClosed) { _plugin.Detach(this); return false; }
            _active = true; Interlocked.Increment(ref _generation); _ownedPanel.Activate();
            Controller.Changed += Changed; _plugin.Pilot.Changed += OwnedChanged;
            return true;
        }
    }
    private void CaptureMetadata() => Capture(() =>
    { if (_caseId is not null) Controller.CaptureCaseMetadata(_caseId, _title.Text, _prerequisites.Text); });
    private void CaptureStep() => Capture(() =>
    { if (_caseId is not null && _stepId is not null) Controller.CaptureStepText(_caseId, _stepId, _instruction.Text, _expected.Text); });
    private void Capture(Action action)
    {
        if (_rendering || !_active) return;
        // TextChanging is synchronous: read complete real control values before the first await.
        var work = _plugin.Owner.UserAsync(() =>
        {
            try { action(); }
            catch (Exception error) { ShowError(error); }
            return Task.CompletedTask;
        });
        // Only already-settled admission rejection escapes the delegate. No unowned UI postfix.
        if (work.IsFaulted) ShowError(work.Exception!.InnerException!);
    }
    private Button Action(string label, string name, Func<Task> action, bool author = true, bool activity = false)
    {
        var button = Named(new Button { Content = label }, name);
        if (author) _authorActions.Add(button);
        if (activity) _activityActions.Add(button);
        button.Click += (_, _) => ObserveUser(action, author);
        return button;
    }
    private void ObserveUser(Func<Task> action, bool author = true, bool announce = true)
    {
        if (!_active || (author && (_busy || _plugin.SourceBusy))) return;
        var generation = _generation;
        var work = _plugin.Owner.HandleUserAsync(async () =>
        {
            await action();
            if (announce && _active && generation == _generation) _message.Text = "本地操作已完成。执行结果以已提交日志为准。";
        }, () => { if (author) _busy = true; if (!announce) _message.Text = ""; Render(); },
           () => { if (author) _busy = false; if (_active && generation == _generation) Render(); }, ShowError);
        if (work.IsFaulted) ShowError(work.Exception!.InnerException!);
    }
    private void ShowError(Exception error)
    {
        if (!_active) return;
        var code = error switch { NativeActivityAdmissionException refused => SafeCode(refused.ErrorCode), PluginTestingException known => SafeCode(known.ErrorCode), _ => "LocalFileOrTestingFailure" };
        _message.Text = "操作未完成：" + code + "。原始文件和未保存草稿已保留；请修正后重试。";
    }
    private static string SafeCode(string code) => code is "RevisionConflict" or "StoreIoFailure" or "StoreBusy" or "InvalidCaseSet" or "InvalidTestSource" or "ManualStepUnsupported" or "StoreLimitExceeded" or "TestingContextClosed" or "InvalidRun" ? code : "TestingFailed";
    private static TextBox Editor(string header, string name) => Named(new TextBox { Header = header, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50 }, name);
    private static TextBlock Text(string text, double size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private static T Named<T>(T element, string name) where T : DependencyObject { AutomationProperties.SetName(element, name); return element; }
}
