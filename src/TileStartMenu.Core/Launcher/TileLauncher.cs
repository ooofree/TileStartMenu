using System.Diagnostics;
using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Launcher;

/// <summary>通过 ShellExecute 打开目标，与在资源管理器中双击等价。</summary>
public sealed class TileLauncher : ITileLauncher
{
    public bool Execute(TileItem tile, out string? error)
    {
        error = null;
        var target = Expand(tile.Target);

        if (string.IsNullOrWhiteSpace(target))
        {
            error = "磁贴没有设置目标";
            return false;
        }

        try
        {
            switch (tile.Kind)
            {
                case TileActionKind.Url:
                    if (!IsHttp(target))
                    {
                        error = "网址必须以 http:// 或 https:// 开头";
                        return false;
                    }
                    return StartProcess(target, null, null);

                case TileActionKind.Folder:
                    if (!Directory.Exists(target))
                    {
                        error = "目录不存在：" + target;
                        return false;
                    }
                    return StartProcess(target, null, null);

                case TileActionKind.File:
                    if (!File.Exists(target))
                    {
                        error = "文件不存在：" + target;
                        return false;
                    }
                    return StartProcess(target, null, null);

                case TileActionKind.Shell:
                    return StartProcess("explorer.exe", target, null);

                case TileActionKind.Launch:
                default:
                    return StartProcess(target, tile.Arguments, Expand(tile.WorkingDirectory));
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool CanResolve(TileItem tile)
    {
        var target = Expand(tile.Target);
        if (string.IsNullOrWhiteSpace(target)) return false;

        switch (tile.Kind)
        {
            case TileActionKind.Folder:
                return Directory.Exists(target);
            case TileActionKind.File:
                return File.Exists(target);
            case TileActionKind.Url:
                return IsHttp(target);
            case TileActionKind.Shell:
                return true;
            case TileActionKind.Launch:
            default:
                return ResolveExecutable(target) is not null;
        }
    }

    /// <summary>解析可执行文件：绝对路径存在，或能在 PATH / 系统目录中找到。</summary>
    private static string? ResolveExecutable(string target)
    {
        if (File.Exists(target)) return target;

        var candidates = new List<string>();
        if (Path.HasExtension(target))
        {
            candidates.Add(target);
        }
        else
        {
            candidates.Add(target + ".exe");
            candidates.Add(target + ".cmd");
            candidates.Add(target + ".bat");
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;

            if (Path.IsPathRooted(candidate)) continue;

            var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in pathDirs)
            {
                var full = Path.Combine(Environment.ExpandEnvironmentVariables(dir), candidate);
                if (File.Exists(full)) return full;
            }

            var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var inSystem = Path.Combine(systemDir, candidate);
            if (File.Exists(inSystem)) return inSystem;
        }

        return null;
    }

    private static bool StartProcess(string fileName, string? arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true
        };
        if (!string.IsNullOrWhiteSpace(arguments)) psi.Arguments = arguments;
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;

        Process.Start(psi);
        return true;
    }

    private static bool IsHttp(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Expand(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
}
