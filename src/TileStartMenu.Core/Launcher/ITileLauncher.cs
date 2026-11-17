using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Launcher;

/// <summary>磁贴动作执行器。所有启动都走 ShellExecute 语义（等同在资源管理器里双击）。</summary>
public interface ITileLauncher
{
    /// <summary>执行磁贴动作。成功返回 true；失败返回 false 并给出可读错误。</summary>
    bool Execute(TileItem tile, out string? error);

    /// <summary>目标是否可解析（用于界面标红无效磁贴）。</summary>
    bool CanResolve(TileItem tile);
}
