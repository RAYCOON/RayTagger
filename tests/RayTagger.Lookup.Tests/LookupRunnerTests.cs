using Microsoft.Extensions.Logging.Abstractions;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using RayTagger.Core.Pipeline;
using RayTagger.Lookup.Caching;

namespace RayTagger.Lookup.Tests;

public class LookupRunnerTests
{
    [Fact]
    public async Task Returns_empty_when_disabled()
    {
        var provider = MakeProvider("musicbrainz");
        var runner = new LookupRunner(
            [provider],
            new LookupOptions { Enabled = false, Providers = ["musicbrainz"] },
            cache: null,
            NullLogger<LookupRunner>.Instance);

        var result = await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y" });

        result.Should().BeSameAs(LookupResult.Empty);
        await provider.DidNotReceive().LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_empty_when_query_has_no_usable_signal()
    {
        var provider = MakeProvider("musicbrainz");
        var runner = MakeRunner(provider);

        var result = await runner.RunAsync(new LookupQuery());

        result.Should().BeSameAs(LookupResult.Empty);
    }

    [Fact]
    public async Task Respects_configured_provider_order()
    {
        var mb = MakeProvider("musicbrainz");
        var lf = MakeProvider("lastfm");

        var callOrder = new List<string>();
        mb.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("mb"); return Task.FromResult<LookupResult?>(LookupResult.Empty); });
        lf.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => { callOrder.Add("lf"); return Task.FromResult<LookupResult?>(LookupResult.Empty); });

        var runner = new LookupRunner(
            [lf, mb],
            new LookupOptions { Providers = ["lastfm", "musicbrainz"] },
            cache: null,
            NullLogger<LookupRunner>.Instance);

        await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y" });

        callOrder.Should().Equal("lf", "mb");
    }

    [Fact]
    public async Task Propagates_acoustid_mbid_into_followups()
    {
        var mbid = Guid.NewGuid();
        var acoustid = MakeProvider("acoustid");
        acoustid.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LookupResult([], [], MbRecordingId: mbid));

        var observed = new List<LookupQuery>();
        var mb = MakeProvider("musicbrainz");
        mb.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                observed.Add(ci.Arg<LookupQuery>());
                return Task.FromResult<LookupResult?>(LookupResult.Empty);
            });

        var runner = new LookupRunner(
            [acoustid, mb],
            new LookupOptions { Providers = ["acoustid", "musicbrainz"] },
            cache: null,
            NullLogger<LookupRunner>.Instance);

        await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y", Fingerprint = "FP", DurationSeconds = 200 });

        observed.Should().ContainSingle();
        observed[0].RecordingMbid.Should().Be(mbid);
    }

    [Fact]
    public async Task Merges_genre_candidates_ranked_by_confidence()
    {
        var a = MakeProvider("a");
        a.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LookupResult(
                [new GenreCandidate("House", 0.5, "a"), new GenreCandidate("Techno", 0.7, "a")],
                []));

        var b = MakeProvider("b");
        b.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LookupResult(
                [new GenreCandidate("House", 0.9, "b"), new GenreCandidate("Electronic", 0.6, "b")],
                []));

        var runner = new LookupRunner(
            [a, b],
            new LookupOptions { Providers = ["a", "b"] },
            cache: null,
            NullLogger<LookupRunner>.Instance);

        var result = await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y" });

        result.GenreCandidates.Should().HaveCount(3);
        result.GenreCandidates[0].Value.Should().Be("House");      // 0.9 wins over 0.5
        result.GenreCandidates[0].Source.Should().Be("b");
        result.GenreCandidates[1].Value.Should().Be("Techno");     // 0.7
        result.GenreCandidates[2].Value.Should().Be("Electronic"); // 0.6
    }

    [Fact]
    public async Task Provider_throwing_unexpectedly_does_not_break_the_chain()
    {
        var bad = MakeProvider("bad");
        bad.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns<LookupResult?>(_ => throw new InvalidOperationException("boom"));

        var good = MakeProvider("good");
        good.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LookupResult([new GenreCandidate("House", 0.8, "good")], []));

        var runner = new LookupRunner(
            [bad, good],
            new LookupOptions { Providers = ["bad", "good"] },
            cache: null,
            NullLogger<LookupRunner>.Instance);

        var result = await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y" });

        result.GenreCandidates.Should().ContainSingle();
        result.GenreCandidates[0].Value.Should().Be("House");
    }

    [Fact]
    public async Task Reads_from_cache_when_present()
    {
        var provider = MakeProvider("musicbrainz");
        var cache = Substitute.For<ILookupCache>();
        var cached = new LookupResult([new GenreCandidate("Cached", 1, "mb")], []);
        cache.GetAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(cached);

        var runner = new LookupRunner(
            [provider],
            new LookupOptions { Providers = ["musicbrainz"] },
            cache,
            NullLogger<LookupRunner>.Instance);

        var result = await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y" });

        result.GenreCandidates[0].Value.Should().Be("Cached");
        await provider.DidNotReceive().LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Writes_aggregated_result_back_into_cache()
    {
        var provider = MakeProvider("mb");
        provider.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LookupResult([new GenreCandidate("House", 0.7, "mb")], []));

        var cache = Substitute.For<ILookupCache>();
        cache.GetAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((LookupResult?)null);

        var runner = new LookupRunner(
            [provider],
            new LookupOptions { Providers = ["mb"] },
            cache,
            NullLogger<LookupRunner>.Instance);

        await runner.RunAsync(new LookupQuery { Artist = "X", Title = "Y" });

        await cache.Received(1).SetAsync(Arg.Any<string>(), Arg.Any<LookupResult>(), Arg.Any<CancellationToken>());
    }

    private static IMetadataProvider MakeProvider(string name)
    {
        var p = Substitute.For<IMetadataProvider>();
        p.Name.Returns(name);
        p.CanHandle(Arg.Any<LookupQuery>()).Returns(true);
        p.LookupAsync(Arg.Any<LookupQuery>(), Arg.Any<CancellationToken>())
            .Returns(LookupResult.Empty);
        return p;
    }

    private static LookupRunner MakeRunner(params IMetadataProvider[] providers) =>
        new(providers, new LookupOptions { Providers = providers.Select(p => p.Name).ToList() }, cache: null,
            NullLogger<LookupRunner>.Instance);
}
