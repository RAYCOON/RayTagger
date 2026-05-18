namespace RayTagger.Core.Mapping;

/// <summary>
/// Action block of a mapping rule. Empty string clears a field; a missing key leaves the
/// resolved field untouched. <see cref="ExtraTags"/> captures any <c>tag.&lt;name&gt;</c>
/// keys flattened into a dictionary by the loader.
/// </summary>
public sealed class SetClause
{
    public string? Genre { get; set; }
    public string? Subgenre { get; set; }

    /// <summary>Mood (sonic character) — e.g. Dark, Driving, Uplifting. Constrained to
    /// <c>taxonomy.moods</c> when <c>taxonomy.enforce: true</c>.</summary>
    public string? Mood { get; set; }

    /// <summary>Set-position label — e.g. Warm-up, Peak Time, Closing. Constrained to
    /// <c>taxonomy.set_positions</c> when enforcement is on.</summary>
    public string? SetPosition { get; set; }

    /// <summary>
    /// When true, the engine looks up the current resolved Genre in
    /// <c>taxonomy.normalise</c> and splits it into a canonical <c>(genre, subgenre)</c> pair.
    /// No-op if the taxonomy doesn't know the current value.
    /// </summary>
    public bool NormaliseGenre { get; set; }

    /// <summary>
    /// Arithmetic transform on the current resolved BPM. Lets a rule fix half-time / double-time
    /// mis-detections (Drum &amp; Bass tracks Essentia tags at 87 BPM, double them; old jungle
    /// at 340 BPM, halve them).
    /// </summary>
    public BpmTransform? BpmTransform { get; set; }

    /// <summary>Values for keys written as <c>tag.NAME: VALUE</c> in YAML.</summary>
    public Dictionary<string, string> ExtraTags { get; set; } = new(StringComparer.Ordinal);

    public string? AddKeyword { get; set; }
}

/// <summary>Arithmetic transformations available to <see cref="SetClause.BpmTransform"/>.</summary>
/// <remarks>
/// CA1720 flags <c>Double</c> as colliding with the System.Double type name — but it's the term
/// of art in DJ workflows ("double the BPM"), and the enum context disambiguates fully. Suppress
/// the warning rather than rename to a less-readable <c>DoubleBpm</c>.
/// </remarks>
#pragma warning disable CA1720
public enum BpmTransform
{
    None,
    Double,
    Half,
}
#pragma warning restore CA1720
