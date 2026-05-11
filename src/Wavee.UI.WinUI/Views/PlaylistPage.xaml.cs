using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.AvatarStack;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PlaylistPage : Page, INavigationCacheMemoryParticipant, IDisposable, IContentPageHost
{
    private readonly ILogger? _logger;
    private readonly ISettingsService _settings;
    private bool _isNarrowMode;
    // Tracks the last-rendered playlist so the cached page can reset per-playlist
    // view state (filter text) when the bound playlist actually changes.
    private string? _lastPlaylistId;

    private bool _isDisposed;
    private bool _trimmedForNavigationCache;
    private string? _lastRestoredPlaylistId;

    // Composition resources for the left-anchored hero backdrop. Surface is
    // (re)loaded whenever HeaderImageUrl changes. Null when no header image.
    // The hero tree is: ContainerVisual → { _heroSprite (image, masked), _heroScrimSprite
    // (theme-colored scrim with the same mask shape so text on top stays legible) }.
    private Compositor? _heroCompositor;
    private ContainerVisual? _heroContainer;
    private CompositionSurfaceBrush? _heroSurfaceBrush;
    private SpriteVisual? _heroSprite;
    private SpriteVisual? _heroScrimSprite;
    private CompositionColorBrush? _heroScrimColorBrush;
    private LoadedImageSurface? _heroImageSurface;
    private string? _appliedHeroUrl;

    // Scrim strength: tune alpha to taste. 0xB0 (~69%) matched the prior XAML scrim.
    private const byte HeroScrimMaxAlpha = 0xB0;

    public PlaylistViewModel ViewModel { get; }

    public ContentPageController PageController { get; }

    public ShimmerLoadGate ShimmerGate => PageController.ShimmerGate;

    // ── IContentPageHost ─────────────────────────────────────────────────────
    FrameworkElement? IContentPageHost.ShimmerContainer => ShimmerContainer;
    FrameworkElement IContentPageHost.ContentContainer => WidePlaylistScroller;
    FrameworkLayer IContentPageHost.CrossfadeLayer => FrameworkLayer.Composition;
    string IContentPageHost.PageIdForLogging => $"playlist:{XfadeLog.Tag(ViewModel.PlaylistId)}";
    bool IContentPageHost.IsLoading => ViewModel.IsLoading;
    bool IContentPageHost.HasContent => !string.IsNullOrEmpty(ViewModel.PlaylistName);

    public PlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<PlaylistPage>>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        PageController = new ContentPageController(this, _logger);
        InitializeComponent();

        Func<object, string> addedFormatter = item =>
        {
            if (item is PlaylistTrackDto track)
                return track.AddedAtFormatted;
            if (item is LazyTrackItem lazy && lazy.Data is PlaylistTrackDto inner)
                return inner.AddedAtFormatted;
            return "";
        };
        TrackGrid.DateAddedFormatter = addedFormatter;

        // Added-by formatter — emits a name + avatar URL for every row that
        // has an addedBy (including the current user, on collaborative
        // playlists where seeing "I added these, X added those" is the whole
        // point of the column). Falls back to bare "@username" when the
        // resolver hasn't pinned down a display name yet. Defensively, we
        // also short-circuit when the VM's gate says hidden — keeps stale
        // row content from rendering during a binding-propagation race.
        TrackGrid.AddedByFormatter = item =>
        {
            if (!ViewModel.ShouldShowAddedByColumn) return Controls.TrackDataGrid.AddedByCellInfo.Empty;

            var dto = item is PlaylistTrackDto direct
                ? direct
                : (item is LazyTrackItem lz ? lz.Data as PlaylistTrackDto : null);
            if (dto is null || string.IsNullOrEmpty(dto.AddedBy)) return Controls.TrackDataGrid.AddedByCellInfo.Empty;

            var label = dto.AddedByDisplayName ?? "@" + dto.AddedBy;
            System.Diagnostics.Debug.WriteLine($"[addedby-fmt] addedBy={dto.AddedBy} display={dto.AddedByDisplayName ?? "<null>"} avatar={(string.IsNullOrEmpty(dto.AddedByAvatarUrl) ? "<null>" : "set")}");
            return new Controls.TrackDataGrid.AddedByCellInfo(label, dto.AddedByAvatarUrl);
        };

        WidePlaylistPanel.RightTapped += (_, e) =>
        {
            if (string.IsNullOrEmpty(ViewModel.PlaylistId)) return;
            var items = PlaylistContextMenuBuilder.Build(new PlaylistMenuContext
            {
                PlaylistId = ViewModel.PlaylistId,
                PlaylistName = ViewModel.PlaylistName ?? string.Empty,
                IsOwner = ViewModel.IsOwner,
                PlayCommand = ViewModel.PlayAllCommand,
                ShuffleCommand = ViewModel.ShuffleCommand
            });
            ContextMenuHost.Show(WidePlaylistPanel, items, e.GetPosition(WidePlaylistPanel));
            e.Handled = true;
        };

        // Start the wide content panel invisible at composition level so the
        // shimmer→content swap is a smooth crossfade. The previous BoolToVisibility
        // hard cut snapped distractingly when IsLoading flipped false.
        ElementCompositionPreview.GetElementVisual(WidePlaylistScroller).Opacity = 0;
        Unloaded += PlaylistPage_Unloaded;
        _logger?.LogDebug("[xfade][playlist:{Id}] ctor.enter", XfadeLog.Tag(ViewModel.PlaylistId));

        // Editorial / radio playlists don't carry added-at timestamps — hide the whole
        // Date Added column when the loaded tracks have none. Also watch HeaderImageUrl
        // so the composition backdrop reloads when the ViewModel's detail arrives.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        // Rebuild the avatar stack visual whenever the resolved Collaborators
        // collection mutates (full clear / refill on each load completion).
        ViewModel.Collaborators.CollectionChanged += Collaborators_CollectionChanged;

        // Re-push AddedBy cell content onto realized rows once the VM finishes
        // resolving display names + avatars. Without this hook the cells stay
        // on the bare-id "@…" fallback because the imperative formatter only
        // runs at row materialization, not when the source DTO mutates.
        ViewModel.AddedByResolved += ViewModel_AddedByResolved;
        ApplyDateAddedColumnVisibility();
        RebuildDescriptionInlines();

        HeaderBackgroundHost.Loaded += HeaderBackgroundHost_Loaded;
        HeaderBackgroundHost.Unloaded += HeaderBackgroundHost_Unloaded;
        ActualThemeChanged += PlaylistPage_ActualThemeChanged;

        // Seed the VM with the current theme so palette brushes are correct as
        // soon as the data lands. ActualThemeChanged keeps them in sync from
        // there. Mirrors AlbumPage.
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    // ── Crossfade ──

    private void PlaylistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        PageController.IsNavigatingAway = true;
        _heroImageSurface?.Dispose();
        _heroImageSurface = null;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs ev)
    {
        if (_isDisposed)
            return;

        if (ev.PropertyName == nameof(PlaylistViewModel.HasAnyAddedAt))
            ApplyDateAddedColumnVisibility();
        else if (ev.PropertyName == nameof(PlaylistViewModel.HeaderImageUrl))
            ApplyHeaderBackground();
        else if (ev.PropertyName == nameof(PlaylistViewModel.PlaylistDescription))
            RebuildDescriptionInlines();
        else if (ev.PropertyName == nameof(PlaylistViewModel.IsLoading))
            PageController.OnIsLoadingChanged();
        else if (ev.PropertyName == nameof(PlaylistViewModel.PlaylistName))
        {
            // Warm-cache / fresh-create path: PlaylistStore emits Ready directly,
            // IsLoading never transitions false→true→false, OnIsLoadingChanged
            // never schedules the crossfade. The initial TryShowContentNow in
            // LoadParameter bailed because PlaylistName was still empty at that
            // moment (Activate clears it, the queued ApplyDetailState fires later).
            // Re-attempt the schedule the moment the name lands — at this point
            // HasContent is true, IsLoading is false, and ScheduleCrossfade fades
            // out the stuck outer shimmer.
            if (!string.IsNullOrEmpty(ViewModel.PlaylistName))
                PageController.TryShowContentNow();
        }
    }

    private void Collaborators_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isDisposed)
            RebuildCollaboratorStack();
    }

    private void ViewModel_AddedByResolved(object? sender, EventArgs e)
    {
        if (_isDisposed)
            return;

        _logger?.LogInformation("[addedby] page received AddedByResolved -> calling RefreshAddedByCells()");
        if (DispatcherQueue is null)
        {
            TrackGrid.RefreshAddedByCells();
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isDisposed)
                TrackGrid.RefreshAddedByCells();
        });
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Unloaded -= PlaylistPage_Unloaded;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Collaborators.CollectionChanged -= Collaborators_CollectionChanged;
        ViewModel.AddedByResolved -= ViewModel_AddedByResolved;
        HeaderBackgroundHost.Loaded -= HeaderBackgroundHost_Loaded;
        HeaderBackgroundHost.Unloaded -= HeaderBackgroundHost_Unloaded;
        ActualThemeChanged -= PlaylistPage_ActualThemeChanged;
        TrackGrid.Dispose();
        _heroImageSurface?.Dispose();
        _heroImageSurface = null;
        _heroSurfaceBrush = null;
        _heroSprite = null;
        _heroScrimSprite = null;
        _heroContainer = null;
        (ViewModel as IDisposable)?.Dispose();
    }


    private void PlaylistPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_heroScrimColorBrush != null)
            _heroScrimColorBrush.Color = GetHeroScrimColor();
        // Re-derive the palette brushes for the new theme. The album-style
        // ApplyTheme call rebuilds backdrop / hero / pill brushes against the
        // appropriate contrast tier.
        ViewModel.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    private Windows.UI.Color GetHeroScrimColor()
    {
        // Scrim to the theme's base surface color so the hero image gets tinted back
        // toward the normal page background — restoring the contrast track-row text
        // was designed against. Pulls directly from the theme dictionary so a runtime
        // theme switch can repaint without restart.
        var themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj)
            && dictObj is ResourceDictionary themeDict
            && themeDict.TryGetValue("SolidBackgroundFillColorBase", out var colorObj)
            && colorObj is Windows.UI.Color themed)
        {
            return themed;
        }
        // Resource missing — let WinUI resolve via standard lookup.
        return (Windows.UI.Color)Application.Current.Resources["SolidBackgroundFillColorBase"];
    }

    private void ApplyDateAddedColumnVisibility()
    {
        if (TrackGrid.Columns is null) return;
        var dateCol = TrackGrid.Columns.FirstOrDefault(c => c.Key == "DateAdded");
        if (dateCol is null) return;
        dateCol.IsVisible = ViewModel.HasAnyAddedAt;
    }

    private bool TryHandlePendingPlaylistArtConnectedAnimation()
    {
        if (!ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PlaylistArt))
            return false;

        // Skip the standard crossfade — connected animation paints content directly.
        PageController.MarkContentShownDirectly();
        TwoColumnGrid.Opacity = 1;

        using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.playlist.updateLayout"))
        {
            // Element-scoped UpdateLayout: walks up the parent chain to lay
            // out enough to position PlaylistArtContainer for the connected
            // animation, but skips siblings (side rail, action buttons,
            // description, TrackDataGrid). Microsoft docs flag UpdateLayout
            // as taboo in general — narrowing the scope is the closest we
            // can get while still satisfying the connected-anim handshake
            // (the destination must be measured before TryStartAnimation
            // computes its target rect).
            PlaylistArtContainer.UpdateLayout();
        }
        var started = ConnectedAnimationHelper.TryStartAnimation(
            ConnectedAnimationHelper.PlaylistArt,
            PlaylistArtContainer);

        _logger?.LogDebug(
            "[xfade][playlist:{Id}] connected.playlistArt action={Action}",
            XfadeLog.Tag(ViewModel.PlaylistId), started ? "started" : "miss");
        return true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.playlist.onNavigatedTo");
        base.OnNavigatedTo(e);
        LoadParameter(e.Parameter);
    }

    // Same-tab navigation between two playlists reuses this Page instance and
    // never fires OnNavigatedTo — TabBarItem.Navigate routes through this method
    // instead. Without this override, clicking a different playlist from the
    // player bar / sidebar / search while PlaylistPage is the active tab content
    // silently drops the new parameter.
    public void RefreshWithParameter(object? parameter) => LoadParameter(parameter);

    private async void LoadParameter(object? parameter)
    {
        // If we were trimmed since the last LoadParameter, the x:Bind graph is
        // currently detached (TrimForNavigationCache called Bindings.StopTracking).
        // Re-attach BEFORE the Activate / PrefillFrom / ApplyDetail chain below
        // fires its PropertyChanged events, otherwise the hero panel's text and
        // image bindings sit deaf and the view freezes on whatever was bound
        // before the trim. Note: same-tab cross-playlist nav can trim twice
        // (ContentFrame_Navigating, then OnNavigatedFrom) — calling Update()
        // here covers both, including the second one which fires AFTER any
        // earlier Restore-time Update() and silently detaches again.
        var wasTrimmed = _trimmedForNavigationCache;
        _trimmedForNavigationCache = false;
        if (wasTrimmed)
        {
            using (Wavee.UI.WinUI.Services.UiOperationProfiler.Instance?.Profile("page.playlist.bindingsUpdate"))
            {
                Bindings?.Update();
            }
        }
        _logger?.LogInformation(
            "PlaylistPage.LoadParameter: parameter type={Type}, value={Value}",
            parameter?.GetType().FullName ?? "<null>", parameter);

        // Reset shimmer / content visual state for the fresh load — mirrors
        // ArtistPage / AlbumPage so the next playlist fades in cleanly instead
        // of inheriting the previous playlist's already-shown content layer.
        var hasPendingPlaylistArtAnimation =
            ConnectedAnimationHelper.HasPendingAnimation(ConnectedAnimationHelper.PlaylistArt);

        PageController.ResetForNewLoad();

        string? playlistId = null;
        Data.Parameters.ContentNavigationParameter? connectedAnimationNav = null;

        if (parameter is Data.Parameters.ContentNavigationParameter nav)
        {
            _logger?.LogInformation(
                "PlaylistPage.LoadParameter: ContentNavigationParameter Uri='{Uri}', Title='{Title}', Subtitle='{Subtitle}', ImageUrl='{ImageUrl}'",
                nav.Uri, nav.Title, nav.Subtitle, nav.ImageUrl);
            playlistId = nav.Uri;
            // Activate first so its new-playlist clear-down runs BEFORE PrefillFrom
            // writes the nav values — otherwise the clear would wipe the prefill and
            // the UI would stay blank until the store's Ready push arrives.
            if (hasPendingPlaylistArtAnimation)
            {
                // Keep the destination cover/title materialized so TryStart can run
                // before the VM seeds placeholders and subscribes to the store.
                connectedAnimationNav = nav;
                ViewModel.PrefillFrom(nav, clearMissing: true);
            }
            else
            {
                ViewModel.Activate(nav.Uri);
                ViewModel.PrefillFrom(nav);
            }
        }
        else if (parameter is string rawId && !string.IsNullOrWhiteSpace(rawId))
        {
            _logger?.LogInformation("PlaylistPage.LoadParameter: string parameter '{RawId}'", rawId);
            playlistId = rawId;
            ViewModel.Activate(rawId);
        }
        else
        {
            _logger?.LogWarning("PlaylistPage.LoadParameter: unrecognized parameter shape — no load triggered");
        }

        if (!string.IsNullOrEmpty(playlistId))
        {
            if (!string.Equals(playlistId, _lastPlaylistId, StringComparison.Ordinal))
            {
                // Different playlist than the one we last rendered (or first nav):
                // drop the grid's filter so a leftover query from Playlist A doesn't
                // hide tracks on Playlist B. Sort + column widths intentionally persist.
                TrackGrid.ResetFilter();
                _lastPlaylistId = playlistId;
                if (!hasPendingPlaylistArtAnimation)
                    AnimatePlaylistSwap();
            }
            RestorePlaylistPanelWidth(playlistId);
        }

        if (hasPendingPlaylistArtAnimation && TryHandlePendingPlaylistArtConnectedAnimation())
        {
            if (connectedAnimationNav is not null)
            {
                var uri = connectedAnimationNav.Uri;
                await Task.Yield();
                if (!PageController.IsNavigatingAway)
                    ViewModel.Activate(uri, preserveHeaderPrefill: true);
            }

            return;
        }

        // Warm-cache trigger. PlaylistStore is a BehaviorSubject — Activate's subscribe
        // queues ApplyDetailState via the dispatcher, which runs after this method
        // returns. After one yield it has landed (PlaylistName populated, IsLoading
        // stayed false), so TryShowContentNow can fire ScheduleCrossfade for the
        // same-id case where the IsLoading=false write was a no-op.
        if (connectedAnimationNav is not null)
            ViewModel.Activate(connectedAnimationNav.Uri, preserveHeaderPrefill: true);

        await Task.Yield();
        if (PageController.IsNavigatingAway) return;
        PageController.TryShowContentNow();
    }

    /// <summary>
    /// Short cross-panel fade on playlist change. Without this, cached-page
    /// navigations snap the old content straight to the new one (no animation,
    /// because the page instance and its Visibility states don't actually change).
    /// Targets the two-column grid root so the hero panel AND the track grid fade
    /// together — anything less feels partial.
    /// </summary>
    private void AnimatePlaylistSwap()
    {
        if (TwoColumnGrid is null) return;
        TwoColumnGrid.Opacity = 0;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, TwoColumnGrid);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.playlist.onNavigatedFrom");
        base.OnNavigatedFrom(e);
        // Hibernate also releases FilteredTracks / Collaborators / SessionControlChips
        // — bound collections that keep realized item containers alive while this
        // cached page sits invisible in the Frame cache. Activate's isNewPlaylist
        // branch (gated on _tracksLoadedFor, which Hibernate reset) re-seeds the
        // shimmer placeholders before the warm PlaylistStore value lands.
        TrimForNavigationCache();
    }

    public void TrimForNavigationCache()
    {
        if (_trimmedForNavigationCache)
            return;

        _trimmedForNavigationCache = true;
        _lastRestoredPlaylistId = ViewModel.PlaylistId;
        ViewModel.Hibernate();
        ReleaseHeaderBackgroundSurface();
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling is no longer rooted by the (singleton-store-subscribed) VM —
        // without this the entire page tree is pinned across navigations.
        Bindings?.StopTracking();
    }

    public void RestoreFromNavigationCache()
    {
        if (!_trimmedForNavigationCache)
            return;

        // Don't reset _trimmedForNavigationCache here. The next LoadParameter
        // (called either by Restore's tail below, by the subsequent OnNavigatedTo,
        // or both) sees the flag still true and runs Bindings.Update() to re-attach
        // x:Bind to the now-cleared VM. Setting the flag false here would skip
        // that re-attach and leave the hero stuck on its pre-trim values.
        if (!string.IsNullOrEmpty(ViewModel.PlaylistId))
            LoadParameter(ViewModel.PlaylistId);
    }

    private void RestorePlaylistPanelWidth(string playlistId)
    {
        const double defaultWidth = 200;
        var key = $"playlist:{playlistId}";

        var width = _settings.Settings.PanelWidths.TryGetValue(key, out var saved)
            ? saved
            : defaultWidth;

        width = Math.Clamp(width, 200, 500);
        LeftPanelColumn.Width = new GridLength(width, GridUnitType.Pixel);
    }

    private void PlaylistArtContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && e.NewSize.Width > 0)
            border.Height = e.NewSize.Width;
    }

    private void HeaderBackgroundHost_Loaded(object sender, RoutedEventArgs e)
    {
        // First-time setup: build the composition tree. Must happen after the element
        // is parented (hence Loaded, not the constructor).
        if (_heroCompositor == null)
        {
            var visual = ElementCompositionPreview.GetElementVisual(HeaderBackgroundHost);
            _heroCompositor = visual.Compositor;

            // Corner-anchored 2D fade: opaque at the top-left, falling off radially
            // toward both the right and bottom edges, fully transparent toward the
            // bottom-right. A single CompositionRadialGradientBrush gives BOTH
            // horizontal and vertical falloff in one brush — accepted by
            // CompositionMaskBrush.Mask, no Win2D effect tree required.
            //
            // Why this shape and not the alternatives:
            //   • Two-stage mask chains (MaskBrush.Source = MaskBrush) throw at
            //     runtime — "Unsupported source brush type".
            //   • CompositionMaskBrush.Mask also rejects CompositionEffectBrush, so a
            //     gradient-multiply baked via ArithmeticCompositeEffect can't be used
            //     as a mask either.
            //   • Assigning the effect brush directly to a sprite worked on first
            //     load but stopped re-rendering after the source surface was swapped
            //     for a new playlist's image (the effect tree got stuck on a stale /
            //     transiently-disposed surface).
            //   • RadialGradient with EllipseCenter at (0,0) avoids all three issues
            //     and inherits the surface-swap robustness of MaskBrush.
            CompositionRadialGradientBrush BuildCornerFadeMask(byte maxAlpha)
            {
                var g = _heroCompositor.CreateRadialGradientBrush();
                g.EllipseCenter = new Vector2(0f, 0f); // top-left corner
                g.EllipseRadius = new Vector2(1f, 1f); // reaches the bottom-right corner (normalized)
                g.ColorStops.Add(_heroCompositor.CreateColorGradientStop(0f,
                    Windows.UI.Color.FromArgb(maxAlpha, 255, 255, 255)));
                g.ColorStops.Add(_heroCompositor.CreateColorGradientStop(1f,
                    Windows.UI.Color.FromArgb(0, 255, 255, 255)));
                return g;
            }

            // ── Hero image sprite ──────────────────────────────────────────────
            _heroSurfaceBrush = _heroCompositor.CreateSurfaceBrush();
            _heroSurfaceBrush.Stretch = CompositionStretch.UniformToFill;
            // Anchor the crop to the left edge of the source image — it's the portion
            // the user will actually see after the gradient eats the right side.
            _heroSurfaceBrush.HorizontalAlignmentRatio = 0f;
            _heroSurfaceBrush.VerticalAlignmentRatio = 0.5f;

            var heroMask = _heroCompositor.CreateMaskBrush();
            heroMask.Source = _heroSurfaceBrush;
            heroMask.Mask = BuildCornerFadeMask(0xFF);

            _heroSprite = _heroCompositor.CreateSpriteVisual();
            _heroSprite.Brush = heroMask;
            _heroSprite.RelativeSizeAdjustment = Vector2.One;

            // ── Theme-aware scrim sprite (layered above the hero) ─────────────
            // Solid color pulled from SolidBackgroundFillColorBase so the scrim
            // tints toward the page's normal surface color — legible contrast is
            // whatever the theme already designs for. Mask alpha caps at
            // HeroScrimMaxAlpha so the hero still bleeds through, not a flat block.
            _heroScrimColorBrush = _heroCompositor.CreateColorBrush(GetHeroScrimColor());

            var scrimMask = _heroCompositor.CreateMaskBrush();
            scrimMask.Source = _heroScrimColorBrush;
            scrimMask.Mask = BuildCornerFadeMask(HeroScrimMaxAlpha);

            _heroScrimSprite = _heroCompositor.CreateSpriteVisual();
            _heroScrimSprite.Brush = scrimMask;
            _heroScrimSprite.RelativeSizeAdjustment = Vector2.One;

            // ── Container holding both, attached as the element's child visual ─
            _heroContainer = _heroCompositor.CreateContainerVisual();
            _heroContainer.RelativeSizeAdjustment = Vector2.One;
            _heroContainer.Children.InsertAtBottom(_heroSprite);   // image at the back
            _heroContainer.Children.InsertAtTop(_heroScrimSprite); // scrim in front
        }

        // (Re)attach the container on every Loaded. WinUI detaches the child visual when
        // the element unloads (cached-page nav-away), so a plain "already set up, skip"
        // early return would leave the host blank on subsequent returns to this page.
        if (_heroContainer != null)
            ElementCompositionPreview.SetElementChildVisual(HeaderBackgroundHost, _heroContainer);

        // Theme may have changed while the page was cached — refresh the scrim color.
        if (_heroScrimColorBrush != null)
            _heroScrimColorBrush.Color = GetHeroScrimColor();

        // ApplyHeaderBackground's own _appliedHeroUrl dedup short-circuits when the
        // ViewModel's HeaderImageUrl matches what's already loaded — so a cached-tab
        // return with the same playlist is a true no-op (no surface alloc, no
        // decode, no GPU work). Earlier we dropped the surface in Unloaded and reset
        // _appliedHeroUrl here to "force a reload" — but that paid 50-200 ms on every
        // tab switch for the sake of ~500 KB of decoded pixels. Trade reversed: keep
        // the surface alive across Unloaded/Loaded; only re-decode when URL changes.
        ApplyHeaderBackground();
    }

    private void HeaderBackgroundHost_Unloaded(object sender, RoutedEventArgs e)
    {
        // Intentionally NOT disposing _heroImageSurface or nulling _appliedHeroUrl
        // here — see HeaderBackgroundHost_Loaded's comment block above. The cached
        // page instance comes back next nav and we want the dedup to skip the
        // re-decode. Memory cost: ~500 KB per cached PlaylistPage tab. Acceptable.
    }

    private void ReleaseHeaderBackgroundSurface()
    {
        _heroImageSurface?.Dispose();
        _heroImageSurface = null;
        _appliedHeroUrl = null;

        if (_heroSurfaceBrush != null)
            _heroSurfaceBrush.Surface = null;

        SetHeroScrimVisible(false);
    }

    private void ApplyHeaderBackground()
    {
        if (_heroSurfaceBrush == null || _heroCompositor == null) return;

        var url = ViewModel.HeaderImageUrl;

        // No-op if the URL hasn't changed — PropertyChanged can fire redundantly
        // when the ViewModel re-assigns the same value during a refresh cycle.
        if (string.Equals(_appliedHeroUrl, url, StringComparison.Ordinal))
            return;

        _heroImageSurface?.Dispose();
        _heroImageSurface = null;

        var httpsUrl = string.IsNullOrEmpty(url) ? null : SpotifyImageHelper.ToHttpsUrl(url);

        if (string.IsNullOrEmpty(httpsUrl))
        {
            _heroSurfaceBrush.Surface = null;
            _appliedHeroUrl = null;
            // No hero → also hide the scrim, otherwise a solid color block would
            // darken the left of the page on playlists with no header image.
            SetHeroScrimVisible(false);
            return;
        }

        var desiredSize = new Windows.Foundation.Size(
            Math.Max(1, HeaderBackgroundHost.ActualWidth > 0 ? HeaderBackgroundHost.ActualWidth : 1200),
            Math.Max(1, HeaderBackgroundHost.ActualHeight > 0 ? HeaderBackgroundHost.ActualHeight : 800));
        _heroImageSurface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), desiredSize);
        _heroSurfaceBrush.Surface = _heroImageSurface;
        _appliedHeroUrl = url;
        SetHeroScrimVisible(true);
    }

    private void SetHeroScrimVisible(bool visible)
    {
        if (_heroScrimSprite != null)
            _heroScrimSprite.IsVisible = visible;
    }

    private void PlaylistSplitter_ResizeCompleted(object? sender, GridSplitterResizeCompletedEventArgs e)
    {
        var playlistId = ViewModel.PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        _settings.Update(s => s.PanelWidths[$"playlist:{playlistId}"] = e.NewWidth);
    }

    // Tracks the last applied VSM state so we don't re-fire GoToState on
    // every wide-mode resize tick (the third branch of this handler used
    // to do that — VSM is a no-op when state matches but it still costs
    // a state-machine roundtrip per tick of a width drag).
    private string? _lastVsmState;

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var shouldBeNarrow = e.NewSize.Width < 600;
        var targetState = shouldBeNarrow ? "NarrowState" : "WideState";

        if (shouldBeNarrow && !_isNarrowMode)
        {
            _isNarrowMode = true;
            LeftPanelColumn.MinWidth = 0;
            LeftPanelColumn.Width = new GridLength(0);
        }
        else if (!shouldBeNarrow && _isNarrowMode)
        {
            _isNarrowMode = false;
            LeftPanelColumn.MinWidth = 200;
            var playlistId = ViewModel.PlaylistId;
            if (!string.IsNullOrEmpty(playlistId))
                RestorePlaylistPanelWidth(playlistId);
            else
                LeftPanelColumn.Width = new GridLength(200, GridUnitType.Pixel);
        }

        if (_lastVsmState != targetState)
        {
            _lastVsmState = targetState;
            VisualStateManager.GoToState(this, targetState, true);
        }
    }

    // ── Description: HTML decode + clickable spotify: hyperlinks + More/Less ─────

    // Set true once we've observed the description being trimmed at MaxLines=3.
    // Stays true while the user toggles between More/Less so the button doesn't
    // disappear after expanding (RichTextBlock.IsTextTrimmed flips back to false
    // when MaxLines becomes 0). Cleared whenever the description text changes.
    private bool _descriptionWasTrimmed;
    private bool _descriptionExpanded;

    private void RebuildDescriptionInlines()
    {
        if (DescriptionRichText == null) return;

        // Reset toggle state for the new description.
        _descriptionWasTrimmed = false;
        _descriptionExpanded = false;
        DescriptionRichText.MaxLines = 3;
        DescriptionMoreButton.Visibility = Visibility.Collapsed;
        if (DescriptionMoreLabel != null) DescriptionMoreLabel.Text = "More...";

        DescriptionRichText.Blocks.Clear();
        var html = ViewModel?.PlaylistDescription;
        if (string.IsNullOrEmpty(html)) return;

        var paragraph = new Paragraph();
        foreach (var token in SpotifyHtmlHelper.Tokenize(html))
        {
            if (token.IsLink && !string.IsNullOrEmpty(token.Uri))
            {
                // Match the look of WinUI's HyperlinkButton (accent foreground, no
                // underline) instead of the default browser-style blue underline.
                var link = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.None,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                };
                link.Inlines.Add(new Run { Text = token.Text });
                var capturedUri = token.Uri;
                var capturedText = token.Text;
                link.Click += (_, _) => RouteSpotifyUri(capturedUri, capturedText);
                paragraph.Inlines.Add(link);
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = token.Text });
            }
        }
        DescriptionRichText.Blocks.Add(paragraph);
    }

    private void DescriptionRichText_IsTextTrimmedChanged(RichTextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        // RichTextBlock has its own typed handler (RichTextBlock, not TextBlock).
        // Only flip our latch when collapsed and currently trimmed; once latched,
        // the More button stays available even after the user toggles to expanded.
        if (_descriptionExpanded) return;
        if (DescriptionRichText.IsTextTrimmed && !_descriptionWasTrimmed)
        {
            _descriptionWasTrimmed = true;
            DescriptionMoreButton.Visibility = Visibility.Visible;
        }
    }

    // ── Inline edit (title + description) — Phase 1 ──────────────────────────

    private void PlaylistTitleEditor_Committed(object? sender, string newName)
    {
        if (ViewModel.RenameCommand.CanExecute(newName))
            ViewModel.RenameCommand.Execute(newName);
    }

    private void PlaylistDescriptionEditor_Committed(object? sender, string newDescription)
    {
        if (ViewModel.UpdateDescriptionCommand.CanExecute(newDescription))
            ViewModel.UpdateDescriptionCommand.Execute(newDescription);
    }

    // ── Cover photo edit — Phase 2 ───────────────────────────────────────────

    private async void CoverEditOverlay_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        await CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(120))
            .StartAsync(CoverEditOverlay);
    }

    private async void CoverEditOverlay_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Don't fade away while an upload is in flight — the spinner needs to stay visible.
        if (ViewModel.IsUploadingCover) return;
        await CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120))
            .StartAsync(CoverEditOverlay);
    }

    private async void CoverEditOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!ViewModel.IsOwner || ViewModel.IsUploadingCover) return;

        var file = await PickCoverFileAsync();
        if (file is null) return;

        try
        {
            // Show the picked image immediately as a local preview while the
            // upload runs. On success the VM's PlaylistImageUrl will refresh
            // from the next store push and CoverPreviewImage falls back behind
            // the bound Image. On failure we revert.
            using (var stream = await file.OpenReadAsync())
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(stream);
                CoverPreviewImage.Source = bmp;
                CoverPreviewImage.Visibility = Visibility.Visible;
            }

            CoverUploadRing.IsActive = true;
            CoverUploadRing.Visibility = Visibility.Visible;

            byte[] jpegBytes;
            try
            {
                jpegBytes = await Helpers.PlaylistCoverHelper.PrepareForUploadAsync(file);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to prepare cover image for upload");
                ClearCoverPreview();
                Ioc.Default.GetService<INotificationService>()?
                    .Show("Couldn't process that image", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
                return;
            }

            await ViewModel.ChangeCoverCommand.ExecuteAsync(jpegBytes);
            // Success: leave the preview up until the bound URL refreshes.
            // (The store push that lands the new URL will hide it implicitly
            // because the underlying Image then renders the canonical URL.)
        }
        catch
        {
            // ChangeCoverCommand already toasts; revert the preview here.
            ClearCoverPreview();
        }
        finally
        {
            CoverUploadRing.IsActive = false;
            CoverUploadRing.Visibility = Visibility.Collapsed;
        }
    }

    private void CoverEditOverlay_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!ViewModel.IsOwner) return;

        var flyout = new MenuFlyout();
        var remove = new MenuFlyoutItem
        {
            Text = "Remove photo",
            Icon = new FontIcon { Glyph = "" }
        };
        remove.Click += (_, _) =>
        {
            ClearCoverPreview();
            if (ViewModel.RemoveCoverCommand.CanExecute(null))
                ViewModel.RemoveCoverCommand.Execute(null);
        };
        flyout.Items.Add(remove);
        flyout.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
    }

    private async System.Threading.Tasks.Task<Windows.Storage.StorageFile?> PickCoverFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        // FileOpenPicker requires a window handle in WinUI 3 desktop.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

        return await picker.PickSingleFileAsync();
    }

    private void ClearCoverPreview()
    {
        CoverPreviewImage.Source = null;
        CoverPreviewImage.Visibility = Visibility.Collapsed;
    }

    // ── Overflow menu (per-permission) ───────────────────────────────────────

    private void OwnerOverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !ViewModel.HasOverflowItems) return;

        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        var addedAny = false;

        if (ViewModel.CanEditCollaborative)
        {
            var toggleCollab = new MenuFlyoutItem
            {
                Text = ViewModel.IsCollaborative ? "Make solo" : "Make collaborative",
                Icon = new FontIcon { Glyph = "" }
            };
            toggleCollab.Click += (_, _) =>
            {
                if (ViewModel.ToggleCollaborativeCommand.CanExecute(null))
                    ViewModel.ToggleCollaborativeCommand.Execute(null);
            };
            flyout.Items.Add(toggleCollab);

            var invite = new MenuFlyoutItem
            {
                Text = "Invite collaborators…",
                Icon = new FontIcon { Glyph = "" }
            };
            invite.Click += async (_, _) => await ShowInviteFlyoutAsync(fe);
            flyout.Items.Add(invite);
            addedAny = true;
        }

        if (ViewModel.CanAdministratePermissions && ViewModel.HasCollaborators)
        {
            var manage = new MenuFlyoutItem
            {
                Text = "Manage members…",
                Icon = new FontIcon { Glyph = "" }
            };
            manage.Click += (_, _) => ShowMembersFlyout(fe, adminMode: true);
            flyout.Items.Add(manage);
            addedAny = true;
        }

        if (ViewModel.CanCancelMembership)
        {
            if (addedAny) flyout.Items.Add(new MenuFlyoutSeparator());
            var leave = new MenuFlyoutItem
            {
                Text = "Leave playlist",
                Icon = new FontIcon { Glyph = "" }
            };
            leave.Click += async (_, _) => await ConfirmAndLeavePlaylistAsync();
            flyout.Items.Add(leave);
            addedAny = true;
        }

        if (ViewModel.CanDelete)
        {
            if (addedAny) flyout.Items.Add(new MenuFlyoutSeparator());
            var delete = new MenuFlyoutItem
            {
                Text = "Delete playlist",
                Icon = new FontIcon { Glyph = "" },
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            };
            delete.Click += async (_, _) => await ConfirmAndDeletePlaylistAsync();
            flyout.Items.Add(delete);
        }

        if (flyout.Items.Count == 0) return;
        flyout.ShowAt(fe);
    }

    // ── Collaborator avatar stack ────────────────────────────────────────────

    private void RebuildCollaboratorStack()
    {
        const int MaxVisible = 4;

        CollaboratorStackHost.Children.Clear();

        var members = ViewModel.Collaborators;
        if (members.Count == 0) return;

        var visible = Math.Min(members.Count, MaxVisible);
        var overflow = Math.Max(0, members.Count - visible);

        // Reusable AvatarStack control owns the avatar layout math (28dp
        // PersonPicture, 2dp halo, 12dp overlap, "+N" badge). Same control
        // drives the album-page header so visuals stay in sync.
        var stack = new AvatarStack
        {
            MaxVisible = MaxVisible,
            Items = members.Take(visible).Select(m => new AvatarStackItem(
                DisplayName: string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username : m.DisplayName,
                ImageUrl: m.AvatarUrl)).ToList(),
            OverflowCount = overflow,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CollaboratorStackHost.Children.Add(stack);

        // Trailing label so a new user understands what the avatar cluster
        // actually means. Picks a phrase based on context: "Open to
        // collaboration" if it's a collab playlist with only the owner shown
        // (an invitation), or "N collaborators" once contributors have joined.
        string labelText;
        if (members.Count >= 2)
            labelText = $"{members.Count} collaborators";
        else if (ViewModel.IsCollaborative)
            labelText = "Open to collaboration";
        else
            labelText = string.Empty;

        if (!string.IsNullOrEmpty(labelText))
        {
            var label = new TextBlock
            {
                Text = labelText,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            CollaboratorStackHost.Children.Add(label);

            // Trailing right-chevron (Segoe Fluent E76C) — explicit "tap to open"
            // hint without the visual weight of a button. Combined with the
            // hover-pill background + hand cursor on the wrapping Border, the
            // row reads as clickable on first glance.
            var chevron = new FontIcon
            {
                Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.ChevronRight,
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            CollaboratorStackHost.Children.Add(chevron);
        }
    }

    private void CollaboratorStackHost_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // Anchor the flyout off the inner stack so it lines up with the
        // avatars rather than the outer hover-pill (which has padding).
        var anchor = (FrameworkElement?)CollaboratorStackHost ?? (sender as FrameworkElement);
        if (anchor is null) return;
        ShowMembersFlyout(anchor, adminMode: ViewModel.CanAdministratePermissions);
        e.Handled = true;
    }

    private void CollaboratorStackHostFrame_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Wavee.UI.WinUI.Controls.ClickableBorder frame) return;
        // Card hover state: bump from default card brush to the secondary tint
        // — same pattern WinUI's SettingsCard uses on hover. ClearCursor in
        // the exit handler restores the default card brush.
        frame.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        frame.ShowHandCursor();
    }

    private void CollaboratorStackHostFrame_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not Wavee.UI.WinUI.Controls.ClickableBorder frame) return;
        frame.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        frame.ClearCursor();
    }

    // ── Members flyout ───────────────────────────────────────────────────────

    private void ShowMembersFlyout(FrameworkElement anchor, bool adminMode)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        var content = new StackPanel { Spacing = 8, MinWidth = 300 };
        content.Children.Add(new TextBlock
        {
            Text = $"Members ({ViewModel.Collaborators.Count})",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        foreach (var m in ViewModel.Collaborators)
            content.Children.Add(BuildMemberRow(m, adminMode));

        flyout.Content = content;
        flyout.ShowAt(anchor);
    }

    private FrameworkElement BuildMemberRow(Data.Contracts.PlaylistMemberResult member, bool adminMode)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 32,
            Height = 32,
            Fill = string.IsNullOrEmpty(member.AvatarUrl)
                ? (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"]
                : new ImageBrush
                {
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                    ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(member.AvatarUrl))
                    {
                        DecodePixelWidth = 64
                    }
                }
        };
        Grid.SetColumn(avatar, 0);
        row.Children.Add(avatar);

        var nameStack = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = member.DisplayName ?? member.Username,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        nameStack.Children.Add(new TextBlock
        {
            Text = "@" + member.Username,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });
        Grid.SetColumn(nameStack, 1);
        row.Children.Add(nameStack);

        var roleChip = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = member.Role.ToString(),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            }
        };
        Grid.SetColumn(roleChip, 2);
        row.Children.Add(roleChip);

        if (adminMode && member.Role != Data.Contracts.PlaylistMemberRole.Owner)
        {
            var more = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Background = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"],
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6),
                VerticalAlignment = VerticalAlignment.Center
            };
            var memberMenu = new MenuFlyout();
            foreach (var role in new[] {
                Data.Contracts.PlaylistMemberRole.Contributor,
                Data.Contracts.PlaylistMemberRole.Viewer,
                Data.Contracts.PlaylistMemberRole.Blocked })
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = $"Make {role}",
                    IsChecked = member.Role == role
                };
                var captured = role;
                item.Click += (_, _) =>
                {
                    if (ViewModel.SetMemberRoleCommand.CanExecute(null))
                        ViewModel.SetMemberRoleCommand.Execute((member.UserId, captured));
                };
                memberMenu.Items.Add(item);
            }
            memberMenu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem
            {
                Text = "Remove from playlist",
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            };
            remove.Click += (_, _) =>
            {
                if (ViewModel.RemoveMemberCommand.CanExecute(member.UserId))
                    ViewModel.RemoveMemberCommand.Execute(member.UserId);
            };
            memberMenu.Items.Add(remove);
            more.Flyout = memberMenu;

            Grid.SetColumn(more, 3);
            row.Children.Add(more);
        }

        return row;
    }

    // ── Invite flyout ────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task ShowInviteFlyoutAsync(FrameworkElement anchor)
    {
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        // Fixed Width (not MinWidth) so a long URL TextBox can't blow out the
        // flyout horizontally — without this the inner Grid measures the URL's
        // intrinsic width, the * column inherits it, and the entire flyout grows
        // wider than the screen, surfacing a horizontal scrollbar.
        var stack = new StackPanel { Spacing = 8, Width = 380 };
        stack.Children.Add(new TextBlock
        {
            Text = "Invite collaborators",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Anyone with the link can add and remove tracks. The link expires in 7 days.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        // Slot whose content is swapped on Generate / Regenerate. Held inside
        // the outer width-constrained `stack` so rebuilds don't escape the
        // Width=380 cap. Previous code did `flyout.Content = BuildContent()`,
        // which replaced the outer stack entirely and dropped the constraint
        // — the TextBox then measured at its full URL length and pushed the
        // flyout off-screen.
        var contentSlot = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        FrameworkElement BuildContent()
        {
            var link = ViewModel.LatestInviteLink;
            var inner = new StackPanel { Spacing = 8 };

            if (link is null)
            {
                var generate = new Button
                {
                    Content = "Generate link",
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                generate.Click += async (_, _) =>
                {
                    await ViewModel.CreateInviteLinkCommand.ExecuteAsync(TimeSpan.FromDays(7));
                    contentSlot.Content = BuildContent();
                };
                inner.Children.Add(generate);
            }
            else
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var box = new TextBox
                {
                    Text = link.ShareUrl,
                    IsReadOnly = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 12,
                    // Without an explicit MinWidth the TextBox's measure pass
                    // would otherwise still ask for its intrinsic content
                    // width and starve the * column. 0 lets the * column
                    // simply receive whatever the parent allocates.
                    MinWidth = 0,
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Hidden);
                Grid.SetColumn(box, 0);
                row.Children.Add(box);
                var copy = new Button { Content = "Copy" };
                copy.Click += (_, _) =>
                {
                    var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    pkg.SetText(link.ShareUrl);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
                    Ioc.Default.GetService<INotificationService>()?
                        .Show("Link copied", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
                };
                Grid.SetColumn(copy, 1);
                row.Children.Add(copy);
                inner.Children.Add(row);

                var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                meta.Children.Add(new TextBlock
                {
                    Text = $"Expires in {Math.Max(1, (int)link.Ttl.TotalDays)} days",
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                });
                var regen = new HyperlinkButton { Content = "Regenerate", Padding = new Thickness(0) };
                regen.Click += async (_, _) =>
                {
                    await ViewModel.CreateInviteLinkCommand.ExecuteAsync(TimeSpan.FromDays(7));
                    contentSlot.Content = BuildContent();
                };
                meta.Children.Add(regen);
                inner.Children.Add(meta);
            }
            return inner;
        }

        contentSlot.Content = BuildContent();
        stack.Children.Add(contentSlot);
        flyout.Content = stack;
        flyout.ShowAt(anchor);
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task ConfirmAndLeavePlaylistAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Leave playlist?",
            Content = $"You'll lose access to \"{ViewModel.PlaylistName}\". You can rejoin if the owner shares a new invite link.",
            PrimaryButtonText = "Leave",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (ViewModel.LeavePlaylistCommand.CanExecute(null))
        {
            await ViewModel.LeavePlaylistCommand.ExecuteAsync(null);
            NavigationHelpers.OpenHome();
        }
    }

    private async System.Threading.Tasks.Task ConfirmAndDeletePlaylistAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Delete playlist?",
            Content = $"\"{ViewModel.PlaylistName}\" will be removed from your library. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        // Tint the primary button red so the destructive action reads correctly.
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (ViewModel.DeletePlaylistCommand.CanExecute(null))
        {
            await ViewModel.DeletePlaylistCommand.ExecuteAsync(null);
            // Take the user back to a safe surface — Home — after the delete lands.
            NavigationHelpers.OpenHome();
        }
    }

    private void DescriptionMoreButton_Click(object sender, RoutedEventArgs e)
    {
        _descriptionExpanded = !_descriptionExpanded;
        DescriptionRichText.MaxLines = _descriptionExpanded ? 0 : 3;
        if (DescriptionMoreLabel != null)
            DescriptionMoreLabel.Text = _descriptionExpanded ? "Show less" : "More...";
    }

    private static void RouteSpotifyUri(string uri, string displayName)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            NavigationHelpers.OpenPlaylist(uri, displayName);
        else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal))
            NavigationHelpers.OpenAlbum(uri, displayName);
        else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
            NavigationHelpers.OpenArtist(uri, displayName);
        // Anything else (track, episode, etc.) — silently ignore for now; the user
        // can request a route in follow-up work if those start showing up in
        // editorial descriptions.
    }
}
