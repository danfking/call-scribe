using System.Text;
using CallScribe;
using Npgsql;
using TranscriptReconcile;

// Reconcile a meeting's transcripts to benchmark live (coach DB) and final (.md) against a Teams
// VTT (reference). Live is pulled straight from the coach hypertable (no file needed).
//
//   dotnet run --project tools/TranscriptReconcile -- --meeting 2026-06-23-0931 [--teams call.vtt]
//
// --md <path>   final transcript (default: transcripts/<meeting>.md)
// --teams <vtt> Teams reference (optional; without it, only live-vs-final runs)
// --out <path>  report path (default: transcripts/<meeting>.reconcile.md)

string? meeting = null, teamsPath = null, mdPath = null, outPath = null, liveFile = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--meeting": meeting = Arg(ref i); break;
        case "--teams": teamsPath = Arg(ref i); break;
        case "--md": mdPath = Arg(ref i); break;
        case "--out": outPath = Arg(ref i); break;
        case "--live-file": liveFile = Arg(ref i); break;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); return 2;
    }
}
string Arg(ref int i) => ++i < args.Length ? args[i] : throw new ArgumentException("missing value for option");

if (meeting is null)
{
    Console.Error.WriteLine("Usage: TranscriptReconcile --meeting <stem> [--teams <vtt>] [--md <path>] [--out <path>]");
    return 2;
}

var config = AppConfig.Load();
if (config.OutputRoot != null) AppPaths.OutputRootOverride = config.OutputRoot;
mdPath ??= Path.Combine(AppPaths.TranscriptsDir, meeting + ".md");
outPath ??= Path.Combine(AppPaths.TranscriptsDir, meeting + ".reconcile.md");

var live = liveFile is not null ? LoadLiveFile(liveFile) : await LoadLiveAsync(config.PostgresConn, meeting);
Console.WriteLine($"live: {live.Count} lines  [{liveFile ?? "coach DB"}]");

var final = File.Exists(mdPath) ? CallScribeMd.Parse(File.ReadAllText(mdPath)) : [];
Console.WriteLine($"final (.md): {final.Count} lines  [{mdPath}]");

IReadOnlyList<Utterance> teams = [];
if (teamsPath is not null)
{
    if (!File.Exists(teamsPath)) { Console.Error.WriteLine($"Teams VTT not found: {teamsPath}"); return 2; }
    teams = VttParser.Parse(File.ReadAllText(teamsPath));
    Console.WriteLine($"teams (VTT): {teams.Count} lines  [{teamsPath}]");
}

var results = new List<ReconResult>();
if (teams.Count > 0)
{
    results.Add(Metrics.Compute("Live vs Teams (reference = Teams)", teams, live));
    results.Add(Metrics.Compute("Final vs Teams (reference = Teams)", teams, final));
}
results.Add(Metrics.Compute("Live vs Final (reference = Final)", final, live));

var sb = new StringBuilder();
sb.AppendLine($"# Transcript reconciliation: {meeting}").AppendLine();
sb.AppendLine($"Sources: live {live.Count} lines, final {final.Count} lines"
    + (teams.Count > 0 ? $", teams {teams.Count} lines" : ", teams (not provided)")).AppendLine();
foreach (var r in results) sb.AppendLine(Report.Markdown(r)).AppendLine();
File.WriteAllText(outPath, sb.ToString());

Console.WriteLine($"\nReport -> {outPath}\n");
foreach (var r in results)
{
    Console.WriteLine($"  {r.Label}");
    Console.WriteLine($"    WER {r.Wer:P1}  CER {r.Cer:P1}  word recall {r.WordRecall:P1}  precision {r.WordPrecision:P1}  "
        + $"speakers {r.Speakers.DistinctHypothesisLabels}->{r.Speakers.DistinctReferenceSpeakers}  "
        + $"attribution {(r.Speakers.CorrectlyAttributed + r.Speakers.Misattributed == 0 ? "n/a" : $"{(double)r.Speakers.CorrectlyAttributed / (r.Speakers.CorrectlyAttributed + r.Speakers.Misattributed):P0}")}");
}
return 0;

static async Task<IReadOnlyList<Utterance>> LoadLiveAsync(string connectionString, string meeting)
{
    var rows = new List<(DateTime At, string Speaker, string Text)>();
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT at, speaker, text FROM transcript_segments WHERE meeting_id = @m ORDER BY at", conn);
    cmd.Parameters.AddWithValue("m", meeting);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add((reader.GetDateTime(0), reader.GetString(1), reader.GetString(2)));
    }
    if (rows.Count == 0) return [];
    var t0 = rows[0].At;
    return [.. rows.Select(r => new Utterance((r.At - t0).TotalSeconds, null, r.Speaker, r.Text))];
}

// Load a replayed live transcript (LiveReplay JSON: [{sec, speaker, text}]).
static IReadOnlyList<Utterance> LoadLiveFile(string path)
{
    var rows = System.Text.Json.JsonSerializer.Deserialize<List<ReplayLineDto>>(File.ReadAllText(path)) ?? [];
    return [.. rows.Select(r => new Utterance(r.sec, null, r.speaker ?? "", r.text ?? ""))];
}

internal sealed record ReplayLineDto(double sec, string? speaker, string? text);
