using Avalonia.Controls;
using Avalonia.Input.Platform;
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

    private async void OnCopySummary(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RunsViewModel { LatestSummary: { } summary } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, summary);
        }
    }

    private async void OnSaveRecord(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RunsViewModel model && await FilePickers.SaveRecordFileAsync(this, model.RecordName) is { } path)
        {
            model.SaveRecord(path);
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
