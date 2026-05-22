namespace RayTagger.Hosting;

/// <summary>
/// <see cref="DelegatingHandler"/> that stamps every outbound request with the current
/// <see cref="UserAgentState.Current"/>. Wired into every named <see cref="HttpClient"/>
/// via <c>AddHttpMessageHandler&lt;UserAgentHandler&gt;()</c>; the singleton
/// <see cref="UserAgentState"/> ensures every handler instance shares the same value.
/// </summary>
public sealed class UserAgentHandler : DelegatingHandler
{
    private readonly UserAgentState _state;

    public UserAgentHandler(UserAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Clear first so a re-issue from the resilience pipeline doesn't stack identical
        // entries. `ParseAdd` validates the UA against RFC 7231 — a malformed string
        // (e.g. a config typo) surfaces as a FormatException at request time, not silently.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_state.Current);

        return base.SendAsync(request, cancellationToken);
    }
}
