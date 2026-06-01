using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Library;

/// <summary>
/// Unified library source toggle — <b>Saved</b> / <b>From Liked Songs</b>. Wraps a
/// CommunityToolkit <c>Segmented</c> and exposes a two-way <see cref="SourceMode"/>
/// (<see cref="LibrarySource"/>) DP so Albums and Artists bind one control instead
/// of hand-rolling the segmented + SelectionChanged glue (and the old divergent
/// "Hearted" / "Following" labels).
/// </summary>
public sealed partial class LibrarySourceSelector : UserControl
{
    private bool _suppress;

    public static readonly DependencyProperty SourceModeProperty =
        DependencyProperty.Register(
            nameof(SourceMode), typeof(LibrarySource), typeof(LibrarySourceSelector),
            new PropertyMetadata(LibrarySource.Saved, OnSourceModeChanged));

    public LibrarySource SourceMode
    {
        get => (LibrarySource)GetValue(SourceModeProperty);
        set => SetValue(SourceModeProperty, value);
    }

    public LibrarySourceSelector()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplySelectionFromSource();
    }

    private static void OnSourceModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LibrarySourceSelector)d).ApplySelectionFromSource();

    private void ApplySelectionFromSource()
    {
        if (Selector is null) return;
        var index = SourceMode == LibrarySource.FromLikedSongs ? 1 : 0;
        if (Selector.SelectedIndex == index) return;

        _suppress = true;
        try { Selector.SelectedIndex = index; }
        finally { _suppress = false; }
    }

    private void Selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        SourceMode = Selector.SelectedIndex == 1
            ? LibrarySource.FromLikedSongs
            : LibrarySource.Saved;
    }
}
