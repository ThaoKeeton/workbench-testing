namespace Workbench.Testing.Domain.Generation.Ai;

public enum AiGenerationError
{
    InvalidRequest,
    CredentialsUnavailable,
    AuthenticationRefused,
    RateLimited,
    Unavailable,
    ResponseIncomplete,
    ResponseRefused,
    ResponseTooLarge,
    ResponseInvalid,
    Timeout
}

/// <summary>Safe classification and bounded retry metadata only; never retains upstream data.</summary>
public sealed class AiGenerationException : Exception
{
    public AiGenerationException(AiGenerationError error, int? retryAfterSeconds = null) : base("AI generation failed.")
    {
        Error = Enum.IsDefined(error) ? error : AiGenerationError.Unavailable;
        RetryAfterSeconds = Error == AiGenerationError.RateLimited && retryAfterSeconds is >= 0 and <= 86400 ? retryAfterSeconds : null;
    }

    public AiGenerationError Error { get; }
    public int? RetryAfterSeconds { get; }
}

// Only failures produced by our protocol checks may select a public error classification.
// Foreign callback/handler/stream exceptions, including public AiGenerationException, are sanitized.
internal sealed class AiFailure(AiGenerationError error, int? retryAfterSeconds = null) : Exception("AI protocol failed.")
{
    internal AiGenerationError Error { get; } = error;
    internal int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
