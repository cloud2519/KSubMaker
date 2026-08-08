using KSubMaker.Application.Abstractions;
using KSubMaker.Application.Processing;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Worker.Processing;

/// <summary>
/// Picks the pipeline for the current settings.
///
/// Resolution goes through <see cref="IServiceProvider"/> rather than constructor injection so that
/// the in-process pipeline's whole dependency graph (transcriber, translator, subtitle writer, …) is
/// only built when it is actually selected — starting the fake engines on every launch would be pure
/// waste for the common case.
/// </summary>
public sealed class JobProcessorSelector(IServiceProvider services, ILogger<JobProcessorSelector> logger)
    : IJobProcessorSelector
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly ILogger<JobProcessorSelector> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IJobProcessor Select(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // "Fake AI 모드" and the fake translation engine both mean "run everything in-process with the
        // deterministic engines" — no Python, no models, no GPU.
        if (settings.FakeAiMode || settings.TranslationEngine == TranslationEngineKind.Fake)
        {
            var reason = settings.FakeAiMode ? "Fake AI 모드" : "가짜 번역 엔진";
            _logger.LogInformation("{Reason}가 선택되어 인프로세스 파이프라인을 사용합니다.", reason);
            return _services.GetRequiredService<InProcessJobProcessor>();
        }

        return _services.GetRequiredService<WorkerJobProcessor>();
    }
}
