using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TileStartMenu.Core.Catalog;

namespace TileStartMenu.App.UI;

/// <summary>
/// 「全部应用」列表页：按首字母分组列出本机可启动应用（数据来自 IAppCatalog）。
/// 行点击 = 直接启动；行右键 = 由 MainWindow 唤起底部 App Bar（固定/取消固定）。
/// </summary>
public partial class AllAppsView : UserControl
{
    private readonly List<AppEntry> _entries = new();
    private IReadOnlySet<string> _pinnedKeys = new HashSet<string>();

    /// <summary>单击某行（要求启动该应用）。</summary>
    public event Action<AppEntry>? RowClicked;

    /// <summary>右键某行（要求弹出底部操作栏）。</summary>
    public event Action<AppEntry>? RowRightClicked;

    public AllAppsView()
    {
        InitializeComponent();
        List.MouseLeftButtonUp += List_MouseLeftButtonUp;
        List.MouseRightButtonUp += List_MouseRightButtonUp;
    }

    /// <summary>当前列表中是否为空。</summary>
    public bool IsEmpty => _entries.Count == 0;

    /// <summary>
    /// 装载应用列表并标记哪些已固定。
    /// </summary>
    public void Load(IReadOnlyList<AppEntry> entries, IReadOnlySet<string> pinnedKeys)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _pinnedKeys = pinnedKeys;
        BuildItems();
    }

    private void BuildItems()
    {
        List.Items.Clear();
        if (_entries.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "未枚举到应用，或应用枚举器尚未就绪。";
            return;
        }
        EmptyHint.Visibility = Visibility.Collapsed;

        foreach (var grp in _entries.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                                     .GroupBy(LetterOf))
        {
            List.Items.Add(new TextBlock
            {
                Text = grp.Key,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xBC, 0xC8)),
                FontSize = 15,
                Margin = new Thickness(8, 14, 0, 6),
                IsHitTestVisible = false
            });

            foreach (var entry in grp)
            {
                List.Items.Add(MakeRow(entry));
            }
        }
    }

    private static string LetterOf(AppEntry e)
    {
        var name = e.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) return "#";
        var ch = char.ToUpperInvariant(name.Trim()[0]);
        return char.IsLetter(ch) ? ch.ToString() : "#";
    }

    private FrameworkElement MakeRow(AppEntry entry)
    {
        bool pinned = _pinnedKeys.Contains(AppIdentity.Key(entry));

        var icon = new Border
        {
            Width = 36,
            Height = 36,
            Background = MetroColors.ToBrush(TilePalette.ColorForName(entry.DisplayName)),
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(entry.DisplayName) ? "?" : entry.DisplayName.Trim()[..1].ToUpper(),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var name = new TextBlock
        {
            Text = entry.DisplayName,
            Foreground = Brushes.White,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var pinTag = new TextBlock
        {
            Text = pinned ? "已固定" : "",
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0xC8, 0x8A)),
            FontSize = 12,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(pinTag, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(pinTag);
        panel.Children.Add(icon);
        panel.Children.Add(name);

        var row = new Border { Tag = entry, Child = panel };
        return row;
    }

    private AppEntry? EntryFromSource(object? source)
    {
        // 我们的行不是数据绑定，直接沿可视化树找 Tag
        var el = source as DependencyObject;
        while (el != null)
        {
            if (el is FrameworkElement fe && fe.Tag is AppEntry e) return e;
            el = VisualTreeHelper.GetParent(el);
        }
        return null;
    }

    private void List_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var entry = EntryFromSource(e.OriginalSource);
        if (entry != null) RowClicked?.Invoke(entry);
    }

    private void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var entry = EntryFromSource(e.OriginalSource);
        if (entry != null) RowRightClicked?.Invoke(entry);
    }
}
