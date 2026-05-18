using RayTagger.Analysis.Bootstrap;

namespace RayTagger.Hosting;

/// <summary>
/// Host-agnostic sink for "did this tool / provider come online?" reports. The CLI implements it
/// on top of Spectre.Console (coloured banner lines); the UI implements it on top of an
/// observable collection bound to a status panel. The factory itself never depends on either.
/// </summary>
public interface IToolStatusReporter
{
    /// <summary>A native analysis tool resolved successfully for the given dimension.</summary>
    void ReportTool(string dimension, string provider, NativeToolResolution resolution);

    /// <summary>A native analysis tool isn't available; the dimension will be disabled.</summary>
    void ReportMissing(string dimension, string provider, string detail);

    /// <summary>An online lookup provider's availability (typically: API key present or absent).</summary>
    void ReportLookupProvider(string name, bool available, string? detail = null);

    /// <summary>
    /// General informational note (e.g. "native-tools.yaml not found — auto-bootstrap disabled").
    /// Hosts may choose to skip these on a clean run.
    /// </summary>
    void ReportNote(string message);
}
