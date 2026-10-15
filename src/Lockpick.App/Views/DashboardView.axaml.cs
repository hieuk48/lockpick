using Avalonia;
using Avalonia.Controls;
using Lockpick.App.Controls;

namespace Lockpick.App.Views;

public partial class DashboardView : UserControl
{
    /// <summary>Below this content width the cards stack in one column (portrait / side-by-side with the game).</summary>
    public const double SingleColumnBelow = 780;

    /// <summary>From this width (ultrawide) the event log gets a column of its own beside the other cards.</summary>
    public const double ThreeColumnsFrom = 1800;

    private int _columns;

    public DashboardView()
    {
        InitializeComponent();
        ApplyLayout(2);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ApplyLayout(Responsive.IsCompact(Bounds.Width, SingleColumnBelow) ? 1 : Bounds.Width >= ThreeColumnsFrom ? 3 : 2);
        }
    }

    private void ApplyLayout(int columns)
    {
        if (_columns == columns)
        {
            return;
        }

        _columns = columns;
        // The game state card keeps its natural height (its health bar sits under the values, not at the bottom).
        StateCard.VerticalAlignment = columns == 1 ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Top;
        Grid.SetRowSpan(EventsCard, 1);
        if (columns == 3)
        {
            Root.ColumnDefinitions = new ColumnDefinitions("*,*,1.3*");
            Root.RowDefinitions = new RowDefinitions("Auto,*");
            Place(ConnectionCard, 0, 0);
            Place(BuildCard, 1, 0);
            Place(StateCard, 0, 1);
            Grid.SetRowSpan(StateCard, 2);
            Place(EventsCard, 0, 2);
            Grid.SetRowSpan(EventsCard, 2);
            BuildCard.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }
        else if (columns == 1)
        {
            Grid.SetRowSpan(StateCard, 1);
            Root.ColumnDefinitions = new ColumnDefinitions("*");
            Root.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*");
            Place(ConnectionCard, 0, 0);
            Place(BuildCard, 1, 0);
            Place(StateCard, 2, 0);
            Place(EventsCard, 3, 0);
        }
        else
        {
            Grid.SetRowSpan(StateCard, 1);
            BuildCard.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            Root.ColumnDefinitions = new ColumnDefinitions("*,*");
            Root.RowDefinitions = new RowDefinitions("Auto,*");
            Place(ConnectionCard, 0, 0);
            Place(BuildCard, 0, 1);
            Place(StateCard, 1, 0);
            Place(EventsCard, 1, 1);
        }
    }

    private static void Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
    }
}
