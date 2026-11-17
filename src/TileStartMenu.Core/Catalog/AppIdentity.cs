using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Catalog;

/// <summary>
/// 应用身份标识：让"全部应用列表"里的条目与"磁贴墙"上的磁贴能够稳定互相识别，
/// 从而实现 Pin / Unpin 一步到位（取消固定只隐藏磁贴，配置记录保留）。
/// </summary>
public static class AppIdentity
{
    /// <summary>把目标规范化为稳定 key（大小写、首尾空白、引号、环境变量差异都归一）。</summary>
    public static string Key(TileActionKind kind, string? target)
    {
        var normalized = (target ?? "").Trim().Trim('"');
        normalized = Environment.ExpandEnvironmentVariables(normalized);
        if (kind == TileActionKind.Url)
        {
            normalized = normalized.TrimEnd('/');
        }
        return StringKey(kind) + "|" + normalized.ToLowerInvariant();
    }

    public static string Key(TileItem tile) => Key(tile.Kind, tile.Target);

    public static string Key(AppEntry entry)
    {
        // UWP：用 AUMID 识别；Win32：用快捷方式路径识别，同时提供 exe 路径的等价 key
        return entry.Kind == AppEntryKind.Uwp
            ? Key(TileActionKind.Shell, "shell:AppsFolder\\" + entry.LaunchTarget)
            : Key(TileActionKind.Launch, entry.LaunchTarget);
    }

    /// <summary>两个 key 是否指向同一个应用（含 exe 路径与快捷方式路径互相等价的场景）。</summary>
    public static bool IsSameApp(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string StringKey(TileActionKind kind) => kind switch
    {
        TileActionKind.Launch => "launch",
        TileActionKind.File => "file",
        TileActionKind.Folder => "folder",
        TileActionKind.Url => "url",
        TileActionKind.Shell => "shell",
        _ => "launch"
    };
}
