namespace RayTagger.Core.Configuration;

/// <summary>
/// Mutation helpers over <see cref="TaggerOptions"/> that need to be reusable across CLI verbs
/// (scan, validate, future). Lives in Core so any consumer can apply the same overrides without
/// duplicating the magic-numbers list.
/// </summary>
public static class TaggerOptionsExtensions
{
    /// <summary>
    /// Zeroes every per-dimension <c>existing_confidence</c> (BPM, Key, Energy, Lookup). Effect:
    /// every usable analyzer/lookup hit overrides the corresponding existing tag, regardless of
    /// what the YAML configured. Used by the <c>--force-overwrite</c> CLI flag for one-off
    /// re-tagging runs and by <c>tagger validate</c> for backtest accuracy (where the existing
    /// Mixed-In-Key tags would otherwise pass through and inflate the match rate to 100 %).
    /// </summary>
    /// <remarks>
    /// Mutates the passed instance in place — callers typically own the options object for the
    /// lifetime of a single command invocation, so an in-place mutation is the cheapest sane
    /// thing to do. Calling twice is a no-op (idempotent).
    /// </remarks>
    public static void ForceOverwriteExistingTags(this TaggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Analysis.Bpm.ExistingConfidence = 0.0;
        options.Analysis.Key.ExistingConfidence = 0.0;
        options.Analysis.Energy.ExistingConfidence = 0.0;
        options.Lookup.ExistingConfidence = 0.0;
    }
}
