using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels.Home;

/// <summary>
/// Owns the bound home-feed surface: the <see cref="Sections"/> collection,
/// chip row, customization preferences, and the synthetic "Local files"
/// shelf wired in via <see cref="Wavee.Local.ILocalLibraryService"/>.
///
/// <para>The feed VM is intentionally not responsible for fetching the raw
/// home-feed response — that work lives in <see cref="HomeFeedCache"/> /
/// <see cref="IHomeFeedService"/>. This VM consumes snapshots and chips,
/// orchestrates the chunked population pattern that keeps the UI responsive
/// across the initial 4,650-element layout pass, and emits
/// <see cref="SectionsApplied"/> so the parent can fan out to baseline
/// enrichment / recents dispatch without children referencing each other
/// directly.</para>
/// </summary>
public sealed partial class HomeFeedViewModel : ObservableObject, IDisposable
{
    private readonly IHomeFeedService? _homeFeedService;
    private readonly ISettingsService? _settingsService;
    private readonly HomeFeedCache? _homeFeedCache;
    private readonly HomeResponseParserFactory _parserFactory;
    private readonly Wavee.Local.ILocalLibraryService? _localLibrary;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<bool> _isDarkThemeProvider;
    private readonly Action<string?> _greetingSetter;

    private IDisposable? _localProgressSub;
    private bool _isDisposed;

    private const string LocalSectionUri = "wavee:local:home";
    private const int LocalSectionMaxItems = 20;

    /// <summary>
    /// Sentinel chip id for the synthetic, client-side "Local files" chip.
    /// Lives outside the Spotify chip-id namespace (id strings from the
    /// real API are short slugs like "music-chip" / "podcasts-chip") so
    /// SelectChipAsync can short-circuit to local-only display mode
    /// without touching Pathfinder.
    /// </summary>
    private const string LocalChipId = "wavee:chip:local";

    [ObservableProperty]
    private ObservableCollection<HomeSection> _sections = [];

    [ObservableProperty]
    private ObservableCollection<HomeSectionPref> _sectionPreferences = [];

    [ObservableProperty]
    private int _newSectionCount;

    /// <summary>The chips currently displayed in the single row.</summary>
    [ObservableProperty]
    private ObservableCollection<HomeChipViewModel> _displayedChips = [];

    /// <summary>
    /// True while the synthetic "Local files" chip is selected. Drives the
    /// region adapter to drop every non-local band and the hero band to
    /// collapse — so the page reads as a focused local-only view without
    /// rebuilding the underlying section collection.
    /// </summary>
    [ObservableProperty]
    private bool _isLocalChipActive;

    /// <summary>The original main chips (preserved for reverting from sub-chips).</summary>
    private List<HomeChipViewModel>? _mainChips;

    /// <summary>Currently active parent chip when showing sub-chips (null = showing main chips).</summary>
    private HomeChipViewModel? _activeParentChip;

    /// <summary>
    /// Active chip facet driving the home feed (e.g. <c>"music-chip"</c>,
    /// <c>"podcasts-chip"</c>, <c>"audiobooks-chip"</c>, or null/empty for
    /// the default unfaceted view). Exposed so adapters like
    /// <c>HomeHeroAdapter</c> can decide whether to filter podcast
    /// sections out of the hero on music-tab views.
    /// </summary>
    public string? CurrentFacet => _homeFeedCache?.CurrentFacet;

    /// <summary>
    /// Raised after a fresh batch of sections has landed in <see cref="Sections"/>.
    /// The parent listens and fans out to baseline enrichment + recents dispatch.
    /// Payload is the freshly-ordered list (pre-local-restore) so consumers can
    /// inspect server-sourced sections without paying for the bound collection's
    /// post-restore state.
    /// </summary>
    public event EventHandler<List<HomeSection>>? SectionsApplied;

    /// <summary>
    /// Raised when a facet refetch begins or completes. Parent uses this to
    /// flip IsLoading / HasError so the page's loading scrim tracks chip
    /// presses (without the chip-press synchronously poking IsLoading from
    /// inside the child).
    /// </summary>
    public event EventHandler<FacetRefetchEventArgs>? FacetRefetchStateChanged;

    /// <summary>
    /// Raised when an attempt to refetch via facet fails. Parent surfaces the
    /// error through its top-level error banner.
    /// </summary>
    public event EventHandler<Exception>? FacetRefetchFailed;

    public HomeFeedViewModel(
        IHomeFeedService? homeFeedService,
        ISettingsService? settingsService,
        HomeFeedCache? homeFeedCache,
        HomeResponseParserFactory parserFactory,
        Wavee.Local.ILocalLibraryService? localLibrary,
        ILogger? logger,
        Func<bool> isDarkThemeProvider,
        Action<string?> greetingSetter)
    {
        _homeFeedService = homeFeedService;
        _settingsService = settingsService;
        _homeFeedCache = homeFeedCache;
        _parserFactory = parserFactory;
        _localLibrary = localLibrary;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _isDarkThemeProvider = isDarkThemeProvider;
        _greetingSetter = greetingSetter;

        // Subscribe to scan-progress so the Local Files shelf refreshes as
        // the indexer materialises content during a scan, and once when the
        // scan completes (CurrentPath == null tick).
        if (_localLibrary is not null)
            _localProgressSub = _localLibrary.SyncProgress.Subscribe(OnLocalSyncProgress);
    }

    // ── Snapshot apply paths (called from parent's LoadAsync) ───────────────

    /// <summary>
    /// Apply a cache-hit snapshot. Mirrors the cache-warm branch of the
    /// previous monolithic LoadAsync.
    /// </summary>
    public async Task ApplyCacheSnapshotAsync(HomeFeedSnapshot snapshot)
    {
        _greetingSetter(snapshot.Greeting);
        var ordered = ApplyPreferences(snapshot.Sections);
        var localSection = ExtractLocalSection();
        if (Sections.Count == 0)
            await PopulateSectionsChunkedAsync(ordered);
        else
            HomeFeedCache.ApplyDiff(Sections, ordered, _greetingSetter, snapshot.Greeting,
                s => s.ApplyTheme(_isDarkThemeProvider()));
        RestoreLocalSection(localSection);
        ApplyChips(snapshot.Chips);
        SectionsApplied?.Invoke(this, ordered);
        await RefreshLocalSectionAsync();
    }

    /// <summary>
    /// Apply a freshly fetched snapshot from <see cref="HomeFeedCache"/>.
    /// Identical structure to <see cref="ApplyCacheSnapshotAsync"/> — keeps
    /// the snapshot apply pattern obvious at the parent's call site.
    /// </summary>
    public async Task ApplyFreshSnapshotAsync(HomeFeedSnapshot snapshot)
    {
        _greetingSetter(snapshot.Greeting);
        var ordered = ApplyPreferences(snapshot.Sections);
        var localSection = ExtractLocalSection();
        if (Sections.Count == 0)
            await PopulateSectionsChunkedAsync(ordered);
        else
            HomeFeedCache.ApplyDiff(Sections, ordered, _greetingSetter, snapshot.Greeting,
                s => s.ApplyTheme(_isDarkThemeProvider()));
        RestoreLocalSection(localSection);
        ApplyChips(snapshot.Chips);
        SectionsApplied?.Invoke(this, ordered);
        await RefreshLocalSectionAsync();
    }

    /// <summary>
    /// Direct-fetch fallback used when <see cref="HomeFeedCache"/> is not
    /// available. Parses the raw <see cref="HomeResponse"/> on a worker thread
    /// (matching the old behaviour) before populating the bound collection.
    /// </summary>
    public async Task ApplyDirectFetchAsync(HomeResponse response)
    {
        var result = await Task.Run(() => _parserFactory.Parse(response));
        _greetingSetter(result.Greeting);
        var ordered = ApplyPreferences(result.Sections);
        var localSection = ExtractLocalSection();
        await PopulateSectionsChunkedAsync(ordered);
        RestoreLocalSection(localSection);
        ApplyChips(result.Chips);
        SectionsApplied?.Invoke(this, ordered);
        await RefreshLocalSectionAsync();
    }

    /// <summary>
    /// Apply a background-refresh snapshot. Mirrors the parent's old
    /// <c>ApplyBackgroundRefresh</c> orchestration.
    /// </summary>
    public void ApplyBackgroundRefresh(HomeFeedSnapshot snapshot)
    {
        var ordered = ApplyPreferences(snapshot.Sections);
        var localSection = ExtractLocalSection();
        HomeFeedCache.ApplyDiff(Sections, ordered, _greetingSetter, snapshot.Greeting,
            s => s.ApplyTheme(_isDarkThemeProvider()));
        RestoreLocalSection(localSection);
        ApplyChips(snapshot.Chips);
        SectionsApplied?.Invoke(this, ordered);
        _ = RefreshLocalSectionAsync();
    }

    // ── Local files Home section ────────────────────────────────────────────
    //
    // Materialise a "Local files" shelf at the bottom of Home whenever the
    // indexer has at least one local album. The section is generated from
    // ILocalLibraryService.GetAllTracksAsync grouped by album_uri; cards
    // route to LocalLibraryPage on click and the section header carries a
    // ViewAllUri that the page-level click handler maps to the same target.
    // Refreshes on every scan-progress event so adding/removing folders
    // updates the shelf without needing a Home reload.

    public async Task RefreshLocalSectionAsync()
    {
        if (_isDisposed || _localLibrary is null) return;
        if (_settingsService?.Settings.ShowLocalFilesOnHome == false)
        {
            RemoveLocalSectionOnDispatcher();
            return;
        }

        IReadOnlyList<Wavee.Local.LocalTrackRow> rows;
        try
        {
            rows = await _localLibrary.GetAllTracksAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Local section refresh: GetAllTracksAsync failed");
            return;
        }

        if (rows.Count == 0)
        {
            // GetAllTracksAsync can transiently observe zero rows mid-scan
            // or during a DB write. Don't flicker the existing shelf out;
            // ShowLocalFilesOnHome=false is the authoritative remove signal
            // and is already handled above.
            return;
        }

        // Split rows into "has real album metadata" (group as albums) and
        // "no metadata, scanner used Unknown fallback" (show per-file as
        // individual track cards). The scanner stores the literal strings
        // "Unknown Album" / "Unknown Artist" when tags are blank, and
        // LocalNormalize.AlbumUri hashes those fallback strings into a
        // single deterministic URI — so without this split, every untagged
        // file (videos, .mp3s with no ID3, etc.) collapses into one fake
        // "Unknown Album" card. Treating them as per-file track cards lets
        // the shelf surface them with their filename-derived Title instead.
        bool IsSyntheticAlbum(Wavee.Local.LocalTrackRow r) =>
            r.Album == "Unknown Album"
            && (r.AlbumArtist is null or "Unknown Artist")
            && (r.Artist is null or "Unknown Artist");

        var realAlbums = rows
            .Where(r => r.AlbumUri != null && !IsSyntheticAlbum(r))
            .GroupBy(r => r.AlbumUri!)
            .Select(g =>
            {
                var first = g.First();
                return new HomeSectionItem
                {
                    Uri = g.Key,
                    Title = first.Album ?? first.Title ?? "Untitled",
                    Subtitle = first.AlbumArtist ?? first.Artist,
                    ImageUrl = g.FirstOrDefault(t => t.ArtworkUri != null)?.ArtworkUri,
                    ContentType = HomeContentType.Album,
                };
            });

        var orphanFiles = rows
            .Where(r => r.AlbumUri == null || IsSyntheticAlbum(r))
            .Select(r => new HomeSectionItem
            {
                Uri = r.TrackUri,
                Title = r.Title ?? Path.GetFileNameWithoutExtension(r.FilePath) ?? "Untitled",
                // Don't show "Unknown Artist" — leave the subtitle blank so
                // an untagged file reads as a single line (filename) rather
                // than filename+placeholder.
                Subtitle = (r.AlbumArtist == "Unknown Artist" ? null : r.AlbumArtist)
                           ?? (r.Artist == "Unknown Artist" ? null : r.Artist),
                ImageUrl = r.ArtworkUri,
                ContentType = HomeContentType.Album,
            });

        var albums = realAlbums
            .Concat(orphanFiles)
            .Take(LocalSectionMaxItems)
            .ToList();

        void ApplyLocalSection()
        {
            if (_isDisposed) return;
            UpsertLocalSection(albums);
        }

        if (_dispatcherQueue.HasThreadAccess)
            ApplyLocalSection();
        else
            _dispatcherQueue.TryEnqueue(ApplyLocalSection);
    }

    private void UpsertLocalSection(List<HomeSectionItem> items)
    {
        var existing = Sections.FirstOrDefault(s => s.SectionUri == LocalSectionUri);
        if (existing != null)
        {
            existing.Items.Clear();
            foreach (var item in items) existing.Items.Add(item);
            MoveLocalSectionToPreferredPosition(existing);
            EnsureLocalChipPresent();
            return;
        }

        var section = new HomeSection
        {
            Title = "Local files",
            // No Subtitle — the LocalFiles region band already carries the
            // "ON THIS PC" eyebrow, so repeating it inside the inner section
            // header reads as visual duplication.
            SectionType = HomeSectionType.Generic,
            SectionUri = LocalSectionUri,
            ViewAllUri = "wavee:local:library",
        };
        foreach (var item in items) section.Items.Add(item);
        section.ApplyTheme(_isDarkThemeProvider());
        Sections.Insert(GetLocalSectionInsertIndex(), section);
        EnsureLocalChipPresent();
    }

    private int GetLocalSectionInsertIndex()
    {
        if (Sections.Count == 0) return 0;

        var index = 1;
        if (Sections.Count > 1
            && Sections[0].SectionType == HomeSectionType.Shorts
            && Sections[1].SectionType == HomeSectionType.RecentlyPlayed)
        {
            index = 2;
        }

        return Math.Min(index, Sections.Count);
    }

    private void MoveLocalSectionToPreferredPosition(HomeSection section)
    {
        var currentIndex = Sections.IndexOf(section);
        if (currentIndex < 0) return;

        Sections.RemoveAt(currentIndex);
        Sections.Insert(GetLocalSectionInsertIndex(), section);
    }

    private HomeSection? ExtractLocalSection()
    {
        for (int i = Sections.Count - 1; i >= 0; i--)
        {
            if (Sections[i].SectionUri == LocalSectionUri)
            {
                var section = Sections[i];
                Sections.RemoveAt(i);
                return section;
            }
        }

        return null;
    }

    private void RestoreLocalSection(HomeSection? section)
    {
        if (section is null) return;
        if (_settingsService?.Settings.ShowLocalFilesOnHome == false) return;
        if (Sections.Any(s => s.SectionUri == LocalSectionUri)) return;

        section.ApplyTheme(_isDarkThemeProvider());
        Sections.Insert(GetLocalSectionInsertIndex(), section);
    }

    public void RemoveLocalSection()
    {
        _ = ExtractLocalSection();
        RemoveLocalChip();
    }

    /// <summary>
    /// Insert the synthetic "Local files" chip after the "All" chip in
    /// <see cref="DisplayedChips"/>. No-op when the chip is already present,
    /// when no chips have arrived yet (the chip can only ride alongside
    /// real Spotify chips — it never seeds an empty row), or when the user
    /// is currently in a sub-chip morphed view (don't fight the morph).
    /// </summary>
    private void EnsureLocalChipPresent()
    {
        if (_isDisposed) return;
        if (DisplayedChips.Count == 0) return;
        if (_activeParentChip is not null) return;
        if (DisplayedChips.Any(c => c.Id == LocalChipId)) return;

        var chip = new HomeChipViewModel
        {
            Id = LocalChipId,
            Label = "Local files",
            IsSelected = IsLocalChipActive,
        };

        // Insert after "All" (the empty-id chip at the front) so the new
        // chip reads as a peer of "Music" / "Podcasts" rather than a
        // trailing afterthought. If "All" isn't at index 0 (unexpected),
        // just append.
        var insertAt = DisplayedChips.Count > 0 && string.IsNullOrEmpty(DisplayedChips[0].Id) ? 1 : DisplayedChips.Count;
        DisplayedChips.Insert(insertAt, chip);

        // Mirror into _mainChips so morph→back-chip→revert keeps the local
        // chip visible. _mainChips is the canonical row that
        // <see cref="SelectChipAsync"/> restores from on back-chip click.
        if (_mainChips is not null && !_mainChips.Any(c => c.Id == LocalChipId))
        {
            var mainInsertAt = _mainChips.Count > 0 && string.IsNullOrEmpty(_mainChips[0].Id) ? 1 : _mainChips.Count;
            _mainChips.Insert(mainInsertAt, chip);
        }
    }

    private void RemoveLocalChip()
    {
        if (_isDisposed) return;

        for (int i = DisplayedChips.Count - 1; i >= 0; i--)
        {
            if (DisplayedChips[i].Id == LocalChipId)
                DisplayedChips.RemoveAt(i);
        }

        if (_mainChips is not null)
        {
            for (int i = _mainChips.Count - 1; i >= 0; i--)
            {
                if (_mainChips[i].Id == LocalChipId)
                    _mainChips.RemoveAt(i);
            }
        }

        // If the local chip was the active filter, drop the filter too so
        // the page doesn't sit on an empty "local-only" view after the
        // last local folder is removed mid-session.
        if (IsLocalChipActive)
            IsLocalChipActive = false;
    }

    public void RemoveLocalSectionOnDispatcher()
    {
        if (_dispatcherQueue.HasThreadAccess)
            RemoveLocalSection();
        else
            _dispatcherQueue.TryEnqueue(RemoveLocalSection);
    }

    private void OnLocalSyncProgress(Wavee.Local.LocalSyncProgress p)
    {
        // Refresh on the final tick (CurrentPath == null) AND periodically while
        // a scan is in flight so the user sees the shelf grow as files come in.
        // Throttle: only refresh on every Nth tick to keep SQLite reads low.
        if (p.CurrentPath is null || p.ProcessedFiles % 50 == 0)
        {
            _ = RefreshLocalSectionAsync();
        }
    }

    /// <summary>
    /// Populates the bound <see cref="Sections"/> collection in small chunks, yielding
    /// to the dispatcher between chunks so the layout engine can paint each chunk's cards
    /// and handle input (scroll, click) before the next batch lands.
    ///
    /// <para>
    /// Without this, assigning a 31-section collection in one shot triggers a single
    /// massive layout pass that realizes all ~4,650 card elements before the UI thread
    /// yields — the root cause of the "heavy" navigation feel. Yielding every
    /// <paramref name="chunkSize"/> sections spreads that work across several frames
    /// so the user sees content stream in progressively.
    /// </para>
    ///
    /// <para>
    /// Uses <see cref="Task.Yield"/> rather than <c>DispatcherQueue.TryEnqueue</c> because
    /// an async method resumed on the UI thread via <c>Task.Yield</c> posts a continuation
    /// at normal dispatcher priority — equivalent to a message-pump tick — which is exactly
    /// what we need to let WinUI paint between chunks.
    /// </para>
    /// </summary>
    private async Task PopulateSectionsChunkedAsync(IList<HomeSection> ordered, int chunkSize = 4)
    {
        // Start from empty: we only call this when Sections.Count == 0, but guard anyway.
        if (Sections.Count > 0) Sections.Clear();

        var isDark = _isDarkThemeProvider();

        for (int i = 0; i < ordered.Count; i++)
        {
            // Build the per-section accent brushes for the current theme just
            // before adding to the bound collection, so x:Bind picks them up
            // on the first realization (no second pass needed).
            ordered[i].ApplyTheme(isDark);
            Sections.Add(ordered[i]);

            // Yield back to the dispatcher every chunkSize items so realization cost
            // is spread across multiple frames instead of a single 4,650-element burst.
            if ((i + 1) % chunkSize == 0 && i + 1 < ordered.Count)
                await Task.Yield();
        }
    }

    private void ApplyChips(List<HomeChipViewModel>? chips)
    {
        // Only update chips when we receive them (unfaceted responses)
        if (chips == null || chips.Count == 0 || DisplayedChips.Count > 0) return;

        // Spotify's chip API doesn't include an "All" entry — prepend one
        // synthetically so the bar always leads with the unfaceted view.
        // SelectChipAsync already handles `string.IsNullOrEmpty(chip.Id)` as
        // the unfaceted refetch path (lines ~1462), and the back-chip handler
        // restores selection to the empty-Id chip, so a single empty-Id entry
        // at the head is all the wiring needs.
        if (!chips.Any(c => string.IsNullOrEmpty(c.Id)))
        {
            chips.Insert(0, new HomeChipViewModel
            {
                Id = string.Empty,
                Label = "All"
            });
        }

        _mainChips = chips;
        _activeParentChip = null;

        // "All" chip (empty Id) starts selected — every other chip clears.
        foreach (var c in chips) c.IsSelected = string.IsNullOrEmpty(c.Id);
        DisplayedChips = new ObservableCollection<HomeChipViewModel>(chips);

        // If the local section already materialised before the Spotify chips
        // arrived, surface the local chip alongside them. Without this the
        // first scan's chip would be silently swallowed by the row swap.
        if (Sections.Any(s => s.SectionUri == LocalSectionUri))
            EnsureLocalChipPresent();
    }

    private List<HomeSection> ApplyPreferences(List<HomeSection> apiSections)
    {
        // Customization removed — pass through directly
        return apiSections;

        var settings = _settingsService;
        if (settings == null) return apiSections;

        var homeSettings = settings.Settings.HomeSettings;

        if (!homeSettings.Initialized)
        {
            // First load: seed all sections as visible
            homeSettings.Sections = apiSections.Select(s => new HomeSectionPref
            {
                SectionUri = s.SectionUri,
                Title = s.Title,
                IsVisible = true
            }).ToList();
            homeSettings.Initialized = true;
            settings.Update(a => a.HomeSettings = homeSettings);

            SectionPreferences = new ObservableCollection<HomeSectionPref>(homeSettings.Sections);
            NewSectionCount = 0;
            return apiSections;
        }

        // Check for new sections not in preferences
        var knownUris = new HashSet<string>(homeSettings.Sections.Select(s => s.SectionUri));
        var newSections = apiSections.Where(s => !knownUris.Contains(s.SectionUri)).ToList();
        NewSectionCount = newSections.Count;

        // Add new sections to preferences as hidden
        foreach (var ns in newSections)
        {
            homeSettings.Sections.Add(new HomeSectionPref
            {
                SectionUri = ns.SectionUri,
                Title = ns.Title,
                IsVisible = false
            });
        }

        if (newSections.Count > 0)
            settings.Update(a => a.HomeSettings = homeSettings);

        SectionPreferences = new ObservableCollection<HomeSectionPref>(homeSettings.Sections);

        // Build lookup from API sections
        var apiLookup = apiSections.ToDictionary(s => s.SectionUri);

        // Order by preferences, pinned first (after shorts)
        var result = new List<HomeSection>();

        // Shorts always come first, regardless of preferences
        var shorts = apiSections.Where(s => s.SectionType == HomeSectionType.Shorts).ToList();
        result.AddRange(shorts);

        // Then pinned sections
        foreach (var pref in homeSettings.Sections.Where(p => p.IsPinned && p.IsVisible))
        {
            if (apiLookup.TryGetValue(pref.SectionUri, out var section) && section.SectionType != HomeSectionType.Shorts)
                result.Add(section);
        }

        // Then remaining visible sections in preference order
        foreach (var pref in homeSettings.Sections.Where(p => !p.IsPinned && p.IsVisible))
        {
            if (apiLookup.TryGetValue(pref.SectionUri, out var section) && section.SectionType != HomeSectionType.Shorts)
                result.Add(section);
        }

        return result;
    }

    // ── Customization commands (called from parent's command shims) ─────────

    /// <summary>
    /// Sets a section's visibility to a specific value (not a toggle).
    /// Called from the checkbox Checked/Unchecked events to avoid double-toggle bugs.
    /// </summary>
    public void SetSectionVisibility(string sectionUri, bool visible)
    {
        var settings = _settingsService;
        if (settings == null) return;

        var pref = settings.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref == null || pref.IsVisible == visible) return;

        pref.IsVisible = visible;
        settings.Update(a => { });

        if (!visible)
        {
            var matching = Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
            if (matching != null)
                Sections.Remove(matching);
        }
        else
        {
            // Re-add from cache at the correct position
            var cache = _homeFeedCache;
            var cachedSections = cache?.GetCached()?.Sections;
            if (cachedSections != null)
            {
                var section = cachedSections.FirstOrDefault(s => s.SectionUri == sectionUri);
                if (section != null)
                {
                    var prefOrder = settings.Settings.HomeSettings.Sections
                        .Where(p => p.IsVisible)
                        .Select(p => p.SectionUri)
                        .ToList();
                    var targetIdx = prefOrder.IndexOf(sectionUri);
                    var insertAt = Math.Min(Math.Max(0, targetIdx), Sections.Count);
                    Sections.Insert(insertAt, section);
                }
            }
        }
    }

    public void ToggleSectionVisibility(string sectionUri)
    {
        var settings = _settingsService;
        var pref = settings?.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref != null)
            SetSectionVisibility(sectionUri, !pref.IsVisible);
    }

    public void ToggleSectionPin(string sectionUri)
    {
        var settings = _settingsService;
        if (settings == null) return;

        var pref = settings.Settings.HomeSettings.Sections.FirstOrDefault(s => s.SectionUri == sectionUri);
        if (pref != null)
        {
            pref.IsPinned = !pref.IsPinned;
            settings.Update(a => { });
            SectionPreferences = new ObservableCollection<HomeSectionPref>(settings.Settings.HomeSettings.Sections);
        }
    }

    public void MoveSectionUp(string sectionUri)
    {
        var settings = _settingsService;
        if (settings == null) return;

        var list = settings.Settings.HomeSettings.Sections;
        var idx = list.FindIndex(s => s.SectionUri == sectionUri);
        if (idx > 0)
        {
            (list[idx], list[idx - 1]) = (list[idx - 1], list[idx]);
            settings.Update(a => { });
            SectionPreferences = new ObservableCollection<HomeSectionPref>(list);
        }
    }

    public void MoveSectionDown(string sectionUri)
    {
        var settings = _settingsService;
        if (settings == null) return;

        var list = settings.Settings.HomeSettings.Sections;
        var idx = list.FindIndex(s => s.SectionUri == sectionUri);
        if (idx >= 0 && idx < list.Count - 1)
        {
            (list[idx], list[idx + 1]) = (list[idx + 1], list[idx]);
            settings.Update(a => { });
            SectionPreferences = new ObservableCollection<HomeSectionPref>(list);
        }
    }

    public void ResetSectionPreferences()
    {
        var settings = _settingsService;
        if (settings == null) return;

        settings.Settings.HomeSettings = new HomeSectionSettings();
        settings.Update(a => { });
    }

    // ── Chip selection ──────────────────────────────────────────────────────

    /// <summary>
    /// Apply a chip click. Routes to the local-only filter, back-chip revert,
    /// "All" reset, parent → sub-chip morph, or a regular facet refetch via
    /// <see cref="RefetchWithFacetAsync"/>. The parent surfaces this through
    /// its <c>SelectChipCommand</c> shim.
    /// </summary>
    public async Task SelectChipAsync(HomeChipViewModel? chip)
    {
        if (chip == null) return;

        System.Diagnostics.Debug.WriteLine($"[SelectChipAsync] chip={chip.Label}, id={chip.Id}, isBack={chip.IsBackChip}");

        // Synthetic "Local files" chip — purely client-side. Flip the
        // local-only filter, mark the chip as the lone selection, and
        // skip the Pathfinder refetch (there's no Spotify facet to send).
        if (chip.Id == LocalChipId)
        {
            _activeParentChip = null;
            foreach (var c in DisplayedChips) c.IsSelected = c == chip;
            IsLocalChipActive = true;
            return;
        }

        // Back chip → revert to main chips, refetch with no facet
        if (chip.IsBackChip)
        {
            _activeParentChip = null;
            IsLocalChipActive = false;
            if (_mainChips != null)
            {
                // Select "All" chip
                foreach (var c in _mainChips) c.IsSelected = string.IsNullOrEmpty(c.Id);
                DisplayedChips = new ObservableCollection<HomeChipViewModel>(_mainChips);
            }
            await RefetchWithFacetAsync(null);
            return;
        }

        // "All" chip → refetch with no facet, stay on main chips
        if (string.IsNullOrEmpty(chip.Id))
        {
            _activeParentChip = null;
            IsLocalChipActive = false;
            foreach (var c in DisplayedChips) c.IsSelected = c == chip;
            await RefetchWithFacetAsync(null);
            return;
        }

        // Parent chip with sub-chips → morph into sub-chips row
        if (chip.SubChips is { Count: > 0 })
        {
            _activeParentChip = chip;
            IsLocalChipActive = false;

            // Build morphed row: [✕ Parent] [Sub1] [Sub2] ...
            var backChip = new HomeChipViewModel
            {
                Id = chip.Id,
                Label = chip.Label,
                IsBackChip = true,
                IsSelected = true
            };

            var morphed = new ObservableCollection<HomeChipViewModel> { backChip };
            foreach (var sc in chip.SubChips)
            {
                sc.IsSelected = false;
                morphed.Add(sc);
            }

            DisplayedChips = morphed;
            await RefetchWithFacetAsync(chip.Id);
            return;
        }

        // Regular chip (no sub-chips) → select it, refetch
        IsLocalChipActive = false;
        foreach (var c in DisplayedChips) c.IsSelected = c == chip;
        await RefetchWithFacetAsync(chip.Id);
    }

    public async Task RefetchWithFacetAsync(string? facet)
    {
        if (_homeFeedCache == null || _homeFeedService is null || !_homeFeedService.IsAvailable) return;

        _homeFeedCache.CurrentFacet = string.IsNullOrEmpty(facet) ? null : facet;
        _homeFeedCache.Invalidate();

        System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] facet={facet ?? "(null)"}, cache invalidated, about to fetch");
        _logger?.LogDebug("Refetching home with facet: {Facet}", facet ?? "(none)");

        // Tell the parent to flip IsLoading and clear any existing error — bypasses
        // the parent's LoadAsync guard so the refetch always runs.
        FacetRefetchStateChanged?.Invoke(this, new FacetRefetchEventArgs(IsLoading: true, HasError: false));

        try
        {
            var snapshot = await _homeFeedCache.FetchFreshAsync();
            System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] Got {snapshot.Sections.Count} sections, greeting={snapshot.Greeting}");
            _greetingSetter(snapshot.Greeting);
            var ordered = ApplyPreferences(snapshot.Sections);

            var localSection = ExtractLocalSection();
            if (Sections.Count == 0)
                Sections = new ObservableCollection<HomeSection>(ordered);
            else
                HomeFeedCache.ApplyDiff(Sections, ordered,
                    _greetingSetter, snapshot.Greeting,
                    s => s.ApplyTheme(_isDarkThemeProvider()));
            RestoreLocalSection(localSection);

            System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] After diff: {Sections.Count} sections displayed");
            SectionsApplied?.Invoke(this, ordered);
            await RefreshLocalSectionAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RefetchWithFacet] ERROR: {ex.Message}");
            _logger?.LogError(ex, "Failed to refetch with facet {Facet}", facet);
            FacetRefetchFailed?.Invoke(this, ex);
        }
        finally
        {
            FacetRefetchStateChanged?.Invoke(this, new FacetRefetchEventArgs(IsLoading: false, HasError: false));
        }
    }

    // ── Theme propagation ───────────────────────────────────────────────────

    /// <summary>
    /// Refresh per-section accent brushes for the supplied theme. Called by
    /// the parent's ApplyTheme so each shelf header re-tints.
    /// </summary>
    public void ApplyThemeToSections(bool isDark)
    {
        foreach (var section in Sections)
            section.ApplyTheme(isDark);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _localProgressSub?.Dispose();
    }
}

/// <summary>
/// Args for <see cref="HomeFeedViewModel.FacetRefetchStateChanged"/>. The
/// parent reads these to flip top-level <c>IsLoading</c> / <c>HasError</c>
/// without the child holding those flags itself.
/// </summary>
public sealed record FacetRefetchEventArgs(bool IsLoading, bool HasError);
