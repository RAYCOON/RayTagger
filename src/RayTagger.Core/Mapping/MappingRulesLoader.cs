using System.Globalization;
using System.Text.RegularExpressions;
using RayTagger.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Loads <c>mappings.yaml</c> into a <see cref="MappingRuleSet"/>. Walks YamlDotNet's generic
/// object graph manually so we can:
/// <list type="bullet">
///   <item>Handle the polymorphic <c>genre</c> field (string / sentinel / list).</item>
///   <item>Flatten <c>tag.NAME</c> keys into <see cref="SetClause.ExtraTags"/>.</item>
///   <item>Report errors with precise YAML paths (e.g. <c>rules[3].when.bpm.min</c>).</item>
/// </list>
/// </summary>
public static class MappingRulesLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static MappingRuleSet Load(string path, Taxonomy? taxonomy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Mapping rules file not found: {fullPath}");
        }

        var yaml = File.ReadAllText(fullPath);
        return LoadFromString(yaml, fullPath, taxonomy);
    }

    /// <summary>
    /// In-memory variant for tests and callers that already have the YAML text. When
    /// <paramref name="taxonomy"/> is supplied and <c>taxonomy.Enforce</c> is true, every
    /// <c>set: { genre|subgenre|mood|set_position: X }</c> value is checked against the
    /// allowlist and rejected at load time.
    /// </summary>
    public static MappingRuleSet LoadFromString(string yaml, string sourceDescription = "(in-memory)", Taxonomy? taxonomy = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        object? rootNode;
        try
        {
            rootNode = Deserializer.Deserialize<object?>(yaml);
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException(
                $"YAML parsing failed for {sourceDescription} (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}",
                ex);
        }

        if (rootNode is not IDictionary<object, object?> root)
        {
            throw new ConfigurationException(
                $"Mapping rules file root must be a mapping: {sourceDescription}");
        }

        var errors = new List<ConfigurationError>();
        var ruleSet = BuildRuleSet(root, errors);

        if (taxonomy is { Enforce: true })
        {
            EnforceTaxonomy(ruleSet, taxonomy, errors);
        }

        if (errors.Count > 0)
        {
            throw new ConfigurationException(
                $"Mapping rules validation failed for {sourceDescription}.",
                errors);
        }

        return ruleSet;
    }

    /// <summary>
    /// When <c>taxonomy.enforce</c> is true, every literal in a <c>set:</c> block is checked
    /// against the corresponding allowlist. Empty strings (clear-the-field intent) pass through.
    /// Honours <c>defaults.case_sensitive</c> so taxonomy validation matches the engine's runtime
    /// comparison policy.
    /// </summary>
    private static void EnforceTaxonomy(MappingRuleSet ruleSet, Taxonomy taxonomy, List<ConfigurationError> errors)
    {
        var comparer = ruleSet.Defaults.CaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var genreSet = new HashSet<string>(taxonomy.Genres, comparer);
        var moodSet = new HashSet<string>(taxonomy.Moods, comparer);
        var setPositionSet = new HashSet<string>(taxonomy.SetPositions, comparer);

        for (var i = 0; i < ruleSet.Rules.Count; i++)
        {
            var rule = ruleSet.Rules[i];
            var prefix = $"rules[{i}].set";
            if (!string.IsNullOrEmpty(rule.Set.Genre) && !genreSet.Contains(rule.Set.Genre))
            {
                errors.Add(new ConfigurationError($"{prefix}.genre",
                    $"Genre '{rule.Set.Genre}' is not declared in the taxonomy."));
            }
            if (!string.IsNullOrEmpty(rule.Set.Subgenre) && !string.IsNullOrEmpty(rule.Set.Genre))
            {
                if (taxonomy.Subgenres.TryGetValue(rule.Set.Genre, out var allowed)
                    && !allowed.Contains(rule.Set.Subgenre, comparer))
                {
                    errors.Add(new ConfigurationError($"{prefix}.subgenre",
                        $"Sub-genre '{rule.Set.Subgenre}' is not declared under genre '{rule.Set.Genre}'."));
                }
            }
            if (!string.IsNullOrEmpty(rule.Set.Mood) && !moodSet.Contains(rule.Set.Mood))
            {
                errors.Add(new ConfigurationError($"{prefix}.mood",
                    $"Mood '{rule.Set.Mood}' is not declared in taxonomy.moods."));
            }
            if (!string.IsNullOrEmpty(rule.Set.SetPosition) && !setPositionSet.Contains(rule.Set.SetPosition))
            {
                errors.Add(new ConfigurationError($"{prefix}.set_position",
                    $"Set position '{rule.Set.SetPosition}' is not declared in taxonomy.set_positions."));
            }
        }
    }

    private static readonly string[] KnownRootKeys = ["version", "defaults", "rules"];
    private static readonly string[] KnownDefaultsKeys = ["on_match", "case_sensitive"];
    private static readonly string[] KnownRuleKeys = ["name", "when", "set", "on_match"];

    private static MappingRuleSet BuildRuleSet(IDictionary<object, object?> root, List<ConfigurationError> errors)
    {
        var result = new MappingRuleSet();

        CheckUnknownKeys(root, KnownRootKeys, "", errors);

        if (TryGetInt(root, "version", out var version))
        {
            result.Version = version;
            if (version != 1)
            {
                errors.Add(new ConfigurationError("version", $"Only version 1 is supported, found {version}."));
            }
        }

        if (TryGetMap(root, "defaults", out var defaultsMap) && defaultsMap is not null)
        {
            result.Defaults = BuildDefaults(defaultsMap, "defaults", errors);
        }

        if (TryGetSequence(root, "rules", out var rulesSeq) && rulesSeq is not null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rulesSeq.Count; i++)
            {
                if (rulesSeq[i] is not IDictionary<object, object?> ruleMap)
                {
                    errors.Add(new ConfigurationError($"rules[{i}]", "Rule must be a mapping."));
                    continue;
                }

                var rule = BuildRule(ruleMap, $"rules[{i}]", errors);
                if (!string.IsNullOrWhiteSpace(rule.Name) && !names.Add(rule.Name))
                {
                    errors.Add(new ConfigurationError(
                        $"rules[{i}].name",
                        $"Duplicate rule name '{rule.Name}'."));
                }
                result.Rules.Add(rule);
            }
        }

        return result;
    }

    private static MappingDefaults BuildDefaults(IDictionary<object, object?> map, string pathPrefix, List<ConfigurationError> errors)
    {
        CheckUnknownKeys(map, KnownDefaultsKeys, pathPrefix, errors);

        var defaults = new MappingDefaults();
        if (TryGetString(map, "on_match", out var onMatch))
        {
            defaults.OnMatch = ParseOnMatch(onMatch, $"{pathPrefix}.on_match", errors);
        }
        if (TryGetBool(map, "case_sensitive", out var caseSensitive))
        {
            defaults.CaseSensitive = caseSensitive;
        }
        return defaults;
    }

    private static void CheckUnknownKeys(
        IDictionary<object, object?> map,
        IReadOnlyCollection<string> known,
        string pathPrefix,
        List<ConfigurationError> errors)
    {
        foreach (var rawKey in map.Keys)
        {
            if (rawKey is not string key)
            {
                errors.Add(new ConfigurationError(
                    pathPrefix.Length == 0 ? "(root)" : pathPrefix,
                    $"Non-string key: '{rawKey}'."));
                continue;
            }
            if (!known.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                var qualified = pathPrefix.Length == 0 ? key : $"{pathPrefix}.{key}";
                errors.Add(new ConfigurationError(
                    qualified,
                    $"Unknown key '{key}'. Allowed: {string.Join(", ", known)}."));
            }
        }
    }

    private static MappingRule BuildRule(IDictionary<object, object?> map, string pathPrefix, List<ConfigurationError> errors)
    {
        CheckUnknownKeys(map, KnownRuleKeys, pathPrefix, errors);

        var rule = new MappingRule();

        if (TryGetString(map, "name", out var name))
        {
            rule.Name = name;
        }
        else
        {
            errors.Add(new ConfigurationError($"{pathPrefix}.name", "Rule name is required."));
        }

        if (map.TryGetValue("when", out var whenValue))
        {
            rule.When = BuildWhenClause(whenValue, $"{pathPrefix}.when", errors);
        }

        if (TryGetMap(map, "set", out var setMap) && setMap is not null)
        {
            rule.Set = BuildSetClause(setMap, $"{pathPrefix}.set", errors);
        }

        if (TryGetString(map, "on_match", out var onMatch))
        {
            rule.OnMatch = ParseOnMatch(onMatch, $"{pathPrefix}.on_match", errors);
        }

        return rule;
    }

    /// <summary>
    /// Validates regex syntax at load time so a user's typo surfaces immediately with a YAML path,
    /// rather than later as a runtime exception during evaluation.
    /// </summary>
    private static void ValidateRegex(string? pattern, string yamlPath, List<ConfigurationError> errors)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return;
        }
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            errors.Add(new ConfigurationError(yamlPath, $"Invalid regex: {ex.Message}"));
        }
    }

    private static WhenClause? BuildWhenClause(object? rawValue, string pathPrefix, List<ConfigurationError> errors)
    {
        if (rawValue is null)
        {
            return new WhenClause();  // catch-all
        }
        if (rawValue is not IDictionary<object, object?> map)
        {
            errors.Add(new ConfigurationError(pathPrefix, "Expected a mapping or null (catch-all)."));
            return null;
        }

        var when = new WhenClause();

        if (map.TryGetValue("genre", out var genreVal))
        {
            when.Genre = ParseGenrePattern(genreVal, $"{pathPrefix}.genre", errors);
        }
        if (TryGetString(map, "genre_regex", out var genreRegex))
        {
            when.GenreRegex = genreRegex;
            ValidateRegex(genreRegex, $"{pathPrefix}.genre_regex", errors);
        }

        if (map.TryGetValue("subgenre", out var subgenreVal))
        {
            when.Subgenre = ParseGenrePattern(subgenreVal, $"{pathPrefix}.subgenre", errors);
        }
        if (TryGetString(map, "subgenre_regex", out var subgenreRegex))
        {
            when.SubgenreRegex = subgenreRegex;
            ValidateRegex(subgenreRegex, $"{pathPrefix}.subgenre_regex", errors);
        }

        if (TryGetMap(map, "bpm", out var bpmMap) && bpmMap is not null)
        {
            when.Bpm = ParseRange(bpmMap, $"{pathPrefix}.bpm", errors);
        }

        if (map.TryGetValue("key", out var keyVal))
        {
            when.Key = ParseStringList(keyVal, $"{pathPrefix}.key", errors);
        }

        if (TryGetString(map, "key_mode", out var keyMode))
        {
            when.KeyMode = ParseKeyMode(keyMode, $"{pathPrefix}.key_mode", errors);
        }

        if (TryGetMap(map, "energy", out var energyMap) && energyMap is not null)
        {
            when.Energy = ParseRange(energyMap, $"{pathPrefix}.energy", errors);
        }

        if (map.TryGetValue("artist", out var artistVal))
        {
            when.Artist = ParseStringList(artistVal, $"{pathPrefix}.artist", errors);
        }
        if (TryGetString(map, "artist_regex", out var artistRegex))
        {
            when.ArtistRegex = artistRegex;
            ValidateRegex(artistRegex, $"{pathPrefix}.artist_regex", errors);
        }

        if (TryGetString(map, "path_glob", out var pathGlob))
        {
            when.PathGlob = pathGlob;
        }

        if (map.TryGetValue("any_of", out var anyOfVal))
        {
            when.AnyOf = ParseWhenList(anyOfVal, $"{pathPrefix}.any_of", errors);
        }
        if (map.TryGetValue("all_of", out var allOfVal))
        {
            when.AllOf = ParseWhenList(allOfVal, $"{pathPrefix}.all_of", errors);
        }
        if (map.TryGetValue("not", out var notVal))
        {
            when.Not = BuildWhenClause(notVal, $"{pathPrefix}.not", errors);
        }

        CheckUnknownKeys(map, KnownWhenKeys, pathPrefix, errors);

        return when;
    }

    private static readonly string[] KnownWhenKeys =
    [
        "genre", "genre_regex", "subgenre", "subgenre_regex",
        "bpm", "key", "key_mode", "energy",
        "artist", "artist_regex", "path_glob",
        "any_of", "all_of", "not",
    ];

    private static GenrePattern? ParseGenrePattern(object? value, string pathPrefix, List<ConfigurationError> errors)
    {
        switch (value)
        {
            case null:
                return null;
            case string s when s == GenrePattern.MissingSentinel:
                return GenrePattern.Missing;
            case string s when s == GenrePattern.AnySentinel:
                return GenrePattern.Any;
            case string s when s.StartsWith(':') && s.EndsWith(':'):
                errors.Add(new ConfigurationError(
                    pathPrefix,
                    $"Unknown sentinel '{s}'. Allowed: '{GenrePattern.MissingSentinel}', '{GenrePattern.AnySentinel}'."));
                return null;
            case string s:
                return GenrePattern.FromLiterals([s]);
            case IList<object?> list:
                var values = new List<string>(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is string item)
                    {
                        // Catch the common footgun: `genre: [":any:"]`. Sentinels are only
                        // meaningful as a scalar (`genre: ":any:"`); inside a list they would be
                        // taken as a literal genre named ":any:" and silently never match.
                        if (item.Length > 0 && item[0] == ':' && item[^1] == ':')
                        {
                            errors.Add(new ConfigurationError(
                                $"{pathPrefix}[{i}]",
                                $"Sentinel '{item}' cannot appear in a list. Use `{pathPrefix.Split('.')[^1]}: \"{item}\"` (scalar) instead."));
                            continue;
                        }
                        values.Add(item);
                    }
                    else
                    {
                        errors.Add(new ConfigurationError(
                            $"{pathPrefix}[{i}]",
                            "Expected a string."));
                    }
                }
                return GenrePattern.FromLiterals(values);
            default:
                errors.Add(new ConfigurationError(
                    pathPrefix,
                    "Expected a string, sentinel (:missing:/:any:), or list of strings."));
                return null;
        }
    }

    private static NumericRange? ParseRange(IDictionary<object, object?> map, string pathPrefix, List<ConfigurationError> errors)
    {
        var hasMin = TryGetDouble(map, "min", out var min);
        var hasMax = TryGetDouble(map, "max", out var max);

        if (!hasMin || !hasMax)
        {
            errors.Add(new ConfigurationError(
                pathPrefix,
                "Both 'min' and 'max' are required."));
            return null;
        }

        if (min > max)
        {
            errors.Add(new ConfigurationError(
                pathPrefix,
                $"min ({min}) must be <= max ({max})."));
        }

        return new NumericRange { Min = min, Max = max };
    }

    private static List<string> ParseStringList(object? value, string pathPrefix, List<ConfigurationError> errors)
    {
        switch (value)
        {
            case null:
                return [];
            case string single:
                return [single];
            case IList<object?> list:
                var result = new List<string>(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is string item)
                    {
                        result.Add(item);
                    }
                    else
                    {
                        errors.Add(new ConfigurationError($"{pathPrefix}[{i}]", "Expected a string."));
                    }
                }
                return result;
            default:
                errors.Add(new ConfigurationError(pathPrefix, "Expected a string or list of strings."));
                return [];
        }
    }

    private static List<WhenClause> ParseWhenList(object? value, string pathPrefix, List<ConfigurationError> errors)
    {
        if (value is not IList<object?> list)
        {
            errors.Add(new ConfigurationError(pathPrefix, "Expected a list of when-clauses."));
            return [];
        }

        var result = new List<WhenClause>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var clause = BuildWhenClause(list[i], $"{pathPrefix}[{i}]", errors);
            if (clause is not null)
            {
                result.Add(clause);
            }
        }
        return result;
    }

    private static SetClause BuildSetClause(IDictionary<object, object?> map, string pathPrefix, List<ConfigurationError> errors)
    {
        var set = new SetClause();

        foreach (var entry in map)
        {
            var key = entry.Key as string ?? entry.Key.ToString() ?? string.Empty;
            switch (key)
            {
                case "genre":
                    set.Genre = entry.Value as string;
                    break;
                case "subgenre":
                    set.Subgenre = entry.Value as string;
                    break;
                case "mood":
                    set.Mood = entry.Value as string;
                    break;
                case "set_position":
                    set.SetPosition = entry.Value as string;
                    break;
                case "normalise_genre":
                    set.NormaliseGenre = ParseBoolish(entry.Value, $"{pathPrefix}.{key}", errors);
                    break;
                case "bpm_transform":
                    set.BpmTransform = ParseBpmTransform(entry.Value, $"{pathPrefix}.{key}", errors);
                    break;
                case "add_keyword":
                    set.AddKeyword = entry.Value as string;
                    break;
                default:
                    if (key.StartsWith("tag.", StringComparison.Ordinal))
                    {
                        var tagName = key[4..];
                        if (string.IsNullOrEmpty(tagName))
                        {
                            errors.Add(new ConfigurationError(
                                $"{pathPrefix}.{key}",
                                "Empty tag name after 'tag.' prefix."));
                        }
                        else if (entry.Value is string tagValue)
                        {
                            set.ExtraTags[tagName] = tagValue;
                        }
                        else
                        {
                            errors.Add(new ConfigurationError(
                                $"{pathPrefix}.{key}",
                                "Custom tag value must be a string."));
                        }
                    }
                    else
                    {
                        errors.Add(new ConfigurationError(
                            $"{pathPrefix}.{key}",
                            $"Unknown set key '{key}'. Allowed: genre, subgenre, mood, set_position, " +
                            "normalise_genre, bpm_transform, add_keyword, tag.<name>."));
                    }
                    break;
            }
        }

        return set;
    }

    private static bool ParseBoolish(object? value, string path, List<ConfigurationError> errors)
    {
        if (value is string s && bool.TryParse(s, out var parsed))
        {
            return parsed;
        }
        errors.Add(new ConfigurationError(path, $"Expected 'true' or 'false', got '{value}'."));
        return false;
    }

    private static BpmTransform? ParseBpmTransform(object? value, string path, List<ConfigurationError> errors)
    {
        if (value is not string s) return Record<BpmTransform?>(errors, path, "Expected 'double' or 'half'.", null);
        if (s.Equals("double", StringComparison.OrdinalIgnoreCase)) return BpmTransform.Double;
        if (s.Equals("half", StringComparison.OrdinalIgnoreCase)) return BpmTransform.Half;
        if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return BpmTransform.None;
        return Record<BpmTransform?>(errors, path, $"Expected 'double' or 'half', got '{s}'.", null);
    }

    private static OnMatch ParseOnMatch(string value, string path, List<ConfigurationError> errors) =>
        value.Equals("stop", StringComparison.OrdinalIgnoreCase) ? OnMatch.Stop
        : value.Equals("continue", StringComparison.OrdinalIgnoreCase) ? OnMatch.Continue
        : Record(errors, path, $"Expected 'stop' or 'continue', got '{value}'.", OnMatch.Stop);

    private static KeyMode? ParseKeyMode(string value, string path, List<ConfigurationError> errors) =>
        value.Equals("major", StringComparison.OrdinalIgnoreCase) ? KeyMode.Major
        : value.Equals("minor", StringComparison.OrdinalIgnoreCase) ? KeyMode.Minor
        : Record<KeyMode?>(errors, path, $"Expected 'major' or 'minor', got '{value}'.", null);

    private static T Record<T>(List<ConfigurationError> errors, string path, string reason, T fallback)
    {
        errors.Add(new ConfigurationError(path, reason));
        return fallback;
    }

    // --- YAML accessors ---------------------------------------------------------------------

    private static bool TryGetString(IDictionary<object, object?> map, string key, out string value)
    {
        if (map.TryGetValue(key, out var v) && v is string s)
        {
            value = s;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetBool(IDictionary<object, object?> map, string key, out bool value)
    {
        if (map.TryGetValue(key, out var v) && v is string s && bool.TryParse(s, out value))
        {
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryGetInt(IDictionary<object, object?> map, string key, out int value)
    {
        if (map.TryGetValue(key, out var v) && v is string s &&
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryGetDouble(IDictionary<object, object?> map, string key, out double value)
    {
        if (map.TryGetValue(key, out var v) && v is string s &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryGetMap(IDictionary<object, object?> map, string key, out IDictionary<object, object?>? value)
    {
        if (map.TryGetValue(key, out var v) && v is IDictionary<object, object?> m)
        {
            value = m;
            return true;
        }
        value = null;
        return false;
    }

    private static bool TryGetSequence(IDictionary<object, object?> map, string key, out IList<object?>? value)
    {
        if (map.TryGetValue(key, out var v) && v is IList<object?> seq)
        {
            value = seq;
            return true;
        }
        value = null;
        return false;
    }
}
