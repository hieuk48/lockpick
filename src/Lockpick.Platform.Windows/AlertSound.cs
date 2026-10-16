using System.Runtime.InteropServices;

namespace Lockpick.Platform.Windows;

/// <summary>Windows' own "critical stop" sound (the player's sound scheme decides what it is; none is silent).</summary>
public static partial class AlertSound
{
    private const uint IconHand = 0x00000010;

    public static void Play() => MessageBeep(IconHand);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
