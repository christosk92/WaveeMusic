// ── Themes/Zune/Structure/Views/Library/ZuneLibraryArtistReader.Blocks.cs — the reader's block faces (themes plan 6.3; P5 Z7) ───────────────────
//
// One block: cover + "2022 · Album" caption | head (title link · meta · play, heart, "…") + rows, in its REAL face and its COUNTED skeleton, which is the same geometry with
// placeholder boxes and `ReaderBlock.Rows` placeholder rows, so `ReaderShape.ExtentOf` stays exact across the reveal. The cell (`IArtistReaderBlock`) is the core's: its
// readiness `Loadable`, its record, its liked rows, its verbs. The view builds the finished region ONCE per cell and caches it on the cell (`Face`), so a re-realize allocates
// nothing.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryArtistReader
{
    /// <summary>The block body for one cell: its CACHED region element, so a re-realize allocates nothing at all. The region cross-dissolves the counted skeleton into the real
    /// block as the cell's <c>Load</c> flips (a plain opacity fade), and re-diffs its content in place when the cell's version moves. `smoothResize` is OFF: the region sits
    /// inside a MEASURED virtualized item whose extent the shape already states exactly.</summary>
    static Element BlockBody(IArtistReaderBlock cell)
        => cell.Face ??= Skel.Region(cell.Load, () => Build(cell, real: false), _ => Build(cell, real: true), SkelReveal.FadeOnly, smoothResize: false)
            with { Key = "blk:" + cell.AlbumSlot.ToString(CultureInfo.InvariantCulture) };

    /// <summary>One block. <paramref name="real"/> false is the COUNTED skeleton: the same geometry with placeholder boxes and <see cref="Artist.ReaderBlock.Rows"/> placeholder
    /// rows, which is what keeps <see cref="Artist.ReaderShape.ExtentOf"/> exact across the reveal. A failed tracklist replaces the rows with the Error voice's own Retry strip.</summary>
    static Element Build(IArtistReaderBlock cell, bool real)
    {
        var b = cell.Block;
        int slot = cell.AlbumSlot;
        var a = new Album(slot);
        bool narrow = cell.Narrow;                                // the arm this mount froze (it is a list mount input)
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
            rows = Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Library.SongsFailed), onAction: cell.Retry);
        }
        else
        {
            // A LIKED-ONLY block lists the liked rows and nothing else: you did not save this record, so the rest of it is not yours to show. The row NUMBER is the position in that
            // list, not the album's track number; the play action is still the track in the ALBUM's context.
            var tracks = b.LikedOnly ? cell.LikedRows() : valid ? a.TrackSlots : default;
            int n = real ? tracks.Length : Math.Max(1, b.Rows);
            var kids = new Element[n];
            for (int r = 0; r < n; r++)
            {
                if (!real)
                {
                    kids[r] = PlaceholderRow(r) with { Key = "sh:" + r.ToString(CultureInfo.InvariantCulture) };
                    continue;
                }
                int trackSlot = tracks[r];
                // ZERO-based: `Track.NumberCell` prints `displayIndex + 1`.
                kids[r] = Track.EagerRow(new Track(trackSlot), r, in LR.Cols, LR.Tracks, Artist.ReaderShape.RowH,
                    () => cell.PlayTrack(trackSlot),
                    in LR.RowOptions)
                    with { Key = "t:" + trackSlot.ToString(CultureInfo.InvariantCulture) };
            }
            rows = new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Artist.ReaderShape.RowsPadBottom),
                Children = kids,
            };
        }

        // A plain stretched column: on the BOUND path the block hangs under the scroller's content node (through the slot's layout-transparent anchors), and that is a column
        // with `AlignItems.Stretch`: the full width comes for free. A `Grow 1 / Basis 0` on a column would aim at the HEIGHT and zero the block.
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
                            // STICKY COVER: the art and its caption ride down the block while its tracks scroll past, and stop at the block's own bottom edge. The sticky clamps to
                            // the containing block (the row above, `AlignItems.Start`, so this column keeps its content height and the clamp has real room). The inset is the pinned
                            // rail plus the block's top pad, so the cover comes to rest exactly where an unscrolled block draws it. Declared on BOTH faces because `Build` is one
                            // function; the SHIMMER face harmlessly loses it (`SkeletonDeriver` rewrites a box with children as `ScrollEffects = []`).
                            ScrollEffects = [new ScrollEffectSpec(ScrollEffect.Sticky(Artist.ReaderShape.SubRailH + Artist.ReaderShape.BlockPadTop))],
                            Children =
                            [
                                new BoxEl
                                {
                                    Width = cover, Height = cover, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
                                    Cursor = valid ? CursorId.Hand : CursorId.Arrow,
                                    Role = valid ? AutomationRole.Button : AutomationRole.None,
                                    OnClick = valid ? go : null,
                                    Children = [Controls.Artwork(valid ? Controls.ArtUrl(a.ImageId) : null, cover, cover, R.Shape.For(ShapeRole.Cover), decodePx: LR.BlockDecodePx)],
                                },
                                R.Type.MicroMeta(caption) with
                                {
                                    Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, LineHeight = Artist.ReaderShape.CaptionLine,
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
                    Margin = new Edges4(LR.ReaderInset, 0f, LR.ReaderInset, 0f),
                },
            ],
        };
    }

    /// <summary>One counted-skeleton row: the shared row's lanes (the number, the title, the heart, the duration) at <see cref="Artist.ReaderShape.RowH"/> with bars where the
    /// number, the title and the clock go. The heart lane holds nothing: it carries no content to promise.</summary>
    static BoxEl PlaceholderRow(int i) => new()
    {
        Direction = 0, Height = Artist.ReaderShape.RowH, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = LR.ReaderNumLane, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Controls.PendingBar(LR.ShimNumW, LR.ShimBarH)] },
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 0, AlignItems = FlexAlign.Center, ClipToBounds = true, Children = [Controls.PendingBar(LR.ShimRowTitleW(i), LR.ShimBarH)] },
            new BoxEl { Width = Track.Lane.HeartTrailing, Shrink = 0f },
            new BoxEl { Width = LR.ReaderClockLane, Shrink = 0f, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Children = [Controls.PendingBar(LR.ShimClockW, LR.ShimBarH)] },
        ],
    };

    /// <summary>The album head, in its arm. INLINE: one row, the title slot the row's flexible filler (floored at <see cref="Artist.ReaderShape.TitleFloorW"/>), then the meta beside the
    /// three circles; the meta shrinks before the title would go under its floor, and the circles never do. STACKED (narrow): the title on a row of its own, over the meta and the
    /// circles right-aligned beneath it. Both roots DECLARE <see cref="Artist.ReaderShape.HeadHeight"/> as their <c>Height</c>, not a <c>MinHeight</c>: a locale whose metrics
    /// overrun keeps the stated box, it never reflows the block.</summary>
    static Element Head(IArtistReaderBlock cell, string uri, string title, Action go, bool narrow)
    {
        Element metaEl = R.Type.MicroMeta(cell.Meta()) with
        {
            Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        Element play = Controls.Named(Controls.PlayFab(cell.PlayAlbum, ZunePlayerLayout.PlaySolid, Artist.ReaderShape.HeadCircle), Strings.Library.PlayAlbum(title));
        Element save = K.Save(uri, title, Artist.ReaderShape.HeadCircle, Artist.ReaderShape.HeadGlyph);
        Element more = K.MoreButton(cell.Menu, Artist.ReaderShape.HeadCircle);
        Element titleSlot = TitleSlot(title, go, narrow);
        if (!narrow)
            return new BoxEl
            {
                Direction = 0, Gap = Artist.ReaderShape.HeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Height = Artist.ReaderShape.HeadH, OnBoundsChanged = cell.OnHeadBounds,
                Children = [titleSlot, metaEl, play, save, more],        // title fills (floor) → meta shrinks → circles never
            };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, Height = Artist.ReaderShape.HeadStackedH, OnBoundsChanged = cell.OnHeadBounds,
            Children =
            [
                titleSlot,                                               // HeadTitleRowH tall, stretched to the body
                new BoxEl
                {
                    // `Justify End`, not a spacer: a spacer would charge the meta one more gap.
                    Direction = 0, Gap = Artist.ReaderShape.HeadGap, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
                    MinWidth = 0f, Height = Artist.ReaderShape.HeadCircle,
                    Margin = new Edges4(0f, Artist.ReaderShape.HeadStackGap, 0f, Artist.ReaderShape.HeadStackPadBottom),
                    Children = [metaEl, play, save, more],
                },
            ],
        };
    }

    /// <summary>The title link inside its tooltip, inside a slot the FLEX algorithm sizes: inline it is the head row's `Grow 1 / Basis 0` filler, floored at
    /// <see cref="Artist.ReaderShape.TitleFloorW"/>; stacked it is a column child of the stated row height, cross-stretched to the body. In both, the ToolTip wrapper is a COLUMN
    /// child and the link inside it is `Shrink 1 / MinWidth 0`, so a short title keeps a content-hugging hit area, a long one ellipsizes, and only a trimmed one gets a tooltip.</summary>
    static Element TitleSlot(string title, Action go, bool narrow)
    {
        TextEl text = R.Type.QuickplayHead(title) with
        {
            Size = LR.HeadTitleSize, LineHeight = Artist.ReaderShape.HeadTitleLine, Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary,
            BrushTransitionMs = Design.Motion.Faster, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        Element link = new BoxEl
        {
            MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false, Role = AutomationRole.Button, OnClick = go,
            Children = [text],
        };
        return new BoxEl
        {
            Direction = 1, Justify = FlexJustify.Center,
            Grow = narrow ? 0f : 1f, Basis = narrow ? float.NaN : 0f,
            MinWidth = narrow ? 0f : Artist.ReaderShape.TitleFloorW,
            Height = narrow ? Artist.ReaderShape.HeadTitleRowH : float.NaN,
            Children = [Controls.TrimTip(link, title, text)],
        };
    }

    /// <summary>The head's SHIMMER face, per arm: <see cref="Head"/>'s geometry with sized boxes for the title, the meta and the three circles (the deriver needs a measurable shape,
    /// and `Skel.Region` forbids mounting stateful components during load). Same stated heights, so the reveal moves nothing. The title and meta bars ride in clipping CONTAINER
    /// slots shaped like the real ones: `SkeletonDeriver` keeps a container's layout but turns a childless box into a bar that keeps its <c>Width</c> and drops its <c>Shrink</c>.</summary>
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
            Children = [new BoxEl { Width = LR.ShimmerMetaW, Height = LR.ShimmerMetaH, Corners = R.Shape.All(ShapeRole.Control) }],
        };
        Element titleBar = new BoxEl { Width = LR.ShimmerTitleW, Height = Artist.ReaderShape.HeadTitleLine, Corners = R.Shape.All(ShapeRole.Control) };
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
