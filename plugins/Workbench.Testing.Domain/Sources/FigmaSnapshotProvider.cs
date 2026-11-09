using Workbench.Sdk;
namespace Workbench.Testing.Domain.Sources;
public sealed class FigmaSnapshotProvider : ITestSourceProvider
{
    public string Id => "builtin.figma-snapshot-json";
    public string Version => "1";
    public TestSourceKind Kind => TestSourceKind.Figma;
    public TestSourceDraft Parse(ReadOnlyMemory<byte> bytes) => LocalSnapshotParser.Parse(bytes, this, "nodes", "name");
}
