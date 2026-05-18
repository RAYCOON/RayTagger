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
    /// <remarks>
    /// The <see cref="HttpClient"/> is sourced from <see cref="IHttpClientFactory"/> by the caller
    /// (typically <see cref="ServiceCollectionComposer.NativeToolsBootstrapHttpClient"/>) and the
    /// factory does not own its lifetime. A fresh <c>new HttpClient()</c> per scan would leak
    /// socket pool entries on every UI scan; the IHttpClientFactory pooling avoids that.
    /// </remarks>
    public static NativeToolResolver BuildResolver(
        NativeToolsOptions opts,
        IAnalysisToolProbe probe,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(statusReporter);

        var bootstrapper = BuildBootstrapper(opts, httpClient, loggerFactory, statusReporter);
        return new NativeToolResolver(
            probe,
            bootstrapper,
            opts.AutoBootstrap,
            loggerFactory.CreateLogger<NativeToolResolver>());
    }

    /// <summary>
    /// Builds just the bootstrapper (manifest reader + downloader) without the resolver chain.
    /// Used by the <c>setup</c> verb which walks <see cref="INativeToolBootstrapper.KnownTools"/>
    /// directly.
    /// </summary>
    public static INativeToolBootstrapper? BuildBootstrapper(
        NativeToolsOptions opts,
        HttpClient httpClient,
        ILoggerFactory loggerFactory,
        IToolStatusReporter statusReporter)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(httpClient);
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

        return new NativeToolBootstrapper(
            manifest,
            new UserDataDirectoryProvider(),
            httpClient,
            loggerFactory.CreateLogger<NativeToolBootstrapper>());
    }

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
