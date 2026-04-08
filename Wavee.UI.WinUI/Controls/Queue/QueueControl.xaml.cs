using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.Connect.Playback;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Controls.Queue;

/// <summary>
/// Flat display item for the queue. Rendered by <see cref="QueueItemTemplateSelector"/>.
/// </summary>
public sealed class QueueDisplayItem
{
    public enum ItemKind { NowPlaying, Header, Track, Delimiter }

    public required ItemKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public string? BadgeGlyph { get; init; }
    public Visibility HasBadge => BadgeGlyph != null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Selects DataTemplate based on <see cref="QueueDisplayItem.Kind"/>.
/// </summary>
public sealed class QueueItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NowPlayingTemplate { get; set; }
    public DataTemplate? TrackTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? DelimiterTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
    {
        if (item is QueueDisplayItem di)
        {
            return di.Kind switch
            {
                QueueDisplayItem.ItemKind.NowPlaying => NowPlayingTemplate,
                QueueDisplayItem.ItemKind.Header => HeaderTemplate,
                QueueDisplayItem.ItemKind.Track => TrackTemplate,
                QueueDisplayItem.ItemKind.Delimiter => DelimiterTemplate,
                _ => TrackTemplate
            };
        }
        return TrackTemplate;
    }
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
        var items = new List<QueueDisplayItem>();

        _logger?.LogDebug("QueueControl.Refresh: hasTrack={HasTrack}, rawNext={RawCount}",
            hasTrack, rawNextQueue.Count);

        // Now Playing
        if (hasTrack)
        {
            items.Add(new QueueDisplayItem
            {
                Kind = QueueDisplayItem.ItemKind.NowPlaying,
                Title = _playbackService.CurrentTrackTitle ?? "Unknown",
                Subtitle = _playbackService.CurrentArtistName ?? "",
                ImageUrl = _playbackService.CurrentAlbumArt,
            });
        }

        // Categorize queue items
        var userQueued = new List<QueueTrack>();
        var nextFrom = new List<QueueTrack>();
        QueueDelimiter? delimiter = null;

        foreach (var item in rawNextQueue)
        {
            switch (item)
            {
                case QueueTrack t when t.IsUserQueued:
                    userQueued.Add(t);
                    break;
                case QueueTrack t:
                    nextFrom.Add(t);
                    break;
                case QueueDelimiter d:
                    delimiter = d;
                    break;
            }
        }

        // User queue section
        if (userQueued.Count > 0)
        {
            items.Add(new QueueDisplayItem
            {
                Kind = QueueDisplayItem.ItemKind.Header,
                Title = $"Next in Queue \u00B7 {userQueued.Count}"
            });
            foreach (var t in userQueued)
            {
                items.Add(new QueueDisplayItem
                {
                    Kind = QueueDisplayItem.ItemKind.Track,
                    Title = t.Title ?? t.Uri,
                    Subtitle = t.Artist ?? "",
                    ImageUrl = t.ImageUrl,
                    BadgeGlyph = "\uE8CB"
                });
            }
        }

        // Next from context/autoplay
        if (nextFrom.Count > 0)
        {
            var isAutoplay = nextFrom.Any(t => t.IsAutoplay);
            var label = isAutoplay ? "Next from radio" : "Next up";
            items.Add(new QueueDisplayItem
            {
                Kind = QueueDisplayItem.ItemKind.Header,
                Title = $"{label} \u00B7 {nextFrom.Count}"
            });
            foreach (var t in nextFrom)
            {
                items.Add(new QueueDisplayItem
                {
                    Kind = QueueDisplayItem.ItemKind.Track,
                    Title = t.Title ?? t.Uri,
                    Subtitle = t.Artist ?? "",
                    ImageUrl = t.ImageUrl,
                });
            }
        }

        // Delimiter
        if (delimiter != null)
        {
            items.Add(new QueueDisplayItem
            {
                Kind = QueueDisplayItem.ItemKind.Delimiter,
                Title = delimiter.AdvanceAction == "pause" ? "End of queue" : "Queue continues..."
            });
        }

        QueueRepeater.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
