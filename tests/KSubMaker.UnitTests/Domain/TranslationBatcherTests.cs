using FluentAssertions;
using KSubMaker.Domain.Subtitles;
using Xunit;

namespace KSubMaker.UnitTests.Domain;

/// <summary>Covers "번역 배치 분할": one limit at a time, context carry-over and lossless partitioning.</summary>
public sealed class TranslationBatcherTests
{
    /// <summary>Contiguous segments, <paramref name="seconds"/> long each, with a fixed text length.</summary>
    private static IReadOnlyList<TranscriptionSegment> Segments(int count, int textLength = 5, double seconds = 2d) =>
        Enumerable.Range(0, count)
            .Select(i => new TranscriptionSegment
            {
                Id = i + 1,
                Start = i * seconds,
                End = (i + 1) * seconds,
                Text = new string('가', textLength)
            })
            .ToArray();

    private static readonly TranslationBatchOptions Unlimited = new()
    {
        MaxItems = 10_000,
        MaxChars = 1_000_000,
        MaxSeconds = 1_000_000d,
        ContextItems = 0
    };

    [Fact]
    public void An_empty_transcript_produces_no_batches()
    {
        TranslationBatcher.Split([], Unlimited).Should().BeEmpty();
    }

    [Fact]
    public void Split_rejects_a_null_segment_list()
    {
        var act = () => TranslationBatcher.Split(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Everything_fits_in_one_batch_when_no_limit_is_reached()
    {
        var batches = TranslationBatcher.Split(Segments(10), Unlimited);

        batches.Should().HaveCount(1);
        batches[0].Index.Should().Be(0);
        batches[0].Segments.Should().HaveCount(10);
        batches[0].Context.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // one limit at a time
    // -----------------------------------------------------------------------

    [Fact]
    public void The_item_limit_alone_closes_a_batch()
    {
        var batches = TranslationBatcher.Split(
            Segments(10),
            Unlimited with { MaxItems = 3 });

        batches.Select(b => b.Segments.Count).Should().Equal(3, 3, 3, 1);
    }

    [Fact]
    public void The_character_limit_alone_closes_a_batch()
    {
        // 30 characters per segment, 100-character budget: three fit, the fourth opens a new batch.
        var batches = TranslationBatcher.Split(
            Segments(10, textLength: 30),
            Unlimited with { MaxChars = 100 });

        batches.Select(b => b.Segments.Count).Should().Equal(3, 3, 3, 1);
        batches.Should().OnlyContain(b => b.Segments.Sum(s => s.Text.Length) <= 100);
    }

    [Fact]
    public void The_duration_limit_alone_closes_a_batch()
    {
        // 4-second segments with a 10-second window: two fit, the third would span 12 seconds.
        var batches = TranslationBatcher.Split(
            Segments(6, seconds: 4d),
            Unlimited with { MaxSeconds = 10d });

        batches.Select(b => b.Segments.Count).Should().Equal(2, 2, 2);
        batches.Select(b => b.Segments[^1].End - b.Segments[0].Start)
            .Should().OnlyContain(span => span <= 10d);
    }

    [Fact]
    public void The_item_limit_has_a_floor_of_one_so_a_zero_never_produces_empty_batches()
    {
        var batches = TranslationBatcher.Split(Segments(3), Unlimited with { MaxItems = 0 });

        batches.Should().HaveCount(3);
        batches.Should().OnlyContain(b => b.Segments.Count == 1);
    }

    [Fact]
    public void A_single_oversized_segment_still_gets_its_own_batch_rather_than_being_dropped()
    {
        var huge = new TranscriptionSegment
        {
            Id = 1,
            Start = 0d,
            End = 9_999d,
            Text = new string('가', 50_000)
        };

        var batches = TranslationBatcher.Split([huge], new TranslationBatchOptions
        {
            MaxItems = 30,
            MaxChars = 100,
            MaxSeconds = 10d
        });

        batches.Should().HaveCount(1);
        batches[0].Segments.Should().ContainSingle().Which.Id.Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // context
    // -----------------------------------------------------------------------

    [Fact]
    public void The_first_batch_never_has_context()
    {
        var batches = TranslationBatcher.Split(Segments(9), Unlimited with { MaxItems = 3, ContextItems = 2 });

        batches[0].Context.Should().BeEmpty();
        batches[0].ContextItems.Should().BeEmpty();
    }

    [Fact]
    public void Context_comes_from_the_immediately_preceding_batch_only()
    {
        var batches = TranslationBatcher.Split(Segments(9), Unlimited with { MaxItems = 3, ContextItems = 2 });

        batches.Should().HaveCount(3);

        batches[1].Context.Select(s => s.Id).Should().Equal(2, 3);
        batches[2].Context.Select(s => s.Id).Should().Equal(5, 6);
    }

    [Fact]
    public void Context_is_capped_at_the_configured_number_of_lines()
    {
        var batches = TranslationBatcher.Split(Segments(12), Unlimited with { MaxItems = 4, ContextItems = 3 });

        batches.Skip(1).Should().OnlyContain(b => b.Context.Count == 3);
    }

    [Fact]
    public void Context_shorter_than_the_previous_batch_is_taken_from_its_tail()
    {
        var batches = TranslationBatcher.Split(Segments(4), Unlimited with { MaxItems = 3, ContextItems = 10 });

        // The previous batch only had three segments, so all three become context.
        batches[1].Context.Select(s => s.Id).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Context_can_be_switched_off_entirely()
    {
        var batches = TranslationBatcher.Split(Segments(9), Unlimited with { MaxItems = 3, ContextItems = 0 });

        batches.Should().OnlyContain(b => b.Context.Count == 0);
    }

    [Fact]
    public void Context_is_never_part_of_the_items_that_must_be_translated()
    {
        var batches = TranslationBatcher.Split(Segments(9), Unlimited with { MaxItems = 3, ContextItems = 2 });

        foreach (var batch in batches)
        {
            var itemIds = batch.Items.Select(i => i.Id).ToHashSet();
            var contextIds = batch.ContextItems.Select(i => i.Id).ToHashSet();

            itemIds.Overlaps(contextIds).Should().BeFalse();
        }
    }

    // -----------------------------------------------------------------------
    // partitioning invariants
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1, 3, 100, 10d)]
    [InlineData(7, 3, 100, 10d)]
    [InlineData(50, 4, 90, 7d)]
    [InlineData(97, 30, 2500, 180d)]
    [InlineData(200, 1, 50, 5d)]
    public void Batching_never_loses_reorders_or_duplicates_a_segment(
        int count,
        int maxItems,
        int maxChars,
        double maxSeconds)
    {
        var segments = Segments(count, textLength: 17, seconds: 3d);

        var batches = TranslationBatcher.Split(segments, new TranslationBatchOptions
        {
            MaxItems = maxItems,
            MaxChars = maxChars,
            MaxSeconds = maxSeconds,
            ContextItems = 3
        });

        var flattened = batches.SelectMany(b => b.Segments).ToArray();

        flattened.Select(s => s.Id).Should().Equal(segments.Select(s => s.Id));
        flattened.Should().OnlyHaveUniqueItems();
        batches.Should().OnlyContain(b => b.Segments.Count > 0);
    }

    [Fact]
    public void Batch_indexes_are_contiguous_from_zero()
    {
        var batches = TranslationBatcher.Split(Segments(20), Unlimited with { MaxItems = 3 });

        batches.Select(b => b.Index).Should().Equal(Enumerable.Range(0, batches.Count));
    }

    [Fact]
    public void Items_project_the_segment_id_and_text_without_timings()
    {
        var batches = TranslationBatcher.Split(Segments(2), Unlimited);

        batches[0].Items.Should().Equal(
            new SubtitleItem(1, new string('가', 5)),
            new SubtitleItem(2, new string('가', 5)));
    }

    [Fact]
    public void Default_options_match_the_documented_defaults()
    {
        var options = new TranslationBatchOptions();

        options.MaxItems.Should().Be(30);
        options.MaxChars.Should().Be(2500);
        options.MaxSeconds.Should().Be(180d);
        options.ContextItems.Should().Be(3);
    }
}
