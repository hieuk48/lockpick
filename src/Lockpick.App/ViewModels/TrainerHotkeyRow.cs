using CommunityToolkit.Mvvm.ComponentModel;

namespace Lockpick.App.ViewModels;

/// <summary>One trainer action in Settings and the key chosen for it.</summary>
public sealed partial class TrainerHotkeyRow : ObservableObject
{
    private readonly Func<TrainerHotkeyRow, string, string?> _apply;
    private bool _loading = true;

    public TrainerHotkeyRow(string id, string name, string key, IReadOnlyList<string> choices, Func<TrainerHotkeyRow, string, string?> apply)
    {
        Id = id;
        Name = name;
        Choices = choices;
        _apply = apply;
        Key = key;
        _loading = false;
    }

    public string Id { get; }

    public string Name { get; }

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    public partial string Key { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    partial void OnKeyChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        var problem = _apply(this, value);
        Status = problem ?? string.Empty;
        if (problem is not null)
        {
            // Show what is in effect: the key wasn't taken, so the action has none.
            _loading = true;
            Key = "Off";
            _loading = false;
        }
    }
}
