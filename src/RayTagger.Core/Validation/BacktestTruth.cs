namespace RayTagger.Core.Validation;

/// <summary>
/// Ground-truth values for a single track in the backtest reference library. Genre/SubGenre come
/// from the folder structure (<c>./music/Tagged/&lt;Genre&gt;/[&lt;SubGenre&gt;/]&lt;file&gt;</c>);
/// BPM/Key/Energy come from the Mixed-In-Key comment-tag, with genre-specific BPM correction
/// applied (see <see cref="MixedInKeyCommentParser.ApplyGenreCorrection"/>).
/// </summary>
/// <remarks>
/// All fields are nullable because the backtest tolerates partial truth — a file might sit in a
/// genre subfolder but lack a parseable MIK comment. Per-dimension metrics skip files where the
/// respective truth field is null rather than treating it as a "miss".
/// </remarks>
public sealed record BacktestTruth(
    string FilePath,
    string Genre,
    string? SubGenre,
    double? Bpm,
    string? CamelotKey,
    int? Energy,
    bool BpmWasCorrected)
{
    /// <summary>
    /// Optional BPM from a secondary truth root (e.g. Virtual DJ's TBPM frame). Used by the
    /// validate harness when <c>--reference-vdj</c> is supplied — a track is considered a BPM
    /// match if either the primary (MIK) or secondary (VDJ) truth matches the prediction.
    /// </summary>
    public double? SecondaryBpm { get; init; }

    /// <summary>
    /// Optional Camelot key from a secondary truth root. Standard-notation input (e.g. <c>Ebm</c>)
    /// is normalised to Camelot via <see cref="Models.KeyNotationConverter"/> before storage so
    /// enharmonic spellings (Ebm/D#m) collapse to the same Camelot slot.
    /// </summary>
    public string? SecondaryCamelotKey { get; init; }
}
