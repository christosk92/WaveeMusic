using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
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
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.Enums;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Controls.Reorder;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Queue;

/// <summary>
/// Display item bound by the shared TrackTemplate in ItemsRepeaters.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class QueueDisplayItem : ObservableObject
{
    public enum ItemKind { NowPlaying, Header, Track, Delimiter }

    public required ItemKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public bool HasMetadata { get; init; } = true;
    public double VisualOpacity { get; init; } = 1.0;

    /// <summary>Album URI � the title links here.</summary>
    public string? AlbumUri { get; init; }
    /// <summary>Primary-artist URI � the artist line links here.</summary>
    public string? ArtistUri { get; init; }
    /// <summary>Album display name, for the navigation tab header.</summary>
    public string? AlbumName { get; init; }
    /// <summary>Formatted track duration (e.g. "3:24"); empty when unknown.</summary>
    public string? Duration { get; init; }
    public bool IsExplicit { get; init; }
    public bool HasVideo { get; init; }
    /// <summary>The track's own Spotify URI � used to build the drag payload
    /// when a queue row is dragged onto a playlist / other drop target.</summary>
    public string? TrackUri { get; init; }

    /// <summary>0-based position within the upcoming context tail (Next-up +
    /// Autoplay rows together, in playback order); -1 for non-context rows.
    /// Assigned by <c>Refresh</c>; maps a context-section drag to a backend
    /// reorder index.</summary>
    public int ContextTailIndex { get; set; } = -1;

    /// <summary>0-based position among all upcoming next-tracks (Queue ? Next up
    /// ? Queued later ? Autoplay). The hover play button skips here.</summary>
    public int QueueIndex { get; set; } = -1;

    public Visibility IsLoaded => HasMetadata ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsShimmer => HasMetadata ? Visibility.Collapsed : Visibility.Visible;

    [ObservableProperty]
    public partial Brush? ArtworkTintBrush { get; set; }

    public Visibility ArtworkTintVisibility =>
        ArtworkTintBrush == null ? Visibility.Collapsed : Visibility.Visible;

    partial void OnArtworkTintBrushChanged(Brush? value)
        => OnPropertyChanged(nameof(ArtworkTintVisibility));

    public void ApplyArtworkTint(string? hex)
    {
        if (!TintColorHelper.TryParseHex(hex, out var parsed))
        {
            ArtworkTintBrush = null;
            return;
        }

        var tint = TintColorHelper.BrightenForTint(parsed);
        ArtworkTintBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(96, tint.R, tint.G, tint.B));
    }
}

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class QueueControl : UserControl
{
    private static readonly InputCursor HandCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private readonly IPlaybackStateService? _playbackService;
    private readonly IPlaybackService? _playbackCommandService;
    private readonly ISettingsService? _settingsService;
    private readonly ITrackColorHintService? _colorHintService;
    private readonly ILogger? _logger;

    // Rows that already have a ManualDragAttachment (guards against re-attaching
    // on container recycle).
    private readonly HashSet<UIElement> _dragAttachedRows = new();
    // Coalesce bursts of PropertyChanged (up to 7 per state batch) into a single
    // Refresh on the UI thread. Each Refresh re-materializes ~80 ItemsRepeater
    // containers; not deduping caused a 697ms flush on every playback transition.
    private bool _refreshQueued;
    // Set when a refresh-worthy playback change arrives while the queue tab is
    // hidden; flushed by SizeChanged when the tab is re-shown.
    private bool _pendingRefresh;
    private RepeatMode _repeatVisualMode = RepeatMode.Off;

    public QueueControl()
    {
        InitializeComponent();

        _playbackService  = Ioc.Default.GetService<IPlaybackStateService>();
        _playbackCommandService = Ioc.Default.GetService<IPlaybackService>();
        _settingsService  = Ioc.Default.GetService<ISettingsService>();
        _colorHintService = Ioc.Default.GetService<ITrackColorHintService>();
        _logger           = Ioc.Default.GetService<ILoggerFactory>()?.CreateLogger("QueueControl");

        if (_playbackService is INotifyPropertyChanged pc)
        {
            pc.PropertyChanged += OnPropertyChanged;
            Unloaded += (_, _) => pc.PropertyChanged -= OnPropertyChanged;
        }

        WeakReferenceMessenger.Default.Register<AutoplayEnabledChangedMessage>(this, (_, msg) =>
        {
            DispatcherQueue.TryEnqueue(() => InfiniteButton.IsChecked = msg.Value);
        });
        Unloaded += (_, _) => WeakReferenceMessenger.Default.UnregisterAll(this);

        Loaded += (_, _) => Refresh();
        // The queue tab is collapsed (but kept loaded + subscribed) when another
        // right-panel tab is shown. SizeChanged fires when a collapsed ancestor
        // re-arranges us back into view — that's our cue to apply any refresh we
        // deferred while hidden, so the queue is never stale when reopened.
        SizeChanged += (_, _) =>
        {
            if (_pendingRefresh && IsEffectivelyVisible())
            {
                _pendingRefresh = false;
                Refresh();
            }
        };
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.Queue)
            or nameof(IPlaybackStateService.CurrentTrackId)
            or nameof(IPlaybackStateService.CurrentTrackTitle)
            or nameof(IPlaybackStateService.CurrentArtistName)
            or nameof(IPlaybackStateService.CurrentAlbumArt)
            or nameof(IPlaybackStateService.CurrentContext)
            or nameof(IPlaybackStateService.IsShuffle)
            or nameof(IPlaybackStateService.RepeatMode))
        {
            // The queue panel re-materializes ~80 ItemsRepeater containers per
            // Refresh. Doing that on every track change while the tab is hidden was
            // pure waste (a chunk of the 477–806ms PlaybackStateFlush). Defer until
            // the tab is actually visible; SizeChanged above flushes the pending
            // refresh on reopen, so this is a visibility gate, not a stale hack.
            if (!IsEffectivelyVisible())
            {
                _pendingRefresh = true;
                return;
            }

            if (_refreshQueued) return;
            _refreshQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _refreshQueued = false;
                Refresh();
            });
        }
    }

    /// <summary>
    /// True only when this control and every ancestor are <see cref="Visibility.Visible"/>
    /// and it's in the live tree. Catches the right-panel collapsing the queue tab
    /// (an ancestor goes Collapsed) without coupling to the panel's view model.
    /// </summary>
    private bool IsEffectivelyVisible()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || XamlRoot is null)
            return false;

        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null)
        {
            if (node is FrameworkElement { Visibility: Visibility.Collapsed })
                return false;
            node = VisualTreeHelper.GetParent(node);
        }
        return true;
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

        ApplyContextCard();

        // ── Now Playing ──
        NowPlayingCard.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        if (hasTrack)
        {
            NowPlayingTitle.Text = _playbackService.CurrentTrackTitle ?? "Unknown";
            NowPlayingArtist.Text = _playbackService.CurrentArtistName ?? "";

            var artUrl = SpotifyImageHelper.ToHttpsUrl(_playbackService.CurrentAlbumArt);
            NowPlayingArt.Source = artUrl != null
                ? new BitmapImage(new System.Uri(artUrl)) { DecodePixelWidth = 48, DecodePixelType = DecodePixelType.Logical }
                : null;
            NowPlayingEqualizer.IsActive = _playbackService.IsPlaying;
        }

        // ── Categorize raw queue items into four buckets ──
        // Render order matches play order: Play-Next (head of user queue) → context → post-context → autoplay
        var userQueued   = new List<QueueDisplayItem>();
        var nextFrom     = new List<QueueDisplayItem>();
        var postContext  = new List<QueueDisplayItem>();
        var autoplay     = new List<QueueDisplayItem>();
        QueueDelimiter? delimiter = null;

        // Running index over the upcoming context tail � Next-up AND Autoplay
        // rows share one bucket (_contextTracks) in the backend, so this counter
        // spans both, in RawNextQueue (playback) order.
        int contextTailIndex = 0;
        int flatIndex = 0;   // 0-based position among all next-tracks (skip target)
        foreach (var item in rawNextQueue)
        {
            switch (item)
            {
                case QueueTrack t when t.IsPostContext:
                {
                    var d = ToDisplay(t, 1.0);
                    d.QueueIndex = flatIndex++;
                    postContext.Add(d);
                    break;
                }
                case QueueTrack t when t.IsUserQueued:
                {
                    var d = ToDisplay(t, 1.0);
                    d.QueueIndex = flatIndex++;
                    userQueued.Add(d);
                    break;
                }
                case QueueTrack t when t.IsAutoplay:
                {
                    int idx = autoplay.Count;
                    double opacity = Math.Max(0.35, 0.90 - (idx / 6.0 * 0.55));
                    var d = ToDisplay(t, opacity);
                    d.ContextTailIndex = contextTailIndex++;
                    d.QueueIndex = flatIndex++;
                    autoplay.Add(d);
                    break;
                }
                case QueueTrack t:
                {
                    var d = ToDisplay(t, 1.0);
                    d.ContextTailIndex = contextTailIndex++;
                    d.QueueIndex = flatIndex++;
                    nextFrom.Add(d);
                    break;
                }
                case QueueDelimiter d:
                    delimiter = d;
                    break;
            }
        }

        bool hasAutoplay = autoplay.Count > 0;
        ResolveArtworkTints(userQueued);
        ResolveArtworkTints(nextFrom);
        ResolveArtworkTints(postContext);
        ResolveArtworkTints(autoplay);

        // ── Pill states ──
        ShuffleButton.IsChecked = _playbackService.IsShuffle;
        ApplyRepeatPill(_playbackService.RepeatMode);
        InfiniteButton.IsChecked = _settingsService?.Settings.AutoplayEnabled ?? true;
        CrossfadeButton.IsChecked = false;

        // ── User Queue section ──
        UserQueueSection.Visibility = userQueued.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (userQueued.Count > 0)
            UserQueueHeaderLabel.Text = $"{AppLocalization.GetString("Queue_Section_Queue")} \u00B7 {userQueued.Count}";
        UserQueueRepeater.ItemsSource = userQueued.Count > 0 ? userQueued : null;

        // ── Next Up section (context continuation, non-autoplay) ──
        NextUpSection.Visibility = nextFrom.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (nextFrom.Count > 0)
            NextUpHeader.Text = $"{AppLocalization.GetString("Queue_Section_NextUp")} \u00B7 {nextFrom.Count}";
        NextUpRepeater.ItemsSource = nextFrom.Count > 0 ? nextFrom : null;

        // ── Queued later section (post-context bucket, plays after this context) ──
        PostContextSection.Visibility = postContext.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (postContext.Count > 0)
            PostContextHeader.Text = $"{AppLocalization.GetString("Queue_Section_QueuedLater")} � {postContext.Count}";
        PostContextRepeater.ItemsSource = postContext.Count > 0 ? postContext : null;

        // ── Autoplay section (similar music, dimmed) ──
        AutoPlaySection.Visibility = hasAutoplay ? Visibility.Visible : Visibility.Collapsed;
        AutoPlayRepeater.ItemsSource = hasAutoplay ? autoplay : null;

        // ── Delimiter ──
        DelimiterSection.Visibility = delimiter != null ? Visibility.Visible : Visibility.Collapsed;
        if (delimiter != null)
        {
            DelimiterText.Text = delimiter.AdvanceAction == "pause" ? "End of queue" : "Queue continues...";
        }

        // ── Empty state ──
        EmptyState.Visibility = !hasTrack && userQueued.Count == 0 && nextFrom.Count == 0 && postContext.Count == 0 && !hasAutoplay
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyContextCard()
    {
        var context = _playbackService?.CurrentContext;
        if (context is null || !IsNavigableContext(context))
        {
            ContextCard.Visibility = Visibility.Collapsed;
            ContextArt.Source = null;
            return;
        }

        ContextCard.Visibility = Visibility.Visible;
        ContextTitle.Text = GetContextTitle(context);

        var artUrl = SpotifyImageHelper.ToHttpsUrl(
            FirstNonWhiteSpace(
                context.ImageUrl,
                _playbackService?.CurrentAlbumArtLarge,
                _playbackService?.CurrentAlbumArt));
        ContextArt.Source = artUrl != null
            ? new BitmapImage(new System.Uri(artUrl)) { DecodePixelWidth = 48, DecodePixelType = DecodePixelType.Logical }
            : null;
    }

    private void ContextCard_Click(object sender, RoutedEventArgs e)
    {
        OpenCurrentContext();
    }

    private void OpenCurrentContext()
    {
        var context = _playbackService?.CurrentContext;
        if (context is null || !IsNavigableContext(context))
            return;

        var title = GetContextTitle(context);
        var param = new ContentNavigationParameter
        {
            Uri = context.ContextUri,
            Title = title,
            ImageUrl = context.ImageUrl ?? _playbackService?.CurrentAlbumArtLarge ?? _playbackService?.CurrentAlbumArt
        };

        switch (context.Type)
        {
            case PlaybackContextType.Playlist:
                NavigationHelpers.OpenPlaylist(param, param.Title);
                break;
            case PlaybackContextType.Album:
                NavigationHelpers.OpenAlbum(param, param.Title);
                break;
            case PlaybackContextType.Artist:
                NavigationHelpers.OpenArtist(param, param.Title);
                break;
            case PlaybackContextType.LikedSongs:
                NavigationHelpers.OpenLikedSongs();
                break;
            case PlaybackContextType.Show:
                NavigationHelpers.OpenShowPage(param.Uri, param.Title);
                break;
            case PlaybackContextType.Episode:
                if (param.Uri.Contains("your-episodes", StringComparison.OrdinalIgnoreCase))
                    NavigationHelpers.OpenYourEpisodes();
                else
                    NavigationHelpers.OpenEpisodePage(param.Uri, param.Title);
                break;
        }
    }

    private static bool IsNavigableContext(PlaybackContextInfo context)
        => !string.IsNullOrWhiteSpace(context.ContextUri)
           && context.Type is PlaybackContextType.Playlist
              or PlaybackContextType.Album
              or PlaybackContextType.Artist
              or PlaybackContextType.LikedSongs
              or PlaybackContextType.Show
              or PlaybackContextType.Episode;

    private static string GetContextTitle(PlaybackContextInfo context)
        => FirstNonWhiteSpace(context.Name, context.Type switch
        {
            PlaybackContextType.Playlist => "Playlist",
            PlaybackContextType.Album => "Album",
            PlaybackContextType.Artist => "Artist",
            PlaybackContextType.LikedSongs => "Liked Songs",
            PlaybackContextType.Show => "Show",
            PlaybackContextType.Episode => "Episode",
            _ => "Playback context"
        })!;

    private static string? FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static QueueDisplayItem ToDisplay(QueueTrack t, double opacity) => new()
    {
        Kind = QueueDisplayItem.ItemKind.Track,
        Title = t.Title ?? t.Uri,
        Subtitle = t.Artist ?? "",
        ImageUrl = t.ImageUrl,
        HasMetadata = t.HasMetadata,
        VisualOpacity = opacity,
        AlbumUri = t.AlbumUri,
        ArtistUri = t.ArtistUri,
        AlbumName = t.Album,
        Duration = FormatDuration(t.DurationMs),
        IsExplicit = t.IsExplicit || MetadataFlag(t.Metadata, "wavee.is_explicit", "is_explicit", "explicit"),
        HasVideo = t.HasVideo || MetadataHasVideo(t.Metadata),
        TrackUri = t.Uri,
    };

    private static bool MetadataFlag(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null) return false;

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value)
                && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "1", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MetadataHasVideo(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null) return false;

        if (MetadataFlag(metadata, "wavee.has_video", "has_video"))
            return true;

        return metadata.TryGetValue("track_player", out var player)
               && string.Equals(player, "video", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatDuration(int? ms)
    {
        if (ms is null or <= 0)
            return "";
        var ts = TimeSpan.FromMilliseconds(ms.Value);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // ── Pill click handlers ──

    private void ResolveArtworkTints(IEnumerable<QueueDisplayItem> items)
    {
        if (_colorHintService == null)
            return;

        foreach (var item in items)
        {
            var httpsUrl = SpotifyImageHelper.ToHttpsUrl(item.ImageUrl);
            if (string.IsNullOrWhiteSpace(httpsUrl))
            {
                item.ApplyArtworkTint(null);
                continue;
            }

            if (_colorHintService.TryGet(httpsUrl, out var cachedHex))
            {
                item.ApplyArtworkTint(cachedHex);
                continue;
            }

            _ = ResolveArtworkTintAsync(item, httpsUrl);
        }
    }

    private async Task ResolveArtworkTintAsync(QueueDisplayItem item, string httpsUrl)
    {
        try
        {
            var hex = await _colorHintService!.GetOrResolveAsync(httpsUrl).ConfigureAwait(true);
            item.ApplyArtworkTint(hex);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Queue artwork tint resolution failed for {Url}", httpsUrl);
        }
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackService == null) return;
        var desired = !_playbackService.IsShuffle;
        _logger?.LogInformation("Queue pill: shuffle ? {State}", desired);
        _playbackService.SetShuffle(desired);
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playbackService == null) return;
        var next = _repeatVisualMode switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Off,
            _ => RepeatMode.Off,
        };
        _logger?.LogInformation("Queue pill: repeat ? {Mode}", next);
        ApplyRepeatPill(next);
        _playbackService.SetRepeatMode(next);
    }

    private void ApplyRepeatPill(RepeatMode mode)
    {
        _repeatVisualMode = mode;
        RepeatButton.IsChecked = mode != RepeatMode.Off;
        RepeatGlyph.Glyph = mode == RepeatMode.Track
            ? "\uE8ED"   // RepeatOne
            : "\uE8EE";  // RepeatAll
        ToolTipService.SetToolTip(RepeatButton, mode switch
        {
            RepeatMode.Off => "Repeat off",
            RepeatMode.Context => "Repeat all",
            RepeatMode.Track => "Repeat one",
            _ => "Repeat"
        });
    }

    private void ClearQueueButton_Click(object sender, RoutedEventArgs e)
    {
        // Backend clear-queue API not yet implemented — log a no-op for now so the
        // affordance is present without pretending to work.
        _logger?.LogInformation("Queue pill: clear queue (no-op, API pending)");
    }

    private void UserQueue_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Wavee.UI.Services.DragDrop.DragFormats.Tracks)
            || e.DataView.Contains(Wavee.UI.Services.DragDrop.DragFormats.Album)
            || e.DataView.Contains(Wavee.UI.Services.DragDrop.DragFormats.Playlist)
            || e.DataView.Contains(Wavee.UI.Services.DragDrop.DragFormats.Artist))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            var shift = InputKeyboardSource
                .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            // Light up the queue panel as a drop zone; caption is the secondary hint.
            Wavee.UI.WinUI.DragDrop.DropHighlight.Apply(QueueScrollContent, Wavee.UI.WinUI.DragDrop.DropHighlight.Intensity.Zone);
            // Hint the modifier in the non-shift caption so first-time users can
            // discover the alternate route without reading docs. The ? glyph is in
            // the base Unicode plane so it round-trips fine through XAML/code.
            e.DragUIOverride.Caption = shift ? "Play next" : "Add to queue   ? for Play next";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            Wavee.UI.WinUI.DragDrop.DropHighlight.Clear(QueueScrollContent);
        }
    }

    private void UserQueue_DragLeave(object sender, DragEventArgs e)
        => Wavee.UI.WinUI.DragDrop.DropHighlight.Clear(QueueScrollContent);

    private async void UserQueue_Drop(object sender, DragEventArgs e)
    {
        Wavee.UI.WinUI.DragDrop.DropHighlight.Clear(QueueScrollContent);
        var dropService = Ioc.Default.GetService<Wavee.UI.Services.DragDrop.IDragDropService>();
        if (dropService is null) return;

        var payload = await Wavee.UI.WinUI.DragDrop.DragPackageReader.ReadAsync(e.DataView, dropService);
        if (payload is null) return;

        var modifiers = Wavee.UI.WinUI.DragDrop.DragModifiersCapture.Current();
        var ctx = new Wavee.UI.Services.DragDrop.DropContext(
            payload,
            Wavee.UI.Services.DragDrop.DropTargetKind.Queue,
            TargetId: null,
            Position: Wavee.UI.Services.DragDrop.DropPosition.Inside,
            TargetIndex: null,
            modifiers);
        var result = await dropService.DropAsync(ctx);
        if (result.UserMessage is { } msg)
        {
            Ioc.Default.GetService<INotificationService>()?
                .Show(msg, result.Success ? Wavee.UI.WinUI.Data.Models.NotificationSeverity.Informational : Wavee.UI.WinUI.Data.Models.NotificationSeverity.Warning,
                    TimeSpan.FromSeconds(3));
        }
    }

    private void InfiniteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;
        var current = _settingsService.Settings.AutoplayEnabled;
        var desired = !current;
        _logger?.LogInformation("Queue pill: autoplay ? {State}", desired);
        _settingsService.Update(s => s.AutoplayEnabled = desired);
        InfiniteButton.IsChecked = desired;
        WeakReferenceMessenger.Default.Send(new AutoplayEnabledChangedMessage(desired));
    }

    private void CrossfadeButton_Click(object sender, RoutedEventArgs e)
    {
        // No crossfade API yet — visual-only toggle.
        _logger?.LogInformation("Queue pill: crossfade toggled (no-op, API pending)");
        CrossfadeButton.IsChecked = false;
    }

    // ── Track row hover state ──

    // -- Row title / artist navigation --------------------------------------

    private void TitleLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueDisplayItem item }
            && !string.IsNullOrEmpty(item.AlbumUri))
        {
            var param = new ContentNavigationParameter
            {
                Uri = item.AlbumUri,
                Title = item.AlbumName ?? "",
                ImageUrl = item.ImageUrl,
            };
            NavigationHelpers.OpenAlbum(param, param.Title);
        }
    }

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueDisplayItem item }
            && !string.IsNullOrEmpty(item.ArtistUri))
        {
            NavigationHelpers.OpenArtist(item.ArtistUri, item.Subtitle ?? "");
        }
    }

    // -- Drag-reorder --------------------------------------------------------
    //   Intra-section reorder is owned by the react-beautiful-dnd ReorderController
    //   (one per section ListView; see QueueControl.Reorder.cs). It captures the row
    //   press, lifts the row, displaces neighbours, springs the drop, and hands off
    //   to OLE when the pointer leaves the list. Cross-surface drag-out (queue row →
    //   playlist) is still served by ManualDragAttachment, but only when reorder is
    //   disabled (remote playback) — otherwise the engine's own handoff covers it.

    private QueueReorderTarget? SectionTargetFor(ListView list)
    {
        if (ReferenceEquals(list, UserQueueRepeater)) return QueueReorderTarget.UserQueue;
        if (ReferenceEquals(list, PostContextRepeater)) return QueueReorderTarget.PostContextQueue;
        if (ReferenceEquals(list, NextUpRepeater) || ReferenceEquals(list, AutoPlayRepeater))
            return QueueReorderTarget.ContextUpcoming;
        return null;
    }

    // Attaches a manual drag source to each realized row, once.
    private void TrackRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || !_dragAttachedRows.Add(row))
            return;

        row.AddHandler(
            UIElement.RightTappedEvent,
            new RightTappedEventHandler(TrackRow_RightTapped),
            handledEventsToo: true);
        row.AddHandler(
            UIElement.HoldingEvent,
            new HoldingEventHandler(TrackRow_Holding),
            handledEventsToo: true);

        Wavee.UI.WinUI.DragDrop.ManualDragAttachment.AttachWithPackageWriter(
            row, () => BuildQueueDragPayload(row));

        // rbd reorder engine owns the in-section reorder gesture (incl. its own OLE
        // handoff). Attaches to the row's ListViewItem container; self-gates on
        // QueueReorderEnabled, so it's inert during remote playback.
        AttachReorderToRow(row);
    }

    // Runs at drag-start (pointer past the threshold). Records the reorder
    // source and returns a TrackDragPayload so the row can also be dropped on a
    // playlist.
    private Wavee.UI.Services.DragDrop.IDragPayload? BuildQueueDragPayload(FrameworkElement row)
    {
        // When reorder is active the ReorderController owns the gesture (including
        // its own OLE handoff for cross-surface drops), so suppress this OLE path
        // to avoid two drag systems competing on the same press. During remote
        // playback (reorder disabled) this is the only way to drag a queue row out.
        if (QueueReorderEnabled) return null;
        if (row.DataContext is not QueueDisplayItem item || string.IsNullOrEmpty(item.TrackUri))
            return null;

        // SourceContextUri null: the queue is not a playlist context, so a drop
        // on a playlist routes to "add tracks", never the intra-list reorder.
        return new Wavee.UI.Services.DragDrop.Payloads.TrackDragPayload(
            new[] { item.TrackUri }, sourceContextUri: null, sourceStartIndex: null);
    }

    // The user-queue section is the only OLE drop target left (external enqueue).
    // Its DragOver/Drop bind directly to UserQueue_DragOver / UserQueue_Drop in XAML;
    // intra-section reorder no longer routes through here.

    private async Task ReorderAsync(QueueReorderTarget target, int from, int to)
    {
        if (_playbackCommandService is null)
            return;
        try
        {
            await _playbackCommandService.ReorderQueueAsync(target, from, to);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Queue reorder failed: {Target} {From}->{To}", target, from, to);
        }
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

    // -- Hover play button ---------------------------------------------------

    private static void SetRowHoverState(object sender, bool shown)
    {
        if (sender is not FrameworkElement root)
            return;

        var hasLoadedTrack = root.DataContext is QueueDisplayItem { HasMetadata: true };
        var showActions = shown && hasLoadedTrack;

        if (root.FindName("RowPlayButton") is Button btn)
        {
            btn.Opacity = showActions ? 1 : 0;
            btn.IsHitTestVisible = showActions;
        }

        if (root.FindName("RowMoreButton") is Button more)
            more.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;

        if (root.FindName("RowDurationText") is TextBlock duration)
            duration.Visibility = showActions || !hasLoadedTrack ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TrackRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Touch has no hover: a finger-scroll would otherwise leave the row's play /
        // more buttons revealed, because the scroll capture delivers Canceled/
        // CaptureLost rather than PointerExited (issue #4). Mouse/pen still reveal.
        if (Wavee.UI.WinUI.DragDrop.PointerInput.IsTouch(e)) return;
        SetRowHoverState(sender, true);
    }

    // Also wired to PointerCanceled / PointerCaptureLost in XAML so a mouse
    // drag-capture (or any lost capture) clears the revealed hover controls.
    private void TrackRow_PointerExited(object sender, PointerRoutedEventArgs e) => SetRowHoverState(sender, false);

    private void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: QueueDisplayItem item } && item.QueueIndex >= 0)
            _ = SkipToQueueAsync(item.QueueIndex);
    }

    private void RowMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: QueueDisplayItem item } button)
            return;

        var origin = new Windows.Foundation.Point(0, button.ActualHeight);
        ShowQueueRowMenu(button, item, origin);
    }

    private async Task SkipToQueueAsync(int queueIndex)
    {
        if (_playbackCommandService is null || queueIndex < 0)
            return;
        try
        {
            await _playbackCommandService.SkipToQueueItemAsync(queueIndex);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Skip to queue item #{Index} failed", queueIndex);
        }
    }

    // -- Right-click context menu (shared track menu, queue-row adapter) ------

    private void TrackRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            return;   // touch raises Holding instead
        if (sender is FrameworkElement { DataContext: QueueDisplayItem item } root)
        {
            ShowQueueRowMenu(root, item, e.GetPosition(root));
            e.Handled = true;
        }
    }

    private void TrackRow_Holding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != Microsoft.UI.Input.HoldingState.Started)
            return;
        if (sender is FrameworkElement { DataContext: QueueDisplayItem item } root)
        {
            ShowQueueRowMenu(root, item, e.GetPosition(root));
            e.Handled = true;
        }
    }

    private void ShowQueueRowMenu(FrameworkElement row, QueueDisplayItem item, Windows.Foundation.Point position)
    {
        if (string.IsNullOrEmpty(item.TrackUri))
            return;

        var track = new QueueRowTrackItem(item);
        var ctx = new Wavee.UI.WinUI.Controls.ContextMenu.Builders.TrackMenuContext
        {
            // Play = skip playback to this queue item (the rest of the menu
            // falls back to the builder's URI-based defaults).
            PlayCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(
                () => _ = SkipToQueueAsync(item.QueueIndex)),
        };
        var menuItems = Wavee.UI.WinUI.Controls.ContextMenu.Builders.TrackContextMenuBuilder.Build(track, ctx);
        Wavee.UI.WinUI.Controls.ContextMenu.ContextMenuHost.Show(row, menuItems, position);
    }

    /// <summary>
    /// Lightweight <see cref="ITrackItem"/> over a <see cref="QueueDisplayItem"/>
    /// so the shared <c>TrackContextMenuBuilder</c> can be reused for queue rows.
    /// </summary>
    private sealed partial class QueueRowTrackItem : ITrackItem
    {
        private readonly QueueDisplayItem _src;
        public QueueRowTrackItem(QueueDisplayItem src) => _src = src;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public string Id => _src.TrackUri ?? "";
        public string Uri => _src.TrackUri ?? "";
        public string Title => _src.Title;
        public string ArtistName => _src.Subtitle ?? "";
        public string ArtistId => LastSegment(_src.ArtistUri);
        public string AlbumName => _src.AlbumName ?? "";
        public string AlbumId => LastSegment(_src.AlbumUri);
        public string? ImageUrl => _src.ImageUrl;
        public TimeSpan Duration => TimeSpan.Zero;
        public bool IsExplicit => _src.IsExplicit;
        public bool HasVideo => _src.HasVideo;
        public string DurationFormatted => _src.Duration ?? "";
        public int OriginalIndex => _src.QueueIndex;
        public bool IsLoaded => true;
        public bool IsLiked { get; set; }

        private static string LastSegment(string? uri)
            => string.IsNullOrEmpty(uri) ? "" : uri.Substring(uri.LastIndexOf(':') + 1);
    }
}
