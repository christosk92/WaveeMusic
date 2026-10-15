// ── Themes/Classic/Structure/Views/Pages/ClassicRecentsRows.cs — Classic's Recents rows (themes plan 6.1, T10c2) ─────────────────────────────────
//
// From `Entities\Recents.UI.cs`: the group card, the Saved stack, the single-play arm, the drawer accordion and its children.
//
// WHAT A ROW IS MADE OF. The GROUP ARM is the shared surface's row (`Controls.Surface(…, Shape.Row(48))` pinned to the
// 64 band): focusable, the ListRow plate, the now-playing overlay, a drag, the right-click menu and the hover "…"
// overlay come from the surface; the kind chip, played-at stamp and expand chevron ride its trailing cluster, and the
// Saved row's member stack is its cover (36-DIP members behind a 40 context tile inside the 48 square, W7). The SINGLE-PLAY ARM (a lone track) and every DRAWER CHILD are
// `Track.EagerRow` — ch 01's vocabulary, the now-playing ink and the equalizer included; an episode member is the
// app's shared media surface (`Controls.Surface` fed by `Episode.RowData`) at the same band — the track grid is Track-only.
//
// NOTHING IS REMOVED TO MAKE ROOM (§0 #17): a `!CanExpand` row keeps a 24 × 24 spacer where the chevron would be, a
// zero-count day header keeps its rule with an empty caption, and a row whose target has no identity yet keeps its
// geometry with two quiet bars instead of an invented string.
//
// EVERY HANDLER IS THE SLOT'S (`ClassicRecentsRowActions`, built once per recycled slot, resolving the CURRENT item at invocation), so
// a rebind allocates no closure; every accent consumer is a bound `Prop` over the page's pre-created thunks.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>One recycled slot's handlers, built ONCE per slot; each resolves the slot's CURRENT item at invocation.</summary>
internal sealed class ClassicRecentsRowActions
{
    public readonly Action Toggle, Open, Play;
    public readonly Func<ContextMenuModel?> Menu;
    public readonly DragSource DragFrom;

    public ClassicRecentsRowActions(IRecentsHost host, IReadSignal<RecentsFlatItem> item)
    {
        Toggle = () => host.ToggleExpanded(item.Peek().OriginalRowIndex);
        Open = () => host.Open(item.Peek().OriginalRowIndex);
        Play = () => host.Play(item.Peek().OriginalRowIndex);
        Menu = () => host.MenuFor(item.Peek().OriginalRowIndex);
        DragFrom = global::Wavee.Drag.Source(() => host.DragPayloadFor(item.Peek().OriginalRowIndex));
    }
}

/// <summary>The rows.</summary>
internal static class ClassicRecentsRows
{
    // ── the lanes (rebuilt when the theme epoch moves: `ChildActionsCol` reads the spacing ramp) ──────────────────────

    static readonly Track.ColumnSet ChildCols = new(Album: false, By: false, Date: false, Plays: false,
        Heart: true, Thumb: true, Actions: true);
    static readonly Track.ColumnSet ChildColsNoArt = ChildCols with { Thumb = false };

    static int s_epoch = -1;
    static TrackSize[] s_childTracks = [], s_childTracksNoArt = [], s_singleTracks = [], s_singleTracksNoArt = [];

    static void Lanes()
    {
        if (s_epoch == Tok.Epoch) return;
        s_epoch = Tok.Epoch;
        // Drawer child: # · ♥ · art · title* · duration · [when · "…"] (W6); the thumb lane drops with the setting.
        s_childTracks =
            [TrackSize.Px(ClassicRecentsRules.ChildNumCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Px(Track.RowMetrics.ThumbSize), TrackSize.Star(1f),
             TrackSize.Px(ClassicRecentsRules.DurationCol), TrackSize.Px(ClassicRecentsRules.ChildActionsCol)];
        s_childTracksNoArt =
            [TrackSize.Px(ClassicRecentsRules.ChildNumCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(1f), TrackSize.Px(ClassicRecentsRules.DurationCol),
             TrackSize.Px(ClassicRecentsRules.ChildActionsCol)];
        // The single-play arm: # · ♥ · art · title* · duration · "…" — static, the surface has no width tiers.
        s_singleTracks =
            [TrackSize.Px(ClassicRecentsRules.SingleNumCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Px(Track.RowMetrics.ThumbSize), TrackSize.Star(1f),
             TrackSize.Px(ClassicRecentsRules.DurationCol), TrackSize.Px(ClassicRecentsRules.SingleActionsCol)];
        s_singleTracksNoArt =
            [TrackSize.Px(ClassicRecentsRules.SingleNumCol), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(1f), TrackSize.Px(ClassicRecentsRules.DurationCol),
             TrackSize.Px(ClassicRecentsRules.SingleActionsCol)];
    }

    /// <summary>An episode member's two bands: the single-play arm is the 64 band the group card and the track row share
    /// (48 art — <c>Shape.Row(48)</c>'s own floor); a drawer child is a 40 cover in a 56 row (the shared row cannot be
    /// shorter than its text column plus its 8-DIP padding, so it is taller than a 40-DIP track child).</summary>
    static SurfaceShape SingleEpisodeShape => global::Wavee.Shape.Row(ClassicRecentsRules.CardArt);
    static SurfaceShape ChildEpisodeShape => global::Wavee.Shape.Row(ClassicRecentsRules.ChildArt) with { MinHeight = ClassicRecentsRules.ChildEpisodeMinHeight };

    // ── what a target says ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The type capsule (§3.3): borrowed keys only; Liked Songs and Unknown carry none.</summary>
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

    /// <summary>Does the group card offer the container menu (and therefore a "…")?</summary>
    static bool HasCardMenu(EntityKind kind)
        => kind is EntityKind.Album or EntityKind.Artist or EntityKind.Playlist or EntityKind.Collection;

    // ── the group card (W3, W5, W7, W16, W23) ─────────────────────────────────────────────────────────────────────

    /// <summary>One card row, and — when this row is the open accordion entry — its drawer under it.</summary>
    internal static Element Card(IRecentsHost host, RecentsShape shape, int r, bool expanded, ClassicRecentsRowActions h)
    {
        var rows = shape.Rows;
        var row = rows.Rows[r];
        var target = RecentsView.EntitySlotOf(rows, r);
        var facts = host.FactsOf(target);
        bool canExpand = RecentsView.CanExpand(rows, r);
        bool saved = row.Why == RecentsReason.Saved;
        bool circular = target.Kind == EntityKind.Artist;
        bool menu = HasCardMenu(target.Kind);
        string uri = host.UriOf(shape, r, target);
        string when = host.WhenOf(shape, r);
        string sub = host.SubtitleOf(shape, r, in row, facts.Subtitle);

        // The cover: the context art (a Saved row's smaller tile sits in front of two member covers). The surface mounts the
        // shared now-playing overlay over it (the hover FAB, swapped for the equalizer pill when this context plays, W23).
        float ctxEdge = saved ? ClassicRecentsRules.SavedContext : ClassicRecentsRules.CardArt;
        Element art = target.Kind == EntityKind.Collection
            ? Sidebar.Cover.Liked(ctxEdge)
            : Controls.Artwork(facts.Cover, ctxEdge, ctxEdge, circular ? ctxEdge / 2f : Radii.Control,
                               morphKey: host.MorphOf(shape, r, target, uri), decodePx: ClassicRecentsRules.ContextDecodePx);

        // The trailing cluster: [kind chip] · [when] · [chevron]. The chevron lane is RESERVED, never collapsed (an artist
        // row's lane aligns with an album row's, parity 73), and the "…" overlay lands on the lane after it, so the chevron
        // stays visible while the pointer is on the row.
        string? chip = KindLabel(target.Kind);
        var trail = new List<Element>(4);
        if (chip is not null) trail.Add(Controls.RowChip(chip) with { Key = "chip" });
        if (when.Length > 0) trail.Add(Caption(when) with { Key = "when", Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 });
        trail.Add(canExpand
            ? Track.ExpandCell(expanded, h.Toggle) with { Key = "chevron" }
            : new BoxEl { Key = "chevron", Width = Spacing.XXL, Height = Spacing.XXL, Shrink = 0f });
        trail.Add(new BoxEl { Key = "more-lane", Width = Controls.IconButtonSize, Shrink = 0f });

        var data = new Controls.CardData(
            uri, facts.Known ? facts.Title : "",
            !facts.Known ? Controls.PendingBar(ClassicRecentsRules.CardNameBar, ClassicRecentsRules.CardNameBarHeight) : saved ? SavedMeta(sub) : sub.Length > 0 ? SubText(sub) : null,
            null, canExpand ? h.Toggle : h.Open, h.Play, circular,
            global::Wavee.Drag.KindOf(target.Kind) == DragKind.Route ? null : h.DragFrom,
            ShowMenu: menu,
            CoverOverride: saved ? SavedArtwork(host, rows, r, art) : art)
        {
            Menu = menu ? h.Menu : null,
            Trailing = new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f, Children = trail.ToArray() },
            Height = RecentsLayout.RowHeight,
        };
        Element primary = Controls.Surface(data, global::Wavee.Shape.Row(ClassicRecentsRules.CardArt) with { MinHeight = RecentsLayout.RowHeight });
        return expanded && canExpand
            ? new BoxEl { Direction = 1, MinWidth = 0f, Children = [primary, Drawer(host, shape, r)] }
            : new BoxEl { Direction = 1, MinWidth = 0f, Children = [primary] };
    }

    static TextEl SubText(string sub)
        => Caption(sub) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The Saved row's green meta line (W7): a 12 check and the sentence, both <c>SystemFillSuccess</c> at 600.</summary>
    static Element SavedMeta(string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Icon(Icons.Check, ClassicRecentsRules.CheckSize, Tok.SystemFillSuccess) with { Shrink = 0f },
            Caption(text) with
            {
                Color = Tok.SystemFillSuccess, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>The Saved stack (W7): two 36-DIP member covers stepped to x 6 / x 12 behind the 40 context tile at y 8,
    /// inside the surface's 48 art square — the same members-first source the drawer lists.</summary>
    static BoxEl SavedArtwork(IRecentsHost host, RecentsSnapshot rows, int r, Element context)
    {
        var members = RecentsView.DrawerTargets(rows, r);
        var layers = new List<Element>(3);
        for (int i = 0; i < members.Length && layers.Count < 2; i++)
        {
            if (!RecentsView.Names(members[i])) continue;
            layers.Add(new BoxEl
            {
                Width = ClassicRecentsRules.SavedTile, Height = ClassicRecentsRules.SavedTile,
                Transform = Affine2D.Translation(layers.Count == 0 ? Spacing.XS + 2f : Spacing.S + 4f, 0f),
                Children = [Controls.Artwork(host.CoverOf(members[i]), ClassicRecentsRules.SavedTile, ClassicRecentsRules.SavedTile, Radii.Control,
                    decodePx: ClassicRecentsRules.MemberDecodePx)],
            });
        }
        layers.Add(new BoxEl
        {
            Width = ClassicRecentsRules.SavedContext, Height = ClassicRecentsRules.SavedContext, Transform = Affine2D.Translation(0f, Spacing.S), Children = [context],
        });
        return new BoxEl
        {
            Width = ClassicRecentsRules.CardArt, Height = ClassicRecentsRules.CardArt, Shrink = 0f, ZStack = true, Children = layers.ToArray(),
        };
    }

    // ── the single-play arm ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A lone track (or episode) play: the shared eager row at 64 with no artist line (§3.1 "single-play row").</summary>
    internal static Element SingleRow(IRecentsHost host, RecentsShape shape, int r, ClassicRecentsRowActions h)
    {
        Lanes();
        var target = shape.Rows.Targets[r];
        bool art = !ClassicOptions.HideTrackArt();
        if (target.Kind == EntityKind.Episode)   // the track grid is Track-only: an episode is the shared media row, same band
            return EpisodeRow(new Episode(target.Slot), SingleEpisodeShape, when: null, h.Play);
        return Track.EagerRow(new Track(target.Slot), r, art ? ChildCols : ChildColsNoArt, art ? s_singleTracks : s_singleTracksNoArt,
            RecentsLayout.RowHeight, h.Play, new Track.EagerRowOptions(ShowTrackArtist: false));
    }

    // ── the drawer (W6, §5 rows 10-13, §9.1 #5-#6) ────────────────────────────────────────────────────────────────

    // ONE spec on the clip box (Design.Reveal.Drawer): the presented height springs, the rows below ride it; the inner box
    // is plain structure.

    /// <summary>The accordion's body: a 1-DIP accent spine 32 in, the children a further 24. A member with no target is
    /// skipped, so the RENDERED ordinal (the # cell) and the wire index (the key) differ on purpose (§9.2).</summary>
    internal static Element Drawer(IRecentsHost host, RecentsShape shape, int r)
    {
        var rows = shape.Rows;
        var entries = RecentsView.DrawerEntries(rows, r);
        var targets = RecentsView.DrawerTargets(rows, r);
        string key = FormatCache.Int(rows.Rows[r].ItemId.Value);
        bool art = !ClassicOptions.HideTrackArt();
        var rendered = new List<Element>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            if (!RecentsView.Names(targets[i])) continue;
            int ordinal = rendered.Count;
            rendered.Add(new BoxEl
            {
                Key = "child:" + key + ":" + FormatCache.Int(i), Direction = 1,
                Children = [ChildRow(host, targets[i], ordinal, entries[i].PlayedAtMs, art)],
            });
        }
        Element body = new BoxEl
        {
            Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children =
            [
                // The section spine: the one structural-looking element the accent budget lets carry accent (§0 #10).
                new BoxEl
                {
                    Width = 1f, Shrink = 0f, Fill = host.AccentInkProp, BrushTransitionMs = ClassicRecents.AccentTransitionMs,
                    HitTestVisible = false,
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 0f,
                    Padding = new Edges4(Spacing.L + Spacing.S, 0f, Spacing.S, 0f),
                    Children = rendered.ToArray(),
                },
            ],
        };
        return new BoxEl
        {
            Key = "drawer:" + key, Direction = 1, MinWidth = 0f, Shrink = 0f, ClipToBounds = true,
            Margin = new Edges4(Spacing.XXL + Spacing.S, Spacing.XXS, 0f, Spacing.S),
            Animate = Design.Reveal.Drawer,
            Children = [new BoxEl { Key = "drawer-presence:" + key, Direction = 1, MinWidth = 0f, Children = [body] }],
        };
    }

    /// <summary>ch 16 §1.3's <c>Recents.ChildRow</c>: a drawer member at 40 — the shared eager row (its own menu, heart,
    /// now-playing ink) with the actions lane [played-at · "…"] in a FIXED 60-DIP caption lane so every "…" aligns. An
    /// episode member is the shared media surface (<see cref="EpisodeRow"/>) with the same played-at lane.</summary>
    static Element ChildRow(IRecentsHost host, EntityRef target, int ordinal, long playedAtMs, bool art)
    {
        Lanes();
        string when = host.PlayedAt(playedAtMs);
        if (target.Kind == EntityKind.Episode)
        {
            var episode = new Episode(target.Slot);
            return EpisodeRow(episode, ChildEpisodeShape, when, () => host.PlayContext(episode.Id));
        }
        if (target.Kind != EntityKind.Track) return new BoxEl { Height = RecentsLayout.ChildRowHeight };
        var track = new Track(target.Slot);
        return Track.EagerRow(track, ordinal, art ? ChildCols : ChildColsNoArt, art ? s_childTracks : s_childTracksNoArt,
            RecentsLayout.ChildRowHeight, () => host.PlayContext(track.Id),
            new Track.EagerRowOptions(ShowTrackArtist: true, ActionsCell: ChildActions(when)));
    }

    static Element ChildActions(string when) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.M, MinWidth = 0f,
        Children = when.Length == 0
            ? [Track.MoreCell(true, false)]
            : [Caption(when) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 }, Track.MoreCell(true, false)],
    };

    /// <summary>An episode member (the track grid's rows are Track-only): the app's shared media surface at the lane's
    /// <paramref name="shape"/>, fed by the episode's own adapter — the show name under the title, the length at the right
    /// (then the played-at stamp in a drawer: <paramref name="when"/> null = none, "" = a reserved lane with nothing in
    /// it), and the episode menu behind the "…" and the right-click (the lane used to be an inert placeholder). A click
    /// plays the member, as the row always did. The surface owns click and focus here (this slot provides no
    /// <c>SlotRow</c>), exactly as the group card does.</summary>
    static Element EpisodeRow(Episode e, SurfaceShape shape, string? when, Action play)
    {
        Element trailing = new BoxEl
        {
            Width = ClassicRecentsRules.DurationCol, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
            Children = [Caption(Track.Format.DurationCell(e.DurationMs)) with { Color = Tok.TextSecondary, MaxLines = 1 }],
        };
        if (when is not null)
            trailing = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
                Children =
                [
                    trailing,
                    new BoxEl
                    {
                        Width = ClassicRecentsRules.ChildWhenCol, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                        Children = when.Length == 0 ? [] : [Caption(when) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 }],
                    },
                ],
            };
        // The "…" is the surface's hover overlay at the row's end; the lane is kept (not reserved by the surface) so the
        // duration and played-at columns line up with the track children's [when · "…"] lane.
        trailing = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f,
            Children = [trailing, new BoxEl { Width = Controls.IconButtonSize, Shrink = 0f }],
        };
        var options = new Episode.RowOptions(OnClick: play, OnPlay: play, ShowMeta: false, Trailing: trailing);
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = [Controls.Surface(Episode.RowData(e, in options), shape)] };
    }
}
