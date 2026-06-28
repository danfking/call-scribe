using System.Text.Json;
using System.Text.Json.Serialization;
using CallScribe.Coach.Llm;
using CallScribe.Coach.Memory;

namespace CallScribe.Coach;

/// <summary>Runs once a meeting ends: reads the persisted transcript, asks the reasoning
/// model to extract durable, standalone facts (decisions, action items, person facts,
/// preferences, insights), and stores each as an embedded memory for future recall.
/// This is what closes the loop — a meeting's takeaways become recallable next time.</summary>
public sealed class MeetingConsolidator
{
    private static readonly string SystemPrompt =
        """
        You are consolidating a finished meeting transcript into durable memories for
        future meetings. Extract ONLY reusable, standalone facts — decisions made, action
        items, facts about people, stated preferences, and key insights. Ignore small talk,
        filler, and anything only meaningful in the moment. Each memory must make sense on
        its own without the transcript.

        Each transcript line is prefixed with the speaker's name. When a fact is about, or a
        preference belongs to, a specific named person, set "person" to that name; otherwise
        omit it. Do not guess a person for general decisions or insights.

        Respond with ONLY a JSON object:
        {"items": [{"kind": "insight"|"decision"|"action_item"|"person_fact"|"preference", "text": "...", "person": "<name or omit>"}]}
        Return {"items": []} if there is nothing durable worth keeping.
        """;

    private readonly ICoachChat _chat;
    private readonly string _model;
    private readonly IMemoryStore _store;

    public MeetingConsolidator(ICoachChat chat, string model, IMemoryStore store)
    {
        _chat = chat;
        _model = model;
        _store = store;
    }

    /// <summary>Consolidate the meeting; returns the number of memories stored.</summary>
    public async Task<int> ConsolidateAsync(string meetingId, CancellationToken ct)
    {
        var segments = await _store.GetTranscriptAsync(meetingId, ct).ConfigureAwait(false);
        if (segments.Count == 0) return 0;

        var transcript = TranscriptText.ForPrompt(segments.Select(s => (s.Speaker, s.Text)));

        // A whole meeting can extract many durable items; give the JSON array room so it is
        // not truncated mid-object (which would fail to parse and silently drop everything).
        var raw = await _chat.CompleteAsync(_model, SystemPrompt, transcript, jsonMode: true, maxTokens: 2048, ct)
            .ConfigureAwait(false);

        Extraction? extraction;
        try
        {
            extraction = JsonSerializer.Deserialize<Extraction>(raw);
        }
        catch (JsonException)
        {
            return 0;
        }

        var items = extraction?.Items ?? [];
        var stored = 0;
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Text)) continue;
            var person = string.IsNullOrWhiteSpace(item.Person) ? null : item.Person.Trim();
            await _store.StoreMemoryAsync(meetingId, MapKind(item.Kind), item.Text.Trim(), person, ct)
                .ConfigureAwait(false);
            stored++;
        }
        return stored;
    }

    private static MemoryKind MapKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "decision" => MemoryKind.Decision,
        "action_item" => MemoryKind.ActionItem,
        "person_fact" => MemoryKind.PersonFact,
        "preference" => MemoryKind.Preference,
        _ => MemoryKind.Insight,
    };

    private sealed class Extraction
    {
        [JsonPropertyName("items")] public List<Item>? Items { get; init; }
    }

    private sealed class Item
    {
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("person")] public string? Person { get; init; }
    }
}
