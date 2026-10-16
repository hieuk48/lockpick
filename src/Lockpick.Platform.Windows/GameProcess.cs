using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lockpick.Platform.Windows;

/// <summary>Restarts the game for the player (e.g. after changing its display settings, which it reads at start).</summary>
public static partial class GameProcess
{
    private const uint WmClose = 0x0010;

    /// <summary>
    /// Asks the game to close (WM_CLOSE to its window), ends it if it hasn't after <paramref name="grace"/>, then starts
    /// <paramref name="executablePath"/> again from its folder, as the player (never elevated). Returns null, or why not.
    /// </summary>
    public static async Task<string?> RestartAsync(int processId, string executablePath, string? windowClass, TimeSpan grace)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (GameWindows.Find(processId, windowClass) is var window and not 0)
            {
                PostMessage(window, WmClose, 0, 0);
            }

            using (var wait = new CancellationTokenSource(grace))
            {
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    using var killed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(killed.Token).ConfigureAwait(false);
                }
            }
        }
        catch (ArgumentException)
        {
            // Already closed.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return $"Couldn't close the game ({ex.Message}). If it runs as administrator, close it yourself.";
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                UseShellExecute = false,
            });
            return started is null ? "Couldn't start the game again." : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return $"The game closed but couldn't be started again ({ex.Message}).";
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
