namespace Lockpick.App.Views;

/// <summary>
/// Lockpick never takes the focus from the game while it is exclusive fullscreen and in front: the game would minimize
/// and stop (docs/DISPLAY.md). Windows that would come forward by themselves (the ask-mode question) show without
/// activating then.
/// </summary>
public static class FocusGuard
{
    /// <summary>Set by the app: whether the game is exclusive fullscreen and the foreground window right now.</summary>
    public static Func<bool> GameHoldsTheScreen { get; set; } = () => false;
}
