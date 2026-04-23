using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls.TrackDataGrid;

/// <summary>
/// Column header button with an animated sort-direction arrow. Ported from
/// files-community/Files' <c>DataGridHeader</c> — same visual-state machine
/// (Unsorted / SortAscending / SortDescending) and DP surface.
/// </summary>
public sealed partial class TrackDataGridColumnHeader : UserControl
{
    public TrackDataGridColumnHeader()
    {
        InitializeComponent();
        UpdateSortVisualState(useTransitions: false);
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(TrackDataGridColumnHeader),
            new PropertyMetadata(string.Empty));

    public bool CanBeSorted
    {
        get => (bool)GetValue(CanBeSortedProperty);
        set => SetValue(CanBeSortedProperty, value);
    }
    public static readonly DependencyProperty CanBeSortedProperty =
        DependencyProperty.Register(nameof(CanBeSorted), typeof(bool), typeof(TrackDataGridColumnHeader),
            new PropertyMetadata(true));

    public TrackDataGridSortDirection? ColumnSortOption
    {
        get => (TrackDataGridSortDirection?)GetValue(ColumnSortOptionProperty);
        set => SetValue(ColumnSortOptionProperty, value);
    }
    public static readonly DependencyProperty ColumnSortOptionProperty =
        DependencyProperty.Register(nameof(ColumnSortOption), typeof(TrackDataGridSortDirection?), typeof(TrackDataGridColumnHeader),
            new PropertyMetadata(null, OnColumnSortOptionChanged));

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(TrackDataGridColumnHeader),
            new PropertyMetadata(null));

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(nameof(CommandParameter), typeof(object), typeof(TrackDataGridColumnHeader),
            new PropertyMetadata(null));

    /// <summary>
    /// Start-side padding applied to the inner <see cref="HeaderButton"/>'s content,
    /// so the header label lines up horizontally with the matching row cell's text.
    /// The UserControl's own <see cref="Control.Padding"/> doesn't propagate inside the
    /// template, hence this dedicated DP.
    /// </summary>
    public Thickness LabelPadding
    {
        get => (Thickness)GetValue(LabelPaddingProperty);
        set => SetValue(LabelPaddingProperty, value);
    }
    public static readonly DependencyProperty LabelPaddingProperty =
        DependencyProperty.Register(nameof(LabelPadding), typeof(Thickness), typeof(TrackDataGridColumnHeader),
            new PropertyMetadata(new Thickness(0)));

    private static void OnColumnSortOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackDataGridColumnHeader header)
            header.UpdateSortVisualState(useTransitions: true);
    }

    private void UpdateSortVisualState(bool useTransitions)
    {
        var state = ColumnSortOption switch
        {
            TrackDataGridSortDirection.Ascending => "SortAscending",
            TrackDataGridSortDirection.Descending => "SortDescending",
            _ => "Unsorted"
        };
        VisualStateManager.GoToState(this, state, useTransitions);
    }
}
