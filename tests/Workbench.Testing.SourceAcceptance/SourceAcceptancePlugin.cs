using Workbench.Sdk;

namespace Workbench.Testing.SourceAcceptance;

/// <summary>Independent synthetic SDK entry; every view/action belongs to the same production Testing instance.</summary>
public sealed class SourceAcceptancePlugin : IWorkbenchPlugin
{
    private SourceAcceptanceOwnership? _ownership;
    private CancellationTokenRegistration _lifetime;
    private bool _initialized, _stopped;

    public SourceAcceptancePlugin() { }

    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        if (_initialized || _stopped || context is not IPluginTestingContext testing)
            throw new ArgumentException("An unused synthetic source entry and testing context are required.");
        var project = SourceAcceptanceProtocol.ValidateContext(testing);
        _initialized = true;
        var ledger = new SourceAcceptanceScenarioLedger(project);
        SourceAcceptanceFigmaHandler? figma = null; SourceAcceptanceAiHandler? ai = null;
        try
        {
            figma = new(ledger); ai = new(ledger);
            var inner = new TestingPlugin(new(new(figma), new(ai)));
            _ownership = new(ledger, figma, ai, inner.StopAsync);
            _lifetime = testing.Lifetime.Register(ledger.SealCommands);
            await SourceAcceptanceReleaseBridge.InitializeAsync(testing, ledger, cancellationToken).ConfigureAwait(false);
            await inner.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
            SourceAcceptanceProtocol.Register(testing, ledger, () => inner.Lifecycle.ReadAdmission().ActivityStarts);
        }
        catch (Exception)
        {
            if (_ownership is not null)
            {
                try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the actual initialization failure after joined cleanup. */ }
            }
            else
            {
                try { figma?.Dispose(); } catch (Exception) { }
                try { ai?.Dispose(); } catch (Exception) { }
            }
            throw;
        }
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        try { if (_ownership is not null) await _ownership.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifetime.Dispose(); }
    }
}
