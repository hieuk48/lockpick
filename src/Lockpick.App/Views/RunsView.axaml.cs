using Avalonia.Controls;
using Avalonia.Interactivity;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class RunsView : UserControl
{
    public RunsView() => InitializeComponent();

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RunsViewModel model && await FilePickers.PickSplitsFileAsync(this) is { } path)
        {
            model.Import(path);
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RunsViewModel model && await FilePickers.SaveSplitsFileAsync(this, model.ExportName) is { } path)
        {
            model.Export(path);
        }
    }
}
