using Workbench.Sdk;

namespace Workbench.Testing.Domain;

public enum AiGenerationPublicationState { Created, Cancelled, Closed, Failed }

public sealed record AiGenerationPublication(TestSourceSnapshot Receipt,TestCaseSetSnapshot? CaseSet,AiGenerationPublicationState State,string? ErrorCode);
