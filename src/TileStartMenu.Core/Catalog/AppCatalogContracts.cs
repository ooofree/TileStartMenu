using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Catalog;

/// <summary>应用的来源类型。</summary>
public enum AppEntryKind
{
    /// <summary>开始菜单里的 .lnk 快捷方式（传统 Win32 程序）。</summary>
    Win32Shortcut,
    /// <summary>UWP / Store 应用（通过 shell:AppsFolder 的 AUMID 启动）。</summary>
    Uwp
}

/// <summary>从本机枚举出来的一个"可启动应用"。</summary>
public sealed class AppEntry
{
    /// <summary>磁贴显示名（快捷方式/应用的显示名，去掉多余后缀）。</summary>
    public string DisplayName { get; set; } = "";

    public AppEntryKind Kind { get; set; }

    /// <summary>
    /// 启动目标：Win32 为 .lnk 快捷方式路径；UWP 为完整 AUMID
    /// （即 shell:AppsFolder\{AUMID} 里的那一段）。
    /// </summary>
    public string LaunchTarget { get; set; } = "";

    /// <summary>.lnk 解析出的可执行文件绝对路径（UWP 为 null），用于图标提取。</summary>
    public string? ExecutablePath { get; set; }

    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }

    /// <summary>图标来源路径（exe / .ico / .lnk 自身），供图标服务提取；UWP 可为 null。</summary>
    public string? IconSourcePath { get; set; }
}

/// <summary>
/// 本机可启动应用的枚举器。首次运行用它生成"全部应用"磁贴；
/// 之后只在用户主动点"重置为全部应用"时再次调用。
/// </summary>
public interface IAppCatalog
{
    /// <summary>
    /// 枚举本机所有可启动应用（已去重、已过滤卸载器/帮助文档/网址快捷方式等无意义项）。
    /// 必须能在后台线程调用，且不得抛出异常（失败返回已有结果）。
    /// </summary>
    IReadOnlyList<AppEntry> Enumerate();

    /// <summary>把枚举结果转成磁贴（分组、颜色、尺寸、排序由实现统一决定）。</summary>
    List<TileItem> CreateTiles(IEnumerable<AppEntry> entries);

    /// <summary>最近一次枚举的耗时与条数，供日志与验收记录使用。</summary>
    string LastDiagnostics { get; }
}

/// <summary>Metro 调色板（磁贴底色与取色面板共用），保证核心与界面配色一致。</summary>
public static class TilePalette
{
    public static readonly string[] Metro =
    {
        "#0078D4", "#005A9E", "#00A4EF", "#00B7C3", "#107C10", "#498205",
        "#7A7574", "#5D5D5D", "#B7472A", "#D13438", "#C239B3", "#8764B8",
        "#4A5459", "#2D7D9A", "#0B7A5A", "#986F0B"
    };

    /// <summary>按名称稳定取色：同一应用每次得到同一个颜色。</summary>
    public static string ColorForName(string name)
    {
        if (string.IsNullOrEmpty(name)) return Metro[0];
        unchecked
        {
            int hash = 0;
            foreach (var ch in name) hash = hash * 31 + ch;
            var index = Math.Abs(hash) % Metro.Length;
            return Metro[index];
        }
    }
}
