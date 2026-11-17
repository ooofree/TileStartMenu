using Microsoft.Win32;

namespace TileStartMenu.App.AppHost;

/// <summary>
/// 开机自启：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下的 "TileStartMenu" 值。
/// 默认关闭；只有用户在设置里打开才写注册表。
/// </summary>
public static class StartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TileStartMenu";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return;
                key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // 注册表写失败不致命（可能被组策略拦截），下次启动再试
        }
    }
}
