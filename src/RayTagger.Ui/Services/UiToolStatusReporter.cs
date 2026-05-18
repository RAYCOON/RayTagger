using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RayTagger.Analysis.Bootstrap;
using RayTagger.Hosting;

namespace RayTagger.Ui.Services;

/// <summary>
/// <see cref="IToolStatusReporter"/> that pushes status entries into an
/// <see cref="ObservableCollection{T}"/> bound to the UI's tool-status panel. The factory writes
/// to this from a worker thread, so every mutation is marshalled to the dispatcher.
/// </summary>
public sealed class UiToolStatusReporter : ObservableObject, IToolStatusReporter
{
    public ObservableCollection<ToolStatusEntry> Entries { get; } = [];

    public void ReportTool(string dimension, string provider, NativeToolResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var version = string.IsNullOrWhiteSpace(resolution.Probe.Version) ? "(version unknown)" : resolution.Probe.Version;
        var sourceLabel = resolution.Source switch
        {
            NativeToolResolutionSource.Path => "PATH",
            NativeToolResolutionSource.Cache => "cached",
            NativeToolResolutionSource.Downloaded => "downloaded",
            _ => "",
        };
        Add(new ToolStatusEntry(
            Kind: ToolStatusKind.AnalyzerOk,
            Label: $"{dimension} via {provider}",
            Detail: $"{version} ({sourceLabel})"));
    }

    public void ReportMissing(string dimension, string provider, string detail) =>
        Add(new ToolStatusEntry(
            Kind: ToolStatusKind.AnalyzerMissing,
            Label: $"{dimension} via {provider}",
            Detail: string.IsNullOrWhiteSpace(detail) ? "not on PATH" : detail));

    public void ReportLookupProvider(string name, bool available, string? detail = null) =>
        Add(new ToolStatusEntry(
            Kind: available ? ToolStatusKind.LookupOk : ToolStatusKind.LookupOff,
            Label: $"lookup {name}",
            Detail: detail ?? (available ? "ready" : "disabled")));

    public void ReportNote(string message) =>
        Add(new ToolStatusEntry(Kind: ToolStatusKind.Note, Label: message, Detail: null));

    /// <summary>Clears every entry. Call before a new scan run to refresh the panel.</summary>
    public void Reset()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Entries.Clear();
        }
        else
        {
            Dispatcher.UIThread.Post(Entries.Clear);
        }
    }

    private void Add(ToolStatusEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Entries.Add(entry);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Entries.Add(entry));
        }
    }
}

public enum ToolStatusKind
{
    AnalyzerOk,
    AnalyzerMissing,
    LookupOk,
    LookupOff,
    Note,
}

public sealed record ToolStatusEntry(ToolStatusKind Kind, string Label, string? Detail)
{
    /// <summary>UI-friendly glyph (used by the XAML data template).</summary>
    public string Glyph => Kind switch
    {
        ToolStatusKind.AnalyzerOk => "✓",
        ToolStatusKind.AnalyzerMissing => "✗",
        ToolStatusKind.LookupOk => "✓",
        ToolStatusKind.LookupOff => "·",
        _ => "i",
    };

    /// <summary>Used to drive cell foreground binding in the XAML.</summary>
    public bool IsOk => Kind is ToolStatusKind.AnalyzerOk or ToolStatusKind.LookupOk;
    public bool IsMissing => Kind is ToolStatusKind.AnalyzerMissing;
}
