namespace Workbench.Testing.Domain.Sources.Figma;

public enum FigmaReadError
{
    InvalidRequest,
    CredentialsUnavailable,
    AuthenticationRefused,
    NotFound,
    RateLimited,
    Unavailable,
    ResponseTooLarge,
    ResponseInvalid,
    Timeout
}

/// <summary>Only safe classification and bounded retry metadata; never retains upstream exceptions or bodies.</summary>
public sealed class FigmaReadException : Exception
{
    public FigmaReadException(FigmaReadError error, int? retryAfterSeconds = null) : base("Figma capture failed.")
    {
        Error = Enum.IsDefined(error) ? error : FigmaReadError.Unavailable;
        RetryAfterSeconds = Error == FigmaReadError.RateLimited && retryAfterSeconds is >= 0 and <= 86400 ? retryAfterSeconds : null;
    }

    public FigmaReadError Error { get; }
    public int? RetryAfterSeconds { get; }
}
