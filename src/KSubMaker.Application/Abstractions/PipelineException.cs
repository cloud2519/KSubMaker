namespace KSubMaker.Application.Abstractions;

/// <summary>
/// A pipeline failure that already knows its <c>KSubMaker.Domain.Errors.ErrorCodes</c> classification.
///
/// Infrastructure components (FFmpeg, model download, …) throw subclasses of this so the processor can
/// surface the precise Korean explanation and the correct auto-retry decision, instead of collapsing
/// everything into <c>UNKNOWN</c> in a generic <c>catch</c>.
/// </summary>
public class PipelineException : Exception
{
    public PipelineException(string errorCode, string message, bool recoverable = false)
        : base(message)
    {
        ErrorCode = errorCode;
        Recoverable = recoverable;
    }

    public PipelineException(string errorCode, string message, Exception innerException, bool recoverable = false)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Recoverable = recoverable;
    }

    /// <summary>One of the <c>ErrorCodes</c> constants.</summary>
    public string ErrorCode { get; }

    /// <summary>Whether the host may retry the job once without user intervention.</summary>
    public bool Recoverable { get; }
}
