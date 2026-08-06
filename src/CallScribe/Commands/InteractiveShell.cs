using System.CommandLine;
using System.Text;
using Spectre.Console;

namespace CallScribe.Commands;

/// <summary>The no-arguments home screen: an arrow-key menu plus a typed command palette. Both build
/// an argv and hand it to the same System.CommandLine root the direct CLI uses, so every command runs
/// through its existing handler (I wrap the commands, I do not reimplement them). Explicit
/// <c>call-scribe &lt;command&gt;</c> bypasses this entirely, so Docker, CI, and scripts are unaffected.</summary>
public static class InteractiveShell
{
    // Menu labels, kept as constants so the render and the dispatch switch can never drift apart.
    private const string Start = "Start a call (record + live captions + transcript)";
    private const string Transcribe = "Transcribe a recording";
    private const string Background = "Background recording";
    private const string Devices = "Devices";
    private const string Config = "Config";
    private const string Coach = "Coach";
    private const string Rpg = "RPG";
    private const string Palette = "Type a command...";
    private const string Quit = "Quit";

    // The command currently running from the menu, if any. A Ctrl-C cancels this and returns to the
    // menu; at the menu prompt itself (null) Ctrl-C falls through to the default and exits the app.
    private static CancellationTokenSource? _running;

    public static async Task<int> RunAsync(RootCommand root, CancellationToken ct)
    {
        void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            var running = Volatile.Read(ref _running);
            if (running == null) return; // idle at the menu: let Ctrl-C exit the app
            e.Cancel = true;             // a command is running: stop it, stay in the app
            try { running.Cancel(); } catch { /* already disposed */ }
        }

        Console.CancelKeyPress += OnCancel;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                AnsiConsole.Clear();
                DrainKeys(); // the live dashboard leaves its final frame and buffered keys behind
                RenderHeader();

                var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                    .Title("What would you like to do?")
                    .PageSize(10)
                    .AddChoices(Start, Transcribe, Background, Devices, Config, Coach, Rpg, Palette, Quit));

                if (choice == Quit) return 0;

                var args = Resolve(choice);
                if (args is { Length: > 0 })
                    await RunArgsAsync(root, args, ct).ConfigureAwait(false);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Press Enter to return to the menu.[/]");
                Console.ReadLine();
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static void RenderHeader()
    {
        AnsiConsole.Write(new Rule("[bold]call-scribe[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Local dual-track call recording and transcription. Ctrl-C stops a running command.[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>Turn a home-menu choice into an argv, prompting for any arguments. Returns an empty
    /// array to fall back to the menu without running anything (a "Back" or an empty prompt).</summary>
    private static string[] Resolve(string choice) => choice switch
    {
        Start => WithLabel("start"),
        Transcribe => ["transcribe", Ask("Recording to transcribe", "latest")],
        Background => BackgroundMenu(),
        Devices => ["devices"],
        Config => ConfigMenu(),
        Coach => CoachMenu(),
        Rpg => RpgMenu(),
        Palette => Tokenize(Ask("Command (e.g. config set liveModel base.en)", "")),
        _ => [],
    };

    private static string[] BackgroundMenu()
    {
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Background (detached) recording")
            .AddChoices("Start", "Stop", "Status", "Back"));
        return pick switch
        {
            "Start" => WithLabel("record", "start"),
            "Stop" => ["record", "stop"],
            "Status" => ["record", "status"],
            _ => [],
        };
    }

    private static string[] ConfigMenu()
    {
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Config")
            .AddChoices("View all settings", "Edit a setting", "Back"));
        if (pick == "View all settings") return ["config"];
        if (pick != "Edit a setting") return [];

        var settings = ConfigCommand.ListSettings();
        var key = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Which setting?")
            .PageSize(15)
            .UseConverter(k => $"{k}  [grey]=[/] {Current(settings, k)}")
            .AddChoices([.. settings.Select(s => s.Key), "Back"]));
        if (key == "Back") return [];

        AnsiConsole.MarkupLine($"Current [bold]{key}[/]: {Current(settings, key)}");
        var value = Ask("New value (blank resets to default)", "");
        // A blank value routes to `config set <key> ""`, which resets to the default.
        return ["config", "set", key, value];
    }

    private static string Current(IReadOnlyList<(string Key, string Display)> settings, string key) =>
        settings.FirstOrDefault(s => s.Key == key).Display ?? "";

    private static string[] CoachMenu()
    {
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Coach")
            .AddChoices(
                "Enroll my voice",
                "Enroll a person",
                "List enrolled speakers",
                "Replay a script",
                "Back"));
        switch (pick)
        {
            case "Enroll my voice":
                var me = Ask("Your name", "");
                return me.Length == 0 ? [] : ["coach", "enroll-me", me];
            case "Enroll a person":
                var name = Ask("Person's name", "");
                var wav = Ask("Path to a WAV clip (10s+)", "");
                return name.Length == 0 || wav.Length == 0 ? [] : ["coach", "enroll", name, wav];
            case "List enrolled speakers":
                return ["coach", "speakers"];
            case "Replay a script":
                var script = Ask("Path to a JSONL meeting script", "");
                return script.Length == 0 ? [] : ["coach", "replay", script];
            default:
                return [];
        }
    }

    private static string[] RpgMenu()
    {
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("RPG")
            .AddChoices(
                "Start a call as a boss fight",
                "Replay a script",
                "Back"));
        switch (pick)
        {
            case "Start a call as a boss fight":
                // Same handler as Start; --rpg swaps the coach panel for the boss-fight panel.
                return WithLabel("start", "--rpg");
            case "Replay a script":
                // --fast is left to the typed palette: the time-window rules want real pacing.
                var script = Ask("Path to a JSONL meeting script", "");
                return script.Length == 0 ? [] : ["rpg", "replay", script];
            default:
                return [];
        }
    }

    /// <summary>Prompt for an optional label and append it as <c>--label</c> when given.</summary>
    private static string[] WithLabel(params string[] verb)
    {
        var label = Ask("Label (Enter to skip)", "");
        return label.Length == 0 ? verb : [.. verb, "--label", label];
    }

    private static async Task RunArgsAsync(RootCommand root, string[] args, CancellationToken shellCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shellCt);
        Volatile.Write(ref _running, linked);
        try
        {
            await root.Parse(args).InvokeAsync(cancellationToken: linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
        }
        catch (Exception ex)
        {
            // A command throwing should drop back to the menu, not tear down the shell.
            AnsiConsole.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
        }
        finally
        {
            Volatile.Write(ref _running, null);
        }
    }

    private static string Ask(string prompt, string @default)
    {
        var p = new TextPrompt<string>($"{prompt}:").AllowEmpty();
        if (@default.Length > 0) p.DefaultValue(@default);
        return AnsiConsole.Prompt(p).Trim();
    }

    private static void DrainKeys()
    {
        try { while (Console.KeyAvailable) Console.ReadKey(intercept: true); }
        catch { /* no console / redirected: nothing to drain */ }
    }

    /// <summary>Split a typed command line into argv, honouring double-quoted spans and stripping a
    /// leading "call-scribe" if the user typed the whole command. Good enough for the palette; the
    /// menu builds argv directly and never needs this.</summary>
    internal static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        if (tokens.Count > 0 && tokens[0].Equals("call-scribe", StringComparison.OrdinalIgnoreCase))
            tokens.RemoveAt(0);
        return [.. tokens];
    }
}
