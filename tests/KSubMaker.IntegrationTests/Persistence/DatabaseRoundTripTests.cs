using FluentAssertions;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.Infrastructure.Persistence;
using KSubMaker.Infrastructure.Persistence.Repositories;
using KSubMaker.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.IntegrationTests.Persistence;

/// <summary>
/// A real SQLite file on disk: migrations, persistence across contexts and crash recovery.
/// </summary>
public sealed class DatabaseRoundTripTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new("ksubmaker-db");

    private AppPaths _paths = null!;
    private SqliteFileContextFactory _factory = null!;
    private DatabaseInitializer _initializer = null!;
    private JobRepository _jobs = null!;
    private SettingsRepository _settings = null!;

    public Task InitializeAsync()
    {
        _paths = new AppPaths(Path.Combine(_workspace.Root, "appdata"));
        _paths.EnsureCreated();

        _factory = new SqliteFileContextFactory(_paths.DatabaseFile);
        _initializer = new DatabaseInitializer(_factory, _paths, NullLogger<DatabaseInitializer>.Instance);
        _jobs = new JobRepository(_factory, NullLogger<JobRepository>.Instance);
        _settings = new SettingsRepository(_factory, NullLogger<SettingsRepository>.Instance);

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _workspace.Dispose();
        return Task.CompletedTask;
    }

    private static Job NewJob(string path, JobStatus status = JobStatus.Pending, int order = 0) => new()
    {
        VideoPath = path,
        FileName = Path.GetFileName(path),
        FileSize = 1_234_567L,
        LastWriteTimeUtc = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc),
        DurationSeconds = 321.5d,
        Status = status,
        CurrentStage = status switch
        {
            JobStatus.Completed => JobStage.Done,
            JobStatus.Transcribing => JobStage.Transcribing,
            _ => JobStage.None
        },
        QueueOrder = order,
        OutputPath = Path.ChangeExtension(path, ".ko.srt"),
        TranslationEngine = TranslationEngineKind.Fake
    };

    [Fact]
    public async Task InitializeAsync_creates_the_file_and_applies_the_migration()
    {
        File.Exists(_paths.DatabaseFile).Should().BeFalse();

        await _initializer.InitializeAsync();

        File.Exists(_paths.DatabaseFile).Should().BeTrue();

        await using var context = _factory.CreateDbContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        applied.Should().NotBeEmpty("MigrateAsync must record history so later releases can upgrade");

        (await context.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_is_idempotent()
    {
        await _initializer.InitializeAsync();

        var act = async () => await _initializer.InitializeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Jobs_persist_across_a_new_context()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/영화 제목.mkv");
        await _jobs.AddAsync(job);

        var reloaded = await _jobs.FindAsync(job.Id);

        reloaded.Should().NotBeNull();
        reloaded!.VideoPath.Should().Be(job.VideoPath);
        reloaded.FileName.Should().Be(job.FileName);
        reloaded.FileSize.Should().Be(job.FileSize);
        reloaded.LastWriteTimeUtc.Should().Be(job.LastWriteTimeUtc);
        reloaded.DurationSeconds.Should().Be(job.DurationSeconds);
        reloaded.Status.Should().Be(job.Status);
        reloaded.OutputPath.Should().Be(job.OutputPath);
        reloaded.TranslationEngine.Should().Be(TranslationEngineKind.Fake);
        reloaded.Should().NotBeSameAs(job, "the repository must hand back a detached copy");
    }

    [Fact]
    public async Task Enum_columns_are_stored_by_name()
    {
        await _initializer.InitializeAsync();
        await _jobs.AddAsync(NewJob("/videos/a.mkv", JobStatus.Transcribing));

        await using var context = _factory.CreateDbContext();
        await context.Database.OpenConnectionAsync();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Status, CurrentStage FROM Jobs LIMIT 1";

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();

        reader.GetString(0).Should().Be("Transcribing");
        reader.GetString(1).Should().Be("Transcribing");
    }

    [Fact]
    public async Task The_per_file_subtitle_source_override_survives_a_restart()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/애니메이션.mkv");
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 3;
        job.SelectedSubtitleLanguage = "ja";
        job.HasEmbeddedSubtitle = true;

        await _jobs.AddAsync(job);

        var reloaded = await _jobs.FindAsync(job.Id);

        reloaded!.SourceOverride.Should().Be(JobSourceOverride.EmbeddedSubtitle);
        reloaded.SelectedSubtitleTrackIndex.Should().Be(3);
        reloaded.SelectedSubtitleLanguage.Should().Be("ja");
        reloaded.SelectedAudioTrackIndex.Should().BeNull();
        reloaded.HasSourceOverride.Should().BeTrue();
    }

    [Fact]
    public async Task A_job_with_no_override_reloads_on_the_core_path()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/기본.mkv");
        await _jobs.AddAsync(job);

        var reloaded = await _jobs.FindAsync(job.Id);

        reloaded!.SourceOverride.Should().Be(JobSourceOverride.None);
        reloaded.HasSourceOverride.Should().BeFalse();
        reloaded.SelectedAudioTrackIndex.Should().BeNull();
        reloaded.SelectedSubtitleTrackIndex.Should().BeNull();
        reloaded.SelectedSubtitleLanguage.Should().BeNull();
    }

    [Fact]
    public async Task An_audio_track_override_survives_a_restart()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/이중음성.mkv");
        job.SourceOverride = JobSourceOverride.Audio;
        job.SelectedAudioTrackIndex = 1;

        await _jobs.AddAsync(job);

        var reloaded = await _jobs.FindAsync(job.Id);

        reloaded!.SourceOverride.Should().Be(JobSourceOverride.Audio);
        reloaded.SelectedAudioTrackIndex.Should().Be(1);
    }

    [Fact]
    public async Task The_source_override_column_is_stored_by_name_like_the_other_enums()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/a.mkv");
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        await _jobs.AddAsync(job);

        await using var context = _factory.CreateDbContext();
        await context.Database.OpenConnectionAsync();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT SourceOverride FROM Jobs LIMIT 1";

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("EmbeddedSubtitle");
    }

    /// <summary>
    /// The scaffolder's default for a new non-nullable string column is <c>""</c>, which is not a
    /// member name and would make every pre-upgrade row unreadable. The migration overrides it.
    /// </summary>
    [Fact]
    public async Task A_row_inserted_without_the_new_column_still_loads()
    {
        await _initializer.InitializeAsync();

        await using (var context = _factory.CreateDbContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Jobs (Id, VideoPath, FileName, FileSize, LastWriteTimeUtc, DurationSeconds,
                                  HasAudioTrack, HasEmbeddedSubtitle, HasExternalSubtitle, HasKoreanSubtitle,
                                  Status, CurrentStage, OverallProgress, StageProgress, ProcessingSpeed,
                                  RetryCount, CreatedAtUtc, UpdatedAtUtc, QueueOrder)
                VALUES ('legacy-row', '/videos/old.mkv', 'old.mkv', 1, '2026-01-01 00:00:00', 10,
                        1, 0, 0, 0, 'Pending', 'None', 0, 0, 0, 0,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00', 0)
                """);
        }

        var reloaded = await _jobs.FindAsync("legacy-row");

        reloaded.Should().NotBeNull();
        reloaded!.SourceOverride.Should().Be(JobSourceOverride.None);
    }

    [Fact]
    public async Task FindByPathAsync_is_case_insensitive()
    {
        await _initializer.InitializeAsync();
        await _jobs.AddAsync(NewJob("/videos/Movie.MKV"));

        (await _jobs.FindByPathAsync("/videos/movie.mkv")).Should().NotBeNull();
    }

    [Fact]
    public async Task Jobs_come_back_ordered_by_queue_position()
    {
        await _initializer.InitializeAsync();

        await _jobs.AddRangeAsync(
        [
            NewJob("/videos/c.mkv", order: 2),
            NewJob("/videos/a.mkv", order: 0),
            NewJob("/videos/b.mkv", order: 1)
        ]);

        var all = await _jobs.GetAllAsync();

        all.Select(j => j.FileName).Should().Equal("a.mkv", "b.mkv", "c.mkv");
    }

    [Fact]
    public async Task UpdateAsync_writes_a_detached_instance_back()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/a.mkv");
        await _jobs.AddAsync(job);

        job.Status = JobStatus.Failed;
        job.ErrorCode = "FFMPEG_FAILED";
        job.ErrorMessage = "음성 추출 실패";
        job.OverallProgress = 42.5d;

        await _jobs.UpdateAsync(job);

        var reloaded = await _jobs.FindAsync(job.Id);

        reloaded!.Status.Should().Be(JobStatus.Failed);
        reloaded.ErrorCode.Should().Be("FFMPEG_FAILED");
        reloaded.ErrorMessage.Should().Be("음성 추출 실패");
        reloaded.OverallProgress.Should().Be(42.5d);
    }

    [Fact]
    public async Task UpdateAsync_on_a_deleted_row_is_a_no_op_rather_than_a_resurrection()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/a.mkv");
        await _jobs.AddAsync(job);
        await _jobs.RemoveAsync(job.Id);

        var act = async () => await _jobs.UpdateAsync(job);

        await act.Should().NotThrowAsync();
        (await _jobs.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task EstimatedTimeRemaining_is_deliberately_not_persisted()
    {
        await _initializer.InitializeAsync();

        var job = NewJob("/videos/a.mkv");
        job.EstimatedTimeRemaining = TimeSpan.FromMinutes(3);

        await _jobs.AddAsync(job);

        (await _jobs.FindAsync(job.Id))!.EstimatedTimeRemaining
            .Should().BeNull("a stale ETA on a job that is not running would be misleading");
    }

    [Fact]
    public async Task ResetOrphanedActiveJobsAsync_demotes_every_active_job_to_paused()
    {
        await _initializer.InitializeAsync();

        await _jobs.AddRangeAsync(
        [
            NewJob("/videos/probing.mkv", JobStatus.Probing, 0),
            NewJob("/videos/transcribing.mkv", JobStatus.Transcribing, 1),
            NewJob("/videos/translating.mkv", JobStatus.Translating, 2),
            NewJob("/videos/pending.mkv", JobStatus.Pending, 3),
            NewJob("/videos/done.mkv", JobStatus.Completed, 4)
        ]);

        var reset = await _jobs.ResetOrphanedActiveJobsAsync();

        reset.Should().Be(3);

        var all = await _jobs.GetAllAsync();

        all.Single(j => j.FileName == "probing.mkv").Status.Should().Be(JobStatus.Paused);
        all.Single(j => j.FileName == "transcribing.mkv").Status.Should().Be(JobStatus.Paused);
        all.Single(j => j.FileName == "translating.mkv").Status.Should().Be(JobStatus.Paused);
        all.Single(j => j.FileName == "pending.mkv").Status.Should().Be(JobStatus.Pending);
        all.Single(j => j.FileName == "done.mkv").Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task ResetOrphanedActiveJobsAsync_keeps_the_stage_so_a_resume_is_cheap()
    {
        await _initializer.InitializeAsync();
        await _jobs.AddAsync(NewJob("/videos/a.mkv", JobStatus.Transcribing));

        await _jobs.ResetOrphanedActiveJobsAsync();

        var job = (await _jobs.GetAllAsync()).Single();

        job.Status.Should().Be(JobStatus.Paused);
        job.CurrentStage.Should().Be(JobStage.Transcribing,
            "Paused keeps CurrentStage so the checkpoint store can resume from the interrupted stage");
    }

    [Fact]
    public async Task ResetOrphanedActiveJobsAsync_returns_zero_when_there_is_nothing_to_repair()
    {
        await _initializer.InitializeAsync();
        await _jobs.AddAsync(NewJob("/videos/a.mkv", JobStatus.Completed));

        (await _jobs.ResetOrphanedActiveJobsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Settings_persist_across_a_new_context()
    {
        await _initializer.InitializeAsync();

        var settings = new AppSettings
        {
            LastFolder = "/영상 보관함",
            BeamSize = 3,
            TranslationStyle = TranslationStyle.Polite,
            OutputConflictPolicy = OutputConflictPolicy.CreateNumberedCopy,
            Glossary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Seoul"] = "서울" }
        };

        await _settings.SaveAsync(settings);

        // A brand-new factory over the same file: nothing can be cached in memory.
        using var secondFactory = new SqliteFileContextFactory(_paths.DatabaseFile);
        var secondRepository = new SettingsRepository(secondFactory, NullLogger<SettingsRepository>.Instance);

        var loaded = await secondRepository.LoadAsync();

        loaded.LastFolder.Should().Be("/영상 보관함");
        loaded.BeamSize.Should().Be(3);
        loaded.TranslationStyle.Should().Be(TranslationStyle.Polite);
        loaded.OutputConflictPolicy.Should().Be(OutputConflictPolicy.CreateNumberedCopy);
        loaded.Glossary.Should().ContainKey("Seoul").WhoseValue.Should().Be("서울");
    }

    [Fact]
    public async Task Jobs_and_settings_share_one_database_file_without_interfering()
    {
        await _initializer.InitializeAsync();

        await _jobs.AddAsync(NewJob("/videos/a.mkv"));
        await _settings.SaveAsync(new AppSettings { BeamSize = 4 });

        (await _jobs.GetAllAsync()).Should().ContainSingle();
        (await _settings.LoadAsync()).BeamSize.Should().Be(4);
    }

    [Fact]
    public async Task RemoveRangeAsync_deletes_only_the_named_jobs()
    {
        await _initializer.InitializeAsync();

        var keep = NewJob("/videos/keep.mkv", order: 0);
        var drop = NewJob("/videos/drop.mkv", order: 1);

        await _jobs.AddRangeAsync([keep, drop]);
        await _jobs.RemoveRangeAsync([drop.Id]);

        (await _jobs.GetAllAsync()).Select(j => j.FileName).Should().Equal("keep.mkv");
    }
}
