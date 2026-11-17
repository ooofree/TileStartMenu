using System.Windows;

namespace TileStartMenu.App.UI;

/// <summary>
/// Metro 网格打包算法：把不同尺寸的磁贴按 (Cell=56, Gap=6) 单元格网格摆放。
/// 语义为 Win8.1「列优先」：一列自上而下填满 rowsPerColumn 行后才向右开下一列；
/// 横向不设上限（交给外层 ScrollViewer 翻页）。宽/大磁贴严格整块占位、不跨列断开。
/// 布局面板与拖拽预览共用同一套算法，保证“拖到哪就排到哪”。
/// </summary>
public static class MetroPack
{
    /// <summary>单元格步距 = Cell + Gap。</summary>
    public const double Pitch = TileMetrics.Cell + TileMetrics.Gap;

    /// <summary>
    /// 按给定尺寸序列（顺序即磁贴顺序）布局，返回每个磁贴在面板坐标系下的矩形。
    /// 第二参数 rowsPerColumn = 每列最多叠多少行（列数不设上限，满列自动右扩）。
    /// 可选 names/log：逐块打印 <c>LAYOUT tile=… col=… row=… cols=… rows=…</c>（unit 网格坐标），
    /// 并在排布完成后做重叠/越界自检，发现问题打印 <c>LAYOUT-VIOLATION</c>。
    /// </summary>
    public static List<Rect> Layout(IReadOnlyList<Size> sizes, int rowsPerColumn,
        IReadOnlyList<string>? names = null, Action<string>? log = null)
    {
        var result = new List<Rect>(sizes.Count);
        var occupied = new HashSet<(int C, int R)>();
        int rowsPerCol = rowsPerColumn <= 0 ? 4096 : rowsPerColumn;

        for (int i = 0; i < sizes.Count; i++)
        {
            var s = sizes[i];
            int cols = (int)Math.Round((s.Width + TileMetrics.Gap) / Pitch);
            int rows = (int)Math.Round((s.Height + TileMetrics.Gap) / Pitch);
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;

            bool placed = false;

            // 已占用包围盒（用于区分“桥洞”与“还在往下长的活动列”）
            int maxC = 0, maxR = 0;
            foreach (var (pc, pr) in occupied) { if (pc + 1 > maxC) maxC = pc + 1; if (pr + 1 > maxR) maxR = pr + 1; }

            // Phase A —— 空洞回填：仅 1×1 小磁贴允许填“下方已有同列内容”的桥洞。
            // 中/宽/大一律走 Phase B 列优先（先把一列自上而下填满再右扩）。
            if (cols == 1 && rows == 1)
            for (int r = 0; r < maxR && !placed; r++)
            {
                for (int c = 0; c <= maxC - cols && !placed; c++)
                {
                    if (r + rows > rowsPerCol) break;
                    if (!Fits(occupied, c, r, cols, rows)) continue;
                    // 桥洞判定：本列在 r+rows 之下已有内容，说明它是悬空空位而非活动列旁
                    bool bridged = true;
                    for (int dc = 0; dc < cols && bridged; dc++)
                        if (!ColumnHasBelow(occupied, c + dc, r + rows)) bridged = false;
                    if (bridged)
                    {
                        Mark(occupied, c, r, cols, rows);
                        result.Add(new Rect(c * Pitch, r * Pitch, s.Width, s.Height));
                        log?.Invoke($"LAYOUT tile={names?[i] ?? "#" + i} col={c} row={r} cols={cols} rows={rows} (hole-fill)");
                        placed = true;
                    }
                }
            }

            // Phase B —— 向下续列 / 开新列：列优先，一列自上而下填满再右扩。
            for (int c = 0; c < 4096 && !placed; c++)
            {
                for (int r = 0; r < rowsPerCol && !placed; r++)
                {
                    if (r + rows > rowsPerCol) break;
                    if (Fits(occupied, c, r, cols, rows))
                    {
                        Mark(occupied, c, r, cols, rows);
                        result.Add(new Rect(c * Pitch, r * Pitch, s.Width, s.Height));
                        log?.Invoke($"LAYOUT tile={names?[i] ?? "#" + i} col={c} row={r} cols={cols} rows={rows}");
                        placed = true;
                    }
                }
            }
            if (!placed)
            {
                // 兜底：理论上不会走到
                result.Add(new Rect(0, result.Count * Pitch, s.Width, s.Height));
                log?.Invoke($"LAYOUT-VIOLATION no-slot tile={names?[i] ?? "#" + i} cols={cols} rows={rows}");
            }
        }

        // 自检 1：两两重叠（占格集合已保证，这里做防御性核对，边贴边不算重叠）
        for (int a = 0; a < result.Count; a++)
            for (int b = a + 1; b < result.Count; b++)
            {
                var ia = Rect.Intersect(result[a], result[b]);
                if (ia.Width > 0.5 && ia.Height > 0.5)
                    log?.Invoke($"LAYOUT-VIOLATION overlap a=({result[a].X:0},{result[a].Y:0},{result[a].Width:0},{result[a].Height:0}) b=({result[b].X:0},{result[b].Y:0},{result[b].Width:0},{result[b].Height:0})");
            }
        // 自检 2：越出本列行高上限（纵向不该超过 rowsPerCol 个 unit）
        double limit = rowsPerCol * Pitch;
        foreach (var r in result)
            if (r.Bottom > limit + 0.5)
                log?.Invoke($"LAYOUT-VIOLATION out-of-bounds rect=({r.X:0},{r.Y:0},{r.Width:0},{r.Height:0}) bottom={r.Bottom:0} limit={limit:0}");

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

    /// <summary>该列在 rowStart（含）以下是否已被占用（用于判定“悬空桥洞”）。</summary>
    private static bool ColumnHasBelow(HashSet<(int, int)> occ, int c, int rowStart)
    {
        foreach (var (pc, pr) in occ)
            if (pc == c && pr >= rowStart) return true;
        return false;
    }

    private static void Mark(HashSet<(int, int)> occ, int c, int r, int cols, int rows)
    {
        for (int dr = 0; dr < rows; dr++)
            for (int dc = 0; dc < cols; dc++)
                occ.Add((c + dc, r + dr));
    }
}
