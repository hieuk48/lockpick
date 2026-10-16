using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>One overlay widget in Settings: the corner it sits in (or off), and its own window (pop-out).</summary>
public sealed partial class OverlayWidgetRow : ObservableObject
{
    private readonly Action<OverlayWidgetRow> _apply;
    private readonly WidgetWindows _windows;
    private readonly bool _loading = true;

    public OverlayWidgetRow(string id, string name, string corner, IReadOnlyList<string> choices, Action<OverlayWidgetRow> apply, WidgetWindows windows)
    {
        Id = id;
        Name = name;
        Choices = choices;
        _apply = apply;
        _windows = windows;
        Corner = corner;
        _loading = false;
        windows.Changed += (_, _) => OnPropertyChanged(nameof(PopOutText));
    }

    /// <summary>Where the widget sits when placed by hand (the layout editor), or null.</summary>
    public WidgetSpot? Spot { get; set; }

    /// <summary>The pop-out button's text: open the widget's window, or close it.</summary>
    public string PopOutText => _windows.IsOpen(Id) ? "Close window" : "Pop out";

    [RelayCommand]
    private void PopOut() => _windows.Toggle(Id);

    public string Id { get; }

    public string Name { get; }

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    public partial string Corner { get; set; }

    partial void OnCornerChanged(string value)
    {
        if (!_loading && Choices.Contains(value))
        {
            _apply(this);
        }
    }
}
