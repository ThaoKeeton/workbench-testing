using Workbench.Sdk;
namespace Workbench.Testing.Domain;

internal static class DomainErrors
{
    internal static string Code(Exception error) => error is PluginTestingException known ? Safe(known.ErrorCode) : error is IOException or UnauthorizedAccessException ? "StoreIoFailure" : "TestingFailed";
    internal static string Safe(string? code) => code is "TestingContextClosed" or "InvalidTestSource" or "InvalidCaseSet" or "InvalidRun" or "InvalidEvidence" or "RevisionConflict" or "StoreLimitExceeded" or "StoreIoFailure" or "UnsafePath" or "TestStoreCorrupt" or "RecoveryRequired" or "ManualStepUnsupported" or "TargetUnavailable" or "AssertionFailed" or "AdapterFailed" or "TestRecordNotFound" or "StoreBusy" or "InvalidId" or "InvalidProducer" ? code : "TestingFailed";
    internal static PluginTestingException Error(string code) => new(Safe(code));
    internal static bool IsTerminal(TestRunState state) => state is TestRunState.Completed or TestRunState.Failed or TestRunState.Cancelled or TestRunState.Interrupted;
    internal static bool ValidUnicode(string value)
    {
        for (var i=0;i<value.Length;i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i==value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
}
