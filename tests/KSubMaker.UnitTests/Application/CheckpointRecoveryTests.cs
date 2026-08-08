using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Processing;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// Covers "체크포인트 복구" through <see cref="InProcessJobProcessor"/> with counting fakes: the
/// expensive stages must not run twice, and a partially written translation must only ask for the ids
/// it is actually missing.
/// </summary>
public sealed class CheckpointRecoveryTests
{
    private const string VideoPath = "/videos/movie.mkv";
    private const long OriginalSize = 12_345L;

    private static readonly DateTime LastWrite = new(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc);

    private sealed class Harness
    {
        public Harness(ILogger<InProcessJobProcessor>? logger = null)
        {
            FileSystem = new InMemoryFileSystem()
                .AddDirectory("/videos")
                .AddFile(VideoPath, size: OriginalSize, lastWriteUtc: LastWrite);

            Extractor = new CountingAudioExtractor(FileSystem);
            Transcriber = new CountingTranscriber(BuildSegments(9));
            Translator = new CountingTranslationEngine();
            Writer = new RecordingSubtitleWriter(FileSystem);
            Checkpoints = new InMemoryCheckpointStore();
            Paths = new FakeAppPaths();

            Processor = new InProcessJobProcessor(
                Extractor,
                Transcriber,
                Translator,
                Writer,
                Checkpoints,
                Paths,
                FileSystem,
                logger ?? NullLogger<InProcessJobProcessor>.Instance);
        }

        public InMemoryFileSystem FileSystem { get; }
        public CountingAudioExtractor Extractor { get; }
        public CountingTranscriber Transcriber { get; }
        public CountingTranslationEngine Translator { get; }
        public RecordingSubtitleWriter Writer { get; }
        public InMemoryCheckpointStore Checkpoints { get; }
        public FakeAppPaths Paths { get; }
        public InProcessJobProcessor Processor { get; }

        public Job Job { get; } = new()
        {
            Id = "job-under-test",
            VideoPath = VideoPath,
            FileName = "movie.mkv",
            FileSize = OriginalSize,
            LastWriteTimeUtc = LastWrite,
            DurationSeconds = 45d
        };

        public Task<JobExecutionResult> RunAsync(AppSettings? settings = null, JobPhase phase = JobPhase.Full) =>
            Processor.ProcessAsync(
                Job,
                settings ?? DefaultSettings(),
                phase,
                new Progress<JobProgress>(_ => { }),
                CancellationToken.None);
    }

    private static AppSettings DefaultSettings() => new()
    {
        TranslationBatchMaxItems = 3,
        TranslationBatchMaxChars = 100_000,
        TranslationBatchMaxSeconds = 100_000,
        TranslationContextLines = 1,
        OutputConflictPolicy = OutputConflictPolicy.Overwrite
    };

    private static IReadOnlyList<TranscriptionSegment> BuildSegments(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new TranscriptionSegment
            {
                Id = i,
                Start = (i - 1) * 5d,
                End = i * 5d,
                Text = $"Sentence number {i} of the transcript."
            })
            .ToArray();

    // -----------------------------------------------------------------------
    // happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_first_run_extracts_transcribes_translates_and_writes()
    {
        var harness = new Harness();

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        harness.Extractor.Calls.Should().Be(1);
        harness.Transcriber.Calls.Should().Be(1);
        harness.Writer.Calls.Should().Be(1);
        harness.Checkpoints.Peek(harness.Job.Id)!.CompletedStage.Should().Be(JobStage.Done);
    }

    [Fact]
    public async Task A_missing_source_file_fails_before_anything_else_runs()
    {
        var harness = new Harness();
        harness.FileSystem.Delete(VideoPath);

        var result = await harness.RunAsync();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(KSubMaker.Domain.Errors.ErrorCodes.VideoNotFound);
        harness.Extractor.Calls.Should().Be(0);
        harness.Transcriber.Calls.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // ASR is not repeated
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Re_running_a_finished_job_does_not_repeat_speech_recognition()
    {
        var harness = new Harness();

        await harness.RunAsync();
        harness.Transcriber.Calls.Should().Be(1);

        await harness.RunAsync();

        harness.Transcriber.Calls.Should().Be(1, "the transcription checkpoint must be reused");
        harness.Extractor.Calls.Should().Be(1, "the extracted audio is still on disk");
    }

    [Fact]
    public async Task Audio_extraction_is_skipped_when_the_wav_survived_a_crash()
    {
        var harness = new Harness();

        // Simulate a crash right after extraction: the wav and the stage checkpoint exist, nothing else.
        var audioPath = Path.Combine(harness.Paths.JobCacheDirectory(harness.Job.Id), "audio.wav");
        harness.FileSystem.CreateDirectory(harness.Paths.JobCacheDirectory(harness.Job.Id));
        harness.FileSystem.AddFile(audioPath, size: 64);

        await harness.Checkpoints.SaveAsync(new JobCheckpoint
        {
            JobId = harness.Job.Id,
            VideoPath = VideoPath,
            CompletedStage = JobStage.ExtractingAudio,
            AudioPath = audioPath,
            SourceFileSize = OriginalSize,
            SourceLastWriteUtc = LastWrite
        });

        await harness.RunAsync();

        harness.Extractor.Calls.Should().Be(0, "audio.wav from the interrupted run is reusable");
        harness.Transcriber.Calls.Should().Be(1, "transcription had not finished yet");
    }

    [Fact]
    public async Task A_transcribe_only_phase_stops_before_translation()
    {
        var harness = new Harness();

        var result = await harness.RunAsync(phase: JobPhase.TranscribeOnly);

        result.Success.Should().BeTrue();
        result.CueCount.Should().Be(9);
        harness.Transcriber.Calls.Should().Be(1);
        harness.Translator.Calls.Should().Be(0);
        harness.Writer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_translate_phase_resumes_from_the_transcription_checkpoint()
    {
        var harness = new Harness();

        await harness.RunAsync(phase: JobPhase.TranscribeOnly);
        await harness.RunAsync(phase: JobPhase.TranslateAndWrite);

        harness.Transcriber.Calls.Should().Be(1);
        harness.Writer.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_translate_phase_with_no_transcription_fails_cleanly()
    {
        var harness = new Harness();

        var result = await harness.RunAsync(phase: JobPhase.TranslateAndWrite);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(KSubMaker.Domain.Errors.ErrorCodes.TranscriptionFailed);
    }

    // -----------------------------------------------------------------------
    // partial translation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Only_the_missing_ids_are_re_translated_after_a_truncated_checkpoint()
    {
        var harness = new Harness();

        await harness.RunAsync();

        harness.Checkpoints.PeekPartial(harness.Job.Id).Keys.Should().BeEquivalentTo(Enumerable.Range(1, 9));

        // Simulate a crash after the first two batches of three had been persisted.
        harness.Checkpoints.TruncatePartialTranslation(harness.Job.Id, id => id <= 6);

        harness.Translator.RequestedBatches.Clear();
        harness.Translator.AllRequestedIds.Clear();

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        harness.Translator.AllRequestedIds.Should().Equal(7, 8, 9);
        harness.Translator.RequestedBatches.Should().ContainSingle();
    }

    [Fact]
    public async Task Nothing_is_re_translated_when_the_checkpoint_is_complete()
    {
        var harness = new Harness();

        await harness.RunAsync();
        harness.Translator.AllRequestedIds.Clear();

        await harness.RunAsync();

        harness.Translator.AllRequestedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_missing_id_only_costs_one_more_request()
    {
        var harness = new Harness();

        await harness.RunAsync();
        harness.Checkpoints.TruncatePartialTranslation(harness.Job.Id, id => id != 5);

        harness.Translator.AllRequestedIds.Clear();

        await harness.RunAsync();

        harness.Translator.AllRequestedIds.Should().Equal(5);
    }

    [Fact]
    public async Task The_partial_translation_is_persisted_after_every_batch()
    {
        var harness = new Harness();

        await harness.RunAsync();

        // 9 segments in batches of 3 => three saves, and the final map holds every id.
        harness.Checkpoints.PeekPartial(harness.Job.Id).Should().HaveCount(9);
        harness.Translator.RequestedBatches.Should().HaveCount(3);
        harness.Translator.RequestedBatches[0].Should().Equal(1, 2, 3);
        harness.Translator.RequestedBatches[1].Should().Equal(4, 5, 6);
        harness.Translator.RequestedBatches[2].Should().Equal(7, 8, 9);
    }

    [Fact]
    public async Task A_malformed_translation_response_is_retried_for_the_missing_ids_only()
    {
        var harness = new Harness();
        harness.Translator.DropOnFirstAttempt.Add(2);

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        harness.Translator.RequestedBatches[0].Should().Equal(1, 2, 3);
        harness.Translator.RequestedBatches[1].Should().ContainSingle("only the dropped id is re-requested")
            .Which.Should().Be(2);
    }

    // -----------------------------------------------------------------------
    // checkpoint invalidation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_changed_source_file_size_invalidates_the_checkpoint()
    {
        var logger = new CapturingLogger<InProcessJobProcessor>();
        var harness = new Harness(logger);

        await harness.RunAsync();

        // The user replaced the video: same path, different content.
        harness.Job.FileSize = OriginalSize + 1;

        await harness.RunAsync();

        logger.ContainsMessage("원본 파일이 변경되어 체크포인트를 폐기합니다").Should().BeTrue();
    }

    [Fact]
    public async Task A_changed_last_write_time_invalidates_the_checkpoint()
    {
        var logger = new CapturingLogger<InProcessJobProcessor>();
        var harness = new Harness(logger);

        await harness.RunAsync();
        harness.Job.LastWriteTimeUtc = LastWrite.AddSeconds(1);

        await harness.RunAsync();

        logger.ContainsMessage("원본 파일이 변경되어 체크포인트를 폐기합니다").Should().BeTrue();
    }

    [Fact]
    public async Task An_unchanged_source_file_keeps_the_checkpoint()
    {
        var logger = new CapturingLogger<InProcessJobProcessor>();
        var harness = new Harness(logger);

        await harness.RunAsync();
        await harness.RunAsync();

        logger.ContainsMessage("원본 파일이 변경되어 체크포인트를 폐기합니다").Should().BeFalse();
    }

    [Fact]
    public async Task A_changed_source_file_forces_the_expensive_stages_to_run_again()
    {
        var harness = new Harness();

        await harness.RunAsync();
        harness.Job.FileSize = OriginalSize + 1;

        await harness.RunAsync();

        harness.Extractor.Calls.Should().Be(2, "the audio belongs to the previous version of the file");
        harness.Transcriber.Calls.Should().Be(2, "the transcript belongs to the previous version of the file");
    }

    // -----------------------------------------------------------------------
    // clearing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Clearing_the_checkpoint_makes_the_whole_pipeline_run_again()
    {
        var harness = new Harness();

        await harness.RunAsync();
        await harness.Checkpoints.ClearAsync(harness.Job.Id);
        harness.FileSystem.Delete(Path.Combine(harness.Paths.JobCacheDirectory(harness.Job.Id), "audio.wav"));

        await harness.RunAsync();

        harness.Extractor.Calls.Should().Be(2);
        harness.Transcriber.Calls.Should().Be(2);
    }

    [Fact]
    public async Task The_checkpoint_records_the_source_identity_used_for_invalidation()
    {
        var harness = new Harness();

        await harness.RunAsync();

        var checkpoint = harness.Checkpoints.Peek(harness.Job.Id);

        checkpoint.Should().NotBeNull();
        checkpoint!.JobId.Should().Be(harness.Job.Id);
        checkpoint.VideoPath.Should().Be(VideoPath);
        checkpoint.SourceFileSize.Should().Be(OriginalSize);
        checkpoint.SourceLastWriteUtc.Should().Be(LastWrite);
    }

    [Fact]
    public async Task An_empty_transcript_fails_instead_of_writing_an_empty_subtitle_file()
    {
        var harness = new Harness();
        await harness.Checkpoints.SaveTranscriptionAsync(harness.Job.Id, new TranscriptionResult
        {
            SourceLanguage = "en",
            Segments = []
        });

        var result = await harness.RunAsync(phase: JobPhase.TranslateAndWrite);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(KSubMaker.Domain.Errors.ErrorCodes.TranscriptionFailed);
        harness.Writer.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_is_reported_rather_than_treated_as_a_failure()
    {
        var harness = new Harness();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await harness.Processor.ProcessAsync(
            harness.Job,
            DefaultSettings(),
            JobPhase.Full,
            new Progress<JobProgress>(_ => { }),
            cts.Token);

        result.Cancelled.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(KSubMaker.Domain.Errors.ErrorCodes.OperationCancelled);
    }
}
