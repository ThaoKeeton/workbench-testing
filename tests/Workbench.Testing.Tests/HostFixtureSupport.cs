// Test-only fixture helpers extracted from the pinned host tests. No host contract tests moved.
using System.Buffers.Binary;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Workbench.Core;
using Workbench.Core.Projects;
using Workbench.Core.Testing;
using Workbench.Sdk;
using Xunit;
using static Workbench.Core.Tests.ProjectTestStoreTests;
namespace Workbench.Core.Tests;
internal static class ProjectTestStoreTests
{
    internal static TestSourceDraft Source(byte[]? bytes = null, IReadOnlyList<TestSourceItem>? items = null) => new(TestSourceKind.Requirements, "builtin.requirements", "1", "doc", "Source", bytes ?? Encoding.UTF8.GetBytes("{}"), items ?? [new("../../x", "Item", "", ["Criterion"], null)]);
    internal static TestCaseSetDraft Draft(TestSourceSnapshot source)
    {
        TestTrace[] traces = [new(source.Id, source.Revision, "../../x", 0)];
        return new("Set", [new("Case", "", traces, [new("Set title", "Title entered", traces, new(TestActionKind.SetText, "task.title", "Test"))])]);
    }
    internal static string StoreRoot(string user) => Path.Combine(user, ".workbench-project", "testing");
    internal static string SourcePath(string user, string id) => Path.Combine(StoreRoot(user), "sources", id + ".json");
    internal static string CasePath(string user, string id) => Path.Combine(StoreRoot(user), "cases", id + ".json");
    internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static async Task Code(string expected, Func<Task> action)
    {
        var failure = await Assert.ThrowsAsync<PluginTestingException>(action);
        Assert.Equal(expected, failure.ErrorCode);
        Assert.Equal("Testing operation failed.", failure.Message);
        Assert.Null(failure.InnerException);
    }
}

internal static class PluginTestingContextTests
{
    internal const string Owner = "com.projectworkbench.testing";
    internal const string Other = "com.projectworkbench.executor";
    internal static PluginRegistrationContext Registration(ProjectRuntimeScope scope, string producer = Owner)
        => new(producer, Path.Combine(scope.UserRoot, "data", producer), new JsonPluginSettings(Path.Combine(scope.UserRoot, "settings", producer), _ => { }),
            new PluginBackgroundTasks(new Dictionary<string, SavedTaskIntent>(), (_, _, _) => Task.CompletedTask, _ => { }, TimeSpan.FromMilliseconds(75)),
            new PluginSecretStore(scope.UserRoot, producer, new WindowsSecretProtector(), _ => { }), scope);

    internal static IProjectTestWorkspace Workspace(PluginRegistrationContext context) => Assert.IsAssignableFrom<IPluginTestingContext>(context).Testing;
    internal static async Task<TestCaseSetSnapshot> Seed(ProjectTestStore store)
        => await store.CreateCaseSetAsync(Owner, Draft(await store.PublishSourceAsync(Owner, Source())));
    internal static FileStream HoldLease(string root) => new(Path.Combine(root, ".workbench-project", "testing.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    internal static byte[] Frame()
    {
        var bytes = new byte[172854];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), 172854);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 320);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 180);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), 172800);
        return bytes;
    }

    internal sealed class HeldList<T>(T[] values) : IReadOnlyList<T>, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get { Entered.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return values.Length; } }
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Release.TrySetResult();
    }

}
