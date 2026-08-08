using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Subtitles;

namespace KSubMaker.IntegrationTests.Infrastructure;

/// <summary>Counts how often the real extractor was invoked, without changing what it does.</summary>
public sealed class CountingAudioExtractorDecorator(IAudioExtractor inner) : IAudioExtractor
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task ExtractAsync(AudioExtractionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return inner.ExtractAsync(request, progress, cancellationToken);
    }
}

/// <summary>
/// Blocks the first transcription until it is cancelled, so a test can simulate a crash in the middle
/// of the most expensive stage. Every later call is delegated to the real fake transcriber.
/// </summary>
public sealed class GatedTranscriber(ITranscriber inner) : ITranscriber
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    /// <summary>Completes as soon as the first transcription has begun.</summary>
    public Task Started => _started.Task;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Set to false to let the first call through unhindered (used after a restart).</summary>
    public bool BlockFirstCall { get; set; } = true;

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);

        if (call == 1 && BlockFirstCall)
        {
            _started.TrySetResult();

            // Waits forever; the queue's cancellation is what ends it. No Thread.Sleep, no polling.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        _started.TrySetResult();
        return await inner.TranscribeAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }
}
