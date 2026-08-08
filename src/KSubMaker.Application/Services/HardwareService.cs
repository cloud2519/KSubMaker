using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Hardware;
using KSubMaker.Domain.Models;
using Microsoft.Extensions.Logging;

namespace KSubMaker.Application.Services;

/// <summary>How far <see cref="HardwareService.RefreshAsync(HardwareRefreshMode, CancellationToken)"/> goes.</summary>
public enum HardwareRefreshMode
{
    /// <summary>
    /// Local detection, plus the worker's answer only if the worker happens to be running already.
    /// The default: application start-up must not pay for a Python process nobody asked for.
    /// </summary>
    WorkerIfAlreadyRunning,

    /// <summary>
    /// Local detection, then start the worker if necessary to get the authoritative CUDA answer.
    /// Only for explicit user actions — the settings screen's 새로 고침 button.
    /// </summary>
    IncludeWorker
}

/// <summary>
/// Caches the detected hardware profile and the recommendation derived from it.
///
/// Detection happens in two halves. The C# detector (nvidia-smi, registry, /proc) runs first and is
/// cheap. The Python worker answers the one question the host cannot — whether CTranslate2 can open
/// a CUDA device — and its answer is folded over the local one by
/// <see cref="HardwareProfile.MergeWorkerReport"/>, after which the recommendation is recomputed and
/// <see cref="ProfileChanged"/> is raised again.
///
/// The worker half is deliberately opportunistic. At start-up it is skipped unless a worker is
/// already up, so the first paint never waits on CPython; it then happens for free the first time
/// the worker starts for another reason (see <c>WorkerJobProcessor</c>), and on demand from the
/// settings screen.
/// </summary>
public sealed class HardwareService(
    IHardwareDetector detector,
    ModelCatalog catalog,
    ILogger<HardwareService> logger,
    IWorkerHardwareProbe? workerProbe = null)
{
    private readonly IHardwareDetector _detector = detector;
    private readonly ModelCatalog _catalog = catalog;
    private readonly ILogger<HardwareService> _logger = logger;
    private readonly IWorkerHardwareProbe? _workerProbe = workerProbe;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private HardwareProfile? _profile;
    private HardwareRecommendation? _recommendation;

    /// <summary>Set once the worker's answer has been folded in, so it is not asked again per job.</summary>
    private bool _workerAnswerApplied;

    public event EventHandler<HardwareProfile>? ProfileChanged;

    /// <summary>Last detected profile, or <see cref="HardwareProfile.Unknown"/> before first detection.</summary>
    public HardwareProfile CurrentProfile => _profile ?? HardwareProfile.Unknown;

    /// <summary>True once <see cref="CurrentProfile"/> reflects the worker's authoritative answer.</summary>
    public bool HasWorkerAnswer => _workerAnswerApplied;

    public async Task<HardwareProfile> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        if (_profile is not null)
        {
            return _profile;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<HardwareRecommendation> GetRecommendationAsync(CancellationToken cancellationToken = default)
    {
        if (_recommendation is not null)
        {
            return _recommendation;
        }

        await GetProfileAsync(cancellationToken).ConfigureAwait(false);
        return _recommendation!;
    }

    public Task<HardwareProfile> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(HardwareRefreshMode.WorkerIfAlreadyRunning, cancellationToken);

    public async Task<HardwareProfile> RefreshAsync(
        HardwareRefreshMode mode,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await _detector.DetectAsync(cancellationToken).ConfigureAwait(false);

            // Local detection is a fresh start: whatever the worker said last time was about the
            // previous profile and has to be asked for again.
            _workerAnswerApplied = false;

            var report = await AskWorkerAsync(mode, cancellationToken).ConfigureAwait(false);
            if (report is not null)
            {
                profile = profile.MergeWorkerReport(report);
                _workerAnswerApplied = true;
            }

            Apply(profile, fromWorker: report is not null);
            return profile;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Re-asks the worker and merges its answer into the already-detected profile, without repeating
    /// the local probe.
    ///
    /// Called when the worker has come up for another reason. It is a no-op when the answer is
    /// already in, when no probe is wired up, or when nothing has been detected yet — in that last
    /// case the caller has not needed a profile, and forcing one here would move work onto whichever
    /// thread happened to start the worker.
    /// </summary>
    public async Task<bool> RefreshFromWorkerAsync(CancellationToken cancellationToken = default)
    {
        if (_workerProbe is null || _workerAnswerApplied || _profile is null)
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked under the lock: two jobs starting at once both reach here.
            if (_workerAnswerApplied || _profile is null)
            {
                return false;
            }

            var report = await _workerProbe
                .TryDetectAsync(startWorkerIfNeeded: false, cancellationToken)
                .ConfigureAwait(false);

            if (report is null)
            {
                return false;
            }

            _workerAnswerApplied = true;
            Apply(_profile.MergeWorkerReport(report), fromWorker: true);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<WorkerHardwareReport?> AskWorkerAsync(
        HardwareRefreshMode mode,
        CancellationToken cancellationToken)
    {
        if (_workerProbe is null)
        {
            return null;
        }

        var startIfNeeded = mode == HardwareRefreshMode.IncludeWorker;
        if (!startIfNeeded && !_workerProbe.IsWorkerRunning)
        {
            return null;
        }

        return await _workerProbe.TryDetectAsync(startIfNeeded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the profile, recomputes the recommendation and notifies. Callers hold the lock.</summary>
    private void Apply(HardwareProfile profile, bool fromWorker)
    {
        _profile = profile;
        _recommendation = HardwareRecommendationPolicy.Recommend(profile, _catalog);

        _logger.LogInformation(
            "하드웨어 감지 결과({Source}): GPU={Gpu}, VRAM={Vram:0.#}GB, CUDA={Cuda}, CPU={Cpu}, 코어={Cores}, RAM={Ram:0.#}GB",
            fromWorker ? "worker 확인 포함" : "로컬",
            profile.PrimaryGpu?.Name ?? "없음",
            profile.PrimaryVramGb,
            profile.CudaAvailable,
            profile.CpuName,
            profile.LogicalCoreCount,
            profile.TotalRamGb);

        _logger.LogInformation("권장 설정: {Rationale}", _recommendation.Rationale);

        ProfileChanged?.Invoke(this, profile);
    }
}
