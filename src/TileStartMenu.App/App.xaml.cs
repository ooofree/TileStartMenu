using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
    private string? _pendingCommand; // 首实例启动时待执行的 PIN/UNPIN 命令

    public IConfigStore Store => _store ?? throw new InvalidOperationException("Store 尚未初始化");
    public IIconService Icons => _icons ?? throw new InvalidOperationException("IconService 尚未初始化");
    public IAppCatalog Catalog => _catalog ?? throw new InvalidOperationException("Catalog 尚未初始化");
    public IPowerService Power => _power ?? throw new InvalidOperationException("PowerService 尚未初始化");

    private static void UiLog(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TileStartMenu");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ui-debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 0) 尽早解析命令行：--pin "<path>" / --unpin "<path>"
        string? cliCmd = ParseCliCommand(e.Args, out string? cliPath);

        // 0b) 无头布局自检：--layout-test 直接跑完表格写日志后退出，不建窗口、不碰配置
        if (e.Args.Contains("--layout-test"))
        {
            RunLayoutTest();
            Shutdown(0);
            return;
        }

        if (cliCmd is not null && cliPath is null)
        {
            UiLog($"CLI {cliCmd} missing path, exiting.");
            Shutdown(2);
            return;
        }

        // 1) 单实例：第二个实例唤起已有实例后自行退出
        _single = new SingleInstanceManager();
        if (!_single.TryAcquire())
        {
            // 已有实例：把命令转发过去
            if (cliCmd is not null)
            {
                SingleInstanceManager.ForwardCommand($"{cliCmd}|{cliPath}");
                UiLog($"CLI forwarded to existing instance: {cliCmd}|{cliPath}");
            }
            Shutdown(0);
            return;
        }

        // 我们是首实例；记住待执行命令
        if (cliCmd is not null) _pendingCommand = $"{cliCmd}|{cliPath}";

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
        _tray.OnToggleContextMenu += () => Dispatcher.BeginInvoke(new Action(() => ToggleContextMenuSetting()));
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

        // 9) 第二个实例唤起（命令协议：SHOW / SHOT / PIN|path / UNPIN|path）
        UiLog("PIPE name=TileStartMenu.BringToFront.2026");
        _single.StartServer(line => Dispatcher.BeginInvoke(new Action(() => HandlePipeCommand(line))));

        // 9.5) 右键菜单 verb 幂等校正
        ApplyContextMenuSetting();

        // 9.6) 首实例启动时携带的 PIN/UNPIN 命令
        if (_pendingCommand is not null)
        {
            var cmd = _pendingCommand; _pendingCommand = null;
            Dispatcher.BeginInvoke(new Action(() => HandlePipeCommand(cmd)));
        }

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

    // ---- 右键固定 / 取消固定 ----

    /// <summary>
    /// 无头布局自检：构造固定尺寸序列，调 MetroPack.Layout，把 unit 网格坐标表写进
    /// %LOCALAPPDATA%\TileStartMenu\layout-test.log。不建窗口、不碰 menu.json。
    /// unit 语义：小=1×1、中=2×2、宽=4×2、大=4×4（含 gap 的整格）。
    /// </summary>
    private static void RunLayoutTest()
    {
        var sb = new StringBuilder();
        const int Rows = 14;
        double Pitch = TileMetrics.Cell + TileMetrics.Gap;
        Size S = new(TileMetrics.Width(TileSize.Small), TileMetrics.Height(TileSize.Small));
        Size M = new(TileMetrics.Width(TileSize.Medium), TileMetrics.Height(TileSize.Medium));
        Size W = new(TileMetrics.Width(TileSize.Wide), TileMetrics.Height(TileSize.Wide));
        Size L = new(TileMetrics.Width(TileSize.Large), TileMetrics.Height(TileSize.Large));
        string Tag(Size z) => z == S ? "S" : z == W ? "W" : z == L ? "L" : "M";

        void Case(string title, List<(string nm, Size z)> tiles)
        {
            sb.AppendLine($"=== {title} rowsPerColumn={Rows} ===");
            var sizes = tiles.Select(t => t.z).ToList();
            var names = tiles.Select(t => t.nm).ToList();
            string? viol = null;
            var rects = MetroPack.Layout(sizes, Rows, names, line =>
            {
                if (line.Contains("VIOLATION")) viol = line;
            });
            sb.AppendLine("idx name size x y w h");
            int maxColEnd = 0;
            for (int i = 0; i < rects.Count; i++)
            {
                int x = (int)Math.Round(rects[i].X / Pitch);
                int y = (int)Math.Round(rects[i].Y / Pitch);
                int w = (int)Math.Round((rects[i].Width + TileMetrics.Gap) / Pitch);
                int h = (int)Math.Round((rects[i].Height + TileMetrics.Gap) / Pitch);
                sb.AppendLine($"{i + 1,-3}{tiles[i].nm,-6}{Tag(tiles[i].z),-4}{x,-3}{y,-3}{w,-3}{h}");
                maxColEnd = Math.Max(maxColEnd, x + w);
            }
            sb.AppendLine($"COLUMNS={maxColEnd}");
            sb.AppendLine($"VIOLATION={(viol ?? "none")}");
            sb.AppendLine();
        }

        // CASE-1 对齐用例：中,中,小,小,中,小,小,宽,大
        Case("CASE-1 align", new()
        {
            ("中1", M), ("中2", M), ("小1", S), ("小2", S),
            ("中3", M), ("小3", S), ("小4", S), ("宽1", W), ("大1", L)
        });

        // CASE-2a 列尾余格：6 个中占 rows0-11，小必须落列尾 (0,12)/(0,13) 不开新列；
        // 紧接着的中 2x2 塞不下 14 行，必须开新列。
        Case("CASE-2a column-tail", new()
        {
            ("中1", M), ("中2", M), ("中3", M), ("中4", M), ("中5", M), ("中6", M),
            ("小1", S), ("小2", S), ("中7", M)
        });

        // CASE-2b 桥洞回填：中(0,0) + 宽(0,2,4x2) 后，cols2-3 rows0-1 是悬空区，
        // 小必须落进 (2,0) 而非 (0,4)。
        Case("CASE-2b bridged-hole", new() { ("中1", M), ("宽1", W), ("小1", S) });

        // CASE-3 单组 12 个中：应跨 >=3 列
        {
            var t = new List<(string, Size)>();
            for (int i = 1; i <= 12; i++) t.Add(($"中{i}", M));
            Case("CASE-3 one-group-12", t);
        }

        // CASE-4 两组宽度：A 12 中、B 2 中
        {
            var a = new List<(string, Size)>();
            for (int i = 1; i <= 12; i++) a.Add(($"A{i}", M));
            var ra = MetroPack.Layout(a.Select(x => x.Item2).ToList(), Rows, null, null);
            int aEnd = 0; foreach (var r in ra) aEnd = Math.Max(aEnd, (int)Math.Round((r.X + r.Width + TileMetrics.Gap) / Pitch));
            var b = new List<(string, Size)>() { ("B1", M), ("B2", M) };
            var rb = MetroPack.Layout(b.Select(x => x.Item2).ToList(), Rows, null, null);
            int bEnd = 0; foreach (var r in rb) bEnd = Math.Max(bEnd, (int)Math.Round((r.X + r.Width + TileMetrics.Gap) / Pitch));
            sb.AppendLine("=== CASE-4 two-groups-widths ===");
            sb.AppendLine($"GROUP-A colStart=0 colSpan={aEnd} (12 medium)");
            sb.AppendLine($"GROUP-B colStart=0 colSpan={bEnd} (2 medium)");
            sb.AppendLine($"WIDTHS-DIFFER={aEnd != bEnd}");
            sb.AppendLine();
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TileStartMenu");
        File.WriteAllText(Path.Combine(dir, "layout-test.log"), sb.ToString());
        UiLog("LAYOUT-TEST written to layout-test.log");
    }

    private static string? ParseCliCommand(string[] args, out string? path)
    {
        path = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--pin" || a == "/pin") { if (i + 1 < args.Length) path = args[i + 1]; return "PIN"; }
            if (a == "--unpin" || a == "/unpin") { if (i + 1 < args.Length) path = args[i + 1]; return "UNPIN"; }
        }
        return null;
    }

    private void HandlePipeCommand(string line)
    {
        if (line == "SHOW") { ToggleMenu(); return; }
        if (line == "SHOT")
        {
            // 自渲染快照：不抢焦点、不受截图缩放影响，DIP 1:1 存盘
            var p = Path.Combine(_store.RootDir, "wall-render.png");
            UiLog($"PIPE SHOT requested -> {p}");
            Dispatcher.BeginInvoke(new Action(() => _menuWindow?.SaveSelfRender(p)));
            return;
        }
        if (line == "ZOOM")
        {
            UiLog("PIPE ZOOM requested");
            Dispatcher.BeginInvoke(new Action(() => _menuWindow?.CommandToggleZoom()));
            return;
        }
        if (line.StartsWith("SCROLL|"))
        {
            if (double.TryParse(line[7..], out double sx))
                Dispatcher.BeginInvoke(new Action(() => _menuWindow?.CommandScroll(sx)));
            return;
        }
        int sep = line.IndexOf('|');
        if (sep <= 0) { ToggleMenu(); return; }
        string cmd = line[..sep];
        string path = line[(sep + 1)..];
        path = path.Trim().Trim('"');
        try
        {
            if (cmd == "PIN") PinFromContextMenu(path);
            else if (cmd == "UNPIN") UnpinFromContextMenu(path);
        }
        catch (Exception ex) { UiLog($"{cmd}-FROM-CONTEXT error: {ex.Message} path={path}"); }
    }

    private void PinFromContextMenu(string path)
    {
        if (_store is null || _icons is null) { UiLog("PIN-FROM-CONTEXT ok=False reason=no-store"); return; }
        if (!File.Exists(path)) { UiLog($"PIN-FROM-CONTEXT ok=False reason=not-exists path={path}"); return; }

        var resolved = ResolveInput(path);
        if (resolved is null)
        {
            UiLog($"RESOLVE arg={path} target= N/A matched=none");
            UiLog($"PIN-FROM-CONTEXT ok=False reason=not-lnk-or-exe path={path}");
            return;
        }
        var (name, kind, target, args, wd) = resolved.Value;

        var cfg = _store.Current;
        string normTarget = NormalizeTarget(target);
        string normRaw = NormalizeTarget(path);

        // 1) 已 pinned 的同 target → 不新增
        var existing = cfg.Tiles.FirstOrDefault(t => t.Pinned &&
            (NormalizeTarget(t.Target) == normTarget || NormalizeTarget(t.Target) == normRaw));
        if (existing is not null)
        {
            UiLog($"RESOLVE arg={path} target={target} matched={existing.Name} (already pinned)");
            UiLog($"PIN-FROM-CONTEXT ok=True name={existing.Name} (already pinned, no new tile)");
            ShowMenuAfterPin();
            return;
        }

        // 2) 之前 unpin 留下的同 target 记录 → 复用（删了重建或直接 Pinned=true）
        var orphan = cfg.Tiles.FirstOrDefault(t => !t.Pinned &&
            (NormalizeTarget(t.Target) == normTarget || NormalizeTarget(t.Target) == normRaw));
        if (orphan is not null)
        {
            orphan.Pinned = true;
            orphan.Name = name;
            orphan.Target = target;
            orphan.Arguments = args;
            orphan.WorkingDirectory = wd;
            orphan.GroupId = MenuConfig.DefaultGroupId;
            int maxOrder = cfg.Tiles.Where(t => t.GroupId == MenuConfig.DefaultGroupId).Select(t => t.Order).DefaultIfEmpty(-1).Max();
            orphan.Order = maxOrder + 1;
            _store.Save(cfg);
            UiLog($"RESOLVE arg={path} target={target} matched={orphan.Name} (reused orphan)");
            UiLog($"PIN-FROM-CONTEXT ok=True name={orphan.Name} (reused old tile, no new row)");
            _icons.RequestIcon(orphan, _ => { });
            ShowMenuAfterPin();
            return;
        }

        int nextOrder = cfg.Tiles.Where(t => t.GroupId == MenuConfig.DefaultGroupId).Select(t => t.Order).DefaultIfEmpty(-1).Max() + 1;
        var tile = new TileItem
        {
            Name = name, Kind = kind, Target = target, Arguments = args, WorkingDirectory = wd,
            Color = TilePalette.ColorForName(name), Size = TileSize.Medium,
            GroupId = MenuConfig.DefaultGroupId, Order = nextOrder, Pinned = true
        };
        cfg.Tiles.Add(tile);
        _store.Save(cfg);
        UiLog($"RESOLVE arg={path} target={target} matched=none (new tile)");
        UiLog($"PIN-FROM-CONTEXT ok=True name={name} target={target}");
        _icons.RequestIcon(tile, _ => { });
        ShowMenuAfterPin();
    }

    private void UnpinFromContextMenu(string path)
    {
        if (_store is null) { UiLog("UNPIN-FROM-CONTEXT ok=False reason=no-store"); return; }
        var resolved = ResolveInput(path);
        string target = resolved?.Target ?? path;
        var cfg = _store.Current;
        string normTarget = NormalizeTarget(target);
        string normRaw = NormalizeTarget(path);

        // 同时匹配 target 和原始 .lnk 路径，避免 .lnk→exe 解析后对不上
        var tile = cfg.Tiles.FirstOrDefault(t =>
            NormalizeTarget(t.Target) == normTarget || NormalizeTarget(t.Target) == normRaw);
        if (tile is null)
        {
            UiLog($"RESOLVE arg={path} target={target} matched=none");
            UiLog($"UNPIN-FROM-CONTEXT ok=False reason=not-pinned path={path}");
            return;
        }
        string matchedName = tile.Name;
        cfg.Tiles.Remove(tile);
        _store.Save(cfg);
        UiLog($"RESOLVE arg={path} target={target} matched={matchedName} (removed)");
        UiLog($"UNPIN-FROM-CONTEXT ok=True name={matchedName}");
    }

    private static (string Name, TileActionKind Kind, string Target, string? Args, string? Wd)? ResolveInput(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var parsed = ShortcutResolver.Resolve(path);
            if (parsed is not null && File.Exists(parsed.Target))
                return (name, TileActionKind.Launch, parsed.Target, parsed.Arguments, parsed.WorkingDirectory);
            return (name, TileActionKind.Launch, path, null, null);
        }
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (Path.GetFileNameWithoutExtension(path), TileActionKind.Launch, path, null, null);
        }
        return null;
    }

    private static string NormalizeTarget(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "";
        return t.Trim().Trim('"').Replace('/', '\\').ToLowerInvariant();
    }

    private void ShowMenuAfterPin()
    {
        if (_menuWindow is null) return;
        // 菜单已开就就地刷新（Normalize/Save 已经触发 Changed，UI 会重建墙）；没开就打开它
        if (!_menuWindow.IsMenuOpen)
        {
            _prevForeground = GetForegroundWindow();
            _menuWindow.ShowMenu();
        }
    }

    private void ApplyContextMenuSetting()
    {
        if (_store is null) return;
        bool want = _store.Current.Settings.DesktopContextMenu;
        bool have = ContextMenuRegistry.IsInstalled();
        if (want && !have) ContextMenuRegistry.Install();
        else if (!want && have) ContextMenuRegistry.Uninstall();
        RefreshTrayChecks();
    }

    private void ToggleContextMenuSetting()
    {
        if (_store is null) return;
        var cfg = _store.Current;
        cfg.Settings.DesktopContextMenu = !cfg.Settings.DesktopContextMenu;
        _store.Save(cfg);
        UiLog($"ContextMenu toggled -> {cfg.Settings.DesktopContextMenu}");
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
        ApplyContextMenuSetting();
        RefreshTrayChecks();
    }

    private void RefreshTrayChecks()
    {
        if (_tray is null || _store is null) return;
        _tray.WinKeyChecked = _store.Current.Settings.WinKeyEnabled;
        _tray.AutostartChecked = StartupRegistry.IsEnabled();
        _tray.ContextMenuChecked = _store.Current.Settings.DesktopContextMenu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _hook?.Stop(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _single?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
