namespace TileStartMenu.Core.Power;

/// <summary>磁贴墙上的系统电源动作。</summary>
public enum PowerAction
{
    None,
    Shutdown,
    Restart,
    Sleep,
    Lock,
    SignOut
}

/// <summary>
/// 电源动作执行器。二次确认（尤其关机/重启）由界面层完成：UI 弹确认框 → 用户点"确定" →
/// 先调 <see cref="Arm"/> 再调 <see cref="Execute"/>。只有最近 <see cref="ArmExpirySeconds"/>
/// 秒内被 Arm 过的动作才允许 Execute，否则核心层拒绝（避免任何误触路径直接关机）。
/// Shutdown/Restart 走 ExitWindowsEx + SE_SHUTDOWN_NAME 权限提升；Sleep 走 SetSuspendState；
/// Lock 走 LockWorkStation；SignOut 走 ExitWindowsEx(EWX_LOGOFF)。
/// </summary>
public interface IPowerService
{
    /// <summary>在 Execute 前调用，标记用户已在 UI 二次确认。有效期 <see cref="ArmExpirySeconds"/> 秒。</summary>
    void Arm(PowerAction action);

    /// <summary>真正执行系统电源动作。未 Arm 会被拒绝。</summary>
    bool Execute(PowerAction action, out string? error);

    /// <summary>只做权限提升与参数校验，不真正触发（供测试与自检使用）。不受 Arm 限制。</summary>
    bool DryRun(PowerAction action, out string? report);

    /// <summary>Arm 有效期（秒）。</summary>
    public const int ArmExpirySeconds = 10;
}
