using Lockpick.Platform.Windows;

namespace Lockpick.Tests.Platform;

public class GamepadBindingTests
{
    private static readonly GamepadButton XboxLt = new(GamepadButton.XInput, GamepadButton.LeftTrigger);
    private static readonly GamepadButton DualShockL2 = GamepadButton.Hid(0x054C, 0x09CC, 7);
    private static readonly GamepadButton DualSenseL2 = GamepadButton.Hid(0x054C, 0x0CE6, 7);
    private static readonly GamepadButton AdapterButton5 = GamepadButton.Hid(0x0810, 0x0001, 5);

    [Fact]
    public void The_L2_preset_is_LT_on_XInput_pads_and_L2_on_PlayStation_pads()
    {
        var l2 = new GamepadBinding(GamepadBinding.L2);
        Assert.True(l2.Matches(XboxLt));
        Assert.True(l2.Matches(DualShockL2));
        Assert.True(l2.Matches(DualSenseL2));
        Assert.False(l2.Matches(new GamepadButton(GamepadButton.XInput, GamepadButton.RightTrigger)));
        Assert.False(l2.Matches(GamepadButton.Hid(0x054C, 0x09CC, 5))); // L1
        Assert.False(l2.Matches(AdapterButton5));                        // unknown adapter: bind by pressing
    }

    [Fact]
    public void A_bound_button_round_trips_through_the_settings_and_matches_only_itself()
    {
        Assert.Equal("hid:0810:0001/5", AdapterButton5.ToString());
        Assert.True(GamepadButton.TryParse("hid:0810:0001/5", out var parsed));
        Assert.Equal(AdapterButton5, parsed);

        var bound = new GamepadBinding(AdapterButton5.ToString());
        Assert.False(bound.IsOff);
        Assert.True(bound.Matches(AdapterButton5));
        Assert.False(bound.Matches(GamepadButton.Hid(0x0810, 0x0001, 6)));
        Assert.False(bound.Matches(XboxLt));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("hid:12/3")]
    [InlineData("xinput/")]
    public void Anything_else_is_off(string value)
    {
        var binding = new GamepadBinding(value);
        Assert.True(binding.IsOff);
        Assert.False(binding.Matches(XboxLt));
        Assert.Equal("Off", binding.DisplayName);
    }

    [Fact]
    public void Buttons_have_names_a_player_recognises()
    {
        Assert.Equal("LT (L2)", XboxLt.DisplayName);
        Assert.Equal("L2 (PlayStation pad)", DualShockL2.DisplayName);
        Assert.Equal("Button 5 (USB pad 0810:0001)", AdapterButton5.DisplayName);
        Assert.Equal("L2 / LT", new GamepadBinding(GamepadBinding.L2).DisplayName);
    }
}
