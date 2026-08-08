using KSubMaker.Domain.Settings;

namespace KSubMaker.Domain.Subtitles;

/// <summary>Outcome of applying <see cref="OutputConflictPolicy"/> to a target path.</summary>
public sealed record OutputResolution(string Path, bool ShouldWrite, bool WasRenamed, string? Reason);

/// <summary>
/// Decides where the Korean SRT goes. Pure apart from the injected existence probe, which keeps it
/// unit testable without touching the file system.
/// </summary>
public static class OutputPathResolver
{
    /// <summary>Builds <c>{directory}/{video base name}.{suffix}.srt</c>.</summary>
    public static string BuildDefaultPath(string videoPath, string suffix = "ko")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var cleanSuffix = string.IsNullOrWhiteSpace(suffix) ? "ko" : suffix.Trim().Trim('.');

        return Path.Combine(directory, $"{baseName}.{cleanSuffix}.srt");
    }

    /// <summary>
    /// Applies the conflict policy. <paramref name="exists"/> is normally
    /// <see cref="File.Exists(string)"/>; tests substitute a set-backed predicate.
    /// </summary>
    public static OutputResolution Resolve(
        string desiredPath,
        OutputConflictPolicy policy,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredPath);
        ArgumentNullException.ThrowIfNull(exists);

        if (!exists(desiredPath))
        {
            return new OutputResolution(desiredPath, ShouldWrite: true, WasRenamed: false, Reason: null);
        }

        switch (policy)
        {
            case OutputConflictPolicy.Overwrite:
                return new OutputResolution(desiredPath, ShouldWrite: true, WasRenamed: false,
                    Reason: "기존 자막 파일을 덮어씁니다.");

            case OutputConflictPolicy.CreateNumberedCopy:
            {
                var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
                var name = Path.GetFileNameWithoutExtension(desiredPath);
                var extension = Path.GetExtension(desiredPath);

                for (var i = 2; i < 1000; i++)
                {
                    var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
                    if (!exists(candidate))
                    {
                        return new OutputResolution(candidate, ShouldWrite: true, WasRenamed: true,
                            Reason: "기존 파일이 있어 번호를 붙여 새 파일로 저장합니다.");
                    }
                }

                return new OutputResolution(desiredPath, ShouldWrite: false, WasRenamed: false,
                    Reason: "번호를 붙일 수 있는 파일명을 찾지 못했습니다.");
            }

            case OutputConflictPolicy.Skip:
            default:
                return new OutputResolution(desiredPath, ShouldWrite: false, WasRenamed: false,
                    Reason: "이미 자막 파일이 있어 건너뜁니다.");
        }
    }

    /// <summary>
    /// True when the sidecar looks like an existing Korean subtitle for the video
    /// (<c>movie.ko.srt</c>, <c>movie.kor.srt</c>, <c>movie.korean.srt</c>, <c>movie.ko-KR.srt</c>).
    /// </summary>
    public static bool LooksKorean(string subtitlePath)
    {
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot < 0)
        {
            return false;
        }

        var tag = name[(lastDot + 1)..];

        return tag.Equals("ko", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("kor", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("korean", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("ko-kr", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("ko_kr", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("kr", StringComparison.OrdinalIgnoreCase);
    }
}
