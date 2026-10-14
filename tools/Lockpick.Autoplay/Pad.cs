using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace Lockpick.Autoplay;

/// <summary>
/// A virtual Xbox 360 controller (ViGEmBus). Inputs are named A, B, X, Y, Start, Back, LB, RB, LT, RT, LS, RS,
/// Up/Down/Left/Right (d-pad) and LUp/LDown/LLeft/LRight (left stick, full tilt); combine them with '+'.
/// </summary>
internal sealed class Pad : IDisposable
{
    private static readonly Dictionary<string, Xbox360Button> Buttons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = Xbox360Button.A, ["B"] = Xbox360Button.B, ["X"] = Xbox360Button.X, ["Y"] = Xbox360Button.Y,
        ["Start"] = Xbox360Button.Start, ["Back"] = Xbox360Button.Back,
        ["LB"] = Xbox360Button.LeftShoulder, ["RB"] = Xbox360Button.RightShoulder,
        ["LS"] = Xbox360Button.LeftThumb, ["RS"] = Xbox360Button.RightThumb,
        ["Up"] = Xbox360Button.Up, ["Down"] = Xbox360Button.Down, ["Left"] = Xbox360Button.Left, ["Right"] = Xbox360Button.Right,
    };

    private static readonly Dictionary<string, (Xbox360Axis Axis, short Value)> Sticks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LUp"] = (Xbox360Axis.LeftThumbY, short.MaxValue), ["LDown"] = (Xbox360Axis.LeftThumbY, short.MinValue),
        ["LLeft"] = (Xbox360Axis.LeftThumbX, short.MinValue), ["LRight"] = (Xbox360Axis.LeftThumbX, short.MaxValue),
    };

    private static readonly Dictionary<string, Xbox360Slider> Triggers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LT"] = Xbox360Slider.LeftTrigger, ["RT"] = Xbox360Slider.RightTrigger,
    };

    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _controller;

    public Pad()
    {
        _client = new ViGEmClient();
        _controller = _client.CreateXbox360Controller();
        _controller.Connect();
    }

    /// <summary>Every input name, in calibration order.</summary>
    public static IEnumerable<string> AllInputs => Buttons.Keys.Concat(Triggers.Keys).Concat(Sticks.Keys);

    public static bool IsKnown(string combo) =>
        combo.Split('+').All(n => Buttons.ContainsKey(n) || Sticks.ContainsKey(n) || Triggers.ContainsKey(n));

    public void Set(string combo, bool down)
    {
        foreach (var name in combo.Split('+'))
        {
            if (Buttons.TryGetValue(name, out var button))
            {
                _controller.SetButtonState(button, down);
            }
            else if (Sticks.TryGetValue(name, out var stick))
            {
                _controller.SetAxisValue(stick.Axis, down ? stick.Value : (short)0);
            }
            else if (Triggers.TryGetValue(name, out var trigger))
            {
                _controller.SetSliderValue(trigger, down ? byte.MaxValue : (byte)0);
            }
            else
            {
                throw new ArgumentException($"unknown input '{name}'");
            }
        }
    }

    public void ReleaseAll()
    {
        _controller.ResetReport();
        _controller.SubmitReport();
    }

    public void Dispose()
    {
        try
        {
            ReleaseAll();
            _controller.Disconnect();
        }
        finally
        {
            _client.Dispose();
        }
    }
}
