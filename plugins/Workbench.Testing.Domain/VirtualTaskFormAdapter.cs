using System.Globalization;
using Workbench.Sdk;
namespace Workbench.Testing.Domain;

public sealed record VirtualTaskFormState(string Title,string Toast,string Validation,int CreatedCount);

/// <summary>Only an in-memory task form. No process, network, selector interpreter or OS input.</summary>
public sealed class VirtualTaskFormAdapter : IControlledTestAdapter
{
    public const string VirtualTargetId="virtual.task-form.v1";
    public string TargetId=>VirtualTargetId;
    public Task<IControlledTestSession> OpenAsync(string sessionId,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();ValidateId(sessionId);
        return Task.FromResult<IControlledTestSession>(new Session());
    }
    private static void ValidateId(string id)
    { if(id is not { Length:32 } || id.Any(c=>c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw DomainErrors.Error("InvalidId"); }
    private sealed class Session : IControlledTestSession
    {
        private VirtualTaskFormState _state=new("","","",0);
        private bool _disposed,_reset;
        public Task ResetCaseAsync(string caseId,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();EnsureOpen();ValidateId(caseId);
            _state=new("","","",0);_reset=true;return Task.CompletedTask;
        }
        public Task<TestActionResult> ExecuteAsync(TestAction action,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();EnsureOpen();
            if(!_reset) throw DomainErrors.Error("AdapterFailed");
            var passed=true;
            switch(action)
            {
                case { Kind:TestActionKind.SetText,ElementId:"task.title",Value:not null }:
                    if(!DomainErrors.ValidUnicode(action.Value)) throw DomainErrors.Error("AdapterFailed");
                    if(action.Value.Length>4096) throw DomainErrors.Error("StoreLimitExceeded");
                    _state=_state with { Title=action.Value,Toast="",Validation="" };break;
                case { Kind:TestActionKind.Click,ElementId:"task.save",Value:null }:
                    _state=string.IsNullOrWhiteSpace(_state.Title) ? _state with { Toast="",Validation="Title required" }
                        : _state with { Toast="Saved",Validation="",CreatedCount=_state.CreatedCount+1 };break;
                case { Kind:TestActionKind.AssertText,Value:not null }:
                    var actual=action.ElementId switch { "task.toast"=>_state.Toast,"task.validation"=>_state.Validation,"task.createdCount"=>_state.CreatedCount.ToString(CultureInfo.InvariantCulture),_=>throw DomainErrors.Error("TargetUnavailable") };
                    passed=string.Equals(actual,action.Value,StringComparison.Ordinal);break;
                default:throw DomainErrors.Error("TargetUnavailable");
            }
            var text=$"SIMULATED {VirtualTargetId}\r\nTitle: {_state.Title}\r\nToast: {_state.Toast}\r\nValidation: {_state.Validation}\r\nCreated count: {_state.CreatedCount.ToString(CultureInfo.InvariantCulture)}";
            return Task.FromResult(new TestActionResult(passed,passed ? null : "AssertionFailed",text,SimulatedFrameRenderer.Render(_state)));
        }
        private void EnsureOpen() { if(_disposed) throw DomainErrors.Error("AdapterFailed"); }
        public ValueTask DisposeAsync() { _disposed=true;_state=new("","","",0);return ValueTask.CompletedTask; }
    }
}
