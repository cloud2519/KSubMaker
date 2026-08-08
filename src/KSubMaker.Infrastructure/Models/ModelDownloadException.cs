namespace KSubMaker.Infrastructure.Models;

/// <summary>
/// A model download or verification failed in a way the UI can explain.
///
/// <see cref="ErrorCode"/> is one of <c>KSubMaker.Domain.Errors.ErrorCodes</c>
/// (<c>MODEL_DOWNLOAD_FAILED</c> / <c>MODEL_VERIFICATION_FAILED</c>).
/// </summary>
public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public ModelDownloadException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
