using System.Runtime.InteropServices;

namespace RayTagger.Analysis.Bootstrap;

/// <summary>
/// Computes the .NET runtime identifier (RID) for the current host in a form that matches the
/// keys we use in <c>native-tools.yaml</c>. We don't use <see cref="RuntimeInformation.RuntimeIdentifier"/>
/// directly because it varies between framework-dependent (e.g. <c>osx</c>) and self-contained
/// (<c>osx-arm64</c>) builds, while the manifest always uses the arch-qualified form.
/// </summary>
internal static class RuntimeIdentifierResolver
{
    public static string Current => Resolve(
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        RuntimeInformation.OSArchitecture);

    internal static string Resolve(bool isOsx, bool isLinux, bool isWindows, Architecture arch)
    {
        var os = (isOsx, isLinux, isWindows) switch
        {
            (true, _, _) => "osx",
            (_, true, _) => "linux",
            (_, _, true) => "win",
            _ => throw new NativeToolBootstrapException(
                $"Unsupported OS platform for native-tool bootstrap. Set the binary on PATH manually."),
        };

        var archName = arch switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => throw new NativeToolBootstrapException(
                $"Unsupported CPU architecture '{arch}' for native-tool bootstrap."),
        };

        return $"{os}-{archName}";
    }
}
