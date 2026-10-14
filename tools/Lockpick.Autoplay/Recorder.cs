using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Recording;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.Autoplay;

/// <summary>
/// Records a Lockpick capture (trace, events, memory regions, markers — the app's Debug-page format) while tests
/// run, so the existing analysis tools (tools/bounties) read automated runs like the owner's own sessions. The
/// monitor re-attaches by itself when a test restarts the game.
/// </summary>
internal sealed class Recorder : IAsyncDisposable
{
    private readonly HighResolutionWaitStrategy _wait = new();
    private readonly GameMonitor _monitor;
    private readonly CaptureSession _capture;

    public Recorder(string folder)
    {
        _monitor = new GameMonitor(new Re1Adapter(), new Win32ProcessHost(), new FileFingerprintProvider(),
            new MonitorOptions { WaitStrategy = _wait });
        var version = typeof(Recorder).Assembly.GetName().Version?.ToString(3) ?? "dev";
        _capture = new CaptureSession(_monitor, folder, "re1", version + "-autoplay");
        _monitor.Start();
    }

    public string Directory => _capture.Directory;

    public CaptureMarker Mark(string note) => _capture.AddMarker(note);

    /// <summary>Stops recording and returns the capture zip.</summary>
    public async Task<string> StopAsync()
    {
        await _monitor.StopAsync();
        return _capture.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.DisposeAsync();
        _capture.Dispose();
        _wait.Dispose();
    }
}
