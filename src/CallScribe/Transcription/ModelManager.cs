using Spectre.Console;
using Whisper.net.Ggml;

namespace CallScribe.Transcription;

/// <summary>Downloads and caches ggml models under %LOCALAPPDATA%\call-scribe\models.
/// Models are never bundled with the exe; the default whisper model is ~874 MB.</summary>
public static class ModelManager
{
    public const GgmlType DefaultModel = GgmlType.LargeV3Turbo;
    public const QuantizationType DefaultQuantization = QuantizationType.Q8_0;

    public static async Task<string> EnsureWhisperModelAsync(
        GgmlType type = DefaultModel,
        QuantizationType quantization = DefaultQuantization,
        CancellationToken ct = default)
    {
        var fileName = quantization == QuantizationType.NoQuantization
            ? $"ggml-{ModelName(type)}.bin"
            : $"ggml-{ModelName(type)}-{quantization.ToString().ToLowerInvariant()}.bin";
        var path = Path.Combine(AppPaths.ModelsDir, fileName);
        if (File.Exists(path)) return path;

        await DownloadAsync(
            path,
            $"whisper {ModelName(type)} ({quantization})",
            () => WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, quantization, ct)).ConfigureAwait(false);
        return path;
    }

    public static async Task<string> EnsureVadModelAsync(CancellationToken ct = default)
    {
        var path = Path.Combine(AppPaths.ModelsDir, "ggml-silero-vad.bin");
        if (File.Exists(path)) return path;

        await DownloadAsync(
            path,
            "Silero VAD",
            () => WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(cancellationToken: ct)).ConfigureAwait(false);
        return path;
    }

    private static async Task DownloadAsync(string path, string description, Func<Task<Stream>> open)
    {
        Directory.CreateDirectory(AppPaths.ModelsDir);
        var tempPath = path + ".partial";

        await AnsiConsole.Status().StartAsync($"Downloading {description} model...", async ctx =>
        {
            await using var source = await open().ConfigureAwait(false);
            await using var target = File.Create(tempPath);

            var buffer = new byte[1 << 20];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                total += read;
                ctx.Status($"Downloading {description} model... {total / 1024.0 / 1024.0:F0} MB");
            }
        }).ConfigureAwait(false);

        File.Move(tempPath, path, overwrite: true);
        AnsiConsole.MarkupLine($"[green]Model ready:[/] {path.EscapeMarkup()}");
    }

    private static string ModelName(GgmlType type) => type switch
    {
        GgmlType.LargeV3Turbo => "large-v3-turbo",
        GgmlType.LargeV3 => "large-v3",
        GgmlType.LargeV2 => "large-v2",
        GgmlType.Medium => "medium",
        GgmlType.MediumEn => "medium.en",
        GgmlType.Small => "small",
        GgmlType.SmallEn => "small.en",
        GgmlType.Base => "base",
        GgmlType.BaseEn => "base.en",
        GgmlType.Tiny => "tiny",
        GgmlType.TinyEn => "tiny.en",
        _ => type.ToString().ToLowerInvariant(),
    };

    public static GgmlType ParseModel(string name) => name.ToLowerInvariant() switch
    {
        "large-v3-turbo" or "turbo" => GgmlType.LargeV3Turbo,
        "large-v3" or "large" => GgmlType.LargeV3,
        "medium" => GgmlType.Medium,
        "medium.en" => GgmlType.MediumEn,
        "small" => GgmlType.Small,
        "small.en" => GgmlType.SmallEn,
        "base" => GgmlType.Base,
        "base.en" => GgmlType.BaseEn,
        "tiny" => GgmlType.Tiny,
        "tiny.en" => GgmlType.TinyEn,
        _ => throw new ArgumentException(
            $"Unknown model '{name}'. Use large-v3-turbo, large-v3, medium[.en], small[.en], base[.en] or tiny[.en]."),
    };
}
