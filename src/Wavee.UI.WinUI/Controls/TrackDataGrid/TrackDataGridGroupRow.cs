namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Marker row injected into <see cref="TrackDataGrid"/>'s visible-rows
/// collection immediately before the first track of each group. The grid
/// uses a <see cref="TrackDataGridItemTemplateSelector"/> to render this
/// type with the consumer-supplied <see cref="TrackDataGrid.GroupHeaderTemplate"/>
/// instead of the per-track row template.
///
/// <para>Replaces the legacy <c>CollectionViewSource</c> + <c>ListView.GroupStyle</c>
/// pipeline. <see cref="ItemsView"/> / <see cref="ItemsRepeater"/> has no
/// native group-style hook, so we flatten the data ourselves and let the
/// template selector switch between header and row markup.</para>
///
/// <para>The selection, sort, filter, and drag-reorder helpers in the grid
/// filter <c>is ITrackItem</c> so they ignore header rows automatically.</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class TrackDataGridGroupRow
{
    /// <summary>The header content the consumer's <c>GroupHeaderSelector</c>
    /// produced for this bucket (typically a string like "Disc 1" or a
    /// composite record). Bound by <c>GroupHeaderTemplate</c>.</summary>
    public required object Header { get; init; }

    /// <summary>Number of tracks in this bucket. Bound by <c>GroupHeaderTemplate</c>
    /// (usually formatted via the consumer's <c>GroupCountFormatter</c>).</summary>
    public required int Count { get; init; }

    /// <summary>Pre-formatted count label produced by the consumer's
    /// <c>GroupCountFormatter</c>. Empty when no formatter is wired —
    /// XAML can fall back to <see cref="Count"/>.</summary>
    public string CountText { get; init; } = string.Empty;
}
