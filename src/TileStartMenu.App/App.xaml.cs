using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using TileStartMenu.App.AppHost;
using TileStartMenu.App.UI;
using TileStartMenu.Core.Catalog;
using TileStartMenu.Core.Hotkey;
using TileStartMenu.Core.Icons;
using TileStartMenu.Core.Launcher;
using TileStartMenu.Core.Models;
using TileStartMenu.Core.Persistence;
using TileStartMenu.Core.Power;

namespace TileStartMenu.App;

/// <summary>
/// 应用宿主：装配 Core 服务，处理单实例 / 托盘常驻 / Win 键钩子 / 开机自启 / 备用热键 / 性能埋点。
/// 不显示主窗口，Win 键或托盘点击时才切换菜单显隐；关闭后把焦点还给原来的前台窗口。
/// </summary>
public partial class App : Application
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly Stopwatch _processStopwatch = Stopwatch.StartNew();

    private SingleInstanceManager? _single;
    private JsonConfigStore? _store;
    private TileLauncher? _launcher;
    private IconService? _icons;
    private WinKeyHookService? _hook;
    private TrayIconHost? _tray;
    private MainWindow? _menuWindow;
    private AppCatalog? _catalog;
    private PowerService? _power;
    private IntPtr _prevForeground = IntPtr.Zero;

    public IConfigStore Store => _store ?? throw new InvalidOperationException("Store 尚未初始化");
    public IIconService Icons => _icons ?? throw new InvalidOperationException("IconService 尚未初始化");
    public IAppCatalog Catalog => _catalog ?? throw new InvalidOperationException("Catalog 尚未初始化");
    public IPowerService Power => _power ?? throw new InvalidOperationException("PowerService 尚未初始化");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1) 单实例：第二个实例唤起已有实例后自行退出
        _single = new SingleInstanceManager();
        if (!_single.TryAcquire())
        {
            Shutdown(0);
            return;
        }

        // 2) 装配 Core 服务
        _store = new JsonConfigStore();
        _launcher = new TileLauncher();
        _icons = new IconService(_store.IconCacheDir);
        _catalog = new AppCatalog(_store.RootDir);
        _power = new PowerService();
        Debug.WriteLine("[App] power=ready");

        // 3) Win 键钩子
        _hook = new WinKeyHookService
        {
            Settings = _store.Current.Settings
        };
        _hook.StartKeyPressed += OnStartKeyPressed;
        // 紧急恢复必须异步：钩子线程自己会 PostThreadMessage(WM_QUIT) 退出，不能在钩子线程内
        // 同步 Dispatcher.Invoke 再 Join 钩子线程自身（会死等 2s 超时）。
        _hook.EmergencyRestoreRequested += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(EmergencyRestore));
        };

        // 4) 创建主窗口（隐藏）；UI 方签名 MainWindow(IConfigStore, ITileLauncher, IIconService?, IAppCatalog?, IPowerService?)
        _menuWindow = new MainWindow(_store, _launcher, _icons, _catalog, _power);

        // 5) 托盘 + 备用热键
        _tray = new TrayIconHost();
        _tray.OnOpenMenu += () => Dispatcher.BeginInvoke(new Action(() => ToggleMenu()));
        _tray.OnToggleWinKey += () => Dispatcher.BeginInvoke(new Action(() => ToggleWinKeySetting()));
        _tray.OnToggleAutostart += () => Dispatcher.BeginInvoke(new Action(() => ToggleAutostartSetting()));
        _tray.OnOpenConfigDir += () => Dispatcher.BeginInvoke(new Action(() => OpenConfigDir()));
        _tray.OnRestoreWin += () => Dispatcher.BeginInvoke(new Action(() => EmergencyRestore()));
        _tray.OnExit += () => Dispatcher.BeginInvoke(new Action(() => Shutdown(0)));
        try { _tray.Create(); }
        catch (Exception ex) { Debug.WriteLine("托盘创建失败：" + ex.Message); }
        RefreshTrayChecks();

        // 6) 启动钩子
        if (_store.Current.Settings.WinKeyEnabled)
        {
            if (!_hook.Start())
            {
                Debug.WriteLine("WinKeyHook 启动失败：" + _hook.LastError);
            }
        }

        // 7) 开机自启
        StartupRegistry.Apply(_store.Current.Settings.LaunchAtStartup);

        // 8) 配置变化即时重应用
        _store.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(ReapplySettings));

        // 9) 第二个实例唤起
        _single.StartServer(() => Dispatcher.BeginInvoke(new Action(() => ToggleMenu())));

        // 10) 种子/迁移：SeedVersion < 2 时执行 v4 基础种子迁移（即使 AllAppsSeeded=true 也会迁）。
        if (_store.Current.Settings.SeedVersion < 2)
        {
            ThreadPool.QueueUserWorkItem(_ => SeedAllApps());
        }

        // 11) 冷启动埋点：进程启动 → 钩子就绪
        long coldMs = _processStopwatch.ElapsedMilliseconds;
        TimingLog.RecordColdStart(coldMs);
    }

    /// <summary>首次运行：枚举本机应用，生成磁贴，落盘。</summary>
    private void SeedAllApps()
    {
        try
        {
            // 注意：首轮不再全量枚举 547 个应用。只生成 6~10 个基础磁贴 + 5 个电源磁贴。
            // 其余应用通过「全部应用」列表（IAppCatalog.Enumerate，有缓存）由用户自行 Pin。
            var tiles = new List<TileItem>();
            int order = 0;

            void AddBasic(string name, TileActionKind kind, string target, string? args = null)
            {
                tiles.Add(new TileItem
                {
                    Name = name, Kind = kind, Target = target, Arguments = args,
                    Color = TilePalette.ColorForName(name), Size = TileSize.Medium,
                    GroupId = MenuConfig.DefaultGroupId, Order = order++, Pinned = true
                });
            }

            // 文件资源管理器
            AddBasic("文件资源管理器", TileActionKind.Launch, "explorer.exe");

            // 默认浏览器（从注册表 http 协议关联解析，不写死 Edge）
            var browserOpt = ResolveDefaultBrowser();
            if (browserOpt is var (bName, bPath, bArgs)) AddBasic(bName, TileActionKind.Launch, bPath, bArgs);

            // 设置
            AddBasic("设置", TileActionKind.Shell, "ms-settings:");

            // 计算器 / 记事本
            AddBasic("计算器", TileActionKind.Launch, "calc.exe");
            AddBasic("记事本", TileActionKind.Launch, "notepad.exe");

            // 终端（优先 wt.exe，退回 powershell.exe）
            string? terminal = ResolveExecutableOnPath("wt.exe");
            terminal ??= ResolveExecutableOnPath("WindowsTerminal.exe");
            terminal ??= ResolveExecutableOnPath("powershell.exe");
            if (terminal is not null) AddBasic("终端", TileActionKind.Launch, terminal);

            // 电源磁贴组
            var powerGroupId = "g-power";
            if (_store!.Current.Groups.All(g => g.Id != powerGroupId))
            {
                // 分组由 Normalize 兜底；这里直接在磁贴里引用即可
            }
            int pOrder = 0;
            void AddPower(string name, PowerAction act)
            {
                tiles.Add(new TileItem
                {
                    Name = name, Kind = TileActionKind.Launch, Target = "power:" + act,
                    Color = "#1B1B1B", Size = TileSize.Small,
                    GroupId = powerGroupId, Order = pOrder++, Pinned = true,
                    PowerAction = act
                });
            }
            AddPower("关机", PowerAction.Shutdown);
            AddPower("重启", PowerAction.Restart);
            AddPower("睡眠", PowerAction.Sleep);
            AddPower("锁屏", PowerAction.Lock);
            AddPower("注销", PowerAction.SignOut);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_store is null) return;
                var cfg = _store.Current;
                cfg.Tiles.Clear();
                // 确保"系统"分组存在
                if (cfg.Groups.All(g => g.Id != "g-power"))
                {
                    cfg.Groups.Add(new TileGroup { Id = "g-power", Name = "系统", Order = cfg.Groups.Count });
                }
                cfg.Tiles.AddRange(tiles);
                cfg.Settings.AllAppsSeeded = true;
                cfg.Settings.SeedVersion = 2;
                _store.Save(cfg);
            }));

            // 电源 DryRun 自检（不真触发）
            try
            {
                var power = new PowerService();
                foreach (var act in new[] { PowerAction.Shutdown, PowerAction.Restart, PowerAction.Sleep, PowerAction.Lock, PowerAction.SignOut })
                {
                    power.DryRun(act, out var report);
                    Debug.WriteLine($"Power DryRun {act}: {report}");
                }
            }
            catch (Exception ex) { Debug.WriteLine("Power DryRun failed: " + ex.Message); }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("SeedBasicTiles 失败：" + ex.Message);
        }
    }

    private static (string DisplayName, string Path, string? Args)? ResolveDefaultBrowser()
    {
        try
        {
            using var httpKey = Registry.ClassesRoot.OpenSubKey(@"http\shell\open\command");
            var cmd = httpKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            // 形如 "\"C:\Program Files\...\browser.exe" -- "%1""
            cmd = cmd.Trim();
            string exe;
            string? args = null;
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) { exe = cmd.Substring(1, end - 1); args = cmd[(end + 1)..].Trim(); }
                else exe = cmd;
            }
            else
            {
                var parts = cmd.Split(' ', 2);
                exe = parts[0];
                args = parts.Length > 1 ? parts[1] : null;
            }
            if (!File.Exists(exe)) return null;
            // 剥掉 "%1"、"%*" 占位符——这些是协议处理器给传入 URL 留的坑位，
            // 磁贴启动时不带 URL，留着会被当成字面量参数传给浏览器。
            // 注意：不要 Trim 引号，避免误伤其它合法带引号参数。
            if (args is not null)
            {
                args = args.Replace("\"%1\"", "").Replace("%1", "")
                           .Replace("\"%*\"", "").Replace("%*", "")
                           .Trim();
                if (string.IsNullOrWhiteSpace(args)) args = null;
            }
            return (Path.GetFileNameWithoutExtension(exe), exe, args);
        }
        catch { return null; }
    }

    private static string? ResolveExecutableOnPath(string name)
    {
        if (File.Exists(name)) return name;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var d in dirs)
        {
            var full = Path.Combine(Environment.ExpandEnvironmentVariables(d), name);
            if (File.Exists(full)) return full;
        }
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var inSys = Path.Combine(sys, name);
        return File.Exists(inSys) ? inSys : null;
    }

    private void ToggleMenu()
    {
        if (_menuWindow is null) return;

        if (_menuWindow.IsMenuOpen)
        {
            // 关闭：先关窗口，再把焦点还给原来的前台窗口
            _menuWindow.HideMenu();
            if (_prevForeground != IntPtr.Zero)
            {
                try { SetForegroundWindow(_prevForeground); } catch { }
                _prevForeground = IntPtr.Zero;
            }
        }
        else
        {
            // 打开：记录当前前台窗口，再显示菜单
            _prevForeground = GetForegroundWindow();
            _menuWindow.ShowMenu();
        }
    }

    private void OnStartKeyPressed(object? sender, EventArgs e)
    {
        var sw = Stopwatch.StartNew();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_menuWindow is null) return;
            bool wasOpen = _menuWindow.IsMenuOpen;
            ToggleMenu();
            if (wasOpen) return; // 是关闭动作，不记耗时

            // 等下一帧渲染完成，记"按键 → 首帧可见"耗时
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                CompositionTarget.Rendering -= handler;
                sw.Stop();
                TimingLog.RecordInvoke(sw.ElapsedMilliseconds);
            };
            CompositionTarget.Rendering += handler;
        }));
    }

    private void ToggleWinKeySetting()
    {
        if (_store is null) return;
        var cfg = _store.Current;
        cfg.Settings.WinKeyEnabled = !cfg.Settings.WinKeyEnabled;
        _store.Save(cfg);
    }

    private void ToggleAutostartSetting()
    {
        if (_store is null) return;
        var cfg = _store.Current;
        cfg.Settings.LaunchAtStartup = !cfg.Settings.LaunchAtStartup;
        _store.Save(cfg);
    }

    private void OpenConfigDir()
    {
        try
        {
            if (_store is null) return;
            Process.Start(new ProcessStartInfo { FileName = _store.ConfigPath, UseShellExecute = true });
        }
        catch { }
    }

    private void EmergencyRestore()
    {
        if (_store is null || _hook is null) return;
        _hook.Stop();
        var cfg = _store.Current;
        if (cfg.Settings.WinKeyEnabled)
        {
            cfg.Settings.WinKeyEnabled = false;
            _store.Save(cfg);
        }
        RefreshTrayChecks();
    }

    private void ReapplySettings()
    {
        if (_store is null || _hook is null || _tray is null) return;
        var settings = _store.Current.Settings;

        _hook.Settings = settings;
        if (settings.WinKeyEnabled && !_hook.IsRunning) _hook.Start();
        else if (!settings.WinKeyEnabled && _hook.IsRunning) _hook.Stop();

        StartupRegistry.Apply(settings.LaunchAtStartup);
        RefreshTrayChecks();
    }

    private void RefreshTrayChecks()
    {
        if (_tray is null || _store is null) return;
        _tray.WinKeyChecked = _store.Current.Settings.WinKeyEnabled;
        _tray.AutostartChecked = StartupRegistry.IsEnabled();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hook?.Stop(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _single?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
