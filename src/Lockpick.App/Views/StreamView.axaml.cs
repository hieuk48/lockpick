using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Lockpick.App.Views;

public partial class StreamView : UserControl
{
    public StreamView() => InitializeComponent();

    private async void OnCopyObsLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, url);
            Copied.IsVisible = true;
        }
    }
}
