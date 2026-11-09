using Microsoft.UI.Xaml;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.OwnedLab;
using Workbench.Testing.Windows;
using Workbench.WinUI.Sdk;

namespace Workbench.Testing;

public sealed class TestingPlugin : IWorkbenchPlugin
{
    private readonly TestingSourceServices _sources;
    public TestingPlugin() : this(TestingSourceServices.CreateDefault()) { }
    internal TestingPlugin(TestingSourceServices sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sources.Figma);
        ArgumentNullException.ThrowIfNull(sources.Ai);
        _sources = sources;
    }
    private readonly NativeActionOwner _owner = new();
    private readonly object _pagesSync = new();
    private readonly HashSet<TestingPage> _pages = [];
    private bool _pagesClosed;
    internal bool PageProducersClosed { get { lock (_pagesSync) return _pagesClosed; } }
    private readonly NativeRemoteOperation _remote = new();
    private readonly NativeRemoteOperation _ownedOperation = new();
    private OwnedPilotController? _pilot;
    private NativeOwnedPilot? _ownedPilot;
    private NativeTestingLifecycle? _lifecycle;
    internal OwnedPilotController Pilot => _pilot ?? throw new OwnedLabException("AdmissionClosed");
    internal NativeOwnedPilot Owned => _ownedPilot ?? throw new OwnedLabException("AdmissionClosed");
    internal NativeTestingLifecycle Lifecycle => _lifecycle ??= new(_owner, _activity, _remote, _ownedOperation, _controller, _pilot, NotifyPagesAsync, RetirePagesAsync);
    private NativeActivityCoordinator? _activity;
    private NativeActivityCoordinator Activity => _activity ?? throw new PluginTestingException("TestingContextClosed");
    internal bool ActivityBusy => _activity?.IsBusy == true;
    internal NativeSourceDraft SourceDraft { get; } = new();
    internal bool SourceBusy => _remote.IsBusy;
    internal bool SourceSecretsSupported => _secrets.IsSupported;
    private TestingController _controller = null!;
    private IPluginTestingContext _context = null!;
    private IPluginSecretStore _secrets = null!;
    private CancellationTokenRegistration _lifetimeRegistration;
    internal TestingController Controller => _controller;
    internal NativeActionOwner Owner => _owner;
    internal CancellationToken Lifetime => _context.Lifetime;
    private string? _ownedRunId;
    internal bool CanStart(TestingControllerSnapshot snapshot, NativeTestingAdmission? admission = null) => (admission ?? Lifecycle.ReadAdmission()).ActivityStarts && NativeRunAdmission.CanStart(snapshot, Volatile.Read(ref _ownedRunId));
    internal Task RunAsync(Func<IReadOnlyList<string>> capture) => Lifecycle.RetainActivityAsync(() => Activity.VirtualAsync(async () =>
    { var run = await _controller.RunAsync(capture()).ConfigureAwait(false); await RecordOwnedAsync(run).ConfigureAwait(false); return run; }, NotifyPagesAsync));
    internal Task RetryAsync(Func<string> capture, CancellationToken token = default) => Lifecycle.RetainActivityAsync(() => Activity.VirtualAsync(async () =>
    { var run = await _controller.RetryAsync(capture(), token).ConfigureAwait(false); await RecordOwnedAsync(run).ConfigureAwait(false); return run; }, NotifyPagesAsync));
    internal Task QueueMappedAsync(CancellationToken token) => Lifecycle.RetainActivityAsync(() => Activity.VirtualAsync(async () =>
    { var run = await _controller.QueueRunAsync(MappedIds(), token).ConfigureAwait(false); await RecordOwnedAsync(run).ConfigureAwait(false); return run; }, NotifyPagesAsync));
    internal Task StartQueuedAsync(CancellationToken token) => Lifecycle.RetainActivityAsync(() => Activity.VirtualAsync(() => _controller.StartQueuedRunAsync(token), NotifyPagesAsync));
    internal Task GenerateAsync(Func<IReadOnlyList<string>> capture, CancellationToken token = default)
        => Lifecycle.RetainActivityAsync(() => Activity.SourceAsync(() => capture().ToArray(), ids => _controller.GenerateAsync(ids, token), NotifyPagesAsync));
    internal Task RunOwnedAsync(Func<OwnedScenario> capture) => Lifecycle.RetainActivityAsync(() => Owned.RunAsync(capture, NotifyPagesAsync));
    internal Task RetryOwnedAsync(Func<string> capture) => Lifecycle.RetainActivityAsync(() => Owned.RetryAsync(capture, NotifyPagesAsync));
    private async Task RecordOwnedAsync(TestRunSnapshot run)
    {
        Volatile.Write(ref _ownedRunId, run.Plan.Id);
        foreach (var page in Pages()) await page.NotifyAsync().ConfigureAwait(false);
    }

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        try
        {
        _context = context as IPluginTestingContext ?? throw new PluginTestingException("TestingContextClosed");
        _secrets = _context.Secrets;
        _secrets.Register(new("figma-token", "Figma 访问令牌", "仅在原生页面明确读取时使用；需 file_content:read 和目标文件访问权限。"));
        _secrets.Register(new("openai-api-key", "OpenAI API 密钥", "仅在原生页面明确发送所选来源时使用；可能产生费用。"));
        _controller = _sources.CreateController(_context);
        _activity = new(_controller, _remote);
        var scope = new OwnedScope(_context.Project.Identity.Id, _context.PluginId, _context.DataDirectory);
        _pilot = new(scope, new Win32OwnedDriverFactory(scope, TimeProvider.System), TimeProvider.System);
        _ownedPilot = new(_pilot, _activity, _ownedOperation);
        var lifecycle = Lifecycle;
        _lifetimeRegistration = _context.Lifetime.Register(() => { ClosePages(); lifecycle.SealLifetime(); });
        await _controller.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _pilot.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _context.RegisterBeforeProjectLeave(PrepareAsync);
        context.RegisterView(new WinUiView("testing", "测试设计", CreatePage));
        Register("import-example-requirements", "导入打包的需求示例（含故意失败）", token => ImportBundledAsync(TestSourceKind.Requirements, token));
        Register("import-example-figma", "导入打包的 Figma 本地示例", token => ImportBundledAsync(TestSourceKind.Figma, token));
        Register("generate-all", "从所有本地来源生成用例", token => GenerateAsync(() => _controller.Snapshot.Sources.Select(s => s.Id).ToArray(), token));
        Register("export-local-csv", "导出当前用例 CSV 到插件本地目录", async token =>
        { await NativeTestingFiles.WriteAsync(Path.Combine(context.DataDirectory, "testing-cases.csv"), await _controller.ExportCsvAsync(token), token); });
        Register("queue-mapped", "排队当前用例集的全部映射用例", QueueMappedAsync);
        Register("start-queued", "启动已排队的虚拟执行", StartQueuedAsync);
        Register("cancel-run", "取消并等待虚拟执行实际结束", async token => { await _controller.CancelAsync(token); });
        Register("retry-selected", "从第一步重试选中的历史执行", async token =>
        { await RetryAsync(() => _controller.Snapshot.SelectedRun?.Plan.Id ?? throw new PluginTestingException("InvalidRun"), token); });
        Register("refresh", "刷新已提交的测试记录", async token =>
        {
            await _controller.RefreshAsync(token);
            foreach (var page in Pages()) await page.NotifyAsync(retryEvidence: true).ConfigureAwait(false);
        });
        }
        catch (Exception)
        {
            ClosePages();
            try { await Lifecycle.StopAsync().ConfigureAwait(false); }
            catch (Exception) { /* Preserve the actual initialization failure after all partial cleanup. */ }
            _lifetimeRegistration.Dispose();
            throw;
        }
    }
    private string[] MappedIds() => (_controller.Snapshot.Draft?.Content.Cases ?? []).Where(c => c.Steps.All(s => s.Action is not null)).Select(c => c.Id).ToArray();
    private void Register(string id, string title, Func<CancellationToken, Task> action) => _context.RegisterCommand(new(id, title,
        token => _owner.UserAsync(async () =>
        {
            try { await action(token).ConfigureAwait(false); }
            catch (NativeActivityAdmissionException error) { throw error.ToTestingException(); }
            return new PluginCommandResult(true, "本地操作已完成；执行状态以已提交日志为准。SIMULATED。");
        })));

    internal Task ImportBundledAsync(TestSourceKind kind, CancellationToken token) => Lifecycle.RetainActivityAsync(() => Activity.SourceAsync(() => kind, sourceKind =>
    {
        var name = sourceKind == TestSourceKind.Requirements ? "requirements.synthetic.json" : "figma.synthetic.json";
        var path = Path.Combine(Path.GetDirectoryName(typeof(TestingPlugin).Assembly.Location)!, "Fixtures", name);
        return ImportFileCoreAsync(sourceKind, path, token);
    }, NotifyPagesAsync));
    internal Task ImportFileAsync(TestSourceKind kind, string path, CancellationToken token)
        => Lifecycle.RetainActivityAsync(() => Activity.SourceAsync(() => (Kind: kind, Path: path), input => ImportFileCoreAsync(input.Kind, input.Path, token), NotifyPagesAsync));
    private async Task<TestSourceSnapshot> ImportFileCoreAsync(TestSourceKind kind, string path, CancellationToken token)
    {
        var bytes = await NativeTestingFiles.ReadAsync(path, token).ConfigureAwait(false);
        return await _controller.ImportAsync(kind, bytes, token).ConfigureAwait(false);
    }
    internal Task ImportRemoteFigmaAsync(Func<NativeFigmaInput> capture) => RemoteAsync("正在读取 Figma 并导入", () => NativeActivityInputs.Figma(capture), async (input, token) =>
    {
        var read = NativeSourceCredentials.ForAction(_secrets, "figma-token");
        SourceDraft.CompleteFigma(await _controller.ImportFigmaAsync(input.Request, input.Criteria, read, token).ConfigureAwait(false));
    });
    internal Task GenerateRemoteAiAsync(Func<IReadOnlyList<string>> capture) => RemoteAsync("正在发送所选来源到 OpenAI", () => NativeActivityInputs.Selected(capture), async (selected, token) =>
    {
        var read = NativeSourceCredentials.ForAction(_secrets, "openai-api-key");
        SourceDraft.CompleteAi(await _controller.GenerateWithAiAsync(selected, read, token).ConfigureAwait(false));
    });
    private async Task RemoteAsync<TInput>(string label, Func<TInput> capture, Func<TInput, CancellationToken, Task> action)
    {
        try
        {
            await Lifecycle.RetainActivityAsync(() => Activity.RemoteSourceAsync(capture, async (input, token) =>
            {
                SourceDraft.Begin(label);
                await NotifyPagesAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await action(input, token).ConfigureAwait(false);
                return true;
            }, async failure =>
            {
                if (failure is not null and not NativeActivityAdmissionException) SourceDraft.Fail(failure);
                await NotifyPagesAsync().ConfigureAwait(false);
            }, NotifyPagesAsync)).ConfigureAwait(false);
        }
        catch (NativeActivityAdmissionException) { throw; }
        catch (Exception) { /* The retained remote settlement owns safe failure; no late status/UI postfix follows activity release. */ }
    }
    internal async Task CancelSourceAsync()
    {
        try { await _remote.RequestCancellationAsync().ConfigureAwait(false); }
        catch (Exception) { /* Remote settlement owns the failure message; a late cancel continuation must not overwrite a later action. */ }
        await NotifyPagesAsync().ConfigureAwait(false);
    }
    private Task NotifyPagesAsync() => NotifyPagesAsync(null);
    private async Task NotifyPagesAsync(NativeReopening? reopening)
    { foreach (var page in Pages()) await page.NotifyAsync(reopening).ConfigureAwait(false); }
    private FrameworkElement CreatePage()
    {
        _context.Lifetime.ThrowIfCancellationRequested();
        return new TestingPage(this);
    }
    internal bool Attach(TestingPage page)
    {
        lock (_pagesSync)
        {
            if (_pagesClosed) return false;
            _pages.Add(page);
            return true;
        }
    }
    private void ClosePages() { lock (_pagesSync) _pagesClosed = true; }
    internal void Detach(TestingPage page) { lock (_pagesSync) _pages.Remove(page); }
    private TestingPage[] Pages() { lock (_pagesSync) return _pages.ToArray(); }
    private Task PrepareAsync(CancellationToken token) => Lifecycle.PrepareLeaveAsync(token);
    private async Task RetirePagesAsync()
    {
        var pages = Pages();
        foreach (var page in pages) page.Detach();
        await Task.WhenAll(pages.Select(page => page.RetireAsync())).ConfigureAwait(false);
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ClosePages();
        try { await Lifecycle.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            _lifetimeRegistration.Dispose();
        }
    }
}
