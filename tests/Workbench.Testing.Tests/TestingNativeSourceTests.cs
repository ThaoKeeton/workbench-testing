using System.Net;
using System.Text;
using Workbench.Sdk;
using Workbench.Testing;
using Workbench.Testing.Domain;
using Xunit;

namespace Workbench.Core.Tests;

public sealed class TestingNativeSourceTests
{
    // Losing the owned form copy or assigning criteria by row index after an edit changes real published records.
    [Fact]
    public async Task Captured_rows_publish_their_original_node_criteria_while_reopened_form_keeps_later_input()
    {
        var draft = new NativeSourceDraft();
        NativeFigmaNode[] rows = [new("2:3", "  First literal  \r\nSecond"), new("1:2", "Other 🧪")];
        string[] selected = [new('a', 32)];
        draft.Capture("Ab12", "v1", rows, selected);
        var captured = draft.CaptureFigma();
        rows[0] = new("8:9", "Mutated caller row"); selected[0] = new('b', 32);
        using var handler = new FigmaHandler();
        await using var fixture = await TestingDomainFixture.CreateAsync(figmaReader: new(handler));
        var key = new AsyncTestGate();
        var import = fixture.Controller.ImportFigmaAsync(captured.Request, captured.Criteria, async _ =>
        { await key.HoldAsync(); return "synthetic-native-token"; });
        await key.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        draft.Capture("EditedKey", "", [new("7:8", "Correction retained")], draft.Reopen().SelectedSourceIds);
        key.Release.SetResult();
        var saved = await import;
        draft.CompleteFigma(saved);
        Assert.Equal("Ab12@v1", saved.DocumentId);
        Assert.Equal(new[] { "2:3", "1:2" }, saved.Items.Select(i => i.Locator));
        Assert.Equal(new[] { "  First literal  ", "Second" }, saved.Items[0].AcceptanceCriteria);
        Assert.Equal("Other 🧪", Assert.Single(saved.Items[1].AcceptanceCriteria));
        var reopened = draft.Reopen();
        Assert.Equal("EditedKey", reopened.FileKey);
        Assert.Equal(new string('a', 32), Assert.Single(reopened.SelectedSourceIds));
        Assert.Equal("Correction retained", Assert.Single(draft.CaptureFigma().Criteria["7:8"]));
        Assert.Contains(saved.Id, draft.Status);
    }

    [Fact]
    public void Row_changes_and_failed_capture_preserve_the_other_literal_inputs_for_correction()
    {
        var draft = new NativeSourceDraft();
        draft.Capture("File", "v2", [new("1:2", "Original\n=literal"), new("3:4", "Keep")], []);
        draft.AddNode();
        draft.RemoveNode(0);
        var reopened = draft.Reopen();
        draft.Capture(reopened.FileKey, reopened.Version, [reopened.Nodes[0], new("3:4", "Duplicate")], []);
        Assert.Throws<PluginTestingException>(() => draft.CaptureFigma());
        draft.Fail(new PluginTestingException("InvalidTestSource"));
        reopened = draft.Reopen(); // A new page is populated from the same project-session model.
        draft.Capture(reopened.FileKey, reopened.Version, reopened.Nodes, reopened.SelectedSourceIds);
        Assert.Contains("InvalidTestSource", draft.Status);
        draft.RemoveNode(1);
        Assert.Equal("v2", draft.CaptureFigma().Request.Version);
        Assert.Equal("Keep", Assert.Single(draft.CaptureFigma().Criteria["3:4"]));
        Assert.Throws<PluginTestingException>(() => draft.RemoveNode(0));
        for (var i = 1; i < 16; i++) draft.AddNode();
        Assert.Throws<PluginTestingException>(() => draft.AddNode());
    }

    [Fact]
    public void External_generation_requires_actual_selection_and_copies_selected_identities_only()
    {
        var draft = new NativeSourceDraft();
        Assert.Throws<PluginTestingException>(() => draft.SelectedForAi());
        string[] ids = [new('a', 32), new('b', 32)];
        draft.Capture("", "", [new("", "")], ids);
        var captured = draft.SelectedForAi();
        ids[0] = new('c', 32);
        draft.Capture("", "", [new("", "")], []);
        Assert.Equal(new[] { new string('a', 32), new string('b', 32) }, captured);
        Assert.Throws<PluginTestingException>(() => draft.SelectedForAi());
    }

    [Theory]
    [InlineData(AiGenerationPublicationState.Cancelled, null)]
    [InlineData(AiGenerationPublicationState.Closed, "TestingContextClosed")]
    [InlineData(AiGenerationPublicationState.Failed, "StoreIoFailure")]
    public async Task Partial_acknowledgment_remains_visible_after_the_remote_operation_and_form_reopen(AiGenerationPublicationState state, string? code)
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var receipt = await fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements);
        var draft = new NativeSourceDraft();
        var remote = new NativeRemoteOperation();
        var display = new AsyncTestGate();
        var operation = remote.RunAsync(async _ =>
        {
            draft.CompleteAi(new(receipt, null, state, code));
            await display.HoldAsync();
            return receipt.Id;
        });
        await display.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(remote.IsBusy);
        Assert.Contains(receipt.Id, draft.Status);
        Assert.Contains(state.ToString(), draft.Status);
        Assert.DoesNotContain("已创建用例集", draft.Status);
        if (code is not null) Assert.Contains(code, draft.Status);
        display.Release.SetResult(); await operation;
        draft.Fail(new OperationCanceledException()); // A late cancellation fault must not erase the acknowledged receipt.
        var reopened = draft.Reopen();
        draft.Capture(reopened.FileKey, reopened.Version, reopened.Nodes, reopened.SelectedSourceIds);
        Assert.Contains(receipt.Id, draft.Status);
        Assert.Contains("仅回执", draft.Status);
    }

    [Fact]
    public async Task Acknowledged_case_set_is_reported_as_created_and_unknown_failure_never_echoes_provider_details()
    {
        await using var fixture = await TestingDomainFixture.CreateAsync();
        var receipt = await fixture.Controller.ImportExampleAsync(TestSourceKind.Requirements);
        var set = await fixture.Controller.GenerateAsync([receipt.Id]);
        var draft = new NativeSourceDraft();
        draft.CompleteAi(new(receipt, set, AiGenerationPublicationState.Created, null));
        Assert.Contains("已创建用例集", draft.Status); Assert.Contains(set.Id, draft.Status); Assert.Contains(receipt.Id, draft.Status);
        draft.Fail(new IOException("upstream-private-synthetic-key"));
        Assert.Contains("TestingFailed", draft.Status); Assert.DoesNotContain("upstream-private", draft.Status);
    }

    [Fact]
    public async Task Secret_reader_uses_the_captured_project_store_only_on_explicit_invocation_and_refuses_unsupported_storage()
    {
        var first = new SyntheticSecrets(true, "project-one");
        var second = new SyntheticSecrets(true, "project-two");
        var read = NativeSourceCredentials.ForAction(first, "figma-token");
        Assert.Empty(first.Reads); Assert.Empty(second.Reads);
        Assert.Equal("project-one", await read(CancellationToken.None));
        Assert.Equal(new[] { "figma-token" }, first.Reads); Assert.Empty(second.Reads);
        var unsupported = new SyntheticSecrets(false, "unused");
        var error = Assert.Throws<PluginSecretStoreException>(() => NativeSourceCredentials.ForAction(unsupported, "openai-api-key"));
        Assert.Equal("SecretStorageUnsupported", error.ErrorCode); Assert.Empty(unsupported.Reads);
    }

    private sealed class FigmaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.figma.com/v1/files/Ab12/nodes?ids=2%3A3%2C1%3A2&depth=2&version=v1", request.RequestUri!.AbsoluteUri);
            Assert.Equal("synthetic-native-token", Assert.Single(request.Headers.GetValues("X-Figma-Token")));
            var content = new StringContent("""{"name":"Observed","version":"v1","nodes":{"2:3":{"document":{"id":"2:3","name":"First","type":"FRAME"}},"1:2":{"document":{"id":"1:2","name":"Second","type":"FRAME"}}}}""", Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SyntheticSecrets(bool supported, string value) : IPluginSecretStore
    {
        internal List<string> Reads { get; } = [];
        public bool IsSupported => supported;
        public void Register(PluginSecretDefinition definition) => throw new NotSupportedException();
        public IReadOnlyList<PluginSecretDefinition> GetDefinitions() => [];
        public Task<string?> ReadAsync(string id, CancellationToken cancellationToken = default) { Reads.Add(id); return Task.FromResult<string?>(value); }
        public Task WriteAsync(string id, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
