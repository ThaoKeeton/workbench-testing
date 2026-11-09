using Workbench.Sdk;
using Workbench.Testing.Domain;
using Workbench.Testing.Domain.Generation.Ai;
using Workbench.Testing.Domain.Sources.Figma;

namespace Workbench.Testing;

internal sealed record TestingSourceServices(FigmaFileReader Figma, AiTestCaseGenerator Ai)
{
    internal static TestingSourceServices CreateDefault() => new(new(), new());
    internal TestingController CreateController(IPluginTestingContext context) => new(context, null, Figma, Ai);
}
