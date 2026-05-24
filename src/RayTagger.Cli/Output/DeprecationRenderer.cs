using RayTagger.Core.Configuration;
using Spectre.Console;

namespace RayTagger.Cli.Output;

/// <summary>
/// Surfaces <see cref="TaggerOptions.Deprecations"/> as yellow Spectre warnings before each
/// command's real output. Called once per handler after <see cref="TaggerOptionsLoader"/> has
/// produced the <see cref="TaggerOptions"/> instance — the warnings need to be visible BEFORE
/// the scan/validate/etc. output so the user notices them even on long-running commands where
/// the final summary scrolls off-screen.
/// </summary>
internal static class DeprecationRenderer
{
    /// <summary>
    /// Writes one warning line per entry in <see cref="TaggerOptions.Deprecations"/>. No-op when
    /// the list is empty so callers can sprinkle this in every handler unconditionally.
    /// </summary>
    public static void Render(IAnsiConsole console, TaggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Deprecations.Count == 0) return;

        foreach (var message in options.Deprecations)
        {
            console.MarkupLine($"[yellow]⚠ Deprecation:[/] {Markup.Escape(message)}");
        }
        console.WriteLine();
    }
}
