using CommunityToolkit.Mvvm.ComponentModel;

namespace Lockpick.App.ViewModels;

/// <summary>One overlay widget in Settings and the corner it sits in (or off).</summary>
public sealed partial class OverlayWidgetRow : ObservableObject
{
    private readonly Action<OverlayWidgetRow> _apply;
    private readonly bool _loading = true;

    public OverlayWidgetRow(string id, string name, string corner, IReadOnlyList<string> choices, Action<OverlayWidgetRow> apply)
    {
        Id = id;
        Name = name;
        Choices = choices;
        _apply = apply;
        Corner = corner;
        _loading = false;
    }

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
