using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The "+N" artist-overflow chip: a small tertiary "+N" that opens a MenuFlyout listing the given
/// artists, each navigating to its own page. The shared answer to "more credited artists than the line can
/// show" — the artist chart's feat line and the player bar's subtitle both use it, so overflow never clips
/// a name silently.</summary>
sealed class ArtistMoreButton : Component
{
    readonly IReadOnlyList<ArtistRef> _artists;
    readonly Action<string, string?> _go;
    readonly int _shown;   // artists already visible inline — the chip label counts only the hidden rest

    public ArtistMoreButton(IReadOnlyList<ArtistRef> artists, Action<string, string?> go, int shown = 1)
    { _artists = artists; _go = go; _shown = shown; }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        int extra = Math.Max(0, _artists.Count - _shown);

        void Toggle()
        {
            if (svc is null) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
            var items = new MenuFlyoutItem[_artists.Count];
            for (int i = 0; i < _artists.Count; i++)
            {
                var a = _artists[i];
                items[i] = new MenuFlyoutItem(a.Name, Invoke: () => _go("artist:" + a.Uri, a.Name));
            }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Create(items, () => handle.Value?.Close()),
                FlyoutPlacement.BottomEdgeAlignedLeft,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Shrink = 0f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
            OnRealized = h => anchor.Value = h,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = Toggle,
            Children =
            [
                Caption("+" + extra) with { Weight = 600, Color = Tok.TextTertiary },
            ],
        };
    }
}

// Which optional columns a track row shows. #, Title and Duration are always present. Cell build order (and the matching
// track widths) is: # · ♥ · (thumb) · Title · Artist · Album · AddedBy · DateAdded · Plays · Tempo · Duration · Video · Actions.
// SHARED by the detail TrackList header + every row builder, so the header and the rows stay column-aligned by
// construction. Video sits in the trailing chrome (before Expand) so film / hover "…" never land between Album and
// Tempo. Actions = the trailing "…" overflow lane when Video is off (dropped at the ultra-compact tier; still reachable
// via the row context menu). When Video is on, More lives IN the Video lane (rest=Movie / hover=bare "…") and Actions
// stays off so the trailing lane is not double-reserved. Tier = the resolved width tier this set was built for —
// carried here so Grid/Header/TracksFor all derive the SAME tier-scaled padding/gap (the alignment invariant). Both are
// defaulted so the many non-tiered ColumnSet sites (search, artist "Popular", queue, drawers) keep their current look
// (Actions present, tier-0 spacing).
internal readonly record struct ColumnSet(bool Album, bool By, bool Date, bool Video, bool Plays, bool Heart, bool Thumb,
                                          bool Actions = true, int Tier = 0,
                                          // Tempo + musical key (extended-metadata kind 222). Enrichment rather than
                                          // identity, so it is the FIRST column dropped under width pressure — see
                                          // ShowTempo. Off by default: dense surfaces (search, artist Popular) opt out.
                                          bool Tempo = false,
                                          // The expand chevron (alternate versions + per-item audio format). Sits at
                                          // the very END of the row, after the Video/"…" lane.
                                          bool Expand = false,
                                          // Classic's dedicated artist lane. Default false keeps every non-detail row
                                          // on the established title-subline grammar.
                                          bool Artist = false,
                                          // Classic table chrome/ink. Carried with the shape so persistent rows patch
                                          // live even when a surface (for example an album) has no optional columns.
                                          bool Classic = false);

// ── the ONE track-row cell, used EVERYWHERE a track is shown (detail list, library pane, artist "Popular", search) ──
// This is the single source of truth for what a track row LOOKS like and how it BEHAVES at rest/hover/now-playing — the
// number↔play/pause transport reveal, the live equalizer, the buffer spinner, the per-row heart, the art thumb, the
// artist/album hyperlinks, the duration/plays cells. Callers vary only the COLUMN SET (what's shown) and the CONTAINER
// (the detail/library bound-selection skin vs. an eager hover row), so every surface renders an identical cell — they
// can never drift, because they all build from here. Pure/diffable (no Animate) → a bound re-render patches in place.
internal static class TrackRow
{
    // Grid-layout constants — SHARED so a row's columns line up under the detail header (the alignment invariant).
    internal const float RowHeight = 48f;            // density M
    internal const float HeaderHeight = 36f;
    internal const float ColGap = Spacing.M;       // shared by header + rows
    internal const float PadX = Spacing.L;         // shared horizontal inset (header chrome padding == row grid padding)
    internal const float RowInset = Spacing.S;     // rounded row-highlight inset (rows pad PadX−RowInset so columns stay header-aligned)
    /// <summary>The row art column. On the app thumbnail ladder (32/40/48/56/64); 36 was between two rungs and the
    /// tie is broken DOWNWARD on purpose — the COMPACT density row is 40 DIPs tall (<see cref="RowHeightFor"/>), so a
    /// 40px thumb would fill it edge to edge with no breathing room at all.</summary>
    internal const float ThumbSize = WaveeSize.Thumb32;
    /// <summary>The ♥ column. Sized to the 28 DIP like hit-target so the left cluster (# · ♥ · art) stays tight —
    /// a wider lane used to read as empty gutter, worse on every unsaved row when the outline was hover-only.
    /// One number with <see cref="TrackLane.Heart"/>, which the relief ladder measures the squeeze from.</summary>
    internal const float HeartCol = TrackLane.Heart;
    internal const float CompactListItemExtent = ItemsView.ListItemExtent;

    // Track row height by density (0 Compact · 1 Default · 2 Cozy · 3 Comfortable).
    internal static float RowHeightFor(int density) => density switch { 0 => 40f, 2 => 56f, 3 => 64f, _ => RowHeight };

    /// <summary>The row art edge for THIS ladder, by density — forwards to the one decision
    /// (<see cref="DetailTrackTableRules.ArtSizeFor"/>) so the detail table and every other Modern row agree on the
    /// same 32/40/48 numbers instead of drifting into a second copy. Modern only: Classic's tighter room is the
    /// detail table's own call (<c>set.Classic</c>), and no non-detail surface renders Classic rows.</summary>
    internal static float ArtSizeFor(int density) => DetailTrackTableRules.ArtSizeFor(density, classic: false);

    // Tier-scaled horizontal inset + column gap: full at wide tiers, tighter as the pane narrows so the title keeps
    // usable width under pressure. Header AND rows read these SAME helpers (keyed by the set's Tier) so columns stay
    // aligned. Tier 0 returns the unchanged constants, so every non-tiered surface is untouched.
    internal static float PadXFor(int tier) => tier <= 3 ? PadX : tier <= 5 ? Spacing.M : Spacing.S;
    internal static float ColGapFor(int tier) => tier <= 4 ? ColGap : Spacing.S;

    /// <summary>Stable per-column cell keys, shared by the row grid and the header grid so a column that disappears at a
    /// breakpoint is removed by the keyed diff instead of shifting every later cell onto the wrong column.</summary>
    internal static class CellKey
    {
        internal const string Num = "c.num";
        internal const string Heart = "c.heart";
        internal const string Art = "c.art";
        internal const string Title = "c.title";
        internal const string Artist = "c.artist";
        internal const string Album = "c.album";
        internal const string By = "c.by";
        internal const string Date = "c.date";
        internal const string Video = "c.video";
        internal const string Plays = "c.plays";
        internal const string Tempo = "c.tempo";
        internal const string Duration = "c.dur";
        internal const string More = "c.more";
        internal const string Expand = "c.expand";
    }

    // Stream count → "1.85B" / "11.8M" / "654.8K".
    internal static string PlaysLabel(long n) =>
        n >= 1_000_000_000 ? $"{n / 1_000_000_000f:0.##}B"
        : n >= 1_000_000 ? $"{n / 1_000_000f:0.#}M"
        : n >= 1_000 ? $"{n / 1_000f:0.#}K"
        : n.ToString("N0");

    // The per-row playback state the cell reflects (now-playing equalizer / buffer spinner / top-track star / saved heart).
    internal readonly record struct State(bool IsNow, bool IsPlaying, bool IsBuffering, bool IsTop, bool Saved);

    internal enum ArtCardKind { Grid, Rail }

    /// <summary>What a numeric cell shows when the value is not merely zero but not yet knowable — an em dash, which
    /// reads as "nothing to state" where "0" and "0:00" read as facts.</summary>
    internal const string Dash = "—";

    internal static State StateOf(PlaybackBridge? bridge, LibraryBridge? lib, Track t,
                                  bool isTop = false, bool extraBuffering = false)
    {
        bool isNow = bridge?.Identity.Value.Track?.Id == t.Id;
        bool isPlaying = isNow && (bridge?.IsPlaying.Value ?? false);
        bool isBuffering = extraBuffering || (isNow && bridge is not null && bridge.IsBuffering.Value);
        bool saved = t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false);
        return new State(isNow, isPlaying, isBuffering, isTop, saved);
    }

    internal static void TogglePlayPause(PlaybackBridge bridge)
    {
        bool playing = bridge.IsPlaying.Peek();
        bridge.IsPlaying.Value = !playing;
        if (playing) _ = bridge.Player.PauseAsync(); else _ = bridge.Player.ResumeAsync();
    }

    internal static void Invoke(PlaybackBridge? bridge, Track track, Action startDifferent)
    {
        if (bridge is not null && bridge.Identity.Peek().Track?.Id == track.Id)
        {
            TogglePlayPause(bridge);
            return;
        }
        startDifferent();
    }

    // Builds the row GRID — ONE source for the live bound rows, the eager rows, AND the skeleton shimmer. The per-track
    // values arrive resolved (t + state flags + the title element), so the caller decides static (shimmer/eager) vs
    // index-signal-bound (detail BoundRowContent) title. Plain/diffable — no Animate — so a re-render patches cells in place.
    internal static Element Grid(Track t, int displayIndex, in State st, ColumnSet set, TrackSize[] tracks, float rowH,
                                 Element title, bool showTrackArtist, Action<string, string?> go,
                                 // Defaults to ThumbSize (the fixed 32-DIP art) — every non-detail caller (search,
                                 // artist Popular, the album drawer, RecentsPage) never passes this and keeps its
                                 // established look. The detail table is the one caller that threads a density-keyed
                                 // value (DetailTrackTableRules.ArtSizeFor) so its art actually grows with the row.
                                 float art = ThumbSize,
                                 Action? onPlay = null, Action? onLike = null, Owner? addedByProfile = null,
                                 bool likePop = false, Element? actionsCell = null,
                                 bool showAlbumInMeta = false, bool showListBadges = false,
                                 Element? expandCell = null, bool moreEnabled = true,
                                 IReadSignal<bool>? hoverPaused = null)
    {
        float thumb = art;   // the row's art column edge — density-keyed for the detail table, fixed elsewhere

        var cells = new List<Element>(tracks.Length);
        // Every cell carries a STABLE key. The column set changes at runtime (a breakpoint cross drops Album/Added-by/
        // Date), and these are the GridEl's positional children — unkeyed, the reconciler would match the surviving
        // Added-by cell against the departed Album cell and patch the wrong content into the wrong column (and destroy
        // /remount any component that landed opposite a different element type). Keyed, it removes exactly the cells
        // that left and updates the rest in place. Keys are per-parent, so the header below reuses the same names.
        void Add(string key, Element cell) => cells.Add(cell with { Key = key });

        // The server says this one is not out yet. It keeps its position and its height — hiding it would make a 12-track
        // album look like a 3-track one and jump the numbering — but it stops offering things that cannot happen.
        // IsNotYetOut() is the ONE shared predicate (Wavee.Core): the grey treatment here, the play gate in
        // DetailTracks.PlayRow and the "N of M songs" fact tile must never disagree about which rows are pending, and it
        // un-dims the row the moment its live timestamp passes — no refetch needed.
        bool notYetOut = t.IsNotYetOut();
        bool classicNow = set.Classic && st.IsNow;
        ColorF secondaryInk = classicNow ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF tertiaryInk = classicNow ? Tok.AccentTextPrimary : Tok.TextTertiary;

        // # cell: number / live equalizer / fetch spinner at rest; reveals a SINGLE-CLICK play (or pause) button on ROW
        // hover — suppressed for a track that is not released, where the hover play would be a button that does nothing.
        Add(CellKey.Num, NumberCell(displayIndex, st.IsNow, st.IsPlaying, st.IsBuffering, st.IsTop,
                                    notYetOut ? null : onPlay, hoverPaused, chart: t.Chart,
                                    classic: set.Classic));

        // ♥ — in the left cluster (between # and the art thumb). Filled when saved; click toggles via the caller's bridge.
        if (set.Heart) Add(CellKey.Heart, CenterCell(Heart(st.Saved, onLike, likePop, classic: set.Classic)));

        // Art thumb (playlist/liked) gets its OWN column before Title — so the "Title" header aligns over the title TEXT,
        // not the artwork (the WaveeMusic RowArtColDef pattern). Then the title + artist subline (subline hidden on
        // single-artist albums/singles/EPs).
        if (set.Thumb)
            // decodePx tracks the DISPLAYED edge (not the fixed ThumbSize default) — a Comfortable-density 48-DIP
            // thumb must decode at 96px, not the 64px a stale constant would have asked for, or the cover reads soft.
            Add(CellKey.Art, CenterCell(Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, thumb, thumb,
                                                          Radii.Control, decodePx: (int)(art * 2f))));
        // The video glyph is NOT part of the metadata subline: it lives in the trailing Video/More lane at every tier
        // that keeps one (see set.Video), so a row states "has a video" in exactly ONE place. A subline copy meant the
        // same fact moved lanes across a breakpoint cross — the film icon jumping from the trailing chrome into the
        // artist line and back.
        bool showMeta = !set.Classic && (showTrackArtist || showAlbumInMeta || (showListBadges && t.IsExplicit));
        bool classicVideo = DetailTrackTableRules.ShowClassicInlineVideo(
            set.Classic, VideoPresence.HasVideo(t), set.Tier);
        Element titleLine = set.Classic
            ? ClassicTitleLine(t, title, showTrackArtist, classicVideo,
                showListBadges && t.IsExplicit, go, classicNow)
            : title;
        var titleCol = new BoxEl
        {
            // MinWidth=0: this stack sits in the STAR track, which the overflow guard collapses to 0 first. Without the
            // floor override it keeps its natural width and the title/artist runs paint across the whole row.
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS,
            // Dimmed rather than recoloured: the title element is built by the CALLER (plain, marquee, bound), so the
            // grid cannot reach into it to swap a token — but it can hand the whole column back a step in the hierarchy,
            // which is the same signal and works for every title variant.
            Opacity = notYetOut ? 0.45f : 1f,
            // At compact playlist tiers the dedicated Album lane disappears. Preserve its information on the existing
            // artist subline instead of simply dropping it: Explicit · artists · album.
            Children = showMeta
                ? [titleLine, MetadataLine(t, go, showTrackArtist, showAlbumInMeta, showListBadges && t.IsExplicit,
                                      classicNow ? Tok.AccentTextPrimary : null)]
                : [titleLine],
        };
        Add(CellKey.Title, new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true, Children = [titleCol] });

        if (set.Artist)
            Add(CellKey.Artist, LeftCell(ArtistLinks(t.Artists, go,
                size: set.Classic ? 14f : 12f, lineHeight: set.Classic ? 20f : 16f,
                color: secondaryInk)));
        if (set.Album)
            Add(CellKey.Album, LeftCell(AlbumLink(t.Album, go, secondaryInk,
                size: set.Classic ? 14f : 12f, lineHeight: set.Classic ? 20f : 16f)));
        if (set.By)
            Add(CellKey.By, AddedByCell(t.AddedBy, addedByProfile, secondaryInk, classic: set.Classic));
        if (set.Date)
            Add(CellKey.Date, LeftCell(FactualText(DetailFormat.DateAddedLabel(t.AddedAt), set.Classic, secondaryInk)
                with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }));
        // A track the server says is not playable yet (an unreleased entry on a partly-released album) reports 0 plays
        // and 0 duration. Formatting those gives "0" and "0:00", which reads as a real, dismal track rather than as one
        // that is not out — so the cells state the absence instead. Reuses the `notYetOut` local above rather than
        // re-deriving the test: one row must not be dim-but-timed or bright-but-dashed.
        // A count of 0 is "not known yet", never "nobody played it": the kind-185 reader refuses to invent a count, and
        // playlist/Liked rows fill LAZILY (the whole-list hydrator runs off the open path, and album rows are countless
        // until it lands too). Rendering that as "0" would state a fact the app does not have, so the cell dashes.
        if (set.Plays)
            Add(CellKey.Plays, EndCell(FactualText(
                notYetOut || t.PlayCount <= 0 ? Dash : PlaysLabel(t.PlayCount), set.Classic, tertiaryInk)));
        if (ShowTempo(set))
            Add(CellKey.Tempo, EndCell(TempoCell(t, classicNow ? Tok.AccentTextPrimary : null, set.Classic)));
        // A pending track states WHEN rather than a dash, when the metadata plane gave us a live instant in the future
        // (TrackV4.earliest_live_timestamp). "Fri 4 Sep" answers the question the row actually raises; "—" only says
        // the duration is unknown, which the reader can already see.
        // A hydrated-looking row with DurationMs == 0 is the same unknown: formatting that as "0:00" would claim a
        // zero-second track (the Plays cell already dashes 0). DurationCell is the 0-is-unknown rule.
        string durationText = notYetOut
            ? (t.AvailableAt is { } live && live > DateTimeOffset.UtcNow ? DetailFormat.ShortDate(live) : Dash)
            : DetailFormat.DurationCell(t.DurationMs);
        // Every secondary COLUMN in the row (album, added-by, date, plays, tempo, duration, the resting number) sits on
        // ONE rung — Caption 12/16 — instead of the old 13/12.5/13/13/12.5/13/13 spread. The row therefore carries
        // exactly two type steps: BodyStrong 14/20/600 for the title and Caption 12/16 for everything factual.
        Add(CellKey.Duration, EndCell(FactualText(durationText, set.Classic,
            classicNow ? Tok.AccentTextPrimary : notYetOut ? Tok.TextTertiary : Tok.TextSecondary)));

        // Trailing chrome: Video (film at rest / bare "…" on hover) OR dedicated Actions "…", then Expand. Video must
        // sit AFTER Duration so it never wedges between Album and Tempo when the BPM column is on.
        if (set.Video)
            // Override-aware: a user-attached local video counts as "this row has a video" exactly like the source's own
            // association. VideoPresence.HasVideo is one ordinal dictionary probe — no context read, no per-row signal.
            Add(CellKey.Video, CenterCell(VideoMoreCell(VideoPresence.HasVideo(t), moreEnabled)));
        // Trailing "..." overflow lane when Video is off. Present only when the set keeps Actions AND the caller
        // reserved its width in `tracks`. When Video is on, More lives in the Video lane instead.
        if (set.Actions && actionsCell is not null) Add(CellKey.More, actionsCell);
        // The expand chevron is the LAST cell — after Video/"…" — so it reads as "open this row" rather than as another
        // row command. Emitted only when the caller both wants it and supplied one, so the width track and the cell
        // can never disagree.
        if (set.Expand && expandCell is not null) Add(CellKey.Expand, expandCell);

        float padX = PadXFor(set.Tier);
        return new GridEl
        {
            Columns = tracks, ColGap = ColGapFor(set.Tier), RowHeight = rowH, Grow = 1f,   // fill the row skin's content lane
            // Pad padX − RowInset: with the skin's RowInset margin, columns still start at padX (header-aligned).
            Padding = new Edges4(set.Classic ? padX : padX - RowInset, 0f,
                                 set.Classic ? padX : padX - RowInset, 0f),
            Children = cells.ToArray(),
        };
    }

    // A self-contained, EAGER (non-virtualized) interactive row for small preview lists — artist "Popular", search
    // "Songs". It wraps the SAME cell in a hover container that is the interactive ancestor, so the number↔play/pause
    // transport reveal + the now-playing equalizer + the per-row heart behave EXACTLY like the big virtualized lists; only
    // virtualization + multi-select are dropped (these are short previews). Single-click plays (no multi-select here). The
    // title is a plain now-playing-coloured ellipsis (the marquee is reserved for the full lists' now-playing row).
    // Component-hosted so the row hover signal (EQ pause + HoverOpacity source) lives across parent re-renders.
    internal static Element Row(Track t, int displayIndex, in State st, ColumnSet set, TrackSize[] tracks, float rowH,
                                bool showTrackArtist, Action<string, string?> go, Action onPlay, Action? onLike = null, bool zebra = false,
                                Element? actionsCell = null)
        => Embed.Comp(
            new EagerRowProps(t, displayIndex, st, set, tracks, rowH, showTrackArtist, go, onPlay, onLike, zebra, actionsCell),
            () => new EagerRowHost());

    sealed record EagerRowProps(
        Track Track, int DisplayIndex, State St, ColumnSet Set, TrackSize[] Tracks, float RowH,
        bool ShowTrackArtist, Action<string, string?> Go, Action OnPlay, Action? OnLike, bool Zebra, Element? ActionsCell);

    sealed class EagerRowHost : Component
    {
        public override Element Render()
        {
            var m = UsePropsOrDefault<EagerRowProps>();
            if (m is null) return new BoxEl();
            var hovered = UseSignal(false);
            bool oddZebra = m.Zebra && m.DisplayIndex % 2 != 0;
            Element title = WaveeType.TrackTitle(m.Track.Title) with
            {
                Color = m.St.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
            return new BoxEl
            {
                MinHeight = m.RowH, ClipToBounds = true, Margin = new Edges4(RowInset, 0f, RowInset, 0f),
                // The interactive row highlight takes the CONTROL rung. The app used to run a 6-for-grids / 5-for-lists
                // split that matched nothing on the Radii ramp; both are 4 now.
                Corners = Radii.ControlAll,
                Fill = oddZebra ? WaveeColors.RowZebra : ColorF.Transparent,
                HoverFill = oddZebra ? WaveeColors.RowHoverZebra : WaveeColors.RowHover,
                PressedFill = oddZebra ? WaveeColors.RowPressedZebra : WaveeColors.RowPressed,
                PressScale = WaveeMotion.ScaleSubtle.Press, BorderWidth = 1f,
                BorderColor = oddZebra ? Tok.StrokeCardDefault : ColorF.Transparent,
                HoverBorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button, OnClick = m.OnPlay,
                // Real enter/exit (not a no-op): PointerBit for HoverOpacity inheritance AND the EQ pause signal.
                OnHoverMove = _ => { if (!hovered.Peek()) hovered.Value = true; },
                OnPointerExit = () => { if (hovered.Peek()) hovered.Value = false; },
                Children =
                [
                    Grid(m.Track, m.DisplayIndex, m.St, m.Set, m.Tracks, m.RowH, title, m.ShowTrackArtist, m.Go,
                         onPlay: m.OnPlay, onLike: m.OnLike, actionsCell: m.ActionsCell, hoverPaused: hovered),
                ],
            };
        }
    }

    // The artist subline as inline HYPERLINK spans — one clickable link per artist (each navigates on its own), joined by
    // ", ". The engine resolves the Hand cursor over each link rect and fires its OnClick on release; the press lands on
    // this text leaf (no PressedBit) so clicking an artist navigates WITHOUT playing/selecting the row.
    // Art-forward track-list cell content for compact bound lists (artist Popular, now-playing queue). Selection,
    // tap/double-tap and keyboard behavior belong to ItemsView + SelectorVisualsBound; this builds only the shared cell.
    internal static Element ArtCard(Track t, in State st, ColumnSet set, Action<string, string?>? go,
                                    Action onPlay, Action? onLike = null, float art = 48f,
                                    bool showArtists = true, bool explicitBadge = false,
                                    bool showDuration = true, ArtCardKind kind = ArtCardKind.Rail,
                                    Action? onAdd = null, bool likePop = false, bool showMore = false,
                                    bool showArtwork = true)
    {
        // One radius for the art, not the old grid-4 / list-5 split (5 was on no ramp at all).
        const float radius = Radii.Control;
        float fab = Math.Clamp(art * 0.62f, 28f, 36f);
        var meta = new List<Element>(5);

        if (explicitBadge && t.IsExplicit) meta.Add(ExplicitBadge());
        if (set.Video && VideoPresence.HasVideo(t))
        {
            if (meta.Count > 0) meta.Add(Caption("\u00B7") with { Color = Tok.TextTertiary });
            meta.Add(Icon(Icons.Movie, 13f, Tok.TextTertiary));
        }
        if (showArtists)
        {
            if (meta.Count > 0) meta.Add(Caption("\u00B7") with { Color = Tok.TextTertiary });
            meta.Add(go is null
                ? Caption(DetailFormat.ArtistNames(t.Artists)) with { Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : ArtistLinks(t.Artists, go));
        }

        var textKids = new List<Element>(3)
        {
            WaveeType.TrackTitle(t.Title) with
            {
                Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                MaxLines = 1,
                Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f,
            },
        };
        if (meta.Count > 0)
            textKids.Add(new BoxEl { Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Children = meta.ToArray() });
        if (set.Plays)
            textKids.Add(Caption($"{t.PlayCount:N0} plays") with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        var trailing = new List<Element>(3);
        if (onAdd is not null) trailing.Add(AddButton(onAdd));   // recommendation rows: the "+" add-to-playlist button leads the trailing cluster
        if (set.Heart) trailing.Add(Heart(st.Saved, onLike, likePop));
        if (showDuration)
            trailing.Add(new BoxEl
            {
                Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = [Caption(DetailFormat.DurationCell(t.DurationMs)) with { Color = Tok.TextSecondary }],
            });
        // Trailing "…" overflow — opens the card's ancestor context menu on click (ClickRequestsContext), revealed on
        // card hover exactly like a track row. The card must carry a .WithContextMenu ancestor (ArtistPopular does).
        if (showMore) trailing.Add(MoreButton(true));

        var content = new List<Element>(2 + trailing.Count);
        if (showArtwork)
        {
            content.Add(new BoxEl
            {
                Width = art,
                Height = art,
                Shrink = 0f,
                ZStack = true,
                ClipToBounds = true,
                Corners = CornerRadius4.All(radius),
                Children =
                [
                    Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, art, art, radius,
                                     decodePx: (int)MathF.Max(64f, art * 2f)),
                    st.IsBuffering
                        ? new BoxEl { Width = art, Height = art, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Fill = WaveeOnMedia.CoverScrim, Children = [Spinner()] }
                        : NowPlayingOverlay.Create(t.Uri, onPlay, fab, cover: true, art, centered: true).Skeletonized(false),
                ],
            });
        }
        else
        {
            content.Add(new BoxEl
            {
                Width = WaveeSize.ControlH,
                Height = WaveeSize.ControlH,
                Shrink = 0f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                Role = AutomationRole.Button,
                Cursor = CursorId.Hand,
                Focusable = true,
                OnClick = onPlay,
                Children = [st.IsBuffering ? Spinner() : Icon(st.IsNow && st.IsPlaying ? Icons.Pause : Icons.Play, 14f, Tok.TextPrimary)],
            }.Interactive(Interaction.Subtle));
        }
        content.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS, Justify = FlexJustify.Center, Children = textKids.ToArray() });
        content.AddRange(trailing);

        return new BoxEl
        {
            Direction = 0,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = kind == ArtCardKind.Grid ? 64f : 52f,
            Gap = Spacing.M,   // was a 10-vs-10 ternary — one value, on the grid
            Padding = kind == ArtCardKind.Grid ? Edges4.All(Spacing.XS) : new Edges4(Spacing.XS, Spacing.XXS, Spacing.XS, Spacing.XXS),
            AlignItems = FlexAlign.Center,
            Children = content.ToArray(),
        };
    }

    internal static BoxEl ArtCardSelectSkin(in RowScope s, Element content, ArtCardKind kind, Func<bool>? showCheckbox = null)
    {
        Func<bool> isSel = s.IsSelected, isEn = s.IsEnabled;
        var interact = s.OnInteraction;
        Action<bool> focusChanged = s.OnFocusChanged;
        Element[] kids = showCheckbox is null
            ? [content]
            :
            [
                SelectorVisualsBound.BoundCheckLane(showCheckbox, isSel, interact, leftMargin: 4f),
                content,
            ];
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = kind == ArtCardKind.Grid ? 66f : 54f,
            Margin = kind == ArtCardKind.Grid ? new Edges4(0f, 1f, 0f, 1f) : new Edges4(Spacing.XS, Spacing.XXS, Spacing.XS, Spacing.XXS),
            // Same convergence as the eager row's highlight: one control rung, not 6-for-grids / 5-for-lists.
            Corners = Radii.ControlAll,
            ClipToBounds = true,
            Fill = Prop.Of(() => isSel() ? Tok.FillSubtleSecondary : ColorF.Transparent),
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            BorderWidth = 1f,
            BorderColor = ColorF.Transparent,
            HoverBorderColor = Tok.StrokeCardDefault,
            PressScale = WaveeMotion.ScaleSubtle.Press,
            Opacity = Prop.Of(() => isEn() ? 1f : ItemContainer.DisabledOpacity),
            Focusable = false,
            FocusVisualMargin = Edges4.All(1f),
            Role = AutomationRole.Button,
            OnPointerReleased = args =>
            {
                if (args.ClickCount >= 2) interact(ItemContainerTrigger.DoubleTap, args.Mods);
                else interact(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(showCheckbox?.Invoke() ?? false, args.Mods));
            },
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { interact(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(showCheckbox?.Invoke() ?? false, args.Mods)); args.Handled = true; }
            },
            OnFocusChanged = focusChanged,
            OnPointerExit = static () => { },
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center,
                    Animate = showCheckbox is null ? null : new LayoutTransition(
                        TransitionChannels.Position,
                        TransitionDynamics.Tween(333f, Easing.FluentDecelerate)),
                    Children = kids,
                },
            ],
        };
    }

    /// <summary>Tempo/key is shown only when the caller asked for it AND the pane is wide enough. It is the first
    /// column to go under pressure: a row must always keep title, duration and its transport, never a BPM readout.</summary>
    internal static bool ShowTempo(in ColumnSet set) => set.Tempo && set.Tier <= 3;

    /// <summary>"101.5 · 4A" with the Camelot-wheel colour as a leading swatch — colour carries the identity so the
    /// text stays short enough for a narrow lane. One key token only (Camelot preferred, else standard). Renders EMPTY
    /// (not "0 BPM" / "—") when the adornment has not landed: kind 222 arrives asynchronously, and a placeholder dash
    /// would flicker to a real value a moment later.</summary>
    static Element TempoCell(Track t, ColorF? factualInk = null, bool classic = false)
    {
        if (t.TempoBpm is not { } bpm || bpm <= 0d) return new BoxEl();

        var parts = new List<Element>(4);
        if (t.CamelotColor is { } argb)
            parts.Add(new BoxEl
            {
                // 6px, dimmed: a server-supplied Camelot hue is fully saturated, and at 8px opaque it out-shouted the
                // title on an otherwise quiet row. Small and slightly veiled still reads as the key's colour identity.
                // Both the 6-DIP box and its 1.5-DIP corner are deliberately BELOW their ramps' smallest rungs — this
                // is a colour SWATCH, not a surface, and a 4-DIP corner on a 6-DIP box is a circle.
                // The colour goes through DataDotInk, which is a PASSTHROUGH in dark and a hue-dependent darkening in
                // light — the wire hues were authored for a dark surface and are unreadable smears on a light row.
                Width = 6f, Height = 6f, Corners = CornerRadius4.All(1.5f), Opacity = 0.85f,
                Fill = WaveePalette.DataDotInk(argb, Tok.Theme), AlignSelf = FlexAlign.Center,
            });
        parts.Add(FactualText(DetailFormat.Bpm(bpm), classic, factualInk ?? Tok.TextSecondary));
        if (KeyLabel(t) is { Length: > 0 } key)
        {
            // Separator: two bare numeric-ish tokens ("110 7B") read as one mangled value. The middot is the same
            // metadata-joining glyph the sublines use.
            parts.Add(FactualText("·", classic, factualInk ?? Tok.TextTertiary));
            parts.Add(FactualText(key, classic, factualInk ?? Tok.TextTertiary));
        }

        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Children = parts.ToArray() };
    }

    /// <summary>One key notation: Camelot code when present (matches the swatch + filter), else standard MusicalKey.
    /// Never both — dual tokens bloated the Tempo lane and fought the narrowed track. Forwards to the SHARED formatter
    /// (<see cref="TrackExpandedFacts.KeyLabel"/>) that the drawer and the expanded row's facts strip also call.</summary>
    static string? KeyLabel(Track t) => TrackExpandedFacts.KeyLabel(t.CamelotCode, t.MusicalKey);

    /// <summary>The row's expand affordance — ONE control, both skins. Classic and Modern build the same chevron, in
    /// the same trailing lane, on the same width gate; nothing about the disclosure is skin-specific, because what it
    /// opens (the track's facts, then its versions) is a property of the TRACK.
    ///
    /// The glyph swaps rather than rotates when open, so the control states its own state rather than relying on the
    /// drawer below being visible (which it is not, once the row scrolls to the viewport edge). Focusable + Button
    /// role: Tab reaches it and Space/Enter toggles it, which is the keyboard path in either skin.</summary>
    internal static Element ExpandChevron(bool expanded, Action onToggle) => new BoxEl
    {
        Width = Spacing.XXL, Height = Spacing.XXL, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillControlSecondary,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
        FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
        OnClick = onToggle,
        BlocksDragArm = true,   // its own affordance — a press here opens the drawer, it never drags the row
        Children =
        [
            // Glyph swap rather than a rotation: icons are TEXT here, and WinUI's own Expander swaps the chevron
            // glyph for exactly this reason.
            Icon(expanded ? Icons.ChevronDown : Icons.ChevronRight, 12f,
                 expanded ? Tok.AccentTextPrimary : Tok.TextSecondary),
        ],
    };

    /// <summary>Classic's one-line Title cell. The title/media facts stay together at the leading edge while the
    /// content-rating mark is pinned to the column's trailing edge, matching the legacy desktop table. When the
    /// dedicated Artist lane folds, title + video + linked artists become one ellipsized span run instead of growing a
    /// modern metadata subline.</summary>
    static Element ClassicTitleLine(Track t, Element title, bool showArtists, bool showVideo, bool showExplicit,
                                    Action<string, string?> go, bool nowPlaying)
    {
        ColorF primary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextPrimary;
        ColorF secondary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextSecondary;
        ColorF tertiary = nowPlaying ? Tok.AccentTextPrimary : Tok.TextTertiary;
        Element leading;

        if (showArtists)
        {
            var spans = new List<TextSpan>(t.Artists.Count * 2 + 4)
            {
                new(t.Title, Weight: 600, Color: primary),
            };
            if (showVideo)
            {
                spans.Add(new TextSpan(" "));
                spans.Add(new TextSpan(Icons.Movie, Color: tertiary, FontFamily: Theme.IconFont));
            }
            if (t.Artists.Count > 0) spans.Add(new TextSpan("  ·  ", Color: tertiary));
            for (int i = 0; i < t.Artists.Count; i++)
            {
                if (i > 0) spans.Add(new TextSpan(", ", Color: secondary));
                var artist = t.Artists[i];
                string route = RouteForRef(artist.Uri, "artist:");
                spans.Add(new TextSpan(artist.Name, Color: secondary,
                    OnClick: () => go(route, artist.Name)));
            }
            leading = new SpanTextEl(spans.ToArray())
            {
                Size = 14f, LineHeight = 20f, Color = primary,
                Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Shrink = 1f, MinWidth = 0f,
            };
        }
        else
        {
            var titleKids = new List<Element>(2)
            {
                new BoxEl
                {
                    Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                    Children = [title],
                },
            };
            if (showVideo)
                titleKids.Add(Icon(Icons.Movie, 12f, tertiary) with { Shrink = 0f });
            leading = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                // This lane lives inside another flexible wrapper. It must claim that wrapper's solved width before
                // its own Basis=0 title child can grow; Shrink-only made the intrinsic width collapse to the optional
                // movie glyph, leaving every ordinary Classic title at zero width.
                Grow = 1f, Basis = 0f, MinWidth = 0f, ClipToBounds = true,
                Children = titleKids.ToArray(),
            };
        }

        var kids = new List<Element>(2)
        {
            new BoxEl
            {
                Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
                AlignItems = FlexAlign.Center, ClipToBounds = true,
                Children = [leading],
            },
        };
        if (showExplicit) kids.Add(ClassicExplicitBadge(nowPlaying ? Tok.AccentTextPrimary : null));
        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.Center, Gap = Spacing.S, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    static TextEl FactualText(string text, bool classic, ColorF color) => new(text)
    {
        Size = classic ? 14f : 12f,
        LineHeight = classic ? 20f : 16f,
        Color = color,
        Wrap = TextWrap.NoWrap,
        MaxLines = 1,
        Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>The "explicit" badge. TWO DELIBERATE EXCEPTIONS to the ramps, both documented here rather than left as
    /// bare literals:
    /// <list type="number">
    /// <item><b>10px type, below the Caption rung (12).</b> The badge is a 14-DIP box carrying a single capital, and it
    /// has to sit inline on a 16-DIP metadata line without displacing it. A 12px "E" measures ~8.6 DIPs cap-height and
    /// leaves no optical margin inside a 14px box with a 1px stroke; the ONLY way to keep the ramp here would be to grow
    /// the badge until it out-shouted the artist names it annotates. The engine's <c>InfoBadge</c> is not the escape
    /// hatch either — it renders a filled severity pill around a COUNT or a glyph, not an outlined letterform.</item>
    /// <item><b>A 2px corner, below the Radii ramp's control rung (4).</b> On a 14px box a 4px corner rounds the square
    /// into a lozenge and it stops reading as the standard explicit MARK.</item>
    /// </list>
    /// Everything else converged: 13 → 14 DIPs (the 4-grid) and the weight stays 600.</summary>
    internal static Element ExplicitBadge() => new BoxEl
    {
        MinWidth = 14f, Height = 14f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f),
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Tok.TextTertiary,
        Opacity = 0.6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl("E") { Size = 10f, LineHeight = 12f, Weight = 600, Color = Tok.TextTertiary }],
    };

    /// <summary>The legacy table's full content-rating mark. It deliberately stays separate from the compact modern
    /// single-letter badge: Classic pins this word-mark to the right edge of the Title lane.</summary>
    internal static Element ClassicExplicitBadge(ColorF? ink = null) => new BoxEl
    {
        Height = 14f, Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Shrink = 0f,
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = ink ?? Tok.TextTertiary,
        Opacity = ink is null ? 0.6f : 1f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children =
        [
            new TextEl(Loc.Get(Strings.Detail.Badge.Explicit))
            {
                Size = 9f, LineHeight = 12f, Weight = 600, Color = ink ?? Tok.TextTertiary,
            },
        ],
    };

    /// <summary>The route a metadata REF navigates to. A ref states its own kind, so the ONE route table
    /// (<see cref="RichText.RouteForUri"/>) answers it — which is how a podcast SHOW sitting in the artist/album slot of
    /// an episode row (<c>EpisodeAsTrack</c>, design §1.5) opens the show page instead of a dead album route. A ref the
    /// table cannot classify (a name-only artist with no uri) keeps the lane's own prefix, i.e. today's behaviour.</summary>
    static string RouteForRef(string uri, string fallbackPrefix) => RichText.RouteForUri(uri) ?? (fallbackPrefix + uri);

    /// <summary>The billed artists as one ellipsized run of per-artist links. <paramref name="size"/>/<paramref name="weight"/>
    /// default to the track-row metadata style (Caption 12/16); a caller with its own type ramp (the library detail
    /// pane's hero line) passes its own — including a matching <paramref name="lineHeight"/>, so the run never falls
    /// back to the shaper's natural box.</summary>
    internal static Element ArtistLinks(IReadOnlyList<ArtistRef> artists, Action<string, string?> go,
                                        float size = 12f, ushort weight = 0, float lineHeight = 0f,
                                        ColorF? color = null)
    {
        if (artists.Count == 0) return new BoxEl();
        var spans = new TextSpan[artists.Count * 2 - 1];
        int n = 0;
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = artists[i];   // fresh per-iteration capture → each link navigates to its OWN artist
            string route = RouteForRef(a.Uri, "artist:");
            spans[n++] = new TextSpan(a.Name, OnClick: () => go(route, a.Name));
        }
        return new SpanTextEl(spans)
        {
            // A run with no LineHeight falls to the shaper's natural box, which is what left the metadata lines off the
            // vertical rhythm. At the Caption default the ramp's 16 is pinned; a caller that raised the SIZE without
            // naming a line height keeps its previous natural box rather than being silently squeezed into 16.
            Size = size, LineHeight = lineHeight > 0f ? lineHeight : size <= 12f ? 16f : float.NaN,
            Weight = weight, Color = color ?? Tok.TextSecondary, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            MinWidth = 0f,   // the NoWrap names must not inflate the flexible title column
        };
    }

    // The responsive playlist/Liked metadata subline. Artist and album remain separate hyperlinks even though they share
    // one ellipsized text run; the middle-dot separator makes the compact fallback read as one deliberate metadata line.
    static Element MetadataLine(Track t, Action<string, string?> go, bool showArtists, bool showAlbum,
                                bool showExplicit, ColorF? factualInk = null)
    {
        // An EPISODE is a playable with no artists and its SHOW in the album slot (EpisodeAsTrack, design §1.5). Its
        // subtitle is therefore the show — always, not only at the compact tiers where the Album lane folds into this
        // line: dropping it would leave the row's second line empty and the row silent about which podcast it is from.
        bool episode = EntityUri.KindOf(t.Uri) == EntityKind.Episode;
        var spans = new List<TextSpan>(t.Artists.Count * 2 + 2);
        if (showArtists)
        {
            for (int i = 0; i < t.Artists.Count; i++)
            {
                if (i > 0) spans.Add(new TextSpan(", "));
                var a = t.Artists[i];
                string artistRoute = RouteForRef(a.Uri, "artist:");
                spans.Add(new TextSpan(a.Name, OnClick: () => go(artistRoute, a.Name)));
            }
        }
        if ((showAlbum || episode) && t.Album.Name.Length > 0)
        {
            if (spans.Count > 0) spans.Add(new TextSpan(" \u00B7 "));
            var album = t.Album;
            // The ref decides its own route: a show uri opens the podcast page, an album uri the album page, and a
            // uri-less ref (a name-only show/album) stays plain text rather than a link into an empty route.
            string? route = RichText.RouteForUri(album.Uri);
            spans.Add(new TextSpan(album.Name, OnClick: route is null ? null : () => go(route, album.Name)));
        }

        var kids = new List<Element>(3);
        // The row's ONE type token, in the lane the explicit badge owns (the two never co-occur — an episode files no
        // explicit mark here). It answers "why is this row in my playlist" with no new column and no second line, on
        // the eyebrow rung the rest of the app states a type on (Caption 12/16/600 + tracking, tertiary).
        if (episode)
            kids.Add(WaveeType.Eyebrow(Loc.Get(Strings.Detail.Badge.Episode)) with
            {
                Color = Tok.TextTertiary, Shrink = 0f, MaxLines = 1,
            });
        if (showExplicit) kids.Add(ExplicitBadge());
        if (spans.Count > 0)
            kids.Add(new SpanTextEl(spans.ToArray())
            {
                Size = 12f, LineHeight = 16f, Color = factualInk ?? Tok.TextSecondary, Wrap = TextWrap.NoWrap,
                Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
                Grow = 1f, Basis = 0f, MinWidth = 0f,
            });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f,
            MinWidth = 0f, ClipToBounds = true,
            Children = kids.ToArray(),
        };
    }

    // The album cell as a single clickable hyperlink (navigates to the album page).
    //
    // A row can carry a KNOWN album uri with an EMPTY name (a name-less TrackV4 album sub-message; the artist-overview
    // chart, whose wire shape has no album name at all). That rendered as a BLANK lane, which reads as broken rather than
    // as pending — so a name-less album states the absence with the same `Dash` + TextTertiary treatment the Plays and
    // Duration cells use for a not-yet-out track, never a fabricated title. The click survives whenever a uri exists
    // (opening the album is itself one of the things that hydrates its name) and is dropped when it does not — a span
    // that navigated to a bare "album:" was a dead link. The playable ladder's ref-closure post-step (blank AlbumRef scan)
    // closes the gap for liked rows that hydrate with a known album URI and an empty name — this is what the row looks
    // like until it does.
    internal static Element AlbumLink(AlbumRef album, Action<string, string?> go, ColorF? color = null,
                                      float size = 12f, float lineHeight = 16f)
    {
        bool named = album.Name.Length > 0;
        Action? open = null;
        // An episode row carries its SHOW in this ref (EpisodeAsTrack, design §1.5), so the lane routes by the ref's own
        // kind: "show:" for a podcast, "album:" for a release. RouteForUri is the ONE table this and the subline share.
        if (RichText.RouteForUri(album.Uri) is { } route)
        {
            string title = album.Name;   // captured by value — no AlbumRef held by the closure
            open = () => go(route, title.Length > 0 ? title : null);
        }
        return new SpanTextEl([new TextSpan(named ? album.Name : Dash, OnClick: open)])
        {
            Size = size, LineHeight = lineHeight, Color = color ?? (named ? Tok.TextSecondary : Tok.TextTertiary),
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1,
            Grow = 1f, Basis = 0f, MinWidth = 0f,   // yield to a squeezed Album track instead of flooring at the name's width
        };
    }

    // The Added-by cell: resolved profile when available, otherwise the raw playlist membership id.
    internal static Element AddedByCell(string? by, Owner? profile = null, ColorF? color = null, bool classic = false)
    {
        if (string.IsNullOrEmpty(by)) return new BoxEl();
        string label = profile?.Name is { Length: > 0 } name ? name : by;
        if (classic)
            return FactualText(label, classic: true, color: color ?? Tok.TextSecondary)
                with { Grow = 1f, Basis = 0f, MinWidth = 0f };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Gap = Spacing.S,
            MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                PersonPicture.Create("", Spacing.XXL, displayName: label, imageSourcePath: profile?.Avatar?.Url),
                Caption(label) with { Color = color ?? Tok.TextSecondary, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    // transitions.dev heart pop: Enter-only overshoot (spring dynamics survive the reduced-motion/easing policy in
    // SeedTerminal; tween easings on Enter legs don't). Exit stays inactive — a scrolling list must never spawn
    // exit orphans for recycled glyphs.
    static readonly LayoutTransition HeartPopIn = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.30f, 0.55f),   // low damping → the overshoot pop (BadgePop's spring)
        Enter: new EnterExit(Sx: 0.25f, Sy: 0.25f, Opacity: 0f, Active: true, Blur: Expressive.BlurSmall));

    /// <summary>Per-slot like-edge detector: true only when the SAME uri flipped unsaved→saved since this slot's last
    /// render — a recycle re-binds to a different uri, so scrolling never reports an edge (no pop replay).</summary>
    internal static bool LikeEdge(Ref<(string? Uri, bool Saved)> prev, string uri, bool saved)
    {
        bool edge = saved && !prev.Value.Saved && string.Equals(prev.Value.Uri, uri, StringComparison.Ordinal);
        prev.Value = (uri, saved);
        return edge;
    }

    // The per-row like heart: filled (accent) when the track is in the saved-set, outline otherwise; click toggles it
    // through the caller's LibraryBridge (optimistic). Null onLike (skeleton / overscan rows) → a static, non-interactive heart.
    // `pop` (a caller-detected like EDGE, see LikeEdge) attaches the overshoot Enter to the keyed glyph for that ONE
    // render; any other render — recycling included — mounts the (possibly key-changed) glyph with Animate = null → snap.
    //
    // Always painted at rest — filled when saved, outline when not. Saved-ness is a FACT the row owes the reader, and
    // hiding the outline until hover left a 40-DIP dead gutter on every unsaved row (the common case). The outline is
    // the like affordance sitting in a lane the table already reserved; it has to be there to click.
    internal static Element Heart(bool saved, Action? onLike, bool pop = false, bool classic = false)
    {
        return new BoxEl
        {
            Width = HeartCol, Height = HeartCol, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.Circle(HeartCol),
            Cursor = onLike is null ? (CursorId?)null : CursorId.Hand, OnClick = onLike,
            // Its own affordance, not a handle for dragging the row (rows are Drag.Source): without this a press on the
            // heart arms the row drag and the like never fires. Same rule as MoreButton / the queue panel's row buttons.
            BlocksDragArm = true,
            Children =
            [
                new BoxEl
                {
                    Key = saved ? "hg:on" : "hg:off",              // keyed CHILD of the stable circle (keys live in child arrays)
                    Animate = pop && saved ? HeartPopIn : null,
                    Children = [Icon(saved ? Icons.HeartFill : Icons.Heart, 14f,
                        saved ? (classic ? Tok.TextPrimary : Tok.AccentTextPrimary)
                              : (classic ? Tok.TextSecondary : Tok.TextTertiary))],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    // The trailing row "..." overflow button (Apple Music / Spotify): revealed on ROW hover — the same interactive-ancestor
    // reveal the # cell's play/pause transport uses (the recorder drives the fade off the nearest interactive ancestor, the
    // row). A click opens the SAME context menu the row shows on right-click, anchored at the button — the engine's
    // declarative BoxEl.ClickRequestsContext (input-a11y §6.5.1): a left-click / tap / Space-Enter on the button re-enters
    // the context-request funnel here, so the ancestor row's OnContextRequested opens byte-identically to a right-click,
    // with no OnRealized node capture, no InputHooks, no re-hit-test. `enabled: false` → a static, non-interactive, hidden
    // placeholder (skeleton / overscan) so the shimmer derives the identical reserved lane.
    internal static Element MoreButton(bool enabled, bool classic = false)
    {
        var btn = new BoxEl
        {
            Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = classic ? CornerRadius4.All(Radii.None) : Radii.Circle(28f),
            HoverScale = classic ? 1f : WaveeMotion.ScaleEmphatic.Hover,
            PressScale = classic ? 1f : WaveeMotion.ScaleEmphatic.Press,
            Cursor = enabled ? CursorId.Hand : (CursorId?)null, ClickRequestsContext = enabled,
            Role = AutomationRole.Button,
            BlocksDragArm = true,   // its own affordance — a press here opens the menu, it never drags the row
            Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
        };
        Element button = classic ? btn : btn.Interactive(Interaction.Subtle);
        // QUIET at rest, full on row hover (inherited from the row's hover progress). Fully hidden at rest was the
        // discoverability half of "adding to playlist is unclear" (user report 2026-08-10): every per-row verb — add to
        // playlist, go to album, share, remove — lives behind this glyph, and a control that does not exist until you
        // happen to point at the row cannot be found. 0.45 states "there is a menu on every row" without competing with
        // the title; a fully opaque "…" on 1500 rows is real scanning cost, which is why this is a rung and not a flip.
        // A disabled placeholder (skeleton / overscan) stays invisible so the shimmer reserves the lane without drawing.
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Opacity = enabled ? (classic ? 0f : MoreRestOpacity) : 0f,
            HoverOpacity = enabled ? 1f : 0f,
            Children = [button],
        };
    }

    /// <summary>The at-rest opacity of a row's trailing "…" — quiet enough not to compete with the title, present enough
    /// that the row's verbs are discoverable without hovering. The one calibration knob for row-menu discoverability.</summary>
    internal const float MoreRestOpacity = 0.45f;

    // The recommendation-row "add to this playlist" button (Spotify's playlist-extender "+"): a bordered round button that
    // leads the trailing cluster, before the duration. Mirrors Heart — a null onAdd yields a non-interactive button.
    internal static Element AddButton(Action? onAdd) => new BoxEl
    {
        Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = Radii.Circle(28f), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        HoverScale = WaveeMotion.ScaleEmphatic.Hover, PressScale = WaveeMotion.ScaleEmphatic.Press,
        Cursor = onAdd is null ? (CursorId?)null : CursorId.Hand, OnClick = onAdd,
        BlocksDragArm = true,   // its own affordance — see MoreButton
        Children = [Icon(Icons.Add, 15f, Tok.TextPrimary)],
    }.Interactive(Interaction.Subtle);

    /// <summary>Video lane that doubles as the row More affordance: film icon at rest when this track HAS a video, else
    /// the quiet "…" — then the full-strength "…" on row hover either way. Same interactive-ancestor HoverOpacity swap as
    /// <see cref="NumberCell"/> — no circular chrome on the ellipsis (the dedicated <see cref="MoreButton"/> keeps that
    /// look for Actions-only surfaces). Click raises <c>ClickRequestsContext</c> so the row context menu opens anchored
    /// at this cell.
    /// <para>FACTS AT REST, ACTIONS ON HOVER: a video is a property of the track, so it wins the at-rest slot. A row
    /// WITHOUT one has nothing to state there, so the lane spends it on the quiet menu glyph instead of rendering empty —
    /// the whole lane is reserved on every row as soon as any track in the list has a video, and leaving most of those
    /// rows blank is what made the row menu undiscoverable on exactly the album pages that report it.</para></summary>
    internal static Element VideoMoreCell(bool hasVideo, bool moreEnabled)
    {
        Element rest = hasVideo ? Icon(Icons.Movie, 13f, Tok.TextTertiary) : new BoxEl();
        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    HoverOpacity = 0f, Children = [rest],
                },
                new BoxEl
                {
                    Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    // Only a row that has no film to show spends its resting slot on the quiet menu glyph.
                    Opacity = moreEnabled && !hasVideo ? MoreRestOpacity : 0f,
                    HoverOpacity = moreEnabled ? 1f : 0f,
                    Cursor = moreEnabled ? CursorId.Hand : (CursorId?)null,
                    ClickRequestsContext = moreEnabled,
                    Role = AutomationRole.Button,
                    BlocksDragArm = true,   // its own affordance — see MoreButton
                    Children = [Icon(Icons.More, 16f, Tok.TextSecondary)],
                },
            ],
        };
    }

    // The # cell — a small state machine over the playback of THIS track, with the transport button revealed on row hover:
    //   • fetching/buffering → a spinner (shown whether or not you're hovering);
    //   • now-playing + playing → a LIVE animated equalizer at rest, the PAUSE button on hover;
    //   • now-playing + paused  → a settled equalizer at rest, the PLAY button on hover;
    //   • album top track       → the star at rest, the PLAY button on hover;
    //   • otherwise             → the track number at rest, the PLAY button on hover.
    // The number/equalizer layer fades OUT on row hover and the transport layer fades IN — the recorder drives both off
    // the nearest interactive ancestor (the row), so the reveal follows ROW hover, and survives the pointer crossing onto
    // the button. The transport layer is itself the SINGLE-CLICK target (its OnClick + hand cursor); the inner glyph
    // PressScale-pushes on press for a real button feel.
    /// <param name="hoverPaused">Row/card hover signal that also drives this cell's HoverOpacity fade — pause the EQ
    /// while invisible. Must come from the interactive ancestor (not the bars' own hit target).</param>
    /// <param name="ctx">The page's ambient accent (<see cref="WaveeAccentCtx"/>), read by the CALLER — this is a plain
    /// static helper, not a Component, so it cannot call UseContext itself. Null (what a caller on any page other than
    /// Recents has to pass) is a pure no-op: the equalizer keeps its ordinary <c>Tok.AccentTextPrimary</c>.</param>
    /// <param name="chart">A chart playlist's per-row rank movement (null off a non-chart list). Rendered as a small
    /// glyph beside the plain track-number rest state ONLY — the equalizer/star/spinner states already say something
    /// about this row, so the movement glyph does not compete with them for the same few pixels.</param>
    internal static Element NumberCell(int index, bool isNow, bool isPlaying, bool isBuffering, bool isTop,
                                       Action? onPlay = null, IReadSignal<bool>? hoverPaused = null,
                                       IReadSignal<PageAccent>? ctx = null, ChartEntry? chart = null,
                                       bool classic = false)
    {
        ColorF accent = Tok.AccentTextPrimary;
        Element number = Caption((index + 1).ToString()) with { Color = Tok.TextTertiary };
        Element rest = classic
            ? isBuffering ? Spinner()
              : isNow ? Icon(Icons.Volume, 13f, accent)
              : new BoxEl()
            : isBuffering ? Spinner()
              : isNow ? WaveeEqualizer.Of(isPlaying, () => ctx is {} a ? a.Value.Ink : Tok.AccentTextPrimary, paused: hoverPaused)
              : isTop ? Icon(Icons.FavoriteStarFill, 11f, accent)
              : ChartGlyph(chart) is { } glyph
                  ? new BoxEl { Direction = 0, Gap = 2f, AlignItems = FlexAlign.Center, Children = [number, glyph] }
                  : number;
        Element transport = isBuffering
            ? Spinner()
            : new BoxEl
            {
                Width = 24f, Height = 24f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                PressScale = WaveeMotion.ScaleEmphatic.Press,   // a real button press-push (the row-driven reveal is the hover cue)
                Children = [Icon(isNow && isPlaying ? Icons.Pause : Icons.Play, 12f, isNow ? accent : Tok.TextPrimary)],
            };
        return new BoxEl
        {
            ZStack = true,
            Children =
            [
                new BoxEl { Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f, Children = [rest] },
                new BoxEl
                {
                    Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Opacity = 0f, HoverOpacity = 1f,
                    OnClick = onPlay, Cursor = onPlay is null ? (CursorId?)null : CursorId.Hand,
                    Children = [transport],
                },
            ],
        };
    }

    // The chart rank-movement glyph beside the # cell's plain number: a green up-triangle / red down-triangle / a
    // compact "NEW" label / nothing at all (Equal or an Unknown status this client has never seen — never a guessed
    // arrow). Plain Unicode glyphs rather than the icon font: the movement is a tiny, three-way fact next to a number,
    // not an action, and it keeps this cell out of the shared Icons glyph table entirely. Constant literals only — no
    // per-render string concat, matching the row's existing zero-alloc-in-hot-cells discipline.
    static Element? ChartGlyph(ChartEntry? chart) => chart?.Status switch
    {
        ChartEntryStatus.Up => new TextEl("▲") { Size = 8f, LineHeight = 12f, Color = Tok.SystemFillSuccess, Weight = 700 },
        ChartEntryStatus.Down => new TextEl("▼") { Size = 8f, LineHeight = 12f, Color = Tok.SystemFillCritical, Weight = 700 },
        ChartEntryStatus.New => new TextEl("NEW") { Size = 8f, LineHeight = 12f, Color = Tok.SystemFillSuccess, Weight = 700 },
        _ => null,   // Equal / Unknown — no glyph, never a guess
    };

    // The indeterminate fetch/buffer spinner (WinUI ProgressRing). The now-playing equalizer is the shared WaveeEqualizer.
    internal static Element Spinner() => ProgressRing.Indeterminate(size: 16f, foreground: Tok.AccentTextPrimary);

    // ── cell wrappers (the cell fills its grid rect; these vertical-center + horizontally place the content) ──
    // Every column cell is a CLIPPED, fully-shrinkable box. Both halves are load-bearing whenever the grid is handed
    // less width than its fixed tracks need (FlexLayout.ResolveColumns scales the fixed tracks and resolves Star to 0):
    // MinWidth=0 lets the cell's content actually yield instead of flooring at its natural min, and ClipToBounds bounds
    // whatever still overflows to its own track — without it a squeezed cell paints straight over its neighbours (the
    // rail-open pile-up: title, artist and album stacked on the same pixels).
    internal static Element CenterCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinWidth = 0f, ClipToBounds = true, Children = [content] };
    internal static Element LeftCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, MinWidth = 0f, ClipToBounds = true, Children = [content] };
    internal static Element EndCell(Element content) =>
        new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f, ClipToBounds = true, Children = [content] };
}
