using System.Windows.Media;

namespace TileStartMenu.App.UI;

/// <summary>Metro 预设调色板与颜色解析工具（全部界面内联，不依赖外部组件）。</summary>
public static class MetroColors
{
    /// <summary>Metro 风格预设色板（≥12 色）。</summary>
    public static readonly IReadOnlyList<string> Presets = new[]
    {
        "#0078D4", "#0099BC", "#107C10", "#8764B8", "#CA5010", "#E74856",
        "#E81123", "#B4009E", "#486860", "#498205", "#5B5FC7", "#767171",
        "#69797E", "#2D7D9A", "#7A5EA8", "#0B7A5A", "#B7472A", "#0B3A6E",
        "#24292E", "#3A3A3A", "#1B1B1B", "#FFB900", "#D2691E", "#0063B1"
    };

    /// <summary>解析 "#RRGGBB"（或带 alpha）为 WPF Color；失败回退到默认蓝。</summary>
    public static Color Parse(string? hex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.FromRgb(0x00, 0x78, 0xD4);
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Color.FromRgb(0x00, 0x78, 0xD4);
        }
    }

    public static SolidColorBrush ToBrush(string? hex) => new(Parse(hex));
}
