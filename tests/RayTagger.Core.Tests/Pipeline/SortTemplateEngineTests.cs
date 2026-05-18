using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;

namespace RayTagger.Core.Tests.Pipeline;

public class SortTemplateEngineTests
{
    [Fact]
    public void Renders_required_placeholders()
    {
        var existing = new TrackTags(Title: "Hey Jude", Artist: "The Beatles");
        var resolved = WithGenre("Rock");

        var path = SortTemplateEngine.Render("{genre}/{artist} - {title}.{ext}", existing, resolved, "mp3");

        path.Should().Be(Path.Combine("Rock", "The Beatles - Hey Jude.mp3"));
    }

    [Fact]
    public void Drops_optional_path_component_when_value_missing()
    {
        var existing = new TrackTags(Title: "Test", Artist: "X");
        var resolved = WithGenre("Electronic");  // no SubGenre

        var path = SortTemplateEngine.Render("{genre}/{subgenre?}/{artist} - {title}.{ext}", existing, resolved, "flac");

        path.Should().Be(Path.Combine("Electronic", "X - Test.flac"));
        path.Should().NotContain(Path.DirectorySeparatorChar + Path.DirectorySeparatorChar.ToString());
    }

    [Fact]
    public void Includes_optional_path_component_when_value_present()
    {
        var existing = new TrackTags(Title: "Test", Artist: "X");
        var resolved = new ResolvedTrackTags(
            new ResolvedField<string>("Electronic", TagFieldSource.Rules, 1),
            new ResolvedField<string>("Deep House", TagFieldSource.Rules, 1),
            ResolvedField.EmptyValue<double>(),
            ResolvedField.Empty<MusicalKey>(),
            ResolvedField.EmptyValue<int>(),
            ResolvedField.Empty<string>(),
            ResolvedField.Empty<string>(),
            new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));

        var path = SortTemplateEngine.Render("{genre}/{subgenre?}/{artist} - {title}.{ext}", existing, resolved, "mp3");

        path.Should().Be(Path.Combine("Electronic", "Deep House", "X - Test.mp3"));
    }

    [Fact]
    public void Throws_when_required_placeholder_missing()
    {
        var existing = new TrackTags(Title: "Test");  // no Artist
        var resolved = WithGenre("Rock");

        var act = () => SortTemplateEngine.Render("{genre}/{artist}.{ext}", existing, resolved, "mp3");

        act.Should().Throw<SortTemplateException>().WithMessage("*{artist}*");
    }

    [Fact]
    public void Throws_on_unknown_placeholder()
    {
        var existing = new TrackTags(Title: "T", Artist: "A");
        var resolved = WithGenre("G");

        var act = () => SortTemplateEngine.Render("{nonexistent}/{title}.{ext}", existing, resolved, "mp3");

        act.Should().Throw<SortTemplateException>().WithMessage("*nonexistent*");
    }

    [Theory]
    [InlineData("AC/DC", "AC_DC")]                  // forward slash → reserved
    [InlineData("Foo: Bar", "Foo_ Bar")]            // colon
    [InlineData("a*b?c\"d<e>f|g", "a_b_c_d_e_f_g")] // every Windows-reserved char
    [InlineData("trailing  ", "trailing")]          // trailing spaces stripped
    [InlineData("trailing dots...", "trailing dots")] // trailing dots stripped
    public void Sanitises_filesystem_unsafe_chars(string raw, string expected)
    {
        var existing = new TrackTags(Title: raw, Artist: "X");
        var resolved = WithGenre("G");

        var path = SortTemplateEngine.Render("{title}.{ext}", existing, resolved, "mp3");

        path.Should().Be($"{expected}.mp3");
    }

    // A tag value like "..." or "   " sanitises to an empty string after TrimEnd. Before the
    // fix, that empty component slipped through and `string.Join('/', ["", "rest"])` produced
    // a leading separator that Path.Combine treats as rooted on POSIX — silently moving files
    // outside the user's sort destination. Regression guards against the path-traversal vector.
    [Theory]
    [InlineData("...", "_")]
    [InlineData("   ", "_")]
    [InlineData(".. ..", "_")]   // trims trailing dots+spaces — also collapses
    [InlineData(". .", "_")]
    public void Empty_after_sanitise_substitutes_underscore_not_traversal(string maliciousTitle, string expected)
    {
        var existing = new TrackTags(Title: maliciousTitle, Artist: "Artist");
        var resolved = WithGenre("Rock");

        var path = SortTemplateEngine.Render("{title}/{artist}.{ext}", existing, resolved, "mp3");

        // The dangerous result would be "/Artist.mp3" — Path.Combine(dest, "/Artist.mp3")
        // discards dest on POSIX. The safe result is "<placeholder>/Artist.mp3".
        path.Should().Be(Path.Combine(expected, "Artist.mp3"));
        path.Should().NotStartWith(Path.DirectorySeparatorChar.ToString());
    }

    [Fact]
    public void Combining_with_destination_stays_under_destination_even_for_dot_title()
    {
        var existing = new TrackTags(Title: "...", Artist: "Artist");
        var resolved = WithGenre("Rock");

        var relative = SortTemplateEngine.Render("{title}/{artist}.{ext}", existing, resolved, "mp3");
        var destination = OperatingSystem.IsWindows() ? @"C:\Music\Library" : "/Music/Library";
        var fullPath = Path.GetFullPath(Path.Combine(destination, relative));

        // The whole point: combined path must stay under the destination root.
        fullPath.Should().StartWith(destination);
    }

    [Fact]
    public void Renders_bpm_key_and_energy_from_resolved_tags()
    {
        var existing = new TrackTags(Title: "T", Artist: "A");
        var resolved = new ResolvedTrackTags(
            new ResolvedField<string>("G", TagFieldSource.Rules, 1),
            ResolvedField.Empty<string>(),
            new ResolvedValueField<double>(128.0, TagFieldSource.Analysis, 1),
            new ResolvedField<MusicalKey>(new MusicalKey("Am", "8A"), TagFieldSource.Analysis, 1),
            new ResolvedValueField<int>(7, TagFieldSource.Analysis, 1),
            ResolvedField.Empty<string>(),
            ResolvedField.Empty<string>(),
            new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));

        var path = SortTemplateEngine.Render("{bpm}_{camelot}_{energy}.{ext}", existing, resolved, "mp3");

        path.Should().Be("128_8A_7.mp3");
    }

    [Fact]
    public void Placeholder_names_are_case_insensitive()
    {
        var existing = new TrackTags(Title: "T", Artist: "A");
        var resolved = WithGenre("G");

        var path = SortTemplateEngine.Render("{Genre}/{Artist}.{EXT}", existing, resolved, "mp3");

        path.Should().Be(Path.Combine("G", "A.mp3"));
    }

    private static ResolvedTrackTags WithGenre(string genre) =>
        new(
            new ResolvedField<string>(genre, TagFieldSource.Rules, 1),
            ResolvedField.Empty<string>(),
            ResolvedField.EmptyValue<double>(),
            ResolvedField.Empty<MusicalKey>(),
            ResolvedField.EmptyValue<int>(),
            ResolvedField.Empty<string>(),
            ResolvedField.Empty<string>(),
            new Dictionary<string, ResolvedField<string>>(StringComparer.OrdinalIgnoreCase));
}
