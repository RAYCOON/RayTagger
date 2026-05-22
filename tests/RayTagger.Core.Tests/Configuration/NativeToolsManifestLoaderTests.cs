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
    public void Empty_tools_and_models_section_is_rejected()
    {
        const string yaml = "schema_version: 1\ntools: {}";

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath == "tools"));
    }

    [Fact]
    public void Models_only_manifest_is_accepted()
    {
        // The pre-B4 schema required at least one tool entry. After B4 a manifest can declare
        // only models — useful for orgs that ship Essentia/fpcalc via OS packages but still want
        // RayTagger to bootstrap the TF model files.
        const string yaml = """
            schema_version: 1
            tools: {}
            models:
              electronic:
                version: "1"
                files:
                  - url: "https://example.invalid/embedding.pb"
                    sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                  - url: "https://example.invalid/head.pb"
                    sha256: "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
                  - url: "https://example.invalid/labels.json"
                    sha256: "1111111111111111111111111111111111111111111111111111111111111111"
                    rename_to: "labels.json"
            """;

        var manifest = NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        manifest.Models.Should().ContainKey("electronic");
        var electronic = manifest.Models["electronic"];
        electronic.Version.Should().Be("1");
        electronic.Files.Should().HaveCount(3);
        electronic.Files.Last().RenameTo.Should().Be("labels.json");
    }

    [Fact]
    public void Model_with_plain_http_url_is_refused()
    {
        const string yaml = """
            schema_version: 1
            models:
              electronic:
                version: "1"
                files:
                  - url: "http://example.com/model.pb"
                    sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            """;

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath.Contains("models.electronic.files[0].url", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("subdir/file.json")]
    [InlineData("subdir\\file.json")]
    [InlineData("/abs/path.json")]
    public void Model_rename_to_with_path_separators_is_refused(string renameTo)
    {
        // Path-traversal guard: rename_to feeds Path.Combine which would happily resolve '..'
        // outside the cache root. The validator rejects anything that isn't a simple filename.
        var yaml = $$"""
            schema_version: 1
            models:
              electronic:
                version: "1"
                files:
                  - url: "https://example.com/labels.json"
                    sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
                    rename_to: '{{renameTo}}'
            """;

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath.EndsWith("rename_to", StringComparison.Ordinal)));
    }

    [Fact]
    public void Model_with_zero_files_is_rejected()
    {
        const string yaml = """
            schema_version: 1
            models:
              electronic:
                version: "1"
                files: []
            """;

        var act = () => NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        act.Should().Throw<ConfigurationException>()
            .Where(ex => ex.Errors.Any(e => e.YamlPath == "models.electronic.files"));
    }

    [Fact]
    public void Example_manifest_loads_successfully()
    {
        var yaml = File.ReadAllText(RepoRoot.Samples("native-tools.example.yaml"));

        var manifest = NativeToolsManifestLoader.LoadFromString(yaml, RepoRoot.Path);

        manifest.Tools.Keys.Should().Contain("essentia_streaming_extractor_music");
        manifest.Tools.Keys.Should().Contain("fpcalc");
        manifest.Models.Keys.Should().Contain("electronic");
        manifest.Models.Keys.Should().Contain("jamendo");
        manifest.Models.Keys.Should().Contain("discogs-effnet");

        // Every shipped model entry resolves the embedding source plus a head .pb / labels.json
        // — coverage sanity check for the example.
        foreach (var (key, model) in manifest.Models)
        {
            model.Version.Should().NotBeEmpty(because: $"{key} needs a cache-bust marker");
            model.Files.Should().NotBeEmpty(because: $"{key} needs at least one file");
            model.Files.Should().Contain(f => f.RenameTo == "labels.json",
                because: $"{key} must rename the Essentia metadata JSON to labels.json");
        }
    }
}
