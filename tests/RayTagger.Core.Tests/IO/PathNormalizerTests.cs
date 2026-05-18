using RayTagger.Core.IO;

namespace RayTagger.Core.Tests.IO;

public class PathNormalizerTests
{
    private static readonly string ConfigDir = Path.Combine(Path.GetTempPath(), "tagger-tests");
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void Absolute_path_returned_unchanged_except_canonical_form()
    {
        var input = Path.Combine(Home, "Music", "incoming");

        var result = PathNormalizer.Normalize(input, ConfigDir);

        result.Should().Be(Path.GetFullPath(input));
    }

    [Fact]
    public void Tilde_alone_expands_to_user_home()
    {
        var result = PathNormalizer.Normalize("~", ConfigDir);

        result.Should().Be(Home);
    }

    [Fact]
    public void Tilde_slash_path_expands_to_user_home_subpath()
    {
        var result = PathNormalizer.Normalize("~/Music/incoming", ConfigDir);

        result.Should().Be(Path.Combine(Home, "Music", "incoming"));
    }

    [Fact]
    public void Relative_path_resolves_against_config_directory_not_cwd()
    {
        var result = PathNormalizer.Normalize("./subdir/file.yaml", ConfigDir);

        result.Should().Be(Path.GetFullPath(Path.Combine(ConfigDir, "subdir", "file.yaml")));
    }

    [Fact]
    public void Empty_input_passes_through()
    {
        PathNormalizer.Normalize("", ConfigDir).Should().Be("");
        PathNormalizer.Normalize("   ", ConfigDir).Should().Be("   ");
    }

    [Fact]
    public void Tilde_followed_by_non_separator_is_not_expanded()
    {
        // "~user" form is not supported and must pass through unchanged
        // (then be resolved as a relative path against configDir).
        var result = PathNormalizer.Normalize("~user/file.yaml", ConfigDir);

        result.Should().Be(Path.GetFullPath(Path.Combine(ConfigDir, "~user", "file.yaml")));
    }
}
