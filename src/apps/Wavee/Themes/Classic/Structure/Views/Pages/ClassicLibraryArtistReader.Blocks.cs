// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryArtistReader.Blocks.cs — the reader's block faces (themes plan 6.1, T10c1) ───────────────────────────
//
// From `Entities\Artist.Reader.cs` (`BlockCell.Build` / `Head` / `TitleSlot` / `ShimmerHead`): one block — cover + "2022 · Album" caption | head (title link · meta · ▶ ♥ ⋯) + rows — in
// its REAL face and its COUNTED skeleton, which is the same geometry with placeholder boxes and `ReaderBlock.Rows` placeholder rows, so `ReaderShape.ExtentOf` stays exact across the reveal.
// The cell (`IArtistReaderBlock`) is the core's: its readiness `Loadable`, its record, its liked rows, its verbs. The view builds the finished region ONCE per cell and caches it on the cell
// (`Face`), so a re-realize allocates nothing.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

internal static partial class ClassicLibraryArtistReader
{
    /// <summary>The block body for one cell: its CACHED region element, so a re-realize allocates nothing at all. The region cross-dissolves the counted skeleton into the real block as the cell's
    /// <c>Load</c> flips (a plain opacity fade — `SkelReveal.FadeOnly`; the blur-rise `Soft` stacked on a remount storm read as flicker), and re-diffs its content in place when the cell's version
    /// moves. `smoothResize` is OFF: the region sits inside a MEASURED virtualized item whose extent the shape already states exactly, and a Reflow size track there clips the region and fights
    /// the table.</summary>
    static Element BlockBody(IArtistReaderBlock cell)
        => cell.Face ??= Skel.Region(cell.Load, () => Build(cell, real: false), _ => Build(cell, real: true), SkelReveal.FadeOnly, smoothResize: false)
            with { Key = "blk:" + cell.AlbumSlot.ToString(CultureInfo.InvariantCulture) };

    /// <summary>One block: cover + "2022 · Album" caption | head (title link · meta · ▶ ♥ ⋯) + rows. <paramref name="real"/> false is the COUNTED skeleton — the same geometry with placeholder boxes and
    /// <see cref="Artist.ReaderBlock.Rows"/> placeholder rows, which is what keeps <see cref="Artist.ReaderShape.ExtentOf"/> exact across the reveal. A failed tracklist replaces the rows with the Error
    /// voice's own Retry strip.</summary>
    static Element Build(IArtistReaderBlock cell, bool real)
    {
        var b = cell.Block;
        int slot = cell.AlbumSlot;
        var a = new Album(slot);
        bool narrow = cell.Narrow;                                // the reader's arm now (a flip rebuilds the block in place: the cell's face follows it)
        float cover = Artist.ReaderShape.CoverOf(narrow);
        bool valid = a.IsValid;
        Action go = cell.Open;
        string uri = valid ? a.Uri.Text : "";
        string title = valid && a.Knows(AlbumFields.Title) ? a.Title : "";
        string caption = (valid && a.Knows(AlbumFields.Year) && a.Year > 0
                            ? a.Year.ToString(CultureInfo.InvariantCulture) + " · " : "")
                       + Detail.Text.KindLabel(valid && a.Knows(AlbumFields.Kind) ? a.Kind : AlbumKind.Album);

        Element rows;
        if (real && b.Failed)
        {
            rows = Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact,
                title: Loc.Get(Strings.Library.SongsFailed),
                onAction: cell.Retry);
        }
        else
        {
            // A LIKED-ONLY block lists the liked rows and nothing else — you did not save this record, so the rest of it is not yours to show. The row NUMBER is the position in that list, not the
            // album's track number; the play action is still the track in the ALBUM's context, which is what makes the rest of the record reachable from the queue.
            var tracks = b.LikedOnly ? cell.LikedRows() : valid ? a.TrackSlots : default;
            int n = real ? tracks.Length : Math.Max(1, b.Rows);
            var kids = new Element[n];
            for (int r = 0; r < n; r++)
            {
                if (!real)
                {
                    kids[r] = ClassicTrackChrome.Placeholder(in ClassicLibraryReaderRules.Cols, ClassicLibraryReaderRules.Tracks, Artist.ReaderShape.RowH, 0f)
                        with { Key = "sh:" + r.ToString(CultureInfo.InvariantCulture) };
                    continue;
                }
                int trackSlot = tracks[r];
                // ZERO-based: `Track.NumberCell` prints `displayIndex + 1`. Passing r + 1 here numbered every block from 2.
                kids[r] = Track.EagerRow(new Track(trackSlot), r, in ClassicLibraryReaderRules.Cols, ClassicLibraryReaderRules.Tracks, Artist.ReaderShape.RowH,
                    () => cell.PlayTrack(trackSlot),
                    in ClassicLibraryReaderRules.RowOptions)
                    with { Key = "t:" + trackSlot.ToString(CultureInfo.InvariantCulture) };
            }
            rows = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Artist.ReaderShape.RowsPadBottom),
                Children = kids,
            };
        }

        // A plain stretched column: on the BOUND path the block hangs under the scroller's content node (through the slot's layout-transparent anchors), and that is a column with
        // `AlignItems.Stretch` — the full width comes for free. A `Grow 1 / Basis 0` on a column would aim at the HEIGHT and zero the block.
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    // `ReaderShape`'s own fields, so `ExtentOf` describes THIS row by identity.
                    Direction = 0, Gap = Artist.ReaderShape.BlockCoverGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                    Padding = new Edges4(Artist.ReaderShape.BlockPadLeft, Artist.ReaderShape.BlockPadTop, Artist.ReaderShape.BlockPadRight, Artist.ReaderShape.BlockPadBottom),
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = Artist.ReaderShape.CoverCaptionGap, Shrink = 0f, Width = cover,
                            // STICKY COVER (prototype `.block .side{position:sticky; top:64px}`): the art and its caption ride down the block while its tracks scroll past, and stop at the block's own
                            // bottom edge. The sticky clamps to the containing block — here the row above (its immediate parent), which is `AlignItems.Start` so this column keeps its content height and
                            // the clamp has real room. The inset is the pinned rail plus the block's top pad, so the cover comes to rest exactly where an unscrolled block draws it. The column carries no
                            // static `Transform`: a transform-owning scroll effect and a static matrix on one node is a hard reconciler error, not a silent fight. It is declared on BOTH faces because
                            // `Build` is one function, and the SHIMMER face harmlessly loses it: `SkeletonDeriver`'s container arm rewrites a box with children as `ScrollEffects = []`.
                            ScrollEffects = [new ScrollEffectSpec(ScrollEffect.Sticky(Artist.ReaderShape.SubRailH + Artist.ReaderShape.BlockPadTop))],
                            Children =
                            [
                                new BoxEl
                                {
                                    Width = cover, Height = cover, Corners = Radii.CardAll, ClipToBounds = true,
                                    Shadow = Elevation.Card, Cursor = valid ? CursorId.Hand : CursorId.Arrow,
                                    Role = valid ? AutomationRole.Button : AutomationRole.None,
                                    OnClick = valid ? go : null,
                                    Children = [Controls.Artwork(valid ? Controls.ArtUrl(a.ImageId) : null,
                                                                 cover, cover, Radii.Card, decodePx: 256)],
                                },
                                Ui.Caption(caption) with
                                {
                                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                    LineHeight = Artist.ReaderShape.CaptionLine,
                                },
                            ],
                        },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children = [real ? Head(cell, uri, title, go, narrow) : ShimmerHead(cell, narrow), rows],
                        },
                    ],
                },
                new BoxEl
                {
                    Height = Artist.ReaderShape.Divider, MinWidth = 0f, Shrink = 0f, Fill = Tok.StrokeDividerDefault,
                    Margin = new Edges4(ClassicTiers.PaneInset, 0f, ClassicTiers.PaneInset, 0f),
                },
            ],
        };
    }

    /// <summary>The album head, in its arm (#158). INLINE (≥ 640): one row — the title slot is the row's flexible filler, floored at <see cref="Artist.ReaderShape.TitleFloorW"/>, then the meta beside the three
    /// circles; the meta shrinks before the title would go under its floor, and the circles never do. STACKED (narrow): the title on a row of its own, over the meta and the circles right-aligned
    /// beneath it, so the title gets the whole body — the inline row left it 3 DIP at a 391-DIP reader ("P…") and overflowed outright at 360, clipping the ⋯ off the pane. The meta sits beside the
    /// circles in BOTH arms: the title is the flexible filler, which is what lets its tooltip wrapper live in a slot the flex algorithm sizes (<see cref="TitleSlot"/>).
    /// <para>Both roots DECLARE <see cref="Artist.ReaderShape.HeadHeight"/> as their <c>Height</c> — the number <see cref="Artist.ReaderShape.ExtentOf"/> adds — not as a <c>MinHeight</c>: a locale whose
    /// metrics overrun keeps the stated box, it never reflows the block. They do NOT clip: the title link's hover plate overhangs the root by its −4 margin and its focus ring outsets past the stacked
    /// root's top edge, and with stated heights and a width budget that cannot overflow (the head tests pin it) a clip would only shear those.</para></summary>
    static Element Head(IArtistReaderBlock cell, string uri, string title, Action go, bool narrow)
    {
        Element metaEl = Ui.Caption(cell.Meta()) with
        {
            Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        Element play = Controls.Named(Controls.PlayFab(cell.PlayAlbum, Icons.Play, Artist.ReaderShape.HeadCircle),
                                      Strings.Library.PlayAlbum(title));
        Element save = Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = Artist.ReaderShape.HeadGlyph, Box = Artist.ReaderShape.HeadCircle })
                       with { Key = "save:" + uri };
        Element more = ClassicDetailKit.MoreButton(cell.Menu, Artist.ReaderShape.HeadCircle);
        Element titleSlot = TitleSlot(title, go, narrow);
        // ONE tree in both arms (audit FU2: the reader re-arms in place, the narrow arm is no list mount input): the title slot and the verbs row are the head's two
        // children in either arm, the head a row inline and a column stacked, so a flip moves the verbs instead of re-parenting them (a re-parent rebuilt the
        // save, play and "…" circles on the 1008 crossing). Inline: the title fills (floor) → the meta shrinks → the circles never. Stacked: the title row stated
        // tall and stretched to the body, the verbs at the end (`Justify End`, not a spacer: a spacer would charge the meta one more gap).
        Element verbs = new BoxEl
        {
            Direction = 0, Gap = Artist.ReaderShape.HeadGap, AlignItems = FlexAlign.Center, Justify = narrow ? FlexJustify.End : FlexJustify.Start,
            Shrink = narrow ? 0f : 1f, MinWidth = 0f, Height = Artist.ReaderShape.HeadCircle,
            Margin = narrow ? new Edges4(0f, Artist.ReaderShape.HeadStackGap, 0f, Artist.ReaderShape.HeadStackPadBottom) : default,
            Children = [metaEl, play, save, more],
        };
        return new BoxEl
        {
            Direction = narrow ? (byte)1 : (byte)0, Gap = narrow ? 0f : Artist.ReaderShape.HeadGap, AlignItems = narrow ? FlexAlign.Stretch : FlexAlign.Center,
            MinWidth = 0f, Height = narrow ? Artist.ReaderShape.HeadStackedH : Artist.ReaderShape.HeadH, OnBoundsChanged = cell.OnHeadBounds,
            Children = [titleSlot, verbs],
        };
    }

    /// <summary>The title link inside its tooltip, inside a slot the FLEX algorithm sizes: inline it is the head row's `Grow 1 / Basis 0` filler, floored at <see cref="Artist.ReaderShape.TitleFloorW"/>; stacked it is
    /// a column child of the stated row height, cross-stretched to the body. In both, the ToolTip wrapper is a COLUMN child — stretched to the slot's width; a wrapper is `Shrink 0` and can never be a
    /// shrinking ROW child (the engine's rule 11) — and the link inside it is `Shrink 1 / MinWidth 0`, so a short title keeps a content-hugging hover plate, a long one ellipsizes, and only a trimmed one
    /// gets a tooltip (<c>Controls.TrimTip</c>).</summary>
    static Element TitleSlot(string title, Action go, bool narrow)
    {
        TextEl text = new TextEl(title)
        {
            Size = ClassicLibraryReaderRules.HeadTitleSize, LineHeight = Artist.ReaderShape.HeadTitleLine, Weight = 600, Color = Tok.TextPrimary,
            HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster,
            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        // 4 DIP of padding each side, the LEFT one cancelled by a −4 margin: the hover plate overhangs and the text stays flush with the rows below; 2 + 26 + 2 is the 30-DIP title row.
        Element link = new BoxEl
        {
            Corners = Radii.ControlAll, MinWidth = 0f, Shrink = 1f,
            Padding = new Edges4(Artist.ReaderShape.HeadLinkInsetX, Artist.ReaderShape.HeadLinkInsetY / 2f,
                                 Artist.ReaderShape.HeadLinkInsetX, Artist.ReaderShape.HeadLinkInsetY / 2f),
            Margin = new Edges4(-Artist.ReaderShape.HeadLinkInsetX, 0f, 0f, 0f),
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = go,
            Children = [text],
        }.Interactive(Interaction.Subtle);
        return new BoxEl
        {
            Direction = 1, Justify = FlexJustify.Center,
            Grow = narrow ? 0f : 1f, Basis = narrow ? float.NaN : 0f,
            MinWidth = narrow ? 0f : Artist.ReaderShape.TitleFloorW,
            Height = narrow ? Artist.ReaderShape.HeadTitleRowH : float.NaN,
            Children = [Controls.TrimTip(link, title, text)],
        };
    }

    /// <summary>The head's SHIMMER face, per arm: <see cref="Head"/>'s geometry with sized boxes for the title, the meta and the three circles (the deriver needs a measurable shape, and `Skel.Region` forbids
    /// mounting stateful components, the tooltip host included, during load). Same stated heights, so the reveal moves nothing. The title and meta bars ride in clipping CONTAINER slots shaped like the real
    /// ones: `SkeletonDeriver` keeps a container's layout but turns a childless box into a bar that keeps its <c>Width</c> and drops its <c>Shrink</c> — and a childless `Grow` spacer into a visible 14-px
    /// pill.</summary>
    static Element ShimmerHead(IArtistReaderBlock cell, bool narrow)
    {
        static Element Circle() => new BoxEl
        {
            Width = Artist.ReaderShape.HeadCircle, Height = Artist.ReaderShape.HeadCircle, Shrink = 0f,
            Corners = Radii.Circle(Artist.ReaderShape.HeadCircle),
        };
        Element metaSlot = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f, ClipToBounds = true,
            Children = [new BoxEl { Width = ClassicLibraryReaderRules.ShimmerMetaW, Height = ClassicLibraryReaderRules.ShimmerMetaH, Corners = Radii.ControlAll }],
        };
        Element titleBar = new BoxEl { Width = ClassicLibraryReaderRules.ShimmerTitleW, Height = Artist.ReaderShape.HeadTitleLine, Corners = Radii.ControlAll };
        if (!narrow)
            return new BoxEl
            {
                Direction = 0, Gap = Artist.ReaderShape.HeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Height = Artist.ReaderShape.HeadH, OnBoundsChanged = cell.OnHeadBounds,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Basis = 0f,
                        MinWidth = Artist.ReaderShape.TitleFloorW, ClipToBounds = true, Children = [titleBar],
                    },
                    metaSlot, Circle(), Circle(), Circle(),
                ],
            };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Height = Artist.ReaderShape.HeadStackedH, OnBoundsChanged = cell.OnHeadBounds,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
                    Height = Artist.ReaderShape.HeadTitleRowH, Children = [titleBar],
                },
                new BoxEl
                {
                    Direction = 0, Gap = Artist.ReaderShape.HeadGap, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    MinWidth = 0f, Height = Artist.ReaderShape.HeadCircle,
                    Margin = new Edges4(0f, Artist.ReaderShape.HeadStackGap, 0f, Artist.ReaderShape.HeadStackPadBottom),
                    Children = [metaSlot, Circle(), Circle(), Circle()],
                },
            ],
        };
    }
}
