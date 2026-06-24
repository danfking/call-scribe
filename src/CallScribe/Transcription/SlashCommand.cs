using System.Text;

namespace CallScribe.Transcription;

/// <summary>One slash command the live dashboard understands: its canonical name, a short usage
/// hint, whether its first argument is a speaker label (for autocomplete), the handler to run, and
/// any aliases. The dashboard builds the registry (the handlers close over its state); the pure
/// functions here read only the metadata, so they stay unit-testable without a console.</summary>
public sealed record SlashCommandSpec(
    string Name, string Usage, bool FirstArgIsLabel, Action<string[]> Handler, IReadOnlyList<string> Aliases);

/// <summary>Pure parsing, autocomplete, and input highlighting for the dashboard's slash-command
/// line. All command vocabulary comes from the passed-in <see cref="SlashCommandSpec"/> registry, so
/// adding a command is one entry in the registry and never touches this file.</summary>
public static class SlashCommand
{
    /// <summary>Split a command line into the command token and its arguments, honouring double
    /// quotes so multi-word names ("Speaker 1") stay one argument.</summary>
    public static (string Cmd, string[] Args) ParseCommandLine(string line)
    {
        var tokens = Tokenize(line);
        return tokens.Count == 0 ? ("", []) : (tokens[0], [.. tokens.Skip(1)]);
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else
            {
                sb.Append(ch);
            }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    /// <summary>Resolve a typed command token to its spec by canonical name or alias (case-insensitive),
    /// or null when none matches.</summary>
    public static SlashCommandSpec? Match(string cmd, IReadOnlyList<SlashCommandSpec> specs) =>
        specs.FirstOrDefault(s =>
            s.Name.Equals(cmd, StringComparison.OrdinalIgnoreCase) ||
            s.Aliases.Any(a => a.Equals(cmd, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Autocomplete candidates for the current input: matching command names and aliases
    /// while the command word is still being typed, or matching speaker labels for the first argument
    /// of a label-taking command. Empty when nothing applies.</summary>
    public static IReadOnlyList<string> Complete(
        string input, IReadOnlyList<SlashCommandSpec> specs, IEnumerable<string> labels)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/')) return [];

        if (!input.Contains(' '))
        {
            return [.. specs.SelectMany(s => s.Aliases.Prepend(s.Name))
                            .Where(n => n.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                            .Distinct()];
        }

        var (cmd, args) = ParseCommandLine(input);
        if (Match(cmd, specs) is { FirstArgIsLabel: true } && args.Length <= 1)
        {
            var prefix = args.Length == 1 ? args[^1] : "";
            return [.. labels.Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        }
        return [];
    }

    /// <summary>Apply a chosen completion <paramref name="candidate"/> to the current input: replace
    /// the command word (then a space), or set it as a quoted first argument (then a space).</summary>
    public static string ApplyCompletion(string input, string candidate)
    {
        if (!input.Contains(' ')) return candidate + " ";
        var (cmd, _) = ParseCommandLine(input);
        return $"{cmd} {Quote(candidate)} ";
    }

    /// <summary>Spectre markup for the input line with the command word coloured (cyan when it is or
    /// is becoming a known command, red when not, white for plain text) and the arguments in white.
    /// The user's text is markup-escaped.</summary>
    public static string Highlight(string input, IReadOnlyList<SlashCommandSpec> specs)
    {
        if (input.Length == 0) return "";

        var space = input.IndexOf(' ');
        var verb = space < 0 ? input : input[..space];
        var rest = space < 0 ? "" : input[space..];

        string verbColour;
        if (!verb.StartsWith('/')) verbColour = "white";
        else if (space < 0) verbColour = AnyStartsWith(verb, specs) ? "cyan" : "red"; // still typing the verb
        else verbColour = Match(verb, specs) != null ? "cyan" : "red";                // verb is complete

        var markup = $"[{verbColour}]{Escape(verb)}[/]";
        return rest.Length > 0 ? markup + $"[white]{Escape(rest)}[/]" : markup;
    }

    /// <summary>Escape Spectre markup metacharacters so user text renders literally. Kept local so
    /// this class carries no Spectre dependency and stays unit-testable on its own.</summary>
    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>One-line help derived from the registry (label-taking commands show their usage).</summary>
    public static string Help(IReadOnlyList<SlashCommandSpec> specs) =>
        string.Join("  ·  ",
            specs.Select(s => s.FirstArgIsLabel && s.Usage.Length > 0 ? $"{s.Name} {s.Usage}" : s.Name))
        + "  ·  Esc to finish";

    private static bool AnyStartsWith(string prefix, IReadOnlyList<SlashCommandSpec> specs) =>
        specs.Any(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || s.Aliases.Any(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
