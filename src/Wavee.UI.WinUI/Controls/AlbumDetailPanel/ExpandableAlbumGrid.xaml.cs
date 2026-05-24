using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Wavee.Core.Http;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Controls.Cards;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.AlbumDetailPanel;

/// <summary>
/// Discography grid that opens an inline <see cref="AlbumDetailPanel"/> between
/// two rows (Apple-Music style) without ever tearing the album cards down.
///
/// <para>The grid is a single virtualizing <see cref="ItemsRepeater"/> of
/// <see cref="ContentCard"/>s driven by <see cref="ExpandingGridLayout"/>. The
/// detail panel is a single persistent overlay child — expanding only changes
/// the layout (it reserves a band and the cards re-arrange) and re-positions
/// the panel, so card artwork never flashes and nothing is realized or
/// recycled. The view-model's <c>ExpandedAlbum</c> is the single source of
/// truth; this control reacts through the <see cref="ExpandedAlbum"/> property
/// and raises <see cref="ExpandRequested"/> / <see cref="CollapseRequested"/>
/// for the host to route to the expand / collapse commands.</para>
/// </summary>
public sealed partial class ExpandableAlbumGrid : UserControl
{
    // The repeater's source — a mirror of ItemsSource kept in sync via an
    // identity diff so a source Reset (pagination append) never rebuilds card
    // containers.
    private readonly ObservableCollection<LazyReleaseItem> _items = new();
    private INotifyCollectionChanged? _sourceNotifier;

    private LazyReleaseItem? _expandedItem;

    private IColorService? _colorService;
    private int _colorRevision;

    private ImplicitAnimationCollection? _glideAnimations;
    private DispatcherTimer? _glideTimer;

    public ExpandableAlbumGrid()
    {
        InitializeComponent();
        Repeater.ItemsSource = _items;
        GridLayout.ExpanderGeometryChanged += OnExpanderGeometryChanged;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => _glideTimer?.Stop();

    // ── Dependency properties ───────────────────────────────────────────────

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(ExpandableAlbumGrid),
            new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>The discography releases — an enumerable of <c>LazyReleaseItem</c>.</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty ExpandedAlbumProperty =
        DependencyProperty.Register(nameof(ExpandedAlbum), typeof(LazyReleaseItem), typeof(ExpandableAlbumGrid),
            new PropertyMetadata(null, OnExpandedAlbumPropertyChanged));

    /// <summary>The currently-expanded album (the view-model owns this). When it
    /// is one of this grid's releases the expander opens here; otherwise this
    /// grid collapses.</summary>
    public LazyReleaseItem? ExpandedAlbum
    {
        get => (LazyReleaseItem?)GetValue(ExpandedAlbumProperty);
        set => SetValue(ExpandedAlbumProperty, value);
    }

    public static readonly DependencyProperty ExpandedAlbumTracksProperty =
        DependencyProperty.Register(nameof(ExpandedAlbumTracks), typeof(object), typeof(ExpandableAlbumGrid),
            new PropertyMetadata(null, OnExpandedAlbumTracksChanged));

    /// <summary>The expanded album's track list, forwarded to the panel.</summary>
    public object? ExpandedAlbumTracks
    {
        get => GetValue(ExpandedAlbumTracksProperty);
        set => SetValue(ExpandedAlbumTracksProperty, value);
    }

    public static readonly DependencyProperty IsLoadingExpandedTracksProperty =
        DependencyProperty.Register(nameof(IsLoadingExpandedTracks), typeof(bool), typeof(ExpandableAlbumGrid),
            new PropertyMetadata(false, OnIsLoadingExpandedTracksChanged));

    /// <summary>Whether the expanded album's tracks are still loading.</summary>
    public bool IsLoadingExpandedTracks
    {
        get => (bool)GetValue(IsLoadingExpandedTracksProperty);
        set => SetValue(IsLoadingExpandedTracksProperty, value);
    }

    // ── Events ──────────────────────────────────────────────────────────────

    /// <summary>Raised when the user clicks a collapsed album card.</summary>
    public event EventHandler<LazyReleaseItem>? ExpandRequested;

    /// <summary>Raised when the user clicks the expanded album again or the
    /// panel's close button.</summary>
    public event EventHandler? CollapseRequested;

    /// <summary>Raised once after an expand, with the panel, so the host can
    /// nudge the scroll if the panel is clipped below the viewport.</summary>
    public event EventHandler<AlbumDetailPanel>? ExpandLayoutReady;

    // ── Source mirroring ────────────────────────────────────────────────────

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (ExpandableAlbumGrid)d;

        if (grid._sourceNotifier is not null)
            grid._sourceNotifier.CollectionChanged -= grid.OnSourceCollectionChanged;

        grid._sourceNotifier = e.NewValue as INotifyCollectionChanged;
        if (grid._sourceNotifier is not null)
            grid._sourceNotifier.CollectionChanged += grid.OnSourceCollectionChanged;

        grid.SyncFromSource();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SyncFromSource();

    /// <summary>
    /// Brings <see cref="_items"/> in line with <see cref="ItemsSource"/> via an
    /// identity diff (≤30 capped items) — granular Insert / Move / Remove ops so
    /// card containers for unchanged releases are never rebuilt and a source
    /// Reset is never propagated to the repeater.
    /// </summary>
    private void SyncFromSource()
    {
        var source = new List<LazyReleaseItem>();
        if (ItemsSource is IEnumerable enumerable)
        {
            foreach (var entry in enumerable)
            {
                if (entry is LazyReleaseItem release)
                    source.Add(release);
            }
        }

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(_items[i]))
                _items.RemoveAt(i);
        }

        for (var ordinal = 0; ordinal < source.Count; ordinal++)
        {
            var release = source[ordinal];
            var current = _items.IndexOf(release);
            if (current < 0)
                _items.Insert(Math.Min(ordinal, _items.Count), release);
            else if (current != ordinal)
                _items.Move(current, ordinal);
        }

        // Expansion may have shifted (or the expanded album may be gone).
        if (_expandedItem is not null)
        {
            var ordinal = _items.IndexOf(_expandedItem);
            if (ordinal < 0)
            {
                CollapseLocal();
                CollapseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                GridLayout.ExpandedAlbumOrdinal = ordinal;
            }
        }
    }

    // ── Expand / collapse ───────────────────────────────────────────────────

    private static void OnExpandedAlbumPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ExpandableAlbumGrid)d).OnExpandedAlbumChanged(e.NewValue as LazyReleaseItem);

    private void OnExpandedAlbumChanged(LazyReleaseItem? album)
    {
        var ordinal = album is not null ? _items.IndexOf(album) : -1;

        // Not one of this grid's releases (belongs to the other grid, or
        // nothing is expanded) — collapse locally.
        if (album is null || ordinal < 0)
        {
            if (_expandedItem is not null)
            {
                PrepareGlide();
                CollapseLocal();
            }
            return;
        }

        if (ReferenceEquals(_expandedItem, album))
            return;

        // Glide the cards that are about to shift; attach BEFORE the layout
        // change so the reflow animates.
        PrepareGlide();

        DetailPanel.Album = album.Data;
        DetailPanel.ColorHex = album.Data?.ColorHex;
        DetailPanel.Tracks = ExpandedAlbumTracks as IEnumerable;
        DetailPanel.IsLoading = IsLoadingExpandedTracks;
        DetailPanel.Visibility = Visibility.Visible;
        _expandedItem = album;

        // Estimate the band height up-front; OnDetailPanelSizeChanged refines
        // it once the panel measures.
        if (GridLayout.ExpanderHeight <= 0)
            GridLayout.ExpanderHeight = 360;
        GridLayout.ExpandedAlbumOrdinal = ordinal;

        StartColorFetch(album);
        RaiseExpandLayoutReadySoon();
    }

    private void CollapseLocal()
    {
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Tracks = null;
        GridLayout.ExpandedAlbumOrdinal = -1;
        _expandedItem = null;
        _colorRevision++;
    }

    private static void OnExpandedAlbumTracksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (ExpandableAlbumGrid)d;
        if (grid._expandedItem is not null)
            grid.DetailPanel.Tracks = e.NewValue as IEnumerable;
    }

    private static void OnIsLoadingExpandedTracksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (ExpandableAlbumGrid)d;
        if (grid._expandedItem is not null)
            grid.DetailPanel.IsLoading = (bool)e.NewValue;
    }

    /// <summary>Synchronous teardown for page unload / dispose.</summary>
    public void CollapseNow()
    {
        CollapseLocal();
        _glideTimer?.Stop();
        ApplyGlideAnimations(null);
    }

    // ── Layout / panel positioning ──────────────────────────────────────────

    private void OnExpanderGeometryChanged(double gapTop, double notchX)
    {
        if (gapTop < 0)
            return; // collapsed — the panel is already hidden
        DetailPanel.Margin = new Thickness(0, gapTop, 0, 0);
        DetailPanel.NotchOffsetX = notchX;
    }

    private void OnDetailPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_expandedItem is not null
            && DetailPanel.Visibility == Visibility.Visible
            && e.NewSize.Height > 0)
        {
            GridLayout.ExpanderHeight = e.NewSize.Height;
        }
    }

    private void RaiseExpandLayoutReadySoon()
    {
        DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ExpandLayoutReady is not null
                && DetailPanel.Visibility == Visibility.Visible
                && DetailPanel.ActualHeight > 0)
            {
                ExpandLayoutReady.Invoke(this, DetailPanel);
            }
        });
    }

    // ── Card / panel interaction ────────────────────────────────────────────

    private void OnCardClick(object? sender, EventArgs e)
    {
        // Resolve the clicked release from the card's realised element index.
        if (sender is not UIElement element)
            return;
        var index = Repeater.GetElementIndex(element);
        if (index < 0 || index >= _items.Count)
            return;
        var item = _items[index];
        if (!item.IsLoaded)
            return; // shimmer placeholder — nothing to expand yet

        if (ReferenceEquals(_expandedItem, item))
            CollapseRequested?.Invoke(this, EventArgs.Empty);
        else
            ExpandRequested?.Invoke(this, item);
    }

    private void OnDetailPanelCloseRequested(object? sender, EventArgs e)
        => CollapseRequested?.Invoke(this, EventArgs.Empty);

    // ── Album accent colour ─────────────────────────────────────────────────

    private void StartColorFetch(LazyReleaseItem album)
    {
        var revision = ++_colorRevision;
        var data = album.Data;
        if (data is null)
            return;

        if (!string.IsNullOrEmpty(data.ColorHex))
        {
            DetailPanel.ColorHex = data.ColorHex;
            return;
        }

        var imageUrl = SpotifyImageHelper.ToHttpsUrl(data.ImageUrl);
        if (string.IsNullOrEmpty(imageUrl))
            return;

        _ = FetchColorAsync(revision, imageUrl);
    }

    private async Task FetchColorAsync(int revision, string imageUrl)
    {
        try
        {
            _colorService ??= Ioc.Default.GetService<IColorService>();
            if (_colorService is null)
                return;

            var color = await _colorService.GetColorAsync(imageUrl);
            if (revision != _colorRevision || _expandedItem is null || color is null)
                return;

            var isDark = ActualTheme == ElementTheme.Dark;
            var hex = isDark
                ? color.DarkHex ?? color.RawHex
                : color.LightHex ?? color.RawHex;
            if (!string.IsNullOrEmpty(hex))
                DetailPanel.ColorHex = hex;
        }
        catch
        {
            // Best-effort — the panel falls back to the theme surface colour.
        }
    }

    // ── Glide animation ─────────────────────────────────────────────────────
    //
    // While an expand / collapse reflows the grid, cards glide to their new
    // positions via an implicit Offset animation, scoped to a short window so
    // it never animates first realisation or scroll re-layout.

    private void PrepareGlide()
    {
        EnsureGlideAnimation();
        if (_glideAnimations is null)
            return;

        ApplyGlideAnimations(_glideAnimations);

        _glideTimer ??= CreateGlideTimer();
        _glideTimer.Stop();
        _glideTimer.Start();
    }

    private DispatcherTimer CreateGlideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            _glideTimer?.Stop();
            ApplyGlideAnimations(null);
        };
        return timer;
    }

    private void ApplyGlideAnimations(ImplicitAnimationCollection? animations)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (Repeater.TryGetElement(i) is ContentCard card)
                ElementCompositionPreview.GetElementVisual(card).ImplicitAnimations = animations;
        }
    }

    private void EnsureGlideAnimation()
    {
        if (_glideAnimations is not null)
            return;

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Target = "Offset";
        var ease = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.16f, 1f),
            new System.Numerics.Vector2(0.3f, 1f));
        offset.InsertExpressionKeyFrame(1f, "this.FinalValue", ease);
        offset.Duration = TimeSpan.FromMilliseconds(220);

        _glideAnimations = compositor.CreateImplicitAnimationCollection();
        _glideAnimations["Offset"] = offset;
    }
}
