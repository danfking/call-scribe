namespace CallScribe;

/// <summary>Well-known directories. Models and state live under LocalAppData;
/// recordings and transcripts under Documents where the user can find them.</summary>
public static class AppPaths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "call-scribe");

    public static string StateDir => Path.Combine(DataDir, "state");
    public static string ModelsDir => Path.Combine(DataDir, "models");

    // Deliberately NOT the Documents folder: Documents is commonly redirected to
    // OneDrive (including corporate tenants), and call recordings must never land
    // in a synced folder by default.
    public static string OutputRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "call-scribe");

    public static string RecordingsDir => Path.Combine(OutputRoot, "recordings");
    public static string TranscriptsDir => Path.Combine(OutputRoot, "transcripts");

    public static string PidFile => Path.Combine(StateDir, "recording.pid");
    public static string StemFile => Path.Combine(StateDir, "recording.stem");
    public static string StopFlag => Path.Combine(StateDir, "stop.flag");
}
