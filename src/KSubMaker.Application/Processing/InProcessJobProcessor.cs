using System.Diagnostics;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Application.Processing;

/// <summary>
/// Runs the full pipeline inside the host process using the injected transcriber / translator.
///
/// This is the implementation behind "Fake AI 모드" and the integration tests. It is *not* a mock:
/// audio extraction, checkpointing, translation validation, subtitle post-processing and atomic SRT
/// writing are all the real code paths. Only the two AI stages are pluggable, and in production the
/// Python worker is used instead of this class entirely.
/// </summary>
public sealed class InProcessJobProcessor(
    IAudioExtractor audioExtractor,
    ITranscriber transcriber,
    ITranslationEngine translationEngine,
    ISubtitleWriter subtitleWriter,
    ICheckpointStore checkpointStore,
    IAppPaths paths,
    IFileSystem fileSystem,
    ILogger<InProcessJobProcessor> logger) : IJobProcessor
{
    private const int MaxBatchRetries = 3;

    public string Name => "인프로세스 파이프라인";

    /// <summary>
    /// Not implemented here, and that is a deliberate no-op rather than an oversight.
    ///
    /// <para>This processor exists for "Fake AI 모드" and the integration tests. Its whole point is
    /// to run without the Python worker, and prefetching is a throughput optimisation for real
    /// multi-hour transcodes — there is nothing to win by extracting audio early for a pipeline
    /// that fabricates its transcript. Returning false is the contract's way of saying "the job
    /// will do its own extraction", which is exactly what happens.</para>
    /// </summary>
    public Task<AudioPrefetchOutcome> PrefetchAudioAsync(
        Job job,
        AppSettings settings,
        CancellationToken cancellationToken) =>
        Task.FromResult(AudioPrefetchOutcome.NotAttempted);

    public async Task<JobExecutionResult> ProcessAsync(
        Job job,
        AppSettings settings,
        JobPhase phase,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!fileSystem.FileExists(job.VideoPath))
            {
                return JobExecutionResult.Fail(
                    ErrorCodes.VideoNotFound,
                    UserFacingErrors.Describe(ErrorCodes.VideoNotFound));
            }

            var cacheDir = paths.JobCacheDirectory(job.Id);
            fileSystem.CreateDirectory(cacheDir);

            var checkpoint = await checkpointStore.LoadAsync(job.Id, cancellationToken).ConfigureAwait(false);
            checkpoint = await InvalidateAsync(checkpoint, job, cancellationToken).ConfigureAwait(false);

            TranscriptionResult? transcription = null;

            // ---- transcription -------------------------------------------------
            if (phase is JobPhase.Full or JobPhase.TranscribeOnly)
            {
                transcription = await checkpointStore
                    .LoadTranscriptionAsync(job.Id, cancellationToken)
                    .ConfigureAwait(false);

                // A null checkpoint means "nothing trustworthy on disk", so the cached transcript and
                // WAV must be treated as absent too. Writing this as `checkpoint?.CompletedStage < x`
                // would lift to false when the checkpoint is null and silently reuse artefacts from a
                // previous version of the source file.
                var transcriptionUsable = transcription is not null
                                          && checkpoint is not null
                                          && checkpoint.CompletedStage >= JobStage.Transcribing;

                if (!transcriptionUsable)
                {
                    transcription = null;
                    var audioPath = Path.Combine(cacheDir, "audio.wav");

                    var audioUsable = fileSystem.FileExists(audioPath)
                                      && checkpoint is not null
                                      && checkpoint.CompletedStage >= JobStage.ExtractingAudio;

                    if (!audioUsable)
                    {
                        Report(progress, job, JobStage.ExtractingAudio, 0);

                        await audioExtractor.ExtractAsync(
                            new AudioExtractionRequest { VideoPath = job.VideoPath, OutputWavPath = audioPath },
                            new Progress<double>(p => Report(progress, job, JobStage.ExtractingAudio, p)),
                            cancellationToken).ConfigureAwait(false);

                        await checkpointStore.SaveAsync(
                            BuildCheckpoint(job, JobStage.ExtractingAudio, audioPath, null),
                            cancellationToken).ConfigureAwait(false);
                    }

                    Report(progress, job, JobStage.Transcribing, 0);

                    transcription = await transcriber.TranscribeAsync(
                        new TranscriptionRequest
                        {
                            AudioPath = audioPath,
                            Language = settings.SourceLanguage,
                            ModelId = settings.WhisperModel,
                            ComputeType = settings.ComputeType,
                            BeamSize = settings.BeamSize,
                            VadFilter = settings.VadFilter,
                            WordTimestamps = settings.WordTimestamps,
                            ConditionOnPreviousText = settings.ConditionOnPreviousText,
                            InitialPrompt = settings.InitialPrompt,
                            DurationSeconds = job.DurationSeconds
                        },
                        new Progress<double>(p => Report(progress, job, JobStage.Transcribing, p)),
                        cancellationToken).ConfigureAwait(false);

                    // Split over-long segments while word timestamps are still available.
                    transcription = transcription with
                    {
                        Segments = SegmentSplitter.Split(
                            transcription.Segments,
                            maxChars: settings.MaxLinesPerCue * settings.MaxCharsPerLine * 2,
                            maxDurationSeconds: settings.MaxCueDurationSeconds)
                    };

                    await checkpointStore
                        .SaveTranscriptionAsync(job.Id, transcription, cancellationToken)
                        .ConfigureAwait(false);

                    await checkpointStore.SaveAsync(
                        BuildCheckpoint(job, JobStage.Transcribing, audioPath, transcription.SourceLanguage),
                        cancellationToken).ConfigureAwait(false);
                }

                if (transcription is null)
                {
                    return JobExecutionResult.Fail(
                        ErrorCodes.TranscriptionFailed,
                        UserFacingErrors.Describe(ErrorCodes.TranscriptionFailed));
                }

                progress.Report(new JobProgress
                {
                    JobId = job.Id,
                    Stage = JobStage.Transcribing,
                    StageProgress = 100,
                    OverallProgress = ProgressCalculator.Overall(JobStage.Transcribing, 100),
                    DetectedLanguage = transcription.SourceLanguage,
                    LanguageProbability = transcription.LanguageProbability
                });

                if (phase == JobPhase.TranscribeOnly)
                {
                    return new JobExecutionResult
                    {
                        Success = true,
                        SourceLanguage = transcription.SourceLanguage,
                        WhisperModel = transcription.ModelId,
                        CueCount = transcription.Segments.Count
                    };
                }
            }

            transcription ??= await checkpointStore
                .LoadTranscriptionAsync(job.Id, cancellationToken)
                .ConfigureAwait(false);

            if (transcription is null)
            {
                return JobExecutionResult.Fail(
                    ErrorCodes.TranscriptionFailed,
                    "음성 인식 결과를 찾을 수 없어 번역을 시작할 수 없습니다.");
            }

            if (transcription.Segments.Count == 0)
            {
                return JobExecutionResult.Fail(
                    ErrorCodes.TranscriptionFailed,
                    "음성에서 인식된 내용이 없습니다.");
            }

            // ---- translation ---------------------------------------------------
            var translations = new Dictionary<int, string>(
                await checkpointStore.LoadPartialTranslationAsync(job.Id, cancellationToken).ConfigureAwait(false));

            var batches = TranslationBatcher.Split(transcription.Segments, new TranslationBatchOptions
            {
                MaxItems = settings.TranslationBatchMaxItems,
                MaxChars = settings.TranslationBatchMaxChars,
                MaxSeconds = settings.TranslationBatchMaxSeconds,
                ContextItems = settings.TranslationContextLines
            });

            Report(progress, job, JobStage.Translating, 0);

            for (var i = 0; i < batches.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = batches[i];
                var pending = batch.Items.Where(item => !translations.ContainsKey(item.Id)).ToArray();

                if (pending.Length == 0)
                {
                    Report(progress, job, JobStage.Translating, (i + 1) * 100d / batches.Count);
                    continue;
                }

                var context = new TranslationContext
                {
                    SourceLanguage = transcription.SourceLanguage,
                    PrecedingContext = batch.ContextItems,
                    Style = settings.TranslationStyle,
                    Glossary = settings.Glossary
                };

                var accepted = await TranslateWithRetryAsync(pending, context, cancellationToken)
                    .ConfigureAwait(false);

                if (accepted is null)
                {
                    return JobExecutionResult.Fail(
                        ErrorCodes.InvalidTranslationResponse,
                        UserFacingErrors.Describe(ErrorCodes.InvalidTranslationResponse),
                        recoverable: true);
                }

                foreach (var (id, text) in accepted)
                {
                    translations[id] = text;
                }

                await checkpointStore
                    .SavePartialTranslationAsync(job.Id, translations, cancellationToken)
                    .ConfigureAwait(false);

                Report(progress, job, JobStage.Translating, (i + 1) * 100d / batches.Count);
            }

            // ---- output ---------------------------------------------------------
            Report(progress, job, JobStage.WritingSubtitle, 0);

            var cues = SubtitlePostProcessor.Build(
                transcription.Segments,
                translations,
                SubtitleFormattingOptions.From(settings));

            if (cues.Count == 0)
            {
                return JobExecutionResult.Fail(ErrorCodes.TranslationFailed, "번역된 자막이 없습니다.");
            }

            var desiredPath = job.OutputPath
                ?? OutputPathResolver.BuildDefaultPath(job.VideoPath, settings.OutputSuffix, settings.OutputDirectory);

            var written = await subtitleWriter
                .WriteAsync(cues, desiredPath, settings.OutputConflictPolicy, cancellationToken)
                .ConfigureAwait(false);

            Report(progress, job, JobStage.WritingSubtitle, 100);

            await checkpointStore.SaveAsync(
                BuildCheckpoint(job, JobStage.Done, null, transcription.SourceLanguage),
                cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "자막 생성 완료: {Output} (자막 {Count}개, {Elapsed:0.0}초)",
                written ?? desiredPath, cues.Count, stopwatch.Elapsed.TotalSeconds);

            return new JobExecutionResult
            {
                Success = true,
                OutputPath = written ?? desiredPath,
                CueCount = cues.Count,
                SourceLanguage = transcription.SourceLanguage,
                WhisperModel = transcription.ModelId,
                TranslationEngine = settings.TranslationEngine,
                TranslationModel = settings.TranslationModel,
                Skipped = written is null
            };
        }
        catch (OperationCanceledException)
        {
            return new JobExecutionResult { Success = false, Cancelled = true, ErrorCode = ErrorCodes.OperationCancelled };
        }
        catch (PipelineException ex)
        {
            // Already classified by the component that threw (FFmpeg, model download, …).
            // Must be caught ahead of IOException/Exception so the precise code is not lost.
            logger.LogError(ex, "파이프라인 오류 {Code}: {JobId}", ex.ErrorCode, job.Id);
            return JobExecutionResult.Fail(
                ex.ErrorCode,
                UserFacingErrors.Describe(ex.ErrorCode),
                ex.Recoverable || ErrorCodes.IsAutoRetryable(ex.ErrorCode));
        }
        catch (FileNotFoundException ex)
        {
            logger.LogError(ex, "필요한 파일을 찾을 수 없습니다: {JobId}", job.Id);
            return JobExecutionResult.Fail(ErrorCodes.VideoNotFound, UserFacingErrors.Describe(ErrorCodes.VideoNotFound));
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "파일 입출력 오류: {JobId}", job.Id);
            return JobExecutionResult.Fail(ErrorCodes.OutputWriteFailed, UserFacingErrors.Describe(ErrorCodes.OutputWriteFailed));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "작업 처리 실패: {JobId}", job.Id);
            return JobExecutionResult.Fail(ErrorCodes.Unknown, UserFacingErrors.Describe(ErrorCodes.Unknown));
        }
    }

    /// <summary>
    /// Translates one batch, re-requesting only the ids that are still missing so a single bad line
    /// does not cost the whole batch.
    ///
    /// <para>Cues with nothing to translate — <c>♪</c>, <c>…</c>, <c>！？</c>, a lone bracket pair —
    /// never reach the engine at all; see <see cref="TranslatableText"/>.</para>
    ///
    /// <para>Returns null <b>only</b> when the response cannot be trusted: ids that were never asked
    /// for, the same id twice, or a batch that came back mostly blank. A batch that merely could not
    /// translate a few lines comes back complete, with those lines' source text standing in — losing
    /// a whole job's work over one untranslatable cue is the worse trade by a wide margin.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>?> TranslateWithRetryAsync(
        IReadOnlyList<SubtitleItem> items,
        TranslationContext context,
        CancellationToken cancellationToken)
    {
        var accumulated = new Dictionary<int, string>();
        var translatable = new List<SubtitleItem>(items.Count);

        foreach (var item in items)
        {
            if (TranslatableText.HasTranslatableContent(item.Text))
            {
                translatable.Add(item);
                continue;
            }

            // Passed through verbatim, keeping the id so the cue keeps its timing.
            var source = item.Text?.Trim();
            if (!string.IsNullOrEmpty(source))
            {
                accumulated[item.Id] = source;
            }
        }

        if (translatable.Count == 0)
        {
            return accumulated;
        }

        var outstanding = translatable;
        TranslationValidationResult? last = null;
        int[]? previouslyMissing = null;

        for (var attempt = 1; attempt <= MaxBatchRetries && outstanding.Count > 0; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await translationEngine
                .TranslateAsync(outstanding, context, cancellationToken)
                .ConfigureAwait(false);

            last = TranslationValidator.Validate(outstanding, response);
            var map = TranslationValidator.ToMap(response);

            foreach (var item in outstanding)
            {
                if (map.TryGetValue(item.Id, out var text))
                {
                    accumulated[item.Id] = text;
                }
            }

            outstanding = translatable.Where(i => !accumulated.ContainsKey(i.Id)).ToList();

            if (outstanding.Count == 0)
            {
                return accumulated;
            }

            logger.LogWarning(
                "번역 응답 검증 실패(시도 {Attempt}/{Max}): {Detail}",
                attempt, MaxBatchRetries, last.Describe());

            var stillMissing = outstanding.Select(i => i.Id).ToArray();

            if (previouslyMissing is not null && previouslyMissing.AsSpan().SequenceEqual(stillMissing))
            {
                // Both engines are deterministic. An identical request has just produced an
                // identical answer, so the remaining attempts would spend the same seconds reaching
                // the same conclusion.
                logger.LogWarning(
                    "같은 id가 계속 비어 있어 재시도를 중단합니다(시도 {Attempt}): {Ids}",
                    attempt, string.Join(",", stillMissing));
                break;
            }

            previouslyMissing = stillMissing;
        }

        return DegradeOrReject(translatable, outstanding, accumulated, last);
    }

    /// <summary>
    /// Decides what a batch that never fully translated is worth: the source text for the stragglers
    /// (returned), or nothing at all (null, which fails the job with
    /// <see cref="ErrorCodes.InvalidTranslationResponse"/>).
    /// </summary>
    private IReadOnlyDictionary<int, string>? DegradeOrReject(
        IReadOnlyList<SubtitleItem> requested,
        IReadOnlyList<SubtitleItem> outstanding,
        Dictionary<int, string> accumulated,
        TranslationValidationResult? last)
    {
        var corrupt = last?.IsCorrupt == true;
        var mostlyUntranslated = TranslationValidator.IsMostlyUntranslated(outstanding.Count, requested.Count);

        if (corrupt || mostlyUntranslated)
        {
            logger.LogError(
                "번역 응답을 신뢰할 수 없어 배치를 거부합니다: {Requested}건 중 {Unusable}건 실패 ({Detail})",
                requested.Count, outstanding.Count, last?.Describe() ?? "응답 없음");

            return null;
        }

        foreach (var item in outstanding)
        {
            var source = item.Text?.Trim();
            if (!string.IsNullOrEmpty(source))
            {
                accumulated[item.Id] = source;
            }
        }

        logger.LogWarning(
            "번역되지 않은 자막 {Count}개는 원문을 그대로 사용합니다. (id {Ids})",
            outstanding.Count, string.Join(",", outstanding.Select(i => i.Id)));

        return accumulated;
    }

    private static void Report(IProgress<JobProgress> progress, Job job, JobStage stage, double stageProgress) =>
        progress.Report(new JobProgress
        {
            JobId = job.Id,
            Stage = stage,
            StageProgress = stageProgress,
            OverallProgress = ProgressCalculator.Overall(stage, stageProgress)
        });

    private JobCheckpoint BuildCheckpoint(Job job, JobStage stage, string? audioPath, string? language) => new()
    {
        JobId = job.Id,
        VideoPath = job.VideoPath,
        CompletedStage = stage,
        AudioPath = audioPath,
        DetectedLanguage = language,
        SourceFileSize = job.FileSize,
        SourceLastWriteUtc = job.LastWriteTimeUtc,
        UpdatedAtUtc = DateTime.UtcNow
    };

    /// <summary>
    /// Drops a checkpoint that belongs to a different version of the source file, and erases the
    /// artefacts that went with it. Merely returning null is not enough: the stale
    /// <c>transcription.json</c>, <c>translation.partial.json</c> and <c>audio.wav</c> would still be
    /// on disk, and the next stage would happily reuse them to caption the wrong video.
    /// </summary>
    private async Task<JobCheckpoint?> InvalidateAsync(
        JobCheckpoint? checkpoint,
        Job job,
        CancellationToken cancellationToken)
    {
        if (checkpoint is null)
        {
            return null;
        }

        if (checkpoint.SourceFileSize == job.FileSize && checkpoint.SourceLastWriteUtc == job.LastWriteTimeUtc)
        {
            return checkpoint;
        }

        logger.LogInformation("원본 파일이 변경되어 체크포인트를 폐기합니다: {JobId}", job.Id);

        try
        {
            await checkpointStore.ClearAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "오래된 체크포인트를 삭제하지 못했습니다: {JobId}", job.Id);
        }

        fileSystem.CreateDirectory(paths.JobCacheDirectory(job.Id));
        return null;
    }
}
