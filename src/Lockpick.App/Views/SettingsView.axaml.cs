using Avalonia.Controls;
using Avalonia.Interactivity;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        LayoutEditor.Placed += (_, placed) => (DataContext as SettingsViewModel)?.PlaceOverlayWidget(placed.Id, placed.Spot);
    }

    private async void OnOpenKeyPage(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(Services.SpeedrunCom.KeyPage));
        }
    }

    private async void OnSaveLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && await FilePickers.SaveLayoutFileAsync(this) is { } path)
        {
            vm.ExportLayout(path);
        }
    }

    private async void OnLoadLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && await FilePickers.PickLayoutFileAsync(this) is { } path)
        {
            vm.ImportLayout(path);
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && await FilePickers.PickGameExecutableAsync(this) is { } path)
        {
            vm.GameExecutablePath = path;
            vm.CheckInstalledBuild();
        }
    }
}
