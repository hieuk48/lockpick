using System.IO.Pipes;
using System.Text;

namespace Lockpick.Autoplay;

/// <summary>
/// Keeps one virtual controller plugged in for the game's lifetime and runs scripts sent over a local named pipe
/// (current user only), so tests can be issued step by step against one running game. Send "quit" to stop.
/// </summary>
internal static class Server
{
    private const string PipeName = "lockpick-autoplay";
    private const string EndMarker = "\u0004";

    public static async Task ServeAsync(Runner runner, string shotFolder)
    {
        runner.Log($"serving on \\\\.\\pipe\\{PipeName}; screenshots in {shotFolder}");
        while (true)
        {
            await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var lines = new List<string>();
            while (await reader.ReadLineAsync() is { } line && line != EndMarker)
            {
                lines.Add(line);
            }

            if (lines is ["quit"])
            {
                await writer.WriteLineAsync("server stopped");
                return;
            }

            var console = runner.Output;
            runner.Output = TextWriter.Synchronized(new TeeWriter(console, writer));
            try
            {
                await runner.RunAsync(lines, shotFolder);
                runner.Log("ok");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                runner.Log("FAILED: " + e.Message);
            }
            finally
            {
                runner.Output = console;
            }

            await writer.WriteLineAsync(EndMarker);
        }
    }

    public static async Task<int> SendAsync(string script)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(3000);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("no autoplay server (start: lockpick-autoplay serve --launch)");
            return 1;
        }

        // The reader and writer aren't disposed: after "quit" the server closes the pipe first and a flush would throw.
        var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(script.TrimEnd() + "\n" + EndMarker);
        var failed = false;
        try
        {
            while (await reader.ReadLineAsync() is { } line && line != EndMarker)
            {
                Console.WriteLine(line);
                failed |= line.Contains("] FAILED: ", StringComparison.Ordinal);
            }
        }
        catch (IOException)
        {
            // The server went away.
        }

        return failed ? 1 : 0;
    }

    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            first.WriteLine(value);
            second.WriteLine(value);
        }

        public override void Write(char value)
        {
            first.Write(value);
            second.Write(value);
        }
    }
}
