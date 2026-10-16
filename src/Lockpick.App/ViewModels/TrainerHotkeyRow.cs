using CommunityToolkit.Mvvm.ComponentModel;

namespace Lockpick.App.ViewModels;

/// <summary>One trainer action in Settings and the key chosen for it.</summary>
public sealed partial class TrainerHotkeyRow : ObservableObject
{
    private readonly Func<TrainerHotkeyRow, string, string?> _apply;
    private bool _loading = true;

    private readonly Func<TrainerHotkeyRow, Task<string?>>? _bindButton;
    private readonly Action<TrainerHotkeyRow>? _clearButton;

    public TrainerHotkeyRow(string id, string name, string key, IReadOnlyList<string> choices, Func<TrainerHotkeyRow, string, string?> apply,
        string button = "Off", Func<TrainerHotkeyRow, Task<string?>>? bindButton = null, Action<TrainerHotkeyRow>? clearButton = null)
    {
        Id = id;
        Name = name;
        Choices = choices;
        _apply = apply;
        _bindButton = bindButton;
        _clearButton = clearButton;
        Key = key;
        ButtonName = button;
        _loading = false;
    }

    /// <summary>The controller button for this action ("Off" when none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasButton))]
    public partial string ButtonName { get; set; }

    public bool HasButton => ButtonName != "Off";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BindButtonCommand))]
    public partial bool Binding { get; private set; }

    /// <summary>Waits for a controller button and binds it (the settings say what happened).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(CanBind))]
    private async Task BindButtonAsync()
    {
        if (_bindButton is null)
        {
            return;
        }

        Binding = true;
        Status = "Press the button on your controller (10 seconds)…";
        try
        {
            Status = await _bindButton(this) ?? string.Empty;
        }
        finally
        {
            Binding = false;
        }
    }

    private bool CanBind() => !Binding;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearButton()
    {
        _clearButton?.Invoke(this);
        ButtonName = "Off";
        Status = string.Empty;
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
