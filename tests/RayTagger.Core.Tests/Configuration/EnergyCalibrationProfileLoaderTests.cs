using RayTagger.Core.Configuration;

namespace RayTagger.Core.Tests.Configuration;

public class EnergyCalibrationProfileLoaderTests
{
    [Fact]
    public void Roundtrips_a_profile_through_save_and_load()
    {
        var profile = new EnergyCalibrationProfile
        {
            SpectralFlux = new FeatureAnchor { Floor = 0.060, Ceiling = 0.140 },
            BeatsLoudness = new FeatureAnchor { Floor = 0.040, Ceiling = 0.220 },
            OnsetRate = new FeatureAnchor { Floor = 4.0, Ceiling = 7.0 },
            Danceability = new FeatureAnchor { Floor = 1.2, Ceiling = 2.3 },
            AverageLoudness = new FeatureAnchor { Floor = 0.55, Ceiling = 0.95 },
            SampleCount = 247,
            GeneratedAt = new DateTime(2026, 5, 21, 14, 30, 0, DateTimeKind.Utc),
            SourcePath = "/Users/dj/Music/Library",
        };

        var path = Path.Combine(Path.GetTempPath(), $"energy-cal-{Guid.NewGuid():N}.yaml");
        try
        {
            EnergyCalibrationProfileLoader.Save(path, profile);
            var loaded = EnergyCalibrationProfileLoader.Load(path);

            loaded.SpectralFlux.Floor.Should().BeApproximately(0.060, 1e-6);
            loaded.SpectralFlux.Ceiling.Should().BeApproximately(0.140, 1e-6);
            loaded.BeatsLoudness.Floor.Should().BeApproximately(0.040, 1e-6);
            loaded.BeatsLoudness.Ceiling.Should().BeApproximately(0.220, 1e-6);
            loaded.OnsetRate.Floor.Should().BeApproximately(4.0, 1e-6);
            loaded.OnsetRate.Ceiling.Should().BeApproximately(7.0, 1e-6);
            loaded.Danceability.Floor.Should().BeApproximately(1.2, 1e-6);
            loaded.Danceability.Ceiling.Should().BeApproximately(2.3, 1e-6);
            loaded.AverageLoudness.Floor.Should().BeApproximately(0.55, 1e-6);
            loaded.AverageLoudness.Ceiling.Should().BeApproximately(0.95, 1e-6);
            loaded.SampleCount.Should().Be(247);
            loaded.GeneratedAt.Should().Be(profile.GeneratedAt);
            loaded.SourcePath.Should().Be(profile.SourcePath);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_writes_a_header_explaining_the_file_is_tool_owned()
    {
        var profile = new EnergyCalibrationProfile
        {
            SpectralFlux = new FeatureAnchor { Floor = 0.06, Ceiling = 0.14 },
            SampleCount = 30,
            GeneratedAt = DateTime.UtcNow,
        };

        var path = Path.Combine(Path.GetTempPath(), $"energy-cal-{Guid.NewGuid():N}.yaml");
        try
        {
            EnergyCalibrationProfileLoader.Save(path, profile);
            var text = File.ReadAllText(path);

            text.Should().StartWith("#");
            text.Should().Contain("Auto-generated");
            text.Should().Contain("calibrate-energy");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_throws_clear_error_when_file_missing()
    {
        var path = $"/nonexistent-tagger-test-{Guid.NewGuid():N}.yaml";

        var act = () => EnergyCalibrationProfileLoader.Load(path);

        act.Should().Throw<ConfigurationException>().WithMessage("*not found*");
    }

    [Fact]
    public void Save_is_atomic_via_temp_file()
    {
        // Smoke test: nothing called .tmp remains after a successful save.
        var path = Path.Combine(Path.GetTempPath(), $"energy-cal-{Guid.NewGuid():N}.yaml");
        var profile = new EnergyCalibrationProfile
        {
            SpectralFlux = new FeatureAnchor { Floor = 0.05, Ceiling = 0.15 },
            SampleCount = 25,
        };

        try
        {
            EnergyCalibrationProfileLoader.Save(path, profile);
            File.Exists(path).Should().BeTrue();
            File.Exists(path + ".tmp").Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
