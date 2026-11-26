// ── Themes/WinUi/Style/WinUiGrounds.cs — WinUI's grounds for one mode, the board's or the user's (#184) ─────────────────────
//
// WinUI's frame is the bare window base (Mica, or the solid base under the None backdrop) with ONE content layer over it, and a few
// opaque plates (a concert tile, its hero, an empty cover). Those colours were statics (`WinUiTokens`) and literals in the role table; the
// definition's token sets, the role table and the stage read this record instead: the board's values by default (`DefaultDark` /
// `DefaultLight`, today's colours bit for bit) or a user's colour set (`From`). A colour set is opaque by design (plan D5): its mode draws on
// no material, so its base is the set's well (the chrome bands, as Catppuccin's mantle), the content layer is the set's ground, opaque, and
// the tab strip is the deep band.

using FluentGpu.Foundation;
using Wavee.Theming.Colours;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's grounds for one mode (see the file header).</summary>
/// <param name="Base">The window base: the title bar, the pane and the player stand on it (the solid colour under the None backdrop).</param>
/// <param name="BaseAlt">The Mica Alt tone: the tab strip.</param>
/// <param name="Content">The content layer as it composites over the base: the opaque stand-in a floating pane or a flattened gate uses.</param>
/// <param name="Layer">The content layer as it paints (translucent over Mica on the board's ladder; the opaque ground for a set).</param>
/// <param name="ContentLayer">The content layer as a wash over the base (the ground rung a page reads).</param>
/// <param name="ContentAlt">The alternate content band.</param>
/// <param name="TextTertiary">Text 3.</param>
/// <param name="WashInk">The opaque colour every row and chrome wash is made of (white on dark, black on light on the board).</param>
/// <param name="EventPlate">A concert tile: a card on the content layer.</param>
/// <param name="EventHeroPlate">A concert's hero plate.</param>
/// <param name="ArtPlaceholder">An empty cover.</param>
public sealed record WinUiGrounds(
    ColorF Base, ColorF BaseAlt, ColorF Content, ColorF Layer, ColorF ContentLayer, ColorF ContentAlt, ColorF TextTertiary, ColorF WashInk,
    ColorF EventPlate, ColorF EventHeroPlate, ColorF ArtPlaceholder)
{
    /// <summary>The board's dark grounds (W-Tokens).</summary>
    public static WinUiGrounds DefaultDark { get; } = new(
        Base: WinUiTokens.DarkBase, BaseAlt: WinUiTokens.DarkBaseAlt, Content: WinUiTokens.DarkContent,
        Layer: ColorF.FromRgba(0x3A, 0x3A, 0x3A, 0x4C),
        ContentLayer: ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = WinUiTokens.ContentLayerDarkA },
        ContentAlt: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x08),
        TextTertiary: WinUiTokens.DarkTextTertiary, WashInk: ColorF.FromRgba(0xFF, 0xFF, 0xFF),
        EventPlate: ColorF.FromRgba(0x33, 0x33, 0x33), EventHeroPlate: ColorF.FromRgba(0x2E, 0x2E, 0x2E), ArtPlaceholder: ColorF.FromRgba(0x2A, 0x2A, 0x2A));

    /// <summary>The board's light grounds (W-Tokens).</summary>
    public static WinUiGrounds DefaultLight { get; } = new(
        Base: WinUiTokens.LightBase, BaseAlt: WinUiTokens.LightBaseAlt, Content: WinUiTokens.LightContent,
        Layer: ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0x80),
        ContentLayer: ColorF.FromRgba(0xFF, 0xFF, 0xFF) with { A = WinUiTokens.ContentLayerLightA },
        ContentAlt: ColorF.FromRgba(0xF6, 0xF6, 0xF6, 0x80),
        TextTertiary: WinUiTokens.LightTextTertiary, WashInk: ColorF.FromRgba(0x00, 0x00, 0x00),
        EventPlate: ColorF.FromRgba(0xFB, 0xFB, 0xFB), EventHeroPlate: ColorF.FromRgba(0xF6, 0xF6, 0xF6), ArtPlaceholder: ColorF.FromRgba(0xF2, 0xF2, 0xF2));

    /// <summary>A user's colour set as WinUI's grounds: the well is the base, the deep band the tab strip, the ground the content layer (opaque).</summary>
    public static WinUiGrounds From(ColourLadder l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return new WinUiGrounds(
            Base: l.Well, BaseAlt: l.Deep, Content: l.Ground, Layer: l.Ground, ContentLayer: l.Ground, ContentAlt: l.Panel,
            TextTertiary: l.InkTertiary, WashInk: l.WashInk,
            EventPlate: l.Raised, EventHeroPlate: l.Panel, ArtPlaceholder: l.Placeholder);
    }

    /// <summary>The grounds of <paramref name="l"/>, or the board's for its mode when there is none.</summary>
    public static WinUiGrounds Of(ColourLadder? l, bool dark) => l is null ? (dark ? DefaultDark : DefaultLight) : From(l);

    /// <summary>A wash of the ink at <paramref name="alpha"/>.</summary>
    public ColorF Wash(float alpha) => WashInk with { A = alpha };
}
