using Workbench.Testing.Domain.OwnedLab;
#if OWNED_LAB_ACCEPTANCE_FIXTURE
namespace Workbench.OwnedLab.FixtureLauncher;
#else
namespace Workbench.Testing.Windows;
#endif

internal sealed record OwnedDriverDescriptor(bool Available, string? ImageSha, string? NativeSourceSha, string? SourceCommit, string? Msvc, string? Sdk, string? Profile)
{
#if OWNED_LAB_REFERENCE_ONLY
    internal static OwnedDriverDescriptor Current { get; } = new(false, null, null, null, null, null, "ReferenceOnly");
#elif !OWNED_LAB_ACCEPTANCE_FIXTURE
    internal static OwnedDriverDescriptor Current => OwnedNativeBuild.Descriptor;
#endif
    internal OwnedHelperIdentity Require(string profile)
    {
        static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (!Available || Profile != profile || !Hex(ImageSha, 64) || !Hex(NativeSourceSha, 64) || !Hex(SourceCommit, 40)
            || string.IsNullOrWhiteSpace(Msvc) || string.IsNullOrWhiteSpace(Sdk)) throw new OwnedLabException("UnavailableHelper");
        return new(ImageSha!, NativeSourceSha!, SourceCommit!, Msvc!, Sdk!, Profile!);
    }
}

internal sealed record OwnedNativeReceipt(uint BrokerPid, ulong BrokerCreationFileTime, uint? TargetPid, ulong? TargetCreationFileTime,
    int? BrokerExitCode, int? TargetExitCode, bool BrokerExited, bool TargetExited, uint ActiveProcesses, bool AllIoJoined, bool CallbacksJoined, bool WasResumed);
