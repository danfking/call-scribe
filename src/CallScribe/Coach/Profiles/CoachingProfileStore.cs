using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CallScribe.Coach.Profiles;

/// <summary>Per-person coaching profiles, one markdown file per named person under a directory. A
/// profile is a small relationship document (how to communicate with that person) that I load whole
/// when the person is in a call, so plain files beat the embedded memory store here: they are
/// hand-editable, portable, and need no semantic search. The display name lives inside the file as an
/// H1; lookups are by a slug of the name.</summary>
public sealed partial class CoachingProfileStore
{
    private readonly string _dir;

    public CoachingProfileStore(string dir) => _dir = dir;

    /// <summary>Filesystem-safe slug of a person name: lowercase ASCII words joined by '-'. Diacritics
    /// are folded (Jose, not Jos), and a name that slugs to nothing (e.g. all non-Latin) falls back to
    /// a stable hash so a path always exists.</summary>
    public static string Slug(string personName)
    {
        personName ??= "";
        var decomposed = personName.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(char.ToLowerInvariant(ch));
        }

        var slug = NonAlphanumeric().Replace(folded.ToString(), "-").Trim('-');
        return slug.Length > 0 ? slug : Hash(personName);
    }

    public string PathFor(string personName) => Path.Combine(_dir, Slug(personName) + ".md");

    public bool Exists(string personName) => File.Exists(PathFor(personName));

    /// <summary>The person's profile markdown, or null if they have none yet.</summary>
    public string? Read(string personName)
    {
        var path = PathFor(personName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Write(string personName, string markdown)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(personName), markdown);
    }

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "person-" + Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
