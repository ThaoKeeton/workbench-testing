using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Workbench.Sdk;
using Workbench.WinUI.Sdk;
using Workbench.Samples;

namespace Workbench.TestCases.Sample;

public sealed class TestCasesPlugin : IWorkbenchPlugin
{
    private SampleTextBuffer _suiteName = null!;
    private SampleTextBuffer _cases = null!;
    private readonly List<SampleCase> _rows =
    [
        new("EX-001", "启用本地插件", "原生页面出现在主窗口"),
        new("EX-002", "编辑摘要示例", "草稿保存到本地数据目录"),
        new("EX-003", "停用示例插件", "页面与命令入口撤销")
    ];

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _suiteName = new(await context.Settings.GetAsync("suiteName", "原生工作台演示", cancellationToken),
            value => context.Settings.SetAsync("suiteName", value));
        var draftPath = Path.Combine(context.DataDirectory, "sample-test-cases.json");
        if (File.Exists(draftPath))
        {
            var saved = JsonSerializer.Deserialize<List<SampleCase>>(await File.ReadAllTextAsync(draftPath, cancellationToken))
                ?? throw new InvalidDataException("Sample rows are invalid.");
            if (saved.Count != 3 || saved.Any(row => row is null || row.Id is null || row.Action is null || row.Expected is null))
                throw new InvalidDataException("Sample rows are invalid.");
            _rows.Clear();
            _rows.AddRange(saved);
        }
        _cases = new(JsonSerializer.Serialize(_rows), value => SampleTextBuffer.WriteFileAsync(draftPath, value));
        if (context is IPluginProjectContext project) project.RegisterBeforeProjectLeave(_ => SaveAllAsync());
        context.RegisterSetting(new("suiteName", "示例用例集名称", "原生工作台演示"));
        context.RegisterView(new WinUiView("cases", "测试用例 · 示例", () => CreatePage(context)));
        context.RegisterView(new WinUiView("preferences", "测试用例", () => CreateSettings(context), WinUiViewKind.Settings));
        context.RegisterCommand(new("export-csv", "导出本地示例 CSV", async token =>
        {
            context.Lifetime.ThrowIfCancellationRequested();
            var rows = JsonSerializer.Deserialize<List<SampleCase>>(_cases.Text)!;
            var suite = _suiteName.Text;
            var lines = new[] { "suite,id,action,expected" }.Concat(rows.Select(row => string.Join(",", new[] { suite, row.Id, row.Action, row.Expected }.Select(Csv))));
            var path = Path.Combine(context.DataDirectory, "sample-test-cases.csv");
            await File.WriteAllTextAsync(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(true), token);
            return new(true, $"演示 CSV 已导出到本地：{path}。未执行真实测试或电脑自动化。");
        }, "导出当前本地示例行，不读取业务系统、不执行自动化。"));
    }

    private FrameworkElement CreatePage(IPluginContext context)
    {
        context.Lifetime.ThrowIfCancellationRequested();
        var panel = Panel("测试用例 · 本地示例", "编辑以下预置示例行，再到“命令”运行“导出本地示例 CSV”。离开项目、停用或退出前保存编辑；CSV 文件保留在本地。");
        panel.Children.Add(Text("这些用例是示例数据，不表示实际测试已执行或通过。"));
        var list = Named(new ListView { SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch }, "可编辑的示例测试用例列表");
        foreach (var row in _rows)
        {
            var cell = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var id = Text(row.Id); id.VerticalAlignment = VerticalAlignment.Center;
            var action = Named(new TextBox { Header = "步骤", Text = row.Action, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true }, $"{row.Id} 示例步骤");
            var expected = Named(new TextBox { Header = "预期结果", Text = row.Expected, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true }, $"{row.Id} 示例预期结果");
            action.TextChanging += (_, _) =>
            {
                if (context.Lifetime.IsCancellationRequested) return;
                row.Action = action.Text;
                _cases.Set(JsonSerializer.Serialize(_rows));
            };
            expected.TextChanging += (_, _) =>
            {
                if (context.Lifetime.IsCancellationRequested) return;
                row.Expected = expected.Text;
                _cases.Set(JsonSerializer.Serialize(_rows));
            };
            Grid.SetColumn(action, 1); Grid.SetColumn(expected, 2);
            cell.Children.Add(id); cell.Children.Add(action); cell.Children.Add(expected);
            list.Items.Add(cell);
        }
        panel.Children.Add(list);
        panel.Children.Add(Text($"本地导出位置：{Path.Combine(context.DataDirectory, "sample-test-cases.csv")}"));
        return Scroll(panel);
    }

    private FrameworkElement CreateSettings(IPluginContext context)
    {
        context.Lifetime.ThrowIfCancellationRequested();
        var panel = Panel("测试用例 · 示例设置", "此偏好随插件 JSON 设置保存；不连接任何测试管理系统。");
        var name = Named(new TextBox { Header = "示例用例集名称", Text = _suiteName.Text }, "示例用例集名称");
        var status = Text("编辑后自动保存；名称写入导出文件的 suite 列。");
        name.TextChanging += (_, _) =>
        {
            if (context.Lifetime.IsCancellationRequested) return;
            _suiteName.Set(name.Text);
            _suiteName.Autosave();
        };
        panel.Children.Add(name); panel.Children.Add(status);
        return Scroll(panel);
    }

    private async Task SaveAllAsync()
    {
        var saves = new List<Task>();
        if (_suiteName is not null) saves.Add(_suiteName.SaveAsync());
        if (_cases is not null) saves.Add(_cases.SaveAsync());
        await Task.WhenAll(saves);
    }
    public Task StopAsync(CancellationToken cancellationToken) => SaveAllAsync();
    private static string Csv(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
    private sealed class SampleCase
    {
        [System.Text.Json.Serialization.JsonConstructor]
        public SampleCase(string id, string action, string expected) { Id = id; Action = action; Expected = expected; }
        public string Id { get; }
        public string Action { get; set; }
        public string Expected { get; set; }
    }
    private static StackPanel Panel(string title, string description)
    {
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(28) };
        panel.Children.Add(Text(title, 28)); panel.Children.Add(Text(description)); return panel;
    }
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private static T Named<T>(T element, string name) where T : DependencyObject { AutomationProperties.SetName(element, name); return element; }
}
