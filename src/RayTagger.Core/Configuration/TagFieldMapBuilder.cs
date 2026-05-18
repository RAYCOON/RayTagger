namespace RayTagger.Core.Configuration;

/// <summary>
/// Parses the raw <c>write.tag_fields</c> dictionary into a <see cref="TagFieldMap"/> and reports
/// every malformed token as a <see cref="ConfigurationError"/>. Token grammar:
/// <code>
///   ID3:&lt;frame&gt;            e.g. ID3:TBPM
///   ID3:TXXX:&lt;description&gt; e.g. ID3:TXXX:CAMELOTKEY
///   VORBIS:&lt;field&gt;         e.g. VORBIS:CAMELOTKEY
/// </code>
/// Standard frames (TBPM, TKEY, TCON, …) are accepted but ignored — they're fixed by the
/// container spec. The override only takes effect for TXXX descriptions and Vorbis field names.
/// </summary>
public static class TagFieldMapBuilder
{
    public static TagFieldMap Build(IReadOnlyDictionary<string, List<string>> raw, List<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(errors);

        var map = TagFieldMap.Default;

        if (raw.Count == 0)
        {
            return map;
        }

        foreach (var (logical, tokens) in raw)
        {
            if (!IsKnownLogicalField(logical))
            {
                errors.Add(new ConfigurationError(
                    $"write.tag_fields.{logical}",
                    $"Unknown logical field '{logical}'. Allowed: {string.Join(", ", KnownLogicalFields)}."));
                continue;
            }

            string? id3TxxxDescription = null;
            string? vorbisField = null;
            foreach (var token in tokens)
            {
                if (!TryParseToken(token, errors, $"write.tag_fields.{logical}", out var parsed))
                {
                    continue;
                }

                if (parsed.Kind == TokenKind.Id3TxxxDescription)
                {
                    id3TxxxDescription = parsed.Value;
                }
                else if (parsed.Kind == TokenKind.VorbisField)
                {
                    vorbisField = parsed.Value;
                }
                // Standard ID3 frames are accepted but currently fixed — the writer always uses
                // the canonical frame per format spec. Recording them here keeps the parser
                // tolerant of the example-YAML form without silently mis-overriding.
            }

            map = ApplyOverride(map, logical, id3TxxxDescription, vorbisField);
        }

        return map;
    }

    private static TagFieldMap ApplyOverride(TagFieldMap map, string logical, string? id3, string? vorbis)
    {
        // Logical field names live in our own config namespace (lower-case kebab is the YAML
        // convention); CA1308 prefers upper-case for security/locale-safe normalisation but
        // these are control tokens we own, not user data — stay readable.
#pragma warning disable CA1308
        var lowered = logical.ToLowerInvariant();
#pragma warning restore CA1308
        return lowered switch
        {
            "subgenre" => map with
            {
                SubGenreId3Description = id3 ?? map.SubGenreId3Description,
                SubGenreVorbisField = vorbis ?? map.SubGenreVorbisField,
            },
            "camelot" or "camelotkey" => map with
            {
                CamelotKeyId3Description = id3 ?? map.CamelotKeyId3Description,
                CamelotKeyVorbisField = vorbis ?? map.CamelotKeyVorbisField,
            },
            "energy" or "energylevel" => map with
            {
                EnergyLevelId3Description = id3 ?? map.EnergyLevelId3Description,
                EnergyLevelVorbisField = vorbis ?? map.EnergyLevelVorbisField,
            },
            "mood" => map with
            {
                MoodId3Description = id3 ?? map.MoodId3Description,
                MoodVorbisField = vorbis ?? map.MoodVorbisField,
            },
            "set_position" or "setposition" => map with
            {
                SetPositionId3Description = id3 ?? map.SetPositionId3Description,
                SetPositionVorbisField = vorbis ?? map.SetPositionVorbisField,
            },
            _ => map,
        };
    }

    private static bool TryParseToken(string token, List<ConfigurationError> errors, string pathPrefix, out ParsedToken parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            errors.Add(new ConfigurationError(pathPrefix, "Empty frame token is not allowed."));
            return false;
        }

        var parts = token.Split(':');
        if (parts.Length < 2)
        {
            errors.Add(new ConfigurationError(pathPrefix,
                $"Frame token '{token}' is malformed. Expected ID3:<frame>, ID3:TXXX:<desc>, or VORBIS:<field>."));
            return false;
        }

        var prefix = parts[0].Trim().ToUpperInvariant();
        switch (prefix)
        {
            case "ID3":
                if (parts.Length == 2)
                {
                    parsed = new ParsedToken(TokenKind.Id3StandardFrame, parts[1].Trim().ToUpperInvariant());
                    return true;
                }
                if (parts.Length == 3 && string.Equals(parts[1].Trim(), "TXXX", StringComparison.OrdinalIgnoreCase))
                {
                    parsed = new ParsedToken(TokenKind.Id3TxxxDescription, parts[2].Trim().ToUpperInvariant());
                    return true;
                }
                errors.Add(new ConfigurationError(pathPrefix,
                    $"ID3 token '{token}' must be 'ID3:<frame>' or 'ID3:TXXX:<description>'."));
                return false;
            case "VORBIS":
                if (parts.Length != 2)
                {
                    errors.Add(new ConfigurationError(pathPrefix,
                        $"VORBIS token '{token}' must be 'VORBIS:<field>'."));
                    return false;
                }
                parsed = new ParsedToken(TokenKind.VorbisField, parts[1].Trim().ToUpperInvariant());
                return true;
            default:
                errors.Add(new ConfigurationError(pathPrefix,
                    $"Unknown frame-container prefix '{prefix}' in '{token}'. Use ID3 or VORBIS."));
                return false;
        }
    }

    private static readonly string[] KnownLogicalFields =
    [
        "genre", "subgenre", "bpm", "key", "camelot", "camelotkey", "energy", "energylevel",
        "mood", "set_position", "setposition",
    ];

    private static bool IsKnownLogicalField(string name) =>
        KnownLogicalFields.Contains(name, StringComparer.OrdinalIgnoreCase);

    private enum TokenKind
    {
        Id3StandardFrame,
        Id3TxxxDescription,
        VorbisField,
    }

    private readonly record struct ParsedToken(TokenKind Kind, string Value);
}
