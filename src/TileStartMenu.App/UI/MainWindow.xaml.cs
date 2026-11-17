using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TileStartMenu.App;
using TileStartMenu.Core.Catalog;
using TileStartMenu.Core.Icons;
using TileStartMenu.Core.Launcher;
using TileStartMenu.Core.Models;
using TileStartMenu.Core.Persistence;
using TileStartMenu.Core.Power;

namespace TileStartMenu.App.UI;

/// <summary>
/// Metro 磁贴墙主窗口（v3：Win8.1 风格）。
/// - 全屏铺满（含任务栏区域）；
/// - 磁贴墙虚拟化（只实例化可视区磁贴），图标异步回填；
/// - 底部 App Bar 替代右键小菜单：取消固定 / 调整大小 / 改颜色 / 重命名 / 删除；
/// - 「全部应用」页按首字母分组，右键固定/取消固定；
/// - 拖拽半透明跟随、其余磁贴实时让位；Esc / 失焦 / ToggleMenu 关闭。
/// 不含：Win 键钩子、注册表、托盘（系统集成层负责）。
/// </summary>
public partial class MainWindow : Window, IMenuWindow
{
    private readonly IConfigStore _store;
    private readonly ITileLauncher _launcher;
    private readonly IIconService? _icons;
    private readonly IAppCatalog? _catalog;
    private readonly IPowerService? _power;

    // ===== 布局模型 =====
    private sealed class TilePlacement
    {
        public required TileItem Tile { get; init; }
        public Rect Rect;
    }

    private sealed class GroupLayout
    {
        public required TileGroup Group { get; init; }
        public double PanelX;       // 横向：分组区块左缘
        public double PanelWidth;
        public double PanelHeight;
        public List<TilePlacement> Tiles { get; } = new();
        public TextBlock HeaderText = null!;
    }

    private readonly List<GroupLayout> _groups = new();
    private double _contentWidth;
    private const double GroupX = 40;
    private const double TopPad = 70;       // 给右上时钟/电源留空间
    private const double GroupGap = 60;     // 分组之间横向间距
    private const double HeaderHeight = 30;
    private double _vOffset = 80;   // 磁贴块垂直居中后的 Y 偏移

    // ===== 虚拟化池 =====
    private readonly Dictionary<string, TileControl> _pool = new();
    private readonly HashSet<string> _iconQueued = new();
    private readonly SemaphoreSlim _iconThrottle = new(4, 4);

    // ===== 拖拽 =====
    private TileControl? _pressTile;
    private Point _pressPos;
    private Point _dragOffset;
    private DispatcherTimer? _holdTimer;
    private bool _isDragging;
    private TileGroup? _dropGroup;
    private int _dropIndex;
    private Rectangle? _dropOutline;
    private GroupLayout? _groupHighlighted;
    private bool _suppressFocusClose;

    // ===== 分组总览（语义缩放，Win8.1 式） =====
    private bool _zoomOpen;
    private readonly List<(Border card, TileGroup group, double baseX)> _zoomGroupCards = new();
    private Border? _zoomDragCard;
    private TileGroup? _zoomDragGroup;
    private Point _zoomDownPos;
    private bool _zoomDragging;
    private double _zoomCardOriginX, _zoomCardOriginY;

    // ===== 背景横向跟手拖动（抓纸式） =====
    private bool _wallDragArmed;
    private bool _wallDragging;
    private Point _wallDragDownPos;
    private double _wallDragStartOffset;

    // ===== App Bar =====
    private enum BarContext { None, WallTile, AllApps }
    private BarContext _barContext = BarContext.None;
    private TileItem? _barTile;
    private AppEntry? _barEntry;

    // ===== 全部应用缓存 =====
    private IReadOnlyList<AppEntry>? _allApps;
    private bool _allAppsLoading;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _errorTimer;

    public MainWindow(IConfigStore store, ITileLauncher launcher,
        IIconService? icons = null, IAppCatalog? catalog = null, IPowerService? power = null)
    {
        _store = store;
        _launcher = launcher;
        var app = Application.Current as App;
        _icons = icons ?? app?.Icons;
        _catalog = catalog ?? TryGetProperty<IAppCatalog>(app, "Catalog");
        _power = power ?? TryGetProperty<IPowerService>(app, "Power");

        InitializeComponent();
        ApplySettings();
        RebuildLayout();

        _store.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            ApplySettings();
            RebuildLayout();
            RefreshAllAppsTags();
        });

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();

        _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _errorTimer.Tick += (_, _) => { ErrorBar.Visibility = Visibility.Collapsed; _errorTimer.Stop(); };

        AllAppsPage.RowClicked += AllAppsRowClicked;
        AllAppsPage.RowRightClicked += AllAppsRowRightClicked;

        // 窗口级右键路由兜底：沿可视化树找 TileItem（DataContext）
        PreviewMouseRightButtonUp += (s, args) =>
        {
            try
            {
                var el = args.OriginalSource as DependencyObject;
                while (el != null)
                {
                    if (el is FrameworkElement fe && fe.DataContext is TileItem ti)
                    {
                        args.Handled = true;
                        OpenWallBar(ti);
                        return;
                    }
                    el = VisualTreeHelper.GetParent(el);
                }
            }
            catch { }
        };

        // 窗口级左键抬起兜底：即使按下期间控件被回收，也保证有人接 Up
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(WindowLevel_MouseLeftButtonUp), handledEventsToo: true);
        // 窗口级左键按下探针：验证点击是否到达窗口
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
        {
            var p = e.GetPosition(this);
            var src = e.OriginalSource as DependencyObject;
            string extra = "";
            if (src is TextBlock tb) extra += $" text=\"{tb.Text}\"";
            if (src is FrameworkElement fe) extra += $" dc={(fe.DataContext?.GetType().Name ?? "null")}";
            // 走一遍父链，确认命中元素归属
            var chain = new List<string>();
            var cur = src;
            for (int i = 0; i < 8 && cur != null; i++)
            {
                chain.Add(cur.GetType().Name);
                cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
            }
            // 直接对窗口做一次命中测试，看最上层到底是谁
            string hitTop = "null";
            try
            {
                var res = VisualTreeHelper.HitTest(RootGrid, p);
                hitTop = res?.VisualHit?.GetType().Name ?? "null";
                if (res?.VisualHit is TextBlock htb) hitTop += $" text=\"{htb.Text}\"";
                if (res?.VisualHit is FrameworkElement hfe) hitTop += $" dc={(hfe.DataContext?.GetType().Name ?? "null")}";
            }
            catch (Exception ex) { hitTop = "err:" + ex.Message; }
            Dbg($"WINDOW-DOWN src={src?.GetType().Name} pos=({p.X:0},{p.Y:0}){extra} chain=[{string.Join("<", chain)}] hittest={hitTop}");
        }), handledEventsToo: true);

        SizeChanged += (_, _) => RebuildLayout();
        KeyDown += MainWindow_KeyDown;
    }

    // ==================== IMenuWindow ====================

    public bool IsMenuOpen => Visibility == Visibility.Visible;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);

    public void ShowMenu()
    {
        Dbg("SHOWMENU");
        // 全屏铺满（含任务栏区域）。注意：必须用 SystemParameters 的 DIP 值，
        // GetSystemMetrics 返回的是物理像素，在高 DPI（如 150%）下会被 WPF 当 DIP 再放大 1.5 倍，
        // 导致右对齐的时钟、底部对齐的「所有应用」被推出屏幕外。
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Dbg($"WIN left={Left:0.0} top={Top:0.0} width={Width:0.0} height={Height:0.0} vsw={SystemParameters.VirtualScreenWidth:0.0} vsh={SystemParameters.VirtualScreenHeight:0.0}");

        if (!IsMenuOpen)
        {
            RebuildLayout();
            ApplySettings();
            Show();
        }
        Activate();
        Topmost = true;
        Focus();
        UpdateVisible();
        // PointToScreen 要等窗口渲染后才可靠，低优先级排到布局/渲染完成后再打
        Dispatcher.BeginInvoke(new Action(LogGeom), System.Windows.Threading.DispatcherPriority.Loaded);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        RootGrid.BeginAnimation(OpacityProperty, fade);
        var rise = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(150));
        RootTransform.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    public void HideMenu()
    {
        CloseAppBar();
        Hide();
        RootGrid.Opacity = 1;
    }

    public void ToggleMenu()
    {
        if (IsMenuOpen) HideMenu();
        else ShowMenu();
    }

    // ==================== 设置 / 时钟 ====================

    private void ApplySettings()
    {
        var s = _store.Current.Settings;
        var c = MetroColors.Parse(s.BackgroundColor);
        c.A = (byte)(Math.Clamp(s.BackgroundOpacity, 0, 1) * 255);
        RootGrid.Background = new SolidColorBrush(c);

        // 时钟始终显示（Win8.1 固定元素），ShowClock 仅控制 DateText
        ClockText.Visibility = Visibility.Visible;
        DateText.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("yyyy/M/d ddd");
    }

    /// <summary>渲染后打印窗口/固定元素的物理坐标，用于诊断"窗口是否大于屏幕"。</summary>
    private void LogGeom()
    {
        try
        {
            var cp = ClockText.PointToScreen(new Point(0, 0));
            var ap = AllAppsEntry.PointToScreen(new Point(0, 0));
            var sp = SemanticZoomButton.PointToScreen(new Point(0, 0));
            Dbg($"GEOM actualW={ActualWidth:0.0} actualH={ActualHeight:0.0} " +
                $"contentW={TileCanvas.Width:0.0} contentH={TileCanvas.Height:0.0} " +
                $"clockScreen=({cp.X:0},{cp.Y:0}) appsScreen=({ap.X:0},{ap.Y:0}) gearScreen=({sp.X:0},{sp.Y:0}) " +
                $"rootPhys=({Left:0},{Top:0},{Width:0.0},{Height:0.0})");
        }
        catch (Exception ex) { Dbg($"GEOM error: {ex.Message}"); }
    }

    /// <summary>
    /// 自渲染快照：用 RenderTargetBitmap 把 RootGrid 按 DIP 1:1 画成 PNG 存盘，
    /// 不依赖外部截屏、不受 DPI 缩放/抢焦点影响。SHOT 管道命令与 F11 都走这里。
    /// </summary>
    public void SaveSelfRender(string path)
    {
        try
        {
            double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
            if (w < 10 || h < 10)
            {
                Dbg($"SHOT skip: root too small {w:0.0}x{h:0.0} (menu hidden?)");
                return;
            }
            int pw = (int)Math.Round(w);
            int ph = (int)Math.Round(h);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(pw, ph, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            RootGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            RootGrid.Arrange(new Rect(new Size(w, h)));
            rtb.Render(RootGrid);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            enc.Save(fs);
            Dbg($"SHOT path={path} size={pw}x{ph}");
        }
        catch (Exception ex) { Dbg($"SHOT error: {ex.GetType().Name}: {ex.Message}"); }
    }

    // ==================== 布局（纯数据） ====================

    private void RebuildLayout()
    {
        var cfg = _store.Current;
        _groups.Clear();
        TileCanvas.Children.Clear();
        _pool.Clear();
        _iconQueued.Clear();
        OverlayCanvas.Children.Clear();
        // 分组标题现在挂在 TileCanvas 上，TileCanvas.Children.Clear() 已统一清理，
        // 不再从 ContentGrid 里按类型挑 TextBlock 删除（那会误伤时钟等装饰元素）。

        // 每列可叠多少行（DIP）：扣掉顶部时钟留白与底部「所有应用」条。
        // 注意 WPF 的 ActualHeight 本就是 DIP，不要再乘 DPI 缩放或取物理像素。
        const double TopReserve = 90;    // 顶部时钟/电源/齿轮留白
        const double BottomReserve = 80; // 底部「所有应用 ↓」条
        double usableH = Math.Max(ActualHeight - TopReserve - BottomReserve, 300);
        int rowsPerColumn = Math.Max(2, (int)Math.Floor((usableH + TileMetrics.Gap) / (TileMetrics.Cell + TileMetrics.Gap)));
        Dbg($"LAYOUT rowsPerColumn={rowsPerColumn} usableH={usableH:0.0} actualHeight={ActualHeight:0.0}");

        // 垂直居中：计算所有分组的最大内容高度，把整组磁贴块居中
        double maxGroupH = 0;
        {
            double x0 = GroupX;
            foreach (var g in cfg.Groups.OrderBy(x => x.Order))
            {
                var tiles = cfg.Tiles.Where(t => t.GroupId == g.Id && t.Pinned).OrderBy(t => t.Order).ToList();
                var sizes = tiles.Select(t => new Size(TileMetrics.Width(t.Size), TileMetrics.Height(t.Size))).ToList();
                var rects = MetroPack.Layout(sizes, rowsPerColumn);
                var cs = MetroPack.ContentSize(rects);
                if (cs.Height > maxGroupH) maxGroupH = cs.Height;
                x0 += cs.Width + GroupGap;
            }
        }
        double vOffset = Math.Max(10, (ActualHeight - maxGroupH) / 2 - 10);
        _vOffset = vOffset;

        double x = GroupX;
        foreach (var g in cfg.Groups.OrderBy(x => x.Order))
        {
            var gl = new GroupLayout { Group = g };

            int pinnedCount = cfg.Tiles.Count(t => t.GroupId == g.Id && t.Pinned);
            var header = new TextBlock
            {
                Text = g.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xBC, 0xC8)),
                FontSize = 15,
                // 纯装饰文字：绝不能参与命中测试，否则会吃掉磁贴的点击
                IsHitTestVisible = false
            };
            Canvas.SetLeft(header, x);
            Canvas.SetTop(header, vOffset - HeaderHeight - 12);
            // 必须加到真正的 Canvas（TileCanvas）上，Canvas.SetLeft/Top 才生效；
            // 早前加在 ContentGrid（Grid）里，定位失效且默认 Stretch 会铺满整面墙、盖在磁贴之上，
            // 导致所有磁贴点击的命中对象都变成分组标题 —— 这正是"点磁贴没反应"的根因。
            header.IsHitTestVisible = false;
            TileCanvas.Children.Add(header);
            gl.HeaderText = header;

            gl.PanelX = x;
            gl.PanelHeight = usableH;
            var tiles = cfg.Tiles.Where(t => t.GroupId == g.Id && t.Pinned).OrderBy(t => t.Order).ToList();
            var sizes = tiles.Select(t => new Size(TileMetrics.Width(t.Size), TileMetrics.Height(t.Size))).ToList();
            // 列优先打包：rowsPerColumn 行/列，自动向右扩列；逐块打 unit 网格坐标 + 重叠/越界自检
            var tileNames = tiles.Select(t => t.Name).ToList();
            var rects = MetroPack.Layout(sizes, rowsPerColumn, tileNames, Dbg);
            for (int i = 0; i < tiles.Count; i++)
            {
                gl.Tiles.Add(new TilePlacement { Tile = tiles[i], Rect = rects[i] });
            }
            var cs = MetroPack.ContentSize(rects);
            gl.PanelWidth = cs.Width;
            x += cs.Width + GroupGap;

            _groups.Add(gl);
        }

        double totalW = Math.Max(x, ActualWidth);
        double totalH = Math.Max(vOffset + maxGroupH + 20, ActualHeight);
        ContentGrid.Width = totalW;
        ContentGrid.Height = totalH;
        TileCanvas.Width = totalW;
        TileCanvas.Height = totalH;
        OverlayCanvas.Width = totalW;
        OverlayCanvas.Height = totalH;

        int pinnedTotal = cfg.Tiles.Count(t => t.Pinned);
        EmptyState.Visibility = pinnedTotal == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateVisible();
    }

    private int MaxColsForViewport() =>
        Math.Max(4, (int)Math.Floor((_contentWidth + TileMetrics.Gap) / MetroPack.Pitch));

    // ==================== 虚拟化 ====================

    private void MainScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateVisible();

    private void UpdateVisible()
    {
        double vpX = WallScroll.HorizontalOffset;
        double vpW = WallScroll.ViewportWidth;
        const double margin = 400;

        foreach (var gl in _groups)
        {
            foreach (var pl in gl.Tiles)
            {
                double absX = gl.PanelX + pl.Rect.X;
                double absY = _vOffset + pl.Rect.Y;
                bool visible = absX + pl.Rect.Width > vpX - margin && absX < vpX + vpW + margin;

                if (visible) EnsureControl(pl, absX, absY);
                else if (_pool.TryGetValue(pl.Tile.Id, out var c) && c != _pressTile)
                {
                    TileCanvas.Children.Remove(c);
                    _pool.Remove(pl.Tile.Id);
                }
            }
        }
    }

    private void EnsureControl(TilePlacement pl, double x, double y)
    {
        if (_pool.TryGetValue(pl.Tile.Id, out var existing))
        {
            Canvas.SetLeft(existing, x);
            Canvas.SetTop(existing, y);
            return;
        }

        var ctrl = new TileControl();
        ctrl.Bind(pl.Tile, null);
        ctrl.DataContext = pl.Tile;
        Canvas.SetLeft(ctrl, x);
        Canvas.SetTop(ctrl, y);
        TileCanvas.Children.Add(ctrl);
        _pool[pl.Tile.Id] = ctrl;
        AttachMouse(ctrl);
        QueueIconAsync(pl.Tile, ctrl);
    }

    private void QueueIconAsync(TileItem tile, TileControl ctrl)
    {
        if (_icons == null || !_iconQueued.Add(tile.Id)) return;
        if (tile.PowerAction != PowerAction.None) { ctrl.ShowFallbackGlyph(tile.PowerAction); return; }
        var t = tile; var c = ctrl;
        try
        {
            _icons.RequestIcon(t, path =>
            {
                try
                {
                    _iconThrottle.Wait();
                    Dispatcher.Invoke(() =>
                    {
                        if (!TileCanvas.Children.Contains(c)) return;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            try
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.UriSource = new Uri(path, UriKind.Absolute);
                                bmp.EndInit();
                                c.ApplyIcon(bmp);
                            }
                            catch { c.ShowFallbackGlyph(null); }
                        }
                        else
                        {
                            c.ShowFallbackGlyph(null); // 明确失败 → 首字块
                        }
                    }, DispatcherPriority.Background);
                }
                finally { _iconThrottle.Release(); }
            });
        }
        catch { }
    }

    // ==================== 点击 / 拖拽 ====================

    private const double DragThreshold = 6;

    private void AttachMouse(TileControl ctrl)
    {
        ctrl.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
        ctrl.MouseMove += Tile_MouseMove;
        ctrl.MouseLeftButtonUp += Tile_MouseLeftButtonUp;
        ctrl.MouseRightButtonUp += Tile_MouseRightButtonUp;
    }

    private bool _pressArmed;   // 长按 250ms 预备态（不等于拖拽）
    private int _pressSerial;   // 防止过期按下残留

    private static void Dbg(string msg)
    {
        try { File.AppendAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TileStartMenu", "ui-debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    private static void PressDownAnim(TileControl? ctrl)
    {
        if (ctrl == null) return;
        ctrl.Opacity = 0.92;
        var st = new ScaleTransform(1, 1);
        ctrl.RenderTransform = st;
        ctrl.RenderTransformOrigin = new Point(0.5, 0.5);
        st.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(90)));
        st.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(90)));
    }

    private static void PressUpAnim(TileControl? ctrl)
    {
        if (ctrl == null) return;
        ctrl.Opacity = 1.0;
        if (ctrl.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(110)));
            st.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(110)));
        }
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
        if (e.ChangedButton != MouseButton.Left) return;
        Dbg($"DOWN-FIRED sender={sender?.GetType().Name} src={e.OriginalSource?.GetType().Name} handled={e.Handled}");
        CloseAppBar();
        _pressTile = (TileControl)sender;
        _pressPos = e.GetPosition(this);
        _pressArmed = false;
        _isDragging = false;
        _pressSerial++;
        int mySerial = _pressSerial;
        _dragOffset = new Point(_pressPos.X - Canvas.GetLeft(_pressTile), _pressPos.Y - Canvas.GetTop(_pressTile));
        try { _pressTile.CaptureMouse(); } catch { }

        Dbg($"DOWN tile={_pressTile.Tile.Name} pos=({_pressPos.X:0},{_pressPos.Y:0})");

        // 250ms 长按只进入"预备拖拽"armed 态，不在原点开始拖
        _holdTimer?.Stop();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            if (mySerial != _pressSerial) return;
            if (ReferenceEquals(_pressTile, sender) && !_isDragging)
            {
                _pressArmed = true;
                PressDownAnim(_pressTile);
                Dbg($"ARMED tile={_pressTile?.Tile.Name} (按住 250ms，未移动)");
            }
        };
        _holdTimer.Start();
        }
        catch (Exception ex) { Dbg($"DOWN-EX: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        var ctrl = (TileControl)sender;
        if (!ReferenceEquals(ctrl, _pressTile)) return;
        var p = e.GetPosition(this);

        if (_isDragging)
        {
            Canvas.SetLeft(ctrl, p.X - _dragOffset.X);
            Canvas.SetTop(ctrl, p.Y - _dragOffset.Y);
            UpdateDragPosition(p);
            return;
        }

        double dx = p.X - _pressPos.X, dy = p.Y - _pressPos.Y;
        double dist = Math.Abs(dx) + Math.Abs(dy);
        if (dist > DragThreshold)
        {
            _holdTimer?.Stop();
            Dbg($"MOVE->DRAG tile={ctrl.Tile.Name} dist=({dx:0},{dy:0})");
            BeginDrag(ctrl);
        }
    }

    private void BeginDrag(TileControl ctrl)
    {
        _isDragging = true;
        _holdTimer?.Stop();
        ctrl.Opacity = 0.8;
        ctrl.RenderTransform = new ScaleTransform(1.06, 1.06);
        ctrl.RenderTransformOrigin = new Point(0.5, 0.5);
        Dbg($"BEGIN-DRAG tile={ctrl.Tile.Name}");

        _dropGroup = null;
        _dropIndex = 0;
        _dropOutline = new Rectangle
        {
            Width = TileMetrics.Width(ctrl.Tile.Size),
            Height = TileMetrics.Height(ctrl.Tile.Size),
            Stroke = Brushes.White,
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))
        };
        OverlayCanvas.Children.Add(_dropOutline);
    }

    private void UpdateDragPosition(Point winPos)
    {
        if (!_isDragging || _pressTile == null) return;

        // 横向布局：按 X 命中分组
        GroupLayout? best = null;
        foreach (var gl in _groups)
        {
            if (winPos.X >= gl.PanelX - 20 && winPos.X <= gl.PanelX + gl.PanelWidth + 20)
            { best = gl; break; }
        }
        if (best == null)
        {
            double bestDist = double.MaxValue;
            foreach (var gl in _groups)
            {
                double center = gl.PanelX + gl.PanelWidth / 2;
                double d = Math.Abs(center - winPos.X);
                if (d < bestDist) { bestDist = d; best = gl; }
            }
        }
        if (best == null) return;

        if (_groupHighlighted != best)
        {
            foreach (var gl in _groups)
                gl.HeaderText.Background = Brushes.Transparent;
            best.HeaderText.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x33, 0x99, 0xFF));
            _groupHighlighted = best;
        }

        var others = best.Tiles.Where(t => t.Tile.Id != _pressTile.Tile.Id).ToList();
        var sizes = others.Select(t => new Size(t.Rect.Width, t.Rect.Height)).ToList();
        int rowsPerColumn = Math.Max(2, (int)Math.Floor((best.PanelHeight + TileMetrics.Gap) / (TileMetrics.Cell + TileMetrics.Gap)));
        var rects = MetroPack.Layout(sizes, rowsPerColumn);

        double mx = winPos.X - best.PanelX;
        double my = winPos.Y - _vOffset;

        int index = others.Count;
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (my >= r.Top - TileMetrics.Gap && my <= r.Bottom + TileMetrics.Gap && mx >= r.Left - TileMetrics.Gap)
            { index = i; break; }
        }

        sizes.Insert(index, new Size(TileMetrics.Width(_pressTile.Tile.Size), TileMetrics.Height(_pressTile.Tile.Size)));
        var preview = MetroPack.Layout(sizes, rowsPerColumn);
        var dragRect = preview[index];

        Canvas.SetLeft(_dropOutline!, best.PanelX + dragRect.X);
        Canvas.SetTop(_dropOutline!, _vOffset + dragRect.Y);
        _dropOutline!.Visibility = Visibility.Visible;

        _dropGroup = best.Group;
        _dropIndex = index;
    }

    private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var ctrl = sender as TileControl;
        // 不强制 ReferenceEquals：窗口级兜底也会走到这里，只要 _pressTile 有值就处理
        if (ctrl == null || !ReferenceEquals(ctrl, _pressTile)) return;
        FinishPress(ctrl, e);
    }

    /// <summary>统一的按下收尾：区分"点击启动"与"拖拽提交"。</summary>
    private void FinishPress(TileControl ctrl, MouseButtonEventArgs e)
    {
        bool wasDragging = _isDragging;
        bool wasArmed = _pressArmed;
        _holdTimer?.Stop();
        try { _pressTile?.ReleaseMouseCapture(); } catch { }

        var p = e.GetPosition(this);
        double dx = p.X - _pressPos.X, dy = p.Y - _pressPos.Y;
        double dist = Math.Abs(dx) + Math.Abs(dy);

        Dbg($"UP tile={ctrl.Tile.Name} dist=({dx:0},{dy:0}) armed={wasArmed} dragging={wasDragging}");

        _isDragging = false;
        _pressArmed = false;
        _pressTile = null;
        _pressSerial++;

        PressUpAnim(ctrl);
        if (_dropOutline != null) { OverlayCanvas.Children.Remove(_dropOutline); _dropOutline = null; }
        foreach (var gl in _groups) gl.HeaderText.Background = Brushes.Transparent;
        _groupHighlighted = null;

        if (wasDragging) { Dbg($"COMMIT-DRAG tile={ctrl.Tile.Name}"); CommitDrag(ctrl); return; }

        // 位移始终 ≤6px：无论按了多久，都算点击
        if (dist <= DragThreshold)
        {
            Dbg($"CLICK->LAUNCH tile={ctrl.Tile.Name}");
            LaunchTile(ctrl.Tile);
        }
        else
        {
            Dbg($"IGNORE-UP tile={ctrl.Tile.Name} dist={dist:0}");
        }
    }

    private void CommitDrag(TileControl ctrl)
    {
        if (_dropGroup == null) return;
        var cfg = _store.Current;
        var item = ctrl.Tile;

        cfg.Tiles.Remove(item);
        item.GroupId = _dropGroup.Id;

        var inGroup = cfg.Tiles.Where(t => t.GroupId == _dropGroup.Id).OrderBy(t => t.Order).ToList();
        var rebuilt = new List<TileItem>();
        foreach (var t in cfg.Tiles.Where(t => t.GroupId != _dropGroup.Id)) rebuilt.Add(t);

        int idx = Math.Clamp(_dropIndex, 0, inGroup.Count);
        for (int i = 0; i < inGroup.Count; i++)
        {
            if (i == idx) rebuilt.Add(item);
            rebuilt.Add(inGroup[i]);
        }
        if (idx >= inGroup.Count) rebuilt.Add(item);

        cfg.Tiles.Clear();
        cfg.Tiles.AddRange(rebuilt);
        SaveCurrent();
    }

    private void LaunchTile(TileItem tile)
    {
        Dbg($"LAUNCH tile={tile.Name} kind={tile.Kind} target={tile.Target}");
        if (tile.PowerAction != PowerAction.None)
        {
            OpenPowerChooser();
            PowerChoice_Click(new Button { Tag = tile.PowerAction.ToString() }, new RoutedEventArgs());
            return;
        }
        bool ok = _launcher.Execute(tile, out var error);
        Dbg($"LAUNCH-RESULT tile={tile.Name} ok={ok} error={error ?? "<null>"}");
        if (ok) HideMenu();
        else ShowError($"「{tile.Name}」启动失败：{error}");
    }

    private void WindowLevel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 兜底：如果磁贴自己的 Up 没收到（被回收等），且 _pressTile 还在，按 FinishPress 处理
        if (_pressTile == null) return;
        Dbg($"WINDOW-UP fallback src={e.OriginalSource?.GetType().Name}");
        try { FinishPress(_pressTile, e); } catch { }
    }

    // ===== 电源浮层 =====
    private void OpenPowerChooser()
    {
        CloseAppBar();
        PowerChooser.Visibility = Visibility.Visible;
        PowerConfirmPanel.Visibility = Visibility.Collapsed;
        PowerPopup.Visibility = Visibility.Visible;
    }

    private void PowerButton_Click(object sender, RoutedEventArgs e) => OpenPowerChooser();

    private void PowerChoice_Click(object sender, RoutedEventArgs e)
    {
        var action = (PowerAction)Enum.Parse(typeof(PowerAction), (string)((Button)sender).Tag);
        PowerChooser.Visibility = Visibility.Collapsed;
        PowerConfirmPanel.Visibility = Visibility.Visible;
        PowerPopup.Tag = action;
        PowerTitle.Text = action switch
        {
            PowerAction.Shutdown => "确定要关机吗？",
            PowerAction.Restart => "确定要重启吗？",
            _ => ""
        };
        PowerConfirmText.Text = action is PowerAction.Shutdown or PowerAction.Restart
            ? "所有未保存的工作将丢失，确定继续？"
            : "即将执行该操作。";
        PowerConfirmButton.Content = action is PowerAction.Shutdown or PowerAction.Restart ? "确定" : "执行";
    }

    private void PowerConfirm_Click(object sender, RoutedEventArgs e)
    {
        var action = (PowerAction)PowerPopup.Tag;
        PowerPopup.Visibility = Visibility.Collapsed;
        // 二次确认后先 Arm（10 秒内允许执行）再 Execute
        try { _power?.Arm(action); } catch { }
        string? error = null;
        bool ok = _power != null && _power.Execute(action, out error);
        if (!ok) ShowError($"电源操作失败：{error ?? "IPowerService 尚未就绪"}");
        else HideMenu();
    }

    private void PowerCancel_Click(object sender, RoutedEventArgs e) => PowerPopup.Visibility = Visibility.Collapsed;

    private void PopupInner_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    // ==================== 底部 App Bar（Win8.1） ====================

    private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var ctrl = (TileControl)sender;
        OpenWallBar(ctrl.Tile);
    }

    private void OpenWallBar(TileItem tile)
    {
        _barContext = BarContext.WallTile;
        _barTile = tile;
        _barEntry = null;
        AppBarTitle.Text = tile.Name;
        AppBarButtons.Children.Clear();

        bool power = tile.PowerAction != PowerAction.None;
        if (!power)
        {
            AppBarButtons.Children.Add(MakeBarButton("📌", "取消固定", () => UnpinTile(tile)));
            AppBarButtons.Children.Add(MakeBarButton("🗑", "删除", () => DeleteTile(tile)));
        }
        AppBarButtons.Children.Add(MakeBarButton("⤢", "调整大小", () => CycleSize(tile)));
        AppBarButtons.Children.Add(MakeBarButton("🎨", "改颜色", () => ChangeColor(tile)));
        AppBarButtons.Children.Add(MakeBarButton("🔍", "图标规格", () => CycleIconScale(tile)));
        if (!power) AppBarButtons.Children.Add(MakeBarButton("✏", "重命名", () => RenameTile(tile)));

        ShowAppBar();
    }

    private static readonly double[] IconScaleSteps = { 0.5, 0.6, 0.75, 1.0 };
    private void CycleIconScale(TileItem tile)
    {
        double cur = tile.IconScale <= 0 ? 1.0 : tile.IconScale;
        int idx = Array.IndexOf(IconScaleSteps, cur);
        idx = (idx + 1) % IconScaleSteps.Length;
        tile.IconScale = IconScaleSteps[idx];
        SaveCurrent();
        // 重新 Bind 该磁贴以应用新规格
        if (_pool.TryGetValue(tile.Id, out var c))
        {
            c.Bind(tile, GetIconPathFor(tile));
        }
    }

    private string? GetIconPathFor(TileItem tile)
    {
        try
        {
            return _icons?.GetIconPngPath(tile);
        }
        catch { return null; }
    }

    /// <summary>右键空白背景 → 底部 AppBar「自定义」。</summary>
    private void OpenWallCustomizeBar()
    {
        _barContext = BarContext.None;
        _barTile = null;
        _barEntry = null;
        AppBarTitle.Text = "自定义";
        AppBarButtons.Children.Clear();
        AppBarButtons.Children.Add(MakeBarButton("➕", "新建分组", AddNewGroup));
        AppBarButtons.Children.Add(MakeBarButton("✏", "重命名分组", RenameFirstGroup));
        AppBarButtons.Children.Add(MakeBarButton("🎨", "主题色", () => ChangeWallColor()));
        ShowAppBar();
    }

    private static class Interaction
    {
        public static string? Input(string prompt, string title, string defaultValue = "")
            => InputDialog.Show(Application.Current.MainWindow, title, prompt, defaultValue);
    }

    private void AddNewGroup()
    {
        var name = Interaction.Input("新分组名称：", "新建分组");
        if (string.IsNullOrWhiteSpace(name)) return;
        var cfg = _store.Current;
        int maxOrder = cfg.Groups.Count == 0 ? 0 : cfg.Groups.Max(g => g.Order);
        cfg.Groups.Add(new TileGroup { Id = Guid.NewGuid().ToString(), Name = name.Trim(), Order = maxOrder + 1 });
        SaveCurrent();
    }

    private void RenameFirstGroup()
    {
        if (_groups.Count == 0) return;
        var g = _groups[0].Group;
        var name = Interaction.Input($"重命名分组「{g.Name}」：", "重命名分组", g.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        g.Name = name.Trim();
        SaveCurrent();
    }

    private void ChangeWallColor()
    {
        var dlg = new ColorPickerDialog();
        if (dlg.ShowDialog() == true && dlg.SelectedColor != null)
        {
            _store.Current.Settings.BackgroundColor = dlg.SelectedColor;
            SaveCurrent();
        }
    }

    private void SemanticZoom_Click(object sender, RoutedEventArgs e) => ToggleZoom();

    // ==================== 分组总览（语义缩放） ====================

    private void ToggleZoom()
    {
        if (_zoomOpen) ExitZoom();
        else EnterZoom();
    }

    /// <summary>管道 ZOOM 命令的无焦点入口。</summary>
    public void CommandToggleZoom() => ToggleZoom();

    /// <summary>管道 SCROLL 命令：横向滚到指定 offset 并打日志（验证翻页）。</summary>
    public void CommandScroll(double x)
    {
        WallScroll.ScrollToHorizontalOffset(x);
        Dbg($"PIPE-SCROLL to={x:0} actualOffset={WallScroll.HorizontalOffset:0}");
    }

    private void EnterZoom()
    {
        if (AllAppsPage.Visibility == Visibility.Visible) return;
        _zoomOpen = true;
        CloseAppBar();
        RebuildZoomPanel();
        ZoomOverlay.Background = RootGrid.Background;
        ZoomOverlay.Visibility = Visibility.Visible;
        Dbg("ZOOM enter");
    }

    private void ExitZoom()
    {
        if (!_zoomOpen) return;
        _zoomOpen = false;
        ZoomOverlay.Visibility = Visibility.Collapsed;
        // 退出后磁贴墙按新分组顺序重建生效
        RebuildLayout();
        Dbg("ZOOM exit");
    }

    private void RebuildZoomPanel()
    {
        ZoomCanvas.Children.Clear();
        _zoomGroupCards.Clear();

        var cfg = _store.Current;
        var groups = cfg.Groups.OrderBy(g => g.Order).ToList();

        const double cardW = 200, cardH = 150, gap = 16;
        int total = groups.Count + 1; // 末尾「＋ 新建分组」
        double totalW = total * cardW + (total - 1) * gap;
        // 组块左边界与磁贴墙内容左边界对齐（同一左内边距常量 GroupX）；纵向仍居中
        double startX = GroupX;
        double y = Math.Max(60, (ActualHeight - cardH) / 2);

        double x = startX;
        foreach (var g in groups)
        {
            int count = cfg.Tiles.Count(t => t.GroupId == g.Id && t.Pinned);
            var card = BuildGroupCard(g, count);
            Canvas.SetLeft(card, x);
            Canvas.SetTop(card, y);
            ZoomCanvas.Children.Add(card);
            _zoomGroupCards.Add((card, g, x));
            x += cardW + gap;
        }

        var addCard = BuildAddCard();
        Canvas.SetLeft(addCard, x);
        Canvas.SetTop(addCard, y);
        ZoomCanvas.Children.Add(addCard);
    }

    private Border BuildGroupCard(TileGroup g, int count)
    {
        var card = new Border
        {
            Width = 200,
            Height = 150,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x26, 0x32)),
            Tag = g
        };
        var sp = new StackPanel { Margin = new Thickness(18, 18, 18, 12) };
        sp.Children.Add(new TextBlock
        {
            Text = g.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xEC, 0xF2)),
            FontSize = 20,
            FontWeight = FontWeights.Light,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"{count} 个磁贴",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x98, 0xA8)),
            FontSize = 13,
            Margin = new Thickness(0, 10, 0, 0)
        });
        card.Child = sp;
        card.MouseLeftButtonDown += ZoomCard_Down;
        card.MouseMove += ZoomCard_Move;
        card.MouseLeftButtonUp += ZoomCard_Up;
        return card;
    }

    private Border BuildAddCard()
    {
        var card = new Border
        {
            Width = 200,
            Height = 150,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x26))
        };
        var sp = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        sp.Children.Add(new TextBlock
        {
            Text = "＋",
            FontSize = 36,
            FontWeight = FontWeights.Light,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xDC)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        sp.Children.Add(new TextBlock
        {
            Text = "新建分组",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x98, 0xA8)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        });
        card.Child = sp;
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_zoomDragging) return;
            AddNewGroup();
            RebuildZoomPanel();
        };
        return card;
    }

    private void ZoomCard_Down(object sender, MouseButtonEventArgs e)
    {
        var card = (Border)sender;
        var g = card.Tag as TileGroup;
        if (g == null) return;
        _zoomDragGroup = g;
        _zoomDragCard = card;
        _zoomDownPos = e.GetPosition(this);
        _zoomCardOriginX = Canvas.GetLeft(card);
        _zoomCardOriginY = Canvas.GetTop(card);
        _zoomDragging = false;
        try { card.CaptureMouse(); } catch { }
        e.Handled = true;
    }

    private void ZoomCard_Move(object sender, MouseEventArgs e)
    {
        if (_zoomDragCard == null || _zoomDragGroup == null) return;
        var p = e.GetPosition(this);
        double dx = p.X - _zoomDownPos.X;
        double dy = p.Y - _zoomDownPos.Y;
        if (!_zoomDragging)
        {
            if (Math.Abs(dx) + Math.Abs(dy) > DragThreshold)
            {
                _zoomDragging = true;
                // 拖起的块提升到最上层，盖在其它块之上
                ZoomCanvas.Children.Remove(_zoomDragCard);
                ZoomCanvas.Children.Add(_zoomDragCard);
            }
            else return;
        }
        Canvas.SetLeft(_zoomDragCard, _zoomCardOriginX + dx);
        Canvas.SetTop(_zoomDragCard, _zoomCardOriginY + dy);
    }

    private void ZoomCard_Up(object sender, MouseButtonEventArgs e)
    {
        if (_zoomDragCard == null || _zoomDragGroup == null) return;
        var card = _zoomDragCard;
        var g = _zoomDragGroup;
        var p = e.GetPosition(this);
        double dx = p.X - _zoomDownPos.X;
        bool wasDragging = _zoomDragging;
        try { card.ReleaseMouseCapture(); } catch { }
        _zoomDragCard = null;
        _zoomDragGroup = null;
        _zoomDragging = false;

        if (wasDragging)
        {
            // 按拖动后块中心 X 决定插入位置
            double draggedCenter = _zoomCardOriginX + dx + 100; // 块半宽
            int curIdx = -1;
            for (int i = 0; i < _zoomGroupCards.Count; i++)
                if (ReferenceEquals(_zoomGroupCards[i].group, g)) { curIdx = i; break; }
            int targetIdx = 0;
            foreach (var c in _zoomGroupCards)
            {
                if (ReferenceEquals(c.group, g)) continue;
                if (c.baseX + 100 < draggedCenter) targetIdx++;
            }
            Dbg($"ZOOM reorder '{g.Name}' {curIdx}->{targetIdx}");
            if (targetIdx != curIdx && curIdx >= 0)
            {
                var cfg = _store.Current;
                cfg.Groups.Remove(g);
                cfg.Groups.Insert(targetIdx, g);
                SaveCurrent(); // Normalize 按 list 序号回写 Order
            }
            RebuildZoomPanel();
        }
        else
        {
            // 未拖动 = 点击改名
            var name = Interaction.Input($"重命名分组「{g.Name}」：", "重命名分组", g.Name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                g.Name = name.Trim();
                SaveCurrent();
                RebuildZoomPanel();
            }
        }
    }

    private void OpenAllAppsBar(AppEntry entry)
    {
        _barContext = BarContext.AllApps;
        _barEntry = entry;
        _barTile = null;
        bool pinned = IsPinned(entry);
        AppBarTitle.Text = entry.DisplayName;
        AppBarButtons.Children.Clear();

        AppBarButtons.Children.Add(MakeBarButton("▶", "启动", () => LaunchEntry(entry)));
        AppBarButtons.Children.Add(MakeBarButton(pinned ? "📌" : "📍", pinned ? "取消固定" : "固定到开始菜单",
            () => { if (pinned) UnpinEntry(entry); else PinEntry(entry); }));

        ShowAppBar();
    }

    private Button MakeBarButton(string glyph, string text, Action onClick)
    {
        var panel = new StackPanel { Width = 104 };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            Foreground = Brushes.White,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xDC)),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        });
        var b = new Button
        {
            Content = panel,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 8, 8, 8),
            Margin = new Thickness(4, 0, 4, 0)
        };
        b.Click += (_, _) => { CloseAppBar(); onClick(); };
        return b;
    }

    private void ShowAppBar()
    {
        AppBar.Visibility = Visibility.Visible;
        AppBarTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(92, 0, TimeSpan.FromMilliseconds(200)));
    }

    private void CloseAppBar()
    {
        if (_barContext == BarContext.None || AppBar.Visibility != Visibility.Visible) return;
        var anim = new DoubleAnimation(0, 92, TimeSpan.FromMilliseconds(150));
        anim.Completed += (_, _) => AppBar.Visibility = Visibility.Collapsed;
        AppBarTransform.BeginAnimation(TranslateTransform.YProperty, anim);
        _barContext = BarContext.None;
    }

    private void AppBarBackground_Click(object sender, MouseButtonEventArgs e) => CloseAppBar();

    // ===== 背景横向跟手拖动（抓纸式翻页） =====

    /// <summary>沿可视化树判断命中源是否位于某个 TileControl 内；是则磁贴拖拽链路负责，不进入背景拖动。</summary>
    private static bool IsOverTileHit(object? originalSource)
    {
        var el = originalSource as DependencyObject;
        while (el != null)
        {
            if (el is TileControl) return true;
            el = el is Visual || el is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(el)
                : LogicalTreeHelper.GetParent(el);
        }
        return false;
    }

    private void WallBg_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            OpenWallCustomizeBar();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (AllAppsPage.Visibility == Visibility.Visible) return;
        if (ZoomOverlay.Visibility == Visibility.Visible) return;
        // 磁贴上的按下由 TileControl 自己的拖拽链路处理，背景不介入
        if (IsOverTileHit(e.OriginalSource)) return;

        CloseAppBar();
        _wallDragArmed = true;
        _wallDragging = false;
        _wallDragDownPos = e.GetPosition(this);
        _wallDragStartOffset = WallScroll.HorizontalOffset;
        try { TileCanvas.CaptureMouse(); } catch { }
    }

    private void WallBg_Move(object sender, MouseEventArgs e)
    {
        if (!_wallDragArmed) return;
        var p = e.GetPosition(this);
        double dx = p.X - _wallDragDownPos.X;
        double dy = p.Y - _wallDragDownPos.Y;
        if (!_wallDragging)
        {
            if (Math.Abs(dx) + Math.Abs(dy) > DragThreshold)
            {
                _wallDragging = true;
                Dbg($"WALL-DRAG start offset={_wallDragStartOffset:0} dx={dx:0}");
            }
            else return;
        }
        // 抓纸式：按下 Offset - dx。向左拖(dx<0) => offset 增大 => 向右翻页，与滚轮向下一致。
        WallScroll.ScrollToHorizontalOffset(_wallDragStartOffset - dx);
    }

    private void WallBg_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_wallDragArmed) return;
        bool wasDragging = _wallDragging;
        try { TileCanvas.ReleaseMouseCapture(); } catch { }
        _wallDragArmed = false;
        _wallDragging = false;
        // 位移未超阈值 = 点空白 => 关菜单；拖动过 => 不关
        if (!wasDragging)
        {
            Dbg("WALL-BLANK click -> HideMenu");
            HideMenu();
        }
    }

    // 滚轮向下 = 向右翻页（Win8.1 横向滚动）；Ctrl+滚轮 = 进/退分组总览
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (AllAppsPage.Visibility == Visibility.Visible) return;
            ToggleZoom();
            e.Handled = true;
            return;
        }
        if (AllAppsPage.Visibility == Visibility.Visible) return;
        double delta = e.Delta;
        WallScroll.ScrollToHorizontalOffset(WallScroll.HorizontalOffset - delta * 3);
        e.Handled = true;
    }

    // ==================== 磁贴操作 ====================

    private void UnpinTile(TileItem tile)
    {
        tile.Pinned = false;
        SaveCurrent();
    }

    private void DeleteTile(TileItem tile)
    {
        var r = MessageBox.Show(this, $"确定删除磁贴「{tile.Name}」？此操作不可恢复。", "删除磁贴",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            _store.Current.Tiles.Remove(tile);
            SaveCurrent();
        }
    }

    private void CycleSize(TileItem tile)
    {
        tile.Size = tile.Size switch
        {
            TileSize.Small => TileSize.Medium,
            TileSize.Medium => TileSize.Wide,
            TileSize.Wide => TileSize.Large,
            _ => TileSize.Small
        };
        SaveCurrent();
    }

    private void ChangeColor(TileItem tile)
    {
        var picked = ColorPickerDialog.Show(this, tile.Color);
        if (picked != null) { tile.Color = picked; SaveCurrent(); }
    }

    private void RenameTile(TileItem tile)
    {
        var name = InputDialog.Show(this, "重命名磁贴", "磁贴名称：", tile.Name);
        if (!string.IsNullOrWhiteSpace(name)) { tile.Name = name; SaveCurrent(); }
    }

    private void SaveCurrent() => _store.Save(_store.Current);

    // ==================== 全部应用页 ====================

    private void SwitchToWall_Click(object sender, RoutedEventArgs e)
    {
        AllAppsPage.Visibility = Visibility.Collapsed;
        WallScroll.Visibility = Visibility.Visible;
    }

    private async void SwitchToAllApps_Click(object sender, RoutedEventArgs e)
    {
        WallScroll.Visibility = Visibility.Collapsed;
        AllAppsPage.Visibility = Visibility.Visible;
        CloseAppBar();
        await EnsureAllAppsLoaded();
    }

    private async Task EnsureAllAppsLoaded()
    {
        if (_allApps != null || _catalog == null || _allAppsLoading) return;
        _allAppsLoading = true;
        await Task.Run(() =>
        {
            try { _allApps = _catalog.Enumerate(); }
            catch { _allApps = Array.Empty<AppEntry>(); }
        });
        _allAppsLoading = false;
        RefreshAllAppsTags();
    }

    private void RefreshAllAppsTags()
    {
        if (_allApps == null) return;
        var pinned = PinnedKeys();
        AllAppsPage.Load(_allApps, pinned);
    }

    private HashSet<string> PinnedKeys() =>
        _store.Current.Tiles.Where(t => t.Pinned)
            .Select(t => AppIdentity.Key(t))
            .ToHashSet();

    private bool IsPinned(AppEntry entry) => PinnedKeys().Contains(AppIdentity.Key(entry));

    private void AllAppsRowClicked(AppEntry entry) => LaunchEntry(entry);

    private void AllAppsRowRightClicked(AppEntry entry) => OpenAllAppsBar(entry);

    private void LaunchEntry(AppEntry entry)
    {
        var tile = TileFromEntry(entry);
        bool ok = _launcher.Execute(tile, out var error);
        if (ok) HideMenu();
        else ShowError($"「{entry.DisplayName}」启动失败：{error}");
    }

    private static TileItem TileFromEntry(AppEntry entry) => new()
    {
        Name = entry.DisplayName,
        Kind = entry.Kind == AppEntryKind.Uwp ? TileActionKind.Shell : TileActionKind.Launch,
        Target = entry.Kind == AppEntryKind.Uwp ? "shell:AppsFolder\\" + entry.LaunchTarget : entry.LaunchTarget,
        Arguments = entry.Arguments,
        WorkingDirectory = entry.WorkingDirectory
    };

    private void PinEntry(AppEntry entry)
    {
        var cfg = _store.Current;
        string key = AppIdentity.Key(entry);
        var existing = cfg.Tiles.FirstOrDefault(t => AppIdentity.Key(t) == key);
        var group = cfg.Groups.OrderBy(g => g.Order).First();

        if (existing != null)
        {
            existing.Pinned = true;
            existing.GroupId = group.Id;
            existing.Order = cfg.Tiles.Where(t => t.GroupId == group.Id).Max(t => (int?)t.Order) ?? 0 + 1;
        }
        else
        {
            var tile = TileFromEntry(entry);
            tile.Pinned = true;
            tile.Color = TilePalette.ColorForName(entry.DisplayName);
            tile.Size = TileSize.Medium;
            tile.GroupId = group.Id;
            tile.Order = cfg.Tiles.Where(t => t.GroupId == group.Id).Max(t => (int?)t.Order) ?? 0 + 1;
            cfg.Tiles.Add(tile);
        }
        SaveCurrent();
    }

    private void UnpinEntry(AppEntry entry)
    {
        var key = AppIdentity.Key(entry);
        var tile = _store.Current.Tiles.FirstOrDefault(t => AppIdentity.Key(t) == key && t.Pinned);
        if (tile != null) { tile.Pinned = false; SaveCurrent(); }
    }

    // ==================== 顶栏按钮 ====================

    private void AddTile_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _store.Current;
        var dlg = new AddTileDialog(cfg.Groups, cfg.Groups.OrderBy(g => g.Order).First().Id) { Owner = this };
        _suppressFocusClose = true;
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            dlg.Result.Pinned = true;
            cfg.Tiles.Add(dlg.Result);
            SaveCurrent();
        }
        _suppressFocusClose = false;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_store, _catalog) { Owner = this };
        _suppressFocusClose = true;
        dlg.ShowDialog();
        _suppressFocusClose = false;
        ApplySettings();
        RebuildLayout();
    }

    // ==================== 关闭行为 ====================

    private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        // F10：调试键——把前 6 个磁贴的物理像素坐标写进日志
        if (e.Key == Key.F10)
        {
            Dbg("=== F10 PointToScreen debug ===");
            int n = 0;
            foreach (var gl in _groups)
                foreach (var pl in gl.Tiles)
                {
                    if (n >= 6) break;
                    if (_pool.TryGetValue(pl.Tile.Id, out var c))
                    {
                        var center = new Point(c.ActualWidth / 2, c.ActualHeight / 2);
                        var screen = c.PointToScreen(center);
                        Dbg($"TILE[{n}] name='{pl.Tile.Name}' screenX={screen.X} screenY={screen.Y}");
                    }
                    n++;
                }
            e.Handled = true;
            return;
        }

        // F11：调试键——自渲染快照存到 %LOCALAPPDATA%\TileStartMenu\wall-render.png
        if (e.Key == Key.F11)
        {
            var p = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TileStartMenu", "wall-render.png");
            SaveSelfRender(p);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape || !IsMenuOpen) return;
        if (_suppressFocusClose) return;

        // Esc 最优先退分组总览层
        if (_zoomOpen) { ExitZoom(); e.Handled = true; return; }

        if (PowerPopup.Visibility == Visibility.Visible)
        { PowerPopup.Visibility = Visibility.Collapsed; e.Handled = true; return; }
        if (_barContext != BarContext.None) { CloseAppBar(); e.Handled = true; return; }
        if (AllAppsPage.Visibility == Visibility.Visible)
        {
            AllAppsPage.Visibility = Visibility.Collapsed;
            WallScroll.Visibility = Visibility.Visible;
            e.Handled = true;
            return;
        }
        HideMenu();
        await Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    private static T? TryGetProperty<T>(object? obj, string name) where T : class
    {
        if (obj == null) return null;
        try { return obj.GetType().GetProperty(name)?.GetValue(obj) as T; } catch { return null; }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!IsMenuOpen || _suppressFocusClose) return;
        if (Environment.GetEnvironmentVariable("TUI_NO_FOCUS_CLOSE") == "1") return;

        GetCursorPos(out POINT pt);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        ScreenToClient(hwnd, ref pt);

        const int margin = 6;
        if (pt.X < -margin || pt.Y < -margin ||
            pt.X > ActualWidth + margin || pt.Y > ActualHeight + margin)
        {
            HideMenu();
        }
    }
}
