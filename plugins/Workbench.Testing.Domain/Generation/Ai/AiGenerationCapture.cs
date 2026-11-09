using System.Security.Cryptography;
using Workbench.Sdk;

namespace Workbench.Testing.Domain.Generation.Ai;

public sealed class AiGeneratedStep
{
    internal AiGeneratedStep(string instruction, string expected) { Instruction = instruction; Expected = expected; }
    public string Instruction { get; }
    public string Expected { get; }
    public TestAction? Action => null;
}

public sealed class AiGeneratedCase
{
    internal AiGeneratedCase(int criterionIndex, string title, string prerequisites, IEnumerable<AiGeneratedStep> steps)
    {
        CriterionIndex = criterionIndex;
        Title = title;
        Prerequisites = prerequisites;
        Steps = Array.AsReadOnly(steps.ToArray());
    }
    public int CriterionIndex { get; }
    public string Title { get; }
    public string Prerequisites { get; }
    public IReadOnlyList<AiGeneratedStep> Steps { get; }
}

/// <summary>Immutable input, response and generated plain values, with no credential field.</summary>
public sealed class AiGenerationCapture
{
    private readonly byte[] _inputJson;
    private readonly byte[] _responseJson;

    internal AiGenerationCapture(byte[] inputJson, IEnumerable<TestTrace> originalTraces, byte[] responseJson,
        string model, string responseId, IEnumerable<AiGeneratedCase> cases)
    {
        _inputJson = inputJson.ToArray();
        _responseJson = responseJson.ToArray();
        InputSha256 = Convert.ToHexStringLower(SHA256.HashData(_inputJson));
        Model = model;
        ResponseId = responseId;
        OriginalTraces = Array.AsReadOnly(originalTraces.ToArray());
        Cases = Array.AsReadOnly(cases.ToArray());
    }

    public byte[] InputJson => _inputJson.ToArray();
    public string InputSha256 { get; }
    public byte[] ResponseJson => _responseJson.ToArray();
    public string Model { get; }
    public string ResponseId { get; }
    /// <summary>Global criterion index maps to this ordered original snapshot/revision/locator/local criterion trace.</summary>
    public IReadOnlyList<TestTrace> OriginalTraces { get; }
    public IReadOnlyList<AiGeneratedCase> Cases { get; }
}
