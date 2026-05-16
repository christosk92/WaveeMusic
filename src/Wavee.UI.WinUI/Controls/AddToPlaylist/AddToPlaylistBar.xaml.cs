using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.Services.AddToPlaylist;
using Wavee.UI.WinUI.Controls.AvatarStack;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.AddToPlaylist;

/// <summary>
/// Floating "Adding to playlist…" bar hosted once in <c>ShellPage</c>. Binds
/// to the singleton <see cref="IAddToPlaylistSession"/> resolved from
/// <see cref="Ioc.Default"/> on load; mirrors the session's state into its
/// own INotifyPropertyChanged surface so x:Bind has plain CLR properties to
/// bind against. Persists across navigation because it lives above the
/// content Frame in the ShellPage visual tree.
/// </summary>
public sealed partial class AddToPlaylistBar : UserControl, INotifyPropertyChanged
{
    private const int MaxVisibleThumbs = 4;

    private IAddToPlaylistSession? _session;

    private string? _targetName;
    private string? _targetImageUrl;
    private IReadOnlyList<StackedThumbnailItem> _thumbnailItems = Array.Empty<StackedThumbnailItem>();
    private int _thumbnailOverflow;
    private bool _hasPending;
    private string _countLabel = string.Empty;
    private string _addButtonLabel = "Add";
    private string _flyoutHeader = "Pending songs";
    private ReadOnlyObservableCollection<PendingTrackEntry>? _pendingItems;
    private bool _isSubmitting;

    public AddToPlaylistBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? TargetName
    {
        get => _targetName;
        private set => SetField(ref _targetName, value);
    }

    public string? TargetImageUrl
    {
        get => _targetImageUrl;
        private set => SetField(ref _targetImageUrl, value);
    }

    public IReadOnlyList<StackedThumbnailItem> ThumbnailItems
    {
        get => _thumbnailItems;
        private set => SetField(ref _thumbnailItems, value);
    }

    public int ThumbnailOverflow
    {
        get => _thumbnailOverflow;
        private set => SetField(ref _thumbnailOverflow, value);
    }

    public bool HasPending
    {
        get => _hasPending;
        private set
        {
            if (_hasPending == value) return;
            _hasPending = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPending)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAddButtonEnabled)));
        }
    }

    public string CountLabel
    {
        get => _countLabel;
        private set => SetField(ref _countLabel, value);
    }

    public string AddButtonLabel
    {
        get => _addButtonLabel;
        private set => SetField(ref _addButtonLabel, value);
    }

    /// <summary>True while a submit is in-flight. Drives the Add button's
    /// idle ↔ spinner cross-fade and disables the button + Cancel to
    /// prevent double-submit / mid-flight cancellation.</summary>
    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (_isSubmitting == value) return;
            _isSubmitting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSubmitting)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIdleStateVisible)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAddButtonEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCancelEnabled)));
        }
    }

    /// <summary>True when the button should show its idle "+ Add (N)" content.
    /// False while a submit is in-flight (the spinner takes over).</summary>
    public bool IsIdleStateVisible => !IsSubmitting;

    /// <summary>Add button enabled only when there's something pending AND we
    /// aren't already mid-submit.</summary>
    public bool IsAddButtonEnabled => HasPending && !IsSubmitting;

    /// <summary>Cancel stays usable in idle mode but locks during a submit so
    /// the user can't tear the session out from under the in-flight wire op.</summary>
    public bool IsCancelEnabled => !IsSubmitting;

    public string FlyoutHeader
    {
        get => _flyoutHeader;
        private set => SetField(ref _flyoutHeader, value);
    }

    /// <summary>
    /// Full pending set surfaced for the flyout's <c>ListView.ItemsSource</c>.
    /// Bound once on session hookup — the underlying
    /// <see cref="IAddToPlaylistSession.Pending"/> raises CollectionChanged so
    /// the ListView updates without a rebind.
    /// </summary>
    public ReadOnlyObservableCollection<PendingTrackEntry>? PendingItems
    {
        get => _pendingItems;
        private set => SetField(ref _pendingItems, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_session is not null) return;
        _session = Ioc.Default.GetService<IAddToPlaylistSession>();
        if (_session is null) return;

        _session.PropertyChanged += OnSessionPropertyChanged;
        if (_session.Pending is INotifyCollectionChanged pendingNcc)
            pendingNcc.CollectionChanged += OnPendingCollectionChanged;

        PendingItems = _session.Pending;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        if (_session.Pending is INotifyCollectionChanged pendingNcc)
            pendingNcc.CollectionChanged -= OnPendingCollectionChanged;
        _session = null;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Mirror everything — the session's signals are coarse, properties are cheap.
        Refresh();
    }

    private void OnPendingCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Refresh();

    private void Refresh()
    {
        if (_session is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = _session.IsActive ? Visibility.Visible : Visibility.Collapsed;

        TargetName = _session.TargetPlaylistName;
        TargetImageUrl = _session.TargetPlaylistImageUrl;

        var pending = _session.Pending;
        var count = pending.Count;
        HasPending = count > 0;
        CountLabel = count switch
        {
            0 => string.Empty,
            1 => "1 song",
            _ => $"{count} songs"
        };
        AddButtonLabel = count > 0 ? $"Add ({count})" : "Add";
        FlyoutHeader = count switch
        {
            0 => "Pending songs",
            1 => "1 pending song",
            _ => $"{count} pending songs"
        };

        // Take the most recent N for the thumbnail strip — the last-added
        // track is the user's latest action and should be visible.
        var visible = pending
            .Skip(Math.Max(0, count - MaxVisibleThumbs))
            .Select(p => new StackedThumbnailItem(p.ImageUrl, $"{p.Title} — {p.ArtistName}"))
            .ToList();
        ThumbnailItems = visible;
        ThumbnailOverflow = Math.Max(0, count - MaxVisibleThumbs);
    }

    /// <summary>
    /// Submit handler — captures the target playlist id+name BEFORE
    /// <see cref="IAddToPlaylistSession.SubmitAsync"/> ends the session
    /// (which clears those properties), then navigates to the target so the
    /// user lands on the playlist they were just building.
    /// </summary>
    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || IsSubmitting) return;
        // Dismiss the pending-list flyout up-front so it doesn't linger
        // visible after the bar collapses (Popup tree isn't auto-light-
        // dismissed when its source button hides under it).
        PendingFlyout?.Hide();

        // Snapshot target identity before SubmitAsync clears it on success.
        var playlistId = _session.TargetPlaylistId;
        var playlistName = _session.TargetPlaylistName ?? string.Empty;

        IsSubmitting = true;
        try
        {
            var n = await _session.SubmitAsync().ConfigureAwait(true);
            if (n <= 0) return; // Empty submit — stay in mode.
            if (!string.IsNullOrEmpty(playlistId))
                NavigationHelpers.OpenPlaylist(playlistId, playlistName);
        }
        catch (Exception ex)
        {
            // Session keeps state on failure (see AddToPlaylistSession.SubmitAsync).
            // Surface the error via the shared notification toast — silently
            // swallowing was masking real failures (server rejection, auth,
            // playlist not found) which left the bar visible with no feedback.
            var notifier = Ioc.Default.GetService<INotificationService>();
            notifier?.Show(
                $"Couldn't add to playlist: {ex.Message}",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _session?.Cancel();
    }

    /// <summary>Clicking the target tile/label opens the playlist in the
    /// current tab. Session stays active so the user can keep shopping
    /// after peeking — submit/cancel still controls session lifetime.</summary>
    private void OnTargetClick(object sender, RoutedEventArgs e)
    {
        var playlistId = _session?.TargetPlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;
        NavigationHelpers.OpenPlaylist(playlistId, _session!.TargetPlaylistName ?? string.Empty);
    }

    /// <summary>Per-row × button in the flyout. Tag-binding carries the
    /// <see cref="PendingTrackEntry"/> so we can drop it directly via
    /// <see cref="IAddToPlaylistSession.Toggle"/>.</summary>
    private void OnRemovePendingClick(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        if (sender is FrameworkElement fe && fe.Tag is PendingTrackEntry entry)
            _session.Toggle(entry);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
