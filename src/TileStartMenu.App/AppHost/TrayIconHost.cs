using System.Runtime.InteropServices;
using System.Windows;

namespace TileStartMenu.App.AppHost;

/// <summary>
/// 托盘常驻图标 + 右键菜单 + 备用热键（Ctrl+Alt+Space）。
/// 由于 App 工程未启用 UseWindowsForms，这里直接 P/Invoke Shell_NotifyIcon / TrackPopupMenu / RegisterHotKey，
/// 在 WPF UI 线程上创建一个 message-only 窗口承载消息循环。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    // ---- Win32 常量 ----
    private const int WH_UNUSED = 0;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_USER = 0x0400;
    private const uint WM_TRAYCALLBACK = WM_USER + 1;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint TPM_RIGHTBUTTON = 0x00000002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint VK_SPACE = 0x20;
    private const int HOTKEY_ID = 0xBEEF;

    // 菜单命令 ID
    private const int ID_OPEN_MENU = 1001;
    private const int ID_TOGGLE_WINKEY = 1002;
    private const int ID_TOGGLE_AUTOSTART = 1003;
    private const int ID_OPEN_CONFIG_DIR = 1004;
    private const int ID_RESTORE_WIN = 1005;
    private const int ID_LAST_INVOKE = 1006;
    private const int ID_EXIT = 1007;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASS lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge,
        out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const string WindowClassName = "TileStartMenu.TrayHostWindow";

    private readonly WndProcDelegate _wndProc; // 必须保持引用
    private IntPtr _hWnd;
    private IntPtr _hIcon;
    private bool _disposed;

    /// <summary>打开菜单（左键单击托盘 / 第二个实例唤起 / Ctrl+Alt+Space）。</summary>
    public event Action? OnOpenMenu;

    /// <summary>切换 Win 键接管勾选。</summary>
    public event Action? OnToggleWinKey;

    /// <summary>切换开机自启勾选。</summary>
    public event Action? OnToggleAutostart;

    /// <summary>打开配置目录。</summary>
    public event Action? OnOpenConfigDir;

    /// <summary>紧急恢复系统默认 Win 键。</summary>
    public event Action? OnRestoreWin;

    /// <summary>退出程序。</summary>
    public event Action? OnExit;

    /// <summary>由宿主刷新菜单勾选状态（配置变化时调用）。</summary>
    public bool WinKeyChecked { get; set; }
    public bool AutostartChecked { get; set; }

    public TrayIconHost()
    {
        _wndProc = WndProc;
    }

    public void Create()
    {
        IntPtr hInstance = GetModuleHandleW(null);

        // 注册窗口类
        var wc = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };
        RegisterClassExW(ref wc); // 重复注册会失败，忽略

        // message-only 窗口（HWND_MESSAGE = -3）
        _hWnd = CreateWindowExW(0, WindowClassName, "TileStartMenu Tray", 0,
            0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);

        // 取本 exe 图标
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            ExtractIconExW(exe, 0, out _, out IntPtr small, 1);
            _hIcon = small;
        }

        // 添加托盘图标
        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYCALLBACK,
            hIcon = _hIcon,
            szTip = "TileStartMenu"
        };
        Shell_NotifyIconW(NIM_ADD, ref nid);

        // 注册备用热键 Ctrl+Alt+Space
        RegisterHotKey(_hWnd, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_SPACE);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYCALLBACK)
        {
            uint trayMsg = (uint)lParam.ToInt64() & 0xFFFF;
            if (trayMsg == WM_LBUTTONUP)
            {
                try { OnOpenMenu?.Invoke(); } catch { }
            }
            else if (trayMsg == WM_RBUTTONUP)
            {
                ShowContextMenu();
            }
            return IntPtr.Zero;
        }

        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            try { OnOpenMenu?.Invoke(); } catch { }
            return IntPtr.Zero;
        }

        if (msg == WM_COMMAND)
        {
            int cmd = wParam.ToInt32() & 0xFFFF;
            DispatchMenuCommand(cmd);
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr hMenu = CreatePopupMenu();
        try
        {
            AppendMenuW(hMenu, MF_STRING, ID_OPEN_MENU, "打开菜单");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING | (WinKeyChecked ? MF_CHECKED : MF_UNCHECKED), ID_TOGGLE_WINKEY,
                "启用 Win 键接管");
            AppendMenuW(hMenu, MF_STRING | (AutostartChecked ? MF_CHECKED : MF_UNCHECKED), ID_TOGGLE_AUTOSTART,
                "开机自启");
            AppendMenuW(hMenu, MF_STRING, ID_OPEN_CONFIG_DIR, "打开配置目录");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, ID_RESTORE_WIN, "恢复系统默认 Win 键");
            AppendMenuW(hMenu, MF_GRAYED | MF_STRING, ID_LAST_INVOKE,
                TimingLog.LastInvokeMs.HasValue ? $"上次唤起 {TimingLog.LastInvokeMs} ms" : "上次唤起 -- ms");
            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, ID_EXIT, "退出");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hWnd);
            int cmd = TrackPopupMenu(hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
            if (cmd != 0) DispatchMenuCommand(cmd);
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    private void DispatchMenuCommand(int cmd)
    {
        try
        {
            switch (cmd)
            {
                case ID_OPEN_MENU: OnOpenMenu?.Invoke(); break;
                case ID_TOGGLE_WINKEY: OnToggleWinKey?.Invoke(); break;
                case ID_TOGGLE_AUTOSTART: OnToggleAutostart?.Invoke(); break;
                case ID_OPEN_CONFIG_DIR: OnOpenConfigDir?.Invoke(); break;
                case ID_RESTORE_WIN: OnRestoreWin?.Invoke(); break;
                case ID_EXIT: OnExit?.Invoke(); break;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { UnregisterHotKey(_hWnd, HOTKEY_ID); } catch { }

        var nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = 1
        };
        try { Shell_NotifyIconW(NIM_DELETE, ref nid); } catch { }

        try { if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd); } catch { }
        try { if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon); } catch { }
    }
}
