using Avalonia;
using Avalonia.Controls;

namespace Lockpick.App.Controls;

/// <summary>
/// A long list in newspaper columns: as many columns as the width allows, filled top to bottom and then the next column,
/// so the list keeps its reading order (the story so far, the item box) while a wide window shows all of it at once.
/// Columns are balanced by height.
/// </summary>
public sealed class ListColumns : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<ListColumns, double>(nameof(MinColumnWidth), 380);

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<ListColumns, int>(nameof(MaxColumns), 4);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ListColumns, double>(nameof(ColumnSpacing), 24);

    static ListColumns() => AffectsMeasure<ListColumns>(MinColumnWidthProperty, MaxColumnsProperty, ColumnSpacingProperty);

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

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    private int Columns(double width, int count) =>
        double.IsInfinity(width) || count == 0 ? 1
            : Math.Clamp((int)((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)), 1, Math.Min(Math.Max(1, MaxColumns), count));

    /// <summary>Which column each visible child goes in: in order, a new column once this one reaches its share of the height.</summary>
    private static int[] Assign(List<Control> children, int columns)
    {
        var total = children.Sum(c => c.DesiredSize.Height);
        var target = total / columns;
        var result = new int[children.Count];
        double used = 0;
        var column = 0;
        for (var i = 0; i < children.Count; i++)
        {
            var height = children[i].DesiredSize.Height;
            if (column < columns - 1 && used > 0 && used + (height / 2) > target)
            {
                column++;
                used = 0;
            }

            result[i] = column;
            used += height;
        }

        return result;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var columns = Columns(availableSize.Width, visible.Count);
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : (availableSize.Width - (ColumnSpacing * (columns - 1))) / columns;
        foreach (var child in visible)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
        }

        var heights = new double[columns];
        var assigned = Assign(visible, columns);
        for (var i = 0; i < visible.Count; i++)
        {
            heights[assigned[i]] += visible[i].DesiredSize.Height;
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, heights.Length == 0 ? 0 : heights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var columns = Columns(finalSize.Width, visible.Count);
        var width = (finalSize.Width - (ColumnSpacing * (columns - 1))) / columns;
        var assigned = Assign(visible, columns);
        var y = new double[columns];
        for (var i = 0; i < visible.Count; i++)
        {
            var column = assigned[i];
            visible[i].Arrange(new Rect(column * (width + ColumnSpacing), y[column], width, visible[i].DesiredSize.Height));
            y[column] += visible[i].DesiredSize.Height;
        }

        return finalSize;
    }
}
