using System;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Contracts;
using Wavee.UI.Services.AddToPlaylist;
using Wavee.UI.WinUI.Styles;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Partial-class extension on <see cref="TrackItem"/> that wires the
/// app-wide "Add to playlist" affordance. While an
/// <see cref="IAddToPlaylistSession"/> is active, every realized
/// <c>TrackItem</c> hides its heart and shows a + (or check) button that
/// toggles the track in the session's pending set. Tap-the-row playback is
/// untouched — only the dedicated button enrols a track.
/// </summary>
public sealed partial class TrackItem
{
    private IAddToPlaylistSession? _addSession;
    private bool _isAddSessionHooked;

    /// <summary>Called from <c>OnLoaded</c> to subscribe to the singleton
    /// session and paint the initial affordance state.</summary>
    private void HookAddToPlaylistSession()
    {
        if (_isAddSessionHooked) return;
        _addSession ??= Ioc.Default.GetService<IAddToPlaylistSession>();
        if (_addSession is null) return;

        _addSession.PropertyChanged += OnAddSessionPropertyChanged;
        if (_addSession.Pending is INotifyCollectionChanged pendingNcc)
            pendingNcc.CollectionChanged += OnAddSessionPendingChanged;
        _isAddSessionHooked = true;

        UpdateAddToPlaylistAffordance();
    }

    /// <summary>Called from <c>OnUnloaded</c> to release the subscription so
    /// recycled rows don't pin the session in the static event list.</summary>
    private void UnhookAddToPlaylistSession()
    {
        if (!_isAddSessionHooked || _addSession is null) return;
        _addSession.PropertyChanged -= OnAddSessionPropertyChanged;
        if (_addSession.Pending is INotifyCollectionChanged pendingNcc)
            pendingNcc.CollectionChanged -= OnAddSessionPendingChanged;
        _isAddSessionHooked = false;
    }

    private void OnAddSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mirror everything — IsActive flips, target changes, pending tweaks
        // all need a single repaint of this row's affordance.
        if (DispatcherQueue?.HasThreadAccess == true)
            UpdateAddToPlaylistAffordance();
        else
            DispatcherQueue?.TryEnqueue(UpdateAddToPlaylistAffordance);
    }

    private void OnAddSessionPendingChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DispatcherQueue?.HasThreadAccess == true)
            UpdateAddToPlaylistAffordance();
        else
            DispatcherQueue?.TryEnqueue(UpdateAddToPlaylistAffordance);
    }

    /// <summary>Repaint the + / heart affordance based on the current
    /// session state and this row's <see cref="Track"/>. Safe to call any
    /// time after the templates have realised — silently no-ops if the
    /// per-mode button isn't realized (the other mode's subtree).</summary>
    private void UpdateAddToPlaylistAffordance()
    {
        var track = Track;
        var active = _addSession?.IsActive == true && track is not null && !string.IsNullOrEmpty(track.Uri);
        var pending = active && _addSession!.Contains(track!.Uri);
        var glyph = pending ? FluentGlyphs.CheckMark : FluentGlyphs.Add;

        // Row-mode affordance — guarded against compact-only realisations.
        if (RowAddToPlaylistButton is not null)
        {
            RowAddToPlaylistButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (RowAddToPlaylistGlyph is not null)
                RowAddToPlaylistGlyph.Glyph = glyph;
        }
        if (RowHeartButton is not null && active)
            RowHeartButton.Visibility = Visibility.Collapsed;
        else if (RowHeartButton is not null && IsRowMode && track is not null && !active)
            RowHeartButton.Visibility = Visibility.Visible;

        // Compact-mode affordance — symmetrical handling.
        if (CompactAddToPlaylistButton is not null)
        {
            CompactAddToPlaylistButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (CompactAddToPlaylistGlyph is not null)
                CompactAddToPlaylistGlyph.Glyph = glyph;
        }
        if (CompactHeartButton is not null && active)
            CompactHeartButton.Visibility = Visibility.Collapsed;
        else if (CompactHeartButton is not null && IsCompactMode && track is not null && !active)
            CompactHeartButton.Visibility = Visibility.Visible;
    }

    private void RowAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        => ToggleAddToPlaylistForCurrentTrack();

    private void CompactAddToPlaylistButton_Click(object sender, RoutedEventArgs e)
        => ToggleAddToPlaylistForCurrentTrack();

    private void ToggleAddToPlaylistForCurrentTrack()
    {
        var track = Track;
        if (_addSession is null || !_addSession.IsActive) return;
        if (track is null || string.IsNullOrEmpty(track.Uri)) return;

        // Image: prefer the small URL so the floating bar's thumbnails decode
        // cheaply. Falls back to the full-size URL if the small isn't present
        // (some surfaces — e.g. search adapters — only carry one).
        var imageUrl = track.ImageSmallUrl ?? track.ImageUrl;
        var entry = new PendingTrackEntry(
            Uri: track.Uri,
            Title: track.Title ?? string.Empty,
            ArtistName: track.ArtistName,
            ImageUrl: imageUrl,
            Duration: track.Duration);
        _addSession.Toggle(entry);
    }
}
