using System;
using Microsoft.UI.Xaml;

namespace Wavee.UI.WinUI.Controls.TrackList;

/// <summary>
/// Defines a custom column for TrackListView. Cell text is produced by the
/// <see cref="ValueSelector"/> delegate (set in code-behind).
/// </summary>
public sealed partial class TrackListColumnDefinition : DependencyObject
{
    /// <summary>
    /// Column header text displayed in the header row.
    /// </summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(TrackListColumnDefinition),
            new PropertyMetadata(string.Empty));

    /// <summary>
    /// Column width. Default: 80px.
    /// </summary>
    public GridLength Width
    {
        get => (GridLength)GetValue(WidthProperty);
        set => SetValue(WidthProperty, value);
    }

    public static readonly DependencyProperty WidthProperty =
        DependencyProperty.Register(nameof(Width), typeof(GridLength), typeof(TrackListColumnDefinition),
            new PropertyMetadata(new GridLength(80)));

    /// <summary>
    /// Custom value selector function. Takes the track item and returns display text.
    /// </summary>
    public Func<object, string>? ValueSelector { get; set; }

    /// <summary>
    /// Text alignment within the cell. Default: Left.
    /// </summary>
    public HorizontalAlignment TextAlignment
    {
        get => (HorizontalAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public static readonly DependencyProperty TextAlignmentProperty =
        DependencyProperty.Register(nameof(TextAlignment), typeof(HorizontalAlignment), typeof(TrackListColumnDefinition),
            new PropertyMetadata(HorizontalAlignment.Left));

    /// <summary>
    /// Optional sort key passed to SortByCommand. If null, column header is not sortable.
    /// </summary>
    public string? SortKey
    {
        get => (string?)GetValue(SortKeyProperty);
        set => SetValue(SortKeyProperty, value);
    }

    public static readonly DependencyProperty SortKeyProperty =
        DependencyProperty.Register(nameof(SortKey), typeof(string), typeof(TrackListColumnDefinition),
            new PropertyMetadata(null));
}