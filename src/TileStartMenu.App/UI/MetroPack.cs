using System.Windows;

namespace TileStartMenu.App.UI;

/// <summary>
/// Metro 网格打包算法：把不同尺寸的磁贴按 (Cell=96, Gap=8) 单元格网格从左到右、
/// 从上到下摆放，宽/大磁贴严格占位，行列对齐、不产生错位空洞。
/// 布局面板与拖拽预览共用同一套算法，保证“拖到哪就排到哪”。
/// </summary>
public static class MetroPack
{
    /// <summary>单元格步距 = Cell + Gap。</summary>
    public const double Pitch = TileMetrics.Cell + TileMetrics.Gap;

    /// <summary>
    /// 按给定尺寸序列（顺序即磁贴顺序）布局，返回每个磁贴在面板坐标系下的矩形。
    /// </summary>
    public static List<Rect> Layout(IReadOnlyList<Size> sizes, int maxColumns)
    {
        var result = new List<Rect>(sizes.Count);
        var occupied = new HashSet<(int C, int R)>();
        int limit = maxColumns <= 0 ? 4096 : maxColumns;

        foreach (var s in sizes)
        {
            int cols = (int)Math.Round((s.Width + TileMetrics.Gap) / Pitch);
            int rows = (int)Math.Round((s.Height + TileMetrics.Gap) / Pitch);
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;

            bool placed = false;
            for (int r = 0; r < 4096 && !placed; r++)
            {
                for (int c = 0; c < limit && !placed; c++)
                {
                    if (Fits(occupied, c, r, cols, rows))
                    {
                        Mark(occupied, c, r, cols, rows);
                        result.Add(new Rect(c * Pitch, r * Pitch, s.Width, s.Height));
                        placed = true;
                    }
                }
            }
            if (!placed)
            {
                // 兜底：理论上不会走到
                result.Add(new Rect(0, result.Count * Pitch, s.Width, s.Height));
            }
        }
        return result;
    }

    /// <summary>根据布局矩形换算内容整体尺寸（右侧/底部留一个 Gap）。</summary>
    public static Size ContentSize(IReadOnlyList<Rect> rects)
    {
        double w = 0, h = 0;
        foreach (var r in rects)
        {
            if (r.Right > w) w = r.Right;
            if (r.Bottom > h) h = r.Bottom;
        }
        return new Size(w + TileMetrics.Gap, h + TileMetrics.Gap);
    }

    private static bool Fits(HashSet<(int, int)> occ, int c, int r, int cols, int rows)
    {
        for (int dr = 0; dr < rows; dr++)
            for (int dc = 0; dc < cols; dc++)
                if (occ.Contains((c + dc, r + dr))) return false;
        return true;
    }

    private static void Mark(HashSet<(int, int)> occ, int c, int r, int cols, int rows)
    {
        for (int dr = 0; dr < rows; dr++)
            for (int dc = 0; dc < cols; dc++)
                occ.Add((c + dc, r + dr));
    }
}
