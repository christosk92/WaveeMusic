// ── Entities/Recents.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// rows, headers, cells, the drawer accordion
//
// Role: UI
// Owner: P
// Wave: 5
// Budget: 550 lines
// Spec: ch 16 §1.3 (DayHeader, ChildRow, the month card and its cell) · §2 W3, W5-W7, W11-W14, W23 · §3.1 · §5 rows 5,
//   10-14 · §9.1 #5-#9, #15 · §9.2 (the drawer's two indices, the ModuleHeader span trap, the artwork setting)
//
// WHAT A ROW IS MADE OF. The GROUP ARM is 0.2.9's `MediaCard.Row(plated: false)` geometry — 64 tall, `pad(8,0,8,0)`, gap
// 12, r4, transparent at rest, the ListRow hover ramp, no press scale — composed from the shared parts
// (`Controls.Artwork`, `Controls.NowPlayingOverlay`, `Controls.RowChip`, `Track.MoreCell`, `Track.ExpandCell`) rather
// than `Controls.MediaRow`, whose 8-DIP vertical padding and clipped 48-square cover slot cannot carry the Saved row's
// 60 × 56 stack inside the fixed 64 band (W7). The SINGLE-PLAY ARM (a lone track) and every DRAWER CHILD are
// `Track.EagerRow` — ch 01's vocabulary, the now-playing ink and the equalizer included; an episode member takes the
// compact episode row below (the shared row is Track-only).
//
// NOTHING IS REMOVED TO MAKE ROOM (§0 #17): a `!CanExpand` row keeps a 24 × 24 spacer where the chevron would be, a
// zero-count day header keeps its rule with an empty caption, and a row whose target has no identity yet keeps its
// geometry with two quiet bars instead of an invented string.
//
// EVERY HANDLER IS THE SLOT'S (`RowActions`, built once per recycled slot, resolving the CURRENT item at invocation), so
// a rebind allocates no closure; every accent consumer is a bound `Prop` over the page's pre-created thunks.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct Recents
{
    // ══ 1. METRICS ═══════════════════════════════════════════════════════════════════════════════════════════════════

    const float CardArt = 48f, SavedTile = 40f, ChildWhenCol = 60f;
    const float ChildActionsCol = 40f + Spacing.M + ChildWhenCol;

    /// <summary>Drawer child: # · ♥ · art · title* · duration · [when · "…"] (W6); the thumb lane drops with the setting.</summary>
    static readonly Track.ColumnSet ChildCols = new(Album: false, By: false, Date: false, Video: false, Plays: false,
        Heart: true, Thumb: true, Actions: true);
    static readonly Track.ColumnSet ChildColsNoArt = ChildCols with { Thumb = false };
    static readonly TrackSize[] ChildTracks =
        [TrackSize.Px(30f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Px(Track.RowMetrics.ThumbSize), TrackSize.Star(1f),
         TrackSize.Px(52f), TrackSize.Px(ChildActionsCol)];
    static readonly TrackSize[] ChildTracksNoArt =
        [TrackSize.Px(30f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(1f), TrackSize.Px(52f), TrackSize.Px(ChildActionsCol)];

    /// <summary>The single-play arm: # · ♥ · art · title* · duration · "…" — static, the surface has no width tiers.</summary>
    static readonly TrackSize[] SingleTracks =
        [TrackSize.Px(36f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Px(Track.RowMetrics.ThumbSize), TrackSize.Star(1f),
         TrackSize.Px(52f), TrackSize.Px(40f)];
    static readonly TrackSize[] SingleTracksNoArt =
        [TrackSize.Px(36f), TrackSize.Px(Track.RowMetrics.HeartCol), TrackSize.Star(1f), TrackSize.Px(52f), TrackSize.Px(40f)];

    /// <summary>The brush budget every accent consumer shares — a VALUE under reduced motion (0 ⇒ an instant swap).</summary>
    static float AccentTransitionMs => Design.Reduced ? 0f : Design.Motion.Standard;

    // ══ 2. WHAT A TARGET SAYS ════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A card's facts, read LIVE off the target's columns (no copied strings). <see cref="Known"/> is the
    /// identity gate: until it holds the row paints geometry, never an invented title.</summary>
    internal readonly record struct Facts(bool Known, string Title, string? Subtitle, string? Cover)
    {
        public static readonly Facts Unknown = new(false, "", null, null);
    }

    internal static Facts FactsOf(EntityRef t)
    {
        var s = Entities.Current;
        int slot = t.Slot;
        switch (t.Kind)
        {
            case EntityKind.Collection:
                return new Facts(true, Loc.Get(Strings.Detail.LikedSongs), null, null);
            case EntityKind.Playlist when slot > 0 && s.Playlists.Knows(slot, (uint)PlaylistFields.Identity):
            {
                var p = new Playlist(slot);
                return new Facts(true, Text(p.TitleId), OwnerOf(p), Controls.ArtUrl(p.ImageId));
            }
            case EntityKind.Album when slot > 0 && s.Albums.Knows(slot, (uint)AlbumFields.Title):
            {
                var a = new Album(slot);
                var artists = a.ArtistSlots;
                return new Facts(true, a.Title, artists.Length > 0 ? NonEmpty(new Artist(artists[0]).Name) : null,
                                 Controls.ArtUrl(a.ImageId));
            }
            case EntityKind.Artist when slot > 0 && s.Artists.Knows(slot, (uint)ArtistFields.Name):
                return new Facts(true, new Artist(slot).Name, null, Controls.ArtUrl(new Artist(slot).ImageId));
            case EntityKind.Show when slot > 0 && s.Shows.Knows(slot, (uint)ShowFields.Title):
            {
                var sh = new Show(slot);
                return new Facts(true, sh.Title, NonEmpty(Text(sh.PublisherId)), Controls.ArtUrl(sh.ImageId));
            }
            case EntityKind.Episode when slot > 0 && s.Episodes.Knows(slot, (uint)EpisodeFields.Title):
            {
                var e = new Episode(slot);
                return new Facts(true, e.Title, NonEmpty(e.Show.Title), Controls.ArtUrl(e.ImageId));
            }
            case EntityKind.Track when slot > 0 && s.Tracks.Knows(slot, (uint)TrackFields.Title):
            {
                var tr = new Track(slot);
                return new Facts(true, tr.Title, NonEmpty(Text(tr.ArtistLineId)), Controls.ArtUrl(tr.ImageId));
            }
        }
        return Facts.Unknown;
    }

    /// <summary>A playlist's byline through <see cref="RecentsView.OwnerSubtitle"/>: never a raw base62 id.</summary>
    static string? OwnerOf(Playlist p)
    {
        var owner = p.Owner;
        if (!owner.IsValid || !owner.Knows(UserFields.Identity)) return null;
        var id = owner.Id;
        string? raw = id.Form == EntityForm.Text ? NonEmpty(Text(id.TextId)) : null;
        return RecentsView.OwnerSubtitle(NonEmpty(Text(owner.NameId)), raw, null);
    }

    /// <summary>A member's cover for the Saved stack, straight off the image column (an empty column is the neutral tile).</summary>
    static string? CoverOf(EntityRef t) => t.Kind switch
    {
        EntityKind.Track when t.Slot > 0 => Controls.ArtUrl(new Track(t.Slot).ImageId),
        EntityKind.Episode when t.Slot > 0 => Controls.ArtUrl(new Episode(t.Slot).ImageId),
        _ => null,
    };

    static string Text(StringId id) => Entities.Strings.Resolve(id);
    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

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

    // ══ 3. THE DAY HEADER (W3, W4, W24) ══════════════════════════════════════════════════════════════════════════════

    /// <summary><c>label ——— N items</c>, 48 tall. The inline copy is the day's one tab stop; the pinned copy
    /// (<paramref name="overlay"/>) wears <c>FillLayerDefault</c> and keeps only the click. The hover ink is the page's
    /// accent — a plain <c>ColorF</c> (TextEl.HoverColor takes no Prop), so the realized header re-renders on a crossing.</summary>
    internal static Element DayHeader(PageView page, Shape shape, int dayIndex, bool overlay, Action onClick)
    {
        var sections = shape.Sections;
        string label = (uint)dayIndex < (uint)sections.HeaderLabels.Length ? sections.HeaderLabels[dayIndex] : "";
        int n = RecentsView.CountForDay(sections, dayIndex);
        return new BoxEl
        {
            Direction = 0, Height = RecentsLayout.DateHeaderHeight, Grow = overlay ? 1f : 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Fill = overlay ? Tok.FillLayerDefault : ColorF.Transparent,
            Role = overlay ? AutomationRole.None : AutomationRole.Button,
            Focusable = !overlay, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                Design.Type.ModuleHeader(label) with
                {
                    Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    HoverColor = page.Accent.Value.Ink, BrushTransitionMs = Design.Motion.Faster,
                },
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, Height = 1f, AlignSelf = FlexAlign.Center,
                    Fill = Tok.StrokeDividerDefault, HitTestVisible = false,
                },
                // n == 0 keeps the node with an empty caption: the lane never drops (§0 #17).
                Caption(n > 0 ? page.CountLabel(n) : "") with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 },
            ],
        };
    }

    // ══ 4. THE GROUP CARD (W3, W5, W7, W16, W23) ═════════════════════════════════════════════════════════════════════

    /// <summary>One card row, and — when this row is the open accordion entry — its drawer under it.</summary>
    internal static Element Card(PageView page, Shape shape, int r, bool expanded, RowActions h, IOverlayService overlay)
    {
        var rows = shape.Rows;
        var row = rows.Rows[r];
        var target = RecentsView.EntitySlotOf(rows, r);
        var facts = FactsOf(target);
        bool canExpand = RecentsView.CanExpand(rows, r);
        bool saved = row.Why == RecentsReason.Saved;
        bool circular = target.Kind == EntityKind.Artist;
        bool menu = HasCardMenu(target.Kind);
        string uri = page.UriOf(shape, r, target);
        string when = page.WhenOf(shape, r);
        string sub = page.SubtitleOf(shape, r, in row, facts.Subtitle);

        Element art = target.Kind == EntityKind.Collection
            ? Sidebar.Cover.Liked(CardArt)
            : Controls.Artwork(facts.Cover, CardArt, CardArt, circular ? CardArt / 2f : Radii.Control,
                               morphKey: page.MorphOf(shape, r, target, uri), decodePx: 96);
        Element tile = new BoxEl
        {
            ZStack = true, Width = CardArt, Height = CardArt, Shrink = 0f,
            // The shared now-playing overlay: the hover FAB, swapped for the equalizer pill when this context plays (W23).
            Children = [art, Controls.NowPlayingOverlay(uri, h.Play, 30f)],
        };

        Element text = new BoxEl
        {
            // Keyed on readiness: identity landing remounts the column, which fades it in — no resize (§5 row 9).
            Key = facts.Known ? "text" : "text-pending",
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Enter = facts.Known ? new EnterExit(Opacity: 0f, Active: true) : null,
            Transition = facts.Known ? MotionTok.ControlNormal : null,
            Children = !facts.Known
                ? [PendingBar(140f, 10f), PendingBar(90f, 8f)]
                : saved ? [TitleText(facts.Title), SavedMeta(sub)]
                : sub.Length > 0 ? [TitleText(facts.Title), SubText(sub)]
                : [TitleText(facts.Title)],
        };

        string? chip = KindLabel(target.Kind);
        var trail = new Element[(when.Length > 0 ? 1 : 0) + 2];
        int k = 0;
        if (when.Length > 0) trail[k++] = Caption(when) with { Key = "when", Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 };
        // A kind with no container menu keeps the "…" lane as an inert placeholder, never a dead button (parity 73).
        trail[k++] = Track.MoreCell(menu, false) with { Key = "more" };
        trail[k] = canExpand
            ? Track.ExpandCell(expanded, h.Toggle) with { Key = "chevron" }
            // The chevron lane is RESERVED, never collapsed: an artist row's "…" aligns with an album row's (parity 73).
            : new BoxEl { Key = "chevron", Width = Spacing.XXL, Height = Spacing.XXL, Shrink = 0f };

        var kids = new Element[chip is null ? 3 : 4];
        kids[0] = saved ? SavedArtwork(rows, r, tile) with { Key = "cover" } : tile with { Key = "cover" };
        kids[1] = text;
        if (chip is not null) kids[2] = Controls.RowChip(chip) with { Key = "chip" };
        kids[^1] = new BoxEl
        {
            Key = "trailing", Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, Shrink = 0f, Children = trail,
        };

        var card = new BoxEl
        {
            Key = "row", Direction = 0, Height = RecentsLayout.RowHeight, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.M, Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.ControlAll, Role = AutomationRole.Button, Cursor = CursorId.Hand,
            FocusVisualMargin = Design.FocusInsetRow,
            OnClick = canExpand ? h.Toggle : h.Open,
            Draggable = Drag.KindOf(target.Kind) == DragKind.Route ? null : h.DragFrom,
            Children = kids,
        }.Interactive(Interaction.ListRow);
        Element primary = menu && !Controls.IsNullOverlay(overlay) ? ContextMenu.Attach(card, overlay, h.Menu) : card;
        return expanded && canExpand
            ? new BoxEl { Direction = 1, MinWidth = 0f, Children = [primary, Drawer(page, shape, r)] }
            : new BoxEl { Direction = 1, MinWidth = 0f, Children = [primary] };
    }

    static TextEl TitleText(string title)
        => Design.Type.TrackTitle(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    static TextEl SubText(string sub)
        => Caption(sub) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>A quiet stand-in bar for a text line whose facts have not landed (geometry, never an invented string).</summary>
    static Element PendingBar(float width, float height) => new BoxEl
    {
        Width = width, Height = height, Shrink = 1f, MinWidth = 0f, Corners = Radii.FullAll, Fill = Tok.FillSubtleSecondary,
    };

    /// <summary>The Saved row's green meta line (W7): a 12 check and the sentence, both <c>SystemFillSuccess</c> at 600.</summary>
    static Element SavedMeta(string text) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Icon(Icons.Check, 12f, Tok.SystemFillSuccess) with { Shrink = 0f },
            Caption(text) with
            {
                Color = Tok.SystemFillSuccess, Weight = 600, Grow = 1f, Basis = 0f, MinWidth = 0f,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    };

    /// <summary>The Saved stack (W7): two 40-DIP member covers stepped to x 12 / x 16, the 48 context tile at y 8, in a
    /// 60 × 56 box — the same members-first source the drawer lists.</summary>
    static BoxEl SavedArtwork(RecentsSnapshot rows, int r, Element context)
    {
        var members = RecentsView.DrawerTargets(rows, r);
        var layers = new List<Element>(3);
        for (int i = 0; i < members.Length && layers.Count < 2; i++)
        {
            if (!RecentsView.Names(members[i])) continue;
            layers.Add(new BoxEl
            {
                Width = SavedTile, Height = SavedTile,
                Transform = Affine2D.Translation(layers.Count == 0 ? Spacing.M : Spacing.L, 0f),
                Children = [Controls.Artwork(CoverOf(members[i]), SavedTile, SavedTile, Radii.Control, decodePx: 80)],
            });
        }
        layers.Add(new BoxEl
        {
            Width = CardArt, Height = CardArt, Transform = Affine2D.Translation(0f, Spacing.S), Children = [context],
        });
        return new BoxEl
        {
            Width = CardArt + Spacing.M, Height = CardArt + Spacing.S, Shrink = 0f, ZStack = true, Children = layers.ToArray(),
        };
    }

    // ══ 5. THE SINGLE-PLAY ARM ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A lone track (or episode) play: the shared eager row at 64 with no artist line (§3.1 "single-play row").</summary>
    internal static Element SingleRow(Shape shape, int r, RowActions h)
    {
        var target = shape.Rows.Targets[r];
        bool art = !Prefs.Appearance.TrackArtworkHidden();
        if (target.Kind == EntityKind.Episode)   // the shared single-track menu is Track-only: the lane stays, inert
            return EpisodeRow(new Episode(target.Slot), r, RecentsLayout.RowHeight, Track.MoreCell(false, false), art, h.Play);
        return Track.EagerRow(new Track(target.Slot), r, art ? ChildCols : ChildColsNoArt, art ? SingleTracks : SingleTracksNoArt,
            RecentsLayout.RowHeight, h.Play, new Track.EagerRowOptions(ShowTrackArtist: false));
    }

    // ══ 6. THE DRAWER (W6, §5 rows 10-13, §9.1 #5-#6) ════════════════════════════════════════════════════════════════

    // TWO specs on TWO nodes. The OUTER clip box animates SIZE only and is a COLUMN, so the host measures the drawer's
    // true height (one spec on the clipping row stalled 333 ms and snapped); the INNER presence node fades and drops
    // INSIDE the stationary clip. SuppressDescendantTransitions stops late-landing rows starting a second geometry wave.
    static readonly LayoutTransition DrawerReveal = new(TransitionChannels.Size, MotionTok.DisclosureExpand.ToDynamics(),
        Size: SizeMode.Reflow, Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
        ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics(), Anchor: SizeAnchor.Leading, SuppressDescendantTransitions: true);

    static readonly LayoutTransition DrawerPresence = new(TransitionChannels.Opacity | TransitionChannels.Position,
        MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Dy: -Spacing.S, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true),
        ExitDynamics: MotionTok.DisclosureCollapse.ToDynamics());

    /// <summary>One child's entrance, delayed by <c>Design.Entrance.DelayMs</c> — capped at 8 × 40, 0 under reduced
    /// motion — never <c>Element.Stagger</c>, which has no ceiling (§9.1 #6).</summary>
    static readonly LayoutTransition ChildReveal = new(TransitionChannels.Opacity, MotionTok.DisclosureExpand.ToDynamics(),
        Enter: new EnterExit(Dy: -Spacing.XS, Opacity: 0f, Active: true));

    /// <summary>The accordion's body: a 1-DIP accent spine 32 in, the children a further 24. A member with no target is
    /// skipped, so the RENDERED ordinal (the # cell) and the wire index (the key) differ on purpose (§9.2).</summary>
    internal static Element Drawer(PageView page, Shape shape, int r)
    {
        var rows = shape.Rows;
        var entries = RecentsView.DrawerEntries(rows, r);
        var targets = RecentsView.DrawerTargets(rows, r);
        string key = FormatCache.Int(rows.Rows[r].ItemId.Value);
        bool art = !Prefs.Appearance.TrackArtworkHidden();
        var rendered = new List<Element>(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            if (!RecentsView.Names(targets[i])) continue;
            int ordinal = rendered.Count;
            rendered.Add(new BoxEl
            {
                Key = "child:" + key + ":" + FormatCache.Int(i), Direction = 1,
                Animate = ChildReveal with { DelayMs = Design.Entrance.DelayMs(ordinal) },
                Children = [ChildRow(page, targets[i], ordinal, entries[i].PlayedAtMs, art)],
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
                    Width = 1f, Shrink = 0f, Fill = page.AccentInkProp, BrushTransitionMs = AccentTransitionMs,
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
            Animate = DrawerReveal,
            Children = [new BoxEl { Key = "drawer-presence:" + key, Direction = 1, MinWidth = 0f, Animate = DrawerPresence, Children = [body] }],
        };
    }

    /// <summary>ch 16 §1.3's <c>Recents.ChildRow</c>: a drawer member at 40 — the shared eager row (its own menu, heart,
    /// now-playing ink) with the actions lane [played-at · "…"] in a FIXED 60-DIP caption lane so every "…" aligns.</summary>
    internal static Element ChildRow(PageView page, EntityRef target, int ordinal, long playedAtMs, bool art)
    {
        Element actions = ChildActions(page.PlayedAt(playedAtMs), target.Kind == EntityKind.Track);
        if (target.Kind == EntityKind.Episode)
        {
            var episode = new Episode(target.Slot);
            return EpisodeRow(episode, ordinal, RecentsLayout.ChildRowHeight, actions, art, () => Playback.PlayContext(episode.Id));
        }
        if (target.Kind != EntityKind.Track) return new BoxEl { Height = RecentsLayout.ChildRowHeight };
        var track = new Track(target.Slot);
        return Track.EagerRow(track, ordinal, art ? ChildCols : ChildColsNoArt, art ? ChildTracks : ChildTracksNoArt,
            RecentsLayout.ChildRowHeight, () => Playback.PlayContext(track.Id),
            new Track.EagerRowOptions(ShowTrackArtist: true, ActionsCell: actions));
    }

    static Element ChildActions(string when, bool menu) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Gap = Spacing.M, MinWidth = 0f,
        Children = when.Length == 0
            ? [Track.MoreCell(menu, false)]
            : [Caption(when) with { Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1 }, Track.MoreCell(menu, false)],
    };

    /// <summary>An episode member (the shared eager row is Track-only): # · art · title/show · duration · actions.</summary>
    static Element EpisodeRow(Episode e, int ordinal, float rowH, Element actions, bool art, Action play)
    {
        var kids = new List<Element>(5)
        {
            new BoxEl
            {
                Width = 30f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Caption(FormatCache.Int(ordinal + 1)) with { Color = Tok.TextTertiary }],
            },
        };
        if (art) kids.Add(Controls.Artwork(Controls.ArtUrl(e.ImageId), Track.RowMetrics.ThumbSize, Track.RowMetrics.ThumbSize,
                                           Radii.Control, decodePx: 64));
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            Children = [TitleText(e.Title), Design.Type.TrackMeta(e.Show.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }],
        });
        kids.Add(new BoxEl
        {
            Width = 52f, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
            Children = [Caption(Track.Format.DurationCell(e.DurationMs)) with { Color = Tok.TextSecondary, MaxLines = 1 }],
        });
        kids.Add(new BoxEl { Width = rowH > RecentsLayout.ChildRowHeight ? 40f : ChildActionsCol, Shrink = 0f, Direction = 1, Children = [actions] });
        return new BoxEl
        {
            Direction = 0, Height = rowH, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Corners = Radii.ControlAll,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = play,
            Children = kids.ToArray(),
        }.Interactive(Interaction.ListRow);
    }

    // ══ 7. THE CALENDAR (W11-W14, §9.1 #8-#9) ════════════════════════════════════════════════════════════════════════

    /// <summary>One month card: its title as its OWN node (the <c>ModuleHeader(title, meta)</c> span cannot carry the
    /// accent bind), the culture-rotated weekday band, and <c>WeekCount</c> week rows — never a fixed 6. Keyed by
    /// year:month under a shape-keyed surface, so the two constructor values are identity, not data.</summary>
    internal sealed class MonthCard(PageView page, int monthIndex) : Component
    {
        public override Element Render()
        {
            var shape = page.ShapeNow;
            var months = shape.Calendar.Months;
            if ((uint)monthIndex >= (uint)months.Length) return new BoxEl { Width = RecentsLayout.CalGridW };
            var month = months[monthIndex];
            var culture = page.Culture;
            DateOnly today = page.Today;

            var weekNames = new Element[7];
            var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
                weekNames[i] = new BoxEl
                {
                    Width = RecentsLayout.CalCellW, Height = RecentsLayout.CalHeaderH, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Caption(culture.DateTimeFormat.AbbreviatedDayNames[((int)firstDay + i) % 7]) with { Color = Tok.TextSecondary, MaxLines = 1 }],
                };

            var weeks = new Element[1 + month.WeekCount];
            weeks[0] = new BoxEl { Direction = 0, Gap = RecentsLayout.CalGap, Children = weekNames };
            DateOnly first = new(month.Year, month.Month, 1);
            DateOnly gridFirst = first.AddDays(-month.FirstDayOffset);
            for (int week = 0; week < month.WeekCount; week++)
            {
                var cells = new Element[7];
                for (int column = 0; column < 7; column++)
                    cells[column] = CalendarCell(page, shape, gridFirst.AddDays(week * 7 + column), month, today, monthIndex);
                weeks[1 + week] = new BoxEl { Direction = 0, Gap = RecentsLayout.CalGap, Children = cells };
            }

            // Three meta states and none is "0 plays" (W13).
            string meta = month.TotalPlays > 0
                ? Strings.Recents.PlayCount(month.TotalPlays) + (month.IsCurrentMonth ? " · " + Loc.Get(Strings.Recents.SoFar) : "")
                : Loc.Get(Strings.Recents.NothingPlayed);
            return new BoxEl
            {
                Direction = 1, Width = RecentsLayout.CalGridW, Gap = Spacing.S, OnPointerExit = page.ResetCalendarDayAction,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                        Children =
                        [
                            Design.Type.ModuleHeader(first.ToString("MMMM", culture)) with
                            {
                                Color = month.IsCurrentMonth ? page.AccentInkProp : (Prop<ColorF>)Tok.TextPrimary,
                                BrushTransitionMs = AccentTransitionMs, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f,
                            },
                            Caption(meta) with
                            {
                                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                Grow = 1f, Basis = 0f, MinWidth = 0f,
                            },
                        ],
                    },
                    new BoxEl { Direction = 1, Gap = RecentsLayout.CalGap, Children = weeks },
                ],
            };
        }
    }

    /// <summary>One 38 × 32 cell, FIXED (a stretchy cell turns the heatmap into ragged bars). A lead/trail day is a BLANK
    /// spacer; today is ONE cue — the numeral at 600 in the accent ink. Only a day the list has rows for is a button.</summary>
    static Element CalendarCell(PageView page, Shape shape, DateOnly date, RecentsCalendarMonth month, DateOnly today, int monthIndex)
    {
        if (date.Year != month.Year || date.Month != month.Month)
            return new BoxEl { Width = RecentsLayout.CalCellW, Height = RecentsLayout.CalCellH, Shrink = 0f };

        var day = RecentsLayout.CalendarDay(shape.Calendar, date);
        bool isToday = date == today;
        bool hasRows = RecentsLayout.HeaderFlatFor(shape.Sections, date) >= 0;
        var numeral = Body(FormatCache.Int(date.Day)) with
        {
            Weight = (ushort)(isToday ? 600 : 400), Wrap = TextWrap.NoWrap, MaxLines = 1,
            Color = isToday ? page.AccentInkProp : (Prop<ColorF>)Tok.TextPrimary,
            BrushTransitionMs = AccentTransitionMs,
        };
        var cell = new BoxEl
        {
            Width = RecentsLayout.CalCellW, Height = RecentsLayout.CalCellH, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Corners = Radii.ControlAll,
            Fill = page.DensityFillProp(day?.DensityLevel ?? 0), BrushTransitionMs = AccentTransitionMs,
            Role = hasRows ? AutomationRole.Button : AutomationRole.None,
            Focusable = hasRows, TabStop = hasRows, Cursor = hasRows ? CursorId.Hand : CursorId.Arrow,
            OnHoverMove = _ => page.CalendarDay.SetIfChanged(date),
            OnClick = hasRows ? () => page.JumpIn(date, monthIndex) : null,
            OnFocusChanged = hasRows ? focused => page.FocusDay(focused, date) : null,
            Children = [numeral],
        };
        return ToolTip.Wrap(cell, page.CalendarTooltip(shape, date));
    }

    /// <summary>The "Quieter → Busier" key: five 12 × 12 swatches painted by the SAME fill function the cells use.</summary>
    internal static Element Legend(PageView page)
    {
        var kids = new Element[7];
        kids[0] = Caption(Loc.Get(Strings.Recents.Legend.Quieter)) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };
        for (int level = 1; level <= 5; level++)
            kids[level] = new BoxEl
            {
                Width = 12f, Height = 12f, Shrink = 0f, Corners = Radii.ControlAll,
                Fill = page.DensityFillProp(level), BrushTransitionMs = AccentTransitionMs,
            };
        kids[6] = Caption(Loc.Get(Strings.Recents.Legend.Busier)) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f };
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f, Children = kids };
    }
}
