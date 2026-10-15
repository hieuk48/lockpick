using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Lockpick.Core.Adapters;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.Autoplay;

/// <summary>The running game: read-only state through the RE1 adapter, its window, focus and screenshots.</summary>
internal sealed class Game : IDisposable
{
    /// <summary>Raw PSX button word the game read this tick (MEMORY_MAP "Focus and input").</summary>
    public const ulong HeldButtons = 0xBF0A04;

    private readonly IAttachedProcess _process;
    private readonly IGameSession _session;
    private long _sequence;

    private Game(IAttachedProcess process, IGameSession session)
    {
        _process = process;
        _session = session;
    }

    public bool HasExited => _process.HasExited;

    public int ProcessId => _process.ProcessId;

    public static Game? TryAttach(out string reason)
    {
        var adapter = new Re1Adapter();
        var host = new Win32ProcessHost();
        var candidates = host.FindProcesses(adapter.ExecutableNames);
        var candidate = candidates.Count > 0 ? candidates[0] : null;
        if (candidate?.ExecutablePath is null)
        {
            reason = "the game is not running";
            return null;
        }

        var match = adapter.Identify(FileFingerprintProvider.ComputeUncached(candidate.ExecutablePath));
        if (match.Profile is null)
        {
            reason = match.Reason;
            return null;
        }

        var process = host.OpenReadOnly(candidate);
        reason = "attached";
        return new Game(process, adapter.Attach(process, match.Profile));
    }

    /// <summary>Starts the GOG install's executable (not elevated, so Lockpick can read it).</summary>
    public static void Launch()
    {
        var installed = GogInstallLocator.Find(Re1Adapter.GogProductId)
            ?? throw new InvalidOperationException("no GOG Resident Evil install found");
        var exe = installed.ExecutablePath ?? throw new InvalidOperationException("GOG install has no executable path");
        using var _ = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
    }

    public GameSnapshot Read() => _session.Read(++_sequence, DateTimeOffset.UtcNow);

    public uint ReadU32(ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        return _process.TryRead(address, buffer) ? BitConverter.ToUInt32(buffer) : throw new IOException($"read 0x{address:X} failed");
    }

    public byte[] ReadBytes(ulong address, int length)
    {
        var buffer = new byte[length];
        return _process.TryRead(address, buffer) ? buffer : throw new IOException($"read 0x{address:X} failed");
    }

    public nint Window
    {
        get
        {
            using var process = Process.GetProcessById(_process.ProcessId);
            return process.MainWindowHandle;
        }
    }

    public bool IsForeground => Window != 0 && Native.GetForegroundWindow() == Window;

    /// <summary>
    /// Asks Windows to bring the game to the front; false if it refused. No key is injected to force it: a tapped Alt
    /// lands in whatever window has focus and opened its menus (2026-10-14: the owner's settings and shortcut pages).
    /// </summary>
    public bool Focus()
    {
        if (IsForeground)
        {
            return true;
        }

        var window = Window;
        Native.ShowWindow(window, 9);
        Native.SetForegroundWindow(window);
        Thread.Sleep(300);
        return IsForeground;
    }

    /// <summary>Saves the game's client area as PNG; works behind other windows (PrintWindow with full content).</summary>
    public void Screenshot(string path, int width = 0)
    {
        var window = Window;
        Native.GetClientRect(window, out var rect);
        using var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                if (!Native.PrintWindow(window, dc, 3))
                {
                    throw new IOException("PrintWindow failed");
                }
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (width > 0 && width < bitmap.Width)
        {
            using var small = new Bitmap(bitmap, width, bitmap.Height * width / bitmap.Width);
            small.Save(path, ImageFormat.Png);
            return;
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    public void Dispose() => _process.Dispose();
}
