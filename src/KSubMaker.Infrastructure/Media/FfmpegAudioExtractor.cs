using System.Globalization;
using System.Text.RegularExpressions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Media;

/// <summary>
/// Extracts 16 kHz mono PCM WAV with FFmpeg — the exact format faster-whisper wants, so the worker
/// never has to resample.
///
/// Output is written to <c>&lt;target&gt;.tmp</c> and moved into place only after FFmpeg exits with
/// code 0. A crash or a cancellation therefore leaves no half-written <c>audio.wav</c> that a later
/// resume would mistake for a finished extraction.
/// </summary>
public sealed partial class FfmpegAudioExtractor(
    IToolLocator toolLocator,
    IFileSystem fileSystem,
    ILogger<FfmpegAudioExtractor> logger) : IAudioExtractor
{
    /// <summary>
    /// FFmpeg prints a progress line several times a second. Silence for this long means it is
    /// wedged (dead network share, unreadable sector) rather than slow, so the process is killed.
    /// There is deliberately no absolute time limit: a four-hour source on a slow disk is legitimate.
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);

    private readonly IToolLocator _toolLocator = toolLocator;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly ILogger<FfmpegAudioExtractor> _logger = logger;

    public async Task ExtractAsync(
        AudioExtractionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VideoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputWavPath);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_fileSystem.FileExists(request.VideoPath))
        {
            throw new AudioExtractionException(
                ErrorCodes.VideoNotFound,
                $"원본 영상을 찾을 수 없습니다: {Path.GetFileName(request.VideoPath)}");
        }

        string ffmpeg;
        try
        {
            ffmpeg = _toolLocator.FfmpegPath;
        }
        catch (FileNotFoundException ex)
        {
            throw new AudioExtractionException(
                ErrorCodes.FfmpegNotFound,
                "ffmpeg 실행 파일을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다.",
                ex);
        }

        var directory = Path.GetDirectoryName(request.OutputWavPath);
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }

        var tempPath = request.OutputWavPath + ".tmp";
        DeleteQuietly(tempPath);

        var arguments = BuildArguments(request, tempPath);

        // Duration is not part of AudioExtractionRequest, so it is taken from FFmpeg's own input
        // banner ("Duration: 00:12:34.56"). When the container has no duration (raw streams), no
        // percentage is invented: the caller simply gets no progress reports rather than a made-up
        // number that would run past 100 %.
        double? totalSeconds = null;
        var lastReported = -1d;

        using var stallSource = new CancellationTokenSource();
        stallSource.CancelAfter(StallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stallSource.Token);

        void OnStderr(string line)
        {
            ResetStallTimer(stallSource);

            if (totalSeconds is null)
            {
                var durationMatch = DurationPattern().Match(line);
                if (durationMatch.Success)
                {
                    var parsed = ParseTimestamp(durationMatch);
                    if (parsed > 0d)
                    {
                        totalSeconds = parsed;
                    }
                }
            }

            if (progress is null || totalSeconds is not > 0d)
            {
                return;
            }

            var timeMatch = TimePattern().Match(line);
            if (!timeMatch.Success)
            {
                return;
            }

            var elapsed = ParseTimestamp(timeMatch);
            var percent = Math.Clamp(elapsed / totalSeconds.Value * 100d, 0d, 100d);

            // FFmpeg re-emits the same time= several times a second; only forward real movement.
            if (percent > lastReported + 0.1d)
            {
                lastReported = percent;
                progress.Report(percent);
            }
        }

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                    ffmpeg,
                    arguments,
                    Timeout.InfiniteTimeSpan,
                    linked.Token,
                    onStandardErrorLine: OnStderr)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ProcessRunner already killed the whole tree; all that is left is not to leave a
            // truncated WAV behind for a later resume to trip over.
            DeleteQuietly(tempPath);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogWarning(
                "ffmpeg가 {Minutes}분 동안 아무 진행도 보고하지 않아 중단했습니다: {Path}",
                StallTimeout.TotalMinutes, request.VideoPath);

            throw new AudioExtractionException(
                ErrorCodes.FfmpegFailed,
                "음성 추출이 응답하지 않아 중단했습니다. 원본 파일이나 저장 장치를 확인해 주세요.");
        }
        catch (Exception ex) when (ex is not AudioExtractionException)
        {
            DeleteQuietly(tempPath);
            throw new AudioExtractionException(
                ErrorCodes.FfmpegFailed,
                $"ffmpeg를 실행하지 못했습니다: {ex.Message}",
                ex);
        }

        if (!result.Success)
        {
            var detail = result.Tail();
            DeleteQuietly(tempPath);

            if (LooksLikeMissingAudio(result.StandardError))
            {
                _logger.LogWarning("선택한 음성 트랙을 찾을 수 없습니다: {Path} {Detail}", request.VideoPath, detail);
                throw new AudioExtractionException(
                    ErrorCodes.AudioTrackNotFound,
                    "영상에서 음성 트랙을 찾을 수 없습니다.");
            }

            _logger.LogError("ffmpeg가 오류로 종료했습니다({Exit}): {Path} {Detail}",
                result.ExitCode, request.VideoPath, detail);

            throw new AudioExtractionException(
                ErrorCodes.FfmpegFailed,
                string.IsNullOrWhiteSpace(detail)
                    ? "음성 추출에 실패했습니다."
                    : $"음성 추출에 실패했습니다: {detail}");
        }

        if (!_fileSystem.FileExists(tempPath) || _fileSystem.GetFileSize(tempPath) == 0)
        {
            DeleteQuietly(tempPath);
            throw new AudioExtractionException(
                ErrorCodes.FfmpegFailed,
                "음성 추출 결과 파일이 비어 있습니다.");
        }

        _fileSystem.Move(tempPath, request.OutputWavPath, overwrite: true);
        progress?.Report(100d);

        _logger.LogInformation(
            "음성 추출 완료: {Output} ({Size:N0} bytes)",
            request.OutputWavPath,
            _fileSystem.GetFileSize(request.OutputWavPath));
    }

    private static string[] BuildArguments(AudioExtractionRequest request, string tempPath)
    {
        var arguments = new List<string>(16)
        {
            "-hide_banner",
            // Never let FFmpeg read from our stdin: without this it can block forever waiting for a
            // confirmation that nobody is there to give.
            "-nostdin",
            "-y",
            "-i", request.VideoPath,
            "-vn"
        };

        if (request.AudioTrackIndex is { } track && track >= 0)
        {
            // Audio-relative selector, matching the ordinal FfprobeMediaProbe reports.
            arguments.Add("-map");
            arguments.Add(string.Create(CultureInfo.InvariantCulture, $"0:a:{track}"));
        }

        arguments.Add("-ac");
        arguments.Add(request.Channels.ToString(CultureInfo.InvariantCulture));
        arguments.Add("-ar");
        arguments.Add(request.SampleRate.ToString(CultureInfo.InvariantCulture));
        arguments.Add("-c:a");
        arguments.Add("pcm_s16le");
        arguments.Add("-f");
        arguments.Add("wav");
        arguments.Add(tempPath);

        return [.. arguments];
    }

    /// <summary>
    /// FFmpeg reports a missing / unmatched audio stream in several different ways depending on
    /// whether the container has no audio at all or the requested track index does not exist.
    /// </summary>
    private static bool LooksLikeMissingAudio(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        // Only phrases that are unique to a stream-selection failure. "Stream mapping:" for example
        // is printed by every run, successful or not, and must not be matched here.
        ReadOnlySpan<string> markers =
        [
            "matches no streams",
            "does not contain any stream",
            "Cannot find a matching stream",
            "Invalid stream specifier",
            "Output file does not contain any stream"
        ];

        foreach (var marker in markers)
        {
            if (stderr.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ResetStallTimer(CancellationTokenSource source)
    {
        try
        {
            source.CancelAfter(StallTimeout);
        }
        catch (ObjectDisposedException)
        {
            // A trailing stderr line arrived after the run completed; nothing to reset.
        }
    }

    private void DeleteQuietly(string path)
    {
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "임시 파일을 삭제하지 못했습니다: {Path}", path);
        }
    }

    private static double ParseTimestamp(Match match)
    {
        var hours = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[4].Success
            ? double.Parse("0." + match.Groups[4].Value, CultureInfo.InvariantCulture)
            : 0d;

        var total = (hours * 3600d) + (minutes * 60d) + seconds + fraction;

        // FFmpeg emits "time=-00:00:00.00" before the first frame is decoded.
        return match.Value.Contains('-') ? 0d : total;
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d{2}):(\d{2})(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"time=\s*-?(\d+):(\d{2}):(\d{2})(?:\.(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
}
