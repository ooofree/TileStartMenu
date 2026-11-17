using System.Windows;
using System.Windows.Controls;

namespace TileStartMenu.App.UI;

/// <summary>
/// Metro 磁贴流布局面板：所有子元素按 <see cref="MetroPack"/> 的单元格网格摆放，
/// 保证小/中/宽/大四种尺寸严格对齐。超出 <see cref="MaxColumns"/> 列后自动换行。
/// </summary>
public sealed class MetroFlowPanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(MetroFlowPanel),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>最大列数（超出自动换到下一行）；0 表示不限。</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>最近一次布局算出的每个子元素矩形（面板坐标系），供拖拽命中测试使用。</summary>
    public List<Rect> LastRects { get; private set; } = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }

        var sizes = new List<Size>(Children.Count);
        foreach (UIElement child in Children) sizes.Add(child.DesiredSize);
        LastRects = MetroPack.Layout(sizes, MaxColumns);
        return MetroPack.ContentSize(LastRects);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < Children.Count && i < LastRects.Count; i++)
        {
            Children[i].Arrange(LastRects[i]);
        }
        return finalSize;
    }
}
