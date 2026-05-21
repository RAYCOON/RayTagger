using RayTagger.Analysis.Internal;

namespace RayTagger.Analysis.Tests;

public class EssentiaJsonParserTests
{
    [Fact]
    public void Reads_bpm_with_first_peak_weight_as_confidence()
    {
        const string json = """
            {
              "rhythm": {
                "bpm": 128.5,
                "bpm_histogram_first_peak_weight": 0.82
              }
            }
            """;

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.Bpm.Should().BeApproximately(128.5, 0.001);
        parsed.BpmConfidence.Should().BeApproximately(0.82, 0.001);
    }

    [Fact]
    public void Reads_edma_key_with_strength_as_confidence()
    {
        const string json = """
            {
              "tonal": {
                "key_edma": {
                  "key": "A",
                  "scale": "minor",
                  "strength": 0.71
                },
                "key_temperley": {
                  "key": "C",
                  "scale": "major",
                  "strength": 0.4
                }
              }
            }
            """;

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.KeyKey.Should().Be("A");
        parsed.KeyScale.Should().Be("minor");
        parsed.KeyStrength.Should().BeApproximately(0.71, 0.001);
    }

    [Fact]
    public void Reads_spectral_energy_from_frame_aggregated_mean()
    {
        const string json = """
            {
              "lowlevel": {
                "spectral_energy": {
                  "mean": 0.0142,
                  "var": 0.0008,
                  "min": 0.0001,
                  "max": 0.21
                }
              }
            }
            """;

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.SpectralEnergy.Should().BeApproximately(0.0142, 1e-6);
    }

    [Fact]
    public void Reads_spectral_energy_when_emitted_as_scalar()
    {
        // Defensive: not all extractor profiles aggregate. Accept a bare number too.
        const string json = """{"lowlevel": {"spectral_energy": 0.05}}""";

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.SpectralEnergy.Should().BeApproximately(0.05, 1e-6);
    }

    [Fact]
    public void Unwraps_single_element_arrays_for_older_essentia()
    {
        const string json = """
            {
              "rhythm": { "bpm": [124.0] },
              "tonal":  { "key_edma": { "strength": [0.6] } }
            }
            """;

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.Bpm.Should().BeApproximately(124.0, 0.001);
        parsed.KeyStrength.Should().BeApproximately(0.6, 0.001);
    }

    [Fact]
    public void Returns_nulls_for_a_completely_unrelated_document()
    {
        const string json = """{"highlevel": {"foo": "bar"}}""";

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.Bpm.Should().BeNull();
        parsed.BpmConfidence.Should().BeNull();
        parsed.KeyKey.Should().BeNull();
        parsed.KeyScale.Should().BeNull();
        parsed.KeyStrength.Should().BeNull();
        parsed.SpectralEnergy.Should().BeNull();
        parsed.AverageLoudness.Should().BeNull();
        parsed.SpectralFlux.Should().BeNull();
        parsed.OnsetRate.Should().BeNull();
        parsed.Danceability.Should().BeNull();
        parsed.BeatsLoudness.Should().BeNull();
    }

    [Fact]
    public void Reads_all_five_energy_composite_features()
    {
        // Shape mirrors a real essentia_streaming_extractor_music JSON: flux/beats_loudness are
        // frame-aggregated (mean/min/max/var); average_loudness/onset_rate/danceability are bare
        // scalars at the top level of their parent section.
        const string json = """
            {
              "lowlevel": {
                "average_loudness": 0.936,
                "spectral_flux":    { "mean": 0.118, "var": 0.011, "min": 0.0001, "max": 0.49 }
              },
              "rhythm": {
                "onset_rate":     6.51,
                "danceability":   1.92,
                "beats_loudness": { "mean": 0.126, "var": 0.005, "min": 0.0, "max": 0.25 }
              }
            }
            """;

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.AverageLoudness.Should().BeApproximately(0.936, 1e-4);
        parsed.SpectralFlux.Should().BeApproximately(0.118, 1e-4);
        parsed.OnsetRate.Should().BeApproximately(6.51, 1e-3);
        parsed.Danceability.Should().BeApproximately(1.92, 1e-3);
        parsed.BeatsLoudness.Should().BeApproximately(0.126, 1e-4);
    }

    [Fact]
    public void Returns_null_for_non_numeric_bpm_field()
    {
        const string json = """{"rhythm": {"bpm": "fast"}}""";

        EssentiaJsonParser.ParseString(json).Bpm.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_edma_section_missing_even_if_other_profiles_present()
    {
        // Defensive: if Essentia is configured to emit only Temperley, we don't silently fall
        // back to the worse profile — we return null so the analyzer reports no key.
        const string json = """
            {
              "tonal": {
                "key_temperley": { "key": "G", "scale": "major", "strength": 0.5 }
              }
            }
            """;

        var parsed = EssentiaJsonParser.ParseString(json);

        parsed.KeyKey.Should().BeNull();
        parsed.KeyScale.Should().BeNull();
        parsed.KeyStrength.Should().BeNull();
    }
}
