// ── Entities/Mix.Badge.cs — #48 Mix, the "Mixed" badge on the playlist's cover (UX direction rev 2 "Playlist header") ────
//
// A small accent pill reading "Mixed" in the cover's upper-left corner while the playlist's Mix is on (and not listened to without
// it on this PC). The cover slot always wraps the art in this layer, badge or not, so turning Mix on or off never remounts the
// cover (its editor, its connected flight and its hand-over stay). The pill comes and goes with Mix (owner 2026-11-26: "now it just
// appears"): it grows out of the cover's corner and fades in, and shrinks back into it on the way out. A page that opens on a mixed
// playlist shows it at rest (it arrives with the cover).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The cover's Mix layer props: the playlist and the cover element it decorates.</summary>
internal sealed record MixCoverBadgeProps(int PlaylistSlot, Element Cover);

internal sealed class MixCoverBadge : Component
{
    /// <summary>The badge's arrival and departure: out of the cover's top-left corner, a small scale and a fade.</summary>
    static readonly LayoutTransition s_motion = new(TransitionChannels.Opacity, TransitionDynamics.Tween(Design.Motion.Standard, Easing.FluentDecelerate),
        Enter: new EnterExit(Sx: BadgeScale, Sy: BadgeScale, Opacity: 0f, Active: true), Exit: new EnterExit(Sx: BadgeScale, Sy: BadgeScale, Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentAccelerate));
    /// <summary>The scale the pill grows from (and back to).</summary>
    const float BadgeScale = 0.6f;
    bool _rendered;

    public override Element Render()
    {
        var props = UseProps<MixCoverBadgeProps>();
        int slot = props.PlaylistSlot;
        bool on = UseComputed(() =>
        {
            _ = Entities.Current.Playlists.Changed.Value;   // the mix flags and a local "listen without" write bump the row
            var p = new Playlist(slot);
            return p.IsValid && p.MixOn && !MixOptOut.Is(p);
        }).Value;
        // The first render is the page's: the pill arrives with the cover, at rest. Later flips animate.
        bool animate = _rendered && !Design.Reduced;
        _rendered = true;
        return new BoxEl
        {
            Key = "mix:cover", ZStack = true, Shrink = 0f,
            Children =
            [
                props.Cover,
                // An unaligned layer fills the cover (an aligned one hugs and lands oddly); inside it a column puts a row at the top (the bottom
                // edge is the cover editor's "change cover" band in Zune) and the row's spacer leaves the pill hugging its word at the left.
                new BoxEl
                {
                    Key = "mix:cover:layer", Direction = 1, Justify = FlexJustify.Start, Padding = new Edges4(Spacing.S, Spacing.S, 0f, 0f),
                    HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, HitTestVisible = false,
                            Children = on ? [Badge(animate), new BoxEl { Grow = 1f, HitTestVisible = false }] : [new BoxEl { Grow = 1f, HitTestVisible = false }],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The pill, keyed so Mix turning off plays its exit (the node that leaves carries it).</summary>
    static Element Badge(bool animate) => new BoxEl
    {
        Key = "mix:cover:badge", Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.ControlAll,
        Fill = Tok.AccentDefault, HitTestVisible = false, TransformOriginX = 0f, TransformOriginY = 0f,
        Animate = animate ? s_motion : s_motion with { Enter = default },
        Children = [Ui.Caption(Loc.Get(Strings.Mix.MixedLine)) with { Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
    };
}
