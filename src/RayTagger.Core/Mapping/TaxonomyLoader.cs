using RayTagger.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RayTagger.Core.Mapping;

/// <summary>
/// Loads a taxonomy YAML file into <see cref="Taxonomy"/>. File shape:
/// <code>
/// version: 1
/// enforce: true
/// genres: [House, Techno, ...]
/// subgenres:
///   House:  [Deep, Tech, Soulful, Funk, ...]
///   Techno: [Detroit, Berlin, ...]
/// normalise:
///   - { genre: House, subgenre: Tech, aliases: ["Tech House"] }
///   - { genre: House, subgenre: Funk, aliases: ["Funky House", "Funk House"] }
/// moods: [Dark, Driving, Uplifting, ...]
/// set_positions: [Warm-up, Peak Time, Closing, ...]
/// </code>
/// All validation errors are accumulated and reported via <see cref="ConfigurationException"/>
/// so a typo'd taxonomy surfaces every problem at once.
/// </summary>
public static class TaxonomyLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public static Taxonomy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new ConfigurationException($"Taxonomy file not found: {fullPath}");
        }

        var raw = File.ReadAllText(fullPath);
        return LoadFromString(raw, fullPath);
    }

    public static Taxonomy LoadFromString(string yaml, string sourceDescription = "(in-memory)")
    {
        ArgumentNullException.ThrowIfNull(yaml);

        RawTaxonomy raw;
        try
        {
            raw = Deserializer.Deserialize<RawTaxonomy>(yaml)
                ?? throw new ConfigurationException($"Taxonomy file is empty: {sourceDescription}");
        }
        catch (YamlException ex)
        {
            throw new ConfigurationException(
                $"Taxonomy YAML deserialization failed for {sourceDescription} (line {ex.Start.Line}): {ex.Message}",
                ex);
        }

        var errors = new List<ConfigurationError>();
        var taxonomy = Build(raw, errors);
        if (errors.Count > 0)
        {
            throw new ConfigurationException(
                $"Taxonomy validation failed for {sourceDescription}.", errors);
        }
        return taxonomy;
    }

    private static Taxonomy Build(RawTaxonomy raw, List<ConfigurationError> errors)
    {
        if (raw.Version != 1)
        {
            errors.Add(new ConfigurationError("version",
                $"Only taxonomy version 1 is supported, found {raw.Version}."));
        }

        var genres = (raw.Genres ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (genres.Count == 0)
        {
            errors.Add(new ConfigurationError("genres", "At least one canonical genre is required."));
        }
        var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);

        // Subgenres: must declare per-genre, parent must exist.
        var subgenres = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var subgenreSetByGenre = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (raw.Subgenres is not null)
        {
            foreach (var (genre, list) in raw.Subgenres)
            {
                if (string.IsNullOrWhiteSpace(genre)) continue;
                if (!genreSet.Contains(genre))
                {
                    errors.Add(new ConfigurationError($"subgenres.{genre}",
                        $"Sub-genre list declared for unknown genre '{genre}'."));
                    continue;
                }
                var cleaned = (list ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                subgenres[genre] = cleaned;
                subgenreSetByGenre[genre] = new HashSet<string>(cleaned, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Normalise table: build flat alias map and check that each canonical destination is valid.
        var normaliseEntries = new List<NormaliseEntry>();
        var byAlias = new Dictionary<string, (string Genre, string Subgenre)>(StringComparer.OrdinalIgnoreCase);
        if (raw.Normalise is not null)
        {
            for (var i = 0; i < raw.Normalise.Count; i++)
            {
                var entry = raw.Normalise[i];
                var prefix = $"normalise[{i}]";
                if (string.IsNullOrWhiteSpace(entry.Genre))
                {
                    errors.Add(new ConfigurationError($"{prefix}.genre", "Canonical genre is required."));
                    continue;
                }
                if (!genreSet.Contains(entry.Genre))
                {
                    errors.Add(new ConfigurationError($"{prefix}.genre",
                        $"Canonical genre '{entry.Genre}' is not in the genres list."));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(entry.Subgenre)
                    && subgenreSetByGenre.TryGetValue(entry.Genre, out var validSubs)
                    && !validSubs.Contains(entry.Subgenre))
                {
                    errors.Add(new ConfigurationError($"{prefix}.subgenre",
                        $"Sub-genre '{entry.Subgenre}' is not declared under genre '{entry.Genre}'."));
                    continue;
                }
                // Trim each alias so " Tech House " in YAML matches "Tech House" on a track.
                var aliases = (entry.Aliases ?? [])
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim())
                    .ToList();
                if (aliases.Count == 0)
                {
                    errors.Add(new ConfigurationError($"{prefix}.aliases",
                        "At least one alias is required (otherwise the entry never fires)."));
                    continue;
                }
                normaliseEntries.Add(new NormaliseEntry
                {
                    Genre = entry.Genre,
                    Subgenre = entry.Subgenre ?? string.Empty,
                    Aliases = aliases,
                });
                foreach (var alias in aliases)
                {
                    // An alias that collides with a canonical genre would silently mutate an
                    // already-canonical track on the next normalise_genre run — a no-op at best,
                    // a quiet sub-genre overwrite at worst.
                    if (genreSet.Contains(alias)
                        && !alias.Equals(entry.Genre, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new ConfigurationError($"{prefix}.aliases",
                            $"Alias '{alias}' is also declared as a canonical genre — would shadow it on normalise."));
                        continue;
                    }
                    if (!byAlias.TryAdd(alias, (entry.Genre, entry.Subgenre ?? string.Empty)))
                    {
                        errors.Add(new ConfigurationError($"{prefix}.aliases",
                            $"Alias '{alias}' is already mapped to a different canonical pair."));
                    }
                }
            }
        }

        var moods = (raw.Moods ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        var setPositions = (raw.SetPositions ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        return new Taxonomy
        {
            Genres = genres,
            Subgenres = subgenres,
            Normalise = normaliseEntries,
            NormaliseByAlias = byAlias,
            Moods = moods,
            SetPositions = setPositions,
            Enforce = raw.Enforce,
        };
    }

    // ---- raw YAML shape, deserialised separately so build-time validation can run --------- //
    // CA1812: instantiated by YamlDotNet via reflection; the analyzer can't see that path. //
#pragma warning disable CA1812
    private sealed class RawTaxonomy
    {
        public int Version { get; set; } = 1;
        public bool Enforce { get; set; }
        public List<string>? Genres { get; set; }
        public Dictionary<string, List<string>>? Subgenres { get; set; }
        public List<RawNormaliseEntry>? Normalise { get; set; }
        public List<string>? Moods { get; set; }
        public List<string>? SetPositions { get; set; }
    }

    private sealed class RawNormaliseEntry
    {
        public string Genre { get; set; } = string.Empty;
        public string? Subgenre { get; set; }
        public List<string>? Aliases { get; set; }
    }
#pragma warning restore CA1812
}
