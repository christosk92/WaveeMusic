using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Switches between the regular per-track row template, group-header template,
/// and optional footer template inside <see cref="TrackDataGrid"/>'s
/// <see cref="ItemsView"/>.
/// <see cref="TrackDataGridGroupRow"/> markers picked up by
/// <c>BuildFlatRowsWithHeaders</c> route to <see cref="GroupHeaderTemplate"/>;
/// everything else (the <c>ITrackItem</c>s) routes to <see cref="RowTemplate"/>.
/// </summary>
public sealed partial class TrackDataGridItemTemplateSelector : DataTemplateSelector
{
    /// <summary>Template used for <c>ITrackItem</c> rows. Wraps a
    /// <see cref="Wavee.UI.WinUI.Controls.Track.TrackItem"/> in <c>Row</c> mode.</summary>
    public DataTemplate? RowTemplate { get; set; }

    /// <summary>Template used for <see cref="TrackDataGridGroupRow"/> markers.
    /// Forwards through to the grid's <c>GroupHeaderTemplate</c> DP so each
    /// consumer's existing header markup keeps working unchanged.</summary>
    public DataTemplate? GroupHeaderTemplate { get; set; }

    /// <summary>Template used for <see cref="TrackDataGridFooterRow"/> markers
    /// when a consumer wants the footer to scroll with the rows.</summary>
    public DataTemplate? FooterTemplate { get; set; }

    /// <summary>Template used for <see cref="TrackDataGridHeaderRow"/> markers
    /// when a consumer wants a header (e.g. a banner) to scroll with the rows.</summary>
    public DataTemplate? HeaderTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item switch
        {
            TrackDataGridHeaderRow => HeaderTemplate,
            TrackDataGridFooterRow => FooterTemplate,
            TrackDataGridGroupRow => GroupHeaderTemplate,
            _ => RowTemplate,
        };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
