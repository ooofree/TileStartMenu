using System.IO;

namespace TileStartMenu.App.AppHost;

/// <summary>
/// 性能埋点：追加写入 %LOCALAPPDATA%\TileStartMenu\timing.log。
/// 同时在内存里保留"上次唤起耗时"，供托盘菜单展示。
/// </summary>
public static class TimingLog
{
    private static readonly object _gate = new();
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TileStartMenu", "timing.log");

    /// <summary>上次"Win 键按下 → 菜单首帧可见"耗时（毫秒），未记录过为 null。</summary>
    public static long? LastInvokeMs { get; private set; }

    public static void RecordColdStart(long hookReadyMs)
    {
        Append($"cold_start_ms={hookReadyMs}");
    }

    public static void RecordInvoke(long invokeToVisibleMs)
    {
        LastInvokeMs = invokeToVisibleMs;
        Append($"invoke_to_visible_ms={invokeToVisibleMs}");
    }

    private static void Append(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "  " + line + Environment.NewLine);
            }
        }
        catch
        {
            // 写日志失败不影响主流程
        }
    }
}
