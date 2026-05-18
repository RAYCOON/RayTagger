using RayTagger.Core.Configuration;
using RayTagger.Core.Tests.Support;

namespace RayTagger.Core.Tests.Configuration;

public class NativeToolsManifestLoaderTests
{
    [Fact]
    public void Loads_a_minimal_valid_manifest()
    {
        const string yaml = """
            schema_version: 1
            tools:
              essentia_streaming_extractor_music:
                version: "2.0-7e90d20"
                sources:
                  osx-arm64:
                    url: "https://example.invalid/essentia.tar.gz"
                    sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    archive_format: tar_gz
                    binary_path: "essentia_streaming_extractor_music"
            """;

        var manifest = NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        manifest.SchemaVersion.Should().Be(1);
        manifest.Tools.Should().ContainKey("essentia_streaming_extractor_music");

        var entry = manifest.Tools["essentia_streaming_extractor_music"];
        entry.Version.Should().Be("2.0-7e90d20");
        entry.Sources.Should().ContainKey("osx-arm64");

        var source = entry.Sources["osx-arm64"];
        source.Url.Should().StartWith("https://");
        source.Sha256.Should().HaveLength(64);
        source.ArchiveFormat.Should().Be(NativeToolArchiveFormat.TarGz);
    }

    [Fact]
    public void Plain_http_urls_are_refused()
    {
        const string yaml = """
            schema_version: 1
            tools:
              fpcalc:
                version: "1.5.1"
                sources:
                  linux-x64:
                    url: "http://example.com/fpcalc.tar.gz"
                    sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    archive_format: tar_gz
                    binary_path: "fpcalc"
            """;

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Message.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Malformed_sha256_is_rejected_with_field_path()
    {
        const string yaml = """
            schema_version: 1
            tools:
              fpcalc:
                version: "1"
                sources:
                  linux-x64:
                    url: "https://example.invalid/fpcalc"
                    sha256: "tooshort"
                    archive_format: none
            """;

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath.EndsWith("sha256", StringComparison.Ordinal)));
    }

    [Fact]
    public void Binary_path_without_archive_is_rejected()
    {
        const string yaml = """
            schema_version: 1
            tools:
              fpcalc:
                version: "1"
                sources:
                  linux-x64:
                    url: "https://example.invalid/fpcalc"
                    sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    archive_format: none
                    binary_path: "should/not/be/here"
            """;

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath.EndsWith("binary_path", StringComparison.Ordinal)));
    }

    [Fact]
    public void Empty_tools_section_is_rejected()
    {
        const string yaml = "schema_version: 1\ntools: {}";

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath == "tools"));
    }

    [Fact]
    public void Example_manifest_loads_successfully()
    {
        var yaml = File.ReadAllText(RepoRoot.Samples("native-tools.example.yaml"));

        var manifest = NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        manifest.Tools.Keys.Should().Contain("essentia_streaming_extractor_music");
        manifest.Tools.Keys.Should().Contain("fpcalc");
    }
}
