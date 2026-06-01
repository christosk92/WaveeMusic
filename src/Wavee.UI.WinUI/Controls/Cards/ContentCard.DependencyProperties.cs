using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Data.Stores;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Dependency properties for <see cref="ContentCard"/> plus their
/// <c>PropertyChanged</c> callbacks. Extracted from the main code-behind so the
/// large surface area of bindable inputs (23 DPs) does not drown out the actual
/// interaction logic.
///
/// <para>All binding paths and DP identifiers are unchanged — call sites and
/// existing <c>{x:Bind}</c> / <c>{Binding}</c> targets keep working without
/// touching XAML.</para>
/// </summary>
public sealed partial class ContentCard
{
    // ── Image / placeholder / shape DPs ──────────────────────────────────────

    /// <summary>
    /// Back-compat shim. The actual gate lives in
    /// <see cref="Wavee.UI.WinUI.Services.ImageLoadingSuspension"/> so the new
    /// <see cref="Wavee.UI.WinUI.Controls.Imaging.CompositionImage"/> control
    /// can observe it without taking a dependency on this card.
    /// </summary>
    public static bool IsImageLoadingSuspended
    {
        get => ImageLoadingSuspension.IsSuspended;
        set => ImageLoadingSuspension.IsSuspended = value;
    }

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnSubtitleChanged));

    public static readonly DependencyProperty SubtitleMaxLinesProperty =
        DependencyProperty.Register(nameof(SubtitleMaxLines), typeof(int), typeof(ContentCard),
            new PropertyMetadata(2, OnSubtitleMaxLinesChanged));

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnBadgeChanged));

    public static readonly DependencyProperty BadgeForegroundProperty =
        DependencyProperty.Register(nameof(BadgeForeground), typeof(Brush), typeof(ContentCard),
            new PropertyMetadata(null, OnBadgeForegroundChanged));

    public static readonly DependencyProperty PlaceholderColorHexProperty =
        DependencyProperty.Register(nameof(PlaceholderColorHex), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnPlaceholderColorChanged));

    public static readonly DependencyProperty PlaceholderGlyphProperty =
        DependencyProperty.Register(nameof(PlaceholderGlyph), typeof(string), typeof(ContentCard),
            new PropertyMetadata("\uE8D6", OnPlaceholderGlyphChanged));

    public static readonly DependencyProperty IsCircularImageProperty =
        DependencyProperty.Register(nameof(IsCircularImage), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsCircularChanged));

    public static readonly DependencyProperty CenterTextProperty =
        DependencyProperty.Register(nameof(CenterText), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnCenterTextChanged));

    public static readonly DependencyProperty ImageSizeProperty =
        DependencyProperty.Register(nameof(ImageSize), typeof(double), typeof(ContentCard),
            new PropertyMetadata(0.0)); // 0 = auto (fill width for square, 120 for circle)

    /// <summary>
    /// Controls the image-host aspect ratio. <see cref="CardAspectMode.Square"/> is the
    /// historical default and keeps every existing ContentCard call site unchanged.
    /// <see cref="CardAspectMode.Tall"/> is 2:3 portrait (TV/movie posters);
    /// <see cref="CardAspectMode.Wide"/> and <see cref="CardAspectMode.Backdrop"/> are
    /// 16:9 landscape (music videos / continue-watching hero rails).
    /// Mutually exclusive with <see cref="IsCircularImage"/> — setting both falls back
    /// to circular at runtime.
    /// </summary>
    public static readonly DependencyProperty AspectModeProperty =
        DependencyProperty.Register(nameof(AspectMode), typeof(CardAspectMode), typeof(ContentCard),
            new PropertyMetadata(CardAspectMode.Square, OnAspectModeChanged));

    /// <summary>
    /// Spotify-style "category tile" mode. Flips the card from the standard
    /// art-on-top / title-below treatment to a full-bleed colored block with a
    /// bold bottom-left title and an optional small rotated artwork pinned to
    /// the bottom-right. The colored surface comes from <see cref="PlaceholderColorHex"/>.
    /// Default false — every existing call site keeps the original layout.
    /// </summary>
    public static readonly DependencyProperty IsCategoryTileProperty =
        DependencyProperty.Register(nameof(IsCategoryTile), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsCategoryTileChanged));

    /// <summary>
    /// Dense, unframed card treatment for embedded grids where the surrounding
    /// surface already provides structure. Keeps ContentCard behavior and image
    /// loading, but removes the outer chrome and tightens vertical spacing.
    /// </summary>
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsCompactChanged));

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// Maximum wrapped subtitle lines. Defaults to the historical two-line
    /// shelf card treatment; Home playlist cards opt into three lines for
    /// longer editorial descriptions.
    /// </summary>
    public int SubtitleMaxLines
    {
        get => (int)GetValue(SubtitleMaxLinesProperty);
        set => SetValue(SubtitleMaxLinesProperty, value);
    }

    /// <summary>
    /// Optional short accent line rendered beneath the subtitle. When <c>null</c> or empty
    /// the badge row is collapsed and the card retains its original height. Used today to
    /// show "Played 3h ago" on the library grid when the user sorts by Recents.
    /// </summary>
    public string? Badge
    {
        get => (string?)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    /// <summary>
    /// Optional foreground override for <see cref="Badge"/>. Null keeps the
    /// historical accent color used by library recents badges.
    /// </summary>
    public Brush? BadgeForeground
    {
        get => (Brush?)GetValue(BadgeForegroundProperty);
        set => SetValue(BadgeForegroundProperty, value);
    }

    public string? PlaceholderColorHex
    {
        get => (string?)GetValue(PlaceholderColorHexProperty);
        set => SetValue(PlaceholderColorHexProperty, value);
    }

    public string PlaceholderGlyph
    {
        get => (string)GetValue(PlaceholderGlyphProperty);
        set => SetValue(PlaceholderGlyphProperty, value);
    }

    public bool IsCircularImage
    {
        get => (bool)GetValue(IsCircularImageProperty);
        set => SetValue(IsCircularImageProperty, value);
    }

    public bool CenterText
    {
        get => (bool)GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public double ImageSize
    {
        get => (double)GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    public CardAspectMode AspectMode
    {
        get => (CardAspectMode)GetValue(AspectModeProperty);
        set => SetValue(AspectModeProperty, value);
    }

    public bool IsCategoryTile
    {
        get => (bool)GetValue(IsCategoryTileProperty);
        set => SetValue(IsCategoryTileProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    // ── Navigation DPs ───────────────────────────────────────────────────────

    public static readonly DependencyProperty NavigationUriProperty =
        DependencyProperty.Register(nameof(NavigationUri), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnNavigationUriChanged));

    public static readonly DependencyProperty NavigationTitleProperty =
        DependencyProperty.Register(nameof(NavigationTitle), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NavigationTotalTracksProperty =
        DependencyProperty.Register(nameof(NavigationTotalTracks), typeof(int), typeof(ContentCard),
            new PropertyMetadata(0));

    public static readonly DependencyProperty SubtitleNavigationUriProperty =
        DependencyProperty.Register(nameof(SubtitleNavigationUri), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnSubtitleNavigationChanged));

    public static readonly DependencyProperty SubtitleNavigationTitleProperty =
        DependencyProperty.Register(nameof(SubtitleNavigationTitle), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnSubtitleNavigationChanged));

    /// <summary>
    /// Origin-known track count for album / playlist cards. Forwarded into the
    /// <c>ContentNavigationParameter</c> built by <see cref="NavigateToUri"/>
    /// so the destination page renders an exact-count skeleton via
    /// <c>TrackDataGrid.LoadingRowCount</c>. Default 0 means "unknown" and the
    /// destination falls back to its default skeleton row count.
    /// </summary>
    public int NavigationTotalTracks
    {
        get => (int)GetValue(NavigationTotalTracksProperty);
        set => SetValue(NavigationTotalTracksProperty, value);
    }

    /// <summary>
    /// Spotify URI to navigate to when clicked (e.g. "spotify:artist:xxx").
    /// When set, the card handles navigation internally (like ShortsPill).
    /// </summary>
    public string? NavigationUri
    {
        get => (string?)GetValue(NavigationUriProperty);
        set => SetValue(NavigationUriProperty, value);
    }

    /// <summary>
    /// Fallback title for the navigation tab header.
    /// </summary>
    public string? NavigationTitle
    {
        get => (string?)GetValue(NavigationTitleProperty);
        set => SetValue(NavigationTitleProperty, value);
    }

    /// <summary>
    /// Optional URI used when the subtitle itself should navigate somewhere
    /// different from the card body, for example an album card's artist name.
    /// </summary>
    public string? SubtitleNavigationUri
    {
        get => (string?)GetValue(SubtitleNavigationUriProperty);
        set => SetValue(SubtitleNavigationUriProperty, value);
    }

    public string? SubtitleNavigationTitle
    {
        get => (string?)GetValue(SubtitleNavigationTitleProperty);
        set => SetValue(SubtitleNavigationTitleProperty, value);
    }

    public static readonly DependencyProperty IsExternalProperty =
        DependencyProperty.Register(nameof(IsExternal), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ShowPlaybackOverlayProperty =
        DependencyProperty.Register(nameof(ShowPlaybackOverlay), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(true, OnShowPlaybackOverlayChanged));

    public static readonly DependencyProperty AutoNavigateOnTapProperty =
        DependencyProperty.Register(nameof(AutoNavigateOnTap), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(true));

    /// <summary>
    /// When true (the default), tapping the card auto-routes through
    /// <see cref="NavigateToUri"/> if <see cref="NavigationUri"/> is set.
    /// When false, tapping the card fires <c>CardClick</c> as if
    /// <see cref="NavigationUri"/> were null — but the URI is still used by
    /// the viewport-prefetch path and by the <see cref="SecondaryActionVisible"/>
    /// "Open album" button. Use this on cards that want prefetch + the
    /// secondary affordance but need a custom primary tap (e.g. artist-page
    /// discography cards that expand inline on tap).
    /// </summary>
    public bool AutoNavigateOnTap
    {
        get => (bool)GetValue(AutoNavigateOnTapProperty);
        set => SetValue(AutoNavigateOnTapProperty, value);
    }

    public static readonly DependencyProperty SecondaryActionVisibleProperty =
        DependencyProperty.Register(nameof(SecondaryActionVisible), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false));

    // OpenInNewWindow (E8A7) - reads as "go to detail page" without looking
    // like an external-link arrow (NavigateExternalInline). Sourced via
    // FluentGlyphs to keep PUA literals out of .cs (CLAUDE.md convention).
    public static readonly DependencyProperty SecondaryActionGlyphProperty =
        DependencyProperty.Register(nameof(SecondaryActionGlyph), typeof(string), typeof(ContentCard),
            new PropertyMetadata(Styles.FluentGlyphs.OpenInNewWindow));

    public static readonly DependencyProperty SecondaryActionTooltipProperty =
        DependencyProperty.Register(nameof(SecondaryActionTooltip), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null));

    /// <summary>
    /// Show a small accent-coloured overlay button at top-right of the cover
    /// image. Click navigates via <see cref="Helpers.Navigation.AlbumNavigationHelper.NavigateToAlbum"/>
    /// (using <see cref="NavigationUri"/> / <see cref="NavigationTotalTracks"/>
    /// / etc.) and consumes the routed event so a parent's tap handler does
    /// not also fire. Designed for surfaces whose primary tap does something
    /// other than navigate (e.g. the discography cards on Artist Page that
    /// expand a track preview inline) — the secondary button gives the user
    /// a discrete "Open full album page" route.
    /// </summary>
    public bool SecondaryActionVisible
    {
        get => (bool)GetValue(SecondaryActionVisibleProperty);
        set => SetValue(SecondaryActionVisibleProperty, value);
    }

    public string SecondaryActionGlyph
    {
        get => (string)GetValue(SecondaryActionGlyphProperty);
        set => SetValue(SecondaryActionGlyphProperty, value);
    }

    public string? SecondaryActionTooltip
    {
        get => (string?)GetValue(SecondaryActionTooltipProperty);
        set => SetValue(SecondaryActionTooltipProperty, value);
    }

    /// <summary>
    /// When true, the hover overlay shows an "open in browser" button (globe icon)
    /// instead of the play button, and the play / now-playing chrome is suppressed.
    /// Use for cards whose target is an external URL (e.g. merch shop links). Click
    /// on the overlay fires <see cref="ExternalActionRequested"/>; clicking the card
    /// body still fires <see cref="CardClick"/> as usual.
    /// </summary>
    public bool IsExternal
    {
        get => (bool)GetValue(IsExternalProperty);
        set => SetValue(IsExternalProperty, value);
    }

    /// <summary>
    /// Controls the play / now-playing hover chrome for non-playable cards that
    /// still use ContentCard's layout and click routing, such as cast members.
    /// </summary>
    public bool ShowPlaybackOverlay
    {
        get => (bool)GetValue(ShowPlaybackOverlayProperty);
        set => SetValue(ShowPlaybackOverlayProperty, value);
    }

    // ── Behaviour DPs ────────────────────────────────────────────────────────

    public static readonly DependencyProperty IsPassiveProperty =
        DependencyProperty.Register(nameof(IsPassive), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsPassiveChanged));

    /// <summary>
    /// When true, the internal Button is disabled for hit testing so clicks pass through
    /// to a parent ItemContainer for selection. Hover/press animations still work via
    /// the UserControl's own pointer handlers.
    /// </summary>
    public bool IsPassive
    {
        get => (bool)GetValue(IsPassiveProperty);
        set => SetValue(IsPassiveProperty, value);
    }

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsLoadingChanged));

    /// <summary>
    /// When true, shows shimmer placeholders instead of real content (ghost/loading state).
    /// </summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsPlayingChanged));

    public static readonly DependencyProperty IsContextPausedProperty =
        DependencyProperty.Register(nameof(IsContextPaused), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsContextPausedChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsContextPaused
    {
        get => (bool)GetValue(IsContextPausedProperty);
        set => SetValue(IsContextPausedProperty, value);
    }

    // ── Property-changed callbacks ───────────────────────────────────────────

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var url = e.NewValue as string;

        // Category-tile mode owns its own bottom-right art slot; route the
        // URL there in addition to (or in place of) the standard square host.
        if (card.IsCategoryTile)
            card.ApplyCategoryTileArt(url);

        if (card.HasLoadedImageFor(url))
            return;

        if (IsImageLoadingSuspended)
        {
            if (!card.IsCurrentImageUrl(url))
                card.ReleaseImage();
            return;
        }

        // A *new* URL means the container was recycled onto a different item. A
        // virtualizing host (ItemsView / ItemsRepeater) only realizes elements in
        // or near the viewport, so the mirrored "_isInsideEffectiveViewport = false"
        // left over from this container's PREVIOUS position is stale — and
        // EffectiveViewportChanged does not reliably re-fire for the new position.
        // Honoring it here is exactly what stranded recycled-but-visible cards on
        // their placeholder. Reset the gate and load; if the card really is just
        // outside the viewport, the behavior re-samples on the next tick and the
        // (cached) load is cheap. This is the "re-trigger LoadImage on
        // re-realization" fix from project memory `feedback_contentcard_unload_nulls_image`.
        if (card.IsLoaded && card._hasEffectiveViewport && !card._isInsideEffectiveViewport)
        {
            card._hasEffectiveViewport = false;
            card._isInsideEffectiveViewport = true;
        }

        card.LoadImage(url);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var value = e.NewValue as string ?? "";
        card.TitleText.Text = value;
        if (card.CategoryTileTitle != null)
            card.CategoryTileTitle.Text = value;
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var value = e.NewValue as string;
        card.SubtitleText.Text = value ?? "";
        // Collapse when empty so cards without a subtitle (e.g. artist
        // circle cards that only carry a Title + Badge) don't reserve a
        // text-line slot in the ContentPanel — which previously pushed
        // the Badge far below the title.
        card.SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        card.UpdateSubtitleNavigationVisualState();
    }

    private static void OnSubtitleMaxLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.SubtitleText.MaxLines = Math.Max(1, (int)e.NewValue);
    }

    private static void OnBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        if (card.BadgeText == null) return;
        var value = e.NewValue as string;
        card.BadgeText.Text = value ?? "";
        card.BadgeText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnBadgeForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ContentCard)d).UpdateBadgeForeground();
    }

    private static void OnPlaceholderColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.ApplyPlaceholderColor(e.NewValue as string);
        if (card.IsCategoryTile)
            card.ApplyCategoryTileBackground();
    }

    private static void OnIsCategoryTileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.ApplyCategoryTileMode();
    }

    private static void OnIsCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ContentCard)d).ApplyDensityMode();
    }

    private static void OnPlaceholderGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var glyph = e.NewValue as string ?? "\uE8D6";
        if (card.SquarePlaceholderIcon != null)
            card.SquarePlaceholderIcon.Glyph = glyph;
        // CirclePlaceholderIcon only exists after CircleImageContainer is realized;
        // EnsureCircleRealized re-applies this glyph when the subtree loads.
        if (card.CirclePlaceholderIcon != null)
            card.CirclePlaceholderIcon.Glyph = glyph;
    }

    private static void OnIsCircularChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.UpdateImageMode();
    }

    private static void OnAspectModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        // Recompute the image-host height under the new aspect ratio. The
        // SizeChanged path won't fire if the container's measured width is
        // unchanged, so push from here directly.
        if (card.SquareImageContainer != null)
        {
            var width = card.ImageSize > 0
                ? card.ImageSize
                : card.SquareImageContainer.ActualWidth;
            if (width > 0)
                card.SetSquareImageSide(width);
        }
    }

    private static void OnCenterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var center = (bool)e.NewValue;
        var hAlign = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        var tAlign = center ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left;

        card.TitleText.HorizontalAlignment = hAlign;
        card.SubtitleText.HorizontalAlignment = hAlign;
        card.TitleText.TextAlignment = tAlign;
        card.SubtitleText.TextAlignment = tAlign;

        // Badge participates in CenterText too — without this, the recents
        // "Played Nd ago" pill stayed left-aligned under a centered title
        // on artist circle cards.
        if (card.BadgeText != null)
        {
            card.BadgeText.HorizontalAlignment = hAlign;
            card.BadgeText.TextAlignment = tAlign;
        }
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var loading = (bool)e.NewValue;
        if (card.ShimmerOverlay != null)
            card.ShimmerOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (card.ContentPanel != null)
            card.ContentPanel.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnIsPassiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Passive behaviour is enforced in CardButton_Click by redirecting to
        // parent ItemContainer selection. CardButton stays hit-testable so the
        // inner play-button overlay still receives clicks. The actual
        // pointer-event re-registration with handledEventsToo=true is owned by
        // CardPassivePointerBehavior (attached in XAML); it inspects IsPassive
        // when the card loads.
    }

    private static void OnShowPlaybackOverlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ContentCard)d).UpdatePlayingState();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.UpdatePlayingState();
    }

    private static void OnIsContextPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        if ((bool)e.NewValue)
            card.EnsurePlayOverlayRealized();
        card.UpdatePlayingState();
    }

    private static void OnNavigationUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.ResetPlaybackVisualStateForNewItem();
        card.SyncInitialPlaybackState();
        card.TryApplyCachedPlaylistTrackCount(e.NewValue as string);
    }

    // When a playlist card binds without an inline track count (Badge still empty),
    // read PlaylistStore synchronously — any playlist the user already opened has its
    // real count cached there. Free, no network; an uncached playlist just shows no
    // badge. Badge is bound BEFORE NavigationUri in the card's x:Bind declaration
    // order (HomePage.xaml), so by the time this fires an inline count (if the home
    // response carried one) is already in Badge and we skip — and because no later
    // binding re-pushes Badge, the value we set here survives. We deliberately do NOT
    // touch NavigationTotalTracks: its binding is declared after NavigationUri, so the
    // x:Bind push of the (still-zero) source would clobber it immediately. The header
    // prefill is best-effort anyway — the playlist page fetches the real count on open.
    private void TryApplyCachedPlaylistTrackCount(string? navUri)
    {
        if (!string.IsNullOrEmpty(Badge)) return;
        if (string.IsNullOrEmpty(navUri)
            || !navUri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            return;

        var cached = Ioc.Default.GetService<PlaylistStore>()?.PeekCached(navUri);
        if (cached is { TrackCount: > 0 } c)
            Badge = TrackCountFormatter.FormatTrackCount(c.TrackCount);
    }

    private static void OnSubtitleNavigationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ContentCard)d).UpdateSubtitleNavigationVisualState();
    }

    private void UpdateBadgeForeground()
    {
        if (BadgeText == null) return;

        BadgeText.Foreground = BadgeForeground
            ?? GetThemeBrush("AccentTextFillColorPrimaryBrush")
            ?? BadgeText.Foreground;
    }

    private void UpdateSubtitleNavigationVisualState()
    {
        if (SubtitleText == null) return;

        var hasLink = IsArtistSubtitleNavigationUri(SubtitleNavigationUri);
        SubtitleText.Foreground =
            GetThemeBrush(hasLink ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")
            ?? SubtitleText.Foreground;

        ToolTipService.SetToolTip(
            SubtitleText,
            hasLink ? $"Open {SubtitleNavigationTitle ?? Subtitle ?? "artist"}" : null);
    }

    private Brush? GetThemeBrush(string key)
    {
        return _themeColorService?.GetBrush(key);
    }

    private static bool IsArtistSubtitleNavigationUri(string? uri)
    {
        return !string.IsNullOrWhiteSpace(uri)
               && uri.StartsWith("spotify:artist:", StringComparison.OrdinalIgnoreCase);
    }
}
