using RayTagger.Core.IO;

namespace RayTagger.Core.Tests.IO;

public class EnvVarResolverTests
{
    [Fact]
    public void Substitutes_env_var_from_process_environment()
    {
        const string varName = "TAGGER_TEST_VAR_" + nameof(Substitutes_env_var_from_process_environment);
        Environment.SetEnvironmentVariable(varName, "abc123");
        try
        {
            var resolver = new EnvVarResolver();

            var success = resolver.TryResolve($"key: ${{env:{varName}}}", out var resolved, out var missing);

            success.Should().BeTrue();
            resolved.Should().Be("key: abc123");
            missing.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Falls_back_to_dotenv_when_process_env_missing()
    {
        var dotEnv = new Dictionary<string, string> { ["MY_API_KEY"] = "from-dotenv" };
        var resolver = new EnvVarResolver(dotEnv);

        var success = resolver.TryResolve("token: ${env:MY_API_KEY}", out var resolved, out _);

        success.Should().BeTrue();
        resolved.Should().Be("token: from-dotenv");
    }

    [Fact]
    public void Process_environment_takes_precedence_over_dotenv()
    {
        const string varName = "TAGGER_TEST_VAR_" + nameof(Process_environment_takes_precedence_over_dotenv);
        Environment.SetEnvironmentVariable(varName, "from-process");
        try
        {
            var dotEnv = new Dictionary<string, string> { [varName] = "from-dotenv" };
            var resolver = new EnvVarResolver(dotEnv);

            resolver.TryResolve($"value: ${{env:{varName}}}", out var resolved, out _);

            resolved.Should().Contain("from-process");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void Reports_missing_variable_names()
    {
        var resolver = new EnvVarResolver();

        var success = resolver.TryResolve(
            "a: ${env:DEFINITELY_NOT_SET_1}\nb: ${env:DEFINITELY_NOT_SET_2}",
            out var resolved,
            out var missing);

        success.Should().BeFalse();
        resolved.Should().BeNull();
        missing.Should().BeEquivalentTo(["DEFINITELY_NOT_SET_1", "DEFINITELY_NOT_SET_2"]);
    }

    [Fact]
    public void Text_without_placeholders_passes_through()
    {
        var resolver = new EnvVarResolver();

        resolver.TryResolve("plain text", out var resolved, out _);

        resolved.Should().Be("plain text");
    }

    [Fact]
    public void Placeholders_inside_yaml_comments_are_not_substituted()
    {
        // Regression: documentation comments that mention ${env:NAME} syntax must NOT trigger
        // lookups — otherwise every doc comment about env-vars would fail the load.
        var resolver = new EnvVarResolver();

        const string yaml = """
            # Set ${env:DEFINITELY_NOT_SET} in your environment.
            key: real-value
            other: real  # also a comment with ${env:ALSO_NOT_SET}
            """;

        var success = resolver.TryResolve(yaml, out var resolved, out var missing);

        success.Should().BeTrue();
        missing.Should().BeEmpty();
        resolved.Should().Contain("${env:DEFINITELY_NOT_SET}");
        resolved.Should().Contain("${env:ALSO_NOT_SET}");
    }

    [Fact]
    public void Placeholders_in_actual_values_are_still_substituted_when_line_also_has_comment()
    {
        const string varName = "TAGGER_TEST_VAR_" + nameof(Placeholders_in_actual_values_are_still_substituted_when_line_also_has_comment);
        Environment.SetEnvironmentVariable(varName, "from-env");
        try
        {
            var resolver = new EnvVarResolver();

            var success = resolver.TryResolve(
                $"key: ${{env:{varName}}}  # ${{env:DEFINITELY_NOT_SET}}",
                out var resolved,
                out var missing);

            success.Should().BeTrue();
            missing.Should().BeEmpty();
            resolved.Should().Contain("from-env");
            resolved.Should().Contain("${env:DEFINITELY_NOT_SET}");  // still inside comment, untouched
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void LoadDotEnv_parses_keys_values_and_quoted_strings()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
                # comment line
                PLAIN=value1
                QUOTED="value with spaces"
                SINGLE='single quotes'

                EMPTY_LINES_OK=yes
                """);

            var result = EnvVarResolver.LoadDotEnv(tempFile);

            result.Should().Contain("PLAIN", "value1");
            result.Should().Contain("QUOTED", "value with spaces");
            result.Should().Contain("SINGLE", "single quotes");
            result.Should().Contain("EMPTY_LINES_OK", "yes");
            result.Should().NotContainKey("# comment line");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
