using Workbench.Sdk;

namespace Workbench.Testing.Domain.Sources.Figma;

/// <summary>Combines observed Figma data with explicitly supplied user criteria. Every generated step remains manual.</summary>
public static class FigmaSourceBuilder
{
    public static TestSourceDraft CreateDraft(FigmaCapture capture, IReadOnlyDictionary<string, IReadOnlyList<string>> acceptanceCriteria)
    {
        if (capture is null) throw new FigmaReadException(FigmaReadError.InvalidRequest);
        var criteria = FigmaInputCapture.CaptureCriteria(capture.Request, acceptanceCriteria);
        var documentId = capture.Request.FileKey + "@" + capture.Version;
        if (!FigmaInputCapture.Text(documentId, 200, true)) throw new FigmaReadException(FigmaReadError.InvalidRequest);
        var items = new List<TestSourceItem>();
        foreach (var node in capture.Nodes)
        {
            var description = "Observed type: " + node.Type + (node.Characters is null ? "" : "\nObserved characters: " + node.Characters);
            if (!FigmaInputCapture.Text(description, 4096, false)) throw new FigmaReadException(FigmaReadError.InvalidRequest);
            items.Add(new(node.Id, node.Name, description, criteria[node.Id], null));
        }
        return new(TestSourceKind.Figma, "figma.rest.nodes.manual-criteria", "1", documentId, capture.Name, capture.OriginalJson, items.AsReadOnly());
    }
}
