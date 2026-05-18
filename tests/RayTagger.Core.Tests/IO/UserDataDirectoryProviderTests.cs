using RayTagger.Core.IO;

namespace RayTagger.Core.Tests.IO;

public class UserDataDirectoryProviderTests
{
    [Fact]
    public void Data_directory_is_absolute_and_ends_with_app_name()
    {
        var provider = new UserDataDirectoryProvider();

        var dir = provider.GetDataDirectory();

        Path.IsPathFullyQualified(dir).Should().BeTrue();
        Path.GetFileName(dir).Should().Be("RayTagger");
    }

    [Fact]
    public void Cache_directory_is_under_data_directory()
    {
        var provider = new UserDataDirectoryProvider();

        var cache = provider.GetCacheDirectory();

        cache.Should().StartWith(provider.GetDataDirectory());
        Directory.Exists(cache).Should().BeTrue("cache directory should be created on access");
    }
}
