using RayTagger.Core.Models;

namespace RayTagger.Ui.ViewModels;

/// <summary>
/// Outcome of <see cref="RuleEditorViewModel.TestAgainstFileAsync"/> — what the rule engine would
/// produce for a single user-picked file given the *current* editor buffer (parsed in-memory, not
/// necessarily the version on disk). Mirrors the CLI <c>explain</c> verb's report shape so the
/// UI dialog and CLI table show the user the same information.
/// </summary>
/// <remarks>
/// <see cref="ErrorMessage"/> is non-null when the test couldn't run end-to-end — invalid YAML in
/// the buffer, unreadable audio file, etc. The dialog renders the error in place of the tables.
/// </remarks>
public sealed record RuleTestResult(
    string FilePath,
    string FileName,
    TrackTags? Existing,
    IReadOnlyList<MappingRuleHit> Applied,
    ResolvedTrackTags? Final,
    string? ErrorMessage = null)
{
    public bool HasError => ErrorMessage is not null;
    public bool AnyRuleMatched => Applied.Count > 0;
}
