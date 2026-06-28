using CallScribe.Coach.Llm;

namespace CallScribe.Coach.Profiles;

/// <summary>Given a finished meeting's transcript, refines each named far-side person's markdown
/// coaching profile. This is what makes the profiles evolve over time. It takes the transcript as
/// lines (the caller supplies them, from the offline-attributed transcript on the live path or from
/// the coach DB on the replay path), so it does not depend on any store but the profile files. Best
/// effort per person: one bad reply never loses the rest.</summary>
public sealed class CoachingProfileUpdater
{
    private static readonly string SystemPrompt =
        """
        You maintain a private, reusable coaching profile that helps "Me" communicate effectively with
        ONE specific person across meetings. You are given that person's name, their existing profile
        (if any), and the transcript of a meeting they were in (each line prefixed with the speaker's
        name; "Me" is the user you are coaching).

        Produce the COMPLETE updated profile as markdown and nothing else: no preamble, no commentary,
        no code fences. Use this structure, omitting a section when you have nothing for it:

        # <name>
        ## Communication style
        ## What works
        ## Friction points
        ## Context

        Rules:
        - Start from the existing profile. Fold in only what this meeting genuinely adds about how the
          person communicates and how to work with them. Deduplicate; drop anything now contradicted.
        - Preserve the user's own hand-written notes; do not delete them.
        - Be concise: short bullet points, the whole profile under about 250 words. Refine rather than
          pile up; this output REPLACES the file, it does not append to it.
        - Capture communication patterns (tone, what persuades or annoys them, how they handle
          disagreement, their preferences), NOT meeting minutes, decisions, or action items.
        - If this meeting adds nothing new, return the existing profile unchanged. If there is no
          existing profile and almost no signal, return a minimal stub with just the heading.
        - Do not use em-dashes.
        """;

    private readonly ICoachChat _chat;
    private readonly string _model;
    private readonly CoachingProfileStore _profiles;
    private readonly string? _selfName;

    public CoachingProfileUpdater(ICoachChat chat, string model, CoachingProfileStore profiles, string? selfName)
    {
        _chat = chat;
        _model = model;
        _profiles = profiles;
        _selfName = selfName;
    }

    /// <summary>Refresh every named far-side person's profile from this meeting's transcript lines;
    /// returns how many profiles were written.</summary>
    public async Task<int> UpdateAsync(IReadOnlyList<(string Speaker, string Text)> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return 0;

        var transcript = TranscriptText.ForPrompt(lines);
        var targets = lines
            .Select(l => l.Speaker)
            .Where(n => CoachingProfiles.IsNamedPerson(n, _selfName))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var updated = 0;
        foreach (var person in targets)
        {
            try
            {
                var existing = _profiles.Read(person);
                var user = BuildUserPrompt(person, _selfName, existing, transcript);
                var raw = await _chat.CompleteAsync(_model, SystemPrompt, user, jsonMode: false, maxTokens: 2048, ct)
                    .ConfigureAwait(false);
                var markdown = CleanMarkdown(raw);
                // Only overwrite with something that actually looks like a profile (starts with the
                // markdown heading the prompt asks for). A refusal or stray prose must never clobber a
                // good, possibly hand-edited, existing profile.
                if (markdown.StartsWith('#'))
                {
                    _profiles.Write(person, markdown);
                    updated++;
                }
            }
            catch
            {
                // Per-person isolation: one person's bad reply or write must not abort the others.
            }
        }
        return updated;
    }

    private static string BuildUserPrompt(string person, string? selfName, string? existing, string transcript)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Person to profile: ").AppendLine(person);
        sb.Append("The user I am coaching is labelled: ")
          .AppendLine(string.IsNullOrWhiteSpace(selfName) ? "Me" : selfName);
        sb.AppendLine();
        sb.AppendLine("Existing profile:");
        sb.AppendLine(string.IsNullOrWhiteSpace(existing) ? "(none yet)" : existing);
        sb.AppendLine();
        sb.Append(transcript);
        return sb.ToString();
    }

    /// <summary>Strip a wrapping markdown code fence the model sometimes adds despite being told not to.</summary>
    private static string CleanMarkdown(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        }
        return text.Trim();
    }
}
