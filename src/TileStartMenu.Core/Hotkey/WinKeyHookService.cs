using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using TileStartMenu.Core.Models;

namespace TileStartMenu.Core.Hotkey;

/// <summary>
/// 基于 WH_KEYBOARD_LL 的真实 Win 键接管实现。
/// 钩子安装在专用线程上，自带消息循环；吞键策略由 <see cref="WinKeyGestureClassifier"/> 决定。
/// 组合键 / 长按通过 SendInput 补送一次 Win 按下，保证 Win+R / Win+D / Win+E 等系统快捷键不被破坏。
/// 紧急恢复：在系统任何地方按下 Ctrl+Alt+Shift+Esc 立即卸载钩子，恢复默认 Win 键行为。
/// </summary>
public sealed class WinKeyHookService : IWinKeyHookService, IDisposable
{
    // ---- Win32 常量 ----
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;

    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_SHIFT = 0x10;
    private const uint VK_MENU = 0x12;
    private const uint VK_ESCAPE = 0x1B;

    private const uint LLKHF_INJECTED = 0x10;

    // 我们自己 SendInput 补送 Win 按下时携带的 dwExtraInfo 魔数；钩子看到它就跳过，避免递归。
    // 外部（测试脚本 / 其他程序）通过 SendInput 合成的按键 dwExtraInfo 为 0，钩子正常处理。
    private static readonly IntPtr OurInjectionCookie = new(0x54534D54); // 'TSMT'

    // ---- P/Invoke 签名 ----
    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg; public ushort wParamL; public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ---- 状态 ----
    private readonly object _gate = new();
    private IntPtr _hookHandle = IntPtr.Zero;
    private Thread? _hookThread;
    private LowLevelKeyboardProc? _proc; // 必须保持引用，避免委托被 GC
    private int _hookThreadId;
    private readonly ManualResetEventSlim _installedEvent = new(false);
    private readonly ManualResetEventSlim _stoppedEvent = new(false);
    private WinKeyGestureClassifier _classifier = new(700);
    private Timer? _holdTimer;
    private long _hookEnterTimestamp;

    /// <summary>由宿主在 Start() 之前赋值，提供 MaxTapHoldMs 等参数。</summary>
    public AppSettings? Settings { get; set; }

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public event EventHandler? StartKeyPressed;

    /// <summary>紧急恢复（Ctrl+Alt+Shift+Esc）触发，宿主收到后应更新托盘 UI / 配置。</summary>
    public event EventHandler? EmergencyRestoreRequested;

    public bool Start()
    {
        lock (_gate)
        {
            if (IsRunning) return true;
            LastError = null;
            _installedEvent.Reset();
            _stoppedEvent.Reset();

            int maxHold = Settings?.MaxTapHoldMs ?? 700;
            // 重建分类器，保证阈值与最新设置一致
            _classifier = new WinKeyGestureClassifier(maxHold);

            _proc = HookCallback;
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "TileStartMenu.WinKeyHook"
            };
            _hookThread.Start();

            // 等待钩子安装完成（最多 2 秒）
            if (!_installedEvent.Wait(TimeSpan.FromSeconds(2)))
            {
                LastError = "等待钩子安装超时";
                TryStopThread();
                return false;
            }

            if (_hookHandle == IntPtr.Zero)
            {
                Diag("Start: hook handle is zero after install");
                return false; // HookThreadMain 已经设置好 LastError
            }

            IsRunning = true;
            Diag("Start: hook installed OK");
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            TryStopThread();
            _classifier.Reset();
            IsRunning = false;
        }
    }

    private void TryStopThread()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
        _classifier.Reset();

        if (_hookThreadId != 0)
        {
            try { PostThreadMessageW((uint)_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
            catch { /* 线程可能已经退出 */ }
        }

        var t = _hookThread;
        if (t is not null && t.IsAlive)
        {
            try { t.Join(TimeSpan.FromSeconds(2)); }
            catch { /* 忽略 */ }
        }

        if (_hookHandle != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hookHandle); } catch { /* 忽略 */ }
            _hookHandle = IntPtr.Zero;
        }

        _hookThread = null;
        _hookThreadId = 0;
    }

    private void HookThreadMain()
    {
        _hookThreadId = (int)GetCurrentThreadId();
        _hookEnterTimestamp = Stopwatch.GetTimestamp();

        try
        {
            // LL 钩子需要 hMod 为本模块句柄；在 .NET 中用 GetModuleHandle(null) 取当前进程
            IntPtr hMod = GetModuleHandleW(null);
            _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _proc!, hMod, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"SetWindowsHookEx 失败（Win32 错误码 {err}）";
                _installedEvent.Set();
                return;
            }

            _installedEvent.Set();

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) != 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            LastError = "钩子线程异常：" + ex.Message;
            _installedEvent.Set();
        }
        finally
        {
            if (_hookHandle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hookHandle); } catch { }
                _hookHandle = IntPtr.Zero;
            }
            long ms = (long)(Stopwatch.GetElapsedTime(_hookEnterTimestamp).TotalMilliseconds);
            Diag($"HookThreadMain: exited, hook unhooked (lifetime {ms} ms)");
            _stoppedEvent.Set();
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // 我们自己 SendInput 补送的事件（带 cookie），直接放行，不参与判定（避免递归）。
        // 外部 SendInput（如自动化测试）不带 cookie，钩子正常处理。
        if (data.dwExtraInfo == OurInjectionCookie)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        int msg = wParam.ToInt32();
        bool isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isUp = msg is WM_KEYUP or WM_SYSKEYUP;
        if (!isDown && !isUp)
        {
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        uint vk = data.vkCode;
        long nowMs = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);

        // 紧急恢复：Ctrl+Alt+Shift+Esc
        if (vk == VK_ESCAPE && isDown &&
            (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0 &&
            (GetAsyncKeyState((int)VK_MENU) & 0x8000) != 0 &&
            (GetAsyncKeyState((int)VK_SHIFT) & 0x8000) != 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            Diag("Emergency: Ctrl+Alt+Shift+Esc detected, posting WM_QUIT");
            try { EmergencyRestoreRequested?.Invoke(this, EventArgs.Empty); } catch { }
            // 立刻 PostThreadMessage(WM_QUIT) 让本线程消息循环退出；不要在钩子线程内同步等待 UI。
            var threadId = (uint)_hookThreadId;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { PostThreadMessageW(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            });
            // 这次 Esc 仍然放行给系统（任务管理器会被调出，符合用户预期）
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var cls = _classifier;
        bool isWin = vk is VK_LWIN or VK_RWIN;

        if (isWin && isDown)
        {
            bool swallow = cls.OnWinDown(nowMs);
            if (swallow)
            {
                ArmHoldTimer();
                return (IntPtr)1; // 吞掉
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        if (isWin && isUp)
        {
            DisarmHoldTimer();
            bool swallow = cls.OnWinUp(nowMs);
            if (swallow)
            {
                // 短按：吞掉抬起，触发菜单
                Diag("OnWinUp: swallow + trigger StartKeyPressed");
                try { StartKeyPressed?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { Diag("StartKeyPressed handler threw: " + ex.Message); }
                return (IntPtr)1;
            }
            Diag("OnWinUp: pass through");
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // 非 Win 键
        if (isDown)
        {
            // 如果正在 Armed 状态，说明这是组合键：补送一次 Win 按下，让系统识别组合
            if (cls.Current == WinKeyGestureClassifier.State.Armed)
            {
                cls.OnOtherKeyDown(nowMs);
                DisarmHoldTimer();
                InjectWinDown();
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        // 其它键抬起：永远放行
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void ArmHoldTimer()
    {
        int ms = Math.Max(50, Settings?.MaxTapHoldMs ?? 700);
        _holdTimer?.Dispose();
        _holdTimer = new Timer(_ =>
        {
            var cls = _classifier;
            long now = Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 1000);
            if (cls.OnHoldTimeout(now))
            {
                InjectWinDown();
            }
        }, null, ms, Timeout.Infinite);
    }

    private void DisarmHoldTimer()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
    }

    private static void InjectWinDown()
    {
        try
        {
            var inputs = new INPUT[1];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = (ushort)VK_LWIN;
            inputs[0].u.ki.wScan = 0;
            inputs[0].u.ki.dwFlags = 0; // keydown
            inputs[0].u.ki.time = 0;
            inputs[0].u.ki.dwExtraInfo = OurInjectionCookie;
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }
        catch
        {
            // 补送失败不致命：最坏情况是组合键没识别，不影响其它逻辑
        }
    }

    private static void Diag(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileStartMenu", "hook-debug.log");
            File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose() => Stop();
}
