// ── Themes/Zune/Structure/Views/Stage/ZuneStageArtistName.cs — Zune's `Stage.ArtistName` (P5 Z4c) ───────────────────────────────────────
//
// The Artist layout's huge name (Z-NowPlaying-Artist): the display face at 300, tracking -0.02 em, the ink at 0.92, over the photo. There is
// no "now playing" eyebrow (the top bar already says it). The board bleeds the name 24 off the left edge and under the plate; here it keeps
// the stage's margin and grows UPWARD from the identity row (bottom-anchored 18 DIP above it, the core's `HeroNameBottomOffset`), so it never
// sits behind the identity's title: a size is a quarter of the stage's height (72 to 300), the width is the core's 0.7 of the stage, two lines.
// The name is the artist whose photo shows (the first billed artist while the overview is pending); it keeps its own casing (names are never
// re-cased).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using Ink = Wavee.Theming.Roles.StageInk;
using HeroR = Wavee.Themes.Zune.ZuneStageHeroRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Artist-layout name.</summary>
internal static class ZuneStageArtistName
{
    /// <summary>The registered template of <see cref="ViewKeys.StageArtistName"/>.</summary>
    public static Element Template(StageArtistNameProps props, in ViewContext ctx)
    {
        var sc = props.Stage;
        var L = sc.Layout.Value;
        var k = sc.Look.Value;
        string name = props.Name();
        float size = HeroR.NameSize(L.H), pad = L.PadXFor(in k);
        return Stage.Layer with
        {
            HitTestVisible = false, HitTestPassThrough = false,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MaxWidth = L.HeroNameMaxW, HitTestVisible = false,
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(pad, 0f, 0f, L.HeroNameBottomOffset(in k)),
                    Children =
                    [
                        new TextEl(name)
                        {
                            Size = size, LineHeight = MathF.Round(size * HeroR.NameLineFrac), Weight = HeroR.NameWeight, FontFamily = R.Type.DisplayFace,
                            CharSpacing = HeroR.NameTracking, Color = Ink.Ink with { A = HeroR.NameInkA },
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
            ],
        };
    }
}
