namespace RayTagger.Metadata.Internal;

/// <summary>
/// Default names for the user-defined ID3v2 frames (<c>TXXX</c>) and Vorbis comment fields that
/// Tagger uses for the dimensions outside the ID3 standard tag set (Camelot key, energy,
/// sub-genre). Centralised here so the legacy read path can't drift before <c>TagFieldMap</c>
/// arrived. Live writes go through <see cref="RayTagger.Core.Configuration.TagFieldMap"/> which
/// the user can override via <c>write.tag_fields</c> in <c>tagger.yaml</c>.
/// See docs/ARCHITECTURE.md §6.1 for the full tag-frame matrix.
/// </summary>
internal static class TaggerTxxxFrames
{
    public const string CamelotKey = "CAMELOTKEY";
    public const string EnergyLevel = "ENERGYLEVEL";
    public const string SubGenre = "SUBGENRE";
}
