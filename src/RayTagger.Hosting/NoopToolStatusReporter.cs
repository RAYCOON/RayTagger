using RayTagger.Analysis.Bootstrap;

namespace RayTagger.Hosting;

/// <summary>
/// Silent <see cref="IToolStatusReporter"/> for code paths that build infrastructure ad-hoc
/// (e.g. the per-track UI lookup service) and don't need to push status into a banner / panel.
/// </summary>
public sealed class NoopToolStatusReporter : IToolStatusReporter
{
    public static NoopToolStatusReporter Instance { get; } = new();

    public void ReportTool(string dimension, string provider, NativeToolResolution resolution) { }
    public void ReportMissing(string dimension, string provider, string detail) { }
    public void ReportLookupProvider(string name, bool available, string? detail = null) { }
    public void ReportNote(string message) { }
}
