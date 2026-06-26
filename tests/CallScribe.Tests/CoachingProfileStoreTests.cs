using CallScribe.Coach.Profiles;

namespace CallScribe.Tests;

public class CoachingProfileStoreTests
{
    [Theory]
    [InlineData("Bob Smith", "bob-smith")]
    [InlineData("GAVIN", "gavin")]
    [InlineData("  Anna-Maria  ", "anna-maria")]
    [InlineData("O'Brien, Pat", "o-brien-pat")]
    [InlineData("José", "jose")]
    [InlineData("Renée Fleming", "renee-fleming")]
    public void Slug_NormalisesNames(string name, string expected)
    {
        Assert.Equal(expected, CoachingProfileStore.Slug(name));
    }

    [Fact]
    public void Slug_NonLatinName_FallsBackToStableHash()
    {
        var first = CoachingProfileStore.Slug("Москва");
        var second = CoachingProfileStore.Slug("Москва");

        Assert.StartsWith("person-", first);
        Assert.Equal(first, second); // stable across calls
        Assert.NotEqual(first, CoachingProfileStore.Slug("北京"));
    }

    [Fact]
    public void ReadWriteExists_RoundTrips()
    {
        var dir = NewTempDir();
        try
        {
            var store = new CoachingProfileStore(dir);

            Assert.False(store.Exists("Bob Smith"));
            Assert.Null(store.Read("Bob Smith"));

            store.Write("Bob Smith", "# Bob Smith\nPrefers directness.\n");

            Assert.True(store.Exists("Bob Smith"));
            Assert.Equal("# Bob Smith\nPrefers directness.\n", store.Read("Bob Smith"));
            Assert.EndsWith("bob-smith.md", store.PathFor("Bob Smith"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_CreatesTheDirectory()
    {
        var dir = Path.Combine(NewTempDir(), "nested", "coaching");
        try
        {
            new CoachingProfileStore(dir).Write("Gavin", "# Gavin\n");
            Assert.True(File.Exists(Path.Combine(dir, "gavin.md")));
        }
        finally
        {
            Directory.Delete(Directory.GetParent(Directory.GetParent(dir)!.FullName)!.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("Gavin", null, true)]
    [InlineData("Me", null, false)]
    [InlineData("Others", null, false)]
    [InlineData("Speaker 1", null, false)]
    [InlineData("Speaker 12", null, false)]
    [InlineData("speaker 3", null, false)] // case-insensitive
    [InlineData("Dan", "Dan", false)]      // self excluded
    [InlineData("dan", "Dan", false)]      // self case-insensitive
    [InlineData("", null, false)]
    [InlineData("Speaker Gavin", null, true)] // not the anonymous pattern
    public void IsNamedPerson_AppliesSkipSet(string name, string? self, bool expected)
    {
        Assert.Equal(expected, CoachingProfiles.IsNamedPerson(name, self));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "callscribe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
