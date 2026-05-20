using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RayTagger.Core.Models;
using RayTagger.Ui.ViewModels;

namespace RayTagger.Ui.Views;

/// <summary>
/// Read-only modal that presents the outcome of <see cref="RuleEditorViewModel.TestAgainstFileAsync"/>
/// — existing tags, rules that matched, and the final resolved values with their source. Same
/// information the CLI <c>tagger explain</c> verb renders, just stacked into Avalonia tables.
/// </summary>
public partial class RuleTestResultDialog : Window
{
    public RuleTestResultDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog modal-over <paramref name="owner"/>. Decomposes the
    /// <see cref="RuleTestResult"/> into pre-formatted row collections so the XAML doesn't have
    /// to do conditional formatting in markup.
    /// </summary>
    public static async Task ShowAsync(Window owner, RuleTestResult result)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(result);

        var dialog = new RuleTestResultDialog
        {
            DataContext = result,
            Title = $"Regel-Test: {result.FileName}",
        };
        dialog.ExistingItems.ItemsSource = BuildExistingRows(result.Existing);
        dialog.AppliedItems.ItemsSource = BuildAppliedRows(result.Applied);
        dialog.FinalItems.ItemsSource = BuildFinalRows(result.Final);

        await dialog.ShowDialog(owner).ConfigureAwait(true);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private static IReadOnlyList<RuleTestRow> BuildExistingRows(TrackTags? existing)
    {
        if (existing is null) return [];
        return
        [
            new RuleTestRow("Artist", FormatValue(existing.Artist)),
            new RuleTestRow("Title", FormatValue(existing.Title)),
            new RuleTestRow("Album", FormatValue(existing.Album)),
            new RuleTestRow("Genre", FormatValue(existing.Genre)),
            new RuleTestRow("Sub-Genre", FormatValue(existing.SubGenre)),
            new RuleTestRow("BPM", FormatBpm(existing.Bpm)),
            new RuleTestRow("Key", FormatValue(existing.Key?.Standard)),
            new RuleTestRow("Energy", FormatInt(existing.Energy)),
            new RuleTestRow("Mood", FormatValue(existing.Mood)),
            new RuleTestRow("Set", FormatValue(existing.SetPosition)),
        ];
    }

    private static List<RuleTestRuleRow> BuildAppliedRows(IReadOnlyList<MappingRuleHit> applied)
    {
        var rows = new List<RuleTestRuleRow>(applied.Count);
        for (var i = 0; i < applied.Count; i++)
        {
            var hit = applied[i];
            var changed = hit.ChangedFields.Count > 0
                ? string.Join(", ", hit.ChangedFields)
                : "(nichts)";
            rows.Add(new RuleTestRuleRow(i + 1, hit.RuleName, changed));
        }
        return rows;
    }

    private static List<RuleTestFinalRow> BuildFinalRows(ResolvedTrackTags? final)
    {
        if (final is null) return [];
        var rows = new List<RuleTestFinalRow>
        {
            new("Genre", FormatValue(final.Genre.Value), final.Genre.Source.ToString()),
            new("Sub-Genre", FormatValue(final.SubGenre.Value), final.SubGenre.Source.ToString()),
            new("BPM", FormatBpm(final.Bpm.Value), final.Bpm.Source.ToString()),
            new("Key", FormatValue(final.Key.Value?.Standard), final.Key.Source.ToString()),
            new("Energy", FormatInt(final.Energy.Value), final.Energy.Source.ToString()),
            new("Mood", FormatValue(final.Mood.Value), final.Mood.Source.ToString()),
            new("Set", FormatValue(final.SetPosition.Value), final.SetPosition.Source.ToString()),
        };
        foreach (var (key, field) in final.Custom)
        {
            rows.Add(new RuleTestFinalRow($"tag.{key}", FormatValue(field.Value), field.Source.ToString()));
        }
        return rows;
    }

    private static string FormatValue(string? value) =>
        string.IsNullOrEmpty(value) ? "—" : value;

    private static string FormatBpm(double? bpm) =>
        bpm?.ToString("0.##", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatInt(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "—";
}

/// <summary>Two-column row — used for "field: value" displays inside the test dialog.</summary>
public sealed record RuleTestRow(string Field, string Value);

/// <summary>One matched rule + the (already-joined) list of fields it touched.</summary>
public sealed record RuleTestRuleRow(int Index, string RuleName, string ChangedFields);

/// <summary>Three-column row — final resolved value plus the source that supplied it.</summary>
public sealed record RuleTestFinalRow(string Field, string Value, string Source);
