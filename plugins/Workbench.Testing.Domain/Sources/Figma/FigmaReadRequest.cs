namespace Workbench.Testing.Domain.Sources.Figma;

/// <summary>Explicit file and basic decimal:decimal node identities; URLs and instance paths are unsupported.</summary>
public sealed record FigmaReadRequest(string FileKey, IReadOnlyList<string> NodeIds, string? Version = null);
