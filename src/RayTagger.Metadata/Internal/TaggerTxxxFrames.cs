namespace RayTagger.Metadata.Internal;

/// <summary>
/// Canonical names for the user-defined ID3v2 frames (<c>TXXX</c>) and Vorbis comment fields that
/// Tagger uses for the dimensions outside the ID3 standard tag set (Camelot key, energy, sub-genre).
/// Centralised here so the read/write paths can't drift. See docs/ARCHITECTURE.md §6.1 for the full
/// tag-frame matrix.
/// </summary>
internal static class TaggerTxxxFrames
{
    public const string CamelotKey = "CAMELOTKEY";
    public const string EnergyLevel = "ENERGYLEVEL";
    public const string SubGenre = "SUBGENRE";
}
