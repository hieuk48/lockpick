using Avalonia;
using Avalonia.Controls;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Streamer mode blurs what's private everywhere in the window (and in its dialogs' content below it).
        Classes.Set("streamer", Privacy.StreamerMode);
        Privacy.Changed += (_, _) => Classes.Set("streamer", Privacy.StreamerMode);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Debug.AskPlayer = QuestionWindow.AskAsync;
                Restore(vm);
            }
        };
        Closing += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                Remember(vm);
            }
        };
    }

    /// <summary>The monitor setup: every screen's place and size (the window is remembered per setup).</summary>
    private string ScreenLayout() =>
        string.Join(";", Screens.All.Select(s => $"{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}").Order(StringComparer.Ordinal));

    /// <summary>Puts the window back where it was on this monitor setup (when that place is still on a screen).</summary>
    private void Restore(MainViewModel vm)
    {
        if (vm.PlacementFor(ScreenLayout()) is not { } placement
            || !Screens.All.Any(s => s.WorkingArea.Contains(new PixelPoint(placement.X + 40, placement.Y + 20))))
        {
            return; // the first time here, or the place is off every screen: centred, as designed
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(placement.X, placement.Y);
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void Remember(MainViewModel vm)
    {
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        var (width, height) = maximized && !double.IsNaN(Width) ? (Width, Height) : (ClientSize.Width, ClientSize.Height);
        vm.SavePlacement(ScreenLayout(), new WindowPlacement(Position.X, Position.Y, width, height, maximized));
    }
}
