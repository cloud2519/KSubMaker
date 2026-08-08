using System.Diagnostics;
using System.Text;

namespace KSubMaker.Infrastructure.Media;

/// <summary>Outcome of an external process run.</summary>
public sealed record ProcessResult
{
    public required int ExitCode { get; init; }
    public required string StandardOutput { get; init; }
    public required string StandardError { get; init; }

    /// <summary>True when the process was killed because it exceeded its time budget.</summary>
    public bool TimedOut { get; init; }

    public bool Success => ExitCode == 0 && !TimedOut;

    /// <summary>Last few stderr lines, which is where FFmpeg puts the actual reason it failed.</summary>
    public string Tail(int lines = 6)
    {
        if (string.IsNullOrWhiteSpace(StandardError))
        {
            return string.Empty;
        }

        var all = StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", all.TakeLast(lines).Select(l => l.Trim()));
    }
}

/// <summary>
/// Runs ffmpeg / ffprobe / nvidia-smi.
///
/// Three rules are non-negotiable here and are the reason this helper exists at all:
/// arguments are always passed through <see cref="ProcessStartInfo.ArgumentList"/> (a file name
/// containing a quote or a space must never be able to change the command line), the exit code is
/// always inspected, and a cancelled or timed-out process is killed together with its children —
/// FFmpeg spawns helpers and an orphaned one keeps a file handle on the output forever.
/// </summary>
public static class ProcessRunner
{
    /// <summary>How long to wait for the redirected pipes to reach EOF after the process exits.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<string>? onStandardErrorLine = null,
        Action<string>? onStandardOutputLine = null,
        int maxCapturedChars = 256 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            Append(stdout, e.Data, maxCapturedChars);
            onStandardOutputLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            Append(stderr, e.Data, maxCapturedChars);
            onStandardErrorLine?.Invoke(e.Data);
        };

        // A linked source lets one CancelAfter cover the timeout while still honouring the caller's
        // token, and keeps the two reasons distinguishable afterwards.
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;

            KillTree(process);

            // Give the pipes a moment so the caller still gets the stderr that explains the failure.
            await WaitForDrainAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);

            if (!timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = Snapshot(stdout),
                StandardError = Snapshot(stderr),
                TimedOut = true
            };
        }

        await WaitForDrainAsync(stdoutClosed.Task, stderrClosed.Task).ConfigureAwait(false);

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = Snapshot(stdout),
            StandardError = Snapshot(stderr),
            TimedOut = timedOut
        };
    }

    /// <summary>
    /// Kills the process and every child it started. Swallowing the failure here is deliberate: the
    /// process may have exited between the cancellation and this call, and there is nothing useful
    /// the caller could do about a failed kill anyway.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited / never started.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied on an already-dying process.
        }
        catch (NotSupportedException)
        {
            // Remote process; cannot happen for a child we started.
        }
    }

    private static async Task WaitForDrainAsync(Task stdout, Task stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A wedged pipe must not hold the pipeline hostage; whatever was captured is enough.
        }
    }

    /// <summary>
    /// Caps captured output. FFmpeg on a broken file can emit tens of megabytes of repeated warnings,
    /// and none of it is worth holding in memory.
    /// </summary>
    private static void Append(StringBuilder builder, string line, int maxChars)
    {
        lock (builder)
        {
            if (builder.Length >= maxChars)
            {
                return;
            }

            builder.Append(line).Append('\n');
        }
    }

    /// <summary>Reads the buffer under the same lock the data-received callbacks use.</summary>
    private static string Snapshot(StringBuilder builder)
    {
        lock (builder)
        {
            return builder.ToString();
        }
    }
}
