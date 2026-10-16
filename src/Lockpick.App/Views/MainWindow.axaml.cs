using Avalonia.Controls;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Streamer mode blurs what's private everywhere in the window (and in its dialogs' content below it).
        Classes.Set("streamer", Services.Privacy.StreamerMode);
        Services.Privacy.Changed += (_, _) => Classes.Set("streamer", Services.Privacy.StreamerMode);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Debug.AskPlayer = QuestionWindow.AskAsync;
            }
        };
    }
}
