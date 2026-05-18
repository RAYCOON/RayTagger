using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using RayTagger.Core.Models;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Default <see cref="IMappingRuleEngine"/>. Walks the ruleset top-to-bottom, evaluates each
/// rule's <see cref="WhenClause"/> against the current state, and applies the <see cref="SetClause"/>
/// of every match. <see cref="MappingRule.OnMatch"/> (or the ruleset default) controls whether
/// evaluation stops on first match or continues for further refinement.
/// </summary>
/// <remarks>
/// All <c>set:</c> writes stamp the affected fields with <see cref="TagFieldSource.Rules"/>. That
/// source wins over <c>existing_tags_policy: skip_if_present</c> per the invariant documented in
/// <see cref="TagFieldSource"/> — rules express the user's explicit declarative intent.
/// </remarks>
public sealed class MappingRuleEngine : IMappingRuleEngine
{
    // Compile each (pattern, caseSensitive) combination at most once per engine lifetime — the
    // .NET implicit regex cache holds 15 entries by default and thrashes for medium-sized rulesets.
    // A scan of N tracks evaluates each rule's regex N times; one compile per ruleset reload is the
    // right tradeoff. ConcurrentDictionary because rule evaluation runs from multiple pipeline
    // workers once the parallel pipeline lands.
    private readonly ConcurrentDictionary<(string Pattern, bool CaseSensitive), Regex> _regexCache = new();

    public MappingEvaluationResult Evaluate(
        ResolvedTrackTags tags,
        TrackTags? existing,
        TrackFile file,
        MappingRuleSet ruleset)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(ruleset);

        var context = new EvaluationContext(tags, existing, file, ruleset.Defaults);
        var applied = new List<MappingRuleHit>();

        foreach (var rule in ruleset.Rules)
        {
            if (!Matches(rule.When, context))
            {
                continue;
            }

            var (newTags, changedFields) = Apply(rule.Set, context.Tags);
            context = context with { Tags = newTags };
            applied.Add(new MappingRuleHit(rule.Name, changedFields));

            if (rule.EffectiveOnMatch(ruleset.Defaults) == OnMatch.Stop)
            {
                break;
            }
        }

        return new MappingEvaluationResult(context.Tags, applied);
    }

    // -------- Predicate evaluation --------------------------------------------------------------

    private bool Matches(WhenClause? when, EvaluationContext ctx)
    {
        // Both "key omitted" and "when: {}" are the catch-all form.
        if (when is null || IsEmptyClause(when))
        {
            return true;
        }

        if (when.Genre is not null && !MatchesGenrePattern(when.Genre, ctx.Tags.Genre.Value, ctx)) return false;
        if (when.Subgenre is not null && !MatchesGenrePattern(when.Subgenre, ctx.Tags.SubGenre.Value, ctx)) return false;
        if (when.GenreRegex is not null && !MatchesRegex(when.GenreRegex, ctx.Tags.Genre.Value, ctx)) return false;
        if (when.SubgenreRegex is not null && !MatchesRegex(when.SubgenreRegex, ctx.Tags.SubGenre.Value, ctx)) return false;

        if (when.Bpm is not null && !MatchesRange(when.Bpm, ctx.Tags.Bpm.Value)) return false;
        if (when.Energy is not null && !MatchesRange(when.Energy, ctx.Tags.Energy.Value)) return false;

        if (when.Key.Count > 0 && !MatchesKey(when.Key, ctx.Tags.Key.Value, ctx)) return false;
        if (when.KeyMode is not null && !MatchesKeyMode(when.KeyMode.Value, ctx.Tags.Key.Value)) return false;

        if (when.Artist.Count > 0 && !MatchesLiteral(when.Artist, ctx.Existing?.Artist, ctx)) return false;
        if (when.ArtistRegex is not null && !MatchesRegex(when.ArtistRegex, ctx.Existing?.Artist, ctx)) return false;

        if (when.PathGlob is not null && !MatchesPathGlob(when.PathGlob, ctx.File.Path)) return false;

        if (when.AnyOf.Count > 0 && !when.AnyOf.Any(child => Matches(child, ctx))) return false;
        if (when.AllOf.Count > 0 && !when.AllOf.All(child => Matches(child, ctx))) return false;
        if (when.Not is not null && Matches(when.Not, ctx)) return false;

        return true;
    }

    private static bool IsEmptyClause(WhenClause w) =>
        w.Genre is null && w.Subgenre is null
        && w.GenreRegex is null && w.SubgenreRegex is null
        && w.Bpm is null && w.Energy is null
        && w.Key.Count == 0 && w.KeyMode is null
        && w.Artist.Count == 0 && w.ArtistRegex is null
        && w.PathGlob is null
        && w.AnyOf.Count == 0 && w.AllOf.Count == 0 && w.Not is null;

    private static bool MatchesGenrePattern(GenrePattern pattern, string? value, EvaluationContext ctx)
    {
        if (pattern.MatchMissing)
        {
            return string.IsNullOrEmpty(value);
        }
        if (pattern.MatchAny)
        {
            return !string.IsNullOrEmpty(value);
        }
        return MatchesLiteral(pattern.Values, value, ctx);
    }

    private static bool MatchesLiteral(IReadOnlyList<string> candidates, string? value, EvaluationContext ctx)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var comparison = ctx.Defaults.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        foreach (var candidate in candidates)
        {
            if (value.Equals(candidate, comparison)) return true;
        }
        return false;
    }

    private bool MatchesRegex(string pattern, string? value, EvaluationContext ctx)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var compiled = _regexCache.GetOrAdd((pattern, ctx.Defaults.CaseSensitive), key =>
        {
            var options = key.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            // Pattern is user-supplied: a malicious pattern could ReDoS. Cap at a small timeout —
            // a mapping rule is supposed to be cheap, never milliseconds. Compiled because each
            // rule evaluates N-tracks times per scan.
            return new Regex(key.Pattern, options | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100));
        });
        return compiled.IsMatch(value);
    }

    private static bool MatchesRange(NumericRange range, double? value)
    {
        // Skip the rule (no false match) when the underlying signal is missing — this is the
        // documented semantics in ARCHITECTURE.md §5.2.
        return value.HasValue && value.Value >= range.Min && value.Value <= range.Max;
    }

    private static bool MatchesRange(NumericRange range, int? value)
    {
        return value.HasValue && value.Value >= range.Min && value.Value <= range.Max;
    }

    private static bool MatchesKey(IReadOnlyList<string> candidates, MusicalKey? key, EvaluationContext ctx)
    {
        if (key is null) return false;
        var comparison = ctx.Defaults.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        foreach (var candidate in candidates)
        {
            if (key.Standard.Equals(candidate, comparison)) return true;
            if (key.Camelot.Equals(candidate, comparison)) return true;
        }
        return false;
    }

    private static bool MatchesKeyMode(KeyMode required, MusicalKey? key)
    {
        if (key is null) return false;
        // Camelot suffix is the authoritative mode marker: A = minor wheel, B = major wheel.
        var isMinor = key.Camelot.EndsWith('A') || key.Camelot.EndsWith('a');
        return required == KeyMode.Minor ? isMinor : !isMinor;
    }

    private bool MatchesPathGlob(string glob, string path)
    {
        // Microsoft.Extensions.FileSystemGlobbing.Matcher is anchored to a base directory and
        // doesn't play nicely with absolute paths in unit tests. Translate the glob to a plain
        // regex instead — deterministic, no FS dependency. Cache the compiled regex so a scan
        // of N tracks doesn't re-compile the same glob N times.
        var pattern = GlobToRegex(glob);
        var compiled = _regexCache.GetOrAdd((pattern, CaseSensitive: false), key =>
            new Regex(key.Pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100)));
        var normalised = path.Replace('\\', '/');
        return compiled.IsMatch(normalised);
    }

    private static string GlobToRegex(string glob)
    {
        // Translate shell-style globs to regex:
        //   `**`  → any sequence (including slashes)
        //   `*`   → any sequence except slash
        //   `?`   → one char except slash
        //   other → escaped literal
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i += 2;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }

    // -------- Set-clause application ------------------------------------------------------------

    private static (ResolvedTrackTags Tags, IReadOnlyList<string> Changed) Apply(SetClause set, ResolvedTrackTags tags)
    {
        var changed = new List<string>();
        var newGenre = tags.Genre;
        var newSubGenre = tags.SubGenre;

        if (set.Genre is not null)
        {
            newGenre = new ResolvedField<string>(NormaliseSetValue(set.Genre), TagFieldSource.Rules, 1.0);
            changed.Add("genre");
        }
        if (set.Subgenre is not null)
        {
            newSubGenre = new ResolvedField<string>(NormaliseSetValue(set.Subgenre), TagFieldSource.Rules, 1.0);
            changed.Add("subgenre");
        }

        IReadOnlyDictionary<string, ResolvedField<string>> newCustom = tags.Custom;
        if (set.ExtraTags.Count > 0 || set.AddKeyword is not null)
        {
            var mutable = new Dictionary<string, ResolvedField<string>>(tags.Custom, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in set.ExtraTags)
            {
                mutable[key] = new ResolvedField<string>(NormaliseSetValue(value), TagFieldSource.Rules, 1.0);
                changed.Add($"tag.{key}");
            }
            if (set.AddKeyword is not null)
            {
                mutable["keywords"] = AppendKeyword(mutable, "keywords", set.AddKeyword);
                changed.Add("tag.keywords");
            }
            newCustom = mutable;
        }

        return (tags with { Genre = newGenre, SubGenre = newSubGenre, Custom = newCustom }, changed);
    }

    private static string? NormaliseSetValue(string raw) =>
        // Empty string in YAML means "clear the field"; otherwise pass through as written.
        raw.Length == 0 ? null : raw;

    private static ResolvedField<string> AppendKeyword(
        Dictionary<string, ResolvedField<string>> custom, string field, string keyword)
    {
        if (custom.TryGetValue(field, out var existing) && !string.IsNullOrEmpty(existing.Value))
        {
            return new ResolvedField<string>($"{existing.Value};{keyword}", TagFieldSource.Rules, 1.0);
        }
        return new ResolvedField<string>(keyword, TagFieldSource.Rules, 1.0);
    }

    // -------- Internal evaluation context -------------------------------------------------------

    private sealed record EvaluationContext(
        ResolvedTrackTags Tags,
        TrackTags? Existing,
        TrackFile File,
        MappingDefaults Defaults);
}
