using System.Globalization;
using System.Runtime.InteropServices;
using KSubMaker.Application.Abstractions;
using KSubMaker.Domain.Hardware;
using KSubMaker.Infrastructure.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace KSubMaker.Infrastructure.Hardware;

/// <summary>
/// Detects GPU / CPU / RAM / disk without WMI and without loading any deep-learning stack.
///
/// WMI is avoided on purpose: <c>System.Management</c> would force a <c>net10.0-windows</c> target
/// framework, and <c>Win32_VideoController</c> reports VRAM as a 32-bit value that wraps at 4 GB,
/// which is useless for exactly the cards this application cares about. <c>nvidia-smi</c> is the
/// vendor's own tool, ships with every driver, and reports the numbers correctly.
///
/// Nothing here throws: a machine with no NVIDIA driver, a locked-down registry or a disconnected
/// drive still produces a usable profile with the problems listed in
/// <see cref="HardwareProfile.DetectionWarnings"/>.
///
/// This detector answers everything except one question honestly. Whether CUDA is *usable* by the
/// inference stack can only be answered by the process that loads it, so the value produced here is
/// provisional and is replaced by the worker's — see <see cref="DetectCuda"/>.
/// </summary>
public sealed class WindowsHardwareDetector(
    IAppPaths paths,
    ILogger<WindowsHardwareDetector> logger) : IHardwareDetector
{
    private static readonly TimeSpan NvidiaSmiTimeout = TimeSpan.FromSeconds(10);

    private const long MiB = 1024L * 1024L;

    private readonly IAppPaths _paths = paths;
    private readonly ILogger<WindowsHardwareDetector> _logger = logger;

    public async Task<HardwareProfile> DetectAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        var gpus = await DetectGpusAsync(warnings, cancellationToken).ConfigureAwait(false);
        var (cudaAvailable, cudaVersion) = DetectCuda(gpus.Count > 0, warnings);
        var cpuName = await DetectCpuNameAsync(warnings, cancellationToken).ConfigureAwait(false);
        var (totalRam, availableRam) = await DetectMemoryAsync(warnings, cancellationToken).ConfigureAwait(false);
        var (diskRoot, freeDisk) = DetectDisk(warnings);

        return new HardwareProfile
        {
            Gpus = gpus,
            CudaAvailable = cudaAvailable,
            CudaVersion = cudaVersion,
            CpuName = cpuName,
            LogicalCoreCount = Environment.ProcessorCount,
            TotalRamBytes = totalRam,
            AvailableRamBytes = availableRam,
            FreeDiskBytes = freeDisk,
            DiskRoot = diskRoot,
            DetectionWarnings = warnings
        };
    }

    // -----------------------------------------------------------------------
    // GPU
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<GpuInfo>> DetectGpusAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var executable = LocateNvidiaSmi();
        if (executable is null)
        {
            warnings.Add("nvidia-smi를 찾을 수 없어 NVIDIA GPU 정보를 읽지 못했습니다. NVIDIA 드라이버가 설치되어 있는지 확인해 주세요.");
            _logger.LogInformation("nvidia-smi를 찾지 못했습니다. GPU 없이 진행합니다.");
            return [];
        }

        string[] arguments =
        [
            "--query-gpu=index,name,memory.total,memory.free,driver_version,compute_cap",
            "--format=csv,noheader,nounits"
        ];

        ProcessResult result;
        try
        {
            result = await ProcessRunner
                .RunAsync(executable, arguments, NvidiaSmiTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("nvidia-smi 실행에 실패하여 GPU 정보를 읽지 못했습니다.");
            _logger.LogWarning(ex, "nvidia-smi 실행에 실패했습니다: {Path}", executable);
            return [];
        }

        if (result.TimedOut)
        {
            warnings.Add("nvidia-smi가 10초 안에 응답하지 않아 GPU 감지를 중단했습니다.");
            _logger.LogWarning("nvidia-smi가 응답하지 않아 중단했습니다.");
            return [];
        }

        if (!result.Success)
        {
            warnings.Add($"nvidia-smi가 오류로 종료했습니다: {result.Tail(2)}");
            _logger.LogWarning("nvidia-smi가 오류로 종료했습니다({Exit}): {Detail}", result.ExitCode, result.Tail(2));
            return [];
        }

        var gpus = ParseNvidiaSmi(result.StandardOutput);

        if (gpus.Count == 0)
        {
            warnings.Add("nvidia-smi가 GPU를 보고하지 않았습니다.");
        }

        return gpus;
    }

    private List<GpuInfo> ParseNvidiaSmi(string csv)
    {
        var gpus = new List<GpuInfo>();

        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',');
            if (fields.Length < 4)
            {
                _logger.LogDebug("nvidia-smi 출력 형식을 해석하지 못했습니다: {Line}", line);
                continue;
            }

            var name = Clean(fields[1]);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            // --format=nounits means the memory columns are bare MiB numbers.
            gpus.Add(new GpuInfo
            {
                Index = ParseInt(fields[0]) ?? gpus.Count,
                Name = name,
                TotalVramBytes = (ParseLong(fields[2]) ?? 0L) * MiB,
                FreeVramBytes = (ParseLong(fields[3]) ?? 0L) * MiB,
                DriverVersion = fields.Length > 4 ? Clean(fields[4]) : null,
                ComputeCapability = fields.Length > 5 ? Clean(fields[5]) : null
            });
        }

        return gpus;
    }

    /// <summary>
    /// PATH first (a driver install puts nvidia-smi in System32, which is always on PATH), then the
    /// two well-known absolute locations for older drivers and for stripped-down PATH environments.
    /// </summary>
    private string? LocateNvidiaSmi()
    {
        string[] fileNames = OperatingSystem.IsWindows()
            ? ["nvidia-smi.exe"]
            : ["nvidia-smi"];

        foreach (var fileName in fileNames)
        {
            var onPath = FindOnPath(fileName);
            if (onPath is not null)
            {
                return onPath;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var candidate in WindowsFallbackLocations())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> WindowsFallbackLocations()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            yield return Path.Combine(systemRoot, "System32", "nvidia-smi.exe");
        }
    }

    private string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed PATH entry is common on Windows; skip it silently apart from a trace.
                _logger.LogTrace(ex, "PATH 항목을 사용할 수 없습니다: {Directory}", directory);
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // CUDA
    // -----------------------------------------------------------------------

    /// <summary>
    /// A best-effort answer only: this proves a CUDA driver library is loadable, not that
    /// CTranslate2 / cuDNN can actually run on it. The authoritative value comes from the Python
    /// worker's <c>detectHardware</c> reply, which <c>HardwareService</c> folds over this profile
    /// through <see cref="HardwareProfile.MergeWorkerReport"/> — as soon as the worker is up, and on
    /// demand from the settings screen's 새로 고침. Until then the recommendation is derived from
    /// this guess, which is why the two warnings below come from
    /// <see cref="HardwareWarnings"/>: the merge retracts them once the worker contradicts them.
    /// </summary>
    private (bool Available, string? Version) DetectCuda(bool hasGpu, List<string> warnings)
    {
        if (!hasGpu)
        {
            return (false, null);
        }

        var cudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        var version = ExtractCudaVersion(cudaPath);

        if (TryLoadCudaDriver())
        {
            return (true, version);
        }

        if (!string.IsNullOrWhiteSpace(cudaPath) && Directory.Exists(cudaPath))
        {
            // Toolkit present but the driver library did not load — usually a 32/64-bit mismatch or
            // a driver older than the toolkit.
            warnings.Add(HardwareWarnings.CudaRuntimeLoadFailed);
            return (false, version);
        }

        warnings.Add(HardwareWarnings.CudaRuntimeNotFound);
        return (false, version);
    }

    private bool TryLoadCudaDriver()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["nvcuda.dll"]
            : ["libcuda.so.1", "libcuda.so"];

        foreach (var candidate in candidates)
        {
            nint handle = 0;
            try
            {
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or DllNotFoundException or BadImageFormatException)
            {
                _logger.LogDebug(ex, "CUDA 드라이버 라이브러리를 불러오지 못했습니다: {Library}", candidate);
            }
            finally
            {
                if (handle != 0)
                {
                    NativeLibrary.Free(handle);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// <c>CUDA_PATH</c> looks like <c>...\CUDA\v12.4</c>; the trailing folder is the version. This is
    /// only a hint for the settings screen, never used to gate anything.
    /// </summary>
    private static string? ExtractCudaVersion(string? cudaPath)
    {
        if (string.IsNullOrWhiteSpace(cudaPath))
        {
            return null;
        }

        var leaf = Path.GetFileName(cudaPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return null;
        }

        var trimmed = leaf.TrimStart('v', 'V');
        return trimmed.Length > 0 && char.IsDigit(trimmed[0]) ? trimmed : null;
    }

    // -----------------------------------------------------------------------
    // CPU
    // -----------------------------------------------------------------------

    private async Task<string> DetectCpuNameAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = ReadCpuNameFromRegistry();
            if (!string.IsNullOrWhiteSpace(fromRegistry))
            {
                return fromRegistry;
            }

            warnings.Add("레지스트리에서 CPU 이름을 읽지 못했습니다.");
            return "알 수 없음";
        }

        var fromProc = await ReadCpuNameFromProcAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(fromProc) ? "알 수 없음" : fromProc;
    }

    private string? ReadCpuNameFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);

            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "레지스트리에서 CPU 이름을 읽지 못했습니다.");
            return null;
        }
    }

    private async Task<string?> ReadCpuNameFromProcAsync(CancellationToken cancellationToken)
    {
        const string CpuInfoPath = "/proc/cpuinfo";

        if (!File.Exists(CpuInfoPath))
        {
            return null;
        }

        try
        {
            foreach (var line in await File.ReadAllLinesAsync(CpuInfoPath, cancellationToken).ConfigureAwait(false))
            {
                // x86 uses "model name", ARM uses "Hardware" or "Model".
                if (!line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator >= 0 && separator + 1 < line.Length)
                {
                    var value = line[(separator + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "/proc/cpuinfo를 읽지 못했습니다.");
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Memory
    // -----------------------------------------------------------------------

    private async Task<(long Total, long Available)> DetectMemoryAsync(
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (TryGetWindowsMemory(out var total, out var available))
            {
                return (total, available);
            }

            warnings.Add("시스템 메모리 정보를 읽지 못했습니다.");
        }
        else
        {
            var fromProc = await ReadMemInfoAsync(cancellationToken).ConfigureAwait(false);
            if (fromProc is not null)
            {
                return fromProc.Value;
            }
        }

        // Last resort. TotalAvailableMemoryBytes is the GC's view of the machine (or of the cgroup
        // limit in a container), which is the closest honest answer available in managed code.
        var gcInfo = GC.GetGCMemoryInfo();
        var totalFallback = gcInfo.TotalAvailableMemoryBytes;

        if (totalFallback <= 0)
        {
            warnings.Add("시스템 메모리 용량을 확인하지 못했습니다.");
            return (0L, 0L);
        }

        var used = Math.Max(0L, gcInfo.MemoryLoadBytes);
        return (totalFallback, Math.Max(0L, totalFallback - used));
    }

    private bool TryGetWindowsMemory(out long total, out long available)
    {
        total = 0L;
        available = 0L;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

            if (!GlobalMemoryStatusEx(ref status))
            {
                _logger.LogDebug(
                    "GlobalMemoryStatusEx가 실패했습니다. 오류 코드 {Error}", Marshal.GetLastWin32Error());
                return false;
            }

            total = (long)Math.Min(status.TotalPhys, long.MaxValue);
            available = (long)Math.Min(status.AvailPhys, long.MaxValue);
            return total > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "kernel32의 GlobalMemoryStatusEx를 호출하지 못했습니다.");
            return false;
        }
    }

    private async Task<(long Total, long Available)?> ReadMemInfoAsync(CancellationToken cancellationToken)
    {
        const string MemInfoPath = "/proc/meminfo";

        if (!File.Exists(MemInfoPath))
        {
            return null;
        }

        try
        {
            long total = 0, available = 0;

            foreach (var line in await File.ReadAllLinesAsync(MemInfoPath, cancellationToken).ConfigureAwait(false))
            {
                // Values are in kB: "MemTotal:       32762936 kB".
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMemInfoKilobytes(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMemInfoKilobytes(line);
                }

                if (total > 0 && available > 0)
                {
                    break;
                }
            }

            return total > 0 ? (total, available) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "/proc/meminfo를 읽지 못했습니다.");
            return null;
        }
    }

    private static long ParseMemInfoKilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)
            ? kb * 1024L
            : 0L;
    }

    // -----------------------------------------------------------------------
    // Disk
    // -----------------------------------------------------------------------

    private (string? Root, long FreeBytes) DetectDisk(List<string> warnings)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_paths.Root));
            if (string.IsNullOrWhiteSpace(root))
            {
                warnings.Add("작업 폴더가 위치한 드라이브를 확인하지 못했습니다.");
                return (null, 0L);
            }

            var drive = new DriveInfo(root);

            // AvailableFreeSpace honours per-user quotas; TotalFreeSpace does not.
            return (root, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            warnings.Add("디스크 여유 공간을 확인하지 못했습니다.");
            _logger.LogWarning(ex, "디스크 정보를 읽지 못했습니다: {Root}", _paths.Root);
            return (null, 0L);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>nvidia-smi writes <c>[N/A]</c> or <c>[Not Supported]</c> for fields a card lacks.</summary>
    private static string? Clean(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0 ||
            trimmed.StartsWith('[') ||
            trimmed.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed;
    }

    private static int? ParseInt(string value) =>
        int.TryParse(Clean(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseLong(string value) =>
        long.TryParse(Clean(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    // -----------------------------------------------------------------------
    // P/Invoke — guarded by OperatingSystem.IsWindows() at every call site.
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // DllImport rather than the source-generated LibraryImport: LibraryImport emits unsafe code and
    // would force <AllowUnsafeBlocks> on for the whole assembly, which is a poor trade for one call
    // into kernel32 that marshals a blittable struct.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
