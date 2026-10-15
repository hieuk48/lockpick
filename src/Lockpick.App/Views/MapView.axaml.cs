using Avalonia;
using Avalonia.Controls;

namespace Lockpick.App.Views;

public partial class MapView : UserControl
{
    // Below this width (e.g. the portrait monitor) the legend goes under the map instead of beside it.
    private const double NarrowWidth = 760;

    public MapView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    private void Arrange(double width)
    {
        var narrow = width < NarrowWidth;
        Grid.SetRow(LegendCard, narrow ? 2 : 1);
        Grid.SetColumn(LegendCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(LegendCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(MapCard, narrow ? 2 : 1);
        LegendCard.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(12, 0, 0, 0);
        LegendCard.MaxHeight = narrow ? 260 : double.PositiveInfinity;
    }
}
