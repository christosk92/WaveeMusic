// ── Themes/Classic/Structure/Views/Stage/ClassicStageTheatreAbout.cs — Classic's about block under Theatre's title ───────────────────────
//
// What YouTube puts under a video, under Theatre's title block: the CHANNEL ROW (the primary artist's round portrait, the name in BodyStrong over the
// monthly listeners, the Follow toggle beside it) and the DESCRIPTION (the track's facts in BodyStrong: plays · year · album · tempo and key, then up
// to three lines of the artist's bio and "Read more"). The portrait, the name and "Read more" open the stage's own Artist tab (the one home of the
// whole story; the stage stays in full screen). Built from the core's `StageRootView.About` (what the Artist pane already asks), never a fetch of its
// own: a missing fact is left out, never a placeholder; no bio leaves the facts line alone; nothing at all leaves the block empty.
//
// No plate: the block stands on the page like the title above it, so the page's colour runs through it and a short bio leaves no empty box. Its height
// is reserved whole from the first frame (`ClassicStageTheatreGeometry.AboutH`): the skeleton (`Skel.Region`, derived from the real block) and a
// track change (the content cross-fades) move nothing. It shows only where it fits whole (`ClassicStageTheatreGeometry.Rects.AboutFits`), at rest
// (it covers no picture), and it moves with the title on the reservation's spring.

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

using G = Wavee.Themes.Classic.ClassicStageTheatreGeometry;
using Ink = Wavee.Theming.Roles.StageInk;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's about block under Theatre's title.</summary>
internal sealed class ClassicStageTheatreAbout : Component
{
    public sealed record Props(StageRootView View);

    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    /// <summary>Follow in the stage arm's ink (the artist pane's own restyle).</summary>
    static readonly Func<ToggleButton.Style, ToggleButton.Style> s_followOnStage =
        static st => st with { OnForeground = Ink.Ink, OnPressedForeground = Ink.InkSecondary, OffForeground = Ink.Ink, OffPressedForeground = Ink.InkSecondary };

    readonly Loadable<StageAboutFacts?> _load = Loadable<StageAboutFacts?>.Pending(null);
    Element? _region;
    StageRootView? _view;
    Func<ColorF>? _accent;

    public override Element Render()
    {
        var v = UseProps<Props>().View;
        _view = v;
        _accent ??= () => _view is { } view ? view.Stage.Accent.Value.Fill : Tok.AccentDefault;
        var f = v.About();
        int artist = f?.Artist ?? 0, track = f?.Track ?? 0;
        UseEffect(() => { if (artist > 0 || track > 0) v.DemandAbout(artist, track); }, DepKey.From(artist, track));
        UseEffect(() => { if (f is null || f.Pending) _load.SetPending(); else _load.SetReady(f); }, DepKey.From(f?.GetHashCode() ?? 0));
        var r = ClassicStageTheatreGeometry.Of(v.Stage.Layout.Value).About;
        _region ??= Skel.Region(_load, () => Block(null), facts => Block(facts), SkelReveal.FadeOnly, smoothResize: false);
        return Stage.Layer with
        {
            Children =
            [
                new BoxEl
                {
                    Key = "picture:about", AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Direction = 1,
                    Margin = new Edges4(r.X, r.Y, 0f, 0f), Width = r.W, Height = r.H, ClipToBounds = true, Layout = ClassicStageTheatre.Move,
                    Children = [_region],
                },
            ],
        };
    }

    /// <summary>The block for <paramref name="f"/> (null: the skeleton's source, the same geometry with empty fields). The content is keyed by the
    /// track, so a track change cross-fades it inside the reserved box.</summary>
    Element Block(StageAboutFacts? f)
    {
        var v = _view!;
        var open = v.OpenArtist;
        bool artist = f is null || f.Artist > 0;
        var kids = new List<Element>(4);
        if (artist)
        {
            var row = new List<Element>(3)
            {
                new BoxEl
                {
                    Width = G.ArtistImage, Height = G.ArtistImage, Shrink = 0f, Corners = Radii.Circle(G.ArtistImage), ClipToBounds = true,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children = [Controls.Artwork(f?.ImageUrl, G.ArtistImage, G.ArtistImage, G.ArtistImageRadius, decodePx: G.ArtistImageDecode)],
                },
                new BoxEl
                {
                    Grow = 1f, Shrink = 1f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.Center, Height = G.ArtistRowH,
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children =
                    [
                        Ui.BodyStrong(f?.Name ?? "") with { Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                        Ui.Caption(f?.Listeners ?? "") with { Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
                    ],
                },
            };
            if (f is { Uri.Length: > 0 } withUri)
                row.Add(Embed.Comp(() => new Controls.FollowToggle { Uri = withUri.Uri, Name = withUri.Name, Accent = _accent, Restyle = s_followOnStage })
                    with { Key = "picture:about:follow:" + withUri.Uri });
            kids.Add(new BoxEl { Height = G.ArtistRowH, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Gap = G.ArtistRowGap, Children = row.ToArray() });
        }
        var desc = new List<Element>(3);
        if (f is null || f.Facts.Length > 0)
            desc.Add(Ui.BodyStrong(f?.Facts ?? "") with { Color = Ink.Ink, LineHeight = G.FactsLine, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (f is null || f.Bio.Length > 0)
        {
            desc.Add(Ui.Body(f?.Bio ?? "") with { Color = Ink.InkSecondary, LineHeight = G.BioLine, MaxLines = G.BioLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            // "Read more" stands in the text column: the link's label, not its hover plate, lines up with the bio (the plate reaches out
            // past the column by the link's own padding, as an inline link's does)
            if (artist)
                desc.Add(new BoxEl
                {
                    Direction = 0, Margin = new Edges4(-ControlMetrics.For(ControlSize.Small).Padding.Left, 0f, 0f, 0f),
                    Children = [HyperlinkButton.Create(Loc.Get(Strings.Stage.Artist.ReadMore), open, size: ControlSize.Small)],
                });
        }
        if (desc.Count > 0)
            kids.Add(new BoxEl { Direction = 1, Gap = G.DescLineGap, MinWidth = 0f, Margin = new Edges4(0f, artist ? G.DescGap : 0f, 0f, 0f), Children = desc.ToArray() });
        // a ZStack, so the outgoing track's block fades out UNDER the incoming one in the same place (never stacked under it)
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
