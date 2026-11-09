using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Generation.Ai;
using Workbench.Testing.Domain.Sources.Figma;
using Workbench.Testing.SourceAcceptance;
using Xunit;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed partial class SourceAcceptanceTests
{
    private const string FixtureId = "com.projectworkbench.testing.source-acceptance";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static FigmaReadRequest Request => new("SourceAcceptanceFile1", ["2:3", "1:2"], "source-v1");
    private static Dictionary<string, IReadOnlyList<string>> Criteria() => new()
    {
        ["2:3"] = ["Synthetic heading is visible.", "Synthetic navigation is reviewed manually."],
        ["1:2"] = ["Synthetic confirmation is reviewed manually."]
    };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static JsonDocument State(SourceAcceptanceScenarioLedger ledger) => JsonDocument.Parse(ledger.StateJson());
    private static long Count(SourceAcceptanceScenarioLedger ledger, string provider, string name)
    { using var state = State(ledger); return state.RootElement.GetProperty(provider).GetProperty(name).GetInt64(); }

    // A bypass of the real protocol/credential namespace or an implicit request fails this test.
    [Fact]
    public async Task PassiveAndIsolation_actual_scoped_services_stay_passive_and_refuse_foreign_contexts()
    {
        await using var rig = await Rig.CreateAsync(seedBeforeInitialize: true);
        await using var other = await Rig.CreateAsync(projectId: new string('a', 32));
        using var state = State(rig.Ledger);
        Assert.Equal(1, state.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("default", state.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(FixtureId, state.RootElement.GetProperty("pluginId").GetString());
        Assert.Equal(0, rig.Protector.Reads);
        Assert.True((await rig.Command("source-acceptance-state")).Succeeded);
        Assert.True((await rig.Command("source-acceptance-arm-figma-happy")).Succeeded);
        await rig.Controller.RefreshAsync();
        await rig.Context.PrepareProjectLeaveAsync(CancellationToken.None);
        Assert.Equal(0, rig.Protector.Reads); Assert.Equal(0, other.Protector.Reads);
        Assert.Equal(0, Count(rig.Ledger, "figma", "entered"));
        Assert.Equal(0, Count(rig.Ledger, "ai", "entered"));
        Assert.Empty(await rig.Context.Testing.ListSourcesAsync());
        Assert.Empty(await other.Context.Testing.ListSourcesAsync());
        using var foreign = rig.NewContext("com.example.foreign", "default");
        Assert.Throws<ArgumentException>(() => SourceAcceptanceProtocol.Register(foreign, rig.Ledger, () => true));
        Assert.Empty(foreign.Snapshot().Commands);
        using var wrongProject = rig.NewContext(FixtureId, new string('b', 32));
        Assert.Throws<ArgumentException>(() => SourceAcceptanceProtocol.Register(wrongProject, rig.Ledger, () => true));
        Assert.Empty(wrongProject.Snapshot().Commands);
        foreign.Secrets.Register(new("figma-token", "Synthetic token"));
        Assert.Null(await foreign.Secrets.ReadAsync("figma-token"));
        Assert.Null(await other.Context.Secrets.ReadAsync("figma-token"));
        Assert.DoesNotContain(SourceAcceptanceProtocol.FigmaToken, rig.Ledger.StateJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(SourceAcceptanceProtocol.OpenAiKey, rig.Ledger.StateJson(), StringComparison.Ordinal);
    }

    // Losing the injected clients, response-index mapping, acknowledgment or manual boundary fails here.
    [Fact]
    public async Task HappyTraceAndReopen_real_clients_publish_selected_exact_bytes_and_manual_receipt_traces()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.SeedSecrets();
        var decoy = await rig.Controller.ImportExampleAsync(TestSourceKind.Requirements);
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Happy));
        var source = await rig.ImportFigma();
        Assert.Equal("SourceAcceptanceFile1@source-v1", source.DocumentId);
        Assert.Equal("figma.rest.nodes.manual-criteria", source.ProviderId);
        Assert.Equal(FixtureId, source.ProducerPluginId); Assert.Equal("default", source.ProjectId);
        Assert.Equal(new[] { "2:3", "1:2" }, source.Items.Select(i => i.Locator));
        Assert.Equal(Criteria()["2:3"], source.Items[0].AcceptanceCriteria);
        Assert.All(source.Items, item => Assert.Null(item.VirtualScenario));
        const string expectedFigma = """
            {"name":"Synthetic source acceptance file","version":"source-v1","nodes":{"2:3":{"document":{"id":"2:3","name":"Synthetic heading","type":"TEXT","characters":"Synthetic heading text"}},"1:2":{"document":{"id":"1:2","name":"Synthetic confirmation","type":"TEXT","characters":"Synthetic confirmation text"}}}}
            """;
        Assert.Equal(Encoding.UTF8.GetBytes(expectedFigma), source.OriginalBytes);
        using (var raw = JsonDocument.Parse(source.OriginalBytes))
        {
            Assert.Equal("source-v1", raw.RootElement.GetProperty("version").GetString());
            Assert.Equal("TEXT", raw.RootElement.GetProperty("nodes").GetProperty("2:3").GetProperty("document").GetProperty("type").GetString());
        }
        var stored = new ProjectTestStore(rig.Disk.User, "default");
        Assert.Equal(source.OriginalBytes, (await stored.ReadSourceAsync(source.Id)).OriginalBytes);
        Assert.Equal(Hash(source.OriginalBytes), source.OriginalSha256);
        Assert.Equal(Hash(await File.ReadAllBytesAsync(SourcePath(rig.Disk.User, source.Id))), source.Revision);
        Assert.True(rig.Ledger.TryArm(SourceAcceptanceProvider.Ai, SourceAcceptanceScenario.Happy));
        var publication = await rig.GenerateAi(source.Id);
        Assert.Equal(AiGenerationPublicationState.Created, publication.State); Assert.Null(publication.ErrorCode);
        var receipt = publication.Receipt; var set = Assert.IsType<TestCaseSetSnapshot>(publication.CaseSet);
        Assert.Equal("openai.responses.test-cases", receipt.ProviderId);
        Assert.Equal("resp_source_acceptance_1", receipt.DocumentId);
        Assert.Equal(FixtureId, receipt.ProducerPluginId);
        using var json = JsonDocument.Parse(receipt.OriginalBytes);
        var root = json.RootElement;
        var input = root.GetProperty("inputUtf8Base64").GetBytesFromBase64();
        var response = root.GetProperty("responseUtf8Base64").GetBytesFromBase64();
        var expectedInput = $$"""
            {"sources":[{"snapshotId":"{{source.Id}}","snapshotRevision":"{{source.Revision}}","kind":"Figma","providerId":"figma.rest.nodes.manual-criteria","providerVersion":"1","documentId":"SourceAcceptanceFile1@source-v1","title":"Synthetic source acceptance file","items":[{"locator":"2:3","title":"Synthetic heading","description":"Observed type: TEXT\nObserved characters: Synthetic heading text","criteria":[{"criterionIndex":0,"text":"Synthetic heading is visible."},{"criterionIndex":1,"text":"Synthetic navigation is reviewed manually."}]},{"locator":"1:2","title":"Synthetic confirmation","description":"Observed type: TEXT\nObserved characters: Synthetic confirmation text","criteria":[{"criterionIndex":2,"text":"Synthetic confirmation is reviewed manually."}]}]}]}
            """;
        Assert.Equal(Encoding.UTF8.GetBytes(expectedInput), input);
        const string generated = """
            {"cases":[{"criterionIndex":2,"title":"Synthetic manual case 2","prerequisites":"Synthetic manual review only.","steps":[{"instruction":"Review synthetic criterion 2 manually.","expected":"Synthetic criterion 2 is satisfied."}]},{"criterionIndex":0,"title":"Synthetic manual case 0","prerequisites":"Synthetic manual review only.","steps":[{"instruction":"Review synthetic criterion 0 manually.","expected":"Synthetic criterion 0 is satisfied."}]},{"criterionIndex":1,"title":"Synthetic manual case 1","prerequisites":"Synthetic manual review only.","steps":[{"instruction":"Review synthetic criterion 1 manually.","expected":"Synthetic criterion 1 is satisfied."}]}]}
            """;
        var expectedResponse = JsonSerializer.SerializeToUtf8Bytes(new { id = "resp_source_acceptance_1", model = "gpt-5.4-mini-2026-03-17", status = "completed",
            output = new[] { new { type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = generated } } } } });
        Assert.Equal(expectedResponse, response);
        Assert.Equal(Hash(input), root.GetProperty("inputSha256").GetString());
        Assert.Equal(Hash(response), root.GetProperty("responseSha256").GetString());
        Assert.DoesNotContain(decoy.Id, Encoding.UTF8.GetString(input), StringComparison.Ordinal);
        using var sent = JsonDocument.Parse(input);
        var selected = Assert.Single(sent.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal(source.Id, selected.GetProperty("snapshotId").GetString());
        Assert.Equal(source.Revision, selected.GetProperty("snapshotRevision").GetString());
        Assert.DoesNotContain("originalBytes", Encoding.UTF8.GetString(input), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("virtualScenario", Encoding.UTF8.GetString(input), StringComparison.OrdinalIgnoreCase);
        var traces = new[] { new TestTrace(source.Id, source.Revision, "2:3", 0), new(source.Id, source.Revision, "2:3", 1), new(source.Id, source.Revision, "1:2", 0) };
        var originals = root.GetProperty("originalTraces").EnumerateArray().ToArray();
        Assert.Equal(3, originals.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(traces[i].Locator, originals[i].GetProperty("locator").GetString());
            Assert.Equal(traces[i].CriterionIndex, originals[i].GetProperty("criterionIndex").GetInt32());
            var expected = traces[new[] { 2, 0, 1 }[i]];
            Assert.Equal(expected, set.Cases[i].Traces[0]);
            Assert.Equal(new TestTrace(receipt.Id, receipt.Revision, "case-" + (i + 1), 0), set.Cases[i].Traces[1]);
            var step = Assert.Single(set.Cases[i].Steps);
            Assert.Equal(expected, step.Traces[0]); Assert.Null(step.Action);
        }
        Assert.All(receipt.Items, item => Assert.Null(item.VirtualScenario));
        Assert.Equal(receipt.OriginalBytes, (await stored.ReadSourceAsync(receipt.Id)).OriginalBytes);
        Assert.Equal(set.Revision, (await stored.ReadCaseSetAsync(set.Id)).Revision);
        Assert.Equal("ManualStepUnsupported", (await Assert.ThrowsAsync<PluginTestingException>(() => rig.Controller.QueueRunAsync([set.Cases[0].Id]))).ErrorCode);
        Assert.All(rig.Context.BackgroundTasks.GetSnapshots(), task => Assert.Equal(PluginBackgroundTaskState.Stopped, task.State));
        Assert.Equal(1, Count(rig.Ledger, "figma", "entered")); Assert.Equal(1, Count(rig.Ledger, "ai", "settled"));
        Assert.False(rig.Figma.IsDisposed); Assert.False(rig.Ai.IsDisposed);
        foreach (var path in Directory.EnumerateFiles(StoreRoot(rig.Disk.User), "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Assert.DoesNotContain(SourceAcceptanceProtocol.FigmaToken, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SourceAcceptanceProtocol.OpenAiKey, text, StringComparison.Ordinal);
        }
    }

    // Control registration, active-generation protection and the cap must have observable consequences.
    [Fact]
    public async Task ProtocolAndLimits_only_fixed_commands_arm_once_and_counters_require_actual_handler_entry()
    {
        await using var rig = await Rig.CreateAsync(); await rig.SeedSecrets();
        Assert.Equal(new[] { "source-acceptance-arm-ai-happy", "source-acceptance-arm-ai-hold", "source-acceptance-arm-ai-malformed", "source-acceptance-arm-ai-partial",
            "source-acceptance-arm-figma-happy", "source-acceptance-arm-figma-hold", "source-acceptance-arm-figma-http-failure", "source-acceptance-arm-figma-malformed", "source-acceptance-release", "source-acceptance-state" },
            rig.Context.Snapshot().Commands.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.False(rig.Ledger.TryArm(SourceAcceptanceProvider.None, SourceAcceptanceScenario.Happy));
        Assert.False(rig.Ledger.TryArm(SourceAcceptanceProvider.Figma, SourceAcceptanceScenario.Partial));
        rig.CanArm = false; Assert.False((await rig.Command("source-acceptance-arm-figma-happy")).Succeeded);
        rig.CanArm = true;
        for (var i = 0; i < 32; i++)
        {
            Assert.True((await rig.Command("source-acceptance-arm-figma-happy")).Succeeded);
            Assert.Equal(i, Count(rig.Ledger, "figma", "entered"));
            Assert.False((await rig.Command("source-acceptance-arm-ai-happy")).Succeeded);
            Assert.False((await rig.Command("source-acceptance-release")).Succeeded);
            await rig.ImportFigma();
        }
        Assert.False((await rig.Command("source-acceptance-arm-figma-happy")).Succeeded);
        Assert.False((await rig.Command("source-acceptance-arm-ai-happy")).Succeeded);
        using var state = State(rig.Ledger);
        Assert.True(state.RootElement.GetProperty("figma").GetProperty("limitExceeded").GetBoolean());
        Assert.True(state.RootElement.GetProperty("ai").GetProperty("limitExceeded").GetBoolean());
        Assert.Equal(32, Count(rig.Ledger, "figma", "accepted"));
        Assert.Equal(32, Count(rig.Ledger, "figma", "settled"));
        Assert.InRange(Encoding.UTF8.GetByteCount(rig.Ledger.StateJson()), 1, 8192);
        var delayed = rig.Context.FindCommand("source-acceptance-state")!;
        rig.Context.Close(PluginStopReason.PluginDisabled);
        Assert.False((await delayed.ExecuteAsync(CancellationToken.None)).Succeeded);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal TestDirectory Disk { get; } = new();
        internal SyntheticProtector Protector { get; } = new();
        internal List<PluginDiagnostic> Diagnostics { get; } = [];
        internal PluginRegistrationContext Context { get; }
        internal SourceAcceptanceScenarioLedger Ledger { get; }
        internal SourceAcceptanceFigmaHandler Figma { get; }
        internal SourceAcceptanceAiHandler Ai { get; }
        internal TestingController Controller { get; }
        internal NativeActionOwner Owner { get; } = new();
        internal NativeRemoteOperation Remote { get; } = new();
        internal NativeActivityCoordinator Activity { get; }
        internal NativeTestingLifecycle Life { get; }
        internal bool CanArm { get; set; } = true;
        internal Action? BeforeArm { get; set; }
        internal Func<Task> Notify { get; set; } = () => Task.CompletedTask;
        private readonly CancellationTokenRegistration _lifetime;
        private Rig(string projectId)
        {
            Context = NewContext(FixtureId, projectId);
            Context.Secrets.Register(new("figma-token", "Synthetic Figma credential"));
            Context.Secrets.Register(new("openai-api-key", "Synthetic AI credential"));
            Ledger = new(projectId); Figma = new(Ledger); Ai = new(Ledger);
            Controller = new TestingSourceServices(new(Figma), new(Ai)).CreateController(Context);
            Activity = new(Controller, Remote);
            Life = new(Owner, Activity, Remote, new(), Controller, null, _ => Notify(), () => Task.CompletedTask);
            _lifetime = Context.Lifetime.Register(Life.SealLifetime);
        }
        internal PluginRegistrationContext NewContext(string pluginId, string projectId)
        {
            var settings = new JsonPluginSettings(Path.Combine(Disk.User, "settings", pluginId), Diagnostics.Add);
            var tasks = new PluginBackgroundTasks(new Dictionary<string, SavedTaskIntent>(),
                (id, intent, token) => settings.SetAsync("intent-" + id, intent, token), Diagnostics.Add, TimeSpan.FromMilliseconds(75));
            return new(pluginId, Path.Combine(Disk.User, "data", pluginId), settings, tasks,
                new PluginSecretStore(Disk.User, pluginId, Protector, Diagnostics.Add), new ProjectRuntimeScope(projectId, "Synthetic project", Disk.User));
        }
        internal static async Task<Rig> CreateAsync(string projectId = "default", bool seedBeforeInitialize = false)
        {
            var rig = new Rig(projectId);
            try
            {
                if (seedBeforeInitialize) await rig.SeedSecrets();
                SourceAcceptanceProtocol.Register(rig.Context, rig.Ledger, () => { rig.BeforeArm?.Invoke(); return rig.CanArm && rig.Life.ReadAdmission().ActivityStarts; });
                await rig.Controller.InitializeAsync();
                rig.Context.RegisterBeforeProjectLeave(rig.Life.PrepareLeaveAsync);
                Assert.True(await rig.Context.BackgroundTasks.PrepareCommitAsync(CancellationToken.None)); rig.Context.Commit();
                return rig;
            }
            catch { await rig.DisposeAsync(); throw; }
        }
        internal Task SeedSecrets() => Task.WhenAll(Context.Secrets.WriteAsync("figma-token", SourceAcceptanceProtocol.FigmaToken),
            Context.Secrets.WriteAsync("openai-api-key", SourceAcceptanceProtocol.OpenAiKey));
        internal Task<TestSourceSnapshot> ImportFigma() => Controller.ImportFigmaAsync(Request, Criteria(), NativeSourceCredentials.ForAction(Context.Secrets, "figma-token"));
        internal Task<AiGenerationPublication> GenerateAi(string id) => Controller.GenerateWithAiAsync([id], NativeSourceCredentials.ForAction(Context.Secrets, "openai-api-key"));
        internal Task<PluginCommandResult> Command(string id) => Context.FindCommand(id)?.ExecuteAsync(CancellationToken.None)
            ?? Task.FromResult(new PluginCommandResult(false, "Unavailable synthetic command."));
        public async ValueTask DisposeAsync()
        {
            Ledger.ReleasePending(); Context.Close(PluginStopReason.PluginDisabled);
            await Task.WhenAll(Context.CancelLifetimeAsync(), Life.StopAsync(), Context.BackgroundTasks.StopAllAsync(), Context.DrainProjectWorkAsync());
            _lifetime.Dispose(); Figma.Dispose(); Ai.Dispose(); Context.Dispose(); Disk.Dispose();
        }
    }

    // Synthetic reversible test protector; the real SDK namespace/entropy/file work remains in Core.
    private sealed class SyntheticProtector : ISecretProtector
    {
        internal int Reads;
        public bool IsSupported => true;
        public byte[] Protect(byte[] plaintext, byte[] entropy) => [.. SHA256.HashData(entropy), .. plaintext.Select(b => (byte)(b ^ 0x5a))];
        public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
        {
            Interlocked.Increment(ref Reads);
            if (ciphertext.Length < 32 || !ciphertext.AsSpan(0, 32).SequenceEqual(SHA256.HashData(entropy))) throw new CryptographicException();
            return ciphertext[32..].Select(b => (byte)(b ^ 0x5a)).ToArray();
        }
    }
}
