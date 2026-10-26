// ── Themes/Zune/Structure/Views/Stage/ZuneStageArtistName.cs — Zune's `Stage.ArtistName` (P5 Z4c) ───────────────────────────────────────
//
// The Artist layout's huge name (Z-NowPlaying-Artist): the display face at 300, tracking -0.02 em, the ink at 0.92, over the photo. There is
// no "now playing" eyebrow (the top bar already says it). The name is the artist whose photo shows (the first billed artist while the overview
// is pending); it keeps its own casing (names are never re-cased).
//
//   ARTIST form (a wide stage, `ZuneStageForm.Artist`): the board's. The name runs across the foot on one line, a third of the stage's height,
//            its line box cut at the strip's foot (the board cuts it at the transport's top) where it fades out, so the name sinks into the
//            foot as the board draws it, the strip's bars rising behind its fade; a name wider than the stage bleeds off its right edge under a fade over the gutter.
//            "about the artist" stands at the right gutter under the head: the Artist pane's heading, the bio's first three lines and its word
//            actions (the pane's own parts, in their brief form).
//   otherwise (the allocator's plate: a narrow or short stage): the name keeps the stage's margin and grows UPWARD from the identity row
//            (bottom-anchored 18 DIP above it, the core's `HeroNameBottomOffset`), a quarter of the stage's height (72 to 300), two lines in the
//            core's 0.7 of the stage.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
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
        var tiers = ctx.Tier();
        var a = ZuneStageKit.Arrangement(sc, in tiers);
        string name = props.Name();
        if (a.Form == ZuneStageForm.Artist) return Board(props, in a, name, ZuneStageTiers.StageOf(in tiers), L.UnitK);
        float size = HeroR.NameSize(L.H), pad = L.PadXFor(in k);
        return Stage.Layer with
        {
            HitTestVisible = false, HitTestPassThrough = false,
            Children =
            [
                new BoxEl
                {
                    Key = "stage:name:plate", Direction = 1, MaxWidth = L.HeroNameMaxW, HitTestVisible = false,
                    Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Transition = MotionTok.ControlNormal,
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(pad, 0f, 0f, L.HeroNameBottomOffset(in k)),
                    Children = [Name(name, size) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
                },
            ],
        };
    }

    /// <summary>The board's arrangement: the name across the foot and "about the artist" at the right gutter.</summary>
    static Element Board(StageArtistNameProps props, in ZuneStageArrangement a, string name, ZuneStageLayout tier, float unit)
    {
        var box = a.Name;
        float fade = MathF.Round(HeroR.BoardNameFadeEm * a.NameSize);
        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                // the line box overhangs the box's foot: the box cuts it at the strip's top and feathers the cut, and the right edge for a long name
                Key = "stage:name:board", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = box.W, Height = box.H,
                Margin = new Edges4(box.X, box.Y, 0f, 0f), Direction = 0, ClipToBounds = true, HitTestVisible = false, Layout = ZuneStageKit.Place,
                Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Transition = MotionTok.ControlNormal,
                EdgeFade = new EdgeFadeSpec(EdgeMask.Right | EdgeMask.Bottom, 0f, 0f, tier.Gutter, fade),
                Children = [Name(name, a.NameSize) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.None, Shrink = 0f, AlignSelf = FlexAlign.Start, LineStacking = LineStacking.BlockLineHeight }],
            },
        };
        if (props.Pane is { } pane)
            kids.Add(Embed.Comp(new ZuneStageArtistBrief.Props(pane, a.About, tier.TargetH, unit), static () => new ZuneStageArtistBrief())
                with { Key = "stage:about", Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Transition = MotionTok.ControlNormal });
        return Stage.Layer with { HitTestPassThrough = true, Children = kids.ToArray() };
    }

    static TextEl Name(string name, float size) => new(name)
    {
        Size = size, LineHeight = MathF.Round(size * HeroR.NameLineFrac), Weight = HeroR.NameWeight, FontFamily = R.Type.DisplayFace,
        CharSpacing = HeroR.NameTracking, Color = Ink.Ink with { A = HeroR.NameInkA },
    };
}

/// <summary>"about the artist" in the Artist layout (Z-NowPlaying-Artist): the Artist pane's heading, the bio's first lines and its word actions,
/// placed at the arrangement's rect. Asks for the artist's overview once per (artist, track, scope), as the pane does.</summary>
internal sealed class ZuneStageArtistBrief : Component
{
    public sealed record Props(StageArtistPaneProps Pane, ZuneRect At, float TargetH, float S);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var target = p.Pane.Target();                     // the track key, the scope, the billed artists
        int a = target.Artist, t = target.Track;
        UseEffect(() => p.Pane.Demand(a, t), DepKey.From(a, t, (int)target.Epoch, 0));
        if (!target.Valid) return Stage.Layer with { HitTestVisible = false };
        return Stage.Layer with
        {
            HitTestPassThrough = true,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Width = p.At.W, Margin = new Edges4(p.At.X, p.At.Y, 0f, 0f), Direction = 1, MinWidth = 0f,
                    Layout = ZuneStageKit.Place,
                    Children =
                    [
                        Embed.Comp(new ZuneStageArtistAbout.Props(p.Pane, a, p.At.W, p.S, HeroR.AboutLines), static () => new ZuneStageArtistAbout()),
                        Embed.Comp(new ZuneStageArtistActions.Props(p.Pane, a, p.TargetH, p.S), static () => new ZuneStageArtistActions()),
                    ],
                },
            ],
        };
    }
}
