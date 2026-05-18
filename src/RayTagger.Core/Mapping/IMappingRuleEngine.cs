using RayTagger.Core.Models;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Evaluates a <see cref="MappingRuleSet"/> against a track's resolved tags and emits a possibly
/// mutated <see cref="ResolvedTrackTags"/> plus the list of rules that fired (in evaluation order).
/// </summary>
/// <remarks>
/// Determinism is part of the contract: identical inputs must always produce identical outputs.
/// No random tie-breaking, no implicit IO. The engine sees only the data it's handed.
/// </remarks>
public interface IMappingRuleEngine
{
    MappingEvaluationResult Evaluate(
        ResolvedTrackTags tags,
        TrackTags? existing,
        TrackFile file,
        MappingRuleSet ruleset);
}

/// <summary>
/// Outcome of evaluating a ruleset for one track. <see cref="Tags"/> is the new resolved state
/// (a fresh <see cref="ResolvedTrackTags"/> record — the input is not mutated); <see cref="Applied"/>
/// captures which rules fired and which logical field names they touched, in evaluation order.
/// </summary>
public sealed record MappingEvaluationResult(
    ResolvedTrackTags Tags,
    IReadOnlyList<MappingRuleHit> Applied);
