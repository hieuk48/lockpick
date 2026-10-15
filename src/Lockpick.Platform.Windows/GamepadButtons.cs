using System.Runtime.InteropServices;

namespace Lockpick.Platform.Windows;

/// <summary>
/// Reports controller button presses whichever window has focus, so a player can mark a moment from the pad while
/// playing. XInput pads are polled; other USB pads (PlayStation pads, PS1 adapters) arrive as HID Raw Input on a
/// message-only window registered with <c>RIDEV_INPUTSINK</c>. It only reads button states: the game and other
/// programs still get every press, and the keyboard is never read. <see cref="ButtonDown"/> is raised on the reader's
/// own thread, once per press.
/// </summary>
public sealed class GamepadButtons : IDisposable
{
    private const string WindowClass = "LockpickGamepadButtons";
    private const byte TriggerPressed = 64;          // of 255: a deliberate pull, not a resting finger
    private const uint PollMilliseconds = 8;
    private static readonly TimeSpan IdleSlotRetry = TimeSpan.FromSeconds(1);

    [ThreadStatic]
    private static GamepadButtons? t_current;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<nint, HidDevice?> _devices = [];
    private readonly HashSet<int>[] _xinputDown = [[], [], [], []];
    private readonly DateTime[] _xinputRetry = new DateTime[4];
    private volatile bool _stop;
    private nint _window;

    public GamepadButtons()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Gamepad buttons" };
        _thread.Start();
        _ready.Wait();
    }

    public event EventHandler<GamepadButton>? ButtonDown;

    /// <summary>False when Windows refused the Raw Input registration (XInput pads still work).</summary>
    public bool HidAvailable { get; private set; }

    public void Dispose()
    {
        if (_stop)
        {
            return;
        }

        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private unsafe void Run()
    {
        t_current = this;
        try
        {
            HidAvailable = CreateWindow();
        }
        finally
        {
            _ready.Set();
        }

        try
        {
            while (!_stop)
            {
                NativeMethods.MsgWaitForMultipleObjects(0, null, false, PollMilliseconds, NativeMethods.QsAllInput);
                while (NativeMethods.PeekMessage(out var message, 0, 0, 0, NativeMethods.PmRemove))
                {
                    NativeMethods.TranslateMessage(message);
                    NativeMethods.DispatchMessage(message);
                }

                PollXInput();
            }
        }
        finally
        {
            if (_window != 0)
            {
                NativeMethods.DestroyWindow(_window);
            }
        }
    }

    private unsafe bool CreateWindow()
    {
        var instance = NativeMethods.GetModuleHandle(null);
        fixed (char* name = WindowClass)
        {
            var windowClass = new NativeMethods.WindowClassEx
            {
                Size = (uint)sizeof(NativeMethods.WindowClassEx),
                WndProc = &WindowProc,
                Instance = instance,
                ClassName = name,
            };
            NativeMethods.RegisterClassEx(&windowClass); // fails harmlessly when an earlier reader registered it
        }

        _window = NativeMethods.CreateWindowEx(0, WindowClass, null, 0, 0, 0, 0, 0, NativeMethods.HwndMessage, 0, instance, 0);
        if (_window == 0)
        {
            return false;
        }

        var devices = stackalloc NativeMethods.RawInputDevice[2];
        devices[0] = new() { UsagePage = NativeMethods.HidUsagePageGeneric, Usage = NativeMethods.HidUsageJoystick, Flags = NativeMethods.RidevInputSink, Target = _window };
        devices[1] = new() { UsagePage = NativeMethods.HidUsagePageGeneric, Usage = NativeMethods.HidUsageGamepad, Flags = NativeMethods.RidevInputSink, Target = _window };
        return NativeMethods.RegisterRawInputDevices(devices, 2, (uint)sizeof(NativeMethods.RawInputDevice));
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmInput && t_current is { } reader)
        {
            try
            {
                reader.OnRawInput(lParam);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A malformed report from one device must not take the reader down.
            }
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private unsafe void OnRawInput(nint handle)
    {
        var headerSize = (uint)sizeof(NativeMethods.RawInputHeader);
        uint size = 0;
        NativeMethods.GetRawInputData(handle, NativeMethods.RidInput, null, ref size, headerSize);
        if (size < headerSize + 8)
        {
            return;
        }

        var buffer = new byte[size];
        fixed (byte* data = buffer)
        {
            if (NativeMethods.GetRawInputData(handle, NativeMethods.RidInput, data, ref size, headerSize) == uint.MaxValue)
            {
                return;
            }

            var header = (NativeMethods.RawInputHeader*)data;
            if (header->Type != NativeMethods.RimTypeHid || Device(header->Device) is not { } device)
            {
                return;
            }

            var reportSize = *(uint*)(data + headerSize);
            var count = *(uint*)(data + headerSize + 4);
            var reports = data + headerSize + 8;
            for (var i = 0; i < count && (i + 1) * reportSize + headerSize + 8 <= size; i++)
            {
                device.Report(this, reports + (i * reportSize), reportSize);
            }
        }
    }

    private HidDevice? Device(nint handle)
    {
        if (!_devices.TryGetValue(handle, out var device))
        {
            device = HidDevice.Open(handle);
            _devices[handle] = device;
        }

        return device;
    }

    private void PollXInput()
    {
        for (uint user = 0; user < 4; user++)
        {
            // An empty slot is slow to ask about; check it about once a second.
            if (_xinputDown[user].Count == 0 && DateTime.UtcNow < _xinputRetry[user])
            {
                continue;
            }

            if (NativeMethods.XInputGetState(user, out var state) != 0)
            {
                _xinputDown[user].Clear();
                _xinputRetry[user] = DateTime.UtcNow + IdleSlotRetry;
                continue;
            }

            _xinputRetry[user] = DateTime.MinValue;
            var pad = state.Gamepad;
            var now = new HashSet<int>();
            for (var bit = 0; bit < 16; bit++)
            {
                if ((pad.Buttons & (1 << bit)) != 0)
                {
                    now.Add(bit);
                }
            }

            if (pad.LeftTrigger >= TriggerPressed)
            {
                now.Add(GamepadButton.LeftTrigger);
            }

            if (pad.RightTrigger >= TriggerPressed)
            {
                now.Add(GamepadButton.RightTrigger);
            }

            Raise(_xinputDown[user], now, b => new GamepadButton(GamepadButton.XInput, b));
        }
    }

    private void Raise(HashSet<int> down, HashSet<int> now, Func<int, GamepadButton> button)
    {
        foreach (var pressed in now)
        {
            if (!down.Contains(pressed))
            {
                ButtonDown?.Invoke(this, button(pressed));
            }
        }

        down.Clear();
        down.UnionWith(now);
    }

    /// <summary>A HID pad: its ids, the parsing data Windows keeps for it, and the buttons held in the last report.</summary>
    private sealed class HidDevice
    {
        private readonly byte[] _preparsed;
        private readonly HashSet<int> _down = [];
        private readonly ushort[] _usages = new ushort[64];

        private HidDevice(uint vendorId, uint productId, byte[] preparsed)
        {
            VendorId = vendorId;
            ProductId = productId;
            _preparsed = preparsed;
        }

        public uint VendorId { get; }

        public uint ProductId { get; }

        public static unsafe HidDevice? Open(nint handle)
        {
            // XInput pads also show up here ("IG_" in the device path); they are read through XInput instead.
            uint size = 0;
            NativeMethods.GetRawInputDeviceInfo(handle, NativeMethods.RidiDeviceName, null, ref size);
            var name = new char[size + 1];
            fixed (char* text = name)
            {
                NativeMethods.GetRawInputDeviceInfo(handle, NativeMethods.RidiDeviceName, text, ref size);
            }

            if (new string(name).Contains("IG_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var info = new NativeMethods.RidDeviceInfoHid { Size = (uint)sizeof(NativeMethods.RidDeviceInfoHid) };
            size = info.Size;
            if (NativeMethods.GetRawInputDeviceInfo(handle, NativeMethods.RidiDeviceInfo, &info, ref size) == uint.MaxValue)
            {
                return null;
            }

            size = 0;
            NativeMethods.GetRawInputDeviceInfo(handle, NativeMethods.RidiPreparsedData, null, ref size);
            if (size == 0)
            {
                return null;
            }

            var preparsed = new byte[size];
            fixed (byte* data = preparsed)
            {
                if (NativeMethods.GetRawInputDeviceInfo(handle, NativeMethods.RidiPreparsedData, data, ref size) == uint.MaxValue)
                {
                    return null;
                }
            }

            return new HidDevice(info.VendorId, info.ProductId, preparsed);
        }

        public unsafe void Report(GamepadButtons reader, byte* report, uint length)
        {
            var count = (uint)_usages.Length;
            int status;
            fixed (byte* preparsed = _preparsed)
            fixed (ushort* usages = _usages)
            {
                status = NativeMethods.HidP_GetUsages(NativeMethods.HidpInput, NativeMethods.HidUsagePageButton, 0, usages, ref count,
                    preparsed, report, length);
            }

            if (status != NativeMethods.HidpStatusSuccess)
            {
                return;
            }

            var now = new HashSet<int>();
            for (var i = 0; i < count; i++)
            {
                now.Add(_usages[i]);
            }

            reader.Raise(_down, now, usage => GamepadButton.Hid(VendorId, ProductId, usage));
        }
    }
}
