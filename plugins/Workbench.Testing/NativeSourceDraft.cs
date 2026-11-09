using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Sources.Figma;

namespace Workbench.Testing;

internal sealed record NativeFigmaNode(string NodeId, string CriteriaText);
internal sealed record NativeSourceForm(string FileKey, string Version, IReadOnlyList<NativeFigmaNode> Nodes, IReadOnlyList<string> SelectedSourceIds);
internal sealed record NativeFigmaInput(FigmaReadRequest Request, IReadOnlyDictionary<string, IReadOnlyList<string>> Criteria);

internal sealed class NativeSourceDraft
{
    private readonly object _sync = new();
    private NativeSourceForm _form = new("", "", Array.AsReadOnly<NativeFigmaNode>([new("", "")]), Array.AsReadOnly<string>([]));
    private string _status = "表单仅保留在当前项目会话；已成功导入的记录持久保存。";
    private bool _acknowledged;
    internal string Status { get { lock (_sync) return _status; } }
    internal NativeSourceForm Reopen() { lock (_sync) return _form; }
    internal void Capture(string fileKey, string version, IReadOnlyList<NativeFigmaNode> nodes, IReadOnlyList<string> selected)
    {
        // These strings came from real controls in the synchronous owned UI prefix. Never keep caller lists.
        var captured = new NativeSourceForm(fileKey, version, Array.AsReadOnly(nodes.ToArray()), Array.AsReadOnly(selected.ToArray()));
        lock (_sync) _form = captured;
    }
    internal NativeFigmaInput CaptureFigma()
    {
        var form = Reopen();
        if (form.Nodes.Count is < 1 or > 16) throw new PluginTestingException("InvalidTestSource");
        var criteria = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var node in form.Nodes)
        {
            // Empty lines separate entries; literal spaces in a criterion are preserved and validated by the controller.
            var lines = node.CriteriaText.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !criteria.TryAdd(node.NodeId, Array.AsReadOnly(lines)))
                throw new PluginTestingException("InvalidTestSource");
        }
        return new(new(form.FileKey, Array.AsReadOnly(form.Nodes.Select(n => n.NodeId).ToArray()), form.Version.Length == 0 ? null : form.Version),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>(criteria));
    }
    internal IReadOnlyList<string> SelectedForAi()
    {
        var selected = Reopen().SelectedSourceIds;
        if (selected.Count is < 1 or > 8) throw new PluginTestingException("InvalidTestSource");
        return selected;
    }
    internal void AddNode()
    {
        lock (_sync)
        {
            if (_form.Nodes.Count >= 16) throw new PluginTestingException("InvalidTestSource");
            _form = _form with { Nodes = Array.AsReadOnly(_form.Nodes.Append(new("", "")).ToArray()) };
        }
    }
    internal void RemoveNode(int index)
    {
        lock (_sync)
        {
            if (_form.Nodes.Count <= 1 || index < 0 || index >= _form.Nodes.Count) throw new PluginTestingException("InvalidTestSource");
            _form = _form with { Nodes = Array.AsReadOnly(_form.Nodes.Where((_, row) => row != index).ToArray()) };
        }
    }
    internal void Begin(string label) { lock (_sync) { _acknowledged = false; _status = label + "；正在等待实际工作结束。"; } }
    internal void CompleteFigma(TestSourceSnapshot source)
    {
        lock (_sync) { _acknowledged = true; _status = $"Figma 来源已导入：{source.Id}@{source.Revision} · {source.DocumentId}。生成的步骤仍需手动验证。"; }
    }
    internal void CompleteAi(AiGenerationPublication result)
    {
        lock (_sync)
        {
            _acknowledged = true;
            var receipt = $"回执 {result.Receipt.Id}@{result.Receipt.Revision}";
            _status = result is { State: AiGenerationPublicationState.Created, CaseSet: not null }
                ? $"已创建用例集 {result.CaseSet.Id}；{receipt}。AI 生成，必须人工审核；所有步骤为手动。"
                : $"仅回执已确认保存：{receipt}；用例集未确认创建。{result.State} · {SafeCode(result.ErrorCode)}。不自动重试或删除；可显式刷新检查实际记录。";
        }
    }
    internal void Fail(Exception error)
    {
        var code = error switch
        {
            OperationCanceledException => "Cancelled",
            PluginTestingException known => SafeCode(known.ErrorCode),
            PluginSecretStoreException { ErrorCode: "SecretStorageUnsupported" } => "SecretStorageUnsupported",
            _ => "TestingFailed"
        };
        lock (_sync) _status = (_acknowledged ? _status + "\n" : "") + $"操作未完成：{code}。会话表单和已接受草稿保留；请检查输入与宿主凭据后重试。";
    }
    private static string SafeCode(string? code) => code switch
    {
        null => "Cancelled",
        "RevisionConflict" or "StoreIoFailure" or "StoreBusy" or "InvalidCaseSet" or "InvalidTestSource" or "InvalidId"
            or "ManualStepUnsupported" or "StoreLimitExceeded" or "TestingContextClosed" or "InvalidRun" => code,
        _ => "TestingFailed"
    };
}

internal static class NativeSourceCredentials
{
    internal static Func<CancellationToken, Task<string?>> ForAction(IPluginSecretStore store, string id)
    {
        if (!store.IsSupported) throw new PluginSecretStoreException("SecretStorageUnsupported");
        return token => store.ReadAsync(id, token);
    }
}
