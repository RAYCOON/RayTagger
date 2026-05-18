using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;

namespace RayTagger.Hosting;

/// <summary>
/// Loads <c>native-tools.yaml</c> (if present) and stitches together the resolver chain that the
/// scan + setup verbs and the UI's coordinator consume. Resolving the manifest's *location*
/// (next to <c>tagger.yaml</c>, then next to the running executable) is a hosting concern, not
/// an Analysis-layer one — that's why this factory lives here.
/// </summary>
public static class NativeToolsBootstrapFactory
{
    /// <summary>
    /// Returns a resolver that consults PATH first, then the bootstrapper (when a manifest was
    /// found). When no manifest is available, the resolver still works — it just can't fall back
    /// to a download, so missing binaries continue to disable their dimensions.
    /// </summary>
#pragma warning disable CA2000  // HttpClient lifecycle: process-scoped; see explanation below.
    public static NativeToolResolver BuildResolver(
        NativeToolsOptions opts,
        IAnalysisToolProbe probe,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(statusReporter);

        var bootstrapper = BuildBootstrapper(opts, loggerFactory, statusReporter);
        return new NativeToolResolver(
            probe,
            bootstrapper,
            opts.AutoBootstrap,
            loggerFactory.CreateLogger<NativeToolResolver>());
    }

    public static INativeToolBootstrapper? BuildBootstrapper(
        NativeToolsOptions opts,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(statusReporter);

        var manifestPath = ResolveManifestPath(opts);
        if (manifestPath is null)
        {
            statusReporter.ReportNote(
                "native-tools.yaml not found — auto-bootstrap disabled. Install Essentia / fpcalc manually or point native_tools.manifest_file at a copy.");
            return null;
        }

        NativeToolsManifest manifest;
        try
        {
            manifest = NativeToolsManifestLoader.Load(manifestPath);
        }
        catch (ConfigurationException ex)
        {
            statusReporter.ReportNote($"native-tools.yaml at {manifestPath} is invalid: {ex.Message}");
            return null;
        }

        // 5-minute timeout: Essentia / fpcalc archives run 5-30 MB. HttpClient ownership matches
        // the lookup-provider clients — process-scoped, factory caller is responsible for shutdown
        // (which on a short-lived CLI means "the OS does it").
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RayTagger/0.1 (+https://github.com/RAYCOON/raytagger)");

        return new NativeToolBootstrapper(
            manifest,
            new UserDataDirectoryProvider(),
            http,
            loggerFactory.CreateLogger<NativeToolBootstrapper>());
    }
#pragma warning restore CA2000

    private static string? ResolveManifestPath(NativeToolsOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.ManifestFile) && File.Exists(opts.ManifestFile))
        {
            return opts.ManifestFile;
        }

        var nextToExe = Path.Combine(AppContext.BaseDirectory, "native-tools.yaml");
        return File.Exists(nextToExe) ? nextToExe : null;
    }
}
