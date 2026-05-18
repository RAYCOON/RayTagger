using System.CommandLine;
using RayTagger.Cli;

namespace RayTagger.Cli.Tests;

public class CommandParsingTests
{
    private static RootCommand Root => Program.BuildRootCommand();

    [Fact]
    public void Root_command_exposes_scan_verb()
    {
        var root = Root;

        root.Subcommands.Should().ContainSingle(c => c.Name == "scan");
    }

    [Fact]
    public void Scan_verb_exposes_documented_options()
    {
        var scan = Root.Subcommands.Single(c => c.Name == "scan");

        var optionNames = scan.Options.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

        optionNames.Should().Contain(["--config", "--source", "--dry-run", "--write", "--verbose"]);
    }

    [Fact]
    public void Empty_args_produce_a_parse_error_pointing_to_a_required_subcommand()
    {
        var result = Root.Parse([]);

        // No subcommand → System.CommandLine surfaces a "required command" error.
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Unknown_subcommand_produces_a_parse_error()
    {
        var result = Root.Parse(["doesnotexist"]);

        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Scan_with_short_aliases_parses_correctly()
    {
        var result = Root.Parse(["scan", "-c", "tagger.yaml", "-v"]);

        result.Errors.Should().BeEmpty();
    }
}
