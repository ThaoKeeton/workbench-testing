using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Workbench.Testing;

/// <summary>Controls only. All values cross the owned synchronous prefix before any backend await.</summary>
internal sealed class TestingSourcePanel : StackPanel
{
    private readonly TestingPlugin _plugin;
    private readonly Action<Action> _capture;
    private readonly Func<string[]> _selected;
    private readonly TextBox _file = Input("Figma 文件 key（非 URL）", "Figma 文件 key", multiline: false);
    private readonly TextBox _version = Input("Figma 版本（可留空，读取当前版本）", "Figma 版本", multiline: false);
    private readonly StackPanel _nodes = new() { Spacing = 8 };
    private readonly List<(TextBox Id, TextBox Criteria, Button Remove)> _rows = [];
    private readonly Button _add = Named(new Button { Content = "添加节点（最多 16）" }, "添加 Figma 节点");
    private readonly Button _figma = Named(new Button { Content = "读取 Figma 并导入" }, "读取 Figma 并导入");
    private readonly Button _ai = Named(new Button { Content = "发送所选来源到 OpenAI 并生成" }, "发送所选来源到 OpenAI 并生成");
    private readonly Button _cancel = Named(new Button { Content = "取消远程操作" }, "取消远程操作");
    private readonly TextBlock _status = Named(Text(""), "远程来源操作状态");
    private bool _hydrating;

    internal TestingSourcePanel(TestingPlugin plugin, Action<Action> capture, Action<Func<Task>> remoteAction,
        Action<Func<Task>> cancelAction, Func<string[]> selected)
    {
        _plugin = plugin; _capture = capture; _selected = selected; Spacing = 10;
        Children.Add(Text("Figma 只读导入与 AI 用例创作", 20));
        Children.Add(Text("表单仅保留在当前项目会话，页面关闭后重开可继续；已成功导入的来源、回执与用例持久保存。凭据请在宿主插件凭据页管理。不会因输入、切换页面或刷新自动联网。"));
        Children.Add(_file); Children.Add(_version); Children.Add(_nodes); Children.Add(_add);
        Children.Add(Text("每个节点输入 ID（例如 1:2）和每行一个手工验收条件，空行忽略。条件文本保留原样；每节点 1–16 条、合计最多 200 条。Figma 读取需要 file_content:read 和目标文件访问权限。"));
        Children.Add(_figma);
        Children.Add(Text("OpenAI 模型：gpt-5.4-mini-2026-03-17。仅发送上方实际选中的 1–8 个来源，最多 32 个验收条件；包含来源身份/版本及标题、描述、验收条件。发送到 OpenAI 可能产生费用；结果必须人工审核，所有生成步骤为手动，不启动自动化。未选择来源时不能发送。"));
        Children.Add(_ai); Children.Add(_cancel); Children.Add(_status);
        LoadForm();
        _file.TextChanging += (_, _) => _capture(CaptureControls);
        _version.TextChanging += (_, _) => _capture(CaptureControls);
        _add.Click += (_, _) => _capture(() => { CaptureControls(); _plugin.SourceDraft.AddNode(); LoadForm(); });
        _figma.Click += (_, _) => remoteAction(() => _plugin.ImportRemoteFigmaAsync(() =>
        {
            CaptureControls();
            return _plugin.SourceDraft.CaptureFigma();
        }));
        _ai.Click += (_, _) => remoteAction(() => _plugin.GenerateRemoteAiAsync(() =>
        {
            CaptureControls();
            return _plugin.SourceDraft.SelectedForAi();
        }));
        _cancel.Click += (_, _) => cancelAction(_plugin.CancelSourceAsync);
    }

    internal void CaptureControls()
    {
        if (_hydrating) return;
        _plugin.SourceDraft.Capture(_file.Text, _version.Text,
            _rows.Select(row => new NativeFigmaNode(row.Id.Text, row.Criteria.Text)).ToArray(), _selected());
    }

    private void LoadForm()
    {
        _hydrating = true;
        try
        {
            var form = _plugin.SourceDraft.Reopen();
            _file.Text = form.FileKey; _version.Text = form.Version;
            _nodes.Children.Clear(); _rows.Clear();
            for (var index = 0; index < form.Nodes.Count; index++)
            {
                var rowIndex = index;
                var row = form.Nodes[index];
                var id = Input($"节点 {index + 1} ID", $"Figma 节点 {index + 1} ID", multiline: false); id.Text = row.NodeId;
                var criteria = Input($"节点 {index + 1} 手工验收条件（每行一条）", $"Figma 节点 {index + 1} 验收条件"); criteria.Text = row.CriteriaText;
                var remove = Named(new Button { Content = $"移除节点 {index + 1}" }, $"移除 Figma 节点 {index + 1}");
                _rows.Add((id, criteria, remove));
                var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(id); panel.Children.Add(criteria); panel.Children.Add(remove); _nodes.Children.Add(panel);
                id.TextChanging += (_, _) => _capture(CaptureControls);
                criteria.TextChanging += (_, _) => _capture(CaptureControls);
                remove.Click += (_, _) => _capture(() => { CaptureControls(); _plugin.SourceDraft.RemoveNode(rowIndex); LoadForm(); });
            }
            Render(editable: true, _plugin.Lifecycle.ReadAdmission());
        }
        finally { _hydrating = false; }
    }
    internal void Render(bool editable, NativeTestingAdmission admission)
    {
        _file.IsEnabled = _version.IsEnabled = editable;
        foreach (var row in _rows)
        { row.Id.IsEnabled = row.Criteria.IsEnabled = editable; row.Remove.IsEnabled = editable && _rows.Count > 1; }
        _add.IsEnabled = editable && _rows.Count < 16;
        var canStart = _plugin.CanStart(_plugin.Controller.Snapshot, admission);
        _figma.IsEnabled = editable && canStart && _plugin.SourceSecretsSupported;
        _ai.IsEnabled = editable && canStart && _plugin.SourceSecretsSupported && _plugin.SourceDraft.Reopen().SelectedSourceIds.Count is >= 1 and <= 8;
        _cancel.IsEnabled = admission.UserActions && _plugin.SourceBusy;
        _status.Text = _plugin.SourceDraft.Status + (_plugin.SourceSecretsSupported ? "" : "\nSecretStorageUnsupported：当前宿主不支持受保护凭据，远程操作不可用。");
    }
    private static TextBox Input(string header, string name, bool multiline = true) => Named(new TextBox
    { Header = header, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 60 : 32 }, name);
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static T Named<T>(T element, string name) where T : DependencyObject { AutomationProperties.SetName(element, name); return element; }
}
