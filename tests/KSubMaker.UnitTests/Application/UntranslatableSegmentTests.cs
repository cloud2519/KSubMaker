using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Processing;
using KSubMaker.Domain.Errors;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using KSubMaker.Domain.Subtitles;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// What happens to a line the translation engine will not translate.
///
/// <para>The reported failure: a Japanese film whose transcript contained a <c>♪</c> cue. NLLB
/// returned an empty string for it every single time, the retry loop re-sent the identical text
/// three times for the identical answer, and the job was failed with
/// <c>INVALID_TRANSLATION_RESPONSE</c> — throwing away 134 seconds of finished GPU work over a cue
/// that had no words in it to begin with.</para>
///
/// <para>Mirrored by <c>worker/tests/test_batching.py</c>; the shared predicate and threshold are
/// pinned across both languages by <c>TranslatableTextParityTests</c>.</para>
/// </summary>
public sealed class UntranslatableSegmentTests
{
    private const string VideoPath = "/videos/anime.mkv";
    private const long Size = 4_096L;

    private static readonly DateTime LastWrite = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Harness
    {
        public Harness(IReadOnlyList<TranscriptionSegment> segments, ITranslationEngine engine)
        {
            FileSystem = new InMemoryFileSystem()
                .AddDirectory("/videos")
                .AddFile(VideoPath, size: Size, lastWriteUtc: LastWrite);

            Writer = new RecordingSubtitleWriter(FileSystem);
            Checkpoints = new InMemoryCheckpointStore();

            Processor = new InProcessJobProcessor(
                new CountingAudioExtractor(FileSystem),
                new CountingTranscriber(segments, "ja"),
                engine,
                Writer,
                Checkpoints,
                new FakeAppPaths(),
                FileSystem,
                NullLogger<InProcessJobProcessor>.Instance);
        }

        public InMemoryFileSystem FileSystem { get; }
        public RecordingSubtitleWriter Writer { get; }
        public InMemoryCheckpointStore Checkpoints { get; }
        public InProcessJobProcessor Processor { get; }

        public Job Job { get; } = new()
        {
            Id = "anime-job",
            VideoPath = VideoPath,
            FileName = "anime.mkv",
            FileSize = Size,
            LastWriteTimeUtc = LastWrite,
            DurationSeconds = 60d
        };

        public Task<JobExecutionResult> RunAsync(AppSettings? settings = null) =>
            Processor.ProcessAsync(
                Job,
                settings ?? Settings(),
                JobPhase.Full,
                new Progress<JobProgress>(_ => { }),
                CancellationToken.None);

        /// <summary>Every written cue's text, in order.</summary>
        public IReadOnlyList<string> WrittenLines =>
            Writer.Written.SelectMany(cues => cues).Select(c => c.Text).ToArray();
    }

    /// <summary>Cue merging off, so one segment is one cue and the assertions stay legible.</summary>
    private static AppSettings Settings(int batchItems = 30) => new()
    {
        TranslationBatchMaxItems = batchItems,
        TranslationBatchMaxChars = 100_000,
        TranslationBatchMaxSeconds = 100_000,
        TranslationContextLines = 0,
        MergeShortCues = false,
        MinCueDurationSeconds = 0.1d,
        MaxCueDurationSeconds = 100d,
        OutputConflictPolicy = OutputConflictPolicy.Overwrite
    };

    private static IReadOnlyList<TranscriptionSegment> Segments(params string[] texts) =>
        texts.Select((text, i) => new TranscriptionSegment
        {
            Id = i + 1,
            Start = i * 5d,
            End = (i * 5d) + 4d,
            Text = text
        }).ToArray();

    /// <summary>A transcript of ordinary Japanese dialogue, <paramref name="count"/> lines long.</summary>
    private static IReadOnlyList<TranscriptionSegment> Dialogue(int count) =>
        Segments([.. Enumerable.Range(1, count).Select(i => $"これは{i}番目のせりふです。")]);

    // -----------------------------------------------------------------------
    // (a) nothing to translate → never reaches the engine
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Symbol_and_punctuation_only_cues_never_reach_the_engine()
    {
        var segments = Segments("♪", "こんにちは", "！？", "また明日", "…", "（）");
        var engine = new ProgrammableTranslationEngine((items, _) => items
            .Select(i => new TranslatedSubtitleItem(i.Id, "[번역] " + i.Text))
            .ToArray());

        var harness = new Harness(segments, engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        // Only the two cues with actual words have anything to translate.
        engine.AllRequestedIds.Should().Equal(2, 4);
    }

    [Fact]
    public async Task An_untranslatable_cue_still_appears_in_the_subtitle_with_its_source_text()
    {
        var segments = Segments("♪", "こんにちは", "…");
        var harness = new Harness(segments, new ProgrammableTranslationEngine((items, _) => items
            .Select(i => new TranslatedSubtitleItem(i.Id, "안녕하세요"))
            .ToArray()));

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        harness.WrittenLines.Should().Equal("♪", "안녕하세요", "…");
    }

    [Fact]
    public async Task A_batch_of_nothing_but_symbols_does_not_call_the_engine_at_all()
    {
        var engine = new ProgrammableTranslationEngine((_, _) =>
            throw new InvalidOperationException("번역 엔진이 호출되면 안 됩니다."));

        var harness = new Harness(Segments("♪", "～", "。"), engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        engine.Calls.Should().Be(0);
        harness.WrittenLines.Should().Equal("♪", "～", "。");
    }

    // -----------------------------------------------------------------------
    // (b) a residual blank degrades rather than aborting
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_deterministically_blank_translation_degrades_to_the_source_text()
    {
        // Exactly the reported shape: one cue in thirty, blank every time it is asked for.
        var segments = Dialogue(30);
        var engine = ProgrammableTranslationEngine.Blanking(3);
        var harness = new Harness(segments, engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue("one untranslatable line must not cost the whole job");
        result.ErrorCode.Should().BeNull();
        harness.WrittenLines.Should().Contain("これは3番目のせりふです。", "the source text stands in");
        harness.WrittenLines.Should().Contain("[테스트] これは4番目のせりふです。");
        harness.WrittenLines.Should().HaveCount(30, "no cue is lost");
    }

    [Fact]
    public async Task The_degraded_cue_is_checkpointed_so_a_resume_does_not_ask_for_it_again()
    {
        var harness = new Harness(Dialogue(30), ProgrammableTranslationEngine.Blanking(3));

        await harness.RunAsync();

        harness.Checkpoints.PeekPartial(harness.Job.Id).Should().ContainKey(3);
    }

    [Fact]
    public async Task Retrying_stops_as_soon_as_the_missing_ids_stop_shrinking()
    {
        var engine = ProgrammableTranslationEngine.Blanking(3);
        var harness = new Harness(Dialogue(30), engine);

        await harness.RunAsync();

        // Attempt 1 leaves {3}; attempt 2 re-asks for exactly {3} and gets exactly the same answer.
        // A third identical request would burn the same seconds for the same result.
        engine.Calls.Should().Be(2);
        engine.RequestedBatches[1].Should().Equal(3);
    }

    [Fact]
    public async Task A_batch_that_needs_all_three_attempts_still_gets_them()
    {
        // One more line comes back on each attempt, so retrying is demonstrably still helping.
        var stubborn = new[] { new HashSet<int> { 4, 5, 6 }, new HashSet<int> { 5, 6 }, new HashSet<int> { 6 } };

        var engine = new ProgrammableTranslationEngine((items, attempt) =>
        {
            var blank = stubborn[Math.Min(attempt, stubborn.Length) - 1];
            return items
                .Select(i => new TranslatedSubtitleItem(i.Id, blank.Contains(i.Id) ? string.Empty : "[테스트]"))
                .ToArray();
        });

        var harness = new Harness(Dialogue(6), engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        engine.Calls.Should().Be(3);
        harness.WrittenLines.Should().Contain("これは6番目のせりふです。");
    }

    // -----------------------------------------------------------------------
    // ...but a genuinely broken response still fails hard
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_mostly_blank_batch_fails_as_a_broken_engine()
    {
        // 9 of 10 blank is not a content quirk: half a file of untranslated Japanese is worse for
        // the user than an error they can act on.
        var engine = ProgrammableTranslationEngine.Blanking(2, 3, 4, 5, 6, 7, 8, 9, 10);
        var harness = new Harness(Dialogue(10), engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidTranslationResponse);
        result.Recoverable.Should().BeTrue();
    }

    [Fact]
    public async Task A_response_with_ids_nobody_asked_for_fails_hard()
    {
        var engine = new ProgrammableTranslationEngine((items, _) =>
            items.Select(i => new TranslatedSubtitleItem(i.Id, i.Id == 2 ? string.Empty : "[테스트]"))
                 .Append(new TranslatedSubtitleItem(9_999, "쓰레기"))
                 .ToArray());

        var harness = new Harness(Dialogue(6), engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidTranslationResponse);
    }

    [Fact]
    public async Task A_response_that_duplicates_an_id_fails_hard()
    {
        var engine = new ProgrammableTranslationEngine((items, _) =>
        {
            var reply = items
                .Select(i => new TranslatedSubtitleItem(i.Id, i.Id == 2 ? string.Empty : "[테스트]"))
                .ToList();

            // The engine answers for cue 2 twice, and both times with nothing. A response whose ids
            // do not line up with the request is the one thing re-asking cannot fix.
            reply.AddRange(items
                .Where(i => i.Id == 2)
                .Select(i => new TranslatedSubtitleItem(i.Id, string.Empty)));

            return reply;
        });

        var harness = new Harness(Dialogue(6), engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ErrorCodes.InvalidTranslationResponse);
    }

    /// <summary>
    /// An engine echoing the id it was handed back at us alongside a complete answer is untidy but
    /// harmless, and it must not be confused with corruption — the batch completed.
    /// </summary>
    [Fact]
    public async Task An_extra_id_on_an_otherwise_complete_response_does_not_fail_the_job()
    {
        var engine = new ProgrammableTranslationEngine((items, _) => items
            .Select(i => new TranslatedSubtitleItem(i.Id, "[테스트]"))
            .Append(new TranslatedSubtitleItem(9_999, "메아리"))
            .ToArray());

        var harness = new Harness(Dialogue(6), engine);

        var result = await harness.RunAsync();

        result.Success.Should().BeTrue();
        engine.Calls.Should().Be(1);
        harness.WrittenLines.Should().HaveCount(6);
    }
}
