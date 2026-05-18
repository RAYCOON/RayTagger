using Microsoft.Extensions.Logging;
using RayTagger.Analysis;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Core.Configuration;
using RayTagger.Core.IO;
using Spectre.Console;

namespace RayTagger.Cli.Commands;

/// <summary>
/// Loads <c>native-tools.yaml</c> (if present) and stitches together the resolver chain that the
/// scan and setup verbs consume. Kept here rather than in the Analysis project because resolving
/// the manifest's *location* is a CLI concern — it depends on where <c>tagger.yaml</c> sits and
/// where the running executable lives, neither of which the Analysis layer should know about.
/// </summary>
internal static class NativeToolsBootstrapFactory
{
    /// <summary>
    /// Returns a resolver that consults PATH first, then the bootstrapper (if a manifest was
    /// found). When no manifest is available, the resolver still works — it just can't fall back
    /// to a download, so missing binaries continue to disable their dimensions exactly like before.
    /// </summary>
#pragma warning disable CA2000  // HttpClient lifecycle: process-scoped, see explanation in BuildBootstrapper.
    public static NativeToolResolver BuildResolver(
        NativeToolsOptions opts,
        IAnalysisToolProbe probe,
        ILoggerFactory loggerFactory,
        IAnsiConsole console)
    {
        var bootstrapper = BuildBootstrapper(opts, loggerFactory, console);
        return new NativeToolResolver(
            probe,
            bootstrapper,
            opts.AutoBootstrap,
            loggerFactory.CreateLogger<NativeToolResolver>());
    }

    public static INativeToolBootstrapper? BuildBootstrapper(
        NativeToolsOptions opts,
        ILoggerFactory loggerFactory,
        IAnsiConsole console)
    {
        var manifestPath = ResolveManifestPath(opts);
        if (manifestPath is null)
        {
            console.MarkupLine("[grey]native-tools.yaml not found — auto-bootstrap is disabled. Run `tagger setup --help` for instructions.[/]");
            return null;
        }

        NativeToolsManifest manifest;
        try
        {
            manifest = NativeToolsManifestLoader.Load(manifestPath);
        }
        catch (ConfigurationException ex)
        {
            console.MarkupLine($"[red]native-tools.yaml is invalid ({Markup.Escape(manifestPath)}):[/]");
            console.WriteLine(ex.Message);
            return null;
        }

        // 5-minute timeout: download archives for Essentia / fpcalc are typically 5–30 MB, but on a
        // poor link still want to give them headroom. HttpClient ownership matches MakeHttpClient
        // in this file — process-scoped, never disposed deliberately because the process exits.
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
