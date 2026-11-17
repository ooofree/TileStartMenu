using TileStartMenu.Core.Models;

namespace TileStartMenu.App.UI;

/// <summary>Metro 网格度量。所有界面尺寸都由这里统一换算，保证磁贴对齐与命中区域一致。</summary>
public static class TileMetrics
{
    /// <summary>最小网格单元（像素）。Win8.1 风格：56px。Medium = 2 格、Wide/Large 宽 = 4 格。</summary>
    public const double Cell = 56;

    /// <summary>磁贴之间的间隙。</summary>
    public const double Gap = 6;

    public static double Width(TileSize size) => size switch
    {
        TileSize.Small => Cell,
        TileSize.Medium => Cell * 2 + Gap,
        TileSize.Wide => Cell * 4 + Gap * 3,
        TileSize.Large => Cell * 4 + Gap * 3,
        _ => Cell * 2 + Gap
    };

    public static double Height(TileSize size) => size switch
    {
        TileSize.Small => Cell,
        TileSize.Medium => Cell * 2 + Gap,
        TileSize.Wide => Cell * 2 + Gap,
        TileSize.Large => Cell * 4 + Gap * 3,
        _ => Cell * 2 + Gap
    };

    /// <summary>网格占位（列数, 行数）。</summary>
    public static (int Cols, int Rows) Spans(TileSize size) => size switch
    {
        TileSize.Small => (1, 1),
        TileSize.Medium => (2, 2),
        TileSize.Wide => (4, 2),
        TileSize.Large => (4, 4),
        _ => (2, 2)
    };
}
