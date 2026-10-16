using Avalonia;
using Avalonia.Controls;

namespace Lockpick.App.Controls;

/// <summary>
/// Lays cards out in as many columns as the width allows (one on a portrait or small window, up to
/// <see cref="MaxColumns"/> on a wide one), each card going into the shortest column so the columns stay even. Pages use
/// it instead of a single fixed-width column, so a wide window shows more at once instead of leaving most of it empty.
/// Cards keep document order down the first column on one column.
/// </summary>
public sealed class CardColumns : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<CardColumns, double>(nameof(MinColumnWidth), 460);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<CardColumns, int>(nameof(MaxColumns), 3);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<CardColumns, double>(nameof(Spacing), 12);

    static CardColumns() => AffectsMeasure<CardColumns>(MinColumnWidthProperty, MaxColumnsProperty, SpacingProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>As many columns as fit, but no more than there are cards (one card uses the whole width).</summary>
    private int Columns(double width) =>
        double.IsInfinity(width) ? 1
        : Math.Clamp((int)((width + Spacing) / (MinColumnWidth + Spacing)), 1, Math.Max(1, Math.Min(MaxColumns, Children.Count(c => c.IsVisible))));

    private double ColumnWidth(double width, int columns) => (width - (Spacing * (columns - 1))) / columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : ColumnWidth(availableSize.Width, columns);
        var heights = new double[columns];
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            child.Measure(new Size(width, double.PositiveInfinity));
            var shortest = Array.IndexOf(heights, heights.Min());
            heights[shortest] += child.DesiredSize.Height + (heights[shortest] > 0 ? Spacing : 0);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, heights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var width = ColumnWidth(finalSize.Width, columns);
        var heights = new double[columns];
        foreach (var child in Children)
        {
            if (!child.IsVisible)
            {
                continue;
            }

            var shortest = Array.IndexOf(heights, heights.Min());
            var y = heights[shortest] + (heights[shortest] > 0 ? Spacing : 0);
            child.Arrange(new Rect(shortest * (width + Spacing), y, width, child.DesiredSize.Height));
            heights[shortest] = y + child.DesiredSize.Height;
        }

        return finalSize;
    }
}
