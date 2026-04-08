using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Connect.Playback;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.Queue;

/// <summary>
/// Display item bound by the shared TrackTemplate in ItemsRepeaters.
/// </summary>
public sealed class QueueDisplayItem
{
    public enum ItemKind { NowPlaying, Header, Track, Delimiter }

    public required ItemKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public string? BadgeGlyph { get; init; }
    public bool HasMetadata { get; init; } = true;
    public Visibility HasBadge => BadgeGlyph != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsLoaded => HasMetadata ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsShimmer => HasMetadata ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class QueueControl : UserControl
{
    private readonly IPlaybackStateService? _playbackService;
    private readonly ILogger? _logger;

    public QueueControl()
    {
        InitializeComponent();

        _playbackService = Ioc.Default.GetService<IPlaybackStateService>();
        _logger = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger("QueueControl");

        if (_playbackService is INotifyPropertyChanged pc)
        {
            pc.PropertyChanged += OnPropertyChanged;
            Unloaded += (_, _) => pc.PropertyChanged -= OnPropertyChanged;
        }

        Loaded += (_, _) => Refresh();
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.Queue)
            or nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackTitle)
            or nameof(IPlaybackStateService.CurrentArtistName)
            or nameof(IPlaybackStateService.CurrentAlbumArt))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private void Refresh()
    {
        if (_playbackService == null) return;

        var hasTrack = !string.IsNullOrEmpty(_playbackService.CurrentTrackId);

        // Access raw IQueueItem list from PlaybackStateService
        var svc = _playbackService as Wavee.UI.WinUI.Data.Contexts.PlaybackStateService;
        var rawNextQueue = svc?.RawNextQueue ?? [];

        _logger?.LogDebug("QueueControl.Refresh: hasTrack={HasTrack}, rawNext={RawCount}",
            hasTrack, rawNextQueue.Count);

        // ── Now Playing ──
        NowPlayingSection.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        if (hasTrack)
        {
            NowPlayingTitle.Text = _playbackService.CurrentTrackTitle ?? "Unknown";
            NowPlayingArtist.Text = _playbackService.CurrentArtistName ?? "";

            var artUrl = SpotifyImageHelper.ToHttpsUrl(_playbackService.CurrentAlbumArt);
            NowPlayingArt.Source = artUrl != null
                ? new BitmapImage(new System.Uri(artUrl)) { DecodePixelWidth = 40 }
                : null;
        }

        // ── Categorize raw queue items ──
        var userQueued = new List<QueueDisplayItem>();
        var nextFrom = new List<QueueDisplayItem>();
        QueueDelimiter? delimiter = null;
        bool hasAutoplay = false;

        foreach (var item in rawNextQueue)
        {
            switch (item)
            {
                case QueueTrack t when t.IsUserQueued:
                    userQueued.Add(new QueueDisplayItem
                    {
                        Kind = QueueDisplayItem.ItemKind.Track,
                        Title = t.Title ?? t.Uri,
                        Subtitle = t.Artist ?? "",
                        ImageUrl = t.ImageUrl,
                        BadgeGlyph = "\uE8CB",
                        HasMetadata = t.HasMetadata
                    });
                    break;
                case QueueTrack t:
                    if (t.IsAutoplay) hasAutoplay = true;
                    nextFrom.Add(new QueueDisplayItem
                    {
                        Kind = QueueDisplayItem.ItemKind.Track,
                        Title = t.Title ?? t.Uri,
                        Subtitle = t.Artist ?? "",
                        ImageUrl = t.ImageUrl,
                        HasMetadata = t.HasMetadata
                    });
                    break;
                case QueueDelimiter d:
                    delimiter = d;
                    break;
            }
        }

        // ── User Queue section ──
        UserQueueSection.Visibility = userQueued.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (userQueued.Count > 0)
        {
            UserQueueHeader.Text = $"Next in Queue \u00B7 {userQueued.Count}";
            UserQueueRepeater.ItemsSource = userQueued;
        }

        // ── Next up / autoplay section ──
        NextUpSection.Visibility = nextFrom.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (nextFrom.Count > 0)
        {
            NextUpHeader.Text = hasAutoplay
                ? $"Next from radio \u00B7 {nextFrom.Count}"
                : $"Next up \u00B7 {nextFrom.Count}";
            NextUpRepeater.ItemsSource = nextFrom;
        }

        // ── Delimiter ──
        DelimiterSection.Visibility = delimiter != null ? Visibility.Visible : Visibility.Collapsed;
        if (delimiter != null)
        {
            DelimiterText.Text = delimiter.AdvanceAction == "pause" ? "End of queue" : "Queue continues...";
        }

        // ── Empty state ──
        EmptyState.Visibility = !hasTrack && userQueued.Count == 0 && nextFrom.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
