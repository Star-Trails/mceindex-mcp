namespace MceIndex.Mcp;

public enum MceIndexErrorCode
{
    BrowserNotFound,
    AccessChallenge,
    LoadTimeout,
    PageNotFound,
    IndicatorNotFound,
    IndexEmpty,
    InvalidConfiguration,
    ExtractionFailed,
    DatabaseError,
}

public sealed class MceIndexException : Exception
{
    public MceIndexException(
        MceIndexErrorCode code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Details = details;
    }

    public MceIndexErrorCode Code { get; }
    public IReadOnlyDictionary<string, object?>? Details { get; }
}

internal static class MceIndexErrorCodes
{
    public static string ToProtocolCode(MceIndexErrorCode code) => code switch
    {
        MceIndexErrorCode.BrowserNotFound => "BROWSER_NOT_FOUND",
        MceIndexErrorCode.AccessChallenge => "ACCESS_CHALLENGE",
        MceIndexErrorCode.LoadTimeout => "LOAD_TIMEOUT",
        MceIndexErrorCode.PageNotFound => "PAGE_NOT_FOUND",
        MceIndexErrorCode.IndicatorNotFound => "INDICATOR_NOT_FOUND",
        MceIndexErrorCode.IndexEmpty => "INDEX_EMPTY",
        MceIndexErrorCode.InvalidConfiguration => "INVALID_CONFIGURATION",
        MceIndexErrorCode.ExtractionFailed => "EXTRACTION_FAILED",
        MceIndexErrorCode.DatabaseError => "DATABASE_ERROR",
        _ => "INTERNAL_ERROR",
    };
}
