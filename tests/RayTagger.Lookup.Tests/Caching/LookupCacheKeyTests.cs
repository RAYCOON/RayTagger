using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Caching;

namespace RayTagger.Lookup.Tests.Caching;

public class LookupCacheKeyTests
{
    [Fact]
    public void Fingerprint_takes_precedence_over_artist_title()
    {
        var withFingerprint = LookupCacheKey.From(new LookupQuery
        {
            Fingerprint = "AQAA...",
            Artist = "X",
            Title = "Y",
        });
        var fingerprintOnly = LookupCacheKey.From(new LookupQuery { Fingerprint = "AQAA..." });

        withFingerprint.Should().Be(fingerprintOnly);
        withFingerprint.Should().StartWith("fp_");
    }

    [Fact]
    public void Artist_title_normalisation_collapses_whitespace_and_case()
    {
        var a = LookupCacheKey.From(new LookupQuery { Artist = "The Beatles", Title = "Hey Jude" });
        var b = LookupCacheKey.From(new LookupQuery { Artist = "the beatles!", Title = "HEY  JUDE" });

        a.Should().Be(b);
        a.Should().StartWith("ata_");
    }

    [Fact]
    public void Different_titles_produce_different_keys()
    {
        var a = LookupCacheKey.From(new LookupQuery { Artist = "Daft Punk", Title = "Around the World" });
        var b = LookupCacheKey.From(new LookupQuery { Artist = "Daft Punk", Title = "One More Time" });

        a.Should().NotBe(b);
    }

    [Fact]
    public void Empty_query_still_produces_a_stable_key()
    {
        // Important for the fail-open path: even a degenerate query gets a cache entry rather
        // than throwing — the runner short-circuits earlier, but the key fn must stay total.
        var key = LookupCacheKey.From(new LookupQuery());

        key.Should().NotBeNullOrWhiteSpace();
        key.Should().StartWith("ata_");
    }
}
