using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Processing;
using KSubMaker.Application.Services;
using KSubMaker.Application.Testing;
using KSubMaker.Domain.Models;
using KSubMaker.Domain.Settings;
using KSubMaker.Infrastructure.Checkpoints;
using KSubMaker.Infrastructure.IO;
using KSubMaker.Infrastructure.Media;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.Infrastructure.Persistence;
using KSubMaker.Infrastructure.Persistence.Repositories;
using KSubMaker.Infrastructure.Subtitles;
using KSubMaker.Worker.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace KSubMaker.IntegrationTests.Infrastructure;

/// <summary>
/// Wires the real infrastructure — physical file system, real ffmpeg/ffprobe, real atomic subtitle
/// writer, real file checkpoint store, real SQLite database — around the deterministic fake AI
/// engines, and hands back a live <see cref="JobQueueService"/>.
///
/// Everything except the two AI stages is production code, which is the whole point: the integration
/// suite must exercise the paths that actually run on a user's machine.
/// </summary>
public sealed class PipelineHarness : IAsyncDisposable
{
    private readonly TempWorkspace _workspace;
    private readonly SqliteFileContextFactory _contextFactory;

    public PipelineHarness(
        TempWorkspace workspace,
        ITranscriber? transcriber = null,
        Func<IAudioExtractor, IAudioExtractor>? wrapAudioExtractor = null)
    {
        _workspace = workspace;

        AppRoot = Path.Combine(workspace.Root, "appdata");
        Paths = new AppPaths(AppRoot);
        Paths.EnsureCreated();

        FileSystem = new PhysicalFileSystem(NullLogger<PhysicalFileSystem>.Instance);
        ToolLocator = new ToolLocator(Paths, NullLogger<ToolLocator>.Instance);

        MediaProbe = new FfprobeMediaProbe(ToolLocator, NullLogger<FfprobeMediaProbe>.Instance);

        RealAudioExtractor = new FfmpegAudioExtractor(
            ToolLocator, FileSystem, NullLogger<FfmpegAudioExtractor>.Instance);

        AudioExtractor = wrapAudioExtractor is null ? RealAudioExtractor : wrapAudioExtractor(RealAudioExtractor);
        Transcriber = transcriber ?? new FakeTranscriber(FileSystem);
        TranslationEngine = new FakeTranslationEngine();

        SubtitleWriter = new AtomicSubtitleWriter(FileSystem, NullLogger<AtomicSubtitleWriter>.Instance);
        CheckpointStore = new FileCheckpointStore(Paths, NullLogger<FileCheckpointStore>.Instance);

        _contextFactory = new SqliteFileContextFactory(Paths.DatabaseFile);
        DatabaseInitializer = new DatabaseInitializer(_contextFactory, Paths, NullLogger<DatabaseInitializer>.Instance);
        JobRepository = new JobRepository(_contextFactory, NullLogger<JobRepository>.Instance);
        SettingsRepository = new SettingsRepository(_contextFactory, NullLogger<SettingsRepository>.Instance);

        HardwareService = new HardwareService(
            new FakeHardwareDetector(FakeHardwareDetector.CpuOnly),
            new ModelCatalog(),
            NullLogger<HardwareService>.Instance);

        Processor = new InProcessJobProcessor(
            AudioExtractor,
            Transcriber,
            TranslationEngine,
            SubtitleWriter,
            CheckpointStore,
            Paths,
            FileSystem,
            NullLogger<InProcessJobProcessor>.Instance);

        Queue = new JobQueueService(
            JobRepository,
            new SingleProcessorSelector(Processor),
            CheckpointStore,
            HardwareService,
            NullLogger<JobQueueService>.Instance);

        ScanService = new VideoScanService(FileSystem, NullLogger<VideoScanService>.Instance);
    }

    public string AppRoot { get; }
    public AppPaths Paths { get; }
    public PhysicalFileSystem FileSystem { get; }
    public ToolLocator ToolLocator { get; }
    public FfprobeMediaProbe MediaProbe { get; }
    public FfmpegAudioExtractor RealAudioExtractor { get; }
    public IAudioExtractor AudioExtractor { get; }
    public ITranscriber Transcriber { get; }
    public FakeTranslationEngine TranslationEngine { get; }
    public AtomicSubtitleWriter SubtitleWriter { get; }
    public FileCheckpointStore CheckpointStore { get; }
    public DatabaseInitializer DatabaseInitializer { get; }
    public JobRepository JobRepository { get; }
    public SettingsRepository SettingsRepository { get; }
    public HardwareService HardwareService { get; }
    public InProcessJobProcessor Processor { get; }
    public JobQueueService Queue { get; }
    public VideoScanService ScanService { get; }

    public IDbContextFactoryAccessor ContextFactory => _contextFactory;

    public async Task InitializeDatabaseAsync()
    {
        await DatabaseInitializer.InitializeAsync().ConfigureAwait(false);
        await Queue.LoadAsync().ConfigureAwait(false);
    }

    /// <summary>Settings that keep the run deterministic: sequential strategy, fake engines, overwrite.</summary>
    public static AppSettings DeterministicSettings(Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings
        {
            FakeAiMode = true,
            TranslationEngine = TranslationEngineKind.Fake,
            ProcessingStrategy = ProcessingStrategy.SequentialPerFile,
            OutputConflictPolicy = OutputConflictPolicy.Overwrite,
            AutoRetryOnRecoverableError = false,
            SourceLanguage = "en"
        };

        configure?.Invoke(settings);
        return settings;
    }

    /// <summary>
    /// Starts the queue and waits for the pump to come to rest. Polls the queue's own state events —
    /// no sleeping, no fixed delays.
    /// </summary>
    public async Task RunQueueToCompletionAsync(AppSettings settings, TimeSpan? timeout = null)
    {
        await WaitForQueueToSettleAsync(() => Queue.StartAsync(settings), timeout).ConfigureAwait(false);
    }

    public async Task WaitForQueueToSettleAsync(Func<Task> start, TimeSpan? timeout = null)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, QueueStateChangedEventArgs args)
        {
            if (args.State is QueueState.Idle or QueueState.Paused)
            {
                settled.TrySetResult();
            }
        }

        Queue.StateChanged += OnStateChanged;

        try
        {
            await start().ConfigureAwait(false);
            await settled.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }
        finally
        {
            Queue.StateChanged -= OnStateChanged;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync().ConfigureAwait(false);
        _contextFactory.Dispose();
    }

    private sealed class SingleProcessorSelector(IJobProcessor processor) : IJobProcessorSelector
    {
        public IJobProcessor Select(AppSettings settings) => processor;
    }
}
