// ── Themes/Common/Rules/CommonMiniPlayerRules.cs — the mini player's fixed geometry and keys (#197) ─────────────────────
//
// Theme numbers (button boxes, radii, paddings, type) live in each theme's MiniPlayerStyle; these are the shape-independent
// constants of the Common template: node keys, the face-flip slide, the strip overlay's fade run, the wide card's text gaps, the
// options flyout's width.

namespace Wavee.Themes.Common;

internal static class CommonMiniPlayerRules
{
    public const string RootKey = "mini.root", FaceKey = "mini.face", ChromeKey = "mini.chrome", TransportKey = "mini.transport",
        SeekKey = "mini.seek", HoverKey = "mini.hover", RestKey = "mini.rest", MetaKey = "mini.meta", PlayKey = "mini.play",
        SaveKey = "mini.save", DeviceKey = "mini.device", OptionsKey = "mini.options", BackKey = "mini.back";

    /// <summary>The face flip slides the new face in from this fraction of its box.</summary>
    public const float FlipSlide = 0.18f;
    /// <summary>The strip's hover controls fade in over the end of the row: the fade's length (DIP).</summary>
    public const float StripFade = 44f;
    /// <summary>Text line gaps (wide, tall) and the seek row's gap.</summary>
    public const float LineGap = 1f, SeekGap = 8f, TimeMinW = 32f;
    /// <summary>The options flyout's width and its row height (a 44-DIP target).</summary>
    public const float OptionsW = 284f, OptionsRow = 44f, OptionsPad = 6f;
    /// <summary>The badge's glyph is this fraction of the badge.</summary>
    public const float BadgeGlyph = 0.56f;
    /// <summary>The tall layout's art takes at most this fraction of the window height.</summary>
    public const float TallArtShare = 0.56f;
    /// <summary>The square's resting shade starts at this fraction of the height.</summary>
    public const float ShadeFrom = 0.45f, ShadeAngle = 90f;
    /// <summary>Text over a filling face (cover, canvas, visualizer) is on-media ink: white, the secondary line at this alpha.</summary>
    public static readonly FluentGpu.Foundation.ColorF OnMediaInk = new(1f, 1f, 1f, 1f);
    public const float OnMediaSecondaryAlpha = 0.8f;
    /// <summary>A disabled control is its ink at this alpha.</summary>
    public const float DisabledAlpha = 0.4f;
    /// <summary>The tall immersive layout keeps this much (DIP) at the bottom for its panel.</summary>
    public const float TallPanel = 186f;
    /// <summary>The square keeps this much (DIP) under its lyrics window for the resting title row.</summary>
    public const float SquareRest = 64f;
}
