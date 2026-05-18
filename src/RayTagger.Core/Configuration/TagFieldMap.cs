namespace RayTagger.Core.Configuration;

/// <summary>
/// Parsed view of the <c>write.tag_fields</c> block. Each logical dimension carries the actual
/// frame names (per format) Tagger should read/write. Built from
/// <see cref="WriteOptions.TagFields"/> at config load time so the writer doesn't re-parse the
/// raw token strings per file.
/// </summary>
/// <remarks>
/// What is and isn't user-overridable:
/// <list type="bullet">
///   <item><b>Genre / BPM / Key (standard) / Year / Title / Artist</b> map to spec-mandated
///         ID3v2 frames (TCON, TBPM, TKEY, TDRC, TIT2, TPE1) or unambiguous Vorbis fields
///         (GENRE, BPM, INITIALKEY). There's nothing to override; the example YAML lists
///         them for documentation only.</item>
///   <item><b>Sub-genre, Camelot key, Energy</b> live in user-defined frames
///         (<c>TXXX:&lt;description&gt;</c> / Vorbis comment fields). The description here is
///         the convention — Traktor / Beatport / various DJ tools each use slightly different
///         names. <c>tag_fields:</c> lets users override these to match their library.</item>
/// </list>
/// </remarks>
public sealed record TagFieldMap
{
    public string SubGenreId3Description { get; init; } = "SUBGENRE";
    public string SubGenreVorbisField { get; init; } = "SUBGENRE";

    public string CamelotKeyId3Description { get; init; } = "CAMELOTKEY";
    public string CamelotKeyVorbisField { get; init; } = "CAMELOTKEY";

    public string EnergyLevelId3Description { get; init; } = "ENERGYLEVEL";
    public string EnergyLevelVorbisField { get; init; } = "ENERGYLEVEL";

    public static TagFieldMap Default { get; } = new();
}
