namespace RayTagger.Core.IO;

/// <inheritdoc />
public sealed class UserDataDirectoryProvider : IUserDataDirectoryProvider
{
    private const string ApplicationName = "RayTagger";

    public string GetDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(localAppData, ApplicationName);
    }

    public string GetCacheDirectory()
    {
        var dir = Path.Combine(GetDataDirectory(), "cache");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
