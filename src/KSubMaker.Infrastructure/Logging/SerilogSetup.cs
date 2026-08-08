using KSubMaker.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Serilog.ILogger;

namespace KSubMaker.Infrastructure.Logging;

/// <summary>
/// Builds the application's Serilog pipeline and adapts it to <see cref="ILoggerFactory"/>.
///
/// Everything else in the codebase logs through <c>Microsoft.Extensions.Logging</c>; Serilog is only
/// the sink implementation, which keeps the rest of the solution free of a logging-library
/// dependency.
/// </summary>
public static class SerilogSetup
{
    /// <summary>
    /// Base file name. Serilog inserts the roll marker before the extension, producing
    /// <c>ksubmaker-20260802.log</c>, <c>ksubmaker-20260802_001.log</c> and so on.
    /// </summary>
    public const string LogFileNamePattern = "ksubmaker-.log";

    /// <summary>20 MB. A single verbose run of a large batch fills roughly a third of this.</summary>
    public const long FileSizeLimitBytes = 20L * 1024 * 1024;

    /// <summary>Two weeks of history, which comfortably covers "it broke last Friday".</summary>
    public const int RetainedFileCount = 14;

    private const string PlainOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Renders <see cref="PathMaskingEnricher.MaskedMessagePropertyName"/> instead of the raw message.
    /// The <c>:l</c> literal specifier matters: without it Serilog would wrap the whole line in quotes.
    /// </summary>
    private const string MaskedOutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {MaskedMessage:l}{NewLine}{Exception}";

    /// <summary>
    /// Creates the logger. <paramref name="levelSwitch"/> lets the settings screen change the level at
    /// runtime — rebuilding the logger would drop the open file handle and lose buffered lines.
    /// </summary>
    public static Logger CreateLogger(
        IAppPaths paths,
        LoggingLevelSwitch levelSwitch,
        bool maskPaths,
        bool writeToConsole = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(levelSwitch);

        Directory.CreateDirectory(paths.LogsDirectory);

        var template = maskPaths ? MaskedOutputTemplate : PlainOutputTemplate;

        var configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)

            // EF Core narrates every command at Information; that is noise in an application log.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)

            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, LogFileNamePattern),
                outputTemplate: template,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: FileSizeLimitBytes,
                retainedFileCountLimit: RetainedFileCount,
                shared: false,
                // Without a flush interval a crash loses the last few seconds, which is exactly the
                // part that explains the crash.
                flushToDiskInterval: TimeSpan.FromSeconds(2));

        if (maskPaths)
        {
            configuration = configuration.Enrich.With(new PathMaskingEnricher());
        }

        if (writeToConsole)
        {
            configuration = configuration.WriteTo.Console(outputTemplate: template);
        }

        return configuration.CreateLogger();
    }

    /// <summary>Convenience overload for callers that do not need to change the level later.</summary>
    public static Logger CreateLogger(
        IAppPaths paths,
        string? minimumLevel,
        bool maskPaths,
        bool writeToConsole = false) =>
        CreateLogger(paths, new LoggingLevelSwitch(ParseLevel(minimumLevel)), maskPaths, writeToConsole);

    /// <summary>
    /// Wraps a Serilog logger as an <see cref="ILoggerProvider"/>.
    /// <paramref name="dispose"/> transfers ownership: the provider then closes the log file when the
    /// host shuts the logger factory down.
    /// </summary>
    public static ILoggerProvider CreateProvider(ILogger logger, bool dispose = true)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new SerilogLoggerProvider(logger, dispose);
    }

    public static ILoggerFactory CreateLoggerFactory(ILogger logger, bool dispose = true)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return new SerilogLoggerFactory(logger, dispose);
    }

    /// <summary>
    /// Adds the Serilog file sink to a standard logging builder, so the host keeps using
    /// <c>AddLogging</c> and never sees Serilog types.
    /// </summary>
    public static ILoggingBuilder AddKSubMakerFileLogging(
        this ILoggingBuilder builder,
        IAppPaths paths,
        LoggingLevelSwitch levelSwitch,
        bool maskPaths,
        bool writeToConsole = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var logger = CreateLogger(paths, levelSwitch, maskPaths, writeToConsole);
        builder.AddProvider(CreateProvider(logger));
        return builder;
    }

    /// <summary>
    /// Maps <c>AppSettings.LogLevel</c> onto a Serilog level. Both Serilog's own names and the
    /// <c>Microsoft.Extensions.Logging</c> names are accepted, because the settings file is
    /// hand-editable and users write whichever they know.
    /// </summary>
    public static LogEventLevel ParseLevel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "verbose" or "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" or "info" => LogEventLevel.Information,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" or "critical" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
}
