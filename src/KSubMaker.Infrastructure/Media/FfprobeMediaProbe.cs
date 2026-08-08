using System.Globalization;
using System.Text.Json;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Media;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Infrastructure.Media;

/// <summary>
/// Reads container metadata with <c>ffprobe -print_format json</c>.
///
/// The contract is "never throw": a corrupt file, a missing ffprobe, a hung probe and unparseable
/// JSON all come back as a <see cref="VideoFile"/> with <see cref="VideoFile.ProbeError"/> set, so a
/// single bad file in a folder of 500 cannot abort the scan.
/// </summary>
public sealed class FfprobeMediaProbe(IToolLocator toolLocator, ILogger<FfprobeMediaProbe> logger) : IMediaProbe
{
    /// <summary>
    /// Generous, because ffprobe on a large MKV over SMB genuinely takes tens of seconds; short
    /// enough that a wedged probe cannot stall the queue.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    private readonly IToolLocator _toolLocator = toolLocator;
    private readonly ILogger<FfprobeMediaProbe> _logger = logger;

    public async Task<VideoFile> ProbeAsync(VideoFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        string ffprobe;
        try
        {
            ffprobe = _toolLocator.FfprobePath;
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "ffprobe 실행 파일을 찾지 못했습니다.");
            return Failed(file, "ffprobe 실행 파일을 찾을 수 없습니다. 설치가 손상되었을 수 있습니다.");
        }

        // ArgumentList only — the path may contain spaces, quotes, '%' or a leading '-', none of
        // which may be allowed to reinterpret the command line.
        string[] arguments =
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            file.FullPath
        ];

        ProcessResult result;
        try
        {
            result = await ProcessRunner
                .RunAsync(ffprobe, arguments, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe 실행에 실패했습니다: {Path}", file.FullPath);
            return Failed(file, $"ffprobe를 실행하지 못했습니다: {ex.Message}");
        }

        if (result.TimedOut)
        {
            _logger.LogWarning("ffprobe가 제한 시간을 초과했습니다: {Path}", file.FullPath);
            return Failed(file, "영상 정보를 읽는 데 너무 오래 걸려 중단했습니다.");
        }

        if (!result.Success)
        {
            var detail = result.Tail();
            _logger.LogWarning(
                "ffprobe가 오류로 종료했습니다({Exit}): {Path} {Detail}", result.ExitCode, file.FullPath, detail);

            return Failed(file, string.IsNullOrWhiteSpace(detail)
                ? "영상 정보를 읽을 수 없습니다. 파일이 손상되었을 수 있습니다."
                : $"영상 정보를 읽을 수 없습니다: {detail}");
        }

        try
        {
            return Parse(file, result.StandardOutput);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ffprobe 출력을 해석하지 못했습니다: {Path}", file.FullPath);
            return Failed(file, "영상 정보를 해석하지 못했습니다.");
        }
    }

    private static VideoFile Parse(VideoFile file, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var duration = 0d;
        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var durationElement))
        {
            duration = ReadDouble(durationElement);
        }

        var audio = new List<AudioTrackInfo>();
        var subtitles = new List<EmbeddedSubtitleTrackInfo>();

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = ReadString(stream, "codec_type");

                switch (codecType)
                {
                    case "audio":
                        audio.Add(new AudioTrackInfo
                        {
                            // Audio-relative ordinal, not the absolute ffprobe stream index: this is
                            // the number FfmpegAudioExtractor feeds to "-map 0:a:<n>", so the value
                            // shown in the track picker is the value that selects that track.
                            Index = audio.Count,
                            Codec = ReadString(stream, "codec_name"),
                            Channels = ReadInt(stream, "channels"),
                            Language = ReadTag(stream, "language"),
                            Title = ReadTag(stream, "title"),
                            IsDefault = ReadDisposition(stream, "default")
                        });
                        break;

                    case "subtitle":
                        subtitles.Add(new EmbeddedSubtitleTrackInfo
                        {
                            // Subtitle-relative ordinal, matching "-map 0:s:<n>".
                            Index = subtitles.Count,
                            Codec = ReadString(stream, "codec_name"),
                            Language = ReadTag(stream, "language"),
                            Title = ReadTag(stream, "title"),
                            IsDefault = ReadDisposition(stream, "default"),
                            IsForced = ReadDisposition(stream, "forced")
                        });
                        break;

                    default:
                        // Video, data and attachment streams are irrelevant here.
                        break;
                }

                // Some containers (notably raw TS) carry no format-level duration; the longest
                // stream duration is the best available answer.
                if (duration <= 0d && stream.TryGetProperty("duration", out var streamDuration))
                {
                    duration = Math.Max(duration, ReadDouble(streamDuration));
                }
            }
        }

        return file with
        {
            DurationSeconds = duration,
            HasAudioTrack = audio.Count > 0,
            AudioTracks = audio,
            SubtitleTracks = subtitles,
            Probed = true,
            ProbeError = null
        };
    }

    /// <summary>
    /// A failed probe still reports <c>Probed = true</c> with no audio tracks: that combination is
    /// what <c>JobQueueService.ApplyProbeAsync</c> turns into a VIDEO_UNREADABLE failure instead of
    /// letting the job march on to an extraction that cannot possibly work.
    /// </summary>
    private static VideoFile Failed(VideoFile file, string error) => file with
    {
        Probed = true,
        ProbeError = error,
        HasAudioTrack = false,
        AudioTracks = [],
        SubtitleTracks = []
    };

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    /// <summary>ffprobe emits durations as strings such as <c>"7261.482000"</c>.</summary>
    private static double ReadDouble(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => Sane(number),
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => Sane(parsed),
            _ => 0d
        };

        static double Sane(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) || value < 0d ? 0d : value;
    }

    private static string? ReadTag(JsonElement stream, string tag)
    {
        if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Tag names are case-insensitive in practice: Matroska writes "LANGUAGE", MP4 "language".
        foreach (var property in tags.EnumerateObject())
        {
            if (property.Name.Equals(tag, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    private static bool ReadDisposition(JsonElement stream, string flag) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        disposition.ValueKind == JsonValueKind.Object &&
        ReadInt(disposition, flag) != 0;
}
