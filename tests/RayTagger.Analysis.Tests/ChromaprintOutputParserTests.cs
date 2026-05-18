using RayTagger.Analysis.Internal;

namespace RayTagger.Analysis.Tests;

public class ChromaprintOutputParserTests
{
    [Fact]
    public void Extracts_fingerprint_and_duration_from_canonical_output()
    {
        const string output = """
            FILE=/path/to/song.mp3
            DURATION=180
            FINGERPRINT=AQADtMOikkmSpEm-cTzCRn-Q
            """;

        var parsed = ChromaprintOutputParser.Parse(output);

        parsed.Fingerprint.Should().Be("AQADtMOikkmSpEm-cTzCRn-Q");
        parsed.DurationSeconds.Should().Be(180);
    }

    [Fact]
    public void Handles_fractional_duration()
    {
        const string output = "DURATION=180.5\nFINGERPRINT=ABC";

        var parsed = ChromaprintOutputParser.Parse(output);

        parsed.DurationSeconds.Should().Be(180.5);
    }

    [Fact]
    public void Ignores_unrecognised_keys()
    {
        const string output = "FILE=/foo.mp3\nNOISE=bla\nFINGERPRINT=XYZ\n";

        var parsed = ChromaprintOutputParser.Parse(output);

        parsed.Fingerprint.Should().Be("XYZ");
        parsed.DurationSeconds.Should().BeNull();
    }

    [Fact]
    public void Returns_nulls_when_keys_missing()
    {
        var parsed = ChromaprintOutputParser.Parse("");

        parsed.Fingerprint.Should().BeNull();
        parsed.DurationSeconds.Should().BeNull();
    }

    [Fact]
    public void Empty_fingerprint_value_becomes_null()
    {
        var parsed = ChromaprintOutputParser.Parse("FINGERPRINT=\n");

        parsed.Fingerprint.Should().BeNull();
    }

    [Fact]
    public void Case_insensitive_key_matching()
    {
        var parsed = ChromaprintOutputParser.Parse("fingerprint=abc\nDuration=42");

        parsed.Fingerprint.Should().Be("abc");
        parsed.DurationSeconds.Should().Be(42);
    }
}
