using RayTagger.Analysis.Genre;

namespace RayTagger.Analysis.Tests.Genre;

public class ClassifierLabelNormaliserTests
{
    [Theory]
    [InlineData("deep_house",      "deep house")]
    [InlineData("Deep House",      "deep house")]
    [InlineData("drum-n-bass",     "drum n bass")]  // "ss" suffix guarded against depluralisation
    [InlineData("electronic/house","electronic house")]
    [InlineData("trances",         "trance")]
    [InlineData("HOUSE",           "house")]
    [InlineData("   tech   house  ", "tech house")]
    [InlineData("non_electronic",  "non electronic")]
    public void Normalises_canonical_forms(string raw, string expected)
    {
        ClassifierLabelNormaliser.Normalise(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_empty_for_null_or_blank(string? raw)
    {
        ClassifierLabelNormaliser.Normalise(raw).Should().BeEmpty();
    }

    [Fact]
    public void Leaves_short_words_alone()
    {
        // "bass" / "jazz" / "rap" must not lose their trailing letter.
        ClassifierLabelNormaliser.Normalise("bass").Should().Be("bass");
        ClassifierLabelNormaliser.Normalise("jazz").Should().Be("jazz");
        ClassifierLabelNormaliser.Normalise("rap").Should().Be("rap");
    }

    [Fact]
    public void Heuristic_canonical_labels_round_trip()
    {
        // The heuristic classifier emits PascalCase labels directly — they go through the
        // same normaliser for symmetry. Lowercase output is fine because the resolver's
        // whole-word regex is case-insensitive.
        ClassifierLabelNormaliser.Normalise("House").Should().Be("house");
        ClassifierLabelNormaliser.Normalise("Techno").Should().Be("techno");
        ClassifierLabelNormaliser.Normalise("Trance").Should().Be("trance");
    }
}
