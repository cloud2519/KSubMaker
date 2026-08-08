using FluentAssertions;
using KSubMaker.Application.Services;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Models;
using KSubMaker.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KSubMaker.UnitTests.Application;

/// <summary>
/// The per-file 자막 원본 override: how the queue records the user's choice, and what it refuses.
/// </summary>
public sealed class JobSourceOverrideTests
{
    private static (JobQueueService Queue, InMemoryJobRepository Repository, Job Job) NewQueue(
        JobStatus status = JobStatus.Pending)
    {
        var job = new Job
        {
            Id = "job-1",
            VideoPath = "/videos/애니.mkv",
            FileName = "애니.mkv",
            HasEmbeddedSubtitle = true,
            Status = status
        };

        var repository = new InMemoryJobRepository(job);

        var queue = new JobQueueService(
            repository,
            new NeverRunsProcessorSelector(),
            new RecordingCheckpointStore(),
            new HardwareService(new CpuOnlyHardwareDetector(), new ModelCatalog(), NullLogger<HardwareService>.Instance),
            NullLogger<JobQueueService>.Instance);

        return (queue, repository, job);
    }

    private static async Task<(JobQueueService Queue, InMemoryJobRepository Repository, Job Job)> LoadedQueueAsync(
        JobStatus status = JobStatus.Pending)
    {
        var harness = NewQueue(status);
        await harness.Queue.LoadAsync();
        return harness;
    }

    // -----------------------------------------------------------------------

    [Fact]
    public async Task Every_job_starts_on_the_core_path()
    {
        var (_, _, job) = await LoadedQueueAsync();

        job.SourceOverride.Should().Be(JobSourceOverride.None);
        job.HasSourceOverride.Should().BeFalse();
    }

    [Fact]
    public async Task Choosing_an_embedded_track_records_the_index_and_language()
    {
        var (queue, repository, _) = await LoadedQueueAsync();

        var applied = await queue.SetSourceOverrideAsync(
            "job-1", JobSourceOverride.EmbeddedSubtitle, subtitleTrackIndex: 3, subtitleLanguage: "ja");

        applied.Should().BeTrue();

        var job = queue.Jobs.Single();
        job.SourceOverride.Should().Be(JobSourceOverride.EmbeddedSubtitle);
        job.SelectedSubtitleTrackIndex.Should().Be(3);
        job.SelectedSubtitleLanguage.Should().Be("ja");
        job.SelectedAudioTrackIndex.Should().BeNull("an audio index is meaningless in subtitle mode");

        repository.Updated.Should().Contain("job-1", "the choice has to survive a restart");
    }

    [Fact]
    public async Task Choosing_an_audio_track_records_only_the_audio_index()
    {
        var (queue, _, _) = await LoadedQueueAsync();

        await queue.SetSourceOverrideAsync(
            "job-1",
            JobSourceOverride.Audio,
            audioTrackIndex: 2,
            subtitleTrackIndex: 9,
            subtitleLanguage: "ja");

        var job = queue.Jobs.Single();
        job.SelectedAudioTrackIndex.Should().Be(2);
        job.SelectedSubtitleTrackIndex.Should().BeNull();
        job.SelectedSubtitleLanguage.Should().BeNull();
    }

    [Fact]
    public async Task Choosing_none_clears_everything_and_restores_the_setting()
    {
        var (queue, _, _) = await LoadedQueueAsync();

        await queue.SetSourceOverrideAsync(
            "job-1", JobSourceOverride.EmbeddedSubtitle, subtitleTrackIndex: 3, subtitleLanguage: "ja");

        await queue.SetSourceOverrideAsync("job-1", JobSourceOverride.None);

        var job = queue.Jobs.Single();
        job.SourceOverride.Should().Be(JobSourceOverride.None);
        job.SelectedSubtitleTrackIndex.Should().BeNull();
        job.SelectedSubtitleLanguage.Should().BeNull();
        job.SelectedAudioTrackIndex.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("und")]
    [InlineData("UND")]
    public async Task A_missing_or_placeholder_language_tag_is_stored_as_nothing(string? language)
    {
        var (queue, _, _) = await LoadedQueueAsync();

        await queue.SetSourceOverrideAsync(
            "job-1", JobSourceOverride.EmbeddedSubtitle, subtitleTrackIndex: 0, subtitleLanguage: language);

        // "und" is the container's way of saying "unknown"; storing it would make the worker translate
        // as if "und" were a language.
        queue.Jobs.Single().SelectedSubtitleLanguage.Should().BeNull();
    }

    [Fact]
    public async Task A_language_tag_is_trimmed()
    {
        var (queue, _, _) = await LoadedQueueAsync();

        await queue.SetSourceOverrideAsync(
            "job-1", JobSourceOverride.EmbeddedSubtitle, subtitleTrackIndex: 0, subtitleLanguage: "  ja  ");

        queue.Jobs.Single().SelectedSubtitleLanguage.Should().Be("ja");
    }

    [Fact]
    public async Task A_running_job_refuses_the_change()
    {
        var (queue, _, _) = await LoadedQueueAsync(JobStatus.Transcribing);

        var applied = await queue.SetSourceOverrideAsync(
            "job-1", JobSourceOverride.EmbeddedSubtitle, subtitleTrackIndex: 1);

        applied.Should().BeFalse("the worker already has a process command built from the old value");
        queue.Jobs.Single().SourceOverride.Should().Be(JobSourceOverride.None);
    }

    [Fact]
    public async Task An_unknown_job_id_is_refused_rather_than_throwing()
    {
        var (queue, _, _) = await LoadedQueueAsync();

        (await queue.SetSourceOverrideAsync("no-such-job", JobSourceOverride.Audio)).Should().BeFalse();
    }

    [Fact]
    public async Task The_change_is_announced_so_the_grid_updates()
    {
        var (queue, _, _) = await LoadedQueueAsync();

        var changed = new List<string>();
        queue.JobChanged += (_, e) => changed.Add(e.Job.Id);

        await queue.SetSourceOverrideAsync("job-1", JobSourceOverride.Audio, audioTrackIndex: 1);

        changed.Should().Equal("job-1");
    }

    [Fact]
    public async Task A_completed_job_can_still_be_re_pointed_before_a_retry()
    {
        var (queue, _, _) = await LoadedQueueAsync(JobStatus.Completed);

        var applied = await queue.SetSourceOverrideAsync(
            "job-1", JobSourceOverride.EmbeddedSubtitle, subtitleTrackIndex: 2);

        applied.Should().BeTrue();
    }

    [Fact]
    public void Clearing_the_override_on_the_entity_resets_every_field()
    {
        var job = new Job
        {
            SourceOverride = JobSourceOverride.EmbeddedSubtitle,
            SelectedAudioTrackIndex = 1,
            SelectedSubtitleTrackIndex = 2,
            SelectedSubtitleLanguage = "ja"
        };

        job.ClearSourceOverride();

        job.SourceOverride.Should().Be(JobSourceOverride.None);
        job.SelectedAudioTrackIndex.Should().BeNull();
        job.SelectedSubtitleTrackIndex.Should().BeNull();
        job.SelectedSubtitleLanguage.Should().BeNull();
        job.HasSourceOverride.Should().BeFalse();
    }
}
