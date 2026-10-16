namespace Lockpick.App.Services;

/// <summary>
/// Streamer mode (Settings → General, #63): file paths, process ids and the API token are blurred wherever Lockpick
/// shows them (elements with the style class <c>private</c>), so the app can be on stream. Copy buttons still work.
/// </summary>
public static class Privacy
{
    private static bool _streamerMode;

    /// <summary>Raised on the UI thread when streamer mode is switched.</summary>
    public static event EventHandler? Changed;

    public static bool StreamerMode
    {
        get => _streamerMode;
        set
        {
            if (_streamerMode != value)
            {
                _streamerMode = value;
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
