using Workbench.Sdk;
using Workbench.Testing.Domain.Sources.Figma;

namespace Workbench.Testing.Domain;

public sealed partial class TestingController
{
    public Task<TestSourceSnapshot> ImportFigmaAsync(FigmaReadRequest request,
        IReadOnlyDictionary<string,IReadOnlyList<string>> acceptanceCriteria,
        Func<CancellationToken,Task<string?>> getToken,CancellationToken cancellationToken=default)
        =>AdmitCaptured(()=>
        {
            try
            {
                var captured=FigmaInputCapture.CaptureRequest(request);
                return (Request:captured,Criteria:FigmaInputCapture.CaptureCriteria(captured,acceptanceCriteria));
            }
            // AdmitCaptured sanitizes non-SDK exceptions, so classify Figma input errors inside its capture delegate.
            catch(FigmaReadException error) { throw FigmaFailure(error); }
        },async input=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_context.Lifetime);
            try
            {
                var capture=await _figmaReader.ReadAsync(input.Request,getToken,linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                var draft=FigmaSourceBuilder.CreateDraft(capture,input.Criteria);
                lock(_sync)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if(_closed || ContextClosed) throw DomainErrors.Error("TestingContextClosed");
                    // This publication ticket belongs to the already-retained operation. Stop closes the same
                    // barrier and joins that operation; a ticket admitted first may settle after standalone Stop.
                }
                // Core Close remains the final host-context barrier. No service/caller prefix runs under _sync.
                var saved=await _testing.PublishSourceAsync(draft,linked.Token).ConfigureAwait(false);
                lock(_sync)
                {
                    var summary=new TestSourceSummary(saved.Id,saved.ProjectId,saved.ProducerPluginId,saved.CreatedUtc,saved.Revision,
                        saved.Kind,saved.ProviderId,saved.ProviderVersion,saved.DocumentId,saved.Title,saved.OriginalSha256,saved.Items.Count);
                    _sources=ModelCopy.List(_sources.Where(source=>source.Id!=saved.Id).Append(summary).OrderBy(source=>source.Id,StringComparer.Ordinal));
                    _error=null;
                }
                // Acknowledgment is authoritative: no cancellation check or fallible reread follows a committed result.
                Publish();return saved;
            }
            catch(FigmaReadException error) { throw FigmaFailure(error); }
        });

    private static PluginTestingException FigmaFailure(FigmaReadException error)=>DomainErrors.Error(error.Error switch
    {
        FigmaReadError.InvalidRequest or FigmaReadError.ResponseInvalid=>"InvalidTestSource",
        FigmaReadError.ResponseTooLarge=>"StoreLimitExceeded",
        _=>"TestingFailed"
    });
}
