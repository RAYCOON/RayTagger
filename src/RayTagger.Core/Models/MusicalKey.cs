namespace RayTagger.Core.Models;

/// <summary>
/// A musical key in both standard (e.g. "Am", "F#m") and Camelot Wheel notation (e.g. "8A", "5B").
/// Both representations are carried together because the write stage emits to different tag frames
/// per format (TKEY = standard, TXXX:CAMELOTKEY = camelot — see docs/ARCHITECTURE.md §6.1).
/// </summary>
public sealed record MusicalKey(string Standard, string Camelot);
