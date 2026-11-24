// ── Entities/Mix.Badge.cs — #48 Mix, the "Mixed" badge on the playlist's cover (UX direction rev 2 "Playlist header") ────
//
// A small accent pill reading "Mixed" in the cover's upper-left corner while the playlist's Mix is on (and not listened to without
// it on this PC). The cover slot always wraps the art in this layer, badge or not, so turning Mix on or off never remounts the
// cover (its editor, its connected flight and its hand-over stay); the pill only shows and hides (opacity, no layout).

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
    public override Element Render()
    {
        var props = UseProps<MixCoverBadgeProps>();
        int slot = props.PlaylistSlot;
        bool on = UseComputed(() =>
        {
            _ = Entities.Current.Playlists.Changed.Value;   // the mix flags and a local "listen without" write bump the row
            var p = new Playlist(slot);
            return p.IsValid && p.MixOn && !MixListenWithout.Is(p);
        }).Value;
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
                    Opacity = on ? 1f : 0f, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 0, HitTestVisible = false,
                            Children =
                            [
                                new BoxEl
                                {
                                    Key = "mix:cover:badge", Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XXS, Spacing.S, Spacing.XXS), Corners = Radii.ControlAll,
                                    Fill = Tok.AccentDefault, HitTestVisible = false,
                                    Children = [Ui.Caption(Loc.Get(Strings.Mix.MixedLine)) with { Color = Tok.TextOnAccentPrimary, MaxLines = 1 }],
                                },
                                new BoxEl { Grow = 1f, HitTestVisible = false },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
