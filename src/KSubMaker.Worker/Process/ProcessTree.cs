using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SysProcess = System.Diagnostics.Process;

namespace KSubMaker.Worker.Process;

/// <summary>
/// Process-tree termination helpers.
///
/// Why this exists: the Python worker spawns FFmpeg (and, for the LLM engine, llama.cpp). If the WPF
/// app is killed from Task Manager, or crashes, those grandchildren would normally survive and keep
/// holding the video file and the GPU. The requirement "UI 종료 시 Python/FFmpeg 자식 프로세스가
/// 남지 않는다" is satisfied by two independent mechanisms:
///
///  1. <see cref="WindowsJobObject"/> — the kernel kills every process in the job the moment the last
///     handle to the job closes. That covers the crash / hard-kill case, where no managed code runs.
///  2. <see cref="KillTree(SysProcess)"/> — the cooperative path used on normal shutdown.
/// </summary>
public static class ProcessTree
{
    /// <summary>
    /// Kills <paramref name="p"/> and everything it spawned. Never throws: it is called from finally
    /// blocks and from Dispose, where an exception would mask the original failure.
    /// </summary>
    public static void KillTree(SysProcess p) => KillTree(p, null);

    /// <inheritdoc cref="KillTree(SysProcess)"/>
    public static void KillTree(SysProcess? process, ILogger? logger)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            logger?.LogInformation("Worker 프로세스 트리를 강제 종료했습니다. (PID {Pid})", SafePid(process));
        }
        catch (InvalidOperationException)
        {
            // Already exited, or never started. Nothing to do.
        }
        catch (NotSupportedException ex)
        {
            logger?.LogWarning(ex, "프로세스 트리 종료를 지원하지 않는 환경입니다.");
        }
        catch (Win32Exception ex)
        {
            logger?.LogWarning(ex, "프로세스 트리 종료에 실패했습니다. (PID {Pid})", SafePid(process));
        }
        catch (AggregateException ex)
        {
            // Kill(entireProcessTree) aggregates per-child failures; a child that exited on its own
            // between enumeration and kill is normal and must not propagate.
            logger?.LogDebug(ex, "일부 자식 프로세스 종료에 실패했습니다. (PID {Pid})", SafePid(process));
        }
    }

    private static string SafePid(SysProcess process)
    {
        try
        {
            return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }
}

/// <summary>
/// A Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
///
/// Every process assigned to it dies when this object is disposed <b>or</b> when the host process
/// dies for any reason, because the kernel closes the handle on our behalf. That is the only way to
/// guarantee no orphaned Python/FFmpeg processes after a hard kill of the UI.
///
/// On non-Windows this is an inert no-op so the same code compiles and runs on Linux/CI.
/// </summary>
public sealed class WindowsJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x0000_2000;

    private readonly ILogger? _logger;
    private readonly SafeJobObjectHandle? _handle;
    private bool _disposed;

    public WindowsJobObject(ILogger? logger = null)
    {
        _logger = logger;

        if (!OperatingSystem.IsWindows())
        {
            IsSupported = false;
            _logger?.LogDebug("Windows가 아니므로 Job Object를 사용하지 않습니다. (자식 프로세스는 KillTree로만 정리됩니다)");
            return;
        }

        _handle = TryCreate(logger);
        IsSupported = _handle is { IsInvalid: false };
    }

    /// <summary>False on non-Windows, or when the job object could not be created.</summary>
    public bool IsSupported { get; }

    /// <summary>
    /// Puts <paramref name="process"/> (and, by inheritance, everything it spawns) into the job.
    /// Returns false when the platform does not support it — the caller then relies on
    /// <see cref="ProcessTree.KillTree(SysProcess)"/> alone.
    /// </summary>
    public bool TryAssign(SysProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (_disposed || !IsSupported || _handle is null || _handle.IsInvalid || !OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var handle = process.Handle;
            if (NativeMethods.AssignProcessToJobObject(_handle, handle))
            {
                _logger?.LogDebug("Worker 프로세스를 Job Object에 할당했습니다.");
                return true;
            }

            _logger?.LogWarning(
                "Job Object 할당에 실패했습니다. (Win32 오류 {Error}) 자식 프로세스는 종료 시 KillTree로 정리됩니다.",
                Marshal.GetLastWin32Error());
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _logger?.LogWarning(ex, "Job Object 할당 중 오류가 발생했습니다.");
            return false;
        }
    }

    /// <summary>Closing the handle is what kills the assigned processes; that is the whole point.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private static SafeJobObjectHandle? TryCreate(ILogger? logger)
    {
        try
        {
            var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                logger?.LogWarning("Job Object 생성에 실패했습니다. (Win32 오류 {Error})", Marshal.GetLastWin32Error());
                handle.Dispose();
                return null;
            }

            var information = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var length = (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();

            if (!NativeMethods.SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref information, length))
            {
                logger?.LogWarning(
                    "Job Object 설정에 실패했습니다. (Win32 오류 {Error})", Marshal.GetLastWin32Error());
                handle.Dispose();
                return null;
            }

            return handle;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            logger?.LogWarning(ex, "Job Object를 사용할 수 없습니다.");
            return null;
        }
    }
}

/// <summary>SafeHandle wrapper so the job handle is released even if <c>Dispose</c> is never called.</summary>
internal sealed class SafeJobObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobObjectHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}

#pragma warning disable SYSLIB1054 // DllImport is intentional here: the structs are blittable and the
                                   // generated-marshalling variant buys nothing for four calls.
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeJobObjectHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        SafeJobObjectHandle hJob,
        int jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(SafeJobObjectHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);
}
#pragma warning restore SYSLIB1054
