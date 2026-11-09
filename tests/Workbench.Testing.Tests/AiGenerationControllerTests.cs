using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Generation.Ai;
using Xunit;
using static Workbench.Core.Tests.PluginTestingContextTests;
using static Workbench.Core.Tests.ProjectTestStoreTests;

namespace Workbench.Core.Tests;

public sealed partial class AiGenerationControllerTests
{
    private const string Model="gpt-5.4-mini-2026-03-17";
    private const string Secret="synthetic-only-controller-ai-key";
    private const string Private="private upstream synthetic detail";
    private const string Prefix="AI generated; manual review required. Model: gpt-5.4-mini-2026-03-17.";
    private static readonly TimeSpan Budget=TimeSpan.FromSeconds(10);
    private static Task<string?> Key(CancellationToken _)=>Task.FromResult<string?>(Secret);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static TestSourceDraft Original(string document="original",params string[] criteria)=>new(
        TestSourceKind.Requirements,"manual.requirements","1",document,"Original source 🧪",Encoding.UTF8.GetBytes("{\"original\":true}"),
        [new("original/item","Original item","Literal description",criteria.Length==0 ? ["Original zero","Original one"] : criteria,TestVirtualScenario.TaskFormPositive)]);
    private static byte[] Raw(string prerequisites="",int steps=2)=>Encoding.UTF8.GetBytes(" \r\n"+JsonSerializer.Serialize(new
    {
        id="resp_controller-1",model=Model,status="completed",output=new[] { new { type="message",role="assistant",status="completed",
            content=new[] { new { type="output_text",text=JsonSerializer.Serialize(new { cases=new[]{1,0}.Select(index=>new
            {
                criterionIndex=index,title="  Generated "+index+" 🧪  ",prerequisites,
                steps=Enumerable.Range(0,steps).Select(step=>new { instruction="  Manual "+index+"/"+step+"  ",expected="=expected,\""+index+"/"+step+"\"\r\nline" })
            }) }) } } } }
    })+"\n");
    private static HttpResponseMessage Response(byte[] bytes)=>ContentResponse(new ByteArrayContent(bytes));
    private static HttpResponseMessage ContentResponse(HttpContent content)
    {
        content.Headers.ContentType=new MediaTypeHeaderValue("application/json");
        return new(HttpStatusCode.OK) { Content=content };
    }
    private static async Task CodeAsync(string code,Func<Task> action)
    {
        var error=await Assert.ThrowsAsync<PluginTestingException>(action);Assert.Equal(code,error.ErrorCode);
        Assert.Null(error.InnerException);Assert.DoesNotContain(Secret,error.ToString(),StringComparison.Ordinal);Assert.DoesNotContain(Private,error.ToString(),StringComparison.Ordinal);
    }
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var elapsed=Stopwatch.StartNew();
        while(!predicate()) { Assert.True(elapsed.Elapsed<Budget,"The actual retained boundary was not reached.");await Task.Delay(1); }
    }
    private static string SourcePath(string root,string id)=>Path.Combine(StoreRoot(root),"sources",id+".json");
    private sealed class SyntheticHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Requests;
        internal byte[]? Input;
        internal bool Disposed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Assert.Equal("POST",request.Method.Method);Assert.Equal("https://api.openai.com/v1/responses",request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer "+Secret,request.Headers.Authorization!.ToString());
            using var body=JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
            Input=Encoding.UTF8.GetBytes(body.RootElement.GetProperty("input").GetString()!);
            return await send(request,ct);
        }
        protected override void Dispose(bool disposing) { Disposed=true;base.Dispose(disposing); }
    }
    private sealed class Rig : IAsyncDisposable
    {
        internal TestDirectory Disk { get; }=new();
        internal PluginRegistrationContext Context { get; }
        internal BoundaryWorkspace Bridge { get; }
        internal TestingController Controller { get; }
        internal TestSourceSnapshot Source { get; private set; }=null!;
        private Rig(SyntheticHandler handler)
        {
            Context=Registration(new ProjectRuntimeScope("default","Default",Disk.User));Bridge=new(Context);
            Controller=new(Bridge,null,new(),new(handler));
        }
        internal static async Task<Rig> CreateAsync(SyntheticHandler handler)
        {
            var rig=new Rig(handler);
            try
            {
                rig.Source=await rig.Context.Testing.PublishSourceAsync(Original());
                await rig.Controller.InitializeAsync();rig.Context.RegisterBeforeProjectLeave(rig.Controller.PrepareLeaveAsync);
                Assert.True(await rig.Context.BackgroundTasks.PrepareCommitAsync(CancellationToken.None));rig.Context.Commit();
                return rig;
            }
            catch { await rig.DisposeAsync();throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Context.Close(PluginStopReason.PluginDisabled);
            await Task.WhenAll(Context.CancelLifetimeAsync(),Controller.StopAsync(),Context.BackgroundTasks.StopAllAsync(),Context.DrainProjectWorkAsync());
            Context.Dispose();Disk.Dispose();
        }
    }
    // Gates only actual facade boundaries. Reads, validation, commits and acknowledgments remain real Core work.
    private sealed class BoundaryWorkspace(IPluginTestingContext actual) : IPluginTestingContext,IProjectTestWorkspace
    {
        internal Action? SourcePrefix { get; set; }
        internal Action? CasePrefix { get; set; }
        internal Func<TestSourceSnapshot,Task>? SourcePostfix { get; set; }
        internal Func<TestCaseSetSnapshot,Task>? CasePostfix { get; set; }
        internal Func<CancellationToken,Task>? ReadPrefix { get; set; }
        internal TestSourceSnapshot? CommittedSource;
        internal TestCaseSetSnapshot? CommittedCase;
        internal int SourceWrites,CaseWrites,Reads,ListReads;
        internal bool RefuseLists;
        public async Task<TestSourceSnapshot> PublishSourceAsync(TestSourceDraft draft,CancellationToken ct=default)
        {
            SourcePrefix?.Invoke();Interlocked.Increment(ref SourceWrites);
            var result=await actual.Testing.PublishSourceAsync(draft,ct);CommittedSource=result;
            if(SourcePostfix is not null) await SourcePostfix(result);return result;
        }
        public async Task<TestCaseSetSnapshot> CreateCaseSetAsync(TestCaseSetDraft draft,CancellationToken ct=default)
        {
            CasePrefix?.Invoke();Interlocked.Increment(ref CaseWrites);
            var result=await actual.Testing.CreateCaseSetAsync(draft,ct);CommittedCase=result;
            if(CasePostfix is not null) await CasePostfix(result);return result;
        }
        public async Task<TestSourceSnapshot> ReadSourceAsync(string id,CancellationToken ct=default)
        { Interlocked.Increment(ref Reads);if(ReadPrefix is not null) await ReadPrefix(ct);return await actual.Testing.ReadSourceAsync(id,ct); }
        private void List() { Interlocked.Increment(ref ListReads);if(RefuseLists) throw new IOException(Private); }
        public Task<IReadOnlyList<TestSourceSummary>> ListSourcesAsync(CancellationToken ct=default) { List();return actual.Testing.ListSourcesAsync(ct); }
        public Task<IReadOnlyList<TestCaseSetSummary>> ListCaseSetsAsync(CancellationToken ct=default) { List();return actual.Testing.ListCaseSetsAsync(ct); }
        public Task<IReadOnlyList<TestRunSummary>> ListRunsAsync(CancellationToken ct=default) { List();return actual.Testing.ListRunsAsync(ct); }
        public string PluginId=>actual.PluginId;public string DataDirectory=>actual.DataDirectory;public CancellationToken Lifetime=>actual.Lifetime;
        public IPluginSettings Settings=>actual.Settings;public IPluginBackgroundTasks BackgroundTasks=>actual.BackgroundTasks;public IPluginSecretStore Secrets=>actual.Secrets;
        public PluginStopReason StopReason=>actual.StopReason;public IProjectContext Project=>actual.Project;public IProjectTestWorkspace Testing=>this;
        public void RegisterBeforeProjectLeave(Func<CancellationToken,Task> hook)=>actual.RegisterBeforeProjectLeave(hook);
        public void RegisterView(IPluginView view)=>actual.RegisterView(view);public void RegisterCommand(PluginCommand command)=>actual.RegisterCommand(command);public void RegisterSetting(PluginSetting setting)=>actual.RegisterSetting(setting);
        public Task<TestCaseSetSnapshot> ReadCaseSetAsync(string id,CancellationToken ct=default)=>actual.Testing.ReadCaseSetAsync(id,ct);
        public Task<TestCaseSetSnapshot> UpdateCaseSetAsync(string id,TestCaseSetContent content,string revision,CancellationToken ct=default)=>actual.Testing.UpdateCaseSetAsync(id,content,revision,ct);
        public Task<TestCaseSetSnapshot> ReadCaseRevisionAsync(string id,string revision,CancellationToken ct=default)=>actual.Testing.ReadCaseRevisionAsync(id,revision,ct);
        public Task<TestRunSnapshot> CreateRunAsync(string id,string revision,IReadOnlyList<string> selected,CancellationToken ct=default)=>actual.Testing.CreateRunAsync(id,revision,selected,ct);
        public Task<TestRunSnapshot> CreateRetryRunAsync(string id,CancellationToken ct=default)=>actual.Testing.CreateRetryRunAsync(id,ct);
        public Task<TestRunSnapshot> ReadRunAsync(string id,CancellationToken ct=default)=>actual.Testing.ReadRunAsync(id,ct);
        public Task<TestRunSnapshot> AppendRunEventAsync(string id,long sequence,string head,TestRunEventDraft draft,CancellationToken ct=default)=>actual.Testing.AppendRunEventAsync(id,sequence,head,draft,ct);
        public Task<TestEvidenceReceipt> AddRunEvidenceAsync(string id,TestEvidenceDraft draft,CancellationToken ct=default)=>actual.Testing.AddRunEvidenceAsync(id,draft,ct);
        public Task<TestEvidence> ReadRunEvidenceAsync(string id,string evidence,CancellationToken ct=default)=>actual.Testing.ReadRunEvidenceAsync(id,evidence,ct);
    }
}
