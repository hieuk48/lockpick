using System.Runtime.InteropServices;

namespace Lockpick.Platform.Windows;

/// <summary>
/// A system-wide hotkey (<c>RegisterHotKey</c>) that works while another window, such as the game, has focus. It is a
/// registered hotkey, not a keyboard hook: Windows reports only this key combination and nothing else that is typed.
/// The key is registered on a dedicated thread whose message loop receives <c>WM_HOTKEY</c>; <see cref="Pressed"/> is
/// raised on that thread.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private bool _disposed;

    public GlobalHotkey(uint virtualKey, uint modifiers = 0, string? name = null)
    {
        Name = name ?? $"VK 0x{virtualKey:X2}";
        _thread = new Thread(() => Run(virtualKey, modifiers)) { IsBackground = true, Name = $"Hotkey {Name}" };
        _thread.Start();
        _ready.Wait();
    }

    public event EventHandler? Pressed;

    /// <summary>
    /// Keys offered for hotkeys, by display name. Only combinations unlikely to mean anything to the game: F9 closes
    /// RE1 (GOG), and the game reads the keyboard directly, so it still sees whatever is pressed.
    /// </summary>
    public static IReadOnlyDictionary<string, (uint Modifiers, uint VirtualKey)> Choices { get; } = new Dictionary<string, (uint, uint)>
    {
        ["Ctrl+Shift+M"] = (ModControl | ModShift, 0x4D),
        ["Ctrl+Alt+M"] = (ModControl | ModAlt, 0x4D),
        ["Scroll Lock"] = (0, 0x91),
        ["Pause"] = (0, 0x13),
        ["Ctrl+Shift+H"] = (ModControl | ModShift, 0x48),
        ["Ctrl+Shift+J"] = (ModControl | ModShift, 0x4A),
        ["Ctrl+Shift+K"] = (ModControl | ModShift, 0x4B),
        ["Ctrl+Shift+L"] = (ModControl | ModShift, 0x4C),
        ["Ctrl+Shift+1"] = (ModControl | ModShift, 0x31),
        ["Ctrl+Shift+2"] = (ModControl | ModShift, 0x32),
        ["Ctrl+Shift+3"] = (ModControl | ModShift, 0x33),
        ["Ctrl+Alt+H"] = (ModControl | ModAlt, 0x48),
        ["Ctrl+Alt+J"] = (ModControl | ModAlt, 0x4A),
        ["Ctrl+Alt+K"] = (ModControl | ModAlt, 0x4B),
        ["Ctrl+Shift+4"] = (ModControl | ModShift, 0x34),
        ["Ctrl+Shift+5"] = (ModControl | ModShift, 0x35),
        ["Ctrl+Shift+6"] = (ModControl | ModShift, 0x36),
        // LiveSplit's own defaults (split, reset, skip, undo) are number-pad keys with nothing held.
        ["Num 1"] = (0, 0x61),
        ["Num 2"] = (0, 0x62),
        ["Num 3"] = (0, 0x63),
        ["Num 8"] = (0, 0x68),
    };

    /// <summary>Registers one of <see cref="Choices"/>; null for an unknown name.</summary>
    public static GlobalHotkey? Create(string name) =>
        Choices.TryGetValue(name, out var key) ? new GlobalHotkey(key.VirtualKey, key.Modifiers, name) : null;

    public string Name { get; }

    /// <summary>False when Windows refused the key (usually because another program already registered it).</summary>
    public bool IsRegistered { get; private set; }

    public int Error { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Run(uint virtualKey, uint modifiers)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        IsRegistered = NativeMethods.RegisterHotKey(0, HotkeyId, modifiers | NativeMethods.ModNoRepeat, virtualKey);
        Error = IsRegistered ? 0 : Marshal.GetLastPInvokeError();

        // Any user32 call creates the thread's message queue, so PostThreadMessage(WM_QUIT) can't be lost after this.
        _ready.Set();
        try
        {
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == NativeMethods.WmHotkey && message.WParam == HotkeyId)
                {
                    Pressed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            if (IsRegistered)
            {
                NativeMethods.UnregisterHotKey(0, HotkeyId);
            }
        }
    }
}
