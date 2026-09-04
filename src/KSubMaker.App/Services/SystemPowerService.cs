using System.Diagnostics;
using System.Runtime.InteropServices;
using KSubMaker.Domain.Settings;
using Microsoft.Extensions.Logging;

namespace KSubMaker.App.Services;

/// <summary>
/// Win32 implementation of <see cref="ISystemPowerService"/>.
///
/// <para><see cref="PreventSleep"/> uses <c>SetThreadExecutionState</c>, whose hold is tied to the
/// thread that set it. It must therefore be called on a thread that outlives the run — in practice
/// the UI dispatcher thread, which <see cref="ViewModels.MainViewModel"/> marshals every queue
/// state change onto. A hold left on a pool thread would evaporate when that thread was recycled.</para>
///
/// <para>Shutdown shells out to <c>shutdown.exe</c> rather than calling <c>ExitWindowsEx</c>, which
/// would need the process to enable <c>SeShutdownPrivilege</c> by hand. Sleep and hibernate go
/// through <c>SetSuspendState</c>, which needs no privilege adjustment.</para>
/// </summary>
public sealed class SystemPowerService(ILogger<SystemPowerService> logger) : ISystemPowerService
{
    private readonly ILogger<SystemPowerService> _logger = logger;

    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    public void PreventSleep()
    {
        try
        {
            var previous = SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
            if (previous == 0)
            {
                _logger.LogDebug("절전 방지 요청이 거부되었습니다. (오류 {Error})", Marshal.GetLastWin32Error());
            }
            else
            {
                _logger.LogDebug("작업이 진행되는 동안 시스템 절전을 막습니다.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "절전 방지를 설정하지 못했습니다.");
        }
    }

    public void AllowSleep()
    {
        try
        {
            SetThreadExecutionState(ExecutionState.Continuous);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "절전 방지를 해제하지 못했습니다.");
        }
    }

    public bool Execute(PostQueueAction action)
    {
        if (action == PostQueueAction.None)
        {
            return true;
        }

        // Whatever happens next, the run is over — drop the keep-awake hold so a failed suspend does
        // not leave the machine pinned awake.
        AllowSleep();

        try
        {
            return action switch
            {
                PostQueueAction.Sleep => Suspend(hibernate: false),
                PostQueueAction.Hibernate => Suspend(hibernate: true),
                PostQueueAction.Shutdown => Shutdown(),
                _ => true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "큐 종료 후 동작을 실행하지 못했습니다: {Action}", action);
            return false;
        }
    }

    private bool Suspend(bool hibernate)
    {
        // disableWakeEvent: false keeps scheduled wake timers alive; forceCritical: false asks
        // running apps to agree, matching what the Start-menu Sleep does.
        var ok = SetSuspendState(hibernate, forceCritical: false, disableWakeEvent: false);
        if (!ok)
        {
            _logger.LogError(
                "{Mode} 전환에 실패했습니다. (오류 {Error})",
                hibernate ? "최대 절전" : "절전",
                Marshal.GetLastWin32Error());
        }

        return ok;
    }

    private bool Shutdown()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = "/s /t 0",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        if (process is null)
        {
            _logger.LogError("shutdown.exe를 시작하지 못했습니다.");
            return false;
        }

        return true;
    }
}
