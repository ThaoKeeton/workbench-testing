using Workbench.Sdk;
namespace Workbench.Testing.Domain;
public static class SyntheticTestingExamples
{
    public static byte[] Read(TestSourceKind kind)
    {
        var file = kind switch { TestSourceKind.Requirements => "requirements", TestSourceKind.Figma => "figma", _ => throw new PluginTestingException("InvalidTestSource") };
        using var stream = typeof(SyntheticTestingExamples).Assembly.GetManifestResourceStream($"Workbench.Testing.Domain.Fixtures.{file}.synthetic.json")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }
}
