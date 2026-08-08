using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace KSubMaker.Infrastructure.Logging;

/// <summary>
/// Replaces the directory components of file-system paths in the rendered message with <c>***</c>,
/// keeping the file name.
///
/// Log files are the first thing a user attaches to a bug report, and a media library path routinely
/// contains a real name (<c>C:\Users\홍길동\Videos\…</c>) plus folder names the user would rather not
/// publish. Masking is opt-in (<c>AppSettings.MaskPathsInLogs</c>) because the full path is genuinely
/// useful when diagnosing on one's own machine.
/// </summary>
public sealed partial class PathMaskingEnricher : ILogEventEnricher
{
    /// <summary>Property the file sink renders instead of <c>{Message}</c> when masking is on.</summary>
    public const string MaskedMessagePropertyName = "MaskedMessage";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var rendered = logEvent.RenderMessage(null);
        var masked = Mask(rendered);

        logEvent.AddOrUpdateProperty(
            propertyFactory.CreateProperty(MaskedMessagePropertyName, masked));
    }

    /// <summary>
    /// Rewrites <c>C:\Users\me\Videos\movie.mkv</c> to <c>C:\***\movie.mkv</c> and
    /// <c>\\server\share\a\b.mkv</c> to <c>\\***\b.mkv</c>. Only the last segment survives, because
    /// that is the part that identifies which file a message is about.
    /// </summary>
    public static string Mask(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var result = DriveRootedPath().Replace(message, static match =>
        {
            var drive = match.Groups["drive"].Value;
            var leaf = match.Groups["leaf"].Value;
            return $@"{drive}\***\{leaf}";
        });

        result = UncPath().Replace(result, static match =>
        {
            var leaf = match.Groups["leaf"].Value;
            return $@"\\***\{leaf}";
        });

        return result;
    }

    /// <summary>
    /// <c>C:\one\two\three.mkv</c> — at least one directory level is required so a bare <c>C:\</c>
    /// is left alone. The leaf stops at whitespace, quotes and the characters Windows forbids in a
    /// file name, so trailing punctuation in a sentence is not swallowed.
    /// </summary>
    [GeneratedRegex(
        @"(?<drive>[A-Za-z]:)\\(?:[^\\/:*?""<>|\r\n]+\\)+(?<leaf>[^\\/:*?""<>|\r\n\s]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DriveRootedPath();

    /// <summary><c>\\server\share\dir\file.ext</c>.</summary>
    [GeneratedRegex(
        @"\\\\(?:[^\\/:*?""<>|\r\n]+\\)+(?<leaf>[^\\/:*?""<>|\r\n\s]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();
}
