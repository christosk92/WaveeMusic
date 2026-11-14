// ── Themes/Zune/Structure/Views/Stage/ZuneStageTheatreAbout.cs — Zune's "about the artist" under Theatre's title ──────────────────────────
//
// What YouTube puts under a video, in Zune's voice: "about the artist" as a display-face heading (light, lowercase through the heading case),
// then the artist row (the round portrait, the name over the monthly listeners, the Follow toggle) and the description (the track's facts, three
// lines of the bio, the "read more" word in the accent). The portrait, the name and "read more" open the stage's own Artist pivot (the stage stays
// full screen). Built from the core's `StageRootView.About` (what the Artist pane already asks), never a fetch of its own: a missing fact is left
// out, never a placeholder. No plate: it stands on the page like the title above it. Its height is reserved whole from the first frame
// (`ZuneStageVideoGeometry.AboutH`): the skeleton (`Skel.Region`, derived from the real block) and a track change (the content cross-fades) move
// nothing. It shows only where it fits whole, at rest, and moves with the title on the reservation's spring.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using G = Wavee.Themes.Zune.ZuneStageVideoGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Zune;

/// <summary>Zune's "about the artist" under Theatre's title.</summary>
internal sealed class ZuneStageTheatreAbout : Component
{
    public sealed record Props(StageRootView View);

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    static readonly Func<ToggleButton.Style, ToggleButton.Style> s_followOnStage =
        static st => st with { OnForeground = Ink.Ink, OnPressedForeground = Ink.InkSecondary, OffForeground = Ink.Ink, OffPressedForeground = Ink.InkSecondary };

    readonly Loadable<StageAboutFacts?> _load = Loadable<StageAboutFacts?>.Pending(null);
    Element? _region;
    StageRootView? _view;
    ZuneStageVideo _z = G.Standard;
    Func<ColorF>? _accent;

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        var t = ZuneStageTiers.StageOf(TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers);
        _view = v;
        if (!ReferenceEquals(_z, t.Video)) { _z = t.Video; _region = null; }
        _accent ??= () => _view is { } view ? view.Stage.Accent.Value.Fill : Tok.AccentDefault;
        var f = v.About();
        int artist = f?.Artist ?? 0, track = f?.Track ?? 0;
        UseEffect(() => { if (artist > 0 || track > 0) v.DemandAbout(artist, track); }, DepKey.From(artist, track));
        UseEffect(() => { if (f is null || f.Pending) _load.SetPending(); else _load.SetReady(f); }, DepKey.From(f?.GetHashCode() ?? 0));
        var r = G.Theatre(v.Stage.Layout.Value, t).About;
        _region ??= Skel.Region(_load, () => Block(null), facts => Block(facts), SkelReveal.FadeOnly, smoothResize: false);
        var z = _z;
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:about", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1,
                    Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, ClipToBounds = true, Layout = ZuneStageTheatre.Move,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = "picture:about:head", Height = z.Dx(G.AboutHeadLine), Shrink = 0f, Margin = new Edges4(0f, 0f, 0f, z.Px(G.AboutHeadGap)), HitTestVisible = false,
                            Children =
                            [
                                new TextEl(Loc.Get(Strings.Detail.AboutTheArtist))
                                {
                                    Size = z.Dx(G.AboutHeadSize), LineHeight = z.Dx(G.AboutHeadLine), Weight = G.TitleWeight, FontFamily = R.Type.DisplayFace,
                                    Color = Ink.Ink, Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap,
                                },
                            ],
                        },
                        new BoxEl { Grow = 1f, Direction = 1, MinHeight = 0f, MinWidth = 0f, Children = [_region] },
                    ],
                },
            ],
        };
    }

    /// <summary>The block for <paramref name="f"/> (null: the skeleton's source, the same geometry with empty fields), keyed by the track so a
    /// track change cross-fades it inside the reserved box.</summary>
    Element Block(StageAboutFacts? f)
    {
        var v = _view!;
        var z = _z;
        var open = v.OpenArtist;
        bool artist = f is null || f.Artist > 0;
        float image = z.Px(G.AboutImage);
        var kids = new List<Element>(3);
        if (artist)
        {
            var row = new List<Element>(3)
            {
                new BoxEl
                {
                    Width = image, Height = image, Shrink = 0f, Corners = Radii.Circle(image), ClipToBounds = true,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children = [Controls.Artwork(f?.ImageUrl, image, image, image / 2f, decodePx: (int)(image * 2f))],
                },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.Center, Height = z.Px(G.AboutRowH),
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children =
                    [
                        new TextEl(f?.Name ?? "") { Size = z.Px(G.AboutNameSize), LineHeight = z.Px(G.AboutNameLine), Weight = G.ArtistWeight, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                        new TextEl(f?.Listeners ?? "") { Size = z.Px(G.AboutMetaSize), LineHeight = z.Px(G.AboutMetaLine), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    ],
                },
            };
            if (f is { Uri.Length: > 0 } withUri)
                row.Add(Embed.Comp(() => new Controls.FollowToggle { Uri = withUri.Uri, Name = withUri.Name, Accent = _accent, Restyle = s_followOnStage })
                    with { Key = "picture:about:follow:" + withUri.Uri });
            kids.Add(new BoxEl { Height = z.Px(G.AboutRowH), Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = z.Px(G.AboutRowGap), Children = row.ToArray() });
        }
        var desc = new List<Element>(3);
        if (f is null || f.Facts.Length > 0)
            desc.Add(new TextEl(f?.Facts ?? "") { Size = z.Px(G.AboutBodySize), LineHeight = z.Px(G.AboutBodyLine), Weight = G.ArtistWeight, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (f is null || f.Bio.Length > 0)
        {
            desc.Add(new TextEl(f?.Bio ?? "") { Size = z.Px(G.AboutBodySize), LineHeight = z.Px(G.AboutBodyLine), Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = G.AboutBioLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            if (artist)
                desc.Add(new BoxEl
                {
                    Key = "picture:about:more", Height = z.Px(G.AboutMoreLine), Direction = 0, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Start, Shrink = 0f,
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = open,
                    Children = [new TextEl(Loc.Get(Strings.Stage.Artist.ReadMore)) { Size = z.Px(G.AboutBodySize), LineHeight = z.Px(G.AboutMoreLine), Weight = G.ArtistWeight, Color = Prop.Of(() => _accent!()), Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
                });
        }
        if (desc.Count > 0)
            kids.Add(new BoxEl { Direction = 1, Gap = z.Px(G.AboutLineGap), MinWidth = 0f, Margin = new Edges4(0f, artist ? z.Px(G.AboutDescGap) : 0f, 0f, 0f), Children = desc.ToArray() });
        return new BoxEl
        {
            ZStack = true, MinWidth = 0f, Grow = 1f,
            Children =
            [
                new BoxEl
                {
                    Key = "picture:about:" + (f?.Track ?? 0), AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Direction = 1, MinWidth = 0f,
                    Enter = s_fade, Exit = s_fade, Transition = MotionTok.ControlNormal, Children = kids.ToArray(),
                },
            ],
        };
    }
}
