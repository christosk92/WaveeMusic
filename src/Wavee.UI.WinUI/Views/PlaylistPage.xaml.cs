using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Wavee.UI.WinUI.Controls.ImageEditor;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Controls.AvatarStack;
using Wavee.UI.WinUI.Controls.InPageFilter;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Controls.HeroHeader;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.Contracts;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Diagnostics;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Wavee.UI.WinUI.ViewModels.Playlist;

namespace Wavee.UI.WinUI.Views;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PlaylistPage : UserControl, ITabBarItemContent, IPageHostAware, IHibernatingPage, IDisposable, IContentPageHost, IInPageFilterable
{
    // ── IInPageFilterable ───────────────────────────────────────────────
    string IInPageFilterable.FilterQuery
    {
        get => ViewModel?.TrackList?.SearchQuery ?? string.Empty;
        set { if (ViewModel?.TrackList is { } tl) tl.SearchQuery = value ?? string.Empty; }
    }
    string IInPageFilterable.FilterPlaceholder => "Filter tracks…";
    bool IInPageFilterable.CanFilter => ViewModel?.TrackList is not null;

    private const int PlaylistCoverDecodeSize = 280;

    private readonly ILogger? _logger;
    private readonly ISettingsService _settings;
    private readonly ImageCacheService? _imageCache;
    private bool _isNarrowMode;
    // Tracks the last-rendered playlist so the cached page can reset per-playlist
    // view state (filter text) when the bound playlist actually changes.
    private string? _lastPlaylistId;

    private bool _isDisposed;
    private int _visualSettlingGeneration;
    private int _loadedViewWorkGeneration;
    private int _navigationRevision;

    // Composition resources for the full-width hero banner image. Surface is
    // (re)loaded whenever HeaderImageUrl changes; null when no header image.
    // The hero tree is: ContainerVisual → { SpriteVisual(image), SpriteVisual(scrim) }.
    // Scrim lives on the composition layer (not XAML) so it travels with the
    // image surface and fades cleanly into the page surface color via a
    // theme-aware linear gradient — same pattern HeroHeader uses for the
    // album / show / artist heroes.
    private Compositor? _heroCompositor;
    private ContainerVisual? _heroContainer;
    private CompositionSurfaceBrush? _heroSurfaceBrush;
    private SpriteVisual? _heroSprite;
    private SpriteVisual? _heroScrimSprite;
    private CompositionLinearGradientBrush? _heroScrimBrush;
    private CompositionColorGradientStop? _heroScrimTopStop;
    private CompositionColorGradientStop? _heroScrimMidStop;
    private CompositionColorGradientStop? _heroScrimBottomStop;
    private LoadedImageSurface? _heroImageSurface;
    private string? _appliedHeroUrl;
    private string? _retriedCoverImageUrl;

    public PlaylistViewModel ViewModel { get; }

    public ContentPageController PageController { get; }

    public ShimmerLoadGate ShimmerGate => PageController.ShimmerGate;

    public TabItemParameter? TabItemParameter
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ViewModel.PlaylistId))
                return null;

            return new TabItemParameter(NavigationPageType.Playlist, ViewModel.PlaylistId)
            {
                InitialPageType = typeof(PlaylistPage),
                Title = string.IsNullOrWhiteSpace(ViewModel.Header.PlaylistName)
                    ? "Playlist"
                    : ViewModel.Header.PlaylistName
            };
        }
    }

    public event EventHandler<TabItemParameter>? ContentChanged;

    // ── IContentPageHost ─────────────────────────────────────────────────────
    FrameworkElement? IContentPageHost.ShimmerContainer => ShimmerContainer;
    FrameworkElement IContentPageHost.ContentContainer => LeftColumnHost;
    FrameworkLayer IContentPageHost.CrossfadeLayer => FrameworkLayer.Xaml;
    string IContentPageHost.PageIdForLogging => $"playlist:{XfadeLog.Tag(ViewModel.PlaylistId)}";
    bool IContentPageHost.IsLoading => ViewModel.IsLoading;
    bool IContentPageHost.HasContent => !string.IsNullOrEmpty(ViewModel.Header.PlaylistName);

    public PlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<PlaylistPage>>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
        PageController = new ContentPageController(this, _logger);
        InitializeComponent();
        CoverImage.ImageFailed += CoverImage_ImageFailed;
        CoverImage.ImageOpened += CoverImage_ImageOpened;

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
            if (!ViewModel.Header.ShouldShowAddedByColumn) return Controls.TrackDataGrid.AddedByCellInfo.Empty;

            var dto = item is PlaylistTrackDto direct
                ? direct
                : (item is LazyTrackItem lz ? lz.Data as PlaylistTrackDto : null);
            if (dto is null || string.IsNullOrEmpty(dto.AddedBy)) return Controls.TrackDataGrid.AddedByCellInfo.Empty;

            var hasProfile = ViewModel.Header.TryGetAddedByProfile(dto.AddedBy, out var profile);
            var label = hasProfile && !string.IsNullOrWhiteSpace(profile?.DisplayName)
                ? profile.DisplayName
                : "@" + dto.AddedBy;
            var avatarUrl = hasProfile ? profile?.AvatarUrl : null;
            if (Wavee.UI.WinUI.Services.AppFeatureFlags.VerboseUiDiagnostics)
                System.Diagnostics.Debug.WriteLine($"[addedby-fmt] addedBy={dto.AddedBy} display={(profile?.DisplayName ?? "<null>")} avatar={(string.IsNullOrEmpty(avatarUrl) ? "<null>" : "set")}");
            return new Controls.TrackDataGrid.AddedByCellInfo(label, avatarUrl);
        };

        // Floating multi-select command bar — observes TrackGrid's selection.
        // Remove is wired only while the playlist is editable (see
        // UpdateSelectionRemoveCommand, kept in sync from the header stream).
        SelectionBar.Attach(TrackGrid);
        TrackGrid.MultiSelectRemoveLabel = "Remove from playlist";
        UpdateSelectionRemoveCommand();

        LeftColumnHost.RightTapped += (_, e) =>
        {
            if (string.IsNullOrEmpty(ViewModel.PlaylistId)) return;
            var items = PlaylistContextMenuBuilder.Build(new PlaylistMenuContext
            {
                PlaylistId = ViewModel.PlaylistId,
                PlaylistName = ViewModel.Header.PlaylistName ?? string.Empty,
                IsOwner = ViewModel.Header.IsOwner,
                PlayCommand = ViewModel.TrackList.PlayAllCommand,
                ShuffleCommand = ViewModel.TrackList.ShuffleCommand
            });
            ContextMenuHost.Show(LeftColumnHost, items, e.GetPosition(LeftColumnHost));
            e.Handled = true;
        };

        // Start the wide content panel invisible without taking a composition
        // visual lease on LeftColumnHost. Banner-mode transition leaves and
        // row hover states use WinUI Scale; mixing that API with
        // ElementCompositionPreview.GetElementVisual on the same tree can
        // throw "Calling Scale API is not allowed on this object".
        LeftColumnHost.Opacity = 0;
        ApplyHeaderPlacement();
        Loaded += PlaylistPage_Loaded;
        Unloaded += PlaylistPage_Unloaded;
        _logger?.LogDebug("[xfade][playlist:{Id}] ctor.enter", XfadeLog.Tag(ViewModel.PlaylistId));

        // Editorial / radio playlists don't carry added-at timestamps — hide the whole
        // Date Added column when the loaded tracks have none. Also watch HeaderImageUrl
        // so the composition backdrop reloads when the ViewModel's detail arrives.
        // After the decomposition, the per-VM PropertyChanged streams fork: the parent
        // owns IsLoading / HasError, the header owns the envelope-projected properties
        // (Name, Image, HeaderImageUrl, LayoutMode, Description), the track list owns
        // HasAnyAddedAt. Each subscription only listens for the properties that live
        // on the corresponding child.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Header.PropertyChanged += ViewModel_Header_PropertyChanged;
        ViewModel.TrackList.PropertyChanged += ViewModel_TrackList_PropertyChanged;
        // Rebuild the avatar stack visual whenever the resolved Collaborators
        // collection mutates (full clear / refill on each load completion).
        ViewModel.Header.Collaborators.CollectionChanged += Collaborators_CollectionChanged;

        // Re-push AddedBy cell content onto realized rows once the VM finishes
        // resolving display names + avatars. Without this hook the cells stay
        // on the bare-id "@…" fallback because the imperative formatter only
        // runs at row materialization, not when the source DTO mutates.
        ViewModel.Header.AddedByResolved += ViewModel_AddedByResolved;
        QueueInitialBindingDerivedUiSync();

        HeaderBackgroundHost.Loaded += HeaderBackgroundHost_Loaded;
        HeaderBackgroundHost.Unloaded += HeaderBackgroundHost_Unloaded;
        ActualThemeChanged += PlaylistPage_ActualThemeChanged;

        // Seed the VM with the current theme so palette brushes are correct as
        // soon as the data lands. ActualThemeChanged keeps them in sync from
        // there. Mirrors AlbumPage.
        ViewModel.Header.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    // ── Crossfade ──

    private void PlaylistPage_Loaded(object sender, RoutedEventArgs e)
    {
        ScheduleLoadedViewWork();
    }

    private void PlaylistPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadedViewWorkGeneration++;
        PageController.IsNavigatingAway = true;
        ReleaseHeaderBackgroundSurface();
    }

    private void QueueInitialBindingDerivedUiSync()
    {
        if (DispatcherQueue is null)
        {
            ApplyDateAddedColumnVisibility();
            RebuildDescriptionInlines();
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isDisposed)
                return;

            ApplyDateAddedColumnVisibility();
            RebuildDescriptionInlines();
        });
    }

    private void ScheduleLoadedViewWork()
    {
        if (DispatcherQueue is null)
        {
            AttachLoadedViewWork();
            return;
        }

        var generation = ++_loadedViewWorkGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            await Task.Yield();
            if (_isDisposed ||
                PageController.IsNavigatingAway ||
                generation != _loadedViewWorkGeneration ||
                !IsLoaded)
            {
                return;
            }

            AttachLoadedViewWork();
        });
    }

    private void AttachLoadedViewWork()
    {
        ApplyHeaderPlacement();
    }

    // ── In-rows banner header placement ─────────────────────────────────────
    //
    // Banner-mode playlists host the hero as the track grid's in-rows scrolling
    // header (TrackDataGrid.HeaderContent = HeroBannerRow). The grid then owns a
    // single virtualized scroll for the banner + rows, and its toolbar / column
    // header stick just below the banner. Cover-mode playlists project no header
    // row. The old parent-scroll fork + shy-pill morph are gone: the title is
    // always visible in the left column now, so there is nothing to "shy".

    private void ApplyHeaderPlacement()
    {
        if (TrackGrid is null)
            return;
        TrackGrid.HeaderPlacement = ViewModel.Header.LayoutMode == PlaylistLayoutMode.Banner
            ? global::Wavee.UI.WinUI.Controls.TrackDataGrid.TrackDataGridHeaderPlacement.InRowsScroll
            : global::Wavee.UI.WinUI.Controls.TrackDataGrid.TrackDataGridHeaderPlacement.None;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs ev)
    {
        if (_isDisposed)
            return;

        // Parent only owns IsLoading + HasError + ErrorMessage + PlaylistId. The
        // envelope-projected properties (Name, Image, HeaderImageUrl, LayoutMode,
        // Description) moved to the header VM and are handled by
        // ViewModel_Header_PropertyChanged below. HasAnyAddedAt moved to the
        // track list VM and is handled by ViewModel_TrackList_PropertyChanged.
        if (ev.PropertyName == nameof(PlaylistViewModel.IsLoading))
            PageController.OnIsLoadingChanged();
    }

    // The floating selection bar's Remove action is only wired while the
    // playlist is editable — non-owner playlists never expose Remove.
    private void UpdateSelectionRemoveCommand()
        => TrackGrid.MultiSelectRemoveCommand = ViewModel.Header.CanEditItems
            ? ViewModel.TrackList.RemoveTracksCommand
            : null;

    private void ViewModel_Header_PropertyChanged(object? sender, PropertyChangedEventArgs ev)
    {
        if (_isDisposed)
            return;

        if (ev.PropertyName == nameof(PlaylistHeaderViewModel.HeaderImageUrl))
        {
            ApplyHeaderBackground();
            if (!string.IsNullOrEmpty(ViewModel.Header.HeaderImageUrl))
                QueueVisualSettlingPass();
        }
        else if (ev.PropertyName == nameof(PlaylistHeaderViewModel.LayoutMode))
            OnLayoutModeChanged();
        else if (ev.PropertyName == nameof(PlaylistHeaderViewModel.PlaylistImageUrl))
        {
            _retriedCoverImageUrl = null;
            _logger?.LogDebug(
                "Playlist cover URL changed: playlist={PlaylistId}, image={ImageUrl}, header={HeaderImageUrl}, layout={LayoutMode}",
                ViewModel.PlaylistId,
                ViewModel.Header.PlaylistImageUrl ?? "<null>",
                ViewModel.Header.HeaderImageUrl ?? "<null>",
                ViewModel.Header.LayoutMode);
        }
        else if (ev.PropertyName == nameof(PlaylistHeaderViewModel.PlaylistDescription))
            RebuildDescriptionInlines();
        else if (ev.PropertyName == nameof(PlaylistHeaderViewModel.CanEditItems))
            UpdateSelectionRemoveCommand();
        else if (ev.PropertyName == nameof(PlaylistHeaderViewModel.PlaylistName))
        {
            RaiseContentChanged();
            // Warm-cache / fresh-create path: PlaylistStore emits Ready directly,
            // IsLoading never transitions false→true→false, OnIsLoadingChanged
            // never schedules the crossfade. The initial TryShowContentNow in
            // LoadParameter bailed because PlaylistName was still empty at that
            // moment (Activate clears it, the queued ApplyDetailState fires later).
            // Re-attempt the schedule the moment the name lands — at this point
            // HasContent is true, IsLoading is false, and ScheduleCrossfade fades
            // out the stuck outer shimmer.
            if (!string.IsNullOrEmpty(ViewModel.Header.PlaylistName))
                PageController.TryShowContentNow();
        }
    }

    private void RaiseContentChanged()
    {
        if (TabItemParameter is { } parameter)
            ContentChanged?.Invoke(this, parameter);
    }

    private void ViewModel_TrackList_PropertyChanged(object? sender, PropertyChangedEventArgs ev)
    {
        if (_isDisposed)
            return;

        if (ev.PropertyName == nameof(Wavee.UI.WinUI.ViewModels.Playlist.PlaylistTrackListViewModel.HasAnyAddedAt))
            ApplyDateAddedColumnVisibility();
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

    private void CoverImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        var url = ResolveCurrentCoverImageUrl();
        if (string.Equals(_retriedCoverImageUrl, url, StringComparison.Ordinal))
            _retriedCoverImageUrl = null;

        // Cover + title now render in both layout modes, so reveal the cover
        // block regardless of mode (was gated to Cover before).
        CoverHeroBlock.Opacity = 1;

        _logger?.LogDebug(
            "Playlist cover image loaded: playlist={PlaylistId}, url={Url}, header={HeaderImageUrl}, layout={LayoutMode}",
            ViewModel.PlaylistId,
            url ?? "<null>",
            ViewModel.Header.HeaderImageUrl ?? "<null>",
            ViewModel.Header.LayoutMode);
    }

    private void CoverImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var url = ResolveCurrentCoverImageUrl();
        if (!string.IsNullOrWhiteSpace(url))
            _imageCache?.Invalidate(url, PlaylistCoverDecodeSize);

        _logger?.LogWarning(
            "Playlist cover image failed: playlist={PlaylistId}, rawUrl={RawUrl}, resolvedUrl={Url}, header={HeaderImageUrl}, layout={LayoutMode}, error={Error}",
            ViewModel.PlaylistId,
            ViewModel.Header.PlaylistImageUrl ?? "<null>",
            url ?? "<null>",
            ViewModel.Header.HeaderImageUrl ?? "<null>",
            ViewModel.Header.LayoutMode,
            string.IsNullOrWhiteSpace(e.ErrorMessage) ? "<no message>" : e.ErrorMessage);

        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            string.Equals(_retriedCoverImageUrl, url, StringComparison.Ordinal))
        {
            return;
        }

        _retriedCoverImageUrl = url;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed ||
                !IsLoaded ||
                !string.Equals(ResolveCurrentCoverImageUrl(), url, StringComparison.Ordinal))
            {
                return;
            }

            var retryImage = new BitmapImage
            {
                DecodePixelWidth = PlaylistCoverDecodeSize
            };
            retryImage.UriSource = uri;

            CoverImage.Visibility = Visibility.Visible;
            CoverImage.Opacity = 0;
            CoverImage.Source = retryImage;

            _logger?.LogDebug(
                "Playlist cover image retry started: playlist={PlaylistId}, url={Url}",
                ViewModel.PlaylistId,
                url);
        });
    }

    private string? ResolveCurrentCoverImageUrl()
    {
        var raw = ViewModel.Header.PlaylistImageUrl;
        return SpotifyImageHelper.ToHttpsUrl(raw) ?? raw;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Loaded -= PlaylistPage_Loaded;
        Unloaded -= PlaylistPage_Unloaded;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Header.PropertyChanged -= ViewModel_Header_PropertyChanged;
        ViewModel.TrackList.PropertyChanged -= ViewModel_TrackList_PropertyChanged;
        ViewModel.Header.Collaborators.CollectionChanged -= Collaborators_CollectionChanged;
        ViewModel.Header.AddedByResolved -= ViewModel_AddedByResolved;
        CoverImage.ImageFailed -= CoverImage_ImageFailed;
        CoverImage.ImageOpened -= CoverImage_ImageOpened;
        HeaderBackgroundHost.Loaded -= HeaderBackgroundHost_Loaded;
        HeaderBackgroundHost.Unloaded -= HeaderBackgroundHost_Unloaded;
        ActualThemeChanged -= PlaylistPage_ActualThemeChanged;
        SelectionBar.Detach();
        TrackGrid.Dispose();
        ReleaseHeaderBackgroundSurface();
        _heroSurfaceBrush = null;
        _heroSprite = null;
        _heroScrimSprite = null;
        _heroScrimBrush = null;
        _heroScrimTopStop = null;
        _heroScrimMidStop = null;
        _heroScrimBottomStop = null;
        _heroContainer = null;
        (ViewModel as IDisposable)?.Dispose();
    }


    private void PlaylistPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        // Re-seed scrim colours for the new theme so the bottom of the hero
        // image fades cleanly into the page surface (white in light, dark in
        // dark) instead of leaving a hard seam.
        ApplyHeroScrimForTheme();
        // Re-derive the palette brushes (and BannerPrimary/AccentColor) for
        // the new theme so AnimatedHeroBackground can re-pick its
        // tier-appropriate colors.
        ViewModel.Header.ApplyTheme(ActualTheme == ElementTheme.Dark);
    }

    /// <summary>
    /// Resolve the page's surface colour from theme resources and seed the
    /// scrim's three gradient stops:
    ///   • top    → fully transparent (image fully visible up here)
    ///   • mid    → half-alpha tint (gentle ramp begins)
    ///   • bottom → opaque surface colour (image dissolves into page bg)
    /// Covers light + dark theme + runtime theme switch by repulling the
    /// resource each time.
    /// </summary>
    private void ApplyHeroScrimForTheme()
    {
        if (_heroScrimTopStop is null || _heroScrimMidStop is null || _heroScrimBottomStop is null)
            return;

        var surface = ResolveHeroScrimSurfaceColor();
        _heroScrimTopStop.Color = Windows.UI.Color.FromArgb(0, surface.R, surface.G, surface.B);
        _heroScrimMidStop.Color = Windows.UI.Color.FromArgb(0x40, surface.R, surface.G, surface.B);
        _heroScrimBottomStop.Color = Windows.UI.Color.FromArgb(0xFF, surface.R, surface.G, surface.B);
    }

    private Windows.UI.Color ResolveHeroScrimSurfaceColor()
    {
        // Pull SolidBackgroundFillColorBase from the appropriate theme
        // dictionary so a runtime theme switch repaints the scrim without
        // restart. Falls back to the standard lookup if the theme dict is
        // missing for any reason.
        var themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj)
            && dictObj is ResourceDictionary themeDict
            && themeDict.TryGetValue("SolidBackgroundFillColorBase", out var colorObj)
            && colorObj is Windows.UI.Color themed)
        {
            return themed;
        }
        return (Windows.UI.Color)Application.Current.Resources["SolidBackgroundFillColorBase"];
    }

    private void ApplyDateAddedColumnVisibility()
    {
        if (TrackGrid.Columns is null) return;
        var dateCol = TrackGrid.Columns.FirstOrDefault(c => c.Key == "DateAdded");
        if (dateCol is null) return;
        dateCol.IsVisible = ViewModel.TrackList.HasAnyAddedAt;
    }

    public void OnEntered(object? parameter, PageHostNavigationMode mode)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.playlist.onEntered");
        EnterPlaylist(parameter, mode);
    }

    // ── IHibernatingPage ───────────────────────────────────────────────────────

    public void Hibernate()
    {
        _logger?.LogDebug("[hibernate] playlist-page {Id}", XfadeLog.Tag(ViewModel.PlaylistId));
        ViewModel.Hibernate();
        Bindings?.StopTracking();
    }

    public void Rehydrate()
    {
        // Re-evaluate + resume x:Bind tracking; the reload itself runs via
        // OnEntered → EnterPlaylist → ViewModel.Activate (its isNewPlaylist branch
        // fires because Hibernate set _tracksLoadedFor = null, showing the skeleton;
        // the PlaylistStore replays + repopulates).
        _logger?.LogDebug("[hibernate] playlist-page rehydrate {Id}", XfadeLog.Tag(ViewModel.PlaylistId));
        Bindings?.Update();
    }

    // Same-tab navigation between two playlists reuses this Page instance and
    // never fires OnEntered — TabBarItem.Navigate routes through this method
    // instead. Without this override, clicking a different playlist from the
    // player bar / sidebar / search while PlaylistPage is the active tab content
    // silently drops the new parameter.
    public void RefreshWithParameter(object? parameter)
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.playlist.refreshWithParameter");
        EnterPlaylist(parameter, PageHostNavigationMode.Refresh);
    }

    private void EnterPlaylist(object? parameter, PageHostNavigationMode mode)
    {
        var navigationRevision = ++_navigationRevision;

        LoadParameter(parameter, mode);
        _ = ResetViewportAfterNavigationAsync(navigationRevision);
    }

    private async Task ResetViewportAfterNavigationAsync(int navigationRevision)
    {
        await Task.Yield();

        if (_isDisposed ||
            PageController.IsNavigatingAway ||
            navigationRevision != _navigationRevision)
        {
            return;
        }

        try
        {
            LeftColumnScrollView?.ScrollTo(
                0, 0,
                new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
            TrackGrid?.ScrollRowsToTop();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PlaylistPage scroll-to-top on navigation failed.");
        }
    }

    private async void LoadParameter(object? parameter, PageHostNavigationMode mode = PageHostNavigationMode.New)
    {
        // Snapshot the nav revision so a follow-on navigation that re-enters
        // LoadParameter can supersede this one cleanly at the Task.Yield()
        // checkpoint below.
        var loadRevision = _navigationRevision;

        _logger?.LogInformation(
            "PlaylistPage.LoadParameter: parameter type={Type}, value={Value}",
            parameter?.GetType().FullName ?? "<null>", parameter);

        // Drop any cover-edit preview overlay stranded from a previous playlist
        // (e.g. navigated away mid-upload before StartCoverEditAsync's finally
        // ran) so it can't render on top of this playlist's cover.
        ClearCoverPreview();

        // Reset shimmer / content visual state for the fresh load — mirrors
        // ArtistPage / AlbumPage so the next playlist fades in cleanly instead
        // of inheriting the previous playlist's already-shown content layer.
        // On cache-hit (Back/Forward), the visual tree is already realised and
        // populated — skip the shimmer reset to avoid flashing a skeleton over
        // good pixels.
        var navParameter = parameter as Data.Parameters.ContentNavigationParameter;

        // Reveal mode (mirrors AlbumPage):
        //  • Back/Forward — keep rendered pixels.
        //  • Same-tab refresh with content showing + usable prefill — warm
        //    content cross-fade, no skeleton over good pixels.
        //  • New / cold — shimmer-gated reveal.
        var useWarmSwap =
            mode == PageHostNavigationMode.Refresh &&
            PageController.IsShowingContent &&
            navParameter is not null &&
            HasUsablePlaylistPrefill(navParameter);

        if (mode != PageHostNavigationMode.Back &&
            mode != PageHostNavigationMode.Forward)
        {
            if (useWarmSwap)
                PageController.CrossfadeContentSwap();
            else
                PageController.ResetForNewLoad();
        }

        // Yield once between the shimmer flip and the Activate / PrefillFrom
        // / data-fetch chain below. The framework runs OnNavigatedTo →
        // LoadParameter synchronously up to the first await, so without this
        // explicit yield DWM never gets a frame to paint the just-armed
        // shimmer OR the page-entrance fade's first percent — the user sees
        // the page pop in fully rendered after the UI thread completes the
        // sync sweep. The revision guard makes a follow-on nav supersede
        // this in-flight invocation cleanly.
        await Task.Yield();
        if (_isDisposed || PageController.IsNavigatingAway || loadRevision != _navigationRevision)
            return;

        string? playlistId = null;

        if (navParameter is { } nav)
        {
            _logger?.LogInformation(
                "PlaylistPage.LoadParameter: ContentNavigationParameter Uri='{Uri}', Title='{Title}', Subtitle='{Subtitle}', ImageUrl='{ImageUrl}'",
                nav.Uri, nav.Title, nav.Subtitle, nav.ImageUrl);
            playlistId = nav.Uri;
            ViewModel.Activate(nav.Uri, prefill: nav);
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
            }
            RestorePlaylistPanelWidth(playlistId);
        }

        // Warm-cache trigger. PlaylistStore is a BehaviorSubject — Activate's subscribe
        // queues ApplyDetailState via the dispatcher, which runs after this method
        // returns. After one yield it has landed (PlaylistName populated, IsLoading
        // stayed false), so TryShowContentNow can fire ScheduleCrossfade for the
        // same-id case where the IsLoading=false write was a no-op. On a warm swap
        // CrossfadeContentSwap already marked content shown, so TryShowContentNow
        // early-returns.
        await Task.Yield();
        if (PageController.IsNavigatingAway) return;
        PageController.TryShowContentNow();
    }

    // True when the nav parameter carries enough to paint the hero immediately,
    // so a same-type refresh can warm-swap instead of resetting to the shimmer.
    private static bool HasUsablePlaylistPrefill(Data.Parameters.ContentNavigationParameter nav)
        => (!string.IsNullOrEmpty(nav.Title) || !string.IsNullOrEmpty(nav.ImageUrl))
           && !SpotifyImageHelper.IsMosaicUri(nav.ImageUrl);

    public void OnLeaving()
    {
        using var _stage = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance?.StageCurrent("page.playlist.onLeaving");
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

    /// <summary>
    /// Fade-in the container that just became visible because LayoutMode
    /// changed. Common case — VM's prediction in Activate matched the
    /// authoritative value set in ApplyDetail — runs once at activation
    /// when nothing was visible yet (initial Visibility binding settles to
    /// Visible at the same time the fade starts; fade is invisible because
    /// the container wasn't on screen yet).
    /// Mismatch case (URI heuristic was wrong, ~5% of cold loads): one
    /// container Visibility binding flips from Visible to Collapsed (the
    /// previously-shown wrong-mode container disappears instantly) and the
    /// other flips from Collapsed to Visible — that one fades in over
    /// 280 ms, which masks the snap.
    /// Defensive: skip the animation if the target element isn't yet in
    /// the visual tree (early in page lifecycle, before InitializeComponent
    /// has fully wired x:Name'd fields).
    /// </summary>
    private async void OnLayoutModeChanged()
    {
        if (_isDisposed) return;

        ApplyHeaderPlacement();

        // Cover + title render in both modes now, so the left-column cover block
        // is the reveal target regardless of layout mode. The banner (banner mode
        // only) reveals via the grid's in-rows header projection.
        FrameworkElement? target = CoverHeroBlock;

        if (target is null) return;

        // Reset opacity to 0 then animate to 1 so the container fades in
        // smoothly even if it had been Visible at full opacity moments ago.
        target.Opacity = 0;
        try
        {
            await CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(280))
                .StartAsync(target);
        }
        catch
        {
            // Animation can throw if the element unloads mid-animation
            // (page navigation away). Restore opacity defensively.
        }

        // StartAsync can complete without visibly applying the final value when
        // the target was Collapsed or not fully attached during the layout-mode
        // swap. Always restore the final state so a Banner -> Cover correction
        // cannot leave the square artwork block transparent while its Image has
        // successfully loaded.
        if (!_isDisposed)
        {
            target.Opacity = 1;
            QueueVisualSettlingPass();
        }
    }

    private void QueueVisualSettlingPass()
    {
        if (_isDisposed || DispatcherQueue is null)
            return;

        var generation = ++_visualSettlingGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            await Task.Yield();
            if (_isDisposed ||
                PageController.IsNavigatingAway ||
                generation != _visualSettlingGeneration)
            {
                return;
            }

            HeroBannerRow?.InvalidateMeasure();
            HeaderBackgroundHost?.InvalidateMeasure();
            CoverHeroBlock?.InvalidateMeasure();
            TwoColumnGrid?.InvalidateMeasure();
            LeftColumnHost?.InvalidateMeasure();

            await Task.Delay(16).ConfigureAwait(true);
            if (_isDisposed ||
                PageController.IsNavigatingAway ||
                generation != _visualSettlingGeneration)
            {
                return;
            }

            PageController.TryShowContentNow();
        });
    }

    private void HeaderBackgroundHost_Loaded(object sender, RoutedEventArgs e)
    {
        // First-time setup: build the composition tree. Must happen after the element
        // is parented (hence Loaded, not the constructor).
        if (_heroCompositor == null)
        {
            var visual = ElementCompositionPreview.GetElementVisual(HeaderBackgroundHost);
            _heroCompositor = visual.Compositor;

            // ── Hero image sprite ──────────────────────────────────────────────
            // Banner is full-width and 280 px tall. Center-anchor the source
            // image's horizontal crop — Spotify's editorial banners are
            // composed with the focal point centered. Vertical alignment 0.5
            // so the middle band of the source shows.
            _heroSurfaceBrush = _heroCompositor.CreateSurfaceBrush();
            _heroSurfaceBrush.Stretch = CompositionStretch.UniformToFill;
            _heroSurfaceBrush.HorizontalAlignmentRatio = 0.5f;
            _heroSurfaceBrush.VerticalAlignmentRatio = 0.5f;

            // No mask brush wrapper on the image — the image fills the host
            // edge to edge. The scrim sprite below provides the bottom-fade
            // legibility / page-surface handoff.
            _heroSprite = _heroCompositor.CreateSpriteVisual();
            _heroSprite.Brush = _heroSurfaceBrush;
            _heroSprite.RelativeSizeAdjustment = Vector2.One;

            // ── Bottom-fade scrim (composition sibling, layered above image) ──
            // Vertical linear gradient: transparent at top → theme surface
            // color at the bottom. Stops are theme-aware via
            // ApplyHeroScrimForTheme below; runtime theme flips re-seed the
            // colors. This lives on the composition layer (not in XAML)
            // because composition-side gradients render edge-to-edge of the
            // host's actual size (no measure/arrange race when the host's
            // Visibility flips), and the brush sits in the same visual tree
            // as the image so they always paint together.
            _heroScrimBrush = _heroCompositor.CreateLinearGradientBrush();
            _heroScrimBrush.StartPoint = new Vector2(0.5f, 0f);
            _heroScrimBrush.EndPoint = new Vector2(0.5f, 1f);
            _heroScrimTopStop = _heroCompositor.CreateColorGradientStop(0f,
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _heroScrimMidStop = _heroCompositor.CreateColorGradientStop(0.45f,
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _heroScrimBottomStop = _heroCompositor.CreateColorGradientStop(1f,
                Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _heroScrimBrush.ColorStops.Add(_heroScrimTopStop);
            _heroScrimBrush.ColorStops.Add(_heroScrimMidStop);
            _heroScrimBrush.ColorStops.Add(_heroScrimBottomStop);

            _heroScrimSprite = _heroCompositor.CreateSpriteVisual();
            _heroScrimSprite.Brush = _heroScrimBrush;
            _heroScrimSprite.RelativeSizeAdjustment = Vector2.One;

            _heroContainer = _heroCompositor.CreateContainerVisual();
            _heroContainer.RelativeSizeAdjustment = Vector2.One;
            _heroContainer.Children.InsertAtBottom(_heroSprite);     // image at back
            _heroContainer.Children.InsertAtTop(_heroScrimSprite);   // scrim in front

            ApplyHeroScrimForTheme();
        }

        // (Re)attach the container on every Loaded. WinUI detaches the child visual when
        // the element unloads (cached-page nav-away), so a plain "already set up, skip"
        // early return would leave the host blank on subsequent returns to this page.
        if (_heroContainer != null)
            ElementCompositionPreview.SetElementChildVisual(HeaderBackgroundHost, _heroContainer);

        // Theme could have changed while the page was cached — refresh the scrim color.
        ApplyHeroScrimForTheme();

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
        if (_heroImageSurface is not null)
        {
            _heroImageSurface.LoadCompleted -= OnHeroImageLoadCompleted;
            _heroImageSurface.Dispose();
            _heroImageSurface = null;
        }
        _appliedHeroUrl = null;

        if (_heroSurfaceBrush != null)
            _heroSurfaceBrush.Surface = null;
    }

    private void ApplyHeaderBackground()
    {
        if (_heroSurfaceBrush == null || _heroCompositor == null) return;

        var url = ViewModel.Header.HeaderImageUrl;

        // No-op if the URL hasn't changed — PropertyChanged can fire redundantly
        // when the ViewModel re-assigns the same value during a refresh cycle.
        if (string.Equals(_appliedHeroUrl, url, StringComparison.Ordinal))
            return;

        if (_heroImageSurface is not null)
        {
            _heroImageSurface.LoadCompleted -= OnHeroImageLoadCompleted;
            _heroImageSurface.Dispose();
            _heroImageSurface = null;
        }

        var httpsUrl = string.IsNullOrEmpty(url) ? null : SpotifyImageHelper.ToHttpsUrl(url);

        if (string.IsNullOrEmpty(httpsUrl))
        {
            _heroSurfaceBrush.Surface = null;
            _appliedHeroUrl = null;
            // No hero image → HasHeaderImage flips false in the VM and the
            // banner row swaps to AnimatedHeroBackground via its visibility
            // converter binding. Nothing to do here.
            return;
        }

        var desiredSize = new Windows.Foundation.Size(
            Math.Max(1, HeaderBackgroundHost.ActualWidth > 0 ? HeaderBackgroundHost.ActualWidth : 1600),
            Math.Max(1, HeaderBackgroundHost.ActualHeight > 0 ? HeaderBackgroundHost.ActualHeight : 280));
        _heroImageSurface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), desiredSize);
        // LoadCompleted fires once per surface (success OR fail). Without
        // this hook, a bad URL / network timeout left the brush silently
        // empty AND the AnimatedHeroBackground was hidden by the
        // HasHeaderImage gate — user saw a blank banner. On failure we null
        // the brush surface and clear HeaderImageUrl so the gradient
        // fallback takes over via the existing visibility binding.
        _heroImageSurface.LoadCompleted += OnHeroImageLoadCompleted;
        _heroSurfaceBrush.Surface = _heroImageSurface;
        _appliedHeroUrl = url;
    }

    private void OnHeroImageLoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
    {
        if (args.Status == LoadedImageSourceLoadStatus.Success) return;
        if (_isDisposed) return;

        _logger?.LogDebug(
            "Hero image decode failed for {Url}: status={Status}",
            _appliedHeroUrl ?? "<null>", args.Status);

        // Drop the broken surface so the brush reads as empty (and the
        // composition visual stops trying to render zero-pixel garbage).
        if (_heroSurfaceBrush != null)
            _heroSurfaceBrush.Surface = null;

        // Tell the VM the header image is unusable. The HasHeaderImage
        // notification flips AnimatedHeroBackground.Visibility on via its
        // existing InverseBoolToVisibility binding — user gets the palette
        // gradient instead of a blank banner.
        ViewModel.Header.HeaderImageUrl = null;
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
        var html = ViewModel?.Header.PlaylistDescription;
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
        if (ViewModel.Mutations.RenameCommand.CanExecute(newName))
            ViewModel.Mutations.RenameCommand.Execute(newName);
    }

    private void PlaylistDescriptionEditor_Committed(object? sender, string newDescription)
    {
        if (ViewModel.Mutations.UpdateDescriptionCommand.CanExecute(newDescription))
            ViewModel.Mutations.UpdateDescriptionCommand.Execute(newDescription);
    }

    // ── Cover photo edit (Cover layout mode only) ────────────────────────────
    // Handlers wire to the cover Grid in LeftColumnHost which is gated on
    // LayoutMode == Cover. Banner-mode playlists (editorial / radio) hide the
    // cover entirely so these never fire there. ViewModel.ChangeCoverCommand /
    // RemoveCoverCommand encode the SpClient upload pipeline.

    private async void CoverEditOverlay_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        await CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(120))
            .StartAsync(CoverEditOverlay);
    }

    private async void CoverEditOverlay_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Don't fade away while an upload is in flight — the spinner needs to stay visible.
        if (ViewModel.Mutations.IsUploadingCover) return;
        await CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120))
            .StartAsync(CoverEditOverlay);
    }

    private async void CoverEditOverlay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!ViewModel.Header.IsOwner || ViewModel.Mutations.IsUploadingCover) return;

        var file = await PickCoverFileAsync();
        if (file is null) return;

        await StartCoverEditAsync(file);
    }

    private void CoverEditOverlay_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (ViewModel.Header.IsOwner && !ViewModel.Mutations.IsUploadingCover
            && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Change cover photo";
            e.DragUIOverride.IsContentVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void CoverEditOverlay_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (!ViewModel.Header.IsOwner || ViewModel.Mutations.IsUploadingCover) return;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault(f => IsSupportedImage(f.FileType));
            if (file is not null) await StartCoverEditAsync(file);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Opens the reframe editor for <paramref name="file"/>; on confirm, previews the cropped
    /// result and pushes it through the cover-change command.
    /// </summary>
    private async Task StartCoverEditAsync(StorageFile file)
    {
        byte[]? jpegBytes;
        try
        {
            jpegBytes = await ImageReframeDialog.ShowAsync(
                XamlRoot, file,
                new ImageReframeOptions { Title = "Change cover photo", PrimaryButtonText = "Set photo" });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to open cover editor");
            Ioc.Default.GetService<INotificationService>()?
                .Show("Couldn't open that image", NotificationSeverity.Error, TimeSpan.FromSeconds(4));
            return;
        }

        if (jpegBytes is null) return;   // user cancelled

        try
        {
            await SetCoverPreviewFromBytesAsync(jpegBytes);

            CoverUploadRing.IsActive = true;
            CoverUploadRing.Visibility = Visibility.Visible;

            await ViewModel.Mutations.ChangeCoverCommand.ExecuteAsync(jpegBytes);
        }
        catch
        {
            // ChangeCoverCommand already toasts on failure; nothing extra here.
        }
        finally
        {
            // Always drop the local preview overlay — on success AND failure.
            // Leaving it set strands the uploaded bitmap on this reused
            // PlaylistPage instance, so it would render on top of every other
            // playlist's cover after navigation (the global cover-leak bug).
            ClearCoverPreview();
            CoverUploadRing.IsActive = false;
            CoverUploadRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SetCoverPreviewFromBytesAsync(byte[] jpegBytes)
    {
        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        using var ms = new MemoryStream(jpegBytes);
        await bmp.SetSourceAsync(ms.AsRandomAccessStream());
        CoverPreviewImage.Source = bmp;
        CoverPreviewImage.Visibility = Visibility.Visible;
    }

    private static bool IsSupportedImage(string fileType)
        => fileType.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp";

    private void CoverEditOverlay_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (!ViewModel.Header.IsOwner) return;

        var flyout = new MenuFlyout();
        var remove = new MenuFlyoutItem
        {
            Text = "Remove photo",
            Icon = new FontIcon { Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Delete }
        };
        remove.Click += (_, _) =>
        {
            ClearCoverPreview();
            if (ViewModel.Mutations.RemoveCoverCommand.CanExecute(null))
                ViewModel.Mutations.RemoveCoverCommand.Execute(null);
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

        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

        return await picker.PickSingleFileAsync();
    }

    private void ClearCoverPreview()
    {
        if (CoverPreviewImage is null) return;
        CoverPreviewImage.Source = null;
        CoverPreviewImage.Visibility = Visibility.Collapsed;
    }

    // Recommended Songs "+" — handled in code-behind so the command resolves
    // against the page's ViewModel. An ElementName binding to PageRoot does NOT
    // resolve inside the recommendations ItemsRepeater item template (separate
    // namescope), which left the button inert.
    private void RecommendationAdd_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Wavee.UI.Contracts.RecommendedTrackResult rec)
            return;
        var cmd = ViewModel?.Mutations?.AddRecommendationCommand;
        if (cmd?.CanExecute(rec) == true)
            cmd.Execute(rec);
    }

    // ── Overflow menu (per-permission) ───────────────────────────────────────

    private void OwnerOverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || !ViewModel.Header.HasOverflowItems) return;

        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        var addedAny = false;

        if (ViewModel.Header.CanEditCollaborative)
        {
            var toggleCollab = new MenuFlyoutItem
            {
                Text = ViewModel.Header.IsCollaborative ? "Make solo" : "Make collaborative",
                Icon = new FontIcon { Glyph = "" }
            };
            toggleCollab.Click += (_, _) =>
            {
                if (ViewModel.Mutations.ToggleCollaborativeCommand.CanExecute(null))
                    ViewModel.Mutations.ToggleCollaborativeCommand.Execute(null);
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

        if (ViewModel.Header.IsOwner)
        {
            var toggleVisibility = new MenuFlyoutItem
            {
                Text = ViewModel.Header.IsPublic ? "Make private" : "Make public",
                Icon = new FontIcon { Glyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Globe }
            };
            toggleVisibility.Click += (_, _) =>
            {
                if (ViewModel.Mutations.ToggleVisibilityCommand.CanExecute(null))
                    ViewModel.Mutations.ToggleVisibilityCommand.Execute(null);
            };
            flyout.Items.Add(toggleVisibility);
            addedAny = true;
        }

        if (ViewModel.Header.CanAdministratePermissions && ViewModel.Header.HasCollaborators)
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

        if (ViewModel.Header.CanCancelMembership)
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

        if (ViewModel.Header.CanDelete)
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

    private async void CopyToPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        var playlistId = ViewModel.PlaylistId;
        var mediator = Ioc.Default.GetService<IPlaylistDragDropMediator>();
        if (string.IsNullOrEmpty(playlistId) || mediator is null) return;

        // Same folder-aware menu the right-click track / card menus use: nests
        // folders, lists only owned playlists, offers "Create new playlist". The
        // track URIs of THIS playlist are resolved lazily (only when a destination
        // is picked) and copied via the shared AddToPlaylistSubmenuBuilder.
        var name = ViewModel.Header.PlaylistName ?? "playlist";
        var loader = AddToPlaylistSubmenuBuilder.Loader(
            sourceLabel: name,
            trackUrisLoader: ct => mediator.GetPlaylistTrackUrisAsync(playlistId, ct));
        var items = await loader();
        ContextMenuHost.Show(fe, items);
    }

    // ── Collaborator avatar stack ────────────────────────────────────────────

    private void RebuildCollaboratorStack()
    {
        const int MaxVisible = 4;

        CollaboratorStackHost.Children.Clear();

        var members = ViewModel.Header.Collaborators;
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
        else if (ViewModel.Header.IsCollaborative)
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
        ShowMembersFlyout(anchor, adminMode: ViewModel.Header.CanAdministratePermissions);
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
            Text = $"Members ({ViewModel.Header.Collaborators.Count})",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
        });

        foreach (var m in ViewModel.Header.Collaborators)
            content.Children.Add(BuildMemberRow(m, adminMode));

        flyout.Content = content;
        flyout.ShowAt(anchor);
    }

    private FrameworkElement BuildMemberRow(Wavee.UI.Contracts.PlaylistMemberResult member, bool adminMode)
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

        if (adminMode && member.Role != Wavee.UI.Contracts.PlaylistMemberRole.Owner)
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
                Wavee.UI.Contracts.PlaylistMemberRole.Contributor,
                Wavee.UI.Contracts.PlaylistMemberRole.Viewer,
                Wavee.UI.Contracts.PlaylistMemberRole.Blocked })
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = $"Make {role}",
                    IsChecked = member.Role == role
                };
                var captured = role;
                item.Click += (_, _) =>
                {
                    if (ViewModel.Header.SetMemberRoleCommand.CanExecute(null))
                        ViewModel.Header.SetMemberRoleCommand.Execute((member.UserId, captured));
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
                if (ViewModel.Header.RemoveMemberCommand.CanExecute(member.UserId))
                    ViewModel.Header.RemoveMemberCommand.Execute(member.UserId);
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
            var link = ViewModel.Header.LatestInviteLink;
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
                    await ViewModel.Header.CreateInviteLinkCommand.ExecuteAsync(TimeSpan.FromDays(7));
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
                    await ViewModel.Header.CreateInviteLinkCommand.ExecuteAsync(TimeSpan.FromDays(7));
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
            Title = AppLocalization.GetString("Playlist_LeaveTitle"),
            Content = AppLocalization.Format("Playlist_LeaveContent", ViewModel.Header.PlaylistName),
            PrimaryButtonText = AppLocalization.GetString("Dialog_Leave"),
            CloseButtonText = AppLocalization.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (ViewModel.Header.LeavePlaylistCommand.CanExecute(null))
        {
            await ViewModel.Header.LeavePlaylistCommand.ExecuteAsync(null);
            NavigationHelpers.OpenHome();
        }
    }

    private async void HeroDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmAndDeletePlaylistAsync();
    }

    private async void TrackGrid_TracksReorderRequested(int fromIndex, int length, int toIndex)
    {
        if (ViewModel is null) return;
        await ViewModel.TrackList.ReorderTracksAsync(fromIndex, length, toIndex);
    }

    private async System.Threading.Tasks.Task ConfirmAndDeletePlaylistAsync()
    {
        var dialog = new ContentDialog
        {
            Title = AppLocalization.GetString("Playlist_DeleteTitle"),
            Content = AppLocalization.Format("Playlist_DeleteContent", ViewModel.Header.PlaylistName),
            PrimaryButtonText = AppLocalization.GetString("Dialog_Delete"),
            CloseButtonText = AppLocalization.GetString("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        // Tint the primary button red so the destructive action reads correctly.
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        if (ViewModel.Mutations.DeletePlaylistCommand.CanExecute(null))
        {
            await ViewModel.Mutations.DeletePlaylistCommand.ExecuteAsync(null);
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
