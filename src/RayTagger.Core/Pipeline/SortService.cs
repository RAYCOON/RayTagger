using System.Globalization;
using Microsoft.Extensions.Logging;
using RayTagger.Core.Configuration;
using RayTagger.Core.Models;
using FsFile = System.IO.File;

namespace RayTagger.Core.Pipeline;

/// <summary>
/// Resolves a track's destination path from <see cref="SortOptions"/> and (unless dry-run) moves
/// or copies the file there. Sidecar tag-backup files (<c>*.tagger.bak.*.yaml</c>) follow the
/// track so <c>tagger restore</c> stays correct after a sort.
/// </summary>
/// <remarks>
/// Conflict resolution follows <see cref="SortConflictPolicy"/>:
/// <list type="bullet">
///   <item><c>Rename</c> — append <c> (2)</c>, <c> (3)</c>, … until a free name is found.</item>
///   <item><c>Skip</c> — leave the source in place, report <see cref="SortAction.SkippedConflict"/>.</item>
///   <item><c>Overwrite</c> — delete the target first, then move. Use with care.</item>
/// </list>
/// <c>File.Move</c> in .NET 8+ falls back to copy+delete across volumes automatically, so no
/// special cross-device handling is required.
/// </remarks>
public sealed class SortService : ISortService
{
    private readonly ILogger<SortService> _logger;

    public SortService(ILogger<SortService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public SortOutcome Execute(
        TrackFile file,
        TrackTags existing,
        ResolvedTrackTags resolved,
        SortOptions options,
        bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return new SortOutcome(file.Path, DestinationPath: null, SortAction.Disabled);
        }
        if (string.IsNullOrWhiteSpace(options.Destination))
        {
            throw new SortServiceException("sort.enabled is true but sort.destination is empty.");
        }

        var ext = Path.GetExtension(file.Path).TrimStart('.');
        var relative = SortTemplateEngine.Render(options.Pattern, existing, resolved, ext);
        var planned = Path.GetFullPath(Path.Combine(options.Destination, relative));

        if (string.Equals(planned, file.Path, StringComparison.Ordinal))
        {
            // Already in the right place — nothing to do, not even a copy.
            return new SortOutcome(file.Path, planned, SortAction.AlreadySorted);
        }

        var resolvedDestination = ResolveConflict(planned, options.Conflict, out var skipped);
        if (skipped)
        {
            return new SortOutcome(file.Path, planned, SortAction.SkippedConflict);
        }

        if (dryRun)
        {
            return new SortOutcome(file.Path, resolvedDestination, SortAction.Planned);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolvedDestination)!);

        if (options.Conflict == SortConflictPolicy.Overwrite && FsFile.Exists(resolvedDestination))
        {
            FsFile.Delete(resolvedDestination);
        }

        if (options.PreserveSource)
        {
            FsFile.Copy(file.Path, resolvedDestination, overwrite: false);
            CarrySidecars(file.Path, resolvedDestination, move: false);
            _logger.LogDebug("Sort copied {Source} → {Dest}", file.Path, resolvedDestination);
            return new SortOutcome(file.Path, resolvedDestination, SortAction.Copied);
        }
        else
        {
            FsFile.Move(file.Path, resolvedDestination, overwrite: false);
            CarrySidecars(file.Path, resolvedDestination, move: true);
            _logger.LogDebug("Sort moved {Source} → {Dest}", file.Path, resolvedDestination);
            return new SortOutcome(file.Path, resolvedDestination, SortAction.Moved);
        }
    }

    /// <summary>
    /// Applies the configured <see cref="SortConflictPolicy"/> against the planned destination.
    /// Returns the path the caller should actually write to; <paramref name="skipped"/> is set
    /// when policy is <see cref="SortConflictPolicy.Skip"/> and the target already exists.
    /// </summary>
    private static string ResolveConflict(string planned, SortConflictPolicy policy, out bool skipped)
    {
        skipped = false;
        if (!FsFile.Exists(planned))
        {
            return planned;
        }

        switch (policy)
        {
            case SortConflictPolicy.Skip:
                skipped = true;
                return planned;
            case SortConflictPolicy.Overwrite:
                return planned;
            case SortConflictPolicy.Rename:
                return FindFreeRename(planned);
            default:
                throw new SortServiceException($"Unsupported conflict policy {policy}.");
        }
    }

    private static string FindFreeRename(string original)
    {
        var dir = Path.GetDirectoryName(original) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(original);
        var ext = Path.GetExtension(original);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i.ToString(CultureInfo.InvariantCulture)}){ext}");
            if (!FsFile.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new SortServiceException($"Refusing to find a free rename slot after 1000 attempts for '{original}'.");
    }

    /// <summary>
    /// A track's sidecar tag-backups (<c>track.mp3.tagger.bak.20260518-...yaml</c>) live next to
    /// the audio file. When we move the audio, the sidecars must follow — otherwise
    /// <c>tagger restore</c> later can't find them. Best-effort: log and continue on errors so
    /// one orphaned sidecar can't fail an otherwise-successful sort.
    /// </summary>
    private void CarrySidecars(string sourceAudio, string destAudio, bool move)
    {
        var sourceDir = Path.GetDirectoryName(sourceAudio) ?? ".";
        var sourceFileName = Path.GetFileName(sourceAudio);
        var destDir = Path.GetDirectoryName(destAudio)!;

        // Filename may legitimately contain glob meta (`*`, `?`, `[`); passing it as a search
        // pattern would make Directory.EnumerateFiles match unrelated tracks. Enumerate the
        // whole directory and StartsWith-filter — costs a directory listing but is correct.
        var sidecarSuffixMarker = ".tagger.bak.";
        IEnumerable<string> sidecars;
        try
        {
            sidecars = Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        var destAudioFileName = Path.GetFileName(destAudio);
        foreach (var sidecar in sidecars)
        {
            var name = Path.GetFileName(sidecar);
            if (!name.StartsWith(sourceFileName + sidecarSuffixMarker, StringComparison.Ordinal)
                || !name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Rewrite the sidecar's filename so it stays paired with the renamed audio file:
            // `OldName.mp3.tagger.bak.X.yaml` → `NewName.mp3.tagger.bak.X.yaml`.
            var sidecarSuffix = name[sourceFileName.Length..];
            var destSidecar = Path.Combine(destDir, destAudioFileName + sidecarSuffix);
            try
            {
                if (move) FsFile.Move(sidecar, destSidecar, overwrite: true);
                else FsFile.Copy(sidecar, destSidecar, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to carry sidecar {Sidecar} → {Dest}", sidecar, destSidecar);
            }
        }
    }
}

/// <summary>Outcome of one track's sort step.</summary>
public sealed record SortOutcome(string SourcePath, string? DestinationPath, SortAction Action);

public enum SortAction
{
    /// <summary><c>sort.enabled</c> is false; no work done.</summary>
    Disabled,
    /// <summary>Dry-run preview — would have moved/copied to <see cref="SortOutcome.DestinationPath"/>.</summary>
    Planned,
    /// <summary>Source already sat at the rendered destination — no work to do.</summary>
    AlreadySorted,
    /// <summary>Conflict policy was <c>Skip</c> and the target existed; source untouched.</summary>
    SkippedConflict,
    /// <summary>Source file moved to the new location (sidecars carried).</summary>
    Moved,
    /// <summary>Source file copied (preserve_source: true).</summary>
    Copied,
}

/// <summary>Thrown for unrecoverable sort setup problems (missing destination, unknown policy).</summary>
public sealed class SortServiceException : Exception
{
    public SortServiceException(string message) : base(message) { }
    public SortServiceException(string message, Exception inner) : base(message, inner) { }
    public SortServiceException() { }
}
