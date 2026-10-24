// ── Themes/WinUi/Structure/Rules/WinUiArtistLightboxRules.cs — the numbers of WinUI's gallery lightbox (themes plan 6.1, T10b2) ──
//
// What `Entities\Artist.UI.cs`'s `GalleryLightbox` held as constants and literals, moved verbatim with its picture (`Views\Pages\WinUiArtistLightbox.cs`):
// the chrome's idle hide and the unzoom settle, the zoom range and its wheel and click steps, the pan's drag slop and its spring, the filmstrip's thumbs, the
// top chrome row, the scrims and the photo's reveal.

using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's gallery lightbox numbers.</summary>
internal static class WinUiArtistLightboxRules
{
    // ── timing ──
    /// <summary>The floating chrome hides after this much pointer stillness; the pager comes back this long after an unzoom spring starts.</summary>
    public const float IdleHideMs = 2600f, UnzoomSettleMs = 260f;

    // ── zoom ──
    /// <summary>The zoom ceiling, the step a click or a Ctrl+wheel entry zooms to, and the wheel's gain per DIP of delta (~7 % per 64-DIP notch).</summary>
    public const float ZoomMax = 4f, ZoomClick = 2.2f, ZoomWheelK = 0.0011f;

    /// <summary>A wheel zoom at or under this scale springs home instead.</summary>
    public const float ZoomFloor = 1.02f;

    /// <summary>The scale under which an unzoom has settled.</summary>
    public const float ZoomRest = 1.001f;

    /// <summary>A drag past this many pixels (|dx| + |dy|) is a pan, not a click.</summary>
    public const float DragSlop = 4f;

    /// <summary>The pan / zoom spring: its response and damping.</summary>
    public const float SpringResponse = 0.32f, SpringDamping = 0.9f;

    // ── the filmstrip ──
    /// <summary>A thumb's edge and the strip's padding.</summary>
    public const float Thumb = 56f, StripPad = 14f;

    /// <summary>A thumb's border and an inactive thumb's opacity.</summary>
    public const float ThumbBorder = 2f, ThumbIdleOpacity = 0.55f;

    /// <summary>A thumb's decode width.</summary>
    public const int ThumbDecode = 128;

    // ── the top chrome ──
    /// <summary>The ONE top chrome row's height, the title stack's gap, its title and its count sizes, and the title's weight.</summary>
    public const float ChromeH = 72f, TitleGap = 2f, TitleSize = 14f, CountSize = 12f;
    public const int TitleWeight = 600;

    // ── colour and motion ──
    /// <summary>The modal's black ground.</summary>
    public static ColorF Scrim => ColorF.FromRgba(0, 0, 0, 224);

    /// <summary>A pager page's ground, opaque black: the FlipView under the photo paints the theme's solid base (white in light mode), so the chrome
    /// and a letterboxed photo sat on white there instead of the viewer's black.</summary>
    public static ColorF PageGround => ColorF.FromRgba(0, 0, 0);

    /// <summary>The photo's placeholder is fully TRANSPARENT, so a still-decoding frame shows the black ground; so is an inactive thumb's border.</summary>
    public static ColorF Clear => ColorF.FromRgba(0, 0, 0, 0);

    /// <summary>The top chrome's scrim: black at 158 fading to nothing.</summary>
    public static readonly GradientSpec TopScrim = GradientDown(
        new GradientStop(0f, ColorF.FromRgba(0, 0, 0, 158)),
        new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 0)));

    /// <summary>The photo's reveal fade.</summary>
    public const float PhotoFadeMs = 140f;
}
