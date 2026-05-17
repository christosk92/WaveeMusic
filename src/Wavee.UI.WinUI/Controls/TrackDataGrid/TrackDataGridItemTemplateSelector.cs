using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Switches between the regular per-track row template and the group-header
/// template inside <see cref="TrackDataGrid"/>'s <see cref="ItemsView"/>.
/// <see cref="TrackDataGridGroupRow"/> markers picked up by
/// <c>BuildFlatRowsWithHeaders</c> route to <see cref="GroupHeaderTemplate"/>;
/// everything else (the <c>ITrackItem</c>s) routes to <see cref="RowTemplate"/>.
/// </summary>
public sealed class TrackDataGridItemTemplateSelector : DataTemplateSelector
{
    /// <summary>Template used for <c>ITrackItem</c> rows. Wraps a
    /// <see cref="Wavee.UI.WinUI.Controls.Track.TrackItem"/> in <c>Row</c> mode.</summary>
    public DataTemplate? RowTemplate { get; set; }

    /// <summary>Template used for <see cref="TrackDataGridGroupRow"/> markers.
    /// Forwards through to the grid's <c>GroupHeaderTemplate</c> DP so each
    /// consumer's existing header markup keeps working unchanged.</summary>
    public DataTemplate? GroupHeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is TrackDataGridGroupRow ? GroupHeaderTemplate : RowTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
