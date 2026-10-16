namespace Lockpick.App.Services;

/// <summary>
/// Which overlay widgets are open in their own windows, and the way to open or close one (Settings, the Stream page).
/// The windows themselves belong to the app (PopOutManager); this is the part view models can see. UI thread only.
/// </summary>
public sealed class WidgetWindows
{
    private readonly HashSet<string> _open = [];

    /// <summary>Someone asked to open or close the widget's window.</summary>
    public event EventHandler<string>? ToggleRequested;

    /// <summary>A window opened or closed.</summary>
    public event EventHandler? Changed;

    public bool IsOpen(string id) => _open.Contains(id);

    public void Toggle(string id) => ToggleRequested?.Invoke(this, id);

    /// <summary>Set by the windows' owner as they open and close.</summary>
    public void SetOpen(string id, bool open)
    {
        if (open ? _open.Add(id) : _open.Remove(id))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
