namespace KSubMaker.Domain.Hardware;

/// <summary>A single detected NVIDIA GPU.</summary>
public sealed record GpuInfo
{
    public required string Name { get; init; }
    public int Index { get; init; }
    public long TotalVramBytes { get; init; }
    public long FreeVramBytes { get; init; }
    public string? DriverVersion { get; init; }
    public string? ComputeCapability { get; init; }

    public double TotalVramGb => TotalVramBytes / 1024d / 1024d / 1024d;
    public double FreeVramGb => FreeVramBytes / 1024d / 1024d / 1024d;
}

/// <summary>
/// Korean warning texts the local detector produces about CUDA.
///
/// They live here rather than inline in the detector because <see cref="HardwareProfile.MergeWorkerReport"/>
/// has to be able to retract them: once the worker proves CUDA works, leaving
/// "CUDA 런타임을 찾지 못했습니다" on the settings screen would contradict the very answer that
/// replaced it.
/// </summary>
public static class HardwareWarnings
{
    public const string CudaRuntimeNotFound =
        "CUDA 런타임을 찾지 못했습니다. GPU 가속을 사용할 수 없어 CPU 모드로 동작합니다.";

    public const string CudaRuntimeLoadFailed =
        "CUDA 런타임 라이브러리를 불러오지 못했습니다. NVIDIA 드라이버를 최신 버전으로 업데이트해 주세요.";

    /// <summary>Warnings that stop being true the moment the worker reports CUDA as usable.</summary>
    public static readonly IReadOnlyList<string> RetractedWhenCudaWorks =
    [
        CudaRuntimeNotFound,
        CudaRuntimeLoadFailed
    ];

    /// <summary>
    /// Shown when a GPU and its driver are fine but cuBLAS / cuDNN are not installed. Formatted with
    /// the missing file names, because "CUDA를 사용할 수 없습니다" sends the user to the driver page —
    /// the one thing that is already correct.
    /// </summary>
    public static string CudaSupportLibrariesMissing(IEnumerable<string> libraries)
    {
        var named = string.Join(", ", libraries.Where(name => !string.IsNullOrWhiteSpace(name)));
        var subject = string.IsNullOrEmpty(named) ? "cuBLAS 12 / cuDNN 9" : named;

        return $"GPU는 정상이지만 CUDA 지원 라이브러리({subject})가 없어 CPU 모드로 동작합니다. " +
               "scripts\\build-worker.ps1로 워커를 다시 설치하세요.";
    }
}

/// <summary>
/// The part of the machine only the Python worker can answer for.
///
/// The C# detector can see that a driver is installed; only the worker can import CTranslate2 and
/// ask it how many CUDA devices it can actually open, and only it can report per-GPU *free* VRAM at
/// the moment a model would be loaded.
/// </summary>
public sealed record WorkerHardwareReport
{
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    /// <summary>
    /// Authoritative: a CUDA device exists <b>and</b> the support libraries load. The worker
    /// computes the conjunction; the host does not re-derive it, so an older (1.1) worker that only
    /// knows about the device half still gets its answer respected.
    /// </summary>
    public bool CudaAvailable { get; init; }

    /// <summary>
    /// Protocol 1.2. CTranslate2 opened a CUDA device — the driver works. On its own this is not
    /// enough to run anything, which is the whole point of splitting it out.
    /// </summary>
    public bool CudaDeviceDetected { get; init; }

    /// <summary>
    /// Protocol 1.2. cuBLAS (CUDA 12) and cuDNN 9 loaded. Defaults to true so a 1.1 worker, which
    /// never sends the field, is not misread as "libraries missing".
    /// </summary>
    public bool CudaLibrariesAvailable { get; init; } = true;

    /// <summary>Protocol 1.2. Support libraries that failed to load, e.g. <c>cublas64_12.dll</c>.</summary>
    public IReadOnlyList<string> MissingCudaLibraries { get; init; } = [];

    public string? CudaVersion { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Everything the recommendation policy needs to know about the machine.
/// Produced by the platform detector on the C# side and enriched by the Python worker
/// (which is the only component that can honestly answer "is CUDA usable by CTranslate2?").
/// </summary>
public sealed record HardwareProfile
{
    public static readonly HardwareProfile Unknown = new();

    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    public bool HasNvidiaGpu => Gpus.Count > 0;

    /// <summary>True only when a CUDA runtime that the inference stack can actually use was found.</summary>
    public bool CudaAvailable { get; init; }

    /// <summary>
    /// A CUDA device is visible to the inference runtime. Can be true while
    /// <see cref="CudaAvailable"/> is false — that is exactly the "driver fine, cuBLAS missing" case.
    /// </summary>
    public bool CudaDeviceDetected { get; init; }

    /// <summary>cuBLAS 12 / cuDNN 9 loaded. True by default: only the worker can disprove it.</summary>
    public bool CudaLibrariesAvailable { get; init; } = true;

    /// <summary>Support libraries the worker could not load, e.g. <c>cublas64_12.dll</c>.</summary>
    public IReadOnlyList<string> MissingCudaLibraries { get; init; } = [];

    /// <summary>
    /// True when a GPU is present and its driver works, but the CUDA support libraries are not
    /// installed — the one state where "GPU를 못 씁니다" would send the user to the wrong fix.
    /// </summary>
    public bool CudaBlockedByMissingLibraries =>
        CudaDeviceDetected && !CudaLibrariesAvailable;

    public string? CudaVersion { get; init; }

    public string CpuName { get; init; } = "알 수 없음";
    public int LogicalCoreCount { get; init; } = Environment.ProcessorCount;
    public long TotalRamBytes { get; init; }
    public long AvailableRamBytes { get; init; }
    public long FreeDiskBytes { get; init; }
    public string? DiskRoot { get; init; }

    /// <summary>Non-fatal problems encountered while probing (shown in the settings screen).</summary>
    public IReadOnlyList<string> DetectionWarnings { get; init; } = [];

    public GpuInfo? PrimaryGpu => Gpus.Count == 0
        ? null
        : Gpus.OrderByDescending(g => g.TotalVramBytes).First();

    public double PrimaryVramGb => PrimaryGpu?.TotalVramGb ?? 0d;
    public double TotalRamGb => TotalRamBytes / 1024d / 1024d / 1024d;
    public double FreeDiskGb => FreeDiskBytes / 1024d / 1024d / 1024d;

    /// <summary>
    /// Folds the worker's answer over this locally-detected profile.
    ///
    /// Who wins what, and why:
    /// <list type="bullet">
    /// <item><b>CUDA availability</b> — the worker, always. It is the process that will actually load
    /// the model, so a driver-only "yes" from the host must not survive its "no", and its "yes" must
    /// be able to correct a host false negative (an app launched without CUDA_PATH on the
    /// environment). Since protocol 1.2 it also reports <i>why</i>: a device can be present while the
    /// cuBLAS/cuDNN libraries are not, and that state needs a different fix from "no GPU".</item>
    /// <item><b>Free VRAM</b> — the worker, per GPU index. Name, total VRAM, driver and compute
    /// capability stay local: nvidia-smi already reported them and the worker only repeats them.</item>
    /// <item><b>CPU / RAM / disk</b> — local, untouched. The worker has nothing to add.</item>
    /// </list>
    /// Pure: it returns a new profile and mutates nothing, so the merge can be unit tested against a
    /// synthetic report without a worker.
    /// </summary>
    public HardwareProfile MergeWorkerReport(WorkerHardwareReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return this with
        {
            Gpus = MergeGpus(Gpus, report.Gpus),
            CudaAvailable = report.CudaAvailable,
            CudaDeviceDetected = report.CudaDeviceDetected,
            CudaLibrariesAvailable = report.CudaLibrariesAvailable,
            MissingCudaLibraries = report.MissingCudaLibraries,
            CudaVersion = string.IsNullOrWhiteSpace(report.CudaVersion) ? CudaVersion : report.CudaVersion,
            DetectionWarnings = MergeWarnings(DetectionWarnings, report.Warnings, report.CudaAvailable)
        };
    }

    private static IReadOnlyList<GpuInfo> MergeGpus(
        IReadOnlyList<GpuInfo> local,
        IReadOnlyList<GpuInfo> fromWorker)
    {
        if (local.Count == 0)
        {
            return fromWorker;
        }

        if (fromWorker.Count == 0)
        {
            return local;
        }

        // Matched on index, which is nvidia-smi's own ordering on both sides. A GPU the worker did
        // not report keeps its locally-measured free VRAM rather than being zeroed.
        var byIndex = new Dictionary<int, GpuInfo>(fromWorker.Count);
        foreach (var gpu in fromWorker)
        {
            byIndex[gpu.Index] = gpu;
        }

        var merged = new List<GpuInfo>(local.Count);
        foreach (var gpu in local)
        {
            merged.Add(byIndex.TryGetValue(gpu.Index, out var reported)
                ? gpu with { FreeVramBytes = reported.FreeVramBytes }
                : gpu);
        }

        return merged;
    }

    private static IReadOnlyList<string> MergeWarnings(
        IReadOnlyList<string> local,
        IReadOnlyList<string> fromWorker,
        bool cudaAvailable)
    {
        var merged = new List<string>(local.Count + fromWorker.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var warning in local)
        {
            if (cudaAvailable && HardwareWarnings.RetractedWhenCudaWorks.Contains(warning, StringComparer.Ordinal))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(warning) && seen.Add(warning))
            {
                merged.Add(warning);
            }
        }

        foreach (var warning in fromWorker)
        {
            if (!string.IsNullOrWhiteSpace(warning) && seen.Add(warning))
            {
                merged.Add(warning);
            }
        }

        return merged;
    }
}
