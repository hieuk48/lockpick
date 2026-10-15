using System.Globalization;
using Lockpick.Autoplay;

// DEVELOPER TOOL ONLY — never shipped (see Lockpick.Autoplay.csproj and docs/DEVELOPMENT.md).
// lockpick-autoplay run <script.txt> [--shots <folder>]       run a test script (commands: see Runner.cs)
// lockpick-autoplay calibrate [--launch] [--background]       press each input, report the PSX bits the game read
// lockpick-autoplay press <inputs> [ms] [--focus]              e.g. press A, press Up+B 400
// lockpick-autoplay serve [--launch] [--background]           keep the controller plugged in; take scripts over a pipe
// lockpick-autoplay send <command; command; ...>               run commands on the server (or: send --file script.txt)
// --background applies the dev-only focus patch (FocusPatch.cs): the game keeps running unfocused, restored on exit.
// The virtual controller is plugged in for the run and unplugged at the end. The game only finds controllers when it
// starts, so launch it with the controller present: `calibrate --launch`, a script's `launch`, or `serve --launch`.
var command = args.Length > 0 ? args[0] : "help";
var launch = args.Contains("--launch");
var focus = args.Contains("--focus");
var background = args.Contains("--background");
var rest = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

// UTF-8 out, so clients (tools/autoplay/ap.py) read the log's dashes and arrows instead of the console code page.
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (command == "send")
{
    var script = args.Contains("--file") ? await File.ReadAllTextAsync(rest[0]) : string.Join(' ', rest).Replace(';', '\n');
    return await Server.SendAsync(script);
}

if (command is not ("run" or "calibrate" or "press" or "serve"))
{
    Console.WriteLine("usage: lockpick-autoplay run <script.txt> [--shots folder] | calibrate [--launch] [--background] [--focus] | press <inputs> [ms] [--focus] | serve [--launch] [--background] | send <commands>");
    return command == "help" ? 0 : 2;
}

using var pad = new Pad();
using var runner = new Runner(pad, Console.Out);
Console.CancelKeyPress += (_, _) => pad.ReleaseAll();
try
{
    switch (command)
    {
        case "run":
            var shots = args.SkipWhile(a => a != "--shots").Skip(1).FirstOrDefault()
                ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rest[0]))!, "shots");
            await runner.RunAsync(File.ReadLines(rest[0]), shots);
            break;
        case "calibrate":
            if (launch)
            {
                // The pad is already plugged in, so the game finds it when it enumerates controllers at startup.
                await runner.RunAsync(["launch", "wait 8000"], string.Empty);
            }
            else
            {
                runner.Attach(TimeSpan.Zero);
            }

            if (background)
            {
                await runner.RunAsync(["background"], string.Empty);
            }

            if (focus && !runner.Game.Focus())
            {
                throw new InvalidOperationException("could not bring the game to the front");
            }

            runner.Calibrate();
            break;
        case "serve":
            if (launch)
            {
                await runner.RunAsync(["launch"], string.Empty);
            }

            else
            {
                runner.Attach(TimeSpan.Zero);
            }

            if (background)
            {
                await runner.RunAsync(["background"], string.Empty);
            }

            await Server.ServeAsync(runner, Path.Combine(Path.GetTempPath(), "lockpick-autoplay"));
            break;
        case "press":
            runner.Attach(TimeSpan.Zero);
            if (focus)
            {
                runner.Game.Focus();
            }

            runner.Press(rest[0], rest.Length > 1 ? int.Parse(rest[1], CultureInfo.InvariantCulture) : 120);
            break;
    }

    runner.Log("done");
    return 0;
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    runner.Log("FAILED: " + e.Message);
    return 1;
}
