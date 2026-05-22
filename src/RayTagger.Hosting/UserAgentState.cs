namespace RayTagger.Hosting;

/// <summary>
/// Shared, mutable holder for the current outbound <c>User-Agent</c>. Registered as a
/// singleton so every <see cref="UserAgentHandler"/> instance (one per named
/// <see cref="HttpClient"/>) sees the same value. The host updates this once at scan
/// startup with the loaded <c>lookup.user_agent_contact</c>; the handlers then stamp
/// every outgoing request with the current string.
/// </summary>
/// <remarks>
/// Why a holder and not <c>DefaultRequestHeaders</c>: <see cref="IHttpClientFactory"/>
/// caches handler chains per name, so headers configured at composition time stick to
/// the first contact value seen. A holder + per-request handler lets the user change
/// the contact via config without rebuilding the DI container.
/// </remarks>
public sealed class UserAgentState
{
    private volatile string _current = UserAgentFactory.Build(null);

    /// <summary>The full UA string used by every Tagger HTTP client.</summary>
    public string Current => _current;

    /// <summary>
    /// Recomputes the UA from the given contact and publishes it to all handlers.
    /// Callers invoke this once after loading <see cref="Core.Configuration.LookupOptions"/>;
    /// passing <c>null</c> or whitespace restores the default contact.
    /// </summary>
    public void SetContact(string? contact) => _current = UserAgentFactory.Build(contact);
}
