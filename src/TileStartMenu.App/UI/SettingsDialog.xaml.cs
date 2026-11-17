using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TileStartMenu.Core.Catalog;
using TileStartMenu.Core.Models;
using TileStartMenu.Core.Persistence;

namespace TileStartMenu.App.UI;

/// <summary>设置面板：应用开关 + 外观 + 分组管理 + 重置为全部应用。所有改动立即写回 IConfigStore。</summary>
public partial class SettingsDialog : Window
{
    private readonly IConfigStore _store;
    private readonly IAppCatalog? _catalog;
    private string _bgColor;

    public SettingsDialog(IConfigStore store, IAppCatalog? catalog = null)
    {
        InitializeComponent();
        _store = store;
        _catalog = catalog;
        var s = _store.Current.Settings;

        WinKeyCheck.IsChecked = s.WinKeyEnabled;
        StartupCheck.IsChecked = s.LaunchAtStartup;
        ClockCheck.IsChecked = s.ShowClock;
        _bgColor = s.BackgroundColor;
        OpacitySlider.Value = s.BackgroundOpacity;

        WinKeyCheck.Checked += (_, _) => SaveSettings();
        WinKeyCheck.Unchecked += (_, _) => SaveSettings();
        StartupCheck.Checked += (_, _) => SaveSettings();
        StartupCheck.Unchecked += (_, _) => SaveSettings();
        ClockCheck.Checked += (_, _) => SaveSettings();
        ClockCheck.Unchecked += (_, _) => SaveSettings();
        OpacitySlider.ValueChanged += (_, _) => SaveSettings();
        BgColorButton.Click += (_, _) =>
        {
            var picked = ColorPickerDialog.Show(this, _bgColor);
            if (picked != null) { _bgColor = picked; ApplyColor(); SaveSettings(); }
        };

        NewGroupBtn.Click += OnNewGroup;
        RenameGroupBtn.Click += OnRenameGroup;
        DeleteGroupBtn.Click += OnDeleteGroup;
        ResetCatalogBtn.Click += OnResetCatalog;
        ResetCatalogBtn.IsEnabled = _catalog != null;
        CatalogStatusText.Text = _catalog != null ? "" : "应用枚举器尚未就绪（核心组件接入后可用）。";

        ApplyColor();
        RefreshGroupList();
    }

    private void ApplyColor()
    {
        BgColorButton.Background = MetroColors.ToBrush(_bgColor);
        BgColorButton.Content = _bgColor;
        BgColorButton.Foreground = System.Windows.Media.Brushes.White;
    }

    private void SaveSettings()
    {
        var cfg = _store.Current;
        cfg.Settings.WinKeyEnabled = WinKeyCheck.IsChecked == true;
        cfg.Settings.LaunchAtStartup = StartupCheck.IsChecked == true;
        cfg.Settings.ShowClock = ClockCheck.IsChecked == true;
        cfg.Settings.BackgroundColor = _bgColor;
        cfg.Settings.BackgroundOpacity = OpacitySlider.Value;
        _store.Save(cfg);
    }

    private void RefreshGroupList()
    {
        GroupList.Items.Clear();
        var cfg = _store.Current;
        foreach (var g in cfg.Groups.OrderBy(x => x.Order))
        {
            int count = cfg.Tiles.Count(t => t.GroupId == g.Id);
            GroupList.Items.Add(new ListBoxItem
            {
                Content = $"{g.Name}（{count} 个磁贴）",
                Tag = g.Id
            });
        }
        if (GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
    }

    private TileGroup? SelectedGroup()
    {
        if (GroupList.SelectedItem is not ListBoxItem item) return null;
        var id = (string)item.Tag;
        return _store.Current.Groups.FirstOrDefault(g => g.Id == id);
    }

    private void OnNewGroup(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "新建分组", "分组名称：");
        if (string.IsNullOrWhiteSpace(name)) return;
        var cfg = _store.Current;
        cfg.Groups.Add(new TileGroup { Name = name, Order = cfg.Groups.Count });
        _store.Save(cfg);
        RefreshGroupList();
    }

    private void OnRenameGroup(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup();
        if (g == null) return;
        var name = InputDialog.Show(this, "重命名分组", "新的分组名称：", g.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var cfg = _store.Current;
        cfg.Groups.First(x => x.Id == g.Id).Name = name;
        _store.Save(cfg);
        RefreshGroupList();
    }

    private void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        var g = SelectedGroup();
        if (g == null) return;
        var cfg = _store.Current;
        if (cfg.Groups.Count <= 1)
        {
            MessageBox.Show(this, "至少保留一个分组。", "删除分组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int count = cfg.Tiles.Count(t => t.GroupId == g.Id);
        var choice = MessageBox.Show(
            this,
            count > 0
                ? $"分组「{g.Name}」内有 {count} 个磁贴。\n\n是 = 删除分组并一并删除这些磁贴\n否 = 磁贴移到第一个分组\n取消 = 不删除"
                : $"确定删除分组「{g.Name}」？",
            "删除分组",
            count > 0 ? MessageBoxButton.YesNoCancel : MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return;

        var keep = cfg.Groups.First(x => x.Id != g.Id);
        if (choice == MessageBoxResult.No || choice == MessageBoxResult.OK)
        {
            // 磁贴移到保留分组
            foreach (var t in cfg.Tiles.Where(t => t.GroupId == g.Id)) t.GroupId = keep.Id;
        }
        else
        {
            cfg.Tiles.RemoveAll(t => t.GroupId == g.Id);
        }
        cfg.Groups.Remove(g);
        _store.Save(cfg);
        RefreshGroupList();
    }

    /// <summary>重置为全部应用：二次确认后调用 IAppCatalog 重新枚举并覆盖磁贴布局。</summary>
    private async void OnResetCatalog(object sender, RoutedEventArgs e)
    {
        if (_catalog == null) return;
        var r = MessageBox.Show(this,
            "将重新枚举本机全部可启动应用，并覆盖当前磁贴布局（自定义磁贴会被清除）。确定继续？",
            "重置为全部应用", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        ResetCatalogBtn.IsEnabled = false;
        CatalogStatusText.Text = "正在枚举本机应用…";

        IReadOnlyList<AppEntry> entries = Array.Empty<AppEntry>();
        await System.Threading.Tasks.Task.Run(() =>
        {
            try { entries = _catalog.Enumerate(); } catch { }
        });

        var cfg = _store.Current;
        var tiles = _catalog.CreateTiles(entries);
        cfg.Tiles.Clear();
        foreach (var t in tiles) cfg.Tiles.Add(t);
        cfg.Settings.AllAppsSeeded = true;
        _store.Save(cfg);

        CatalogStatusText.Text = $"已生成 {tiles.Count} 个磁贴。{_catalog.LastDiagnostics}";
        ResetCatalogBtn.IsEnabled = true;
        RefreshGroupList();
    }
}
