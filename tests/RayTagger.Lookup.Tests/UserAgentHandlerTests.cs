using RayTagger.Hosting;

namespace RayTagger.Lookup.Tests;

public class UserAgentHandlerTests
{
    [Fact]
    public async Task Stamps_state_value_on_outgoing_request()
    {
        var state = new UserAgentState();
        state.SetContact("test@example.com");
        var captured = new CapturingHandler();
        using var handler = new UserAgentHandler(state) { InnerHandler = captured };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        captured.LastUserAgent.Should().EndWith("( test@example.com )");
    }

    [Fact]
    public async Task Picks_up_state_changes_between_requests()
    {
        // The handler is registered transient but reads from a singleton state, so a
        // mid-session SetContact must affect subsequent requests on the same handler too.
        var state = new UserAgentState();
        var captured = new CapturingHandler();
        using var handler = new UserAgentHandler(state) { InnerHandler = captured };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        state.SetContact("first@example.com");
        using (var r1 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/a"))
        {
            (await invoker.SendAsync(r1, CancellationToken.None)).Dispose();
        }
        var firstUa = captured.LastUserAgent;

        state.SetContact("second@example.com");
        using (var r2 = new HttpRequestMessage(HttpMethod.Get, "https://example.com/b"))
        {
            (await invoker.SendAsync(r2, CancellationToken.None)).Dispose();
        }
        var secondUa = captured.LastUserAgent;

        firstUa.Should().EndWith("( first@example.com )");
        secondUa.Should().EndWith("( second@example.com )");
    }

    [Fact]
    public async Task Does_not_stack_entries_when_request_is_replayed()
    {
        // Polly's retry policy hands the same request back to the handler on each attempt.
        // Without the Clear() in UserAgentHandler the UA collection would grow with every replay.
        // RFC 7231 parses "Name/Ver ( contact )" into 2 ProductInfoHeaderValue entries (product +
        // comment), so the stable post-Clear count is 2 regardless of how many times we replay.
        var state = new UserAgentState();
        state.SetContact("retry@example.com");
        var captured = new CapturingHandler();
        using var handler = new UserAgentHandler(state) { InnerHandler = captured };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        var afterFirst = captured.LastUserAgentCount;
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        (await invoker.SendAsync(request, CancellationToken.None)).Dispose();
        var afterThird = captured.LastUserAgentCount;

        afterFirst.Should().Be(2);
        afterThird.Should().Be(afterFirst, "Clear() must keep the collection size stable across replays");
    }

    [Fact]
    public void Constructor_rejects_null_state()
    {
        var act = () => new UserAgentHandler(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void State_default_uses_factory_default_contact()
    {
        var state = new UserAgentState();

        state.Current.Should().Contain($"( {UserAgentFactory.DefaultContact} )");
    }

    [Fact]
    public void State_setting_null_restores_default()
    {
        var state = new UserAgentState();
        state.SetContact("custom@example.com");
        state.SetContact(null);

        state.Current.Should().Contain($"( {UserAgentFactory.DefaultContact} )");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastUserAgent { get; private set; }
        public int LastUserAgentCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUserAgentCount = request.Headers.UserAgent.Count;
            LastUserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
