using Whisper.net.LibraryLoader;

namespace CallScribe.Transcription;

/// <summary>In single-file publish, the bundled native whisper libraries extract to
/// %TEMP%\.net\&lt;exe-name&gt;\&lt;bundle-id&gt;\runtimes\... but Whisper.net's loader only
/// probes next to the assemblies, which have no on-disk location in a bundle. This
/// resolver finds the extraction directory and points RuntimeOptions.LibraryPath at it.
/// No-op in a normal (non-bundled) build.</summary>
public static class WhisperNativeResolver
{
    public static void Apply()
    {
        // Non-bundled builds have a real assembly location and need no help.
        // IL3000 is exactly the behaviour we rely on: empty string = single-file bundle.
#pragma warning disable IL3000
        if (!string.IsNullOrEmpty(typeof(WhisperNativeResolver).Assembly.Location)) return;
#pragma warning restore IL3000

        // Already discoverable next to the exe (e.g. publish without single-file)?
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "runtimes"))) return;

        var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "call-scribe";
        var extractBase = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")
            ?? Path.Combine(Path.GetTempPath(), ".net", exeName);
        if (!Directory.Exists(extractBase)) return;

        // One subdirectory per bundle version; ours is the one extracted most recently.
        var bundleDir = new DirectoryInfo(extractBase)
            .EnumerateDirectories()
            .Where(d => Directory.Exists(Path.Combine(d.FullName, "runtimes")))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .FirstOrDefault();
        if (bundleDir is null) return;

        // The loader takes the directory name of LibraryPath as the search root
        // and appends "runtimes" itself.
        RuntimeOptions.LibraryPath = Path.Combine(bundleDir.FullName, "runtimes");
    }
}
