using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KSubMaker.App.Resources;
using KSubMaker.App.Services;
using KSubMaker.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.ViewModels;

/// <summary>
/// Tails the newest file in <see cref="IAppPaths.LogsDirectory"/>.
///
/// Two details make this safe against the live Serilog sink: the file is opened with
/// <see cref="FileShare.ReadWrite"/> (Serilog holds an exclusive-ish write handle, so anything less
/// throws), and only the tail of a large file is read, so a 20 MB log does not allocate 20 MB of
/// string on every poll.
/// </summary>
public sealed partial class LogViewModel : ObservableObject, IDisposable
{
    /// <summary>Lines kept in the text box. More than this and the control itself becomes the bottleneck.</summary>
    private const int TailLineCount = 500;

    /// <summary>Only the last chunk of a big file is read; 512 KB comfortably holds 500 long lines.</summary>
    private const long TailByteWindow = 512L * 1024L;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IAppPaths _paths;
    private readonly IShellService _shell;
    private readonly ILogger<LogViewModel> _logger;

    private CancellationTokenSource? _pollCts;
    private Task _pollTask = Task.CompletedTask;
    private bool _disposed;

    public LogViewModel(IAppPaths paths, IShellService shell, ILogger<LogViewModel> logger)
    {
        _paths = paths;
        _shell = shell;
        _logger = logger;
    }

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string _currentFile = Strings.Dash;

    [ObservableProperty]
    private bool _autoRefresh = true;

    [ObservableProperty]
    private string _statusMessage = string.Format(
        CultureInfo.CurrentCulture, Strings.LogTailHintFormat, TailLineCount);

    /// <summary>Starts the two-second poll loop. Safe to call more than once.</summary>
    public void Start()
    {
        if (_disposed || !_pollTask.IsCompleted)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pollCts, cts)?.Dispose();
        _pollTask = PollAsync(cts.Token);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken) =>
        await ReadTailAsync(cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (!_shell.OpenFolder(_paths.LogsDirectory))
        {
            StatusMessage = Strings.OpenFolderFailedMessage;
        }
    }

    /// <summary>
    /// Polls with <see cref="PeriodicTimer"/> rather than a <c>DispatcherTimer</c>: the file read must
    /// not happen on the UI thread, and the awaited continuation comes back to it on its own because
    /// the loop is started from the dispatcher.
    /// </summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        try
        {
            await ReadTailAsync(cancellationToken).ConfigureAwait(true);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (!AutoRefresh)
                {
                    continue;
                }

                await ReadTailAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "로그 파일을 감시하는 중 오류가 발생했습니다.");
        }
    }

    private async Task ReadTailAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(() => ReadNewestLogTail(_paths.LogsDirectory), cancellationToken)
                .ConfigureAwait(true);

            if (result.FileName is null)
            {
                CurrentFile = Strings.Dash;
                LogText = Strings.LogEmptyMessage;
                return;
            }

            CurrentFile = result.FileName;

            if (!string.Equals(LogText, result.Text, StringComparison.Ordinal))
            {
                LogText = result.Text;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "로그 파일을 읽지 못했습니다.");
            StatusMessage = string.Format(CultureInfo.CurrentCulture, Strings.LogReadFailedFormat, ex.Message);
        }
    }

    /// <summary>Runs on the thread pool. Returns the newest log file's name and its last lines.</summary>
    private static (string? FileName, string Text) ReadNewestLogTail(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (null, string.Empty);
        }

        FileInfo? newest = null;

        foreach (var path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);

            if (newest is null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
            {
                newest = info;
            }
        }

        if (newest is null)
        {
            return (null, string.Empty);
        }

        // FileShare.ReadWrite is mandatory: Serilog keeps the file open for writing.
        using var stream = new FileStream(
            newest.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var seeked = stream.Length > TailByteWindow;

        if (seeked)
        {
            stream.Seek(-TailByteWindow, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var ring = new Queue<string>(TailLineCount);
        var skipFragment = seeked;

        while (reader.ReadLine() is { } line)
        {
            if (skipFragment)
            {
                // The first line after a mid-file seek is almost certainly a fragment.
                skipFragment = false;
                continue;
            }

            if (ring.Count == TailLineCount)
            {
                ring.Dequeue();
            }

            ring.Enqueue(line);
        }

        return (newest.Name, string.Join(Environment.NewLine, ring));
    }

    /// <summary>
    /// Stops the poll loop.
    ///
    /// Cancelling is enough on its own: <see cref="PeriodicTimer.WaitForNextTickAsync"/> observes the
    /// token, <see cref="PollAsync"/> swallows the resulting cancellation, and the window never has to
    /// wait for the loop — which is what keeps closing the window synchronous and safe during
    /// application shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var cts = Interlocked.Exchange(ref _pollCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        cts.Dispose();
    }
}
