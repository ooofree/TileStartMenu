namespace TileStartMenu.App.UI;

/// <summary>
/// 主菜单窗口的对外契约。系统集成层（托盘 / Win 键钩子）只通过这个接口控制菜单显隐，
/// 不直接引用窗口内部实现，从而与界面层解耦。
/// </summary>
public interface IMenuWindow
{
    bool IsMenuOpen { get; }

    /// <summary>显示并激活菜单（不切换，幂等）。</summary>
    void ShowMenu();

    /// <summary>隐藏菜单（幂等）。</summary>
    void HideMenu();

    /// <summary>显示/隐藏切换，供 Win 键与托盘使用。</summary>
    void ToggleMenu();
}
