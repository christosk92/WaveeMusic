// ── Themes/Zune/Structure/Views/Recents/ZuneRecentsRows.cs — Zune's Recents rows (themes plan 6.3; P5 Z7) ───────────────────────────────────
//
// Board Z-Recents: a row is the kind word (56; folded into the caption at Small and Compact), the 48 art, the title over its caption, the time and
// the chevron. The art, the title, the caption and the trailing cluster are the shared media surface's row (`Controls.Surface` over `Shape.Row`,
// drawn by Zune's surface style: the focus, the wash, the now-playing overlay, the drag, the right-click menu and the hover "..." come from it);
// the kind word stands in a lane of its own before it, ONE tree at every tier (the lane's width is the tier record's, 0 where the word folds).
// A lone track or episode play is the same row (the host reads its facts like any other target), so the page speaks one row shape.
//
// NOTHING IS REMOVED TO MAKE ROOM: a row that cannot expand keeps the chevron's lane empty, a row whose target has no identity yet keeps its
// geometry with a quiet bar instead of an invented string. Every handler is the slot's (`ZuneRecentsRowActions`, built once per recycled slot,
// resolving the CURRENT item at invocation), so a rebind allocates no closure.
//
// THE DRAWER is the accordion under an open row: the members as the shared eager row (Zune's style draws it: the number, the title over its
// artist, the duration, the played-at stamp and the "...") 72 in; an episode member is the shared media row at the same inset.

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
using Z = Wavee.Themes.Zune.ZuneRecentsRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>One recycled slot's handlers, built ONCE per slot; each resolves the slot's CURRENT item at invocation.</summary>
internal sealed class ZuneRecentsRowActions
{
    public readonly Action Toggle, Open, Play;
    public readonly Func<ContextMenuModel?> Menu;
    public readonly DragSource DragFrom;

    public ZuneRecentsRowActions(IRecentsHost host, IReadSignal<RecentsFlatItem> item)
    {
        Toggle = () => host.ToggleExpanded(item.Peek().OriginalRowIndex);
        Open = () => host.Open(item.Peek().OriginalRowIndex);
        Play = () => host.Play(item.Peek().OriginalRowIndex);
        Menu = () => host.MenuFor(item.Peek().OriginalRowIndex);
        DragFrom = global::Wavee.Drag.Source(() => host.DragPayloadFor(item.Peek().OriginalRowIndex));
    }
}

/// <summary>The rows.</summary>
internal static class ZuneRecentsRows
{
    // ── the drawer child's lanes: # · title* · duration · [when · "..."] ─────────────────────────────────────────────────

    static readonly Track.ColumnSet ChildCols = new(Album: false, By: false, Date: false, Plays: false, Heart: false, Thumb: false, Actions: true);

    static int s_epoch = -1;
    static TrackSize[] s_childTracks = [];

    static TrackSize[] ChildTracks()
    {
        if (s_epoch != Tok.Epoch)
        {
            s_epoch = Tok.Epoch;
            s_childTracks =
            [
                TrackSize.Px(Z.ChildNumCol), TrackSize.Star(1f), TrackSize.Px(Z.DurationCol),
                TrackSize.Px(Z.ChildActionsCol + Spacing.M + Z.ChildWhenCol),
            ];
        }
        return s_childTracks;
    }

    static SurfaceShape ChildEpisodeShape => global::Wavee.Shape.Row(Z.ChildArt) with { MinHeight = Z.ChildEpisodeMinHeight };

    // ── what a target says ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The kind word: borrowed keys only; Liked Songs and Unknown carry none.</summary>
    static string? KindLabel(EntityKind kind) => kind switch
    {
        EntityKind.Album => Loc.Get(Strings.Home.Album),
        EntityKind.Artist => Loc.Get(Strings.Home.Artist),
        EntityKind.Show => Loc.Get(Strings.Podcast.Show),
        EntityKind.Episode => Loc.Get(Strings.Podcast.Episodes),
        EntityKind.Track => Loc.Get(Strings.Detail.Column.Song),
        EntityKind.Playlist => Loc.Get(Strings.Nav.Playlist),
        _ => null,
    };

    /// <summary>Does the row offer the container menu (and therefore a "...")?</summary>
    static bool HasCardMenu(EntityKind kind) => kind is EntityKind.Album or EntityKind.Artist or EntityKind.Playlist or EntityKind.Collection;

    // ── the row ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One row, and, when this row is the open accordion entry, its drawer under it.</summary>
    internal static Element Row(IRecentsHost host, RecentsShape shape, int r, bool expanded, ZuneRecentsRowActions h, ZuneRecentsLayout layout)
    {
        var rows = shape.Rows;
        var row = rows.Rows[r];
        var target = RecentsView.EntitySlotOf(rows, r);
        var facts = host.FactsOf(target);
        bool canExpand = RecentsView.CanExpand(rows, r);
        bool saved = row.Why == RecentsReason.Saved;
        bool circular = target.Kind == EntityKind.Artist;
        bool menu = HasCardMenu(target.Kind) && !layout.Stacked;
        string uri = host.UriOf(shape, r, target);
        string when = host.WhenOf(shape, r);
        string sub = host.SubtitleOf(shape, r, in row, facts.Subtitle);
        string? kind = KindLabel(target.Kind);

        float ctxEdge = saved ? Z.SavedContext : Z.CardArt;
        Element art = target.Kind == EntityKind.Collection
            ? Sidebar.Cover.Liked(ctxEdge)
            : Controls.Artwork(facts.Cover, ctxEdge, ctxEdge, circular ? R.Shape.For(ShapeRole.Avatar) : R.Shape.Cover,
                               morphKey: host.MorphOf(shape, r, target, uri), decodePx: Z.ContextDecodePx);

        // The kind word has a lane of its own where the tier gives it one (Standard, Huge); elsewhere it opens the caption, and at Compact the
        // played-at stamp closes it (Z-Recents-Compact: "playlist · 6 tracks · 14:32"), so every row is the same two lines on the same rhythm.
        bool laned = layout.KindLane > 0f;
        string captionText = !laned && kind is not null ? (sub.Length > 0 ? kind + " · " + sub : kind) : sub;
        if (layout.Stacked && when.Length > 0) captionText = captionText.Length > 0 ? captionText + " · " + when : when;

        var trail = new List<Element>(3);
        if (layout.Stacked)
            trail.Add(ChevronLane(canExpand, expanded, h.Toggle, layout.ChevronLane));
        else
        {
            if (when.Length > 0)
                trail.Add(new BoxEl
                {
                    Key = "when", Direction = 0, Width = Z.TimeLane, Shrink = 0f, Justify = FlexJustify.End,
                    Children = [Meta(when) with { Shrink = 0f }],
                });
            else trail.Add(new BoxEl { Key = "when", Width = Z.TimeLane, Shrink = 0f });
            trail.Add(ChevronLane(canExpand, expanded, h.Toggle, layout.ChevronLane));
            trail.Add(new BoxEl { Key = "more-lane", Width = Controls.IconButtonSize, Shrink = 0f });
        }

        Element? subtitle;
        if (!facts.Known) subtitle = Controls.PendingBar(Z.NameBar, Z.NameBarHeight);
        else if (saved) subtitle = SavedMeta(captionText);
        else subtitle = captionText.Length > 0 ? Caption(captionText) : null;

        var data = new Controls.CardData(
            uri, facts.Known ? facts.Title : "",
            subtitle,
            null, canExpand ? h.Toggle : h.Open, h.Play, circular,
            global::Wavee.Drag.KindOf(target.Kind) == DragKind.Route ? null : h.DragFrom,
            ShowMenu: menu,
            CoverOverride: saved ? SavedArtwork(host, rows, r, art) : art)
        {
            Menu = menu ? h.Menu : null,
            Trailing = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f, Children = trail.ToArray() },
            Height = RecentsLayout.RowHeight,
        };
        Element primary = Controls.Surface(data, global::Wavee.Shape.Row(Z.CardArt) with { MinHeight = RecentsLayout.RowHeight, PadX = Z.RowPadX });
        Element line = new BoxEl
        {
            Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Center,
            Children =
            [
                new BoxEl
                {
                    Key = Z.KindLaneKey, Width = layout.KindLane, Height = RecentsLayout.RowHeight, Shrink = 0f, ClipToBounds = true,
                    Direction = 0, AlignItems = FlexAlign.Center, Layout = ZuneMotion.ColumnCollapse,
                    Children = laned && kind is not null ? [Meta(kind) with { Case = R.Case.For(LabelKind.Label) }] : [],
                },
                new BoxEl { Key = "card", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [primary] },
            ],
        };
        return expanded && canExpand
            ? new BoxEl { Direction = 1, MinWidth = 0f, Children = [line, Drawer(host, shape, r, layout)] }
            : new BoxEl { Direction = 1, MinWidth = 0f, Children = [line] };
    }

    static TextEl Caption(string text)
        => R.Type.DenseMeta(text) with
        {
            Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    static TextEl Meta(string text)
        => R.Type.DenseMeta(text) with
        {
            Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };

    /// <summary>The chevron's lane: a <paramref name="lane"/> square target that toggles the drawer (a row with nothing to open keeps the lane
    /// empty, so the rows align).</summary>
    static Element ChevronLane(bool canExpand, bool expanded, Action toggle, float lane) => !canExpand
        ? new BoxEl { Key = "chevron", Width = lane, Height = lane, Shrink = 0f }
        : new BoxEl
        {
            Key = "chevron", Width = lane, Height = lane, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = toggle, BlocksDragArm = true,
            Children = [Icon(expanded ? Icons.ChevronUp : Icons.ChevronDown, Z.ChevronGlyph, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);

    /// <summary>The Saved row's meta line: a check and the sentence, both in the success ink.</summary>
    static Element SavedMeta(string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Icon(Icons.Check, Z.CheckSize, Tok.SystemFillSuccess) with { Shrink = 0f },
            Caption(text) with { Color = Tok.SystemFillSuccess, Weight = Z.SavedWeight, Grow = 1f, Basis = 0f },
        ],
    };

    /// <summary>The Saved stack: two member covers stepped behind the context tile, inside the surface's 48 art square.</summary>
    static BoxEl SavedArtwork(IRecentsHost host, RecentsSnapshot rows, int r, Element context)
    {
        var members = RecentsView.DrawerTargets(rows, r);
        var layers = new List<Element>(3);
        for (int i = 0; i < members.Length && layers.Count < 2; i++)
        {
            if (!RecentsView.Names(members[i])) continue;
            layers.Add(new BoxEl
            {
                Width = Z.SavedTile, Height = Z.SavedTile,
                Transform = Affine2D.Translation(layers.Count == 0 ? Z.SavedStepFirst : Z.SavedStepSecond, 0f),
                Children = [Controls.Artwork(host.CoverOf(members[i]), Z.SavedTile, Z.SavedTile, R.Shape.Cover, decodePx: Z.MemberDecodePx)],
            });
        }
        layers.Add(new BoxEl
        {
            Width = Z.SavedContext, Height = Z.SavedContext, Transform = Affine2D.Translation(0f, Z.SavedDrop), Children = [context],
        });
        return new BoxEl { Width = Z.CardArt, Height = Z.CardArt, Shrink = 0f, ZStack = true, Children = layers.ToArray() };
    }

    // ── the drawer ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The accordion's body, <see cref="ZuneRecentsLayout.DrawerInset"/> in from the row's edge. A member with no target is skipped, so the
    /// RENDERED ordinal (the # cell) and the wire index (the key) differ on purpose.</summary>
    internal static Element Drawer(IRecentsHost host, RecentsShape shape, int r, ZuneRecentsLayout layout)
    {
        var rows = shape.Rows;
        var entries = RecentsView.DrawerEntries(rows, r);
        var targets = RecentsView.DrawerTargets(rows, r);
        string key = FormatCache.Int(rows.Rows[r].ItemId.Value);
        var rendered = new List<Element>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            if (!RecentsView.Names(targets[i])) continue;
            int ordinal = rendered.Count;
            rendered.Add(new BoxEl
            {
                Key = "child:" + key + ":" + FormatCache.Int(i), Direction = 1,
                Children = [ChildRow(host, targets[i], ordinal, entries[i].PlayedAtMs, layout.ChildRowH)],
            });
        }
        Element body = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 0f, Padding = new Edges4(0f, 0f, 0f, Z.DrawerPadBottom),
            Children = rendered.ToArray(),
        };
        return new BoxEl
        {
            Key = "drawer:" + key, Direction = 1, MinWidth = 0f, Shrink = 0f, ClipToBounds = true,
            Margin = new Edges4(layout.DrawerInset, 0f, 0f, 0f),
            Animate = Design.Reveal.Drawer,
            Children = [new BoxEl { Key = "drawer-presence:" + key, Direction = 1, MinWidth = 0f, Children = [body] }],
        };
    }

    /// <summary>A drawer member: the shared eager row (its own menu, heart, now-playing ink) with the actions lane [played-at, "..."], or, for an
    /// episode, the shared media row with the same played-at lane.</summary>
    static Element ChildRow(IRecentsHost host, EntityRef target, int ordinal, long playedAtMs, float rowH)
    {
        string when = host.PlayedAt(playedAtMs);
        if (target.Kind == EntityKind.Episode)
        {
            var episode = new Episode(target.Slot);
            return EpisodeRow(episode, ChildEpisodeShape, when, () => host.PlayContext(episode.Id));
        }
        if (target.Kind != EntityKind.Track) return new BoxEl { Height = rowH };
        var track = new Track(target.Slot);
        return Track.EagerRow(track, ordinal, ChildCols, ChildTracks(), rowH, () => host.PlayContext(track.Id),
            new Track.EagerRowOptions(ShowTrackArtist: true, ActionsCell: ChildActions(when)));
    }

    static Element ChildActions(string when) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.M, MinWidth = 0f,
        Children = when.Length == 0
            ? [Track.MoreCell(true, false)]
            : [Meta(when) with { Shrink = 0f }, Track.MoreCell(true, false)],
    };

    /// <summary>An episode member (the track grid's rows are Track-only): the shared media surface at the drawer's band, fed by the episode's own
    /// adapter, with the length at the right, the played-at stamp after it and the "..." lane.</summary>
    static Element EpisodeRow(Episode e, SurfaceShape shape, string when, Action play)
    {
        Element trailing = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children =
            [
                new BoxEl
                {
                    Width = Z.DurationCol, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                    Children = [Meta(Track.Format.DurationCell(e.DurationMs)) with { Color = Tok.TextSecondary }],
                },
                new BoxEl
                {
                    Width = Z.ChildWhenCol, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                    Children = when.Length == 0 ? [] : [Meta(when) with { Shrink = 0f }],
                },
                new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f },
            ],
        };
        var options = new Episode.RowOptions(OnClick: play, OnPlay: play, ShowMeta: false, Trailing: trailing);
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = [Controls.Surface(Episode.RowData(e, in options), shape)] };
    }
}
