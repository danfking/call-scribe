using TranscriptReconcile;

namespace CallScribe.Tests;

public class WordErrorTests
{
    private static string[] Tok(string s) => WordError.Tokenize(s);

    [Fact]
    public void Distance_ClassifiesSubsDeletionsInsertions()
    {
        var reference = Tok("the quick brown fox");
        Assert.Equal(1, WordError.Distance(reference, Tok("the quick brown dog")).Substitutions);
        Assert.Equal(1, WordError.Distance(reference, Tok("the quick brown")).Deletions);
        Assert.Equal(1, WordError.Distance(reference, Tok("the quick brown fox jumps")).Insertions);
    }

    [Fact]
    public void Rate_IsEditsOverReferenceLength()
    {
        var reference = Tok("the quick brown fox");      // 4 words
        Assert.Equal(0.25, WordError.Distance(reference, Tok("the quick brown dog")).Rate, 3);
        Assert.Equal(0.0, WordError.Distance(reference, reference).Rate, 3);
    }

    [Fact]
    public void Similarity_IsOneForIdentical_ZeroForDisjoint()
    {
        Assert.Equal(1.0, WordError.Similarity(Tok("a b c"), Tok("a b c")), 3);
        Assert.Equal(0.0, WordError.Similarity(Tok("a b c"), Tok("x y z")), 3);
    }

    [Fact]
    public void Tokenize_LowercasesAndStripsPunctuation()
    {
        Assert.Equal(["dont", "stop", "24"], WordError.Tokenize("Don't stop! (24)"));
    }
}

public class VttParserTests
{
    [Fact]
    public void Parse_ReadsSpeakerTimeAndText()
    {
        const string vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\n<v Kiel>I'm thinking of doing one thing.</v>\n\n"
            + "00:01:05.500 --> 00:01:07.000\n<v Deon>Yeah, okay.</v>\n";
        var u = VttParser.Parse(vtt);

        Assert.Equal(2, u.Count);
        Assert.Equal("Kiel", u[0].Speaker);
        Assert.Equal(1.0, u[0].StartSec, 3);
        Assert.Contains("thinking", u[0].Text);
        Assert.Equal("Deon", u[1].Speaker);
        Assert.Equal(65.5, u[1].StartSec, 3);
    }
}

public class CallScribeMdTests
{
    [Fact]
    public void Parse_GroupsSpeakerBlocksWithRelativeTimes()
    {
        const string md = "---\nstarted: 2026-06-23 09:31\nduration: 11:47\n---\n\n# Call transcript: x\n\n"
            + "**Kiel** [09:31:02]\nI'm thinking of doing one thing.\nAnd it's just a script.\n\n"
            + "**Deon** [09:32:05]\nOh, the unmute button.\n";
        var u = CallScribeMd.Parse(md);

        Assert.Equal(3, u.Count);
        Assert.Equal("Kiel", u[0].Speaker);
        Assert.Equal(2.0, u[0].StartSec, 1);   // 09:31:02 - 09:31:00
        Assert.Equal("Kiel", u[1].Speaker);    // interior line keeps speaker + block time
        Assert.Equal("Deon", u[2].Speaker);
        Assert.Equal(65.0, u[2].StartSec, 1);  // 09:32:05 - 09:31:00
    }
}

public class AlignerTests
{
    private static Utterance U(double t, string sp, string text) => new(t, null, sp, text);

    [Fact]
    public void EstimateOffset_FindsConstantShift()
    {
        var reference = new[] { U(0, "A", "the quick brown fox runs"), U(5, "A", "jumps over the lazy dog"), U(10, "B", "hello world this is here") };
        var hypothesis = new[] { U(3, "x", "the quick brown fox runs"), U(8, "x", "jumps over the lazy dog"), U(13, "y", "hello world this is here") };
        Assert.Equal(3.0, Aligner.EstimateOffsetSeconds(reference, hypothesis), 1);
    }

    [Fact]
    public void Align_MatchesSimilar_GapsDissimilar()
    {
        var reference = new[] { U(0, "A", "the quick brown fox"), U(5, "A", "jumps over the lazy dog"), U(10, "B", "hello world out there") };
        var hypothesis = new[] { U(0, "S1", "the quick brown fox"), U(7, "S1", "completely unrelated noise content"), U(11, "S2", "hello world out there") };
        var pairs = Aligner.Align(reference, hypothesis);

        Assert.Equal(2, pairs.Count(p => p.Matched));
        Assert.Equal(1, pairs.Count(p => p.Missing));    // "jumps over the lazy dog"
        Assert.Equal(1, pairs.Count(p => p.Spurious));   // "completely unrelated noise content"
    }
}

public class ReconMetricsTests
{
    private static Utterance U(double t, string sp, string text) => new(t, null, sp, text);

    [Fact]
    public void Compute_MapsLabelsToNames_AndFlagsFragmentation()
    {
        var teams = new[] { U(0, "Alice", "the quick brown fox runs"), U(5, "Bob", "hello world this is bob"), U(10, "Alice", "another line from alice here") };
        var live = new[] { U(0, "Speaker 1", "the quick brown fox runs"), U(5, "Speaker 2", "hello world this is bob"), U(10, "Speaker 3", "another line from alice here") };

        var r = Metrics.Compute("live vs teams", teams, live);

        Assert.Equal(0.0, r.Wer, 3);                              // identical text
        Assert.Equal(1.0, r.WordRecall, 3);
        Assert.Equal(1.0, r.WordPrecision, 3);
        Assert.Equal("Alice", r.Speakers.LabelToName["Speaker 1"]);
        Assert.Equal("Bob", r.Speakers.LabelToName["Speaker 2"]);
        Assert.Equal("Alice", r.Speakers.LabelToName["Speaker 3"]);
        Assert.Equal(1, r.Speakers.FragmentedReferenceSpeakers);  // Alice split across Speaker 1 + Speaker 3
    }
}
