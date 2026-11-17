using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Hotkey;

/// <summary>
/// 接管 Win 键的全局键盘钩子服务。
/// 契约：单次短按 Win（按下与抬起之间没有其他按键、且时长不超过 Settings.MaxTapHoldMs）才触发 StartKeyPressed；
/// 任何 Win+X 组合键、长按都必须放行给系统，不得破坏 Win+L / Win+R / Win+Shift+S / Win+D 等系统组合键。
/// </summary>
public interface IWinKeyHookService
{
    bool IsRunning { get; }

    /// <summary>最近一次失败原因，便于界面与日志呈现（不弹窗骚扰用户）。</summary>
    string? LastError { get; }

    /// <summary>安装并启用钩子。失败返回 false 并设置 LastError。</summary>
    bool Start();

    /// <summary>卸载钩子，恢复系统默认 Win 键行为。</summary>
    void Stop();

    /// <summary>检测到"干净的" Win 键短按。</summary>
    event EventHandler? StartKeyPressed;
}
