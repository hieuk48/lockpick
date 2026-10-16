using Avalonia.Controls;
using Lockpick.Platform.Windows;

namespace Lockpick.App.Views;

/// <summary>The overlay over the game: click-through, kept just above the game window and never activated (the game pauses when it loses focus).</summary>
public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Handle = TryGetPlatformHandle()?.Handle ?? 0;
    }

    private nint _handle;

    /// <summary>The native window handle once opened (0 before).</summary>
    public nint Handle
    {
        get => _handle;
        private set
        {
            _handle = value;
            ClickThrough = value != 0 && GameWindows.MakeOverlay(value);
        }
    }

    /// <summary>True once the window ignores the mouse and can't be activated.</summary>
    public bool ClickThrough { get; private set; }
}
