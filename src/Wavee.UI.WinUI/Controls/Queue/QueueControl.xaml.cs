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
using Wavee.UI.Enums;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Queue;

/// <summary>
/// Display item bound by the shared TrackTemplate in ItemsRepeaters.
/// </summary>
public sealed partial class QueueDisplayItem : ObservableObject
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

    [ObservableProperty]
    private Brush? _artworkTintBrush;

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

public sealed partial class QueueControl : UserControl
{
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly InputCursor HandCursor =
        InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private readonly IPlaybackStateService? _playbackService;
    private readonly ISettingsService? _settingsService;
    private readonly ITrackColorHintService? _colorHintService;
    private readonly ILogger? _logger;
    // Coalesce bursts of PropertyChanged (up to 7 per state batch) into a single
    // Refresh on the UI thread. Each Refresh re-materializes ~80 ItemsRepeater
    // containers; not deduping caused a 697ms flush on every playback transition.
    private bool _refreshQueued;

    public QueueControl()
    {
        InitializeComponent();

        _playbackService  = Ioc.Default.GetService<IPlaybackStateService>();
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
            if (_refreshQueued) return;
            _refreshQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _refreshQueued = false;
                Refresh();
            });
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

        foreach (var item in rawNextQueue)
        {
            switch (item)
            {
                case QueueTrack t when t.IsPostContext:
                    postContext.Add(ToDisplay(t, 1.0));
                    break;
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
        ResolveArtworkTints(userQueued);
        ResolveArtworkTints(nextFrom);
        ResolveArtworkTints(postContext);
        ResolveArtworkTints(autoplay);

        // ── Pill states ──
        ShuffleButton.IsChecked = _playbackService.IsShuffle;
        RepeatButton.IsChecked = _playbackService.RepeatMode != RepeatMode.Off;
        RepeatGlyph.Glyph = _playbackService.RepeatMode == RepeatMode.Track
            ? "\uE8ED"   // RepeatOne
            : "\uE8EE";  // RepeatAll
        InfiniteButton.IsChecked = _settingsService?.Settings.AutoplayEnabled ?? true;
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

        // ── Queued later section (post-context bucket, plays after this context) ──
        PostContextSection.Visibility = postContext.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (postContext.Count > 0)
        {
            PostContextHeader.Text = $"QUEUED LATER · {postContext.Count}";
            PostContextRepeater.ItemsSource = postContext;
        }
        else
        {
            PostContextRepeater.ItemsSource = null;
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
    };

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
            e.DragUIOverride.Caption = shift ? "Play next" : "Add to queue";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    private async void UserQueue_Drop(object sender, DragEventArgs e)
    {
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
        _logger?.LogInformation("Queue pill: autoplay → {State}", desired);
        _settingsService.Update(s => s.AutoplayEnabled = desired);
        InfiniteButton.IsChecked = desired;
        WeakReferenceMessenger.Default.Send(new AutoplayEnabledChangedMessage(desired));
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
