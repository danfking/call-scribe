using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;
using CallScribe.Transcription;

namespace CallScribe.Coach;

/// <summary>Reflect + Act in a single fast-model call: given the recent transcript, the
/// model decides whether a brief, genuinely useful piece of advice would help the user
/// ("Me") and, if so, writes it. The model gates itself (advise sparingly) and returns
/// strict JSON so parsing is robust; a non-advise or unparseable reply yields no advice.</summary>
public sealed class LlmAdvisor : IAdvisor
{
    private const int ContextLines = 8;

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

        Respond with ONLY a JSON object:
        {"advise": true|false, "kind": "tip"|"answer"|"warning", "advice": "<=25 words"}
        If nothing is worth saying, return {"advise": false, "kind": "tip", "advice": ""}.
        """;

    private const int RecallTopK = 3;

    private readonly ICoachChat _chat;
    private readonly string _model;
    private readonly IMemoryStore? _memory;
    private readonly double _recallMaxDistance;

    public LlmAdvisor(ICoachChat chat, string model, IMemoryStore? memory = null, double recallMaxDistance = 0.35)
    {
        _chat = chat;
        _model = model;
        _memory = memory;
        _recallMaxDistance = recallMaxDistance;
    }

    public async Task<AdviceEvent?> ConsiderAsync(
        IReadOnlyList<CaptionEvent> context, CaptionEvent latest, CancellationToken ct)
    {
        var recalled = await RecallAsync(latest, ct).ConfigureAwait(false);
        var prompt = BuildPrompt(context, recalled);
        var raw = await _chat.CompleteAsync(_model, SystemPrompt, prompt, jsonMode: true, ct).ConfigureAwait(false);

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

        var kind = decision.Kind?.ToLowerInvariant() switch
        {
            "answer" => AdviceKind.Answer,
            "warning" => AdviceKind.Warning,
            _ => AdviceKind.Tip,
        };
        return new AdviceEvent(DateTime.Now, kind, decision.Advice.Trim(), _model);
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

    private static string BuildPrompt(IReadOnlyList<CaptionEvent> context, IReadOnlyList<RecalledMemory> recalled)
    {
        var sb = new StringBuilder();
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

        var start = Math.Max(0, context.Count - ContextLines);
        sb.AppendLine("Recent transcript:");
        for (var i = start; i < context.Count; i++)
        {
            sb.Append(context[i].SpeakerName).Append(": ").AppendLine(context[i].Caption);
        }
        sb.AppendLine().Append("Should you advise \"Me\" now? Reply with the JSON object only.");
        return sb.ToString();
    }

    private sealed class Decision
    {
        [JsonPropertyName("advise")] public bool Advise { get; init; }
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("advice")] public string? Advice { get; init; }
    }
}
