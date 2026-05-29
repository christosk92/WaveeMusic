using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Controls.TrackList;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Partial-class extension on <see cref="TrackItem"/> covering mode switching
/// (Compact / Row), the row-level loading shimmer overlay, and every Row-mode
/// property-changed callback (column visibility, density, badges, added-by,
/// date-added, play-count).
///
/// Like the other partials, this lives on the same class — purely a source-layout
/// split, zero runtime cost for virtualized rows.
/// </summary>
public sealed partial class TrackItem
{
    #region Mode Switching

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // Drive x:Load on the inactive mode's whole subtree. Setting these
        // BEFORE ApplyMode/BindTrackData ensures the right subtree is realized
        // by the time we try to set its ImageUrl.
        var compact = item.Mode == TrackItemDisplayMode.Compact;

        // Reset the wired flag for the side we're leaving — x:Load unloads
        // that subtree and a future Mode flip back will create fresh element
        // instances that need fresh handlers.
        if (compact) item._rowHandlersWired = false;
        else item._compactHandlersWired = false;

        item.IsCompactMode = compact;
        item.IsRowMode = !compact;

        // x:Load binding propagates on the next layout pass, but the imperative
        // calls below (BindTrackData, ApplyLoadingVisualState, etc.) need the
        // named fields populated NOW. Force the active subtree to realize
        // synchronously via FindName — this both wires up the generated x:Name
        // fields and triggers x:Load loading of the deferred element.
        if (compact)
        {
            if (item.CompactBorder is null) item.FindName(nameof(CompactBorder));
        }
        else
        {
            if (item.RowRoot is null) item.FindName(nameof(RowRoot));
        }

        // Wire the newly-active mode's event handlers. Each Wire* method is
        // idempotent, so re-firing for the same mode is a no-op.
        if (compact) item.WireCompactHandlers();
        else item.WireRowHandlers();

        item.ApplyMode();
        // Don't reset _isHovered here. Mode is an internal layout switch
        // triggered by the DataTemplate's DP cascade right after realize —
        // if the pointer is already over this row (which happens often when
        // ItemsRepeater realizes an item under the cursor), wiping
        // _isHovered=false means UpdateOverlayState below sees no hover
        // and the play button stays collapsed until the user moves the
        // pointer off and back on (the "first hover doesn't show, second
        // does" bug). Just repaint the active mode's background against
        // the existing _isHovered flag.
        if (compact) item.ApplyCompactBackground();
        else item.ApplyRowBackground();
        item.SyncLoadingStateFromTrack();
        item.BindTrackData();
        item.UpdateOverlayState();
    }

    private void ApplyMode()
    {
        // CompactBorder / RowRoot Visibility is set declaratively in XAML and
        // gated by x:Load on IsCompactMode / IsRowMode — only the active mode's
        // subtree exists, so the inactive side is null. Don't toggle Visibility
        // here; let x:Load handle realization.
        if (Mode != TrackItemDisplayMode.Compact)
        {
            ApplyRowDensityPadding();
            ApplyRowColumnVisibility();
        }

        // RowPopularityBadge lives inside RowRoot, which is null when Mode==Compact.
        if (RowPopularityBadge is not null)
        {
            RowPopularityBadge.Visibility = ShowPopularityBadge && Mode == TrackItemDisplayMode.Row
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    #endregion

    #region Loading State

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.ApplyLoadingVisualState((bool)e.NewValue);
        item.UpdateOverlayState();
    }

    private void SyncLoadingStateFromTrack()
    {
        var loading = Track is { IsLoaded: false };
        if (IsLoading != loading)
            IsLoading = loading;
        else
            ApplyLoadingVisualState(loading);
    }

    private void ApplyLoadingVisualState(bool loading)
    {
        if (Mode == TrackItemDisplayMode.Compact)
        {
            // CompactAlbumArt is x:Load-deferred behind IsCompactMode. When
            // the LazyTrackItem fires IsLoading before x:Bind has propagated
            // the realize, the named field can still be null on the very
            // first state apply. Force-realize, then guard.
            EnsureCompactAlbumArtRealized();
            if (CompactAlbumArt != null)
            {
                CompactAlbumArt.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
                if (!loading)
                    CompactAlbumArt.RefreshCurrentImage();
            }
            // CompactArtShimmer / CompactInfoShimmer are x:Load-gated on
            // IsLoading — null when not loading, realized in default-Visible
            // state when loading. Imperative Visibility toggle stays as a
            // null-safe no-op for diagnostic clarity if the field is present.
            if (CompactArtShimmer is not null)
                CompactArtShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            CompactInfoPanel.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            if (CompactInfoShimmer is not null)
                CompactInfoShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            CompactDuration.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            if (CompactMoreButton is not null && loading)
                CompactMoreButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            RowContentGrid.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            if (loading)
            {
                // RowShimmerOverlay is x:Load-gated on IsLoading. Force
                // synchronous realization so the Shim*ColDef named fields
                // referenced by ApplyRowColumnVisibility are wired before the
                // sync call below.
                if (RowShimmerOverlay is null) FindName(nameof(RowShimmerOverlay));
                if (RowShimmerOverlay is not null)
                {
                    RowShimmerOverlay.Visibility = Visibility.Visible;
                    ApplyRowColumnVisibility();
                }
            }
            else if (RowShimmerOverlay is not null)
            {
                RowShimmerOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    #endregion

    #region Row Properties Changed

    private static void OnRowIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row)
        {
            var track = item.Track;
            var idx = (int)e.NewValue;
            item.RowIndexText.Text = (track?.OriginalIndex > 0)
                ? track.OriginalIndex.ToString()
                : idx > 0 ? idx.ToString() : "";
        }
    }

    private static void OnColumnVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row && item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
    }

    private static void OnShowPlayCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row && item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
        item.UpdateCompactSubtitleText();
    }

    private static void OnDateAddedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowDateAdded lives inside RowRoot, which is x:Load-deferred. Skip
        // when Mode is Compact; BindRowData repopulates this when the row
        // realizes if needed.
        if (item.RowDateAdded is not null)
            item.RowDateAdded.Text = (string?)e.NewValue ?? "";
    }

    private static void OnPlayCountTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowPlayCount lives inside the x:Load-deferred RowRoot subtree.
        if (item.RowPlayCount is not null)
            item.RowPlayCount.Text = (string?)e.NewValue ?? "";
        item.UpdateCompactSubtitleText();
    }

    private static void OnShowPopularityBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowPopularityBadge lives inside the x:Load-deferred RowRoot subtree.
        if (item.RowPopularityBadge is null) return;
        item.RowPopularityBadge.Visibility = (bool)e.NewValue && item.Mode == TrackItemDisplayMode.Row
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnAddedByTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowAddedByText / RowAddedByAvatar / RowAddedByCell live inside the
        // x:Load-deferred RowRoot subtree — skip when Compact-mode hosts set
        // the DP before / without ever realizing the Row subtree.
        if (item.RowAddedByText is null) return;
        var text = (string?)e.NewValue ?? "";
        item.RowAddedByText.Text = text;
        // Feed the same text to PersonPicture so it can derive initials when
        // the avatar URL is missing — without DisplayName, PersonPicture
        // falls back to a generic person glyph instead of the user's initial.
        item.RowAddedByAvatar.DisplayName = text;
        // Empty text → collapse the cell entirely so empty rows don't
        // reserve space for a placeholder avatar + label.
        item.RowAddedByCell.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void OnAddedByAvatarUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowAddedByAvatar lives inside the x:Load-deferred RowRoot subtree.
        if (item.RowAddedByAvatar is null) return;
        var url = (string?)e.NewValue;
        if (string.IsNullOrEmpty(url))
        {
            // Clear the photo so PersonPicture renders its initials / glyph fallback.
            item.RowAddedByAvatar.ProfilePicture = null;
            return;
        }

        // The resolver may return either a direct https URL or a Spotify
        // internal `spotify:image:{hex}` reference; route both through the
        // helper so PersonPicture always gets a loadable URI.
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url) ?? url;
        if (!Uri.TryCreate(httpsUrl, UriKind.Absolute, out var avatarUri))
        {
            item.RowAddedByAvatar.ProfilePicture = null;
            return;
        }
        item.RowAddedByAvatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(avatarUri)
        {
            DecodePixelWidth = 40
        };
    }

    private static void OnIsCompactRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row)
        {
            var compact = (bool)e.NewValue;
            item.RowRoot.Padding = compact ? new Thickness(4, 4, 4, 4) : new Thickness(8, 8, 8, 8);
            item.RowIndexColDef.Width = compact ? new GridLength(30) : new GridLength(40);
        }
    }

    /// <summary>
    /// Applies alternating row styling: border + tinted background on odd rows.
    /// </summary>
    private static readonly Brush DefaultBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public void SetAlternatingBorder(bool isAlternate, bool useCardRow = false)
    {
        _isAlternateRow = isAlternate;
        _useCardRow = useCardRow;
        ApplyRowBackground();
    }

    private const int RowDurationColumnIndex = 9;
    private readonly System.Collections.Generic.List<ColumnDefinition> _customColDefs = [];
    private readonly System.Collections.Generic.List<UIElement> _customColElements = [];

    /// <summary>
    /// Populates custom column values (e.g. Plays) by inserting TextBlocks into the row grid.
    /// Called from TrackListView.ContainerContentChanging with pre-computed values.
    /// </summary>
    public void SetCustomColumnValues(string[] values, System.Collections.Generic.IList<TrackListColumnDefinition> columns)
    {
        // Clear previous custom columns
        foreach (var el in _customColElements)
            RowContentGrid.Children.Remove(el);
        _customColElements.Clear();

        foreach (var cd in _customColDefs)
            RowContentGrid.ColumnDefinitions.Remove(cd);
        _customColDefs.Clear();

        // Reset duration column to base position
        Grid.SetColumn(RowDuration, RowDurationColumnIndex);

        for (int i = 0; i < values.Length; i++)
        {
            // Insert column definition before Duration
            var colDef = new ColumnDefinition { Width = columns[i].Width };
            RowContentGrid.ColumnDefinitions.Insert(RowDurationColumnIndex + i, colDef);
            _customColDefs.Add(colDef);

            var tb = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = values[i],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = ResolveTrackBrush("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = columns[i].TextAlignment,
            };
            Grid.SetColumn(tb, RowDurationColumnIndex + i);
            RowContentGrid.Children.Add(tb);
            _customColElements.Add(tb);
        }

        // Shift duration column right
        Grid.SetColumn(RowDuration, RowDurationColumnIndex + values.Length);
    }

    private void ApplyRowColumnVisibility()
    {
        var density = Math.Clamp(RowDensity, 0, RowDensityArtSizes.Length - 1);
        var artSize = RowDensityArtSizes[density];
        // XS (density 0) force-hides the album art regardless of ShowAlbumArt — the
        // whole point of XS is "image off + tight padding".
        var effectiveShowArt = ShowAlbumArt && artSize > 0;

        RowArtColDef.Width        = effectiveShowArt   ? new GridLength(artSize + 8)         : new GridLength(0);
        RowTitleColDef.MaxWidth   = ResolveColumnMaxWidth(TitleColumnMaxWidth, RowTitleColDef.MinWidth);
        RowAlbumColDef.Width      = ShowAlbumColumn    ? new GridLength(AlbumColumnWidth)     : new GridLength(0);
        RowAddedByColDef.Width    = ShowAddedByColumn  ? new GridLength(AddedByColumnWidth)   : new GridLength(0);
        RowDateColDef.Width       = ShowDateAdded      ? new GridLength(DateAddedColumnWidth) : new GridLength(0);
        RowPlayCountColDef.Width  = ShowPlayCount      ? new GridLength(PlayCountColumnWidth)
            : new GridLength(0);
        RowDurationColDef.Width   = new GridLength(DurationColumnWidth);
        RowPlayCount.Visibility = ShowPlayCount ? Visibility.Visible : Visibility.Collapsed;
        RowProgressCell.Visibility = ShowProgress ? Visibility.Visible : Visibility.Collapsed;

        // Collapsing the column to Width=0 alone isn't enough: RowAlbumArtBorder
        // has a fixed Width/Height in XAML and would still render into the next
        // column. Toggle visibility and resize to match the current density step.
        if (RowAlbumArtBorder is not null)
        {
            RowAlbumArtBorder.Visibility = effectiveShowArt ? Visibility.Visible : Visibility.Collapsed;
            if (effectiveShowArt)
            {
                RowAlbumArtBorder.Width = artSize;
                RowAlbumArtBorder.Height = artSize;
            }
        }

        // Artist subline is hidden at XS too — single-line rows are how we hit the
        // 32-px target height.
        RowArtistsHost.Visibility = (ShowArtistColumn && density > 0 && !ShowProgress) ? Visibility.Visible : Visibility.Collapsed;

        // Keep the shimmer overlay's columns in sync so loading rows align with the
        // real row layout (and with the column headers above). RowShimmerOverlay
        // is x:Load-gated on IsLoading — when not loading, the named
        // ColumnDefinitions are null. Skip the sync; ApplyLoadingVisualState
        // calls back into this method once the shimmer realizes for a fresh
        // loading state.
        if (ShimArtColDef is not null)
        {
            ShimArtColDef.Width       = RowArtColDef.Width;
            ShimTitleColDef.MaxWidth  = RowTitleColDef.MaxWidth;
            ShimAlbumColDef.Width     = RowAlbumColDef.Width;
            ShimAddedByColDef.Width   = RowAddedByColDef.Width;
            ShimDateColDef.Width      = RowDateColDef.Width;
            ShimPlayCountColDef.Width = RowPlayCountColDef.Width;
            ShimDurationColDef.Width  = RowDurationColDef.Width;
        }

        // Subline visibility just changed (artist link) — re-evaluate whether the
        // explicit/video badges should sit on the subline or inline beside the title.
        UpdateBadgePlacement();
    }

    private static double ResolveColumnMaxWidth(double value, double minWidth)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? double.PositiveInfinity
            : Math.Max(minWidth, value);

    private void ApplyRowDensityPadding()
    {
        var density = Math.Clamp(RowDensity, 0, RowDensityPaddings.Length - 1);
        RowRoot.Padding = RowDensityPaddings[density];
    }

    private static void OnRowDensityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode != TrackItemDisplayMode.Row) return;
        item.ApplyRowDensityPadding();
        if (item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
    }

    #endregion
}
