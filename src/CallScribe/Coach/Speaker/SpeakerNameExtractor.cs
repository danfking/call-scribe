using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CallScribe.Coach.Llm;

namespace CallScribe.Coach.Speaker;

/// <summary>Finds far-side speakers' own names from how they introduce themselves in the
/// transcript ("I'm Sammy", "this is Priya", "Sammy here"), so the after-meeting naming
/// prompt can be pre-filled. Regex first (offline, deterministic); an optional gated LLM
/// pass fills speakers the regex missed. Both map a current speaker label (e.g. "Speaker 1")
/// to a suggested name — they never rename anything themselves.</summary>
public static partial class SpeakerNameExtractor
{
    // Words that commonly follow "I'm"/"this is"/"it's" but are not names. Compared
    // lowercase; the initial-capital requirement in the patterns already filters most noise.
    private static readonly HashSet<string> Stoplist = new(StringComparer.OrdinalIgnoreCase)
    {
        "done", "good", "fine", "sure", "here", "back", "ready", "okay", "ok", "going", "gonna",
        "not", "just", "sorry", "glad", "happy", "great", "right", "afraid", "trying", "looking",
        "the", "a", "an", "so", "well", "still", "really", "very", "about", "kind", "sort",
    };

    /// <summary>First confident self-introduced name per speaker, from a small set of phrasings.
    /// Keys are the speaker labels as they appear in <paramref name="lines"/>.</summary>
    public static IReadOnlyDictionary<string, string> DetectRegex(IEnumerable<(string Speaker, string Text)> lines)
    {
        var found = new Dictionary<string, string>();
        foreach (var (speaker, text) in lines)
        {
            if (found.ContainsKey(speaker) || string.IsNullOrWhiteSpace(text)) continue;
            if (TryExtractName(text, out var name)) found[speaker] = name;
        }
        return found;
    }

    private static bool TryExtractName(string text, out string name)
    {
        foreach (Match m in IntroPattern().Matches(text))
        {
            var candidate = (m.Groups["name"].Success ? m.Groups["name"] : m.Groups["name2"]).Value;
            if (candidate.Length >= 2 && !Stoplist.Contains(candidate))
            {
                name = candidate;
                return true;
            }
        }
        name = "";
        return false;
    }

    // "my name is X" / "I'm X" / "I am X" / "this is X" / "it's X"  (name = initial-capital word),
    // or "X here" (name2). The lead-in words are matched case-insensitively (sentence-start
    // capitalisation) via explicit [Aa] classes, but the name group itself stays case-sensitive:
    // requiring an initial capital is the main filter (Whisper capitalises proper nouns, so
    // "I'm done"/"this is great" don't match while "I'm Sammy" does).
    [GeneratedRegex(@"(?:\b[Mm]y name(?:'s| is)\s+|\b[Ii](?:'m| am)\s+|\b[Tt]his is\s+|\b[Ii]t's\s+)(?<name>[A-Z][a-zA-Z]+)\b|\b(?<name2>[A-Z][a-zA-Z]+)\s+[Hh]ere\b")]
    private static partial Regex IntroPattern();

    /// <summary>Ask the model for speaker→name mappings for any speaker the regex missed.
    /// Best-effort: a missing/unreachable model or malformed reply yields no suggestions.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ExtractWithLlmAsync(
        ICoachChat chat, string model, IReadOnlyList<(string Speaker, string Text)> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return new Dictionary<string, string>();

        var transcript = new StringBuilder("Transcript:\n");
        foreach (var (speaker, text) in lines)
        {
            transcript.Append(speaker).Append(": ").AppendLine(text);
        }

        string raw;
        try
        {
            raw = await chat.CompleteAsync(model, SystemPrompt, transcript.ToString(), jsonMode: true, maxTokens: 400, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
        return ParseNameMap(raw);
    }

    private static readonly string SystemPrompt =
        """
        You map speakers to their OWN stated names from a meeting transcript. Each line is
        "Speaker: text". Only include a speaker who clearly states their own name — e.g.
        "I'm Sammy", "this is Priya", "my name is Bob", "Sammy here". Do NOT guess from
        context, and do NOT include speakers who never say their own name.

        Respond with ONLY a JSON object:
        {"names": [{"speaker": "<label as written>", "name": "<first name>"}]}
        Return {"names": []} if no one introduces themselves.
        """;

    /// <summary>Parse the model's <c>{"names":[{speaker,name}]}</c> reply into a map; tolerant
    /// of malformed output (returns empty).</summary>
    public static IReadOnlyDictionary<string, string> ParseNameMap(string json)
    {
        var map = new Dictionary<string, string>();
        Extraction? extraction;
        try
        {
            extraction = JsonSerializer.Deserialize<Extraction>(json);
        }
        catch (JsonException)
        {
            return map;
        }

        foreach (var item in extraction?.Names ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Speaker) || string.IsNullOrWhiteSpace(item.Name)) continue;
            map[item.Speaker.Trim()] = item.Name.Trim();
        }
        return map;
    }

    private sealed class Extraction
    {
        [JsonPropertyName("names")] public List<Item>? Names { get; init; }
    }

    private sealed class Item
    {
        [JsonPropertyName("speaker")] public string? Speaker { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }
}
