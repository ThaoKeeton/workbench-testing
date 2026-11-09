using Workbench.Sdk;

namespace Workbench.Testing.SourceAcceptance;

internal static class SourceAcceptanceProtocol
{
    internal const string FixtureId = "com.projectworkbench.testing.source-acceptance";
    internal const string FigmaToken = "source-acceptance-figma-token";
    internal const string OpenAiKey = "source-acceptance-openai-key";
    internal static string ValidateContext(IPluginTestingContext context)
    {
        if (context.PluginId != FixtureId || context.StopReason != PluginStopReason.None || context.Lifetime.IsCancellationRequested)
            throw new ArgumentException("An open synthetic source fixture context is required.");
        var project = context.Project.Identity.Id;
        _ = new SourceAcceptanceScenarioLedger(project);
        return project;
    }
    internal static void Register(IPluginTestingContext context, SourceAcceptanceScenarioLedger ledger, Func<bool> canArm)
    {
        ArgumentNullException.ThrowIfNull(canArm);
        if (ValidateContext(context) != ledger.ProjectId) throw new ArgumentException("Synthetic project identity changed.");
        RegisterCommand("source-acceptance-state", () => new(true, ledger.StateJson()));
        foreach (var provider in new[] { SourceAcceptanceProvider.Figma, SourceAcceptanceProvider.Ai })
        foreach (var scenario in new[] { SourceAcceptanceScenario.Happy, SourceAcceptanceScenario.HttpFailure, SourceAcceptanceScenario.Malformed, SourceAcceptanceScenario.Partial, SourceAcceptanceScenario.Hold })
        {
            if (provider == SourceAcceptanceProvider.Figma && scenario == SourceAcceptanceScenario.Partial
                || provider == SourceAcceptanceProvider.Ai && scenario == SourceAcceptanceScenario.HttpFailure) continue;
            var name = scenario == SourceAcceptanceScenario.HttpFailure ? "http-failure" : scenario.ToString().ToLowerInvariant();
            RegisterCommand("source-acceptance-arm-" + provider.ToString().ToLowerInvariant() + "-" + name,
                () => Result(canArm() && Open() && ledger.TryArm(provider, scenario)));
        }
        RegisterCommand("source-acceptance-release", () => Result(ledger.ReleasePending()));
        bool Open() => ledger.CommandsOpen && !context.Lifetime.IsCancellationRequested && context.StopReason == PluginStopReason.None;
        void RegisterCommand(string id, Func<PluginCommandResult> action) => context.RegisterCommand(new(id, "Synthetic source fixture control", token =>
        {
            try { return Task.FromResult(!token.IsCancellationRequested && Open() ? action() : Result(false)); }
            catch (Exception) { return Task.FromResult(Result(false)); }
        }));
    }
    private static PluginCommandResult Result(bool succeeded) => new(succeeded,
        succeeded ? "Synthetic source control completed." : "Synthetic source control refused.");
}
