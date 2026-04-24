using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PlaylistPage : Page
{
    private readonly ILogger? _logger;
    private readonly ISettingsService _settings;
    private bool _isNarrowMode;
    // Tracks the last-rendered playlist so the cached page can reset per-playlist
    // view state (filter text) when the bound playlist actually changes.
    private string? _lastPlaylistId;

    // Composition resources for the left-anchored hero backdrop. Surface is
    // (re)loaded whenever HeaderImageUrl changes. Null when no header image.
    // The hero tree is: ContainerVisual → { _heroSprite (image, masked), _heroScrimSprite
    // (theme-colored scrim with the same mask shape so text on top stays legible) }.
    private Compositor? _heroCompositor;
    private ContainerVisual? _heroContainer;
    private CompositionSurfaceBrush? _heroSurfaceBrush;
    private SpriteVisual? _heroSprite;
    private SpriteVisual? _heroScrimSprite;
    private CompositionColorBrush? _heroScrimColorBrush;
    private LoadedImageSurface? _heroImageSurface;
    private string? _appliedHeroUrl;

    // Scrim strength: tune alpha to taste. 0xB0 (~69%) matched the prior XAML scrim.
    private const byte HeroScrimMaxAlpha = 0xB0;

    public PlaylistViewModel ViewModel { get; }

    public PlaylistPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlaylistViewModel>();
        _logger = Ioc.Default.GetService<ILogger<PlaylistPage>>();
        _settings = Ioc.Default.GetRequiredService<ISettingsService>();
        InitializeComponent();

        Func<object, string> addedFormatter = item =>
        {
            if (item is PlaylistTrackDto track)
                return track.AddedAtFormatted;
            if (item is LazyTrackItem lazy && lazy.Data is PlaylistTrackDto inner)
                return inner.AddedAtFormatted;
            return "";
        };
        TrackGrid.DateAddedFormatter = addedFormatter;

        // Editorial / radio playlists don't carry added-at timestamps — hide the whole
        // Date Added column when the loaded tracks have none. Also watch HeaderImageUrl
        // so the composition backdrop reloads when the ViewModel's detail arrives.
        ViewModel.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(PlaylistViewModel.HasAnyAddedAt))
                ApplyDateAddedColumnVisibility();
            else if (ev.PropertyName == nameof(PlaylistViewModel.HeaderImageUrl))
                ApplyHeaderBackground();
            else if (ev.PropertyName == nameof(PlaylistViewModel.PlaylistDescription))
                RebuildDescriptionInlines();
        };
        ApplyDateAddedColumnVisibility();
        RebuildDescriptionInlines();

        HeaderBackgroundHost.Loaded += HeaderBackgroundHost_Loaded;
        HeaderBackgroundHost.Unloaded += HeaderBackgroundHost_Unloaded;
        ActualThemeChanged += PlaylistPage_ActualThemeChanged;
    }

    private void PlaylistPage_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_heroScrimColorBrush != null)
            _heroScrimColorBrush.Color = GetHeroScrimColor();
    }

    private Windows.UI.Color GetHeroScrimColor()
    {
        // Scrim to the theme's base surface color so the hero image gets tinted back
        // toward the normal page background — restoring the contrast track-row text
        // was designed against. Pulls directly from the theme dictionary so a runtime
        // theme switch can repaint without restart.
        var themeKey = ActualTheme == ElementTheme.Light ? "Light" : "Default";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj)
            && dictObj is ResourceDictionary themeDict
            && themeDict.TryGetValue("SolidBackgroundFillColorBase", out var colorObj)
            && colorObj is Windows.UI.Color themed)
        {
            return themed;
        }
        // Resource missing — let WinUI resolve via standard lookup.
        return (Windows.UI.Color)Application.Current.Resources["SolidBackgroundFillColorBase"];
    }

    private void ApplyDateAddedColumnVisibility()
    {
        if (TrackGrid.Columns is null) return;
        var dateCol = TrackGrid.Columns.FirstOrDefault(c => c.Key == "DateAdded");
        if (dateCol is null) return;
        dateCol.IsVisible = ViewModel.HasAnyAddedAt;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _logger?.LogInformation(
            "PlaylistPage.OnNavigatedTo: parameter type={Type}, value={Value}",
            e.Parameter?.GetType().FullName ?? "<null>", e.Parameter);

        string? playlistId = null;

        if (e.Parameter is Data.Parameters.ContentNavigationParameter nav)
        {
            _logger?.LogInformation(
                "PlaylistPage.OnNavigatedTo: ContentNavigationParameter Uri='{Uri}', Title='{Title}', Subtitle='{Subtitle}', ImageUrl='{ImageUrl}'",
                nav.Uri, nav.Title, nav.Subtitle, nav.ImageUrl);
            playlistId = nav.Uri;
            // Activate first so its new-playlist clear-down runs BEFORE PrefillFrom
            // writes the nav values — otherwise the clear would wipe the prefill and
            // the UI would stay blank until the store's Ready push arrives.
            ViewModel.Activate(nav.Uri);
            ViewModel.PrefillFrom(nav);
        }
        else if (e.Parameter is string rawId && !string.IsNullOrWhiteSpace(rawId))
        {
            _logger?.LogInformation("PlaylistPage.OnNavigatedTo: string parameter '{RawId}'", rawId);
            playlistId = rawId;
            ViewModel.Activate(rawId);
        }
        else
        {
            _logger?.LogWarning("PlaylistPage.OnNavigatedTo: unrecognized parameter shape — no load triggered");
        }

        if (!string.IsNullOrEmpty(playlistId))
        {
            if (!string.Equals(playlistId, _lastPlaylistId, StringComparison.Ordinal))
            {
                // Different playlist than the one we last rendered (or first nav):
                // drop the grid's filter so a leftover query from Playlist A doesn't
                // hide tracks on Playlist B. Sort + column widths intentionally persist.
                TrackGrid.ResetFilter();
                _lastPlaylistId = playlistId;
                AnimatePlaylistSwap();
            }
            RestorePlaylistPanelWidth(playlistId);
        }
    }

    /// <summary>
    /// Short cross-panel fade on playlist change. Without this, cached-page
    /// navigations snap the old content straight to the new one (no animation,
    /// because the page instance and its Visibility states don't actually change).
    /// Targets the two-column grid root so the hero panel AND the track grid fade
    /// together — anything less feels partial.
    /// </summary>
    private void AnimatePlaylistSwap()
    {
        if (TwoColumnGrid is null) return;
        TwoColumnGrid.Opacity = 0;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, TwoColumnGrid);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Drop the store subscription — refcount hits zero and any inflight
        // fetch/CTS is cancelled cleanly (kills TaskCanceledException spam
        // that comes from leaving fetches running after navigation).
        ViewModel.Deactivate();
    }

    private void RestorePlaylistPanelWidth(string playlistId)
    {
        const double defaultWidth = 200;
        var key = $"playlist:{playlistId}";

        var width = _settings.Settings.PanelWidths.TryGetValue(key, out var saved)
            ? saved
            : defaultWidth;

        width = Math.Clamp(width, 200, 500);
        LeftPanelColumn.Width = new GridLength(width, GridUnitType.Pixel);
    }

    private void PlaylistArtContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border border && e.NewSize.Width > 0)
            border.Height = e.NewSize.Width;
    }

    private void HeaderBackgroundHost_Loaded(object sender, RoutedEventArgs e)
    {
        // First-time setup: build the composition tree. Must happen after the element
        // is parented (hence Loaded, not the constructor).
        if (_heroCompositor == null)
        {
            var visual = ElementCompositionPreview.GetElementVisual(HeaderBackgroundHost);
            _heroCompositor = visual.Compositor;

            // Corner-anchored 2D fade: opaque at the top-left, falling off radially
            // toward both the right and bottom edges, fully transparent toward the
            // bottom-right. A single CompositionRadialGradientBrush gives BOTH
            // horizontal and vertical falloff in one brush — accepted by
            // CompositionMaskBrush.Mask, no Win2D effect tree required.
            //
            // Why this shape and not the alternatives:
            //   • Two-stage mask chains (MaskBrush.Source = MaskBrush) throw at
            //     runtime — "Unsupported source brush type".
            //   • CompositionMaskBrush.Mask also rejects CompositionEffectBrush, so a
            //     gradient-multiply baked via ArithmeticCompositeEffect can't be used
            //     as a mask either.
            //   • Assigning the effect brush directly to a sprite worked on first
            //     load but stopped re-rendering after the source surface was swapped
            //     for a new playlist's image (the effect tree got stuck on a stale /
            //     transiently-disposed surface).
            //   • RadialGradient with EllipseCenter at (0,0) avoids all three issues
            //     and inherits the surface-swap robustness of MaskBrush.
            CompositionRadialGradientBrush BuildCornerFadeMask(byte maxAlpha)
            {
                var g = _heroCompositor.CreateRadialGradientBrush();
                g.EllipseCenter = new Vector2(0f, 0f); // top-left corner
                g.EllipseRadius = new Vector2(1f, 1f); // reaches the bottom-right corner (normalized)
                g.ColorStops.Add(_heroCompositor.CreateColorGradientStop(0f,
                    Windows.UI.Color.FromArgb(maxAlpha, 255, 255, 255)));
                g.ColorStops.Add(_heroCompositor.CreateColorGradientStop(1f,
                    Windows.UI.Color.FromArgb(0, 255, 255, 255)));
                return g;
            }

            // ── Hero image sprite ──────────────────────────────────────────────
            _heroSurfaceBrush = _heroCompositor.CreateSurfaceBrush();
            _heroSurfaceBrush.Stretch = CompositionStretch.UniformToFill;
            // Anchor the crop to the left edge of the source image — it's the portion
            // the user will actually see after the gradient eats the right side.
            _heroSurfaceBrush.HorizontalAlignmentRatio = 0f;
            _heroSurfaceBrush.VerticalAlignmentRatio = 0.5f;

            var heroMask = _heroCompositor.CreateMaskBrush();
            heroMask.Source = _heroSurfaceBrush;
            heroMask.Mask = BuildCornerFadeMask(0xFF);

            _heroSprite = _heroCompositor.CreateSpriteVisual();
            _heroSprite.Brush = heroMask;
            _heroSprite.RelativeSizeAdjustment = Vector2.One;

            // ── Theme-aware scrim sprite (layered above the hero) ─────────────
            // Solid color pulled from SolidBackgroundFillColorBase so the scrim
            // tints toward the page's normal surface color — legible contrast is
            // whatever the theme already designs for. Mask alpha caps at
            // HeroScrimMaxAlpha so the hero still bleeds through, not a flat block.
            _heroScrimColorBrush = _heroCompositor.CreateColorBrush(GetHeroScrimColor());

            var scrimMask = _heroCompositor.CreateMaskBrush();
            scrimMask.Source = _heroScrimColorBrush;
            scrimMask.Mask = BuildCornerFadeMask(HeroScrimMaxAlpha);

            _heroScrimSprite = _heroCompositor.CreateSpriteVisual();
            _heroScrimSprite.Brush = scrimMask;
            _heroScrimSprite.RelativeSizeAdjustment = Vector2.One;

            // ── Container holding both, attached as the element's child visual ─
            _heroContainer = _heroCompositor.CreateContainerVisual();
            _heroContainer.RelativeSizeAdjustment = Vector2.One;
            _heroContainer.Children.InsertAtBottom(_heroSprite);   // image at the back
            _heroContainer.Children.InsertAtTop(_heroScrimSprite); // scrim in front
        }

        // (Re)attach the container on every Loaded. WinUI detaches the child visual when
        // the element unloads (cached-page nav-away), so a plain "already set up, skip"
        // early return would leave the host blank on subsequent returns to this page.
        if (_heroContainer != null)
            ElementCompositionPreview.SetElementChildVisual(HeaderBackgroundHost, _heroContainer);

        // Theme may have changed while the page was cached — refresh the scrim color.
        if (_heroScrimColorBrush != null)
            _heroScrimColorBrush.Color = GetHeroScrimColor();

        // (Re)load the image on every Loaded. The previous Unloaded dropped the surface
        // to free decoded pixels; reset _appliedHeroUrl so the dedupe in ApplyHeaderBackground
        // doesn't short-circuit a load against a now-null surface.
        _appliedHeroUrl = null;
        ApplyHeaderBackground();
    }

    private void HeaderBackgroundHost_Unloaded(object sender, RoutedEventArgs e)
    {
        // Cached page: Unloaded fires on nav-away but the instance stays alive.
        // Drop the image surface so we don't leak decoded pixels across navigations;
        // the sprite + brush are reusable and recreate cheaply on the next Loaded.
        _heroImageSurface?.Dispose();
        _heroImageSurface = null;
        _appliedHeroUrl = null;
        if (_heroSurfaceBrush != null)
            _heroSurfaceBrush.Surface = null;
    }

    private void ApplyHeaderBackground()
    {
        if (_heroSurfaceBrush == null || _heroCompositor == null) return;

        var url = ViewModel.HeaderImageUrl;

        // No-op if the URL hasn't changed — PropertyChanged can fire redundantly
        // when the ViewModel re-assigns the same value during a refresh cycle.
        if (string.Equals(_appliedHeroUrl, url, StringComparison.Ordinal))
            return;

        _heroImageSurface?.Dispose();
        _heroImageSurface = null;

        var httpsUrl = string.IsNullOrEmpty(url) ? null : SpotifyImageHelper.ToHttpsUrl(url);

        if (string.IsNullOrEmpty(httpsUrl))
        {
            _heroSurfaceBrush.Surface = null;
            _appliedHeroUrl = null;
            // No hero → also hide the scrim, otherwise a solid color block would
            // darken the left of the page on playlists with no header image.
            SetHeroScrimVisible(false);
            return;
        }

        var desiredSize = new Windows.Foundation.Size(
            Math.Max(1, HeaderBackgroundHost.ActualWidth > 0 ? HeaderBackgroundHost.ActualWidth : 1200),
            Math.Max(1, HeaderBackgroundHost.ActualHeight > 0 ? HeaderBackgroundHost.ActualHeight : 800));
        _heroImageSurface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), desiredSize);
        _heroSurfaceBrush.Surface = _heroImageSurface;
        _appliedHeroUrl = url;
        SetHeroScrimVisible(true);
    }

    private void SetHeroScrimVisible(bool visible)
    {
        if (_heroScrimSprite != null)
            _heroScrimSprite.IsVisible = visible;
    }

    private void PlaylistSplitter_ResizeCompleted(object? sender, GridSplitterResizeCompletedEventArgs e)
    {
        var playlistId = ViewModel.PlaylistId;
        if (string.IsNullOrEmpty(playlistId)) return;

        _settings.Update(s => s.PanelWidths[$"playlist:{playlistId}"] = e.NewWidth);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var shouldBeNarrow = e.NewSize.Width < 600;

        if (shouldBeNarrow && !_isNarrowMode)
        {
            _isNarrowMode = true;
            LeftPanelColumn.MinWidth = 0;
            LeftPanelColumn.Width = new GridLength(0);
            VisualStateManager.GoToState(this, "NarrowState", true);
        }
        else if (!shouldBeNarrow && _isNarrowMode)
        {
            _isNarrowMode = false;
            LeftPanelColumn.MinWidth = 200;
            var playlistId = ViewModel.PlaylistId;
            if (!string.IsNullOrEmpty(playlistId))
                RestorePlaylistPanelWidth(playlistId);
            else
                LeftPanelColumn.Width = new GridLength(200, GridUnitType.Pixel);

            VisualStateManager.GoToState(this, "WideState", true);
        }
        else if (!shouldBeNarrow && !_isNarrowMode)
        {
            VisualStateManager.GoToState(this, "WideState", true);
        }
    }

    // ── Description: HTML decode + clickable spotify: hyperlinks + More/Less ─────

    // Set true once we've observed the description being trimmed at MaxLines=3.
    // Stays true while the user toggles between More/Less so the button doesn't
    // disappear after expanding (RichTextBlock.IsTextTrimmed flips back to false
    // when MaxLines becomes 0). Cleared whenever the description text changes.
    private bool _descriptionWasTrimmed;
    private bool _descriptionExpanded;

    private void RebuildDescriptionInlines()
    {
        if (DescriptionRichText == null) return;

        // Reset toggle state for the new description.
        _descriptionWasTrimmed = false;
        _descriptionExpanded = false;
        DescriptionRichText.MaxLines = 3;
        DescriptionMoreButton.Visibility = Visibility.Collapsed;
        if (DescriptionMoreLabel != null) DescriptionMoreLabel.Text = "More...";

        DescriptionRichText.Blocks.Clear();
        var html = ViewModel?.PlaylistDescription;
        if (string.IsNullOrEmpty(html)) return;

        var paragraph = new Paragraph();
        foreach (var token in SpotifyHtmlHelper.Tokenize(html))
        {
            if (token.IsLink && !string.IsNullOrEmpty(token.Uri))
            {
                // Match the look of WinUI's HyperlinkButton (accent foreground, no
                // underline) instead of the default browser-style blue underline.
                var link = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.None,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                };
                link.Inlines.Add(new Run { Text = token.Text });
                var capturedUri = token.Uri;
                var capturedText = token.Text;
                link.Click += (_, _) => RouteSpotifyUri(capturedUri, capturedText);
                paragraph.Inlines.Add(link);
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = token.Text });
            }
        }
        DescriptionRichText.Blocks.Add(paragraph);
    }

    private void DescriptionRichText_IsTextTrimmedChanged(RichTextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        // RichTextBlock has its own typed handler (RichTextBlock, not TextBlock).
        // Only flip our latch when collapsed and currently trimmed; once latched,
        // the More button stays available even after the user toggles to expanded.
        if (_descriptionExpanded) return;
        if (DescriptionRichText.IsTextTrimmed && !_descriptionWasTrimmed)
        {
            _descriptionWasTrimmed = true;
            DescriptionMoreButton.Visibility = Visibility.Visible;
        }
    }

    private void DescriptionMoreButton_Click(object sender, RoutedEventArgs e)
    {
        _descriptionExpanded = !_descriptionExpanded;
        DescriptionRichText.MaxLines = _descriptionExpanded ? 0 : 3;
        if (DescriptionMoreLabel != null)
            DescriptionMoreLabel.Text = _descriptionExpanded ? "Show less" : "More...";
    }

    private static void RouteSpotifyUri(string uri, string displayName)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            NavigationHelpers.OpenPlaylist(uri, displayName);
        else if (uri.StartsWith("spotify:album:", StringComparison.Ordinal))
            NavigationHelpers.OpenAlbum(uri, displayName);
        else if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
            NavigationHelpers.OpenArtist(uri, displayName);
        // Anything else (track, episode, etc.) — silently ignore for now; the user
        // can request a route in follow-up work if those start showing up in
        // editorial descriptions.
    }
}
