using RayTagger.Core.Models;
using RayTagger.Metadata;

namespace RayTagger.Metadata.Tests;

public class AudioFormatDetectorTests
{
    [Theory]
    [InlineData("track.mp3", AudioFormat.Mp3)]
    [InlineData("TRACK.MP3", AudioFormat.Mp3)]
    [InlineData("/path/to/song.flac", AudioFormat.Flac)]
    [InlineData("song.Flac", AudioFormat.Flac)]
    [InlineData("intro.aiff", AudioFormat.Aiff)]
    [InlineData("intro.aif", AudioFormat.Aiff)]
    [InlineData("intro.aifc", AudioFormat.Aiff)]
    public void Detects_supported_format_case_insensitively(string path, AudioFormat expected)
    {
        AudioFormatDetector.TryDetect(path).Should().Be(expected);
        AudioFormatDetector.IsSupported(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("track.wav")]
    [InlineData("track.ogg")]
    [InlineData("track.m4a")]
    [InlineData("noextension")]
    [InlineData("trailing.dot.")]
    public void Returns_null_for_unsupported_extensions(string path)
    {
        AudioFormatDetector.TryDetect(path).Should().BeNull();
        AudioFormatDetector.IsSupported(path).Should().BeFalse();
    }

    [Fact]
    public void Throws_on_null_or_empty_path()
    {
        Action a1 = () => AudioFormatDetector.TryDetect(null!);
        Action a2 = () => AudioFormatDetector.TryDetect(string.Empty);

        a1.Should().Throw<ArgumentException>();
        a2.Should().Throw<ArgumentException>();
    }
}
