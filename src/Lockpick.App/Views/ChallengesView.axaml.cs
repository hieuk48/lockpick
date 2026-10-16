using Avalonia.Controls;
using Avalonia.Interactivity;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class ChallengesView : UserControl
{
    public ChallengesView() => InitializeComponent();

    private async void OnFailFile(object? sender, RoutedEventArgs e) => await UseFile(forDeath: false);

    private async void OnDeathFile(object? sender, RoutedEventArgs e) => await UseFile(forDeath: true);

    private async Task UseFile(bool forDeath)
    {
        if (DataContext is ChallengesViewModel model && await FilePickers.PickSoundFileAsync(this) is { } path)
        {
            model.UseFile(path, forDeath);
        }
    }
}
