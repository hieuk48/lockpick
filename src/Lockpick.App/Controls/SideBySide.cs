using Avalonia;
using Avalonia.Controls;

namespace Lockpick.App.Controls;

/// <summary>
/// Two cards: the first at its own width (at most half), the second filling the rest, so a wide window gives its space
/// to the card that can use it (the item box beside the inventory). Below <see cref="StackBelow"/> they stack.
/// </summary>
public sealed class SideBySide : Panel
{
    public static readonly StyledProperty<double> StackBelowProperty =
        AvaloniaProperty.Register<SideBySide, double>(nameof(StackBelow), 900);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SideBySide, double>(nameof(Spacing), 12);

    static SideBySide() => AffectsMeasure<SideBySide>(StackBelowProperty, SpacingProperty);

    public double StackBelow
    {
        get => GetValue(StackBelowProperty);
        set => SetValue(StackBelowProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private bool Stacked(double width) => double.IsInfinity(width) || width < StackBelow;

    private (Control? First, Control? Second) Pair()
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        return (visible.ElementAtOrDefault(0), visible.ElementAtOrDefault(1));
    }

    private double FirstWidth(Control first, double width)
    {
        first.Measure(new Size((width - Spacing) / 2, double.PositiveInfinity));
        return Math.Min(first.DesiredSize.Width, (width - Spacing) / 2);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (first, second) = Pair();
        if (first is null)
        {
            return default;
        }

        var width = availableSize.Width;
        if (Stacked(width) || second is null)
        {
            first.Measure(new Size(width, double.PositiveInfinity));
            second?.Measure(new Size(width, double.PositiveInfinity));
            var height = first.DesiredSize.Height + (second is null ? 0 : Spacing + second.DesiredSize.Height);
            return new Size(Math.Max(first.DesiredSize.Width, second?.DesiredSize.Width ?? 0), height);
        }

        var left = FirstWidth(first, width);
        second.Measure(new Size(width - left - Spacing, double.PositiveInfinity));
        return new Size(width, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (first, second) = Pair();
        if (first is null)
        {
            return finalSize;
        }

        if (Stacked(finalSize.Width) || second is null)
        {
            first.Arrange(new Rect(0, 0, finalSize.Width, first.DesiredSize.Height));
            second?.Arrange(new Rect(0, first.DesiredSize.Height + Spacing, finalSize.Width, second.DesiredSize.Height));
            return finalSize;
        }

        var left = FirstWidth(first, finalSize.Width);
        first.Arrange(new Rect(0, 0, left, first.DesiredSize.Height));
        second.Arrange(new Rect(left + Spacing, 0, finalSize.Width - left - Spacing, second.DesiredSize.Height));
        return finalSize;
    }
}
