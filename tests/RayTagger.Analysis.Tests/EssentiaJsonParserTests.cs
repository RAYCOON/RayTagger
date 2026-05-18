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
