namespace Workbench.Testing.Domain.Sources.Figma;

public sealed record FigmaNodeCapture(string Id, string Name, string Type, string? Characters);

/// <summary>Immutable observed data, in request order. Credentials and inferred criteria have no place in this shape.</summary>
public sealed class FigmaCapture
{
    private readonly byte[] _originalJson;

    internal FigmaCapture(FigmaReadRequest capturedRequest, byte[] originalJson, string name, string version, IEnumerable<FigmaNodeCapture> nodes)
    {
        Request = FigmaInputCapture.CaptureRequest(capturedRequest);
        _originalJson = originalJson.ToArray();
        Name = name;
        Version = version;
        Nodes = Array.AsReadOnly(nodes.ToArray());
    }

    public FigmaReadRequest Request { get; }
    public byte[] OriginalJson => _originalJson.ToArray();
    public string Name { get; }
    public string Version { get; }
    public IReadOnlyList<FigmaNodeCapture> Nodes { get; }
}
