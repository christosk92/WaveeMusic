// ── Themes/WinUi/Structure/Views/Player/WinUiMiniPlayer.cs — the mini player's look in WinUi (#197) ───────────────────
//
// The picture is the Common template's (Themes/Common/CommonMiniPlayer.cs); this is WinUi's MiniPlayerStyle: its window material, its
// ground, its type and its numbers (WinUiMiniRules).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using M = Wavee.Themes.WinUi.WinUiMiniRules;

namespace Wavee.Themes.WinUi;

internal static class WinUiMiniPlayer
{
    public static void Register(ViewRegistryBuilder views) => CommonMiniPlayer.Register(views, "winui.player.mini", Style);

    static TextEl Line(TextEl e) => e with { MaxLines = 1, Shrink = 1f, MinWidth = 0f };

    public static readonly MiniPlayerStyle Style = new()
    {
        MaterialLight = WindowMaterial.Acrylic,
        MaterialDark = WindowMaterial.Acrylic,
        Ground = static () => Tok.T.FillSolidBase with { A = M.GroundAlpha },
        ArtCorner = M.ArtCorner, ButtonBox = M.ButtonBox, ButtonGlyph = M.ButtonGlyph, PlayBox = M.PlayBox, PlayGlyph = M.PlayGlyph,
        ChromeBox = M.ChromeBox, ChromeGlyph = M.ChromeGlyph, ButtonCorner = M.ButtonCorner, PlayCorner = M.PlayBox * 0.5f,
        Pad = M.Pad, Gap = M.Gap, StripPadStart = M.StripPadStart, StripThumb = M.StripThumb, StripGap = M.StripGap,
        Hairline = M.Hairline, HairlineHot = M.HairlineHot, Badge = M.Badge,
        Title = static (text, shape) => Line(R.Type.TrackTitle(text)) with { Trim = TextTrim.CharacterEllipsis },
        Artist = static text => Line(R.Type.TrackMeta(text)) with { Color = Tok.T.TextSecondary, Trim = TextTrim.CharacterEllipsis },
        Detail = static text => Line(R.Type.TrackMeta(text)) with { Color = Tok.T.TextTertiary, Trim = TextTrim.CharacterEllipsis },
        FaceLabel = static text => Line(R.Type.TrackMeta(text)),
        Veil = static () => Tok.T.FillSmoke with { A = M.VeilAlpha },
        Shade = static () => Tok.T.FillSmoke with { A = M.ShadeAlpha },
        ChromePlate = static () => Tok.T.FillSolidBase,
        HoverInMs = M.HoverInMs,
        HoverOutDelayMs = M.HoverOutDelayMs,
    };
}
