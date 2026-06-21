using System.Text;

namespace CallScribe.Transcription;

/// <summary>Pure parsing + autocomplete for the live dashboard's slash-command input line.
/// Kept separate from <see cref="LiveStatusDisplay"/> so it's unit-testable without a console.</summary>
public static class SlashCommand
{
    /// <summary>The commands the input line understands (also the autocomplete vocabulary).</summary>
    public static readonly IReadOnlyList<string> Commands =
        ["/assign-name", "/rename", "/speakers", "/help", "/stop"];

    public const string HelpText =
        "/assign-name \"Speaker 1\" \"Sammy\"  ·  /rename  ·  /speakers  ·  /help  ·  /stop (or Esc)";

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

    /// <summary>Autocomplete candidates for the current input: matching command names while the
    /// command word is still being typed, or matching speaker labels for the first argument of
    /// assign-name/rename. Empty when nothing applies.</summary>
    public static IReadOnlyList<string> Complete(string input, IEnumerable<string> labels)
    {
        if (string.IsNullOrEmpty(input) || !input.StartsWith('/')) return [];

        if (!input.Contains(' '))
        {
            return [.. Commands.Where(c => c.StartsWith(input, StringComparison.OrdinalIgnoreCase))];
        }

        var (cmd, args) = ParseCommandLine(input);
        if (TakesLabelArg(cmd) && args.Length <= 1)
        {
            var prefix = args.Length == 1 ? args[^1] : "";
            return [.. labels.Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
        }
        return [];
    }

    /// <summary>Apply a Tab completion: if exactly one candidate matches, extend the input to it
    /// (command word, or a quoted label as the first argument); otherwise leave it unchanged.</summary>
    public static string ApplyTab(string input, IEnumerable<string> labels)
    {
        var candidates = Complete(input, labels);
        if (candidates.Count != 1) return input;

        if (!input.Contains(' ')) return candidates[0] + " ";

        var (cmd, _) = ParseCommandLine(input);
        return $"{cmd} {Quote(candidates[0])} ";
    }

    private static bool TakesLabelArg(string cmd) => cmd is "/assign-name" or "/rename";

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
