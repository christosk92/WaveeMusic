// ── Themes/Classic/Structure/Views/Player/ClassicMiniPlayer.cs — the mini player's look in Classic (#197) ───────────────────
//
// The picture is the Common template's (Themes/Common/CommonMiniPlayer.cs); this is Classic's MiniPlayerStyle: its window material, its
// ground, its type and its numbers (ClassicMiniRules).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using M = Wavee.Themes.Classic.ClassicMiniRules;

namespace Wavee.Themes.Classic;

internal static class ClassicMiniPlayer
{
    public static void Register(ViewRegistryBuilder views) => CommonMiniPlayer.Register(views, "classic.player.mini", Style);

    static TextEl Line(TextEl e) => e with { MaxLines = 1, Shrink = 1f, MinWidth = 0f };

    public static readonly MiniPlayerStyle Style = new()
    {
        MaterialLight = WindowMaterial.None,
        MaterialDark = WindowMaterial.None,
        Ground = static () => Tok.T.WindowBackground,
        ArtCorner = M.ArtCorner, ButtonBox = M.ButtonBox, ButtonGlyph = M.ButtonGlyph, PlayBox = M.PlayBox, PlayGlyph = M.PlayGlyph,
        ChromeBox = M.ChromeBox, ChromeGlyph = M.ChromeGlyph, ButtonCorner = M.ButtonCorner, PlayCorner = M.PlayBox * 0.5f,
        Pad = M.Pad, Gap = M.Gap, StripPadStart = M.StripPadStart, StripThumb = M.StripThumb, StripGap = M.StripGap,
        Hairline = M.Hairline, HairlineHot = M.HairlineHot, Badge = M.Badge,
        Title = static (text, shape) => Line(R.Type.TrackTitle(text)) with { Trim = TextTrim.CharacterEllipsis },
        Artist = static text => Line(R.Type.TrackMeta(text)) with { Color = Tok.T.AccentTextPrimary, Trim = TextTrim.CharacterEllipsis },
        Detail = static text => Line(R.Type.TrackMeta(text)) with { Color = Tok.T.TextTertiary, Trim = TextTrim.CharacterEllipsis },
        FaceLabel = static text => Line(R.Type.TrackMeta(text)),
        Veil = static () => Tok.T.FillSmoke with { A = M.VeilAlpha },
        Shade = static () => Tok.T.FillSmoke with { A = M.ShadeAlpha },
        ChromePlate = static () => Tok.T.FillSolidBase,
        HoverInMs = M.HoverInMs,
        HoverOutDelayMs = M.HoverOutDelayMs,
    };
}
