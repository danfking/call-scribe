using CallScribe.Coach.Speaker;
using CallScribe.Transcription;

namespace CallScribe.Coach.Profiles;

/// <summary>Shared rules for the coaching-profile feature. Profiles attach only to named people, so
/// the "is this a named human worth a profile" test lives here once and is used by both the realtime
/// advisor (which injects a present person's profile) and the post-meeting updater (which refines it).</summary>
public static class CoachingProfiles
{
    /// <summary>True when <paramref name="name"/> is a real named participant: not the "Me" or
    /// "Others" channel labels, not my own enrolled name, and not an anonymous "Speaker N" session
    /// label (those have no stable cross-meeting identity, so a profile would be meaningless).</summary>
    public static bool IsNamedPerson(string name, string? selfName)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name == LiveCaptionEngine.MeLabel || name == LiveCaptionEngine.OthersLabel) return false;
        if (!string.IsNullOrEmpty(selfName) && string.Equals(name, selfName, StringComparison.OrdinalIgnoreCase))
            return false;
        return !SpeakerResolver.IsAnonymous(name);
    }
}
