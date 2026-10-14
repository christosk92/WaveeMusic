// ── Themes/Classic/Structure/Views/Video/ClassicVideoPoster.cs — Classic's `Video.JoinPoster` (themes plan 6.1, T7c) ─────────────
//
// From `Shell/Video.UI.cs` (`Poster`, `LivePoster`, `PosterGround`, `JoinNotice`, `WorkingLine`, `FailedLine`): the "no picture yet"
// composition every video surface shares (the docked card, the mini player, the pop-out and fullscreen, and the engine element's own
// `PosterContent`). The core decides WHAT is shown (`Joining.Decide`, folded once into `Video.JoinNow` from the host's events), never by a
// timer guess here; this template draws it.
//
// A no-player state is the current track's artwork at 0.4 over the letterbox, never a black rectangle. The ground is the SAME keyed
// `LivePoster` in every state, so the notice appearing or the fault replacing it never re-fades the artwork underneath and never reflows the
// card. Inside the join budget there is no notice at all (not a hidden one), so a switch that lands fast flashes nothing and the ring never
// animates unseen. Past the budget: the 20-DIP ring and "Loading…". On a failure: a warning glyph and the fault's own line, and NO ring,
// because "not coming" must not draw the same picture as "still coming". The notice fades in (an opacity channel is legal here and ONLY here:
// a poster is mounted exactly while no player is bound, so there is no video hole below it for an ancestor opacity to wash out) and its key
// makes Working -> Failed a real swap, so the new notice plays its own entrance. The ink is the on-media ladder.
//
// NEVER `Controls.ArtworkFill` as the ground: that helper is "a square cover that fills the width its layout hands it", and a ZStack measures
// to its largest child, so hosting it as the element's `PosterContent` made the video AREA width x width and every downstream fit went wrong
// (the bottom-justified transport laid out below the visible edge and was clipped away). A NaN aspect is the engine's "no derived height:
// fill the box your layout gives you".

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's video poster and join notice.</summary>
internal static class ClassicVideoPoster
{
    /// <summary>The registered template of <see cref="ViewKeys.VideoJoinPoster"/>.</summary>
    public static Element Template(VideoJoinPosterProps props, in ViewContext ctx) => new BoxEl
    {
        Grow = 1f, MinHeight = 0f, ClipToBounds = true, ZStack = true, Fill = Tok.MediaLetterbox,
        Visible = props.Shown,
        Children = props.Visual is Video.JoinVisual.Working or Video.JoinVisual.Failed
            ? [LivePoster.Make(props.ArtUrl), Notice(props)]
            : [LivePoster.Make(props.ArtUrl)],
    };

    /// <summary>The poster's GROUND alone: dimmed artwork FILLING the video box and imposing no shape of its own. The watch page draws this
    /// byte-identical layer under its stage so the idle -> live hand-over is ONE cross-fade. A track with no art is the bare letterbox.</summary>
    internal static Element Ground(string? url) => new BoxEl
    {
        Grow = 1f, Opacity = ClassicVideoRules.PosterOpacity, ClipToBounds = true,
        Children = url is { Length: > 0 }
            ? [Ui.Image(url, ImageFit.Cover, aspect: float.NaN, decodePx: ClassicVideoRules.PosterDecodePx, corners: 0f, placeholder: (ColorF?)null)
                   with { Placeholder = Design.WatchedPlaceholder(url) }]
            : [],
    };

    /// <summary>The CURRENT track's art, read inside its OWN render, so a stage whose key survives a source switch never shows the first
    /// track's art forever (<c>PosterContent</c> freezes at the element's mount).</summary>
    sealed class LivePoster : Component
    {
        public override Element Render() => new BoxEl
        {
            Grow = 1f, MinHeight = 0f, ClipToBounds = true, Fill = Tok.MediaLetterbox,
            Children = [Ground(UseProps<Func<string?>>()())],
        };

        internal static Element Make(Func<string?> artUrl) => Embed.Comp(artUrl, static () => new LivePoster()) with { Key = "live-poster" };
    }

    /// <summary>What sits over the poster once the host has something to say.</summary>
    static Element Notice(VideoJoinPosterProps props)
    {
        bool failed = props.Visual == Video.JoinVisual.Failed;
        var stack = new BoxEl
        {
            Key = failed ? "join:failed" : "join:working",
            Direction = 1, AlignItems = FlexAlign.Center, Gap = Spacing.S, HitTestVisible = false,
            // Reduced motion is the DEFAULT terminal: a hard cut, read as a VALUE, never a branch at the call site.
            Enter = Design.Reduced ? default : new EnterExit(Opacity: 0f, Active: true),
            Children = failed ? FailedLine(props) : WorkingLine(),
        };
        return new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestPassThrough = true,
            Children = [stack],
        };
    }

    /// <summary>"Still coming": the 20-DIP indeterminate ring and "Loading…", both in ON-MEDIA ink.</summary>
    static Element[] WorkingLine() =>
    [
        ProgressRing.Indeterminate(size: ClassicVideoRules.NoticeRing, foreground: Tok.OnMediaPrimary),
        new TextEl(Loc.Get(Strings.Player.Loading))
        {
            Size = ClassicVideoRules.NoticeText, Weight = 600, Color = Tok.OnMediaSecondary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        },
    ];

    /// <summary>"Not coming": a static warning glyph and the fault's OWN line, with no ring (a ring means wait). The line is bound
    /// reactively: the reducer types the fault, and it can land after this notice mounted.</summary>
    static Element[] FailedLine(VideoJoinPosterProps props) =>
    [
        new TextEl(Icons.StatusWarning) { Size = ClassicVideoRules.NoticeGlyph, FontFamily = Tok.Definition.Fonts.Icon, Color = Tok.OnMediaPrimary },
        new TextEl(Prop.Of(props.FaultTitle))
        {
            Size = ClassicVideoRules.NoticeText, Weight = 600, Color = Tok.OnMediaPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        },
    ];
}
