using RayTagger.Cli;

namespace RayTagger.Cli.Tests;

/// <summary>
/// End-to-end smoke tests: invoke <c>tagger scan</c> against a real temp directory with config
/// files, verify exit codes and that the pipeline traversal works through to the Spectre render.
/// </summary>
public sealed class EndToEndScanTests : IDisposable
{
    private readonly string _root;

    public EndToEndScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tagger-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Scan_dry_run_on_empty_directory_succeeds_with_exit_code_0()
    {
        WriteValidConfig(scanSource: _root);

        var configPath = Path.Combine(_root, "tagger.yaml");
        var exitCode = await InvokeAsync("scan", "--config", configPath, "--dry-run");

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task Conflicting_dry_run_and_write_produce_exit_code_64()
    {
        WriteValidConfig(scanSource: _root);
        var configPath = Path.Combine(_root, "tagger.yaml");

        var exitCode = await InvokeAsync("scan", "--config", configPath, "--dry-run", "--write");

        exitCode.Should().Be(64);  // ExitCodes.InvalidArguments
    }

    [Fact]
    public async Task Scan_with_force_overwrite_flag_parses_and_runs_clean()
    {
        // Smoke test for the --force-overwrite flag: the option must parse without errors and
        // the run must finish with exit 0 on an empty source folder. This guards the CLI wiring;
        // the underlying merge behaviour (existing_confidence → 0) is covered by TagMergerTests.
        WriteValidConfig(scanSource: _root);
        var configPath = Path.Combine(_root, "tagger.yaml");

        var exitCode = await InvokeAsync(
            "scan", "--config", configPath, "--dry-run", "--force-overwrite");

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task Missing_config_file_returns_invalid_configuration_exit_code()
    {
        var exitCode = await InvokeAsync("scan", "--config", Path.Combine(_root, "does-not-exist.yaml"), "--dry-run");

        exitCode.Should().Be(2);  // ExitCodes.InvalidConfiguration
    }

    [Fact]
    public async Task Scan_with_corrupt_audio_file_reports_failure_but_does_not_crash()
    {
        // A zero-byte .mp3 isn't a valid container — the pipeline catches the per-file error and
        // continues. Exit code becomes 1 because at least one file failed.
        File.WriteAllBytes(Path.Combine(_root, "broken.mp3"), []);
        WriteValidConfig(scanSource: _root);
        var configPath = Path.Combine(_root, "tagger.yaml");

        var exitCode = await InvokeAsync("scan", "--config", configPath, "--dry-run");

        exitCode.Should().Be(1);  // at least one file failed
    }

    private void WriteValidConfig(string scanSource)
    {
        File.WriteAllText(Path.Combine(_root, "tagger.yaml"), $"""
            version: 1
            scan:
              source: "{scanSource.Replace('\\', '/')}"
            mapping:
              rules_file: "./mappings.yaml"
            logging:
              console: false
              file:
                enabled: false
            """);

        File.WriteAllText(Path.Combine(_root, "mappings.yaml"), """
            version: 1
            rules: []
            """);
    }

    private static async Task<int> InvokeAsync(params string[] args)
    {
        var root = Program.BuildRootCommand();
        return await root.Parse(args).InvokeAsync();
    }
}
