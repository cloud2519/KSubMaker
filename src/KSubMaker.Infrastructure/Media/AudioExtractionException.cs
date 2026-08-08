using KSubMaker.Application.Abstractions;

namespace KSubMaker.Infrastructure.Media;

/// <summary>
/// Audio extraction failed in a way the pipeline can classify.
///
/// The <see cref="PipelineException.ErrorCode"/> is one of <c>KSubMaker.Domain.Errors.ErrorCodes</c> so
/// the host maps it to a Korean explanation and to the auto-retry policy without having to pattern
/// match on FFmpeg's English stderr text. It derives from <see cref="PipelineException"/> so the
/// processor's catch chain preserves that classification instead of reporting <c>UNKNOWN</c>.
/// </summary>
public sealed class AudioExtractionException : PipelineException
{
    public AudioExtractionException(string errorCode, string message)
        : base(errorCode, message)
    {
    }

    public AudioExtractionException(string errorCode, string message, Exception innerException)
        : base(errorCode, message, innerException)
    {
    }
}
