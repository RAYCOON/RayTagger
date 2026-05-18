using RayTagger.Core.Configuration;

namespace RayTagger.Core.Tests.Configuration;

public class TagFieldMapBuilderTests
{
    [Fact]
    public void Empty_config_returns_default_map()
    {
        var errors = new List<ConfigurationError>();
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var map = TagFieldMapBuilder.Build(raw, errors);

        errors.Should().BeEmpty();
        map.Should().BeSameAs(TagFieldMap.Default);
    }

    [Fact]
    public void Camelot_id3_txxx_description_overridden()
    {
        var errors = new List<ConfigurationError>();
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["camelot"] = ["ID3:TXXX:DJ_CAMELOT", "VORBIS:CAMELOTKEY"],
        };

        var map = TagFieldMapBuilder.Build(raw, errors);

        errors.Should().BeEmpty();
        map.CamelotKeyId3Description.Should().Be("DJ_CAMELOT");
        map.CamelotKeyVorbisField.Should().Be("CAMELOTKEY");
        // Untouched dimensions keep their defaults.
        map.SubGenreId3Description.Should().Be("SUBGENRE");
        map.EnergyLevelVorbisField.Should().Be("ENERGYLEVEL");
    }

    [Fact]
    public void Unknown_logical_field_reports_error()
    {
        var errors = new List<ConfigurationError>();
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            // "mood" / "set_position" are first-class fields now, so pick something the builder
            // genuinely doesn't recognise.
            ["timecode"] = ["ID3:TXXX:TIMECODE"],
        };

        TagFieldMapBuilder.Build(raw, errors);

        errors.Should().ContainSingle()
            .Which.YamlPath.Should().Be("write.tag_fields.timecode");
    }

    [Fact]
    public void Malformed_token_reports_error_with_yaml_path()
    {
        var errors = new List<ConfigurationError>();
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["camelot"] = ["TBPM"],   // missing prefix
        };

        TagFieldMapBuilder.Build(raw, errors);

        errors.Should().HaveCount(1);
        errors[0].YamlPath.Should().Be("write.tag_fields.camelot");
    }

    [Theory]
    [InlineData("ID3:TBPM")]                    // standard frame — accepted but ignored
    [InlineData("ID3:TXXX:CAMELOTKEY")]         // TXXX description
    [InlineData("VORBIS:CAMELOTKEY")]           // Vorbis field
    public void Valid_token_shapes_are_accepted(string token)
    {
        var errors = new List<ConfigurationError>();
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["camelot"] = [token],
        };

        TagFieldMapBuilder.Build(raw, errors);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_container_prefix_reports_error()
    {
        var errors = new List<ConfigurationError>();
        var raw = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["camelot"] = ["MP4:DJKEY"],
        };

        TagFieldMapBuilder.Build(raw, errors);

        errors.Should().ContainSingle();
        errors[0].Reason.Should().Contain("MP4");
    }
}
