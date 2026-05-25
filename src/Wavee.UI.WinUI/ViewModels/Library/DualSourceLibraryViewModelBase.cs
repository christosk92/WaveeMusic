using System;
using Microsoft.UI.Dispatching;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// Shared source-toggle, per-source preference, and per-source search state for
/// library surfaces that expose Saved and From Liked Songs views.
/// </summary>
public abstract partial class DualSourceLibraryViewModelBase<TSaved, TLiked> : LibraryViewModelBase
    where TSaved : class
    where TLiked : class
{
    private readonly LibraryTabPreferences _savedPrefs = new();
    private readonly LibraryTabPreferences _likedPrefs = new();
    private string _savedSearchQuery = "";
    private string _likedSearchQuery = "";
    private LibrarySource _sourceMode = LibrarySource.Saved;

    protected DualSourceLibraryViewModelBase(
        ISettingsService? settingsService,
        ITrackLikeService? likeService,
        LibraryRecentsService? libraryRecents,
        DispatcherQueue? dispatcherQueue = null)
        : base(settingsService, likeService, libraryRecents, dispatcherQueue)
    {
    }

    protected abstract string SavedPreferencesKey { get; }
    protected abstract string LikedPreferencesKey { get; }
    protected bool LikedSideLoaded { get; set; }

    public LibrarySource SourceMode
    {
        get => _sourceMode;
        set
        {
            if (_sourceMode == value)
                return;

            var oldValue = _sourceMode;
            _sourceMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSavedSource));
            OnPropertyChanged(nameof(IsLikedSource));
            HandleSourceModeChanged(oldValue, value);
        }
    }

    public bool IsSavedSource => SourceMode == LibrarySource.Saved;
    public bool IsLikedSource => SourceMode == LibrarySource.FromLikedSongs;

    protected abstract LibrarySource ReadPersistedSource(AppSettings settings);
    protected abstract void WritePersistedSource(AppSettings settings, LibrarySource source);
    protected abstract void ApplyFilterCore();

    protected virtual void OnSourceModeChangedCore(LibrarySource oldValue, LibrarySource newValue) { }

    protected override void LoadPreferences()
    {
        SuppressPreferenceSave = true;
        try
        {
            var settings = SettingsService?.Settings;
            if (settings == null)
            {
                MarkPreferencesLoaded();
                return;
            }

            CopyInto(_savedPrefs, settings.LibraryTabs.TryGetValue(SavedPreferencesKey, out var saved) ? saved : null);
            CopyInto(_likedPrefs, settings.LibraryTabs.TryGetValue(LikedPreferencesKey, out var liked) ? liked : null);

            _sourceMode = ReadPersistedSource(settings);
            OnPropertyChanged(nameof(SourceMode));
            OnPropertyChanged(nameof(IsSavedSource));
            OnPropertyChanged(nameof(IsLikedSource));
            ApplyActivePrefsToBindings();
            MarkPreferencesLoaded();
        }
        finally
        {
            SuppressPreferenceSave = false;
        }
    }

    protected override void SavePreferences()
    {
        if (!PreferencesLoaded || SuppressPreferenceSave || SettingsService == null)
            return;

        CaptureBindingsIntoActivePrefs();

        SettingsService.Update(s =>
        {
            CapturePrefsIntoSlot(s, SavedPreferencesKey, _savedPrefs);
            CapturePrefsIntoSlot(s, LikedPreferencesKey, _likedPrefs);
            WritePersistedSource(s, SourceMode);
        });

        _ = SettingsService.SaveAsync();
    }

    protected override void OnSearchQueryChangedCore(string value)
    {
        if (SourceMode == LibrarySource.Saved)
            _savedSearchQuery = value ?? "";
        else
            _likedSearchQuery = value ?? "";

        ApplyFilterCore();
        SavePreferences();
    }

    protected override void OnSortChangedCore()
    {
        ApplyFilterCore();
    }

    private void HandleSourceModeChanged(LibrarySource oldValue, LibrarySource newValue)
    {
        if (!PreferencesLoaded)
            return;

        SuppressPreferenceSave = true;
        try
        {
            CaptureBindingsIntoSourcePrefs(oldValue);
            ApplyActivePrefsToBindings();
        }
        finally
        {
            SuppressPreferenceSave = false;
        }

        OnSourceModeChangedCore(oldValue, newValue);
        SavePreferences();
        ApplyFilterCore();
    }

    private void ApplyActivePrefsToBindings()
    {
        var prefs = SourceMode == LibrarySource.Saved ? _savedPrefs : _likedPrefs;
        var search = SourceMode == LibrarySource.Saved ? _savedSearchQuery : _likedSearchQuery;
        ApplyPrefsToBindings(prefs, search);
    }

    private void CaptureBindingsIntoActivePrefs()
    {
        CaptureBindingsIntoSourcePrefs(SourceMode);
    }

    private void CaptureBindingsIntoSourcePrefs(LibrarySource source)
    {
        var prefs = source == LibrarySource.Saved ? _savedPrefs : _likedPrefs;
        CaptureBindingsIntoPrefs(prefs);

        if (source == LibrarySource.Saved)
            _savedSearchQuery = SearchQuery ?? "";
        else
            _likedSearchQuery = SearchQuery ?? "";
    }

    private void CapturePrefsIntoSlot(AppSettings settings, string key, LibraryTabPreferences prefs)
    {
        var entry = EnsurePreferences(settings, key);
        entry.SortBy = prefs.SortBy;
        entry.SortDirection = prefs.SortDirection;
        entry.ViewMode = prefs.ViewMode;
        entry.GridScale = prefs.GridScale;
    }

}
