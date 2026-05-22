using System.Reflection;
using System.Text.RegularExpressions;
using RayTagger.Hosting;

namespace RayTagger.Lookup.Tests;

public class UserAgentFactoryTests
{
    // Matches the MusicBrainz-recommended shape:
    //   <product>/<version>  <SP> ( <SP> <contact> <SP> )
    // The spec is explicit about the space-padded comment; "+" prefix from crawler conventions
    // is NOT in the spec and we deliberately do not emit it.
    private static readonly Regex MbCompliantFormat = new(
        @"^[A-Za-z][A-Za-z0-9._-]*/\S+ \( \S.*\S \)$",
        RegexOptions.Compiled);

    [Fact]
    public void Default_uses_project_repository_as_contact()
    {
        var ua = UserAgentFactory.Build();

        ua.Should().Contain("RayTagger/");
        ua.Should().Contain("( https://github.com/RAYCOON/RayTagger )");
        ua.Should().NotContain("(+");
    }

    [Fact]
    public void Format_matches_musicbrainz_recommendation()
    {
        var ua = UserAgentFactory.Build();

        MbCompliantFormat.IsMatch(ua).Should().BeTrue(
            "MusicBrainz expects 'Name/Version ( contact )' with spaces inside the parens (got '{0}')", ua);
    }

    [Fact]
    public void Parses_as_a_valid_http_user_agent_header()
    {
        var ua = UserAgentFactory.Build();

        // ParseAdd is the same validator HttpClient runs at request time; if this throws,
        // every outgoing request would fail with a FormatException.
        using var request = new HttpRequestMessage();
        var act = () => request.Headers.UserAgent.ParseAdd(ua);

        act.Should().NotThrow();
        request.Headers.UserAgent.Should().HaveCountGreaterThan(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Null_or_whitespace_contact_falls_back_to_default(string? contact)
    {
        var ua = UserAgentFactory.Build(contact);

        ua.Should().Contain($"( {UserAgentFactory.DefaultContact} )");
    }

    [Fact]
    public void Email_contact_is_embedded_verbatim()
    {
        var ua = UserAgentFactory.Build("daniel.pavic@raycoon.com");

        ua.Should().EndWith("( daniel.pavic@raycoon.com )");
    }

    [Fact]
    public void Url_contact_is_embedded_verbatim()
    {
        var ua = UserAgentFactory.Build("https://example.com/contact");

        ua.Should().EndWith("( https://example.com/contact )");
    }

    [Fact]
    public void Surrounding_whitespace_on_override_is_trimmed()
    {
        var ua = UserAgentFactory.Build("  me@example.com  ");

        ua.Should().EndWith("( me@example.com )");
    }

    [Fact]
    public void Version_reflects_hosting_assembly_informational_version()
    {
        var ua = UserAgentFactory.Build();

        // Pull the version directly from the assembly the factory reads from. Comparing against
        // a hard-coded "0.1.0-alpha" would force this test to be updated on every release bump.
        var assemblyVersion = typeof(UserAgentFactory).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? typeof(UserAgentFactory).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";

        // Strip the deterministic-build "+<sha>" suffix the same way the factory does.
        var plus = assemblyVersion.IndexOf('+', StringComparison.Ordinal);
        var expected = plus < 0 ? assemblyVersion : assemblyVersion[..plus];

        ua.Should().StartWith($"RayTagger/{expected} (");
    }
}
