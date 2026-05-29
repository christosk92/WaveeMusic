using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Wavee.Core.Playlists;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Controls.Sidebar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.DragDrop;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.ViewModels.Shell;

/// <summary>
/// Owns the entire sidebar surface: the items tree (Pinned / Your Library
/// / Playlists), persisted folder expansion state, pin/unpin coordination,
/// playlist-rootlist diff/refresh, mosaic icon refresh, library badge counts,
/// selection persistence, drag-time folder auto-expand, and the canonical
/// alias-selection cascade (so Liked Songs lights up whether the user clicks
/// the pinned row or the Your-Library row).
///
/// <para>Extracted from <c>ShellViewModel</c> as part of the shell decomposition.
/// The parent VM still owns library-change-bus dispatch + tab state; this
/// VM is invoked by the parent on the relevant ticks (sync started / completed,
/// library data changed, sign-out).</para>
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class SidebarViewModel : ObservableObject
{
    private readonly ILibraryDataService _libraryDataService;
    private readonly IPinService _pinService;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IShellSessionService _shellSession;
    private readonly INotificationService _notificationService;
    private readonly DragStateService? _dragStateService;
    private readonly PlaylistMosaicService? _mosaicService;
    private readonly IDispatcherService? _dispatcher;
    private readonly ILogger? _logger;

    // Tag → SidebarItemModel index covering the top-level section rows and
    // their immediate (static) children. Built once by IndexSidebarTree after
    // InitializeSidebarItems. Playlist / pinned rows are NOT indexed here —
    // they come and go; the existing recursive FindSidebarItemByTag still
    // handles them.
    private readonly Dictionary<string, SidebarItemModel> _sidebarItemsByTag = new(StringComparer.Ordinal);

    // UI element references for cleanup (built lazily for the Playlists "+" decorator).
    private Button? _playlistsAddButton;
    private MenuFlyoutItem? _newPlaylistMenuItem;
    private MenuFlyoutItem? _newFolderMenuItem;

    // Drag-time auto-expand snapshot.
    private Dictionary<string, bool>? _preDragFolderExpansion;
    private bool _suppressExpansionPersistence;

    /// <summary>
    /// Cancelled on every new OnPlaylistsChanged tick so bursts of dealer events collapse
    /// into a single rebuild ~<see cref="PlaylistRefreshDebounceMs"/> after the last event.
    /// Rebuilding the sidebar is expensive (N SidebarItemModels + connector strips); the
    /// previous "rebuild on every event" path was the main culprit for app-wide slowness.
    /// </summary>
    private CancellationTokenSource? _playlistRefreshCts;

    // Subscription that re-builds a sidebar mosaic when its playlist's items
    // change (Mercury push → PlaylistDiffApplier mutates Items → Updated event
    // fires → we rebuild the composite). Without this, the cached mosaic
    // keeps showing the old top-4 album covers until app restart.
    private IDisposable? _playlistMosaicChangesSubscription;
    private const int PlaylistRefreshDebounceMs = 250;

    internal const string SidebarPinDropZoneTag = "pin-drop-zone";

    public SidebarViewModel(
        ILibraryDataService libraryDataService,
        IPinService pinService,
        IPlaylistCacheService playlistCache,
        IShellSessionService shellSession,
        INotificationService notificationService,
        DragStateService? dragStateService,
        PlaylistMosaicService? mosaicService,
        IDispatcherService? dispatcher,
        ILogger? logger)
    {
        _libraryDataService = libraryDataService;
        _pinService = pinService;
        _playlistCache = playlistCache;
        _shellSession = shellSession;
        _notificationService = notificationService;
        _dragStateService = dragStateService;
        _mosaicService = mosaicService;
        _dispatcher = dispatcher;
        _logger = logger;

        // Drag-state hook: while ANY drag is in progress, expand every
        // sidebar folder so deeply-nested folders become drop targets
        // without requiring the user to hover each folder for the
        // auto-expand-on-hover timeout. Restore pre-drag state on drag end.
        // The expansion changes are suppressed from persistence
        // (OnSidebarGroupPropertyChanged checks _suppressExpansionPersistence)
        // so the user's saved layout isn't trampled.
        if (_dragStateService is not null)
            _dragStateService.DragStateChanged += OnGlobalDragStateChanged;

        // Per-playlist change → sidebar mosaic refresh. PlaylistDiffApplier
        // updates Items in the cache after a Mercury push, but the sidebar's
        // cached IconSource keeps pointing at the old composite forever
        // (LazyIconSourceLoader is cleared on first load). We listen for
        // Updated events here, drop the in-flight + on-disk mosaic via
        // PlaylistMosaicService.Invalidate, then kick off a fresh build and
        // swap model.IconSource — the SidebarItem control listens for that
        // PropertyChanged and re-renders the icon.
        // Subscribe regardless of mosaic-service availability — the handler's
        // first phase promotes real covers from the cache (works with or without
        // the mosaic service), and the second phase only kicks in when a mosaic
        // is actually appropriate.
        _playlistMosaicChangesSubscription = _playlistCache.Changes
            .Where(static evt => evt.Kind == PlaylistChangeKind.Updated
                              && !string.IsNullOrEmpty(evt.Uri))
            .Subscribe(evt => OnPlaylistContentsChanged(evt.Uri));

        InitializeSidebarItems();
        ApplyPersistedSidebarState();
    }

    // ── Bindable state (XAML binds via Vm.Sidebar.X) ────────────────────────

    [ObservableProperty]
    public partial ObservableCollection<SidebarItemModel> SidebarItems { get; set; } = [];

    [ObservableProperty]
    public partial ISidebarItemModel? SelectedSidebarItem { get; set; }

    // ── Lifecycle hooks invoked by the parent ───────────────────────────────

    /// <summary>
    /// Clears library badge counts. Called when a library sync starts so the
    /// old counts don't linger while the new ones are being computed.
    /// </summary>
    public void ClearLibraryBadges()
    {
        var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
        if (librarySection?.Children is ObservableCollection<SidebarItemModel> libraryChildren)
        {
            foreach (var item in libraryChildren)
                item.BadgeCount = null;
        }
    }

    /// <summary>
    /// Clears badges + the playlist / pinned children. Used on sign-out so
    /// the next user doesn't briefly see the previous user's library state.
    /// </summary>
    public void ClearLibrarySidebar()
    {
        ClearLibraryBadges();

        if (_sidebarItemsByTag.TryGetValue("Playlists", out var playlistsSection)
            && playlistsSection.Children is ObservableCollection<SidebarItemModel> playlistChildren)
        {
            playlistChildren.Clear();
        }

        if (_sidebarItemsByTag.TryGetValue("Pinned", out var pinnedSection)
            && pinnedSection.Children is ObservableCollection<SidebarItemModel> pinnedChildren)
        {
            pinnedChildren.Clear();
        }
    }

    /// <summary>
    /// Initial library load — runs stats + playlists + pinned in parallel,
    /// then updates the library badge counts.
    /// </summary>
    public async Task LoadLibraryDataAsync()
    {
        try
        {
            // Stats run in parallel with the two-phase playlist refresh. The
            // playlists fan-out (cache + network) is owned by RefreshPlaylistsAsync
            // so cold-launch shimmer / warm-launch instant-hydrate behave identically
            // here and on subsequent PlaylistsChanged events.
            var statsTask = _libraryDataService.GetStatsAsync();
            var playlistsRefreshTask = RefreshPlaylistsAsync();
            var pinnedRefreshTask = RefreshPinnedAsync();

            await Task.WhenAll(statsTask, playlistsRefreshTask, pinnedRefreshTask);

            var stats = await statsTask;

            // Update "Your Library" section badges
            var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
            if (librarySection?.Children is ObservableCollection<SidebarItemModel> libraryChildren)
            {
                var albumsItem = libraryChildren.FirstOrDefault(x => x.Tag as string == "Albums");
                if (albumsItem != null) albumsItem.BadgeCount = stats.AlbumCount;

                var artistsItem = libraryChildren.FirstOrDefault(x => x.Tag as string == "Artists");
                if (artistsItem != null) artistsItem.BadgeCount = stats.ArtistCount;

                var likedItem = libraryChildren.FirstOrDefault(x => x.Tag as string == "LikedSongs");
                if (likedItem != null) likedItem.BadgeCount = stats.LikedSongsCount;

                var podcastsItem = libraryChildren.FirstOrDefault(x => x.Tag as string is "Podcasts" or "YourEpisodes");
                if (podcastsItem != null) podcastsItem.BadgeCount = stats.PodcastCount;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load library data");
            _notificationService.Show(AppLocalization.GetString("Shell_LoadLibraryFailed"), Wavee.UI.WinUI.Data.Models.NotificationSeverity.Error);
        }
    }

    /// <summary>
    /// Handles a <see cref="Wavee.UI.Services.Infra.ChangeScope.Library"/>
    /// tick — refreshes the four library badge counts (Albums / Artists /
    /// Liked Songs / Podcasts) plus the pinned section. Cheap; the heavy
    /// playlists rebuild only happens via <see cref="OnPlaylistsChanged"/>.
    /// </summary>
    public void OnLibraryDataChanged()
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            try { await RefreshLibraryBadgesAsync(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Sidebar badge refresh failed"); }
            try { await RefreshPinnedAsync(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Sidebar pinned refresh failed"); }
        });
    }

    /// <summary>
    /// Debounced playlist refresh — collapses dealer bursts into a single
    /// rebuild ~<see cref="PlaylistRefreshDebounceMs"/> ms after the last event.
    /// </summary>
    public void OnPlaylistsChanged()
    {
        var previous = Interlocked.Exchange(ref _playlistRefreshCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var token = _playlistRefreshCts!.Token;
        _dispatcher?.TryEnqueue(async () =>
        {
            try
            {
                await Task.Delay(PlaylistRefreshDebounceMs, token);
                await RefreshPlaylistsAsync();
            }
            catch (OperationCanceledException) { /* superseded by a newer event */ }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to handle playlists change event");
                _notificationService.Show(AppLocalization.GetString("Shell_RefreshPlaylistsFailed"), Wavee.UI.WinUI.Data.Models.NotificationSeverity.Error);
            }
        });
    }

    // ── Pin / unpin handling ────────────────────────────────────────────────

    /// <summary>
    /// Handles a click on the inline pin/unpin button on any sidebar row.
    /// For Pinned-section rows (Tag = the raw URI), always unpins. For
    /// canonical Your-Library rows (Tag = "LikedSongs" / "Podcasts"), maps to
    /// the pseudo-URI and unpins only when currently pinned. Optimistic: the
    /// local DB is updated by the service before the network call.
    /// </summary>
    public async Task HandleSidebarPinButtonAsync(SidebarItemModel model)
    {
        if (model is null) return;

        if (model.ShowUnpinButton)
        {
            // Pinned-section row — Tag IS the URI we want to unpin.
            if (string.IsNullOrEmpty(model.Tag)) return;
            try
            {
                var ok = await _pinService.UnpinAsync(model.Tag);
                if (!ok)
                    NotifyPinFailure(unpinned: true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unpin failed for {Uri}", model.Tag);
                NotifyPinFailure(unpinned: true);
            }
            return;
        }

        if (!model.ShowPinToggleButton) return;

        // Canonical YL row — map the row tag to its pseudo-URI.
        var uri = model.Tag switch
        {
            "LikedSongs" => "spotify:collection",
            "Podcasts" or "YourEpisodes" => "spotify:collection:your-episodes",
            _ => null
        };
        if (uri is null) return;

        var wasPinned = _pinService.IsPinned(uri);
        if (!wasPinned)
            return;

        try
        {
            var ok = await _pinService.UnpinAsync(uri);
            if (!ok)
                NotifyPinFailure(unpinned: wasPinned);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pin toggle failed for {Uri}", uri);
            NotifyPinFailure(unpinned: wasPinned);
        }
    }

    private void NotifyPinFailure(bool unpinned)
    {
        // Toast surfaces the rollback to the user: the optimistic local write
        // already reverted inside SpotifyLibraryService, so the sidebar shows
        // the correct (unchanged) state — this message just explains why.
        var message = unpinned
            ? "Couldn't unpin from the sidebar. Check your connection and try again."
            : "Couldn't pin to the sidebar. Check your connection and try again.";
        _notificationService.Show(message, Wavee.UI.WinUI.Data.Models.NotificationSeverity.Warning);
    }

    // ── Selection sync ──────────────────────────────────────────────────────

    /// <summary>
    /// Sync the sidebar selection to the playlist identified by <paramref name="uriOrId"/>.
    /// Accepts a bare playlist id, a Spotify URI (<c>spotify:playlist:xxx</c>), or a
    /// <see cref="ContentNavigationParameter"/> carrying either. The id segment after the
    /// last <c>:</c> is extracted before looking up the sidebar row.
    /// Clears the selection when no sidebar row matches — e.g. a search-opened playlist
    /// that isn't in the user's library.
    /// </summary>
    public void SyncSidebarSelectionToPlaylist(object? uriOrId)
    {
        var s = uriOrId switch
        {
            ContentNavigationParameter nav => nav.Uri,
            string value => value,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(s))
        {
            // Only assign if not already null — otherwise the setter still fires
            // PropertyChanged and cascades to every realized SidebarItem.
            if (SelectedSidebarItem is not null)
                SelectedSidebarItem = null;
            return;
        }

        var trimmed = s.Trim();
        var match = FindSidebarItemByTag(trimmed);
        if (match is null)
        {
            var lastColon = trimmed.LastIndexOf(':');
            var id = lastColon >= 0 ? trimmed[(lastColon + 1)..] : trimmed;

            if (!string.Equals(id, trimmed, StringComparison.Ordinal))
                match = FindSidebarItemByTag(id);

            if (match is null && !trimmed.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                match = FindSidebarItemByTag($"spotify:playlist:{id}");
        }

        // De-dup: if the resolved item is already selected, skip the assignment.
        // The SelectedSidebarItem setter fires PropertyChanged unconditionally, and
        // every realized SidebarItem reacts via the SidebarView.SelectedItemProperty
        // PropertyChangedCallback (running VisualStateManager.GoToState + folder
        // glyph swaps synchronously). Without this guard, every nav — including
        // tab-switches and re-clicks of the currently-selected playlist — produces
        // a visible folder-flash cascade across the entire sidebar tree.
        if (ReferenceEquals(SelectedSidebarItem, match)) return;

        SelectedSidebarItem = match;
    }

    public void SyncSidebarSelectionToTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            if (SelectedSidebarItem is not null)
                SelectedSidebarItem = null;
            return;
        }

        var match = FindSidebarItemByTag(tag);
        if (ReferenceEquals(SelectedSidebarItem, match)) return;

        SelectedSidebarItem = match;
    }

    partial void OnSelectedSidebarItemChanged(ISidebarItemModel? value)
    {
        // Navigation is handled in ShellPage.SidebarControl_ItemInvoked
        // to support modifier keys (Ctrl/middle-click for new tab)
        _shellSession.UpdateSelectedSidebarTag((value as SidebarItemModel)?.Tag);
        UpdateAliasSelections(value);
    }

    /// <summary>
    /// Walks the sidebar tree and toggles <see cref="SidebarItemModel.IsAliasSelected"/>
    /// on rows that aren't the primary selection but represent the same logical
    /// destination — e.g. when the pinned <c>spotify:collection</c> row is selected,
    /// the Your-Library "Liked Songs" row also lights up. Without this, only one of
    /// the two surfaces shows the selected indicator even though both point at the
    /// same page.
    /// </summary>
    private void UpdateAliasSelections(ISidebarItemModel? selected)
    {
        var selectedTag = (selected as SidebarItemModel)?.Tag;
        ApplyAliasSelections(SidebarItems, selected, selectedTag);
    }

    private static void ApplyAliasSelections(
        IEnumerable<SidebarItemModel> items,
        ISidebarItemModel? selected,
        string? selectedTag)
    {
        foreach (var item in items)
        {
            var isAlias = !ReferenceEquals(item, selected)
                && selectedTag is { Length: > 0 }
                && !string.IsNullOrEmpty(item.Tag)
                && AreEquivalentSidebarTags(selectedTag, item.Tag!);

            if (item.IsAliasSelected != isAlias)
                item.IsAliasSelected = isAlias;

            if (item.Children is IEnumerable<SidebarItemModel> children)
                ApplyAliasSelections(children, selected, selectedTag);
        }
    }

    private static bool AreEquivalentSidebarTags(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return false;
        return IsAliasOf(a, b) || IsAliasOf(b, a);
    }

    private static bool IsAliasOf(string canonical, string candidate)
    {
        return canonical switch
        {
            "LikedSongs" =>
                candidate == "spotify:collection"
                || (candidate.StartsWith("spotify:user:", StringComparison.Ordinal)
                    && candidate.EndsWith(":collection", StringComparison.Ordinal)),
            "Podcasts" =>
                candidate == "spotify:collection:your-episodes",
            _ => false
        };
    }

    // ── Static init + indexing ──────────────────────────────────────────────

    private void InitializeSidebarItems()
    {
        SidebarItems = Wavee.UI.WinUI.Helpers.Sidebar.SidebarTreeBuilder.Build(
            buildPinDropZoneRow: BuildPinDropZoneRow,
            createComingSoonBadge: CreateComingSoonBadge,
            createPlaylistsAddButton: CreatePlaylistsAddButton);

        foreach (var group in SidebarItems)
            group.PropertyChanged += OnSidebarGroupPropertyChanged;

        IndexSidebarTree();
    }

    /// <summary>
    /// Build the O(1) tag→item lookup over the static sidebar shape (top-level
    /// sections + their immediate children). Playlist / pinned entries are
    /// excluded — those are dynamic and still walked by the recursive
    /// <see cref="FindSidebarItemByTag(string)"/> when a deep lookup is needed.
    /// </summary>
    private void IndexSidebarTree()
    {
        _sidebarItemsByTag.Clear();
        foreach (var section in SidebarItems)
        {
            if (section.Tag is string sectionTag)
                _sidebarItemsByTag[sectionTag] = section;
            // Only walk immediate children — playlist / pinned rows are dynamic
            // and would invalidate the index. Library badges (Albums, Artists,
            // LikedSongs, Podcasts, LocalFiles) are static after init.
            if (section.Children is IEnumerable<SidebarItemModel> children)
            {
                foreach (var child in children)
                {
                    if (child.Tag is string childTag)
                        _sidebarItemsByTag[childTag] = child;
                }
            }
        }
    }

    private void ApplyPersistedSidebarState()
    {
        foreach (var group in SidebarItems)
        {
            ApplyPersistedSidebarState(group);
        }

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
            SelectedSidebarItem = FindSidebarItemByTag(selectedTag);
    }

    private void ApplyPersistedSidebarState(SidebarItemModel item)
    {
        if (item.Tag is string tag && _shellSession.TryGetSidebarGroupExpansion(tag, out var isExpanded))
            item.IsExpanded = isExpanded;

        if (item.Children is IEnumerable<SidebarItemModel> children)
        {
            foreach (var child in children)
                ApplyPersistedSidebarState(child);
        }
    }

    public SidebarItemModel? FindSidebarItemByTag(string tag)
    {
        // O(1) for the static rows (top-level sections + library children);
        // falls through to the O(n) recursive walk for dynamic rows
        // (playlists, pinned, folder contents) that aren't in the index.
        if (_sidebarItemsByTag.TryGetValue(tag, out var hit))
            return hit;
        return FindSidebarItemByTag(SidebarItems, tag);
    }

    private static SidebarItemModel? FindSidebarItemByTag(IEnumerable<SidebarItemModel> items, string tag)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Tag, tag, StringComparison.Ordinal))
                return item;

            if (item.Children is IEnumerable<SidebarItemModel> children)
            {
                var match = FindSidebarItemByTag(children, tag);
                if (match != null)
                    return match;
            }
        }

        return null;
    }

    // ── Folder expansion persistence + drag auto-expand ─────────────────────

    private void OnSidebarGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SidebarItemModel group
            || e.PropertyName != nameof(SidebarItemModel.IsExpanded)
            || string.IsNullOrWhiteSpace(group.Tag))
        {
            return;
        }

        // Folders: swap Fluent Folder (E8B7) ↔ FolderOpen (E838) so the tree glyph matches state.
        // FontFamily re-pinned on each replacement — without it the new IconSource
        // inherits ContentControlThemeFontFamily (a text font) and the glyph renders as tofu.
        if (group.IsFolder)
            group.IconSource = new FontIconSource
            {
                Glyph = group.IsExpanded ? FluentGlyphs.FolderOpen : FluentGlyphs.Folder,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
            };

        // Suppress persistence during drag-driven auto-expand. The drag flow
        // owns the snapshot/restore; if we let it write through to
        // _shellSession the user's saved layout gets clobbered when they end
        // a drag with folders that were collapsed before the drag started.
        if (_suppressExpansionPersistence) return;

        _shellSession.UpdateSidebarGroupExpansion(group.Tag!, group.IsExpanded);
    }

    private void OnGlobalDragStateChanged(bool isDragging)
    {
        // Marshal to UI thread — SidebarItemModel changes flow into XAML bindings.
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (dq is null)
        {
            ApplyDragExpansion(isDragging);
            return;
        }
        dq.TryEnqueue(() => ApplyDragExpansion(isDragging));
    }

    private void ApplyDragExpansion(bool isDragging)
    {
        if (isDragging)
        {
            // Re-entrancy guard: rapid drag-start/end pairs while a snapshot
            // is mid-restore would mix states. Skip if a snapshot exists.
            if (_preDragFolderExpansion is not null) return;

            _preDragFolderExpansion = new Dictionary<string, bool>(StringComparer.Ordinal);
            _suppressExpansionPersistence = true;
            try
            {
                ForEachFolder(item =>
                {
                    if (string.IsNullOrEmpty(item.Tag)) return;
                    _preDragFolderExpansion[item.Tag!] = item.IsExpanded;
                    if (!item.IsExpanded) item.IsExpanded = true;
                });
            }
            finally
            {
                _suppressExpansionPersistence = false;
            }
        }
        else
        {
            if (_preDragFolderExpansion is null) return;

            _suppressExpansionPersistence = true;
            try
            {
                foreach (var (tag, prev) in _preDragFolderExpansion)
                {
                    var item = FindSidebarItemByTag(tag);
                    if (item is null) continue;
                    if (item.IsExpanded != prev) item.IsExpanded = prev;
                }
            }
            finally
            {
                _suppressExpansionPersistence = false;
                _preDragFolderExpansion = null;
            }
        }
    }

    /// <summary>
    /// Walk every folder-kind sidebar item (including nested subfolders) and
    /// invoke the action. Used by the drag-time auto-expand path.
    /// </summary>
    private void ForEachFolder(Action<SidebarItemModel> action)
    {
        foreach (var root in SidebarItems)
            WalkInto(root);

        void WalkInto(SidebarItemModel item)
        {
            if (item.IsFolder) action(item);
            if (item.Children is System.Collections.IEnumerable kids)
            {
                foreach (var c in kids)
                    if (c is SidebarItemModel child) WalkInto(child);
            }
        }
    }

    // ── Decorator / placeholder factories ───────────────────────────────────

    private static FrameworkElement CreateComingSoonBadge()
    {
        static Brush ResourceBrush(string key, Color fallback)
        {
            return Application.Current?.Resources.TryGetValue(key, out var value) == true
                   && value is Brush brush
                ? brush
                : new SolidColorBrush(fallback);
        }

        return new Border
        {
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(10),
            Background = ResourceBrush("SubtleFillColorSecondaryBrush", Microsoft.UI.ColorHelper.FromArgb(0x22, 0x7F, 0x7F, 0x7F)),
            Child = new TextBlock
            {
                Text = "Soon",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResourceBrush("TextFillColorSecondaryBrush", Microsoft.UI.Colors.Gray)
            }
        };
    }

    private FrameworkElement CreatePlaylistsAddButton()
    {
        var menuFlyout = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };

        var mediaPlayerIconsFont = Application.Current?.Resources?.TryGetValue("MediaPlayerIconsFontFamily", out var fontObj) == true
            ? fontObj as FontFamily
            : null;

        _newPlaylistMenuItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("Shell_NewPlaylist"),
            Icon = new FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = FluentGlyphs.CreatePlaylist }
        };
        _newPlaylistMenuItem.Click += NewPlaylistMenuItem_Click;

        _newFolderMenuItem = new MenuFlyoutItem
        {
            Text = AppLocalization.GetString("Shell_NewFolder"),
            Icon = new FontIcon { FontFamily = mediaPlayerIconsFont, Glyph = FluentGlyphs.CreateFolder }
        };
        _newFolderMenuItem.Click += NewFolderMenuItem_Click;

        menuFlyout.Items.Add(_newPlaylistMenuItem);
        menuFlyout.Items.Add(_newFolderMenuItem);

        // Plain Button + Flyout (not SplitButton) so a click anywhere on the icon opens
        // the menu — same affordance whether the sidebar is expanded or compact (where
        // the only the decorator survives the CompactGroupHeaderWithDecorator state).
        _playlistsAddButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = FluentGlyphs.CreatePlaylist,
                FontSize = 12
            },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 24,
            MinHeight = 24,
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Flyout = menuFlyout,
            // Suppress the default WinUI focus rectangle — it's a saturated
            // accent-coloured rect that reads identically to the sidebar's
            // selected-item border, making the "+" look perpetually selected
            // after any interaction lands focus on it.
            UseSystemFocusVisuals = false
        };

        return _playlistsAddButton;
    }

    private void NewPlaylistMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Wavee.UI.WinUI.Helpers.Navigation.NavigationHelpers.OpenCreatePlaylist(isFolder: false);
    }

    private void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Wavee.UI.WinUI.Helpers.Navigation.NavigationHelpers.OpenCreatePlaylist(isFolder: true);
    }

    // ── Playlists refresh + diff ────────────────────────────────────────────

    private async Task RefreshPlaylistsAsync()
    {
        try
        {
            // Phase 1 — cache-only. SQLite + hot in-memory only, never the network.
            // When the user has been signed in before, this hydrates the sidebar in
            // a few ms. When the cache is empty (cold launch / signed-out), both
            // helpers return null and the shimmer stays visible until Phase 2.
            var cachedPlaylistsTask = _libraryDataService.TryGetUserPlaylistsFromCacheAsync();
            var cachedTreeTask = _playlistCache.TryGetRootlistTreeFromCacheAsync();
            await Task.WhenAll(cachedPlaylistsTask, cachedTreeTask);

            var cachedPlaylists = await cachedPlaylistsTask;
            var cachedTree = await cachedTreeTask;
            if (cachedPlaylists is not null && cachedTree is not null)
            {
                PopulatePlaylistsSidebar(cachedPlaylists, cachedTree);
                ClearPlaylistsLoadingState();
            }

            // Phase 2 — network-backed refresh. Runs to completion even when Phase 1
            // already painted from cache; the smart diff in PopulatePlaylistsSidebar
            // reuses existing SidebarItemModels by Tag, so unchanged rows do not
            // flicker. Network failures here surface as caught exceptions but the
            // sidebar keeps whatever Phase 1 already rendered.
            var playlistsTask = _libraryDataService.GetUserPlaylistsAsync();
            var treeTask = _playlistCache.GetRootlistTreeAsync();
            await Task.WhenAll(playlistsTask, treeTask);
            PopulatePlaylistsSidebar(await playlistsTask, await treeTask);
            ClearPlaylistsLoadingState();
        }
        catch (Exception ex)
        {
            // Network refresh failed — still drop the loading state so the user
            // isn't left staring at perpetual shimmer when the cache was empty.
            ClearPlaylistsLoadingState();
            _logger?.LogError(ex, "Failed to refresh playlists from service");
            throw;
        }
    }

    private async Task RefreshLibraryBadgesAsync()
    {
        var stats = await _libraryDataService.GetStatsAsync();

        if (_sidebarItemsByTag.TryGetValue("Albums", out var albums)) albums.BadgeCount = stats.AlbumCount;
        if (_sidebarItemsByTag.TryGetValue("Artists", out var artists)) artists.BadgeCount = stats.ArtistCount;
        if (_sidebarItemsByTag.TryGetValue("LikedSongs", out var liked)) liked.BadgeCount = stats.LikedSongsCount;
        // Podcasts and YourEpisodes are two valid tags for the same row depending on
        // build flavor — prefer Podcasts when both exist (legacy keeps both around).
        if (_sidebarItemsByTag.TryGetValue("Podcasts", out var podcasts)
            || _sidebarItemsByTag.TryGetValue("YourEpisodes", out podcasts))
        {
            podcasts.BadgeCount = stats.PodcastCount;
        }
    }

    private void ClearPlaylistsLoadingState()
    {
        var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
        if (playlistsSection is not null && playlistsSection.IsLoadingChildren)
            playlistsSection.IsLoadingChildren = false;
    }

    private void PopulatePlaylistsSidebar(
        IReadOnlyList<PlaylistSummaryDto> playlists,
        RootlistTree tree)
    {
        // Smart key-based diff. Reuses existing SidebarItemModel instances by Tag,
        // inserts new ones in place, moves reordered ones, and trims removed ones.
        // Replaces the previous Clear() + walk-and-append, which made the sidebar
        // flash on every refresh even when the fresh data was identical to what
        // was already painted (the common case after the cache→network fan-out).
        var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
        if (playlistsSection?.Children is not ObservableCollection<SidebarItemModel> playlistChildren)
            return;

        var playlistLookup = playlists.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var target = BuildPlaylistTargetNodes(tree.Root, playlistLookup);
        DiffPlaylistCollection(playlistChildren, target, depth: 0);

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
        {
            var match = FindSidebarItemByTag(selectedTag);
            if (!ReferenceEquals(SelectedSidebarItem, match))
                SelectedSidebarItem = match;
        }
    }

    private async Task RefreshPinnedAsync()
    {
        try
        {
            var items = await _pinService.GetPinnedItemsAsync();
            PopulatePinnedSidebar(items);
            SyncCanonicalRowsPinnedState();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh pinned items");
        }
    }

    /// <summary>
    /// Flips the <c>IsPinned</c> flag on the canonical Your-Library Liked Songs
    /// and Podcasts rows based on whether their corresponding pseudo-URIs are in
    /// the pinned set. Drives the pin/unpin glyph on the always-visible toggle
    /// button.
    /// </summary>
    private void SyncCanonicalRowsPinnedState()
    {
        var librarySection = SidebarItems.FirstOrDefault(x => x.Tag == "YourLibrary");
        if (librarySection?.Children is not ObservableCollection<SidebarItemModel> children) return;

        foreach (var row in children)
        {
            if (row.Tag == "LikedSongs")
            {
                row.IsPinned = _pinService.IsPinned("spotify:collection");
            }
            else if (row.Tag == "Podcasts" || row.Tag == "YourEpisodes")
            {
                row.IsPinned = _pinService.IsPinned("spotify:collection:your-episodes");
            }
        }
    }

    private void PopulatePinnedSidebar(IReadOnlyList<PinnedItemDto> items)
    {
        var pinnedSection = SidebarItems.FirstOrDefault(x => x.Tag == "Pinned");
        if (pinnedSection?.Children is not ObservableCollection<SidebarItemModel> children)
            return;

        // The Pinned section keeps trailing drop-zone-only rows (e.g. the pin
        // drop placeholder) anchored at the end. Compute the data-row window
        // so the diff below operates only on real pinned items and never
        // mutates / truncates the placeholders.
        int dropZoneTail = 0;
        while (dropZoneTail < children.Count && children[children.Count - 1 - dropZoneTail].IsDropZoneOnly)
            dropZoneTail++;
        int dataCount = children.Count - dropZoneTail;

        // Flat key-based diff — same shape as DiffPlaylistCollection but without
        // folder recursion. Reuses existing rows by URI so selection survives,
        // and updates Text/Image in place when an unchanged row's title comes
        // back from a fresh metadata fetch.
        for (int i = 0; i < items.Count; i++)
        {
            var t = items[i];
            if (i < dataCount && string.Equals(children[i].Tag, t.Uri, StringComparison.Ordinal))
            {
                UpdatePinnedRow(children[i], t);
                continue;
            }

            int found = -1;
            for (int j = i + 1; j < dataCount; j++)
            {
                if (string.Equals(children[j].Tag, t.Uri, StringComparison.Ordinal))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                children.Move(found, i);
                UpdatePinnedRow(children[i], t);
            }
            else
            {
                children.Insert(i, BuildPinnedRow(t));
                dataCount++;
            }
        }

        // Trim excess data rows from the head of the drop-zone tail downward,
        // leaving the drop-zone rows untouched.
        while (dataCount > items.Count)
        {
            children.RemoveAt(items.Count);
            dataCount--;
        }

        if (_shellSession.GetSelectedSidebarTag() is { Length: > 0 } selectedTag)
        {
            var match = FindSidebarItemByTag(selectedTag);
            if (!ReferenceEquals(SelectedSidebarItem, match))
                SelectedSidebarItem = match;
        }
    }

    private static void UpdatePinnedRow(SidebarItemModel current, PinnedItemDto t)
    {
        current.Text = t.Title;

        // ImageUrl on the model is captured for parity with playlist rows; if
        // the cover URL has changed (metadata backfill landed) reseat the icon.
        if (!string.Equals(current.ImageUrl, t.ImageUrl, StringComparison.Ordinal))
        {
            current.ImageUrl = t.ImageUrl;
            current.IconSource = CreatePinnedIconSource(t);
        }
    }

    private static SidebarItemModel BuildPinnedRow(PinnedItemDto t)
    {
        return new SidebarItemModel
        {
            Text = t.Title,
            Tag = t.Uri,
            ImageUrl = t.ImageUrl,
            IconSource = CreatePinnedIconSource(t),
            Depth = 0,
            ShowUnpinButton = true
        };
    }

    /// <summary>
    /// Always-present, drag-only "Drop here to pin to sidebar" placeholder
    /// at the bottom of the Pinned section. <see cref="SidebarItem"/> keeps
    /// it collapsed until a drag whose payload matches
    /// <see cref="SidebarItemModel.DropPredicate"/> begins, at which point it
    /// fades in as a regular drop target. ShellPage routes drops on this row
    /// to <see cref="IPinService.PinAsync"/> (= ylpin write).
    /// </summary>
    private static SidebarItemModel BuildPinDropZoneRow()
    {
        return new SidebarItemModel
        {
            Text = AppLocalization.GetString("Shell_SidebarPinDropZone"),
            Tag = SidebarPinDropZoneTag,
            IsDropZoneOnly = true,
            Depth = 0,
            IconSource = new FontIconSource { Glyph = FluentGlyphs.Pin },
            DropPredicate = payload => payload is Wavee.UI.Services.DragDrop.Payloads.PlaylistDragPayload
                                    or Wavee.UI.Services.DragDrop.Payloads.AlbumDragPayload
                                    or Wavee.UI.Services.DragDrop.Payloads.ArtistDragPayload
                                    or Wavee.UI.Services.DragDrop.Payloads.ShowDragPayload,
        };
    }

    private static IconSource CreatePinnedIconSource(PinnedItemDto t)
    {
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(t.ImageUrl);
        if (!string.IsNullOrEmpty(httpsUrl))
        {
            return new ImageIconSource
            {
                ImageSource = new BitmapImage
                {
                    UriSource = new Uri(httpsUrl),
                    DecodePixelWidth = 44
                }
            };
        }

        // No cover yet — fall back to a kind-appropriate Fluent glyph so the
        // row reads correctly while the metadata backfill is in flight.
        // Liked Songs / Your Episodes are pseudo-URIs with no cover at all, so
        // the glyph IS the icon — picked to match their "Your Library" siblings.
        var glyph = t.Kind switch
        {
            PinnedItemKind.Artist => FluentGlyphs.Artist,
            PinnedItemKind.Album => FluentGlyphs.Album,
            PinnedItemKind.Show => FluentGlyphs.Radio,
            PinnedItemKind.LikedSongs => FluentGlyphs.HeartFilled,
            PinnedItemKind.YourEpisodes => FluentGlyphs.Radio,
            _ => FluentGlyphs.Playlist
        };
        return new FontIconSource { Glyph = glyph };
    }

    // ── Playlist target-node diff (rootlist) ────────────────────────────────

    /// <summary>
    /// Ephemeral plan of what each position in a sidebar collection should look
    /// like after a refresh. Carries enough state to either mutate an existing
    /// row in place or build a fresh one without re-walking the rootlist tree.
    /// </summary>
    private sealed record PlaylistTargetNode(
        string Key,
        string Name,
        bool IsFolder,
        PlaylistSummaryDto? Summary,
        IReadOnlyList<PlaylistTargetNode> Children);

    private static IReadOnlyList<PlaylistTargetNode> BuildPlaylistTargetNodes(
        RootlistNode node,
        IReadOnlyDictionary<string, PlaylistSummaryDto> playlistLookup)
    {
        var list = new List<PlaylistTargetNode>();
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case RootlistChildPlaylist playlist:
                    if (playlistLookup.TryGetValue(playlist.Uri, out var summary))
                    {
                        list.Add(new PlaylistTargetNode(
                            Key: summary.Id,
                            Name: summary.Name,
                            IsFolder: false,
                            Summary: summary,
                            Children: Array.Empty<PlaylistTargetNode>()));
                    }
                    break;

                case RootlistChildFolder folder:
                    list.Add(new PlaylistTargetNode(
                        Key: $"folder:{folder.Folder.Id}",
                        Name: folder.Folder.Name ?? string.Empty,
                        IsFolder: true,
                        Summary: null,
                        Children: BuildPlaylistTargetNodes(folder.Folder, playlistLookup)));
                    break;
            }
        }
        return list;
    }

    /// <summary>
    /// Walks the target list position-by-position against the live collection:
    /// in-place updates matching keys, Move-s already-existing keys into position,
    /// and Insert-s newcomers. Trailing items beyond the target length are removed
    /// at the end. Recurses into folder children so a moved folder retains its
    /// expanded state and its children diff against the folder's existing
    /// ObservableCollection rather than being torn down.
    /// </summary>
    private void DiffPlaylistCollection(
        ObservableCollection<SidebarItemModel> current,
        IReadOnlyList<PlaylistTargetNode> target,
        int depth)
    {
        for (int i = 0; i < target.Count; i++)
        {
            var t = target[i];
            if (i < current.Count && string.Equals(current[i].Tag, t.Key, StringComparison.Ordinal))
            {
                UpdatePlaylistMutableFields(current[i], t, depth);
            }
            else
            {
                int found = -1;
                for (int j = i + 1; j < current.Count; j++)
                {
                    if (string.Equals(current[j].Tag, t.Key, StringComparison.Ordinal))
                    {
                        found = j;
                        break;
                    }
                }

                if (found >= 0)
                {
                    current.Move(found, i);
                    UpdatePlaylistMutableFields(current[i], t, depth);
                }
                else
                {
                    current.Insert(i, BuildSidebarItemFromTarget(t, depth));
                }
            }

            if (t.IsFolder && current[i].Children is ObservableCollection<SidebarItemModel> nested)
            {
                DiffPlaylistCollection(nested, t.Children, depth + 1);
            }
        }

        while (current.Count > target.Count)
        {
            current.RemoveAt(current.Count - 1);
        }
    }

    /// <summary>
    /// Optimistically move a playlist row next to a target row in the sidebar
    /// before the server confirms, so the reorder feels instant. Searches the
    /// Playlists section and any folder children for both rows; only moves when
    /// both live in the SAME parent collection (cross-folder moves wait for the
    /// server refresh). Returns an <see cref="System.Action"/> that reverts the
    /// move (call it if the server write fails), or null when nothing moved.
    /// </summary>
    public System.Action? TryOptimisticReorder(string sourceUri, string targetUri, bool insertAfter)
    {
        var playlistsSection = SidebarItems.FirstOrDefault(x => x.Tag == "Playlists");
        if (playlistsSection?.Children is not ObservableCollection<SidebarItemModel> roots)
            return null;

        if (!TryLocate(roots, sourceUri, out var list, out var fromIndex)) return null;
        if (!TryLocate(roots, targetUri, out var targetList, out var targetIndex)) return null;
        if (!ReferenceEquals(list, targetList)) return null; // different parents → let the server settle it

        var toIndex = insertAfter ? targetIndex + 1 : targetIndex;
        // Account for the source's own removal shifting indices when it sits above.
        if (fromIndex < toIndex) toIndex--;
        toIndex = System.Math.Clamp(toIndex, 0, list!.Count - 1);
        if (toIndex == fromIndex) return null;

        list.Move(fromIndex, toIndex);
        // Revert restores the source to its original slot.
        return () =>
        {
            if (TryLocate(roots, sourceUri, out var l, out var cur)
                && ReferenceEquals(l, list) && fromIndex < list.Count)
                list.Move(cur, fromIndex);
        };

        static bool TryLocate(ObservableCollection<SidebarItemModel> roots, string uri,
            out ObservableCollection<SidebarItemModel>? owner, out int index)
        {
            for (int i = 0; i < roots.Count; i++)
            {
                if (string.Equals(roots[i].Tag, uri, StringComparison.OrdinalIgnoreCase))
                {
                    owner = roots; index = i; return true;
                }
                if (roots[i].Children is ObservableCollection<SidebarItemModel> nested)
                    for (int j = 0; j < nested.Count; j++)
                        if (string.Equals(nested[j].Tag, uri, StringComparison.OrdinalIgnoreCase))
                        {
                            owner = nested; index = j; return true;
                        }
            }
            owner = null; index = -1; return false;
        }
    }

    private void UpdatePlaylistMutableFields(SidebarItemModel current, PlaylistTargetNode t, int depth)
    {
        // SetProperty short-circuits on equality, so PropertyChanged only fires
        // when a field actually changed — keeps unchanged rows from animating.
        current.Depth = depth;

        if (t.IsFolder)
        {
            var newName = string.IsNullOrWhiteSpace(t.Name)
                ? AppLocalization.GetString("Shell_NewFolder")
                : t.Name;
            current.Text = newName;
            return;
        }

        if (t.Summary is { } summary)
        {
            current.Text = summary.Name;
            current.BadgeCount = summary.TrackCount;
            current.IsOwner = summary.IsOwner;
        }
    }

    private SidebarItemModel BuildSidebarItemFromTarget(PlaylistTargetNode t, int depth)
    {
        if (t.IsFolder)
        {
            var children = new ObservableCollection<SidebarItemModel>();
            var folderItem = new SidebarItemModel
            {
                Text = string.IsNullOrWhiteSpace(t.Name)
                    ? AppLocalization.GetString("Shell_NewFolder")
                    : t.Name,
                IconSource = new FontIconSource
                {
                    Glyph = FluentGlyphs.FolderOpen,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")
                },
                Tag = t.Key,
                IsExpanded = true,
                Depth = depth,
                IsFolder = true,
                ShowEmptyPlaceholder = true,
                EmptyPlaceholderText = AppLocalization.GetString("Shell_SidebarFolderEmpty"),
                Children = children,
                DropPredicate = FolderDropPredicate(t.Key),
            };
            folderItem.PropertyChanged += OnSidebarGroupPropertyChanged;
            ApplyPersistedSidebarState(folderItem);
            DiffPlaylistCollection(children, t.Children, depth + 1);
            return folderItem;
        }

        var item = CreatePlaylistSidebarItem(t.Summary!);
        item.Depth = depth;
        return item;
    }

    /// <summary>
    /// Drop-eligibility predicate for sidebar folder rows. Folders accept any
    /// playlist (nest into folder) or any sibling sidebar row (reorder around
    /// the folder). They never accept tracks directly — tracks land on
    /// playlists, not folders.
    /// </summary>
    private static Func<Wavee.UI.Services.DragDrop.IDragPayload, bool> FolderDropPredicate(string folderTag) =>
        payload => payload switch
        {
            Wavee.UI.Services.DragDrop.Payloads.PlaylistDragPayload => true,
            Wavee.UI.Services.DragDrop.Payloads.SidebarReorderPayload sp
                => !string.Equals(sp.SourceUri, folderTag, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private SidebarItemModel CreatePlaylistSidebarItem(PlaylistSummaryDto playlist)
    {
        // Capture for the DropPredicate closure — keeps the predicate stable
        // when the row is re-bound to a fresh summary later.
        var canEdit = playlist.CanEditItems;
        var item = new SidebarItemModel
        {
            Text = playlist.Name,
            IconSource = CreatePlaylistIconSource(playlist),
            Tag = playlist.Id,
            // Captured so OnPlaylistContentsChanged can gate mosaic rebuilds —
            // only mosaic-backed (null or spotify:mosaic:...) playlists need
            // re-composition on a content change.
            ImageUrl = playlist.ImageUrl,
            IsOwner = playlist.IsOwner,
            CanEditItems = canEdit,
            BadgeCount = playlist.TrackCount,
            // Playlist rows accept:
            //  - Tracks (add)             — only on rows the user can edit
            //  - Album/Artist/Liked/Show  — same edit gate (resolves to add-tracks)
            //  - PlaylistDragPayload      — same edit gate (copy source tracks)
            //  - SidebarReorderPayload    — always allowed when source != target
            //                               (handler branches on drop position:
            //                                Top/Bottom = reorder, Center = copy
            //                                — copy still respects edit gate)
            DropPredicate = payload => payload switch
            {
                Wavee.UI.Services.DragDrop.Payloads.TrackDragPayload          => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.AlbumDragPayload          => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.ArtistDragPayload         => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.LikedSongsDragPayload     => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.ShowDragPayload           => canEdit,
                Wavee.UI.Services.DragDrop.Payloads.PlaylistDragPayload pp    => canEdit
                    && !string.Equals(pp.PlaylistUri, playlist.Id, StringComparison.OrdinalIgnoreCase),
                Wavee.UI.Services.DragDrop.Payloads.SidebarReorderPayload sp
                    => !string.Equals(sp.SourceUri, playlist.Id, StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
        };

        // Spotify "custom" playlists (auto-named, e.g. localized "My Playlist #15") arrive
        // either with ImageUrl == null or ImageUrl == "spotify:mosaic:id1:id2:id3:id4". Neither
        // is loadable as a single image -- CreatePlaylistIconSource above seats the placeholder
        // glyph, and we attach a lazy loader so PlaylistMosaicService can compose a 2x2 bitmap
        // and replace IconSource the first time the row is realized.
        if (_mosaicService is { } service
            && (string.IsNullOrEmpty(playlist.ImageUrl) || SpotifyImageHelper.IsMosaicUri(playlist.ImageUrl)))
        {
            var playlistId = playlist.Id;
            var hint = playlist.ImageUrl;
            item.LazyIconSourceLoader = ct => service.BuildMosaicAsync(playlistId, hint, ct);
        }

        return item;
    }

    /// <summary>
    /// Reacts to a per-playlist <see cref="PlaylistChangeKind.Updated"/> event.
    /// Two-phase:
    ///   1. **Promote.** Re-query the cache. If the cached <c>ImageUrl</c> now
    ///      resolves to a real HTTPS URL (either editorial PictureSize or
    ///      user-uploaded <c>spotify:image:{hex}</c>) and the sidebar row was
    ///      seated without one, swap in a real <see cref="ImageIconSource"/>.
    ///      Covers the common case of non-owned playlists whose rootlist
    ///      decoration omits the picture — the persisted row only fills in
    ///      after the first full detail fetch, and the sidebar wouldn't
    ///      otherwise pick that up until the next sidebar refresh.
    ///   2. **Mosaic refresh.** If the cache still has no real cover (null /
    ///      <c>spotify:mosaic:</c>), invalidate + rebuild the composed mosaic.
    ///      Real-cover rows skip this step entirely.
    /// Idempotent across rapid pushes — the mosaic service's in-flight cache
    /// dedups concurrent rebuilds.
    /// </summary>
    private void OnPlaylistContentsChanged(string playlistUri)
    {
        if (string.IsNullOrEmpty(playlistUri)) return;

        var item = FindSidebarItemByTag(playlistUri);
        if (item is null) return;

        var capturedUri = playlistUri;
        _ = Task.Run(async () =>
        {
            try
            {
                // Phase 1: real-cover promotion. Re-query the cache (cheap on a
                // hot hit) so we see whatever the latest detail fetch wrote into
                // the persisted row.
                var cached = await _playlistCache
                    .GetPlaylistAsync(capturedUri, ct: CancellationToken.None)
                    .ConfigureAwait(false);
                var httpsUrl = SpotifyImageHelper.ToHttpsUrl(cached.ImageUrl);

                if (!string.IsNullOrEmpty(httpsUrl))
                {
                    _dispatcher?.TryEnqueue(() =>
                    {
                        var current = FindSidebarItemByTag(capturedUri);
                        if (current is null) return;
                        // Skip if the URL hasn't changed AND the icon is already
                        // a loaded BitmapImage — avoids replacing a working icon
                        // and triggering needless re-decode flicker.
                        if (string.Equals(current.ImageUrl, cached.ImageUrl, StringComparison.Ordinal)
                            && current.IconSource is ImageIconSource { ImageSource: BitmapImage })
                            return;

                        current.ImageUrl = cached.ImageUrl;
                        current.IconSource = new ImageIconSource
                        {
                            ImageSource = new BitmapImage
                            {
                                UriSource = new Uri(httpsUrl),
                                DecodePixelWidth = 44
                            }
                        };
                        // No further mosaic work needed — a real cover trumps
                        // any composed placeholder. Clear the lazy loader so a
                        // subsequent realization doesn't overwrite our promotion.
                        current.LazyIconSourceLoader = null;
                    });
                    return;
                }

                // Phase 2: mosaic refresh. Cache still has no usable URL — fall
                // back to rebuilding the 2x2 composed tile from track covers.
                if (_mosaicService is null) return;
                _mosaicService.Invalidate(capturedUri);
                var icon = await _mosaicService
                    .BuildMosaicAsync(capturedUri, mosaicHint: null, CancellationToken.None)
                    .ConfigureAwait(false);
                if (icon is null) return;
                _dispatcher?.TryEnqueue(() =>
                {
                    var current = FindSidebarItemByTag(capturedUri);
                    if (current is not null)
                        current.IconSource = icon;
                });
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Sidebar cover refresh failed for {Uri}", capturedUri);
            }
        });
    }

    private static IconSource CreatePlaylistIconSource(PlaylistSummaryDto playlist)
    {
        // Route through SpotifyImageHelper so user-uploaded covers
        // (spotify:image:{hex} — what the v3 cache schema produces from
        // attributes.Picture) render alongside the editorial pre-rendered
        // HTTPS PictureSize URLs. spotify:mosaic: still returns null and
        // falls through to the lazy mosaic loader below.
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(playlist.ImageUrl);
        if (!string.IsNullOrEmpty(httpsUrl))
        {
            return new ImageIconSource
            {
                ImageSource = new BitmapImage
                {
                    UriSource = new Uri(httpsUrl),
                    DecodePixelWidth = 44
                }
            };
        }

        // ImageIconSource with null ImageSource (not FontIconSource) so
        // SidebarItem.CreateSidebarIcon routes through CreateArtworkIcon
        // and renders the same 32x32 rounded tile shape it uses for real
        // artwork. A bare FontIconSource renders at 16px, so the icon
        // rectangle would jump 16->32 when the lazy mosaic loader
        // resolves -- fade animation can't mask a size change.
        return new ImageIconSource { ImageSource = null };
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_dragStateService is not null)
            _dragStateService.DragStateChanged -= OnGlobalDragStateChanged;

        _playlistMosaicChangesSubscription?.Dispose();
        _playlistMosaicChangesSubscription = null;

        // Cancel any in-flight debounced playlist refresh so we don't touch disposed state.
        var pending = Interlocked.Exchange(ref _playlistRefreshCts, null);
        pending?.Cancel();
        pending?.Dispose();

        foreach (var group in SidebarItems)
            group.PropertyChanged -= OnSidebarGroupPropertyChanged;

        // Cleanup sidebar button handlers
        _playlistsAddButton = null;

        if (_newPlaylistMenuItem != null)
        {
            _newPlaylistMenuItem.Click -= NewPlaylistMenuItem_Click;
            _newPlaylistMenuItem = null;
        }

        if (_newFolderMenuItem != null)
        {
            _newFolderMenuItem.Click -= NewFolderMenuItem_Click;
            _newFolderMenuItem = null;
        }
    }
}
