using System.Diagnostics;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Memory;
using Lockpick.Core.Processes;
using Lockpick.Platform.Windows;

namespace Lockpick.Tests.Platform;

/// <summary>
/// Real Win32 behavior against a disposable child process (ping.exe, present on every Windows install).
/// Verifies read-only attach, memory reads, exit detection and post-exit read failure.
/// </summary>
public sealed class Win32IntegrationTests : IDisposable
{
    private readonly Process _child;

    public Win32IntegrationTests()
    {
        _child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        WaitForModules(_child.Id);
    }

    public void Dispose()
    {
        if (!_child.HasExited)
        {
            _child.Kill();
            _child.WaitForExit();
        }

        _child.Dispose();
    }

    [Fact]
    public void Suspends_and_resumes_a_whole_process()
    {
        static bool AllSuspended(int id)
        {
            using var p = Process.GetProcessById(id);
            return p.Threads.Cast<ProcessThread>().All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
        }

        // Thread states are sampled by the OS; give them a moment to show the change.
        static bool Eventually(Func<bool> condition)
        {
            for (var i = 0; i < 50 && !condition(); i++)
            {
                Thread.Sleep(20);
            }

            return condition();
        }

        Assert.Null(ProcessSuspender.Suspend(_child.Id));
        Assert.True(Eventually(() => AllSuspended(_child.Id)));
        Assert.Null(ProcessSuspender.Resume(_child.Id));
        Assert.True(Eventually(() => !AllSuspended(_child.Id)));
        Assert.NotNull(ProcessSuspender.Suspend(int.MaxValue - 1)); // no such process: an error, not an exception
    }

    [Fact]
    public void Finds_process_by_executable_name_with_full_path()
    {
        var found = new Win32ProcessHost().FindProcesses(["PING.EXE"]);
        var me = Assert.Single(found, c => c.ProcessId == _child.Id);
        Assert.EndsWith("ping.exe", me.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_the_main_module_pe_header_through_read_only_handle()
    {
        using var process = Win32AttachedProcess.Open(_child.Id);
        Assert.False(process.HasExited);
        Assert.NotEqual(0ul, process.MainModule.BaseAddress);
        Assert.True(process.MainModule.Size > 0);

        var header = new byte[PeImageInfo.HeaderProbeSize];
        Assert.True(process.TryRead(process.MainModule.BaseAddress, header));
        Assert.True(PeImageInfo.TryParse(header, out var pe));
        Assert.Equal(process.MainModule.Size, pe!.SizeOfImage);

        // The on-disk fingerprint of the same file agrees with the mapped header.
        var fp = FileFingerprintProvider.ComputeUncached(process.MainModule.Path!);
        Assert.Equal(pe.TimeDateStamp, fp.Pe!.TimeDateStamp);
    }

    [Fact]
    public void Unmapped_reads_fail_without_throwing()
    {
        using var process = Win32AttachedProcess.Open(_child.Id);
        Assert.False(process.TryReadU32(0x10, out _)); // page zero is never mapped
        Assert.True(process.FailedReads > 0);
    }

    [Fact]
    public void Exit_is_detected_and_reads_fail_afterwards()
    {
        using var process = Win32AttachedProcess.Open(_child.Id);
        var baseAddress = process.MainModule.BaseAddress;
        _child.Kill();
        _child.WaitForExit();

        Assert.True(process.HasExited);
        Assert.False(process.TryReadU32(baseAddress, out _));
    }

    [Fact]
    public void Opening_a_missing_process_reports_access_failure()
    {
        Assert.Throws<ProcessAccessException>(() => Win32AttachedProcess.Open(int.MaxValue - 1));
    }

    [Fact]
    public void High_resolution_wait_is_close_to_requested_period()
    {
        using var wait = new HighResolutionWaitStrategy();
        var samples = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            wait.Wait(TimeSpan.FromMilliseconds(33), CancellationToken.None);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];

        // Median is robust to CI scheduling hiccups. A default-resolution timer quantizes 33 ms up to ~46.9 ms.
        Assert.InRange(median, 31, wait.IsHighResolution ? 40 : 60);
    }

    private static void WaitForModules(int pid)
    {
        // EnumProcessModules can fail while the loader is still initializing the process.
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var _ = Win32AttachedProcess.Open(pid);
                return;
            }
            catch (ProcessAccessException)
            {
                Thread.Sleep(20);
            }
        }
    }
}
