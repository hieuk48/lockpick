using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.Core.Display;

namespace Lockpick.App.ViewModels;

/// <summary>One of the game's other display options (Settings → Game display → More settings).</summary>
public sealed partial class DisplayOptionRow(DisplayOption option, string current) : ObservableObject
{
    public string Key => option.Key;

    public string Label => option.Label;

    public IReadOnlyList<DisplayOptionChoice> Choices => option.Choices;

    /// <summary>The value in the game's file when the page was opened.</summary>
    public string Loaded { get; } = current;

    [ObservableProperty]
    public partial DisplayOptionChoice? Selected { get; set; } =
        option.Choices.FirstOrDefault(c => c.Value == current) ?? option.Choices.FirstOrDefault(c => c.Value == option.Default);
}
