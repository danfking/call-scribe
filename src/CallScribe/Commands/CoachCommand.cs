using System.CommandLine;
using CallScribe.Coach;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Coach.Mock;
using CallScribe.Coach.Speaker;
using CallScribe.Transcription;
using Spectre.Console;

namespace CallScribe.Commands;

public static class CoachCommand
{
    public static Command Create()
    {
        var scriptArgument = new Argument<string>("script")
        {
            Description = "Path to a JSONL meeting script ({\"t\": seconds, \"speaker\": \"Me|Others\", \"text\": \"...\"})",
        };
        var fastOption = new Option<bool>("--fast")
        {
            Description = "Ignore timestamps and replay as fast as possible",
        };
        var stubOption = new Option<bool>("--stub")
        {
            Description = "Force the deterministic stub advisor (no model calls), even if Ollama is up",
        };

        var replay = new Command("replay", "Replay a scripted meeting through the coach (no audio) for testing");
        replay.Arguments.Add(scriptArgument);
        replay.Options.Add(fastOption);
        replay.Options.Add(stubOption);
        replay.SetAction((parseResult, ct) =>
            ReplayAsync(parseResult.GetValue(scriptArgument)!, parseResult.GetValue(fastOption),
                parseResult.GetValue(stubOption), ct));

        var memtest = new Command("memtest", "Verify the memory store end-to-end: seed memories, then recall by similarity");
        memtest.SetAction((_, ct) => MemTestAsync(ct));

        var meetingOption = new Option<string?>("--meeting")
        {
            Description = "Only forget this meeting's memories (default: forget all)",
        };
        var forget = new Command("forget", "Delete stored coach memories (all, or one meeting's)");
        forget.Options.Add(meetingOption);
        forget.SetAction((parseResult, ct) => ForgetAsync(parseResult.GetValue(meetingOption), ct));

        var nameArgument = new Argument<string>("name") { Description = "Person's name to enroll the voice under" };
        var wavArgument = new Argument<string>("wav") { Description = "Path to a WAV clip of this person speaking (ideally 10s+, just them)" };
        var enroll = new Command("enroll", "Enroll a person's voice so they are auto-named in future calls");
        enroll.Arguments.Add(nameArgument);
        enroll.Arguments.Add(wavArgument);
        enroll.SetAction((parseResult, ct) =>
            EnrollAsync(parseResult.GetValue(nameArgument)!, parseResult.GetValue(wavArgument)!, ct));

        var speakers = new Command("speakers", "List the people with an enrolled voiceprint");
        speakers.SetAction((_, ct) => SpeakersAsync(ct));

        var stemArgument = new Argument<string>("stem")
        {
            Description = "Recording stem (name under the recordings dir, or a full path) to attribute",
        };
        var diarize = new Command("diarize",
            "Identify the far-side speakers in a finished recording and rewrite its transcript with names");
        diarize.Arguments.Add(stemArgument);
        diarize.SetAction((parseResult, ct) => DiarizeAsync(parseResult.GetValue(stemArgument)!, ct));

        var personOption = new Option<string?>("--person")
        {
            Description = "Only forget this person's voiceprint (default: forget all)",
        };
        var forgetVoices = new Command("forget-voices", "Delete enrolled voiceprints (all, or one person's)");
        forgetVoices.Options.Add(personOption);
        forgetVoices.SetAction((parseResult, ct) => ForgetVoicesAsync(parseResult.GetValue(personOption), ct));

        var command = new Command("coach", "Realtime meeting coach (experimental)");
        command.Subcommands.Add(replay);
        command.Subcommands.Add(memtest);
        command.Subcommands.Add(forget);
        command.Subcommands.Add(enroll);
        command.Subcommands.Add(speakers);
        command.Subcommands.Add(diarize);
        command.Subcommands.Add(forgetVoices);
        return command;
    }

    /// <summary>Build the voiceprint store, or print why it is unavailable and return null.
    /// The embed model must be present because its dimension fixes the vector column.</summary>
    private static async Task<(ISpeakerEmbedder Embedder, VoiceprintStore Store)?> OpenVoiceprintsAsync(
        AppConfig config, CancellationToken ct)
    {
        var embedder = SpeakerIdentity.TryCreateEmbedder(config);
        if (embedder == null)
        {
            AnsiConsole.MarkupLine(
                "[red]Speaker models not installed.[/] Run [grey]scripts/coach-pull-speaker-models.ps1[/].");
            return null;
        }

        var store = await SpeakerIdentity.TryCreateVoiceprintsAsync(config, embedder.Dimensions, ct).ConfigureAwait(false);
        if (store == null)
        {
            embedder.Dispose();
            AnsiConsole.MarkupLine(
                $"[red]Voiceprint store unavailable.[/] Need Postgres reachable at "
                + $"[grey]{config.PostgresConn.EscapeMarkup()}[/].");
            return null;
        }
        return (embedder, store);
    }

    private static async Task<int> EnrollAsync(string name, string wav, CancellationToken ct)
    {
        if (!File.Exists(wav))
        {
            AnsiConsole.MarkupLine($"[red]Audio not found:[/] {wav.EscapeMarkup()}");
            return 1;
        }

        var config = AppConfig.Load();
        var opened = await OpenVoiceprintsAsync(config, ct).ConfigureAwait(false);
        if (opened == null) return 1;
        var (embedder, store) = opened.Value;
        try
        {
            var embedding = embedder.Embed(SpeakerAudio.ReadWav16kMono(wav));
            if (embedding.Length == 0)
            {
                AnsiConsole.MarkupLine("[red]Clip too short or too quiet[/] to characterise a voice.");
                return 1;
            }
            await store.EnrollAsync(name, embedding, ct).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[green]Enrolled[/] {name.EscapeMarkup()}.");
            return 0;
        }
        finally
        {
            embedder.Dispose();
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> SpeakersAsync(CancellationToken ct)
    {
        var config = AppConfig.Load();
        var opened = await OpenVoiceprintsAsync(config, ct).ConfigureAwait(false);
        if (opened == null) return 1;
        var (embedder, store) = opened.Value;
        try
        {
            var people = await store.ListPeopleAsync(ct).ConfigureAwait(false);
            if (people.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No enrolled speakers yet.[/] Enroll with [grey]coach enroll <name> <wav>[/].");
            }
            else
            {
                foreach (var person in people) AnsiConsole.MarkupLine($"  {person.EscapeMarkup()}");
            }
            return 0;
        }
        finally
        {
            embedder.Dispose();
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> DiarizeAsync(string stem, CancellationToken ct)
    {
        var config = AppConfig.Load();
        var stemPath = Path.IsPathRooted(stem) ? stem : Path.Combine(AppPaths.RecordingsDir, stem);
        await SpeakerAttributionFlow.RunAsync(stemPath, config, interactive: true, ct).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ForgetVoicesAsync(string? person, CancellationToken ct)
    {
        var config = AppConfig.Load();
        var opened = await OpenVoiceprintsAsync(config, ct).ConfigureAwait(false);
        if (opened == null) return 1;
        var (embedder, store) = opened.Value;
        try
        {
            var deleted = await store.ForgetAsync(person, ct).ConfigureAwait(false);
            var scope = person is null ? "all speakers" : $"'{person}'";
            AnsiConsole.MarkupLine($"[green]Forgot[/] {deleted} voiceprint(s) ({scope.EscapeMarkup()}).");
            return 0;
        }
        finally
        {
            embedder.Dispose();
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> ForgetAsync(string? meetingId, CancellationToken ct)
    {
        var config = AppConfig.Load();
        var memory = await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
        if (memory == null)
        {
            AnsiConsole.MarkupLine(
                "[red]Memory unavailable.[/] Need Ollama (with the embed model) and Postgres reachable at "
                + $"[grey]{config.PostgresConn.EscapeMarkup()}[/].");
            return 1;
        }

        await using var store = memory;
        var deleted = await store.ClearMemoriesAsync(meetingId, ct).ConfigureAwait(false);
        var scope = meetingId is null ? "all meetings" : $"meeting '{meetingId}'";
        AnsiConsole.MarkupLine($"[green]Forgot[/] {deleted} memories ({scope}).");
        return 0;
    }

    private static async Task<int> ReplayAsync(string script, bool fast, bool stub, CancellationToken ct)
    {
        if (!File.Exists(script))
        {
            AnsiConsole.MarkupLine($"[red]Script not found:[/] {script.EscapeMarkup()}");
            return 1;
        }

        var config = AppConfig.Load();
        var memory = stub ? null : await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
        var (advisor, usingModel) = CoachFactory.CreateAdvisor(config, forceStub: stub, memory);
        var meetingId = $"{Path.GetFileNameWithoutExtension(script)}-replay";

        using var display = new LiveStatusDisplay();
        display.EnableAdvicePanel();
        display.Configure(usingModel ? config.FastModel : "stub advisor");
        display.Register(LiveCaptionEngine.OthersLabel, "yellow");
        display.Register(LiveCaptionEngine.MeLabel, "cyan");

        using var coach = new CoachEngine(advisor, memory, meetingId);
        coach.AdviceEmitted += a => display.PrintAdvice(a.At, a.Colour, a.Glyph, a.Text);

        await MockMeetingDriver.ReplayAsync(script, display, coach, realtime: !fast, ct).ConfigureAwait(false);
        await coach.CompleteAsync().ConfigureAwait(false);
        display.Shutdown();

        if (memory != null)
        {
            var consolidator = new MeetingConsolidator(
                new OllamaChat(config.OllamaUrl, config.OllamaKeepAlive), config.ReasoningModel, memory);
            var stored = await consolidator.ConsolidateAsync(meetingId, ct).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[green]Consolidated[/] {stored} memories from this meeting.");
            await memory.DisposeAsync().ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> MemTestAsync(CancellationToken ct)
    {
        var config = AppConfig.Load();
        var memory = await CoachFactory.TryCreateMemoryAsync(config, ct).ConfigureAwait(false);
        if (memory == null)
        {
            AnsiConsole.MarkupLine(
                "[red]Memory unavailable.[/] Need Ollama (with the embed model) and Postgres reachable at "
                + $"[grey]{config.PostgresConn.EscapeMarkup()}[/].");
            return 1;
        }

        await using var store = memory;
        const string meetingId = "memtest";
        // Deliberately well-separated topics so any reasonable embedder ranks them
        // correctly — this verifies the embed -> store -> vector-search pipeline, not
        // the model's nuance on near-synonyms.
        var seeds = new (MemoryKind Kind, string Text)[]
        {
            (MemoryKind.Decision, "We chose PostgreSQL with the pgvector extension as the database for storage."),
            (MemoryKind.PersonFact, "Priya is allergic to peanuts and cycles to the office every day."),
            (MemoryKind.ActionItem, "Remind the team to submit their timesheets before the holidays."),
        };
        foreach (var (kind, text) in seeds)
        {
            await store.StoreMemoryAsync(meetingId, kind, text, person: null, ct).ConfigureAwait(false);
        }
        AnsiConsole.MarkupLine($"[green]Seeded[/] {seeds.Length} memories.");

        const string query = "Which database did we pick for storing data?";
        AnsiConsole.MarkupLine($"\n[grey]Query:[/] {query.EscapeMarkup()}");
        var recalled = await store.RecallAsync(query, topK: 3, ct).ConfigureAwait(false);
        foreach (var memoryItem in recalled)
        {
            AnsiConsole.MarkupLine(
                $"  [grey]{memoryItem.Distance:F3}[/]  [cyan]{memoryItem.Kind}[/]  {memoryItem.Text.EscapeMarkup()}");
        }

        var top = recalled.Count > 0 ? recalled[0] : default;
        var ok = recalled.Count > 0 && top.Kind == MemoryKind.Decision;
        AnsiConsole.MarkupLine(ok
            ? "\n[green]OK[/] nearest memory is the decision, as expected."
            : "\n[red]Unexpected[/] nearest memory.");
        return ok ? 0 : 1;
    }
}
