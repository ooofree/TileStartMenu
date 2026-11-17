using System.Runtime.InteropServices;
using System.Security.Principal;

namespace TileStartMenu.Core.Power;

/// <summary>
/// 电源动作执行器：直接调 Win32 API，不拼 cmd shutdown。
/// Shutdown/Restart 先 AdjustTokenPrivileges 提升 SE_SHUTDOWN_NAME；Sleep 走 SetSuspendState；
/// Lock 走 LockWorkStation；SignOut 走 ExitWindowsEx(EWX_LOGOFF)。
/// Execute 前必须先 Arm（UI 二次确认），否则拒绝。
/// </summary>
public sealed class PowerService : IPowerService
{
    private readonly object _armLock = new();
    private PowerAction _armedAction;
    private DateTime _armedAtUtc = DateTime.MinValue;

    private const uint EWX_LOGOFF = 0x00000000;
    private const uint EWX_SHUTDOWN = 0x00000001;
    private const uint EWX_REBOOT = 0x00000002;
    private const uint EWX_FORCE = 0x00000004;
    private const uint EWX_FORCEIFHUNG = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint uFlags, uint dwReason);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool bHibernate, bool bForce, bool bWakeup);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    public void Arm(PowerAction action)
    {
        lock (_armLock)
        {
            _armedAction = action;
            _armedAtUtc = DateTime.UtcNow;
        }
    }

    private bool IsArmed(PowerAction action)
    {
        lock (_armLock)
        {
            if (_armedAction != action) return false;
            if (_armedAtUtc == DateTime.MinValue) return false;
            var age = DateTime.UtcNow - _armedAtUtc;
            if (age.TotalSeconds > IPowerService.ArmExpirySeconds) return false;
            return true;
        }
    }

    public bool Execute(PowerAction action, out string? error)
    {
        error = null;
        if (!IsArmed(action))
        {
            error = $"需要在界面二次确认后再执行（{action} 未 Arm 或已超过 {IPowerService.ArmExpirySeconds}s）";
            return false;
        }
        try
        {
            switch (action)
            {
                case PowerAction.Shutdown:
                    ElevateShutdownPrivilege(out _);
                    return ExitWindowsEx(EWX_SHUTDOWN | EWX_FORCEIFHUNG, 0);
                case PowerAction.Restart:
                    ElevateShutdownPrivilege(out _);
                    return ExitWindowsEx(EWX_REBOOT | EWX_FORCEIFHUNG, 0);
                case PowerAction.Sleep:
                    return SetSuspendState(false, false, false);
                case PowerAction.Lock:
                    return LockWorkStation();
                case PowerAction.SignOut:
                    return ExitWindowsEx(EWX_LOGOFF, 0);
                default:
                    error = "非电源动作";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool DryRun(PowerAction action, out string? report)
    {
        var parts = new List<string> { $"action={action}" };
        bool ok = true;

        if (action is PowerAction.Shutdown or PowerAction.Restart)
        {
            bool elevated = ElevateShutdownPrivilege(out int err);
            // 交互式用户的 token 通常已自带 SeShutdownPrivilege，AdjustTokenPrivileges 返回 ERROR_NOT_ALL_ASSIGNED(1300) 也属正常
            parts.Add($"SE_SHUTDOWN_NAME elevate={(elevated ? "OK" : $"checked(err={err},通常交互式用户已自带权限)")}");
            // DryRun 不把权限检查失败当失败，因为 ExitWindowsEx 在交互式会话里本就允许普通用户关机
            ok = true;
        }
        else
        {
            parts.Add("no privilege elevation needed");
        }

        parts.Add($"user={WindowsIdentity.GetCurrent().Name}");
        parts.Add("DryRun: not actually triggered");
        report = string.Join("; ", parts);
        return ok;
    }

    private static bool ElevateShutdownPrivilege(out int lastError)
    {
        lastError = 0;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
        {
            lastError = Marshal.GetLastWin32Error();
            return false;
        }
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out LUID luid))
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };
            bool ok = AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            lastError = Marshal.GetLastWin32Error();
            return ok && (lastError == 0 || lastError == 1300); // 1300=ERROR_NOT_ALL_ASSIGNED（权限本就在 token 里）
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
