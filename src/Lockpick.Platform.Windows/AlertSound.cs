using System.Runtime.InteropServices;

namespace Lockpick.Platform.Windows;

/// <summary>
/// Lockpick's alert sounds: Windows' own (the player's sound scheme decides what each is) or a .wav file of the
/// player's. Played without waiting; a missing or unreadable file plays nothing.
/// </summary>
public static partial class AlertSound
{
    public const string None = "none";

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndFilename = 0x00020000;

    /// <summary>Windows' sounds, by id, with the names Lockpick shows.</summary>
    public static IReadOnlyList<(string Id, string Name)> BuiltIn { get; } =
    [
        ("critical", "Windows: Critical Stop"),
        ("exclamation", "Windows: Exclamation"),
        ("asterisk", "Windows: Asterisk"),
        ("question", "Windows: Question"),
        ("beep", "Windows: Default Beep"),
    ];

    /// <summary>Plays <paramref name="sound"/>: a built-in id, or the path of a .wav file. False when nothing played.</summary>
    public static bool Play(string sound)
    {
        uint? type = sound switch
        {
            "critical" => 0x10,
            "exclamation" => 0x30,
            "asterisk" => 0x40,
            "question" => 0x20,
            "beep" => 0x00,
            _ => null,
        };
        if (type is { } builtIn)
        {
            return MessageBeep(builtIn);
        }

        return sound != None && IsWave(sound) && File.Exists(sound) && PlaySound(sound, 0, SndFilename | SndAsync | SndNoDefault);
    }

    /// <summary>The file is a .wav (the only kind Windows plays without a media player).</summary>
    public static bool IsWave(string path) => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySound(string sound, nint module, uint flags);
}
