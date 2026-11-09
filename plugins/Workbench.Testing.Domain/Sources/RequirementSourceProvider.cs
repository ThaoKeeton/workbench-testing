using Workbench.Sdk;
namespace Workbench.Testing.Domain.Sources;
public sealed class RequirementSourceProvider : ITestSourceProvider
{
    public string Id => "builtin.requirements-json";
    public string Version => "1";
    public TestSourceKind Kind => TestSourceKind.Requirements;
    public TestSourceDraft Parse(ReadOnlyMemory<byte> bytes) => LocalSnapshotParser.Parse(bytes, this, "requirements", "title");
}
