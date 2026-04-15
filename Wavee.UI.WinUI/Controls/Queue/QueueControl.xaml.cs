using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.Audio.Queue;
using Wavee.UI.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;

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
    public bool HasMetadata { get; init; } = true;
    public double VisualOpacity { get; init; } = 1.0;
    public Visibility IsLoaded => HasMetadata ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsShimmer => HasMetadata ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class QueueControl : UserControl
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly InputCursor HandCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private readonly IPlaybackStateService? _playbackService;
    private readonly ImageCacheService? _imageCache;
    private readonly ILogger? _logger;

    public QueueControl()
    {
        InitializeComponent();

        _playbackService = Ioc.Default.GetService<IPlaybackStateService>();
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
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
            or nameof(IPlaybackStateService.CurrentAlbumArt)
            or nameof(IPlaybackStateService.IsShuffle)
            or nameof(IPlaybackStateService.RepeatMode))
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private void Refresh()
    {
        if (_playbackService == null) return;

        var hasTrack = !string.IsNullOrEmpty(_playbackService.CurrentTrackId);

        // Access raw IQueueItem list from PlaybackStateService.
        var svc = _playbackService as Wavee.UI.WinUI.Data.Contexts.PlaybackStateService;
        var rawNextQueue = svc?.RawNextQueue ?? [];

        _logger?.LogDebug("QueueControl.Refresh: hasTrack={HasTrack}, rawNext={RawCount}",
            hasTrack, rawNextQueue.Count);

        // ── Now Playing ──
        NowPlayingCard.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        if (hasTrack)
        {
            NowPlayingTitle.Text = _playbackService.CurrentTrackTitle ?? "Unknown";
            NowPlayingArtist.Text = _playbackService.CurrentArtistName ?? "";

            var artUrl = SpotifyImageHelper.ToHttpsUrl(_playbackService.CurrentAlbumArt);
            NowPlayingArt.Source = artUrl != null
                ? _imageCache?.GetOrCreate(artUrl, 48)
                  ?? new BitmapImage(new System.Uri(artUrl)) { DecodePixelWidth = 48 }
                : null;
            NowPlayingEqualizer.IsActive = _playbackService.IsPlaying;
        }

        // ── Categorize raw queue items into three buckets ──
        var userQueued = new List<QueueDisplayItem>();
        var nextFrom   = new List<QueueDisplayItem>();
        var autoplay   = new List<QueueDisplayItem>();
        QueueDelimiter? delimiter = null;

        foreach (var item in rawNextQueue)
        {
            switch (item)
            {
                case QueueTrack t when t.IsUserQueued:
                    userQueued.Add(ToDisplay(t, 1.0));
                    break;
                case QueueTrack t when t.IsAutoplay:
                {
                    int idx = autoplay.Count;
                    double opacity = Math.Max(0.35, 0.90 - (idx / 6.0 * 0.55));
                    autoplay.Add(ToDisplay(t, opacity));
                    break;
                }
                case QueueTrack t:
                    nextFrom.Add(ToDisplay(t, 1.0));
                    break;
                case QueueDelimiter d:
                    delimiter = d;
                    break;
            }
        }

        bool hasAutoplay = autoplay.Count > 0;

        // ── Pill states ──
        ShuffleButton.IsChecked = _playbackService.IsShuffle;
        RepeatButton.IsChecked = _playbackService.RepeatMode != RepeatMode.Off;
        RepeatGlyph.Glyph = _playbackService.RepeatMode == RepeatMode.Track
            ? "\uE8ED"   // RepeatOne
            : "\uE8EE";  // RepeatAll
        InfiniteButton.IsChecked = hasAutoplay;
        CrossfadeButton.IsChecked = false;

        // ── User Queue section ──
        UserQueueSection.Visibility = userQueued.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (userQueued.Count > 0)
        {
            UserQueueHeaderLabel.Text = $"QUEUE \u00B7 {userQueued.Count}";
            UserQueueRepeater.ItemsSource = userQueued;
        }
        else
        {
            UserQueueRepeater.ItemsSource = null;
        }

        // ── Next Up section (context continuation, non-autoplay) ──
        NextUpSection.Visibility = nextFrom.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (nextFrom.Count > 0)
        {
            NextUpHeader.Text = $"NEXT UP \u00B7 {nextFrom.Count}";
            NextUpRepeater.ItemsSource = nextFrom;
        }
        else
        {
            NextUpRepeater.ItemsSource = null;
        }

        // ── Autoplay section (similar music, dimmed) ──
        AutoPlaySection.Visibility = hasAutoplay ? Visibility.Visible : Visibility.Collapsed;
        if (hasAutoplay)
        {
            AutoPlayRepeater.ItemsSource = autoplay;
        }
        else
        {
            AutoPlayRepeater.ItemsSource = null;
        }

        // ── Delimiter ──
        DelimiterSection.Visibility = delimiter != null ? Visibility.Visible : Visibility.Collapsed;
        if (delimiter != null)
        {
            DelimiterText.Text = delimiter.AdvanceAction == "pause" ? "End of queue" : "Queue continues...";
        }

        // ── Empty state ──
        EmptyState.Visibility = !hasTrack && userQueued.Count == 0 && nextFrom.Count == 0 && !hasAutoplay
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static QueueDisplayItem ToDisplay(QueueTrack t, double opacity) => new()
    {
        Kind = QueueDisplayItem.ItemKind.Track,
        Title = t.Title ?? t.Uri,
        Subtitle = t.Artist ?? "",
        ImageUrl = t.ImageUrl,
        HasMetadata = t.HasMetadata,
        VisualOpacity = opacity,
    };

    // ── Pill click handlers ──

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackService == null) return;
        var desired = ShuffleButton.IsChecked == true;
        _logger?.LogInformation("Queue pill: shuffle → {State}", desired);
        _playbackService.SetShuffle(desired);
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackService == null) return;
        var next = _playbackService.RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Off,
            _ => RepeatMode.Off,
        };
        _logger?.LogInformation("Queue pill: repeat → {Mode}", next);
        _playbackService.SetRepeatMode(next);
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        // Backend clear-queue API not yet implemented — log a no-op for now so the
        // affordance is present without pretending to work.
        _logger?.LogInformation("Queue pill: clear queue (no-op, API pending)");
    }

    private void InfiniteButton_Click(object sender, RoutedEventArgs e)
    {
        // No backend autoplay toggle API yet — the checked state is driven by whether
        // autoplay tracks are present in the queue. Let the button toggle visually for
        // now; Refresh() will correct it on the next state update.
        _logger?.LogInformation("Queue pill: autoplay toggled (no-op, API pending)");
    }

    private void CrossfadeButton_Click(object sender, RoutedEventArgs e)
    {
        // No crossfade API yet — visual-only toggle.
        _logger?.LogInformation("Queue pill: crossfade toggled (no-op, API pending)");
    }

    // ── Track row hover state ──

    private void TrackRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid g) return;
        if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var brushObj)
            && brushObj is Brush brush)
        {
            g.Background = brush;
        }
    }

    private void TrackRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid g) return;
        g.Background = TransparentBrush;
    }

    // ── Drag handle cursor ──

    private void DragHandle_Loaded(object sender, RoutedEventArgs e)
    {
        // ChangeCursor sets the (protected) ProtectedCursor property via reflection, so
        // the icon shows the hand cursor whenever the pointer is over it. Done once at
        // realize time — no per-frame pointer event overhead.
        if (sender is FontIcon icon)
            icon.ChangeCursor(HandCursor);
    }
}
