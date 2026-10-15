using System.Globalization;

namespace Lockpick.Platform.Windows;

/// <summary>
/// One controller button. XInput pads (Xbox-style, and the virtual pads Steam/DS4Windows make): <see cref="Device"/> is
/// "xinput" and <see cref="Button"/> the bit of <c>wButtons</c> (0–15) or 16/17 for the left/right trigger. Other USB
/// pads (PlayStation pads, PS1 USB adapters) read through Raw Input: "hid:VVVV:PPPP" (vendor and product id) and the HID
/// button usage (1-based; adapters number their buttons differently, which is why a button can be bound by pressing it).
/// </summary>
public readonly record struct GamepadButton(string Device, int Button)
{
    public const string XInput = "xinput";
    public const int LeftTrigger = 16;
    public const int RightTrigger = 17;
    private const string Sony = "hid:054C:";

    private static readonly string[] XInputNames =
    [
        "D-pad up", "D-pad down", "D-pad left", "D-pad right", "Start", "Back", "LS (L3)", "RS (R3)",
        "LB (L1)", "RB (R1)", "Guide", "Bit 11", "A (Cross)", "B (Circle)", "X (Square)", "Y (Triangle)", "LT (L2)", "RT (R2)",
    ];

    // DualShock 4 and DualSense report their buttons in this order (1-based HID usages).
    private static readonly string[] SonyNames =
        ["Square", "Cross", "Circle", "Triangle", "L1", "R1", "L2", "R2", "Share", "Options", "L3", "R3", "PS", "Touchpad"];

    public static GamepadButton Hid(uint vendorId, uint productId, int usage) =>
        new(string.Create(CultureInfo.InvariantCulture, $"hid:{vendorId:X4}:{productId:X4}"), usage);

    /// <summary>"xinput/16", "hid:054C:09CC/7" — the form kept in the settings.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Device}/{Button}");

    public static bool TryParse(string? text, out GamepadButton button)
    {
        button = default;
        var slash = text?.LastIndexOf('/') ?? -1;
        if (text is null || slash <= 0 || !int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var device = text[..slash];
        if (device != XInput && !(device.StartsWith("hid:", StringComparison.Ordinal) && device.Length == 13))
        {
            return false;
        }

        button = new GamepadButton(device, number);
        return true;
    }

    /// <summary>A name a player recognises: "LT (L2)", "L2 (PlayStation pad)", "Button 5 (USB pad 0810:0001)".</summary>
    public string DisplayName =>
        Device == XInput
            ? Button >= 0 && Button < XInputNames.Length ? XInputNames[Button] : $"Button {Button}"
            : Device.StartsWith(Sony, StringComparison.Ordinal) && Button >= 1 && Button <= SonyNames.Length
                ? $"{SonyNames[Button - 1]} (PlayStation pad)"
                : $"Button {Button} (USB pad {Device[4..]})";

    /// <summary>The "L2" preset: LT on an XInput pad, L2 on a DualShock 4 or DualSense.</summary>
    public bool IsL2 => (Device == XInput && Button == LeftTrigger) || (Device.StartsWith(Sony, StringComparison.Ordinal) && Button == 7);
}

/// <summary>
/// The controller button chosen for an action: "Off", "L2" (the preset, see <see cref="GamepadButton.IsL2"/>) or one
/// bound button in <see cref="GamepadButton.ToString"/> form.
/// </summary>
public sealed record GamepadBinding(string Value)
{
    public const string Off = "Off";
    public const string L2 = "L2";

    public bool IsOff => Value == Off || (Value != L2 && !GamepadButton.TryParse(Value, out _));

    public bool Matches(GamepadButton button) =>
        Value == L2 ? button.IsL2 : GamepadButton.TryParse(Value, out var bound) && bound == button;

    public string DisplayName =>
        Value == L2 ? "L2 / LT"
        : GamepadButton.TryParse(Value, out var bound) ? bound.DisplayName
        : "Off";
}
