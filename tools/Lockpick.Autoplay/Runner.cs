using System.Diagnostics;
using System.Globalization;

namespace Lockpick.Autoplay;

/// <summary>
/// Runs test scripts, one command per line ('#' starts a comment):
///   launch                       start the game if it isn't running and attach
///   restart                      close the game and start it again (keeps 'background' on)
///   speed [ms|normal]            show or set the game's tick interval (normal 33 ms; smaller = fast-forward)
///   place x z [angle]            stand the player at a spot in the current room (PlayerPlacement.cs)
///   freeze-enemies               stop this room's enemies acting (count 0 until the next room; EnemyFreeze.cs)
///   thaw-enemies n               undo freeze-enemies in the same room (n = the count freeze-enemies printed)
///   enemy-hp slot n              set an active enemy's HP (1..5000) so a damage test keeps it alive (EnemyHealth.cs)
///   enemy-place slot x z         move an active enemy (damage tests lock it in front of the player; EnemyHealth.cs)
///   enemy-type slot t            set an active enemy's type (0..0x13) for a damage stand-in
///   enemy-clear-hit slot         clear an enemy's hit state so the next shot can hit it
///   enemy-targetable slot        set an enemy's aim-height bits (it otherwise sets them on noticing the player)
///   enemy-snapshot slot | enemy-restore slot   keep / put back a copy of an enemy's whole entry
///   camera id                    cut to a camera of the room (must have a switch-zone group; CameraCut.cs)
///   inv-clear from               empty inventory slots from..7 (not the equipped one; InventorySlots.cs)
///   give item [qty]              put an item in the first empty inventory slot (default qty 1; InventorySlots.cs)
///   heal                         HP back to the maximum (alive players only)
///   hp n                         set HP to n (1..max, alive players only)
///   flag A|B n 0|1               set or clear one story flag (bank A 0..127, B 0..255; StoryFlags.cs)
///   hold A+RT | release A+RT     hold inputs down until released (e.g. aim with RT, then press A to fire)
///   door hex48                   enter a room through a door record (RoomWarp.cs; dev runs only)
///   focus                        bring the game to the front (only when nobody is using the PC)
///   background                   focus patch: the game keeps running unfocused, keyboard map blanked (restored on exit)
///   press A [ms]                 press (and release) inputs; combine with '+', e.g. press Up+B 400
///   wait ms
///   until room 106 [ms]          wait for a room (default timeout 30 s); also: until gameplay, until field=value
///   expect room 106              fail unless true now; same conditions as until
///   state                        print what Lockpick reads
///   record [folder] | record stop   record a Lockpick capture (memory, trace, markers) across restarts
///   mark note                    add a capture marker
///   peek addr len                print memory (hex, read-only)
///   calibrate                    press every input; print the PSX bits the game read
///   shot file.png [width]        screenshot the game window (optionally scaled down)
///   look question...             screenshot and ask the vision model (LOCKPICK_VISION_URL)
///   echo text
/// Without 'background', input is sent only while the game is the foreground window; the run stops if it loses focus.
/// </summary>
internal sealed class Runner(Pad pad, TextWriter log) : IDisposable
{
    /// <summary>Where the run log goes (the console, or a client of <c>serve</c>).</summary>
    public TextWriter Output { get; set; } = log;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Game? _game;
    private FocusPatch? _patch;
    private RoomWarp? _warp;
    private GameSpeed? _speed;
    private Recorder? _recorder;
    private PlayerPlacement? _placement;
    private InventorySlots? _inventory;
    private StoryFlags? _flags;
    private EnemyHealth? _enemyHealth;
    private CameraCut? _camera;
    private EnemyFreeze? _freeze;

    public Game Game => _game ?? throw new InvalidOperationException("the game is not attached (add 'launch' first)");

    public async Task RunAsync(IEnumerable<string> lines, string shotFolder)
    {
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Log($"> {line}");
            try
            {
                await ExecuteAsync(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), shotFolder);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"line {number} '{line}': {e.Message}", e);
            }
        }
    }

    public void Log(string text) => Output.WriteLine($"[{_clock.Elapsed:mm\\:ss\\.fff}] {text}");

    public void Attach(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        string reason;
        while ((_game = TryAttachOnce(out reason)) is null)
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException(reason);
            }

            Thread.Sleep(500);
        }

        while (Game.Window == 0 && !Game.HasExited)
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("the game has no window yet");
            }

            Thread.Sleep(250);
        }

        Log($"attached ({Describe(Game.Read())})");
    }

    /// <summary>A game that has just been launched may not have its modules readable yet; that's "not ready".</summary>
    private static Game? TryAttachOnce(out string reason)
    {
        try
        {
            return Game.TryAttach(out reason);
        }
        catch (Exception e) when (e is Core.Processes.ProcessAccessException or InvalidOperationException or IOException
                                   or System.ComponentModel.Win32Exception)
        {
            reason = e.Message;
            return null;
        }
    }

    private async Task ExecuteAsync(string[] w, string shotFolder)
    {
        switch (w[0].ToLowerInvariant())
        {
            case "launch":
            case "restart":
                var background = _patch is not null;
                Detach();
                if (Game.TryAttach(out _) is { } running)
                {
                    if (w[0] == "restart")
                    {
                        using (var process = Process.GetProcessById(running.ProcessId))
                        {
                            process.Kill();
                            process.WaitForExit(10000);
                        }

                        running.Dispose();
                        Game.Launch();
                    }
                    else
                    {
                        running.Dispose();
                    }
                }
                else
                {
                    Game.Launch();
                }

                Attach(TimeSpan.FromSeconds(60));
                if (background)
                {
                    _patch = FocusPatch.Apply(Game.ProcessId, Log);
                }

                break;
            case "background":
                _patch ??= FocusPatch.Apply(Game.ProcessId, Log);
                break;
            case "speed":
                _speed ??= new GameSpeed(Game.ProcessId);
                if (w.Length > 1)
                {
                    _speed.SetIntervalMs(w[1] == "normal" ? _speed.OriginalMs : uint.Parse(w[1], CultureInfo.InvariantCulture));
                }

                Log("  " + _speed.Describe());
                break;
            case "place":
                _placement ??= new PlayerPlacement(Game.ProcessId);
                _placement.Place(int.Parse(w[1], CultureInfo.InvariantCulture), int.Parse(w[2], CultureInfo.InvariantCulture),
                    w.Length > 3 ? short.Parse(w[3], CultureInfo.InvariantCulture) : null);
                Log($"  placed ({Describe(Game.Read())})");
                break;
            case "enemy-type":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                var typeWas = _enemyHealth.SetType(int.Parse(w[1], CultureInfo.InvariantCulture), int.Parse(w[2], CultureInfo.InvariantCulture));
                Log($"  enemy {w[1]} type {typeWas} -> {w[2]}");
                break;
            case "enemy-snapshot":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                _enemyHealth.Snapshot(int.Parse(w[1], CultureInfo.InvariantCulture));
                break;
            case "enemy-restore":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                _enemyHealth.Restore(int.Parse(w[1], CultureInfo.InvariantCulture));
                break;
            case "enemy-targetable":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                _enemyHealth.SetTargetable(int.Parse(w[1], CultureInfo.InvariantCulture));
                break;
            case "enemy-clear-hit":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                _enemyHealth.ClearHit(int.Parse(w[1], CultureInfo.InvariantCulture));
                break;
            case "enemy-place":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                _enemyHealth.Place(int.Parse(w[1], CultureInfo.InvariantCulture), int.Parse(w[2], CultureInfo.InvariantCulture),
                    int.Parse(w[3], CultureInfo.InvariantCulture));
                Log($"  enemy {w[1]} placed at {w[2]}, {w[3]}");
                break;
            case "enemy-hp":
                _enemyHealth ??= new EnemyHealth(Game.ProcessId);
                var hpWas = _enemyHealth.Set(int.Parse(w[1], CultureInfo.InvariantCulture), int.Parse(w[2], CultureInfo.InvariantCulture));
                Log($"  enemy {w[1]} HP {hpWas} -> {w[2]}");
                break;
            case "thaw-enemies":
                _freeze ??= new EnemyFreeze(Game.ProcessId);
                _freeze.Thaw(byte.Parse(w[1], CultureInfo.InvariantCulture));
                Log($"  enemy count {w[1]}");
                break;
            case "freeze-enemies":
                _freeze ??= new EnemyFreeze(Game.ProcessId);
                Log($"  enemies frozen (count was {_freeze.Freeze()})");
                break;
            case "camera":
                _camera ??= new CameraCut(Game.ProcessId);
                _camera.Cut(byte.Parse(w[1], CultureInfo.InvariantCulture));
                Log($"  cut to camera {w[1]}");
                break;
            case "inv-clear":
                _inventory ??= new InventorySlots(Game.ProcessId);
                Log($"  cleared {_inventory.ClearFrom(int.Parse(w[1], CultureInfo.InvariantCulture))} inventory slots");
                break;
            case "give":
                _inventory ??= new InventorySlots(Game.ProcessId);
                var given = int.Parse(w[1], CultureInfo.InvariantCulture);
                var slot = _inventory.Give(given, w.Length > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 1);
                Log($"  item {given} in slot {slot}");
                break;
            case "heal":
                _placement ??= new PlayerPlacement(Game.ProcessId);
                Log($"  HP {_placement.Heal()}");
                break;
            case "door":
                var record = Convert.FromHexString(w[1]);
                _warp ??= new RoomWarp(Game.ProcessId);
                _warp.Enter(record);
                Log($"  door handed to the game (destination byte 0x{record[0x0D]:X2}, arrival {BitConverter.ToInt16(record, 0x0E)}, {BitConverter.ToInt16(record, 0x12)})");
                break;
            case "focus":
                if (!Game.Focus())
                {
                    throw new InvalidOperationException("could not bring the game to the front");
                }

                break;
            case "press":
                Press(w[1], w.Length > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 120);
                break;
            case "hold" or "release":
                if (!Pad.IsKnown(w[1]))
                {
                    throw new ArgumentException($"unknown input in '{w[1]}'");
                }

                RequireForeground();
                pad.Set(w[1], w[0] == "hold");
                break;
            case "flag":
                _flags ??= new StoryFlags(Game.ProcessId);
                var flagNumber = int.Parse(w[2], CultureInfo.InvariantCulture);
                var was = _flags.Set(w[1][0], flagNumber, w[3] == "1");
                Log($"  {w[1]}{flagNumber}: {(was ? 1 : 0)} -> {w[3]}");
                break;
            case "hp":
                _placement ??= new PlayerPlacement(Game.ProcessId);
                Log($"  HP {_placement.SetHealth(int.Parse(w[1], CultureInfo.InvariantCulture))}");
                break;
            case "wait":
                await Task.Delay(int.Parse(w[1], CultureInfo.InvariantCulture));
                break;
            case "until":
                var (condition, timeout) = ParseCondition(w[1..]);
                var end = DateTime.UtcNow + timeout;
                while (!condition())
                {
                    if (DateTime.UtcNow > end || Game.HasExited)
                    {
                        throw new TimeoutException($"timed out; now {Describe(Game.Read())}");
                    }

                    await Task.Delay(33);
                }

                Log($"  ok ({Describe(Game.Read())})");
                break;
            case "expect":
                if (!ParseCondition(w[1..]).Condition())
                {
                    throw new InvalidOperationException($"expectation failed; now {Describe(Game.Read())}");
                }

                break;
            case "record":
                if (w.Length > 1 && w[1] == "stop")
                {
                    if (_recorder is not null)
                    {
                        Log("  capture saved: " + await _recorder.StopAsync());
                        await _recorder.DisposeAsync();
                        _recorder = null;
                    }
                }
                else
                {
                    _recorder ??= new Recorder(w.Length > 1 ? string.Join(' ', w[1..]) : shotFolder);
                    Log("  recording to " + _recorder.Directory);
                }

                break;
            case "mark":
                if (_recorder is not null)
                {
                    var marker = _recorder.Mark(string.Join(' ', w[1..]));
                    Log($"  marker {marker.Number} room={marker.Room}");
                }

                break;
            case "peek":
                var at = ulong.Parse(w[1].Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Log($"  0x{at:X}: {Convert.ToHexString(Game.ReadBytes(at, int.Parse(w[2], CultureInfo.InvariantCulture)))}");
                break;
            case "calibrate":
                Calibrate();
                break;
            case "state":
                Log("  " + Describe(Game.Read()));
                break;
            case "shot":
                var path = Path.Combine(shotFolder, w[1]);
                Game.Screenshot(path, w.Length > 2 ? int.Parse(w[2], CultureInfo.InvariantCulture) : 0);
                Log($"  saved {path}");
                break;
            case "look":
                var image = Path.Combine(shotFolder, $"look-{DateTime.Now:HHmmss}.png");
                Game.Screenshot(image);
                Log("  " + (Vision.IsConfigured ? await Vision.AskAsync(image, string.Join(' ', w[1..])) : "(LOCKPICK_VISION_URL not set)"));
                break;
            case "echo":
                Log("  " + string.Join(' ', w[1..]));
                break;
            default:
                throw new ArgumentException($"unknown command '{w[0]}'");
        }
    }

    /// <summary>Presses inputs for <paramref name="holdMs"/> then releases them; the game needs ≥ 2 ticks each way.</summary>
    public void Press(string combo, int holdMs)
    {
        if (!Pad.IsKnown(combo))
        {
            throw new ArgumentException($"unknown input in '{combo}'");
        }

        RequireForeground();
        pad.Set(combo, true);
        try
        {
            Thread.Sleep(Math.Max(holdMs, 80));
        }
        finally
        {
            pad.Set(combo, false);
        }

        Thread.Sleep(80);
    }

    // Presses every input in turn and ORs the game's raw button word over the hold: the controller → PSX mapping the game
    // actually uses (it depends on the player's joystick table, save offset 0xA02).
    public void Calibrate()
    {
        var game = Game;
        RequireForeground();
        var idle = Sample(game, 300);
        Log($"idle word 0x{idle:X4}{(idle != 0 ? " (something is held — results are OR-ed with it)" : string.Empty)}");
        foreach (var input in Pad.AllInputs)
        {
            uint seen = 0;
            var press = Task.Run(() => Press(input, 250));
            while (!press.IsCompleted)
            {
                seen |= game.ReadU32(Game.HeldButtons);
                Thread.Sleep(4);
            }

            press.GetAwaiter().GetResult();
            Thread.Sleep(300);
            Log($"{input,-7} -> 0x{seen & ~idle & 0xFFFF:X4} {Names(seen & ~idle)}");
        }
    }

    private static uint Sample(Game game, int ms)
    {
        uint seen = 0;
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            seen |= game.ReadU32(Game.HeldButtons);
            Thread.Sleep(4);
        }

        return seen;
    }

    private static string Names(uint word)
    {
        (uint Bit, string Name)[] bits =
        [
            (0x1000, "up"), (0x4000, "down"), (0x8000, "left"), (0x2000, "right"), (0x80, "action"), (0x40, "cancel/run"),
            (0x08, "aim"), (0x800, "status"), (0x100, "options?"), (0x10, "0x10"), (0x20, "0x20"), (0x01, "0x01"), (0x02, "0x02"),
            (0x04, "0x04"), (0x200, "0x200"), (0x400, "0x400"),
        ];
        return string.Join(' ', bits.Where(b => (word & b.Bit) != 0).Select(b => b.Name));
    }

    public void RequireForeground()
    {
        if (_patch is null && !Game.IsForeground)
        {
            throw new InvalidOperationException("the game is not the foreground window (is someone using the PC?) — stopped");
        }
    }

    private (Func<bool> Condition, TimeSpan Timeout) ParseCondition(string[] w)
    {
        var timeout = TimeSpan.FromSeconds(30);
        Func<bool> condition;
        if (w[0].Equals("room", StringComparison.OrdinalIgnoreCase))
        {
            var room = w[1];
            condition = () => Game.Read().Location is { HasValue: true } l && l.Value.Id.Equals(room, StringComparison.OrdinalIgnoreCase);
            w = w[2..];
        }
        else if (w[0].Equals("gameplay", StringComparison.OrdinalIgnoreCase))
        {
            condition = () => Game.Read().InGameplay is { HasValue: true, Value: true };
            w = w[1..];
        }
        else if (w[0].Split('=') is [var field, var text])
        {
            var value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : long.Parse(text, CultureInfo.InvariantCulture);
            condition = () => Game.Read().RawFields.TryGetValue(field, out var f)
                ? f.HasValue && f.Value == value
                : throw new ArgumentException($"unknown field '{field}'");
            w = w[1..];
        }
        else
        {
            throw new ArgumentException($"unknown condition '{w[0]}'");
        }

        if (w.Length > 0)
        {
            timeout = TimeSpan.FromMilliseconds(int.Parse(w[0], CultureInfo.InvariantCulture));
        }

        return (condition, timeout);
    }

    public static string Describe(Core.State.GameSnapshot s) =>
        $"gameplay={s.InGameplay} room={s.Location} hp={s.Health} char={s.Character} pos={s.Position}";

    public void Dispose()
    {
        if (_recorder is not null)
        {
            Log("capture saved: " + _recorder.StopAsync().GetAwaiter().GetResult());
            _recorder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _recorder = null;
        }

        Detach();
    }

    /// <summary>Removes the focus patch and lets go of the current game process.</summary>
    private void Detach()
    {
        if (_patch is not null)
        {
            _patch.Dispose();
            _patch = null;
            Log("focus patch removed");
        }

        _warp?.Dispose();
        _warp = null;
        _speed?.Dispose();
        _speed = null;
        _placement?.Dispose();
        _placement = null;
        _inventory?.Dispose();
        _inventory = null;
        _camera?.Dispose();
        _camera = null;
        _freeze?.Dispose();
        _freeze = null;
        _flags?.Dispose();
        _flags = null;
        _enemyHealth?.Dispose();
        _enemyHealth = null;
        _game?.Dispose();
        _game = null;
    }
}
