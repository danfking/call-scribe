namespace CallScribe;

/// <summary>Well-known directories. Models and state live under LocalAppData; recordings and
/// transcripts under %USERPROFILE%\call-scribe (NOT Documents, which is commonly synced) unless
/// the user overrides the output root.</summary>
public static class AppPaths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "call-scribe");

    public static string StateDir => Path.Combine(DataDir, "state");
    public static string ModelsDir => Path.Combine(DataDir, "models");

    /// <summary>Set from config at startup when the user overrides the output location.</summary>
    public static string? OutputRootOverride { get; set; }

    // Deliberately NOT the Documents folder: Documents is commonly redirected to
    // OneDrive (including corporate tenants), and call recordings must never land
    // in a synced folder by default.
    public static string OutputRoot => OutputRootOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "call-scribe");

    public static string RecordingsDir => Path.Combine(OutputRoot, "recordings");
    public static string TranscriptsDir => Path.Combine(OutputRoot, "transcripts");

    public static string PidFile => Path.Combine(StateDir, "recording.pid");
    public static string StemFile => Path.Combine(StateDir, "recording.stem");
    public static string StopFlag => Path.Combine(StateDir, "stop.flag");
}
