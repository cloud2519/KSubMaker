using FluentAssertions;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Jobs;
using KSubMaker.Domain.Settings;
using KSubMaker.Infrastructure.Paths;
using KSubMaker.IntegrationTests.Infrastructure;
using KSubMaker.Worker;
using KSubMaker.Worker.Process;
using KSubMaker.Worker.Processing;
using KSubMaker.WorkerProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KSubMaker.IntegrationTests.Worker;

/// <summary>
/// <see cref="WorkerJobProcessor.PrefetchAudioAsync"/> — what the prefetch lane actually sends.
///
/// <para>The bug this file exists for: the lane starts at the same instant the pump does, but the
/// pump needs seconds to boot CPython and finish the handshake. The first version bailed out on
/// "worker not running yet" and the lane, seeing a plain false, walked on to the next file and
/// never came back. So the first <c>depth</c> files were silently never prefetched — and raising
/// the setting made it worse, not better, which is exactly how it was noticed.</para>
/// </summary>
public sealed class WorkerPrefetchTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TempWorkspace _workspace = new("ksubmaker-prefetch");

    public void Dispose() => _workspace.Dispose();

    private sealed class PrefetchClient : IWorkerClient
    {
        public List<WorkerCommand> Sent { get; } = [];

        public bool IsRunning { get; set; }

        /// <summary>Set to have the worker "come up" this long after the first prefetch attempt.</summary>
        public TimeSpan? ComesUpAfter { get; set; }

        /// <summary>Thrown instead of answering, to model an old worker or a busy lane.</summary>
        public Exception? Fault { get; set; }

        /// <summary>What the worker reports in <c>skipped</c>: true means "the wav was already good".</summary>
        public bool Skipped { get; set; }

        private DateTimeOffset? _firstAsk;

        public event EventHandler<WorkerEvent>? EventReceived;

        public event EventHandler<WorkerExitedEventArgs>? Exited;

        public Task<ReadyEvent> StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.FromResult(new ReadyEvent { WorkerVersion = "fake", PythonVersion = "3.11.0" });
        }

        public Task SendAsync(WorkerCommand command, CancellationToken cancellationToken = default)
        {
            Sent.Add(command);
            _ = EventReceived;
            _ = Exited;
            return Task.CompletedTask;
        }

        public Task<TEvent> RequestAsync<TEvent>(WorkerCommand command, CancellationToken cancellationToken = default)
            where TEvent : WorkerEvent
        {
            Sent.Add(command);

            if (Fault is not null)
            {
                return Task.FromException<TEvent>(Fault);
            }

            var completed = new CompletedEvent
            {
                JobId = (command as ExtractAudioCommand)?.JobId,
                RequestId = command.RequestId,
                OutputPath = string.Empty,
                CueCount = 0,
                Skipped = Skipped
            };

            return Task.FromResult((TEvent)(WorkerEvent)completed);
        }

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Polled by the processor; flips IsRunning once the configured delay has passed.</summary>
        public void Tick()
        {
            if (ComesUpAfter is not { } delay)
            {
                return;
            }

            _firstAsk ??= DateTimeOffset.UtcNow;

            if (DateTimeOffset.UtcNow - _firstAsk >= delay)
            {
                IsRunning = true;
            }
        }
    }

    private WorkerJobProcessor NewProcessor(PrefetchClient client) => new(
        client,
        new AppPaths(Path.Combine(_workspace.Root, "appdata")),
        Options.Create(new WorkerOptions()),
        NullLogger<WorkerJobProcessor>.Instance);

    private static Job NewJob(string id = "job-1") => new()
    {
        Id = id,
        VideoPath = "/videos/" + id + ".mkv",
        FileName = id + ".mkv",
        DurationSeconds = 60d
    };

    [Fact]
    public async Task A_prefetch_sends_an_extract_audio_command()
    {
        var client = new PrefetchClient { IsRunning = true };

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(NewJob(), new AppSettings(), CancellationToken.None)
            .WaitAsync(Timeout);

        outcome.Should().Be(AudioPrefetchOutcome.Extracted);

        var command = client.Sent.OfType<ExtractAudioCommand>().Should().ContainSingle().Subject;
        command.JobId.Should().Be("job-1");
        command.VideoPath.Should().Be("/videos/job-1.mkv");
        command.SourceMode.Should().Be(SourceModes.Audio);
    }

    [Fact]
    public async Task A_wav_an_earlier_run_left_behind_is_reported_as_reuse_not_extraction()
    {
        // The reason this enum exists. Reporting a two-millisecond no-op as "음성을 미리 추출했습니다"
        // is what made the logs useless for telling whether the lane was doing any work.
        var client = new PrefetchClient { IsRunning = true, Skipped = true };

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(NewJob(), new AppSettings(), CancellationToken.None)
            .WaitAsync(Timeout);

        outcome.Should().Be(AudioPrefetchOutcome.AlreadyPresent);
    }

    [Fact]
    public async Task The_prefetch_waits_for_a_worker_that_is_still_starting()
    {
        // The regression. A worker that is mid-boot must not cost this file its prefetch.
        var client = new PrefetchClient { IsRunning = false, ComesUpAfter = TimeSpan.FromMilliseconds(400) };

        var waker = Task.Run(async () =>
        {
            for (var i = 0; i < 200 && !client.IsRunning; i++)
            {
                client.Tick();
                await Task.Delay(10);
            }
        });

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(NewJob(), new AppSettings(), CancellationToken.None)
            .WaitAsync(Timeout);

        await waker;

        outcome.Should().Be(AudioPrefetchOutcome.Extracted, "the pump was bringing the worker up, not refusing to");
        client.Sent.OfType<ExtractAudioCommand>().Should().ContainSingle();
    }

    [Fact]
    public async Task The_prefetch_never_starts_the_worker_itself()
    {
        // Paying a cold CPython start for a lookahead would be backwards: with no job running there
        // is nothing to run ahead of, and the file gets extracted by the job that reaches it.
        var client = new PrefetchClient { IsRunning = false };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(NewJob(), new AppSettings(), cts.Token)
            .WaitAsync(Timeout);

        outcome.Should().Be(AudioPrefetchOutcome.NotAttempted);
        client.IsRunning.Should().BeFalse();
        client.Sent.OfType<ExtractAudioCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task An_already_cancelled_token_prefetches_nothing()
    {
        var client = new PrefetchClient { IsRunning = true };

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(NewJob(), new AppSettings(), new CancellationToken(canceled: true))
            .WaitAsync(Timeout);

        outcome.Should().Be(AudioPrefetchOutcome.NotAttempted);
        client.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_worker_that_rejects_the_command_is_not_fatal()
    {
        // A pre-1.3 worker answers PROTOCOL_ERROR, and so does a 1.3 worker whose lane is busy.
        // Both mean "not prefetched", which is a slower run rather than a broken one.
        var client = new PrefetchClient
        {
            IsRunning = true,
            Fault = new WorkerRequestFailedException(new ErrorEvent
            {
                Code = "PROTOCOL_ERROR",
                Message = "알 수 없는 명령입니다: extractAudio"
            })
        };

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(NewJob(), new AppSettings(), CancellationToken.None)
            .WaitAsync(Timeout);

        outcome.Should().Be(AudioPrefetchOutcome.NotAttempted);
    }

    [Fact]
    public async Task An_embedded_subtitle_job_is_not_prefetched()
    {
        var client = new PrefetchClient { IsRunning = true };

        var job = NewJob();
        job.SourceOverride = JobSourceOverride.EmbeddedSubtitle;
        job.SelectedSubtitleTrackIndex = 0;

        var outcome = await NewProcessor(client)
            .PrefetchAudioAsync(job, new AppSettings(), CancellationToken.None)
            .WaitAsync(Timeout);

        // Such a job never reads audio, so extracting any would be pure waste.
        outcome.Should().Be(AudioPrefetchOutcome.NotAttempted);
        client.Sent.OfType<ExtractAudioCommand>().Should().BeEmpty();
    }
}
