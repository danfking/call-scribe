using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Coach.Profiles;
using CallScribe.Transcription;

namespace CallScribe.Coach;

/// <summary>Reflect + Act in a single fast-model call: given the recent transcript, the
/// model decides whether a brief, genuinely useful piece of advice would help the user
/// ("Me") and, if so, writes it. The model gates itself (advise sparingly) and returns
/// strict JSON so parsing is robust; a non-advise or unparseable reply yields no advice.</summary>
public sealed class LlmAdvisor : IAdvisor
{
    private const int ContextLines = 8;

    // Person-aware coaching: how many of the most-recently-active named people to inject profiles
    // for, and a per-person size cap. The cap matches the updater's ~250-word target with headroom, so
    // a normal profile passes whole and truncation only ever trims an oversized hand-edited file.
    private const int MaxProfiles = 3;
    private const int MaxProfileChars = 1600;

    private static readonly string SystemPrompt =
        """
        You are a silent real-time meeting co-pilot for the participant labelled "Me".
        You see a rolling transcript where "Me" is your user and every other speaker is on
        the far side, labelled by name when known (e.g. "Gavin") or as "Speaker 1",
        "Speaker 2", … when not yet identified. Use those names when advising.
        Decide whether a brief, genuinely useful piece of advice or a factual answer would
        help "Me" right now. Advise SPARINGLY — only for a real question to answer, an
        objection to handle, a factual gap worth filling, or a risk worth flagging.
        Never advise on small talk or acknowledgements.

        Ground every specific you state — names, numbers, decisions, tools — in the
        transcript or the background notes provided. NEVER invent or guess specifics.
        If the user is trying to recall a past fact (a decision, a name, a number) and it
        is not in the transcript or the notes, do NOT supply a specific answer — either
        stay silent (advise=false) or suggest they check their notes. Never name a
        specific from your own background knowledge.

        Your advice must be a complete, self-contained statement that stands on its own: never a
        bare number, a single word, or a fragment copied from the transcript. A reader who cannot
        see the transcript must still understand it.

        Respond with ONLY a JSON object:
        {"advise": true|false, "kind": "tip"|"answer"|"warning", "advice": "<=25 words"}
        If nothing is worth saying, return {"advise": false, "kind": "tip", "advice": ""}.
        """;

    // Appended to the base prompt only when at least one named person present has a coaching profile.
    // It adds the communication-coaching job WITHOUT loosening the sparse bar or the JSON contract.
    private static readonly string SystemPromptWithCoaching =
        SystemPrompt +
        "\n\n" +
        """
        You ALSO help "Me" navigate the conversation with the specific people present, using the
        per-person coaching profiles supplied in the user message. Those describe each person's
        communication style, sensitivities, and what approaches work with them. Use them ONLY to
        shape HOW you advise (tone, framing, what to lead with, what to avoid, when to push or hold
        back), never as a source of facts. A profile is not the transcript: do not state or imply
        anything from it as a fact about this meeting.
        Hold the SAME sparse bar as for factual advice: offer a communication tip only at a real
        inflection point (a sensitive topic surfacing, an objection from someone the profile says
        reacts badly to it, a moment where framing clearly changes the outcome), never as running
        commentary on someone's style. A communication tip uses kind "tip" and obeys the same rules
        as all advice: a complete self-contained statement, 25 words or fewer, and the SAME JSON
        object and nothing else.
        """;

    private const int RecallTopK = 3;

    private readonly ICoachChat _chat;
    private readonly string _model;
    private readonly IMemoryStore? _memory;
    private readonly double _recallMaxDistance;
    private readonly CoachingProfileStore? _profiles;
    private readonly string? _selfSpeakerName;

    // One advisor lives per meeting and is touched only from the single CoachEngine drain task (like
    // the engine's _context), so no locking. A person's profile is read from disk at most once; a
    // null value caches a miss so an absent profile never re-hits disk.
    private readonly Dictionary<string, string?> _profileCache = new(StringComparer.OrdinalIgnoreCase);

    public LlmAdvisor(
        ICoachChat chat, string model, IMemoryStore? memory = null, double recallMaxDistance = 0.35,
        CoachingProfileStore? profiles = null, string? selfSpeakerName = null)
    {
        _chat = chat;
        _model = model;
        _memory = memory;
        _recallMaxDistance = recallMaxDistance;
        _profiles = profiles;
        _selfSpeakerName = selfSpeakerName;
    }

    public async Task<AdviceEvent?> ConsiderAsync(
        IReadOnlyList<CaptionEvent> context, CaptionEvent latest,
        IReadOnlyList<string> recentAdvice, CancellationToken ct)
    {
        var recalled = await RecallAsync(latest, ct).ConfigureAwait(false);
        var profiles = ResolveProfiles(context);
        // Only switch on the coaching prompt when a profile is actually in play, so feature-off and
        // no-one-named produce the exact base behaviour (and only two distinct system strings are sent).
        var system = profiles.Count > 0 ? SystemPromptWithCoaching : SystemPrompt;
        var prompt = BuildPrompt(context, recalled, recentAdvice, profiles);
        // A single short JSON advice object; 300 tokens is ample and keeps latency low.
        var raw = await _chat.CompleteAsync(_model, system, prompt, jsonMode: true, maxTokens: 300, ct)
            .ConfigureAwait(false);

        Decision? decision;
        try
        {
            decision = JsonSerializer.Deserialize<Decision>(raw);
        }
        catch (JsonException)
        {
            return null; // Malformed reply: stay silent rather than surface noise.
        }

        if (decision is not { Advise: true } || string.IsNullOrWhiteSpace(decision.Advice))
        {
            return null;
        }

        // Guard against degenerate output: the model sometimes echoes a transcript fragment like
        // "72", which is meaningless on its own. Require a few words with real letters.
        var advice = decision.Advice.Trim();
        if (!IsSelfContained(advice)) return null;

        var kind = decision.Kind?.ToLowerInvariant() switch
        {
            "answer" => AdviceKind.Answer,
            "warning" => AdviceKind.Warning,
            _ => AdviceKind.Tip,
        };
        return new AdviceEvent(DateTime.Now, kind, advice, _model);
    }

    /// <summary>Advice is worth showing only if it reads as a statement on its own: at least a
    /// couple of words and some real letters. Drops degenerate model output like a bare "72".</summary>
    private static bool IsSelfContained(string advice)
    {
        var words = advice.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && advice.Any(char.IsLetter);
    }

    /// <summary>Coaching profiles for the named people present, most-recently-active first, capped to
    /// MaxProfiles. Scans the whole context window (not just the transcript slice) so a currently quiet
    /// person is not dropped. Loads each profile from disk at most once per meeting via the cache.</summary>
    private IReadOnlyList<(string Name, string Profile)> ResolveProfiles(IReadOnlyList<CaptionEvent> context)
    {
        if (_profiles == null) return [];
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = context.Count - 1; i >= 0 && result.Count < MaxProfiles; i--)
        {
            var name = context[i].SpeakerName;
            if (!CoachingProfiles.IsNamedPerson(name, _selfSpeakerName) || !seen.Add(name)) continue;

            if (!_profileCache.TryGetValue(name, out var text))
            {
                try { text = _profiles.Read(name); }
                catch { text = null; } // best-effort, like RecallAsync; never block advice on a read error
                text = string.IsNullOrWhiteSpace(text) ? null : Truncate(text!.Trim(), MaxProfileChars);
                _profileCache[name] = text;
            }
            if (text != null) result.Add((name, text));
        }
        return result;
    }

    /// <summary>Trim to a character budget at a line boundary where possible, marking the cut.</summary>
    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf('\n', Math.Min(max, s.Length - 1));
        return (cut > max / 2 ? s[..cut] : s[..max]).TrimEnd() + "\n…";
    }

    /// <summary>Semantic recall over past-meeting memories, keyed on the latest line (a
    /// focused query recalls a sharp question far better than a diluted multi-line one)
    /// and filtered to genuinely close matches by distance. Best effort: a memory error
    /// must not block advice.</summary>
    private async Task<IReadOnlyList<RecalledMemory>> RecallAsync(CaptionEvent latest, CancellationToken ct)
    {
        if (_memory == null || string.IsNullOrWhiteSpace(latest.Caption)) return [];
        try
        {
            var recalled = await _memory.RecallAsync(latest.Caption, RecallTopK, ct).ConfigureAwait(false);
            return [.. recalled.Where(m => m.Distance <= _recallMaxDistance)];
        }
        catch
        {
            return [];
        }
    }

    private static string BuildPrompt(
        IReadOnlyList<CaptionEvent> context, IReadOnlyList<RecalledMemory> recalled,
        IReadOnlyList<string> recentAdvice, IReadOnlyList<(string Name, string Profile)> profiles)
    {
        var sb = new StringBuilder();
        if (profiles.Count > 0)
        {
            // The grounding rule is restated right next to the names because that is where the model
            // is most tempted to over-trust the text (same belt-and-braces idea as the memory section).
            sb.AppendLine("People in this conversation: coaching profiles for the named participants on the");
            sb.AppendLine("far side. Use these ONLY to shape HOW you advise \"Me\" (tone, framing, what to lead");
            sb.AppendLine("with or avoid, timing). They are NOT the transcript: never state anything from a");
            sb.AppendLine("profile as a fact about this meeting, and never attribute a claim, number, or");
            sb.AppendLine("decision to a person from their profile.");
            foreach (var (name, profile) in profiles)
            {
                sb.AppendLine().Append("## ").AppendLine(name);
                sb.AppendLine(profile);
            }
            sb.AppendLine();
        }

        if (recalled.Count > 0)
        {
            sb.AppendLine("Background from earlier meetings — use ONLY if directly relevant to what is");
            sb.AppendLine("being discussed right now; otherwise ignore it entirely (do not mention it):");
            foreach (var memory in recalled)
            {
                sb.Append("- [").Append(memory.Kind).Append("] ").AppendLine(memory.Text);
            }
            sb.AppendLine();
        }

        if (recentAdvice.Count > 0)
        {
            sb.AppendLine("You have ALREADY given this advice in this meeting. Do NOT repeat or rephrase any");
            sb.AppendLine("of it; only advise if you have something genuinely new to add:");
            foreach (var advice in recentAdvice)
            {
                sb.Append("- ").AppendLine(advice);
            }
            sb.AppendLine();
        }

        var start = Math.Max(0, context.Count - ContextLines);
        sb.AppendLine("Recent transcript:");
        for (var i = start; i < context.Count; i++)
        {
            sb.Append(context[i].SpeakerName).Append(": ").AppendLine(context[i].Caption);
        }
        sb.AppendLine().Append(
            "Based on the LATEST line, should you advise \"Me\" now (something new, not already said "
            + "above)? Reply with the JSON object only.");
        return sb.ToString();
    }

    private sealed class Decision
    {
        [JsonPropertyName("advise")] public bool Advise { get; init; }
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("advice")] public string? Advice { get; init; }
    }
}
