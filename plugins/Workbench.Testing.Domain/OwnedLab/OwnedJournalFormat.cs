using System.Globalization;
using System.Text.Json;
using Workbench.Sdk;

namespace Workbench.Testing.Domain.OwnedLab;

// Schema and transition checks are shared by accepted writes and fresh-session prefix verification.
internal static class OwnedJournalFormat
{
    internal const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";
    internal static byte[] ExecutionBytes(OwnedExecution e) => OwnedJson.Write(w =>
    {
        w.WriteStartObject(); w.WriteNumber("schema", e.Schema); w.WriteString("runId", e.RunId); w.WriteString("sessionId", e.SessionId);
        w.WriteString("projectId", e.ProjectId); w.WriteString("pluginId", e.PluginId); w.WriteString("parentRunId", e.ParentRunId);
        w.WriteString("parentInputSha", e.ParentInputSha); w.WriteString("inputSha", e.InputSha); w.WriteString("targetId", e.TargetId); w.WriteNumber("protocol", e.Protocol);
        w.WriteStartObject("helper"); w.WriteString("imageSha", e.Helper.ImageSha); w.WriteString("nativeSourceSha", e.Helper.NativeSourceSha);
        w.WriteString("sourceCommit", e.Helper.SourceCommit); w.WriteString("msvc", e.Helper.Msvc); w.WriteString("sdk", e.Helper.Sdk); w.WriteString("profile", e.Helper.Profile); w.WriteEndObject();
        w.WriteString("issuedUtc", Utc(e.IssuedUtc)); w.WriteString("expiresUtc", Utc(e.ExpiresUtc)); w.WriteEndObject();
    });

    internal static OwnedExecution ReadExecution(byte[] bytes, OwnedScope scope, OwnedInputPlan input, string runId)
    {
        using var document = OwnedJson.Parse(bytes); var e = document.RootElement;
        OwnedJson.Fields(e, "schema", "runId", "sessionId", "projectId", "pluginId", "parentRunId", "parentInputSha", "inputSha", "targetId", "protocol", "helper", "issuedUtc", "expiresUtc");
        var h = e.GetProperty("helper"); OwnedJson.Fields(h, "imageSha", "nativeSourceSha", "sourceCommit", "msvc", "sdk", "profile");
        var result = new OwnedExecution(e.GetProperty("schema").GetInt32(), T(e, "runId"), T(e, "sessionId"), T(e, "projectId"), T(e, "pluginId"), N(e, "parentRunId"), N(e, "parentInputSha"), T(e, "inputSha"), T(e, "targetId"), e.GetProperty("protocol").GetUInt16(),
            new(T(h, "imageSha"), T(h, "nativeSourceSha"), T(h, "sourceCommit"), T(h, "msvc"), T(h, "sdk"), T(h, "profile")), Date(e, "issuedUtc"), Date(e, "expiresUtc"));
        ValidateExecution(result, scope, input);
        if (result.RunId != runId) Invalid(); return result;
    }

    internal static void ValidateHelper(OwnedHelperIdentity h)
    {
        if (!OwnedJson.Hex(h.ImageSha, 64) || !OwnedJson.Hex(h.NativeSourceSha, 64) || !OwnedJson.Hex(h.SourceCommit, 40) || h.Profile is not ("Production" or "AcceptanceFixture")) Invalid();
        Bounded(h.Msvc, 128); Bounded(h.Sdk, 128);
    }
    internal static void ValidateExecution(OwnedExecution e, OwnedScope scope, OwnedInputPlan input)
    {
        ValidateHelper(e.Helper); Bounded(e.ProjectId, 32); Bounded(e.PluginId, 128);
        if (e.Schema != 1 || !OwnedJson.Hex(e.RunId, 32) || !OwnedJson.Hex(e.SessionId, 32) || e.ProjectId != scope.ProjectId || e.PluginId != scope.PluginId || e.InputSha != input.Sha256 || e.TargetId != FixedOwnedPlans.TargetId || e.Protocol != 1 || e.IssuedUtc.Offset != TimeSpan.Zero || e.ExpiresUtc.Offset != TimeSpan.Zero || e.ExpiresUtc <= e.IssuedUtc || e.ExpiresUtc - e.IssuedUtc > TimeSpan.FromSeconds(60)) Invalid();
        if ((e.ParentRunId is null) != (e.ParentInputSha is null) || e.ParentRunId is not null && (!OwnedJson.Hex(e.ParentRunId, 32) || !OwnedJson.Hex(e.ParentInputSha, 64) || e.ParentRunId == e.RunId || e.ParentInputSha != input.Sha256)) Invalid();
    }

    internal static byte[] EventBytes(OwnedEvent e) => OwnedJson.Write(w =>
    {
        w.WriteStartObject(); w.WriteNumber("schema", e.Schema); w.WriteNumber("sequence", e.Sequence); w.WriteString("priorHash", e.PriorHash); w.WriteString("executionSha", e.ExecutionSha);
        w.WriteString("utc", Utc(e.Utc)); w.WriteNumber("kind", (int)e.Kind); Number(w, "stepIndex", e.StepIndex); w.WriteString("outcome", e.Outcome?.ToString()); w.WriteString("errorCode", e.ErrorCode);
        w.WritePropertyName("identity");
        if (e.Identity is not { } id) w.WriteNullValue();
        else
        {
            w.WriteStartObject(); w.WriteNumber("brokerPid", id.BrokerPid); w.WriteNumber("brokerCreationFileTime", id.BrokerCreationFileTime); w.WriteNumber("targetPid", id.TargetPid);
            w.WriteNumber("targetCreationFileTime", id.TargetCreationFileTime); w.WriteNumber("hwnd", id.Hwnd); w.WriteString("imageSha", id.ImageSha); w.WriteEndObject();
        }
        w.WritePropertyName("state");
        if (e.State is not { } s) w.WriteNullValue();
        else { w.WriteStartObject(); w.WriteString("title", s.Title); w.WriteString("toast", s.Toast); w.WriteString("validation", s.Validation); w.WriteString("createdCount", s.CreatedCount); w.WriteEndObject(); }
        w.WriteString("frameSha", e.FrameSha); w.WriteString("captureKind", e.CaptureKind); Bool(w, "syntheticTarget", e.SyntheticTarget);
        w.WritePropertyName("retirement");
        if (e.Retirement is not { } r) w.WriteNullValue();
        else { w.WriteStartObject(); w.WriteBoolean("wasLaunched", r.WasLaunched); Number(w, "brokerExitCode", r.BrokerExitCode); Number(w, "targetExitCode", r.TargetExitCode); w.WriteNumber("activeProcesses", r.ActiveProcesses); w.WriteEndObject(); }
        Bool(w, "uncertainStep", e.UncertainStep); w.WriteEndObject();
    });

    internal static OwnedEvent ReadEvent(byte[] bytes)
    {
        using var document = OwnedJson.Parse(bytes); var e = document.RootElement;
        OwnedJson.Fields(e, "schema", "sequence", "priorHash", "executionSha", "utc", "kind", "stepIndex", "outcome", "errorCode", "identity", "state", "frameSha", "captureKind", "syntheticTarget", "retirement", "uncertainStep");
        OwnedIdentity? identity = null; OwnedState? state = null; OwnedRetirement? retirement = null;
        var id = e.GetProperty("identity");
        if (id.ValueKind != JsonValueKind.Null)
        {
            OwnedJson.Fields(id, "brokerPid", "brokerCreationFileTime", "targetPid", "targetCreationFileTime", "hwnd", "imageSha");
            identity = new(id.GetProperty("brokerPid").GetUInt32(), id.GetProperty("brokerCreationFileTime").GetUInt64(), id.GetProperty("targetPid").GetUInt32(), id.GetProperty("targetCreationFileTime").GetUInt64(), id.GetProperty("hwnd").GetUInt64(), T(id, "imageSha"));
        }
        var s = e.GetProperty("state");
        if (s.ValueKind != JsonValueKind.Null) { OwnedJson.Fields(s, "title", "toast", "validation", "createdCount"); state = new(T(s, "title"), T(s, "toast"), T(s, "validation"), T(s, "createdCount")); }
        var r = e.GetProperty("retirement");
        if (r.ValueKind != JsonValueKind.Null) { OwnedJson.Fields(r, "wasLaunched", "brokerExitCode", "targetExitCode", "activeProcesses"); retirement = new(r.GetProperty("wasLaunched").GetBoolean(), Int(r, "brokerExitCode"), Int(r, "targetExitCode"), r.GetProperty("activeProcesses").GetUInt32()); }
        var outcome = N(e, "outcome");
        OwnedOutcome? parsed = outcome switch { null => null, "Passed" => OwnedOutcome.Passed, "Failed" => OwnedOutcome.Failed, "Cancelled" => OwnedOutcome.Cancelled, _ => throw new JsonException() };
        return new(e.GetProperty("schema").GetInt32(), e.GetProperty("sequence").GetInt32(), T(e, "priorHash"), T(e, "executionSha"), Date(e, "utc"), (OwnedEventKind)e.GetProperty("kind").GetInt32(), Int(e, "stepIndex"), parsed, N(e, "errorCode"), identity, state, N(e, "frameSha"), N(e, "captureKind"), Boolean(e, "syntheticTarget"), retirement, Boolean(e, "uncertainStep"));
    }

    internal static byte[] CommitBytes(OwnedEvent e, string eventSha) => OwnedJson.Write(w =>
    { w.WriteStartObject(); w.WriteNumber("schema", 1); w.WriteNumber("sequence", e.Sequence); w.WriteString("eventSha", eventSha); w.WriteString("priorHash", e.PriorHash); w.WriteString("executionSha", e.ExecutionSha); w.WriteEndObject(); });

    internal static void VerifyCommit(byte[] bytes, OwnedEvent e, string eventSha)
    {
        using var document = OwnedJson.Parse(bytes); var c = document.RootElement; OwnedJson.Fields(c, "schema", "sequence", "eventSha", "priorHash", "executionSha");
        if (c.GetProperty("schema").GetInt32() != 1 || c.GetProperty("sequence").GetInt32() != e.Sequence || T(c, "eventSha") != eventSha || T(c, "priorHash") != e.PriorHash || T(c, "executionSha") != e.ExecutionSha) Invalid();
    }

    internal static void ValidateNext(OwnedRunSnapshot run, OwnedEvent e)
    {
        if (e.Schema != 1 || e.Sequence != run.Sequence + 1 || e.PriorHash != run.HeadSha || e.ExecutionSha != run.ExecutionSha || e.Utc.Offset != TimeSpan.Zero || !Enum.IsDefined(e.Kind) || e.Outcome is { } outcome && !Enum.IsDefined(outcome) || run.Events.LastOrDefault()?.Kind == OwnedEventKind.Terminal) Invalid();
        if (e.ErrorCode is not null && new OwnedLabException(e.ErrorCode).ErrorCode != e.ErrorCode) Invalid();
        if (e.Identity is not null) { OwnedWireCodec.ValidateIdentity(e.Identity); if (e.Identity.ImageSha != run.Execution.Helper.ImageSha) Invalid(); }
        if (e.State is not null) OwnedWireCodec.ValidateState(e.State);
        var previous = run.Events.LastOrDefault();
        switch (e.Kind)
        {
            case OwnedEventKind.Queued:
                if (e.Sequence != 0 || !Unused(e, true, true, true, true, true, true, true)) Invalid(); break;
            case OwnedEventKind.Started:
                if (previous?.Kind != OwnedEventKind.Queued || e.Identity is null || e.State != new OwnedState("", "", "", "0") || !Unused(e, true, true, true, false, true, true, true)) Invalid(); break;
            case OwnedEventKind.StepStarted:
                if (previous?.Kind is not (OwnedEventKind.Started or OwnedEventKind.StepResult) || previous.Kind == OwnedEventKind.StepResult && previous.Outcome != OwnedOutcome.Passed || e.StepIndex != run.Events.Count(x => x.Kind == OwnedEventKind.StepResult) || e.StepIndex is < 0 or > 3 || !Unused(e, false, true, true, true, true, true, true)) Invalid(); break;
            case OwnedEventKind.StepResult:
                if (previous?.Kind != OwnedEventKind.StepStarted || previous.StepIndex != e.StepIndex || e.Identity != run.Events.First(x => x.Kind == OwnedEventKind.Started).Identity || e.State is null || !OwnedJson.Hex(e.FrameSha, 64) || e.CaptureKind != "OwnedWindowPrintWindow" || e.SyntheticTarget != true || e.Retirement is not null || e.UncertainStep is not null || e.Outcome is not (OwnedOutcome.Passed or OwnedOutcome.Failed)) Invalid();
                var action = run.Input.Steps[e.StepIndex!.Value].Action; var passed = AssertionPasses(action, e.State!);
                if (e.Outcome != (passed ? OwnedOutcome.Passed : OwnedOutcome.Failed) || e.ErrorCode != (passed ? null : "AssertionFailed")) Invalid(); break;
            case OwnedEventKind.Terminal:
                if (previous is null || e.StepIndex is not null || e.Identity is not null || e.State is not null || e.FrameSha is not null || e.CaptureKind is not null || e.SyntheticTarget is not null || e.Retirement is not { ActiveProcesses: 0 } || e.UncertainStep != (previous.Kind == OwnedEventKind.StepStarted) || e.Outcome is null) Invalid();
                var retired = e.Retirement!;
                if (retired.WasLaunched ? retired.BrokerExitCode is null : retired.BrokerExitCode is not null || retired.TargetExitCode is not null) Invalid();
                if (run.Events.Any(x => x.Kind == OwnedEventKind.Started) && (!retired.WasLaunched || retired.TargetExitCode is null)) Invalid();
                if (e.Outcome == OwnedOutcome.Passed && (e.ErrorCode is not null || run.Events.Count(x => x.Kind == OwnedEventKind.StepResult && x.Outcome == OwnedOutcome.Passed) != 4 || retired is not { WasLaunched: true, BrokerExitCode: 0, TargetExitCode: 0 } || e.UncertainStep != false)) Invalid();
                if (e.Outcome == OwnedOutcome.Cancelled && e.ErrorCode is not ("Cancelled" or "AdmissionClosed")) Invalid();
                if (e.Outcome == OwnedOutcome.Failed && e.ErrorCode is null) Invalid(); break;
            default: Invalid(); break;
        }
    }

    internal static bool AssertionPasses(TestAction action, OwnedState state) => action.Kind != TestActionKind.AssertText || string.Equals(action.Value, action.ElementId switch { "task.toast" => state.Toast, "task.validation" => state.Validation, "task.createdCount" => state.CreatedCount, _ => throw new OwnedLabException("InvalidPlan") }, StringComparison.Ordinal);
    internal static OwnedRunState Project(IReadOnlyList<OwnedEvent> events, bool restored)
    {
        var last = events.LastOrDefault();
        return last?.Kind == OwnedEventKind.Terminal ? last.Outcome switch { OwnedOutcome.Passed => OwnedRunState.Passed, OwnedOutcome.Cancelled => OwnedRunState.Cancelled, _ => OwnedRunState.Failed }
            : restored ? OwnedRunState.Interrupted : last?.Kind == OwnedEventKind.Queued || last is null ? OwnedRunState.Queued : OwnedRunState.Running;
    }
    private static bool Unused(OwnedEvent e, bool step, bool outcome, bool error, bool observation, bool frame, bool retirement, bool uncertain) => (!step || e.StepIndex is null) && (!outcome || e.Outcome is null) && (!error || e.ErrorCode is null) && (!observation || e.Identity is null && e.State is null) && (!frame || e.FrameSha is null && e.CaptureKind is null && e.SyntheticTarget is null) && (!retirement || e.Retirement is null) && (!uncertain || e.UncertainStep is null);
    private static void Bounded(string value, int max) { OwnedJson.ValidateUnicode(value); if (value.Length == 0 || value.Contains('\0') || OwnedJson.Utf8.GetByteCount(value) > max) Invalid(); }
    private static string T(JsonElement e, string name) => OwnedJson.Text(e, name);
    private static string? N(JsonElement e, string name) => OwnedJson.NullableText(e, name);
    private static int? Int(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetInt32();
    private static bool? Boolean(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetBoolean();
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Date(JsonElement e, string name)
    {
        var text = T(e, name); if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date.Offset != TimeSpan.Zero || Utc(date) != text) throw new JsonException(); return date;
    }
    private static void Number(Utf8JsonWriter writer, string name, int? value) { if (value is { } number) writer.WriteNumber(name, number); else writer.WriteNull(name); }
    private static void Bool(Utf8JsonWriter writer, string name, bool? value) { if (value is { } boolean) writer.WriteBoolean(name, boolean); else writer.WriteNull(name); }
    private static void Invalid() => throw new OwnedLabException("InvalidPlan");
}
