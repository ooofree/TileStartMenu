using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TileStartMenu.Core.Models;
using TileStartMenu.Core.Power;

namespace TileStartMenu.App.UI;

/// <summary>单个 Metro 磁贴的视觉控件：色块 + 图标（或首字块）+ 左下角标签。</summary>
public partial class TileControl : UserControl
{
    public TileControl()
    {
        InitializeComponent();
    }

    /// <summary>该控件绑定的磁贴数据。</summary>
    public TileItem Tile { get; private set; } = null!;

    /// <summary>按磁贴数据重绘。先只显示纯色块，图标由 IIconService 异步回填；
    /// 仅当图标明确失败时才退化为名称首字块。</summary>
    public void Bind(TileItem tile, string? iconPath)
    {
        Tile = tile;
        Width = TileMetrics.Width(tile.Size);
        Height = TileMetrics.Height(tile.Size);
        Root.Background = MetroColors.ToBrush(tile.Color);

        Label.Text = tile.Name;
        Label.Visibility = tile.ShowLabel ? Visibility.Visible : Visibility.Collapsed;

        var ch = string.IsNullOrWhiteSpace(tile.Name) ? "?" : tile.Name.Trim()[..1].ToUpper();
        FallbackChar.Text = ch;

        // 初始：纯色块占位，不显示首字（图标异步回来前不抢戏）
        IconImage.Visibility = Visibility.Collapsed;
        FallbackChar.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(iconPath, UriKind.Absolute);
                bmp.EndInit();
                ApplyIcon(bmp);
            }
            catch
            {
                ShowFallback();
            }
        }

        // 不同尺寸给图标不同的最大边长（Win8.1：大图标占主导）
        double iconSize = tile.Size switch
        {
            TileSize.Small => 34,
            TileSize.Medium => 76,
            TileSize.Wide => 105,
            TileSize.Large => 170,
            _ => 76
        };
        // IconScale：Core 侧持久化的图标倍数（0.5/0.6/0.75/1.0）
        double scale = tile.IconScale;
        if (scale <= 0) scale = 1.0;
        iconSize *= scale;
        IconImage.MaxWidth = iconSize;
        IconImage.MaxHeight = iconSize;
    }

    private void ShowFallback()
    {
        IconImage.Visibility = Visibility.Collapsed;
        FallbackChar.Visibility = Visibility.Visible;
    }

    /// <summary>图标明确失败 → 首字块；电源磁贴 → 电源符号。</summary>
    public void ShowFallbackGlyph(PowerAction? power)
    {
        IconImage.Visibility = Visibility.Collapsed;
        FallbackChar.Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        FallbackChar.FontSize = Tile.Size switch
        {
            TileSize.Small => 22,
            TileSize.Medium => 40,
            _ => 36
        };
        FallbackChar.Text = power switch
        {
            PowerAction.Shutdown => "⏻",
            PowerAction.Restart => "⟳",
            PowerAction.Sleep => "☾",
            PowerAction.Lock => "🔒",
            PowerAction.SignOut => "⎋",
            _ => FallbackChar.Text
        };
        FallbackChar.Visibility = Visibility.Visible;
    }

    /// <summary>图标异步提取完成后回填（首批先显示色块+首字）。</summary>
    public void ApplyIcon(BitmapImage bmp)
    {
        IconImage.Source = bmp;
        IconImage.Visibility = Visibility.Visible;
        FallbackChar.Visibility = Visibility.Collapsed;
    }
}
