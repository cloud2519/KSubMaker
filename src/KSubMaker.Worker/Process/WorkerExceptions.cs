using KSubMaker.Domain.Errors;
using KSubMaker.WorkerProtocol;

namespace KSubMaker.Worker.Process;

/// <summary>
/// Base for every failure the worker host raises. Always carries an <see cref="ErrorCodes"/> value so
/// the caller can map it onto a Korean sentence without string matching, and always has a Korean
/// <see cref="Exception.Message"/> in case it does reach a message box.
/// </summary>
public class WorkerException : Exception
{
    public WorkerException(string errorCode, string message) : base(message) => ErrorCode = errorCode;

    public WorkerException(string errorCode, string message, Exception? innerException)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }

    /// <summary>Whether the host may retry the job once by itself.</summary>
    public virtual bool Recoverable => ErrorCodes.IsAutoRetryable(ErrorCode);
}

/// <summary>
/// The worker process died while requests were outstanding. Every pending request is faulted with
/// this so no caller can wait forever on a process that no longer exists.
/// </summary>
public sealed class WorkerCrashedException : WorkerException
{
    public WorkerCrashedException(int exitCode, string? lastStandardError = null, Exception? innerException = null)
        : base(ErrorCodes.WorkerCrashed, BuildMessage(exitCode, lastStandardError), innerException)
    {
        ExitCode = exitCode;
        LastStandardError = lastStandardError;
    }

    public int ExitCode { get; }

    /// <summary>Tail of the worker's stderr, for the log file. Never shown verbatim in the UI.</summary>
    public string? LastStandardError { get; }

    private static string BuildMessage(int exitCode, string? lastStandardError)
    {
        var text = $"AI 작업 프로세스가 예기치 않게 종료되었습니다. (종료 코드 {exitCode})";
        return string.IsNullOrWhiteSpace(lastStandardError) ? text : $"{text} {Tail(lastStandardError)}";
    }

    private static string Tail(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? string.Empty : lines[^1];
    }
}

/// <summary>The worker could not be launched, or never sent <c>ready</c>.</summary>
public sealed class WorkerStartupException(string errorCode, string message, Exception? innerException = null)
    : WorkerException(errorCode, message, innerException);

/// <summary>Protocol version mismatch, or a reply that does not fit the contract.</summary>
public sealed class WorkerProtocolException(string message, Exception? innerException = null)
    : WorkerException(ErrorCodes.ProtocolError, message, innerException);

/// <summary>
/// The watchdog fired: the worker is alive but has produced nothing for the configured idle window.
/// </summary>
public sealed class WorkerTimeoutException(string message)
    : WorkerException(ErrorCodes.WorkerCrashed, message);

/// <summary>A request was answered with an <c>error</c> event carrying the same <c>requestId</c>.</summary>
public sealed class WorkerRequestFailedException : WorkerException
{
    public WorkerRequestFailedException(ErrorEvent errorEvent)
        : base(errorEvent?.Code ?? ErrorCodes.Unknown, BuildMessage(errorEvent))
    {
        ArgumentNullException.ThrowIfNull(errorEvent);
        Detail = errorEvent.Detail;
        ReportedRecoverable = errorEvent.Recoverable;
    }

    public string? Detail { get; }

    private bool ReportedRecoverable { get; }

    public override bool Recoverable => ReportedRecoverable || base.Recoverable;

    private static string BuildMessage(ErrorEvent? errorEvent) =>
        string.IsNullOrWhiteSpace(errorEvent?.Message)
            ? UserFacingErrors.Describe(errorEvent?.Code)
            : errorEvent.Message;
}
