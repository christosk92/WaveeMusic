using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Services.AddToPlaylist;

namespace Wavee.UI.WinUI.Controls.Search;

/// <summary>
/// Partial-class extension that wires <see cref="SearchResultRowCard"/> into
/// the app-wide <see cref="IAddToPlaylistSession"/>: feeds the trailing
/// <c>AddChip</c> with the current row's track identity, and hides the
/// decorative <c>ActionIcon</c> when the chip is showing so the two don't
/// overlap in the same Grid slot.
/// </summary>
public sealed partial class SearchResultRowCard
{
    private IAddToPlaylistSession? _addSession;
    private bool _addSessionHooked;

    private void HookAddToPlaylistSession()
    {
        if (_addSessionHooked) return;
        _addSession ??= Ioc.Default.GetService<IAddToPlaylistSession>();
        if (_addSession is null) return;
        _addSession.PropertyChanged += OnAddSessionPropertyChanged;
        _addSessionHooked = true;
        ApplyAddSessionVisuals();
    }

    private void UnhookAddToPlaylistSession()
    {
        if (!_addSessionHooked || _addSession is null) return;
        _addSession.PropertyChanged -= OnAddSessionPropertyChanged;
        _addSessionHooked = false;
    }

    private void OnAddSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (DispatcherQueue?.HasThreadAccess == true) ApplyAddSessionVisuals();
        else DispatcherQueue?.TryEnqueue(ApplyAddSessionVisuals);
    }

    /// <summary>Push the current row's identity into the trailing chip and
    /// hide the decorative ActionIcon when the chip is going to render.</summary>
    private void ApplyAddChipForItem(SearchResultItem? item)
    {
        if (AddChip is null) return;
        if (item is null || item.Type != SearchResultType.Track)
        {
            AddChip.IsEligible = false;
            AddChip.TrackUri = null;
            ApplyAddSessionVisuals();
            return;
        }
        AddChip.TrackUri = item.Uri;
        AddChip.TrackTitle = item.Name;
        AddChip.TrackArtistName = item.ArtistNames is { Count: > 0 } artists ? artists[0] : null;
        AddChip.TrackImageUrl = item.ImageUrl;
        AddChip.TrackDurationMs = item.DurationMs;
        AddChip.IsEligible = true;
        ApplyAddSessionVisuals();
    }

    private void ApplyAddSessionVisuals()
    {
        if (ActionIconHost is null) return;
        var chipWillShow = _addSession?.IsActive == true && _isTrack;
        ActionIconHost.Visibility = chipWillShow ? Visibility.Collapsed : Visibility.Visible;
    }
}
