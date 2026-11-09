using Workbench.Sdk;
using Workbench.Testing.Domain.Generation.Ai;

namespace Workbench.Testing.Domain;

public sealed partial class TestingController
{
    public Task<AiGenerationPublication> GenerateWithAiAsync(IReadOnlyList<string> sourceIds,
        Func<CancellationToken,Task<string?>> getKey,CancellationToken cancellationToken=default)
        =>AdmitCaptured(()=>
        {
            if(sourceIds is null) throw DomainErrors.Error("InvalidId");
            var ids=CaptureIds(sourceIds,8);
            var distinct=new HashSet<string>(StringComparer.Ordinal);
            foreach(var id in ids)
                if(id is not { Length:32 } || id.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || !distinct.Add(id))
                    throw DomainErrors.Error("InvalidId");
            if(getKey is null) throw DomainErrors.Error("InvalidTestSource");
            return ids;
        },async ids=>
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_context.Lifetime);
            TestSourceSnapshot? receipt=null;
            try
            {
                await FlushIfSelectedAsync().ConfigureAwait(false);
                var sources=new List<TestSourceSnapshot>();
                foreach(var id in ids) sources.Add(await _testing.ReadSourceAsync(id,linked.Token).ConfigureAwait(false));
                var capture=await _aiGenerator.GenerateAsync(sources,getKey,linked.Token).ConfigureAwait(false);
                var receiptDraft=AiPublicationBuilder.Receipt(capture);
                AdmitAiPublication(linked.Token);
                // The lock ticket admits this retained call. Core Close is the final facade barrier.
                receipt=await _testing.PublishSourceAsync(receiptDraft,linked.Token).ConfigureAwait(false);
                lock(_sync)
                {
                    var summary=new TestSourceSummary(receipt.Id,receipt.ProjectId,receipt.ProducerPluginId,receipt.CreatedUtc,receipt.Revision,
                        receipt.Kind,receipt.ProviderId,receipt.ProviderVersion,receipt.DocumentId,receipt.Title,receipt.OriginalSha256,receipt.Items.Count);
                    _sources=ModelCopy.List(_sources.Where(source=>source.Id!=receipt.Id).Append(summary).OrderBy(source=>source.Id,StringComparer.Ordinal));
                    _error=null;
                }
                Publish();
                var caseDraft=AiPublicationBuilder.CaseSet(capture,receipt);
                AdmitAiPublication(linked.Token); // Independent ticket: receipt acknowledgment grants no second write.
                var saved=await _testing.CreateCaseSetAsync(caseDraft,linked.Token).ConfigureAwait(false);
                // Acknowledgment is authoritative; no cancellation check or fallible reread follows it.
                SelectDraft(saved);
                return new AiGenerationPublication(receipt,ModelCopy.Set(saved),AiGenerationPublicationState.Created,null);
            }
            catch(Exception error)
            {
                var safe=error is AiGenerationException ai ? AiFailure(ai) : error;
                if(receipt is null) throw safe;
                AiGenerationPublicationState state;string? code;
                lock(_sync)
                {
                    if(_closed || ContextClosed || safe is PluginTestingException { ErrorCode:"TestingContextClosed" })
                    { state=AiGenerationPublicationState.Closed;code="TestingContextClosed"; }
                    else if(safe is OperationCanceledException)
                    { state=AiGenerationPublicationState.Cancelled;code=null; }
                    else { state=AiGenerationPublicationState.Failed;code=DomainErrors.Code(safe); }
                    _error=code;
                }
                Publish();return new AiGenerationPublication(receipt,null,state,code);
            }
        });

    private void AdmitAiPublication(CancellationToken cancellationToken)
    {
        lock(_sync)
        {
            if(_closed || ContextClosed) throw DomainErrors.Error("TestingContextClosed");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static PluginTestingException AiFailure(AiGenerationException error)=>DomainErrors.Error(error.Error switch
    {
        AiGenerationError.InvalidRequest or AiGenerationError.ResponseInvalid=>"InvalidTestSource",
        AiGenerationError.ResponseTooLarge=>"StoreLimitExceeded",
        _=>"TestingFailed"
    });
}
