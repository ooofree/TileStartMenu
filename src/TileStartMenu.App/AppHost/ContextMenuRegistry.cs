using Microsoft.Win32;

namespace TileStartMenu.App.AppHost;

/// <summary>
/// 在 HKCU 下为 .lnk / .exe 注册"固定到开始菜单 / 从开始菜单取消固定"右键动词。
/// 无需管理员，幂等。
/// </summary>
public static class ContextMenuRegistry
{
    private const string PinVerbName = "TileStartMenu.Pin";
    private const string UnpinVerbName = "TileStartMenu.Unpin";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsInstalled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\lnkfile\shell\{PinVerbName}");
            return k != null;
        }
        catch { return false; }
    }

    public static void Install()
    {
        string exe = ExePath;
        if (string.IsNullOrEmpty(exe)) return;

        foreach (string ext in new[] { "lnkfile", "exefile" })
        {
            using (var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\shell"))
            {
                using (var pin = shell.CreateSubKey(PinVerbName))
                {
                    pin.SetValue(null, "固定到开始菜单");
                    pin.SetValue("Icon", $"\"{exe}\",0");
                    using var cmd = pin.CreateSubKey("command");
                    cmd.SetValue(null, $"\"{exe}\" --pin \"%1\"");
                }
                using (var unpin = shell.CreateSubKey(UnpinVerbName))
                {
                    unpin.SetValue(null, "从开始菜单取消固定");
                    unpin.SetValue("Icon", $"\"{exe}\",0");
                    using var cmd = unpin.CreateSubKey("command");
                    cmd.SetValue(null, $"\"{exe}\" --unpin \"%1\"");
                }
            }
        }
    }

    public static void Uninstall()
    {
        foreach (string ext in new[] { "lnkfile", "exefile" })
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}\shell\{PinVerbName}", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ext}\shell\{UnpinVerbName}", false); } catch { }
        }
    }
}
