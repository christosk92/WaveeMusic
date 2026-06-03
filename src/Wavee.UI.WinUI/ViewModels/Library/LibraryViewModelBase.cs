using System;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.ViewModels.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Common library-page state: loading, search, sort/view preferences, and
/// optional save-state / recents subscriptions.
/// </summary>
public abstract partial class LibraryViewModelBase : TrackListViewModelBase
{
    private bool _isLoading;
    private string _searchQuery = "";
    private LibrarySortBy _sortBy = LibrarySortBy.Recents;
    private LibrarySortDirection _sortDirection = LibrarySortDirection.Descending;
    private LibraryViewMode _viewMode = LibraryViewMode.DefaultGrid;
    private double _gridScale = 0.7;
    private bool _longLivedAttached;

    protected LibraryViewModelBase(
        ISettingsService? settingsService,
        ITrackLikeService? likeService,
        LibraryRecentsService? libraryRecents,
        DispatcherQueue? dispatcherQueue = null)
    {
        SettingsService = settingsService;
        LikeService = likeService;
        LibraryRecents = libraryRecents;
        DispatcherQueue = dispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    protected ISettingsService? SettingsService { get; }
    protected ITrackLikeService? LikeService { get; }
    protected LibraryRecentsService? LibraryRecents { get; }
    protected DispatcherQueue DispatcherQueue { get; }
    protected bool PreferencesLoaded { get; private set; }
    protected bool SuppressPreferenceSave { get; set; }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            value ??= "";
            if (SetProperty(ref _searchQuery, value))
                OnSearchQueryChangedCore(value);
        }
    }

    public LibrarySortBy SortBy
    {
        get => _sortBy;
        set
        {
            if (!IsAllowedSortKey(value))
                value = DefaultSortBy;
            if (SetProperty(ref _sortBy, value))
            {
                OnSortChangedCore();
                SavePreferences();
            }
        }
    }

    public LibrarySortDirection SortDirection
    {
        get => _sortDirection;
        set
        {
            if (SetProperty(ref _sortDirection, value))
            {
                OnSortChangedCore();
                SavePreferences();
            }
        }
    }

    public LibraryViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                OnViewModeChangedCore(value);
                SavePreferences();
            }
        }
    }

    public double GridScale
    {
        get => _gridScale;
        set
        {
            if (SetProperty(ref _gridScale, value))
            {
                OnGridScaleChangedCore(value);
                SavePreferences();
            }
        }
    }

    protected virtual string? PreferencesKey => null;
    protected virtual LibrarySortBy DefaultSortBy => LibrarySortBy.Recents;
    protected virtual bool UsesGridScale => true;
    protected virtual bool IsAllowedSortKey(LibrarySortBy key) => true;

    protected virtual void OnSearchQueryChangedCore(string value)
    {
        SavePreferences();
    }

    protected virtual void OnSortChangedCore() { }
    protected virtual void OnViewModeChangedCore(LibraryViewMode value) { }
    protected virtual void OnGridScaleChangedCore(double value) { }
    protected virtual void OnSaveStateChangedFromBase() { }
    protected virtual void OnRecentsChangedFromBase() { }

    protected virtual void LoadPreferences()
    {
        SuppressPreferenceSave = true;
        try
        {
            var key = PreferencesKey;
            var settings = SettingsService?.Settings;
            if (settings == null || string.IsNullOrEmpty(key))
            {
                MarkPreferencesLoaded();
                return;
            }

            ApplyPrefsToBindings(settings.LibraryTabs.TryGetValue(key, out var prefs) ? prefs : null, "");
            MarkPreferencesLoaded();
        }
        finally
        {
            SuppressPreferenceSave = false;
        }
    }

    protected virtual void SavePreferences()
    {
        var key = PreferencesKey;
        if (!PreferencesLoaded || SuppressPreferenceSave || SettingsService == null || string.IsNullOrEmpty(key))
            return;

        SettingsService.Update(s =>
        {
            var entry = EnsurePreferences(s, key);
            CaptureBindingsIntoPrefs(entry);
        });

        _ = SettingsService.SaveAsync();
    }

    protected void MarkPreferencesLoaded()
    {
        PreferencesLoaded = true;
    }

    protected void ApplyPrefsToBindings(LibraryTabPreferences? prefs, string searchQuery)
    {
        var active = prefs ?? new LibraryTabPreferences();

        _sortBy = Enum.TryParse<LibrarySortBy>(active.SortBy, ignoreCase: true, out var sb) && IsAllowedSortKey(sb)
            ? sb
            : DefaultSortBy;
        _sortDirection = Enum.TryParse<LibrarySortDirection>(active.SortDirection, ignoreCase: true, out var sd)
            ? sd
            : LibrarySortDirection.Descending;
        _viewMode = Enum.TryParse<LibraryViewMode>(active.ViewMode, ignoreCase: true, out var vm)
            ? vm
            : LibraryViewMode.DefaultGrid;
        _gridScale = active.GridScale >= 0.5 && active.GridScale <= 2.0
            ? active.GridScale
            : 0.7;
        _searchQuery = searchQuery ?? "";

        OnPropertyChanged(nameof(SortBy));
        OnPropertyChanged(nameof(SortDirection));
        OnPropertyChanged(nameof(ViewMode));
        OnPropertyChanged(nameof(GridScale));
        OnPropertyChanged(nameof(SearchQuery));
    }

    protected void CaptureBindingsIntoPrefs(LibraryTabPreferences prefs)
    {
        prefs.SortBy = SortBy.ToString();
        prefs.SortDirection = SortDirection.ToString();
        prefs.ViewMode = ViewMode.ToString();
        if (UsesGridScale)
            prefs.GridScale = GridScale;
    }

    protected static void CopyInto(LibraryTabPreferences destination, LibraryTabPreferences? source)
    {
        if (source == null) return;
        destination.SortBy = source.SortBy;
        destination.SortDirection = source.SortDirection;
        destination.ViewMode = source.ViewMode;
        destination.GridScale = source.GridScale;
    }

    protected static LibraryTabPreferences EnsurePreferences(AppSettings settings, string key)
    {
        if (!settings.LibraryTabs.TryGetValue(key, out var entry) || entry == null)
        {
            entry = new LibraryTabPreferences();
            settings.LibraryTabs[key] = entry;
        }

        return entry;
    }

    protected void AttachLongLivedServices()
    {
        if (_longLivedAttached) return;
        _longLivedAttached = true;

        if (LikeService != null)
            LikeService.SaveStateChanged += OnSaveStateChangedFromBase;
        if (LibraryRecents != null)
            LibraryRecents.RecentsChanged += OnRecentsChangedFromBase;
    }

    protected void DetachLongLivedServices()
    {
        if (!_longLivedAttached) return;
        _longLivedAttached = false;

        if (LikeService != null)
            LikeService.SaveStateChanged -= OnSaveStateChangedFromBase;
        if (LibraryRecents != null)
            LibraryRecents.RecentsChanged -= OnRecentsChangedFromBase;
    }

    /// <summary>
    /// Human-friendly last-played formatter: "Played just now" / "Played 12m
    /// ago" / "Played 3h ago" / "Played 2d ago" / "Played Mar 15" for older
    /// entries.
    /// </summary>
    protected static string FormatRecentsSubtitle(DateTimeOffset playedAt)
    {
        var delta = DateTimeOffset.UtcNow - playedAt;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta < TimeSpan.FromSeconds(60)) return "Played just now";
        if (delta < TimeSpan.FromMinutes(60)) return $"Played {(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromHours(24)) return $"Played {(int)delta.TotalHours}h ago";
        if (delta < TimeSpan.FromDays(7)) return $"Played {(int)delta.TotalDays}d ago";
        return $"Played {playedAt.LocalDateTime:MMM d, yyyy}";
    }
}
