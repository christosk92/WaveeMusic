using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Album / single trailing sections, appended below the eager track rows in the outer scroller.
// The enrichment loads behind one route-keyed async boundary so the trailing area shows one coherent skeleton and then
// fades in as a whole. Individual enrichment calls still fail soft inside the aggregate and simply omit their section.
//
// RESERVE FROM MOUNT (one swap per album). This block used to arrive in three jolts: a ZERO-height box while the album
// model was still Pending, then a several-hundred-DIP skeleton INSERTED the moment the tracks landed (page extent +
// scrollbar jump), then a snap-resize to the real sections. There is exactly ONE region now, shimmering from the first
// frame and easing (SmoothResize) into whatever it resolves to — real sections, or the collapsed empty box.
sealed class AlbumTrailing : Component
{
    readonly Loadable<DetailModel> _full;   // read reactively → loaders re-fire when the full album lands / the route swaps
    readonly Signal<Route> _route;
    readonly DetailHandlers _h;
    public AlbumTrailing(Loadable<DetailModel> full, Signal<Route> route, DetailHandlers h)
    { _full = full; _route = route; _h = h; }

    sealed record AlbumTrailingData(
        Artist? About,
        IReadOnlyList<Artist> Fans,
        IReadOnlyList<PlaylistSummary> Featured,
        IReadOnlyList<MerchItem> Merch,
        IReadOnlyList<Album> Similar)
    {
        public static readonly AlbumTrailingData Empty = new(
            null,
            Array.Empty<Artist>(),
            Array.Empty<PlaylistSummary>(),
            Array.Empty<MerchItem>(),
            Array.Empty<Album>());
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var acts = UseContext(ActionServices.Slot);   // only for the trailing shelves' DRAG payloads (resolver + rootlist)
        var m = _full.Value.Value;                 // subscribe → re-render preview→full (the loaders re-key on `ready`)
        var tracks = m.Tracks;
        bool ready = tracks.Count > 0;             // the album fetch completed (hero + tracks are in) → seed the loaders
        // The album fetch's OWN state, subscribed: the TERMINATOR for the reserve window below. Without it an album that
        // resolves with zero tracks (`ready` can then never flip) would shimmer here forever.
        bool albumSettled = _full.State.Value != (byte)LoadState.Pending;
        // Hold the region in its Pending (shimmer) branch while the album itself is still in flight — the enrichment
        // resource cannot even be seeded yet, and a zero-height box here is what made the skeleton INSERT itself later.
        bool reserving = !ready && !albumSettled;
        string albumUri = m.ContextUri ?? "";
        string leadArtistUri = m.Artists.Count > 0 ? m.Artists[0].Uri : "";
        string leadTrackUri = ready ? tracks[0].Uri : "";
        string seedTrackUri = SeedTrack(tracks);   // highest play-count track (fallback: track 0) — Spotify's similar seed
        bool shortRelease = m.ReleaseKind == AlbumKind.Single || tracks.Count is > 0 and <= 2;
        // Re-fire EVERY loader when the full model lands (`:p`→`:r`) or the route swaps on a reused instance. The engine
        // cancels the in-flight run and reseeds to empty on a key change (route-keyed cancellation / no stale flash).
        object key = _route.Value.Name + (ready ? ":r" : ":p");

        var trailing = UseResource(
            ct => LoadTrailingAsync(svc, ready, shortRelease, leadArtistUri, leadTrackUri, albumUri, seedTrackUri, ct),
            AlbumTrailingData.Empty, (string)key).Loadable;

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children =
            [
                // Constructed directly rather than through Skel.Region because the Pending predicate has to fold in
                // `reserving` — the region must shimmer from mount, ACROSS the `:p`→`:r` re-key (which reseeds the
                // resource to Pending), and swap exactly once. `reserving` short-circuits the State read on purpose: in
                // that window nothing the resource does may change the branch, and the window can only be left by a
                // parent re-render (both `ready` and `albumSettled` come from `_full`), which force-reruns this effect.
                new SkelRegionEl(
                    Pending: () => reserving || trailing.State.Value == (byte)LoadState.Pending,
                    Failed: () => !reserving && trailing.State.Value == (byte)LoadState.Failed,
                    Content: () =>
                    {
                        var data = trailing.Value.Value;
                        return HasTrailingSections(m, data, shortRelease)
                            ? TrailingSections(m, data, shortRelease, _h, acts)
                            : new BoxEl();   // nothing to show → collapse (the region EASES down to zero)
                    },
                    ShimmerSource: TrailingSkeleton,
                    OnFailed: () => new BoxEl(),
                    Reveal: SkelReveal.FadeOnly,
                    Style: SkeletonStyle.Default,
                    Group: key,
                    // Ease skeleton height → real height (or → the collapsed empty box) instead of snapping the page
                    // extent and the scrollbar. Read at MOUNT (MountSkeletonRegion), which is why the region is now
                    // mounted from the first frame rather than replacing a zero-height box later.
                    SmoothResize: true),
            ],
        };
    }

    static Element TrailingSections(DetailModel m, AlbumTrailingData data, bool shortRelease, DetailHandlers h,
                                    ActionServices? acts)
    {
        var sections = new List<Element>(8);

        if (shortRelease && m.HasVideo)
            sections.Add(WatchVideoSection(m, h));

        if (data.About is { } about)
            sections.Add(AboutArtistSection(about, h));

        if (data.Fans.Count > 0)
            sections.Add(Section(Loc.Get(Strings.Detail.FansAlsoLike), FansRow(data.Fans, h)));

        var moreBy = m.MoreByArtist is { Count: > 0 } mb ? mb
            : data.About?.TopAlbums is { Count: > 0 } ta ? ta : null;
        if (moreBy is { Count: > 0 } && m.Artists.Count > 0)
            sections.Add(AlbumList(Strings.Detail.MoreBy(m.Artists[0].Name), moreBy, h, acts));

        if (data.Featured.Count > 0)
            sections.Add(FeaturedSection(data.Featured, h, acts));

        if (data.Merch.Count > 0)
            sections.Add(MerchSection(data.Merch));

        if (data.Similar.Count > 0)
            sections.Add(AlbumList(Loc.Get(Strings.Detail.SimilarAlbums), data.Similar, h, acts));

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children = sections.ToArray(),
        };
    }

    static bool HasTrailingSections(DetailModel m, AlbumTrailingData data, bool shortRelease)
    {
        if (shortRelease && m.HasVideo) return true;
        if (data.About is not null || data.Fans.Count > 0 || data.Featured.Count > 0 || data.Merch.Count > 0 || data.Similar.Count > 0)
            return true;
        if (m.MoreByArtist is { Count: > 0 } && m.Artists.Count > 0) return true;
        return data.About?.TopAlbums is { Count: > 0 } && m.Artists.Count > 0;
    }

    static async Task<AlbumTrailingData> LoadTrailingAsync(Services? svc, bool ready, bool shortRelease,
        string leadArtistUri, string leadTrackUri, string albumUri, string seedTrackUri, System.Threading.CancellationToken ct)
    {
        if (svc is null || !ready) return AlbumTrailingData.Empty;

        var about = leadArtistUri.Length > 0 && leadTrackUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetAboutArtistAsync(leadArtistUri, leadTrackUri, ct2), (Artist?)null, ct)
            : Task.FromResult<Artist?>(null);
        var fans = Safe(ct2 => FansAsync(svc, ready, shortRelease, leadArtistUri, seedTrackUri, ct2),
            (IReadOnlyList<Artist>)Array.Empty<Artist>(), ct);
        // The album FULL rung (getAlbum: label / ©℗ / OtherVersions / MoreBy / detailed artists) is what powers
        // "About this release" — and this pane is the only thing that needs it, so it is asked for HERE, in the
        // background, off the interactive open path. The enrichment service no longer back-calls LiveSessionHost for it.
        if (albumUri.Length > 0)
            _ = svc.Hydrator.EnsureAsync(albumUri, HydrationLevel.Full,
                new HydrationOptions(HydrationMode.Background, Surface: TraitSurface.AlbumOpen), ct);
        var featured = albumUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetRecommendedPlaylistsAsync(albumUri, ct2),
                (IReadOnlyList<PlaylistSummary>)Array.Empty<PlaylistSummary>(), ct)
            : Task.FromResult<IReadOnlyList<PlaylistSummary>>(Array.Empty<PlaylistSummary>());
        var merch = albumUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetMerchAsync(albumUri, ct2),
                (IReadOnlyList<MerchItem>)Array.Empty<MerchItem>(), ct)
            : Task.FromResult<IReadOnlyList<MerchItem>>(Array.Empty<MerchItem>());
        var similar = seedTrackUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetSimilarAlbumsAsync(seedTrackUri, 24, ct2),
                (IReadOnlyList<Album>)Array.Empty<Album>(), ct)
            : Task.FromResult<IReadOnlyList<Album>>(Array.Empty<Album>());

        await Task.WhenAll(about, fans, featured, merch, similar).ConfigureAwait(false);
        return new AlbumTrailingData(await about.ConfigureAwait(false), await fans.ConfigureAwait(false),
            await featured.ConfigureAwait(false), await merch.ConfigureAwait(false), await similar.ConfigureAwait(false));
    }

    static async Task<T> Safe<T>(Func<System.Threading.CancellationToken, Task<T>> read, T fallback, System.Threading.CancellationToken ct)
    {
        try { return await read(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return fallback; }
    }

    // "About this release" — the album facts as a COMPACT bento (Songs / Length / Released / Label tiles, the wrap-grow
    // grid from the artist Profile facts but denser), with the legal lines (Courtesy / Copyright) as a small footnote.
    // Always present for an album with tracks (the Songs/Length tiles), hence the Tracks check.
    internal static bool HasReleasePanel(DetailModel m) =>
        m.Tracks.Count > 0 || m.ReleaseDate is { Length: > 0 } || m.Label is { Length: > 0 } || m.Copyright is { Length: > 0 } ||
        m.CourtesyLine is { Length: > 0 } || m.OtherVersions is { Count: > 0 };

    internal static Element ReleasePanel(DetailModel m, DetailHandlers h, bool outerPadding = true)
    {
        var children = new List<Element>(4);
        if (m.OtherVersions is { Count: > 0 } ov) children.Add(OtherVersionsDropDown(ov, h));

        var tiles = AlbumFactTiles(m);
        var notes = ReleaseNotes(m);
        // The facts ARRIVE IN WAVES — Songs/Length with the tracks, Released with the full model, Label/©/℗/other
        // versions with the publishing hydration a moment later — so every piece here is keyed and carries its own
        // entrance: a new tile fades up (staggered left→right), its siblings FLIP into place, a re-labelled value
        // cross-swaps (see CompactStatTile), a note fades in. Reduced motion is a VALUE (the stagger reads it; the
        // fades ride the engine's KeepFade policy), never a branch that changes what is authored.
        float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;
        if (tiles.Length > 0 || notes.Length > 0)
        {
            var body = new List<Element>(3)
            {
                WaveeType.Eyebrow("About this release") with { Color = Tok.TextTertiary },
            };
            // The bento: a wrap row of compact fact tiles. Wrap-grow (FlexLayout.ArrangeWrap) fills each line edge-to-edge,
            // so the tiles flow 2×2 in a narrow rail and a single row when wide — no ragged gaps.
            if (tiles.Length > 0) body.Add(new BoxEl { Key = "release-facts", Direction = 0, Gap = Spacing.S, Wrap = true, Stagger = stagger, Children = tiles });
            if (notes.Length > 0) body.Add(new BoxEl { Key = "release-notes", Direction = 1, Gap = 3f, Stagger = stagger, Children = notes });
            children.Add(new BoxEl { Key = "release-about", Direction = 1, Gap = Spacing.M, Layout = DetailRail.Shove, Children = body.ToArray() });
        }

        if (children.Count == 0) return new BoxEl();
        // The panel owns its ENTRANCE in every arm that mounts it (rail, compact header, the vertical arm's trailing
        // body): one fade-up as it appears, then it FLIPs when siblings shove it. Callers must not wrap it in a second
        // entrance (DetailRail uses Row(...), not LateRow(...), for exactly this reason).
        return new BoxEl
        {
            Key = "release-panel", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Direction = 1, Gap = Spacing.M,
            Padding = outerPadding ? new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.L) : Edges4.All(0f),
            Children = children.ToArray(),
        };
    }

    // Songs / Length / Released / Label — each present-only (a missing fact drops its tile). Songs+Length come from the
    // track list; Released prefers the formatted date, else the year; Label is the record label.
    // TODO(loc): the neighbouring literals are hardcoded English throughout this file.
    static Element[] AlbumFactTiles(DetailModel m)
    {
        var stats = new List<(string Value, string Label, string Key)>(4);
        if (m.Tracks.Count > 0)
        {
            // On a PARTLY released album the plain count and the summed length both lie: the count includes tracks that
            // are not out, and the length silently omits their unknown durations, so "12 songs · 31 min" describes a
            // record that does not exist yet. Report what is actually out, and measure only that.
            int outNow = 0;
            long ms = 0;
            for (int i = 0; i < m.Tracks.Count; i++)
            {
                if (m.Tracks[i].IsNotYetOut()) continue;
                outNow++;
                ms += m.Tracks[i].DurationMs;
            }
            stats.Add((outNow == m.Tracks.Count
                ? m.Tracks.Count.ToString()
                : outNow + " of " + m.Tracks.Count, "Songs", "songs"));
            if (ms > 0) stats.Add((DetailFormat.TotalTime(ms), "Length", "length"));
        }
        string? released = m.ReleaseDate is { Length: > 0 } rd ? rd : m.Year;
        // The tense comes from the FACT, not from the countdown: a partly-released album has a release date in the past
        // AND a countdown to its next track in the future at the same time, so gating this on UpcomingAt would relabel
        // an album that is already partly out as one that has not happened.
        bool future = m.ReleaseInstant is { } ri && ri > DateTimeOffset.UtcNow;
        if (released is { Length: > 0 })
            stats.Add((released, Loc.Get(future ? Strings.Detail.FactReleases : Strings.Detail.FactReleased), "released"));
        if (m.Label is { Length: > 0 } lb) stats.Add((lb, "Label", "label"));
        var tiles = new Element[stats.Count];
        for (int i = 0; i < stats.Count; i++) tiles[i] = CompactStatTile(stats[i].Value, stats[i].Label, stats[i].Key);
        return tiles;
    }

    // The legal footnote (Courtesy + Copyright). Copyright joins several notices with '\n'; the text engine now renders a
    // hard break as a real line break (LineBreaker.IsHardBreak / TextLayoutEngine), so each notice flows onto its own line
    // with no [] tofu — the newline no longer needs splitting at the app layer.
    static Element[] ReleaseNotes(DetailModel m)
    {
        var notes = new List<Element>(2);
        if (m.CourtesyLine is { Length: > 0 } courtesy) notes.Add(NoteText("note:courtesy", courtesy));
        if (m.Copyright is { Length: > 0 } cp) notes.Add(NoteText("note:copyright", cp));
        return notes.ToArray();
    }

    // Keyed + boxed (Enter/Layout bake on BoxEl only): a note that lands with the publishing hydration fades in under the
    // tiles instead of popping, and the notes column's Stagger offsets © from ℗.
    static Element NoteText(string key, string value) => new BoxEl
    {
        Key = key, Direction = 1, Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
        Children =
        [
            new TextEl(value) { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    // A compact fact tile (denser than the artist Profile-facts tile: 18px value, tighter padding). Content-width base +
    // Grow=1 so the wrap-grow row fills; a long Label keeps its own width (base = content) and never clips short tiles.
    // Arrival motion: the tile is KEYED by its fact (Songs/Length/Released/Label), fades up when it first appears (the
    // parent row staggers siblings), and takes a Position+Size FLIP so a tile landing later reflows the wrap-grow row
    // smoothly instead of snapping the others narrower. The VALUE line is the house text-swap (PlaylistInlineEdit's
    // status pill): the value text sits in a value-keyed box inside a ZStack, so "2025" → "May 2, 2025" rises and blurs
    // out while the new value enters from below — never an in-place relabel.
    static readonly LayoutTransition TileReflow = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        SizeMode.Reveal);

    static Element CompactStatTile(string value, string label, string key) => new BoxEl
    {
        Key = "fact:" + key, Enter = DetailRail.FadeUp, Layout = TileReflow,
        Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f,
        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
        Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            ZStack(new BoxEl
            {
                Key = "v:" + value,
                Animate = MotionRecipes.TextSwap,
                Children = [new TextEl(value) { Size = 18f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            }),
            new TextEl(label) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static Element OtherVersionsDropDown(IReadOnlyList<Album> versions, DetailHandlers h)
    {
        var items = new MenuFlyoutItem[versions.Count];
        for (int i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            items[i] = new MenuFlyoutItem(VersionLabel(v), Icons.MusicNote,
                Invoke: () => h.OpenAlbum(v));
        }
        return new BoxEl
        {
            // Lands with the publishing hydration, after the tiles: keyed + fades up like the rest of the panel.
            Key = "release-versions", Enter = DetailRail.FadeUp, Layout = DetailRail.Shove,
            Direction = 0, Padding = new Edges4(0f, 2f, 0f, 2f),
            Children = [new BoxEl { Grow = 1f, Children = [DropDownButton.Create("Other versions", items, Icons.MusicNote)] }],
        };
    }

    static string VersionLabel(Album a)
    {
        var parts = new List<string>(3) { a.Name };
        if (a.Year > 0) parts.Add(a.Year.ToString());
        parts.Add(a.Kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        });
        return string.Join(" · ", parts);
    }

    // "Fans also like": one loadable regardless of release type. A short release reads the LEAD TRACK's related artists
    // (the same getTrack that carries the video signal); a full album reads the ARTIST's related artists (overview).
    static async Task<IReadOnlyList<Artist>> FansAsync(Services? svc, bool ready, bool shortRelease, string leadArtistUri, string seedTrackUri, System.Threading.CancellationToken ct)
    {
        if (svc is null || !ready) return Array.Empty<Artist>();
        if (shortRelease && seedTrackUri.Length > 0)
        {
            var ctx = await svc.AlbumEnrichment.GetTrackContextAsync(seedTrackUri, ct).ConfigureAwait(false);
            return ctx?.RelatedArtists ?? Array.Empty<Artist>();
        }
        return leadArtistUri.Length == 0
            ? Array.Empty<Artist>()
            : await svc.AlbumEnrichment.GetRelatedArtistsAsync(leadArtistUri, ct).ConfigureAwait(false);
    }

    // The similar-albums seed: the highest play-count track (the album's "hit"), falling back to the first track.
    static string SeedTrack(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return "";
        int best = 0;
        for (int i = 1; i < tracks.Count; i++)
            if (tracks[i].PlayCount > tracks[best].PlayCount) best = i;
        return tracks[best].Uri;
    }

    /// <summary>Does any track on this short release play a USER-ATTACHED video? An override always wins over the
    /// source's own video, so the section's label must follow it (a short release is 1–2 tracks — a linear scan).</summary>
    static bool HasCustomVideo(DetailModel m)
    {
        var tracks = m.Tracks;
        for (int i = 0; i < tracks.Count; i++)
            if (VideoPresence.HasOverride(tracks[i].Uri)) return true;
        return false;
    }

    // "Watch the official video" (or "the custom video") — a thumbnail (the cover) with a centered play badge + the
    // release label/meta.
    static Element WatchVideoSection(DetailModel m, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.M, Spacing.M, Spacing.L, Spacing.M),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                HoverFill = Tok.FillCardDefault, OnClick = () => h.PlayContext(m.ContextUri ?? ""),
                Children =
                [
                    new BoxEl
                    {
                        Width = 200f, Height = 116f, Shrink = 0f, ZStack = true,
                        Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                        Children =
                        [
                            Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, 200f, 116f, Radii.Control),
                            new BoxEl
                            {
                                Width = 200f, Height = 116f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Width = 44f, Height = 44f, Corners = CornerRadius4.All(22f), Fill = Tok.AccentDefault,
                                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                        Children = [Icon(Icons.Play, 16f, Tok.TextOnAccentPrimary)],   // theme-aware (black on accent in dark)
                                    },
                                ],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XS,
                        Children =
                        [
                            WaveeType.Eyebrow(Loc.Get(HasCustomVideo(m) ? Strings.VideoOverride.CustomLabel : Strings.Detail.WatchOfficialVideo))
                                with { Color = Tok.TextTertiary },
                            WaveeType.RailHeader(m.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(m.MetaLine) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            },
        ],
    };

    // "Fans also like" — a clipped row of artist chips (avatar + name).
    static Element FansRow(IReadOnlyList<Artist> fans, DetailHandlers h) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, ClipToBounds = true,
        Children = fans.Take(8).Select(a => ArtistChip(a, h)).ToArray(),
    };

    static Element ArtistChip(Artist a, DetailHandlers h) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, Shrink = 0f, Height = 48f,
        Padding = new Edges4(Spacing.S, 0f, Spacing.L, 0f),
        Corners = CornerRadius4.All(24f), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => h.Go("artist:" + a.Uri, a.Name), Cursor = CursorId.Hand,
        Children =
        [
            new BoxEl
            {
                Width = 32f, Height = 32f, Corners = CornerRadius4.All(16f), ClipToBounds = true,
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 32f, 32f, 16f)],
            },
            new TextEl(a.Name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    // ── the trailing sections are VERTICAL, on purpose ───────────────────────────────────────────────────────────────
    //
    // "Featured on", "More by", "Similar albums" and "Merch" used to be four horizontal `PagedShelf`es stacked inside the
    // detail page's own VERTICAL scroller. That is nested-axis scrolling: the user is already panning this column
    // downward, and each shelf asks them to switch to a horizontal gesture (and to hit a pager) to see the rest of a
    // list whose items are then only half-visible. It also means a shelf's card width — and therefore how many items
    // the section shows at all — is decided by the rail's current width, so the same album page shows 3 or 6 "more by"
    // entries depending on an unrelated splitter position.
    //
    // The trailing column now scrolls ONE way. Each section is a short vertical stack of compact rows, capped, with a
    // quiet "Show all" link that expands it IN PLACE (no route, no second surface — everything is already loaded).
    // The section headers are unchanged.
    //
    // Row form per content type:
    //   · albums + playlists → `MediaCard.Row` (48-DIP cover, title, subtitle, hover play FAB, drag source, menu) — the
    //     app's existing compact media row, so these read exactly like a search "All" list;
    //   · merch → a bespoke `MerchRow`, because merch has no play affordance and its second line is a PRICE, which is
    //     the row's call to action rather than a subtitle.
    // The cap itself lives on TrailingStack (which enforces it) — five rows is the point where a section is still a
    // glance rather than a second track list stacked under the first one.
    //
    // "Featured on" — the playlists this album appears on.
    // `acts` is threaded in (DetailHandlers carries only Actions) purely so these rows can be DRAG SOURCES: the
    // payload's track resolver and its rootlist lookup both need the composition root.
    static Element FeaturedSection(IReadOnlyList<PlaylistSummary> pls, DetailHandlers h, ActionServices? acts)
        => TrailingSection(Loc.Get(Strings.Detail.FeaturedOn), pls.Count,
            i => MediaCard.Row(pls[i].Cover, pls[i].Name, pls[i].OwnerName, pls[i].Uri, circular: false,
                onClick: () => h.OpenPlaylist(pls[i]), onPlay: () => h.PlayContext(pls[i].Uri),
                drag: Drag.Source(WaveeDragKinds.Resource,
                    () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Playlist, pls[i].Uri, pls[i].Name,
                                                             pls[i].Cover, acts))),
            signature: "featured:" + pls.Count + ":" + (pls.Count > 0 ? pls[0].Uri : ""));

    // More-by / Similar albums — open the album, or play it from the row's hover FAB.
    static Element AlbumList(string header, IReadOnlyList<Album> albums, DetailHandlers h, ActionServices? acts)
        => TrailingSection(header, albums.Count,
            i => MediaCard.Row(albums[i].Cover, albums[i].Name, AlbumSubtitle(albums[i]), albums[i].Uri, circular: false,
                onClick: () => h.OpenAlbum(albums[i]), onPlay: () => h.PlayContext(albums[i].Uri),
                drag: Drag.Source(WaveeDragKinds.Resource,
                    () => WaveeResourceDragPayload.ForEntity(WaveeResourceKind.Album, albums[i].Uri, albums[i].Name,
                                                             albums[i].Cover, acts))),
            signature: header + ":" + albums.Count + ":" + (albums.Count > 0 ? albums[0].Uri : ""));

    /// <summary>The shared section wrapper: the page's trailing padding around a capped, expandable stack.
    /// <paramref name="signature"/> keys the stateful stack so a re-bound section (the model going preview→full, or a
    /// route swap onto a reused instance) remounts with the new data — a Component freezes its ctor args at mount.</summary>
    static Element TrailingSection(string title, int count, Func<int, Element> rowAt, string signature) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.L),
        Children = [Embed.Comp(() => new TrailingStack(title, count, rowAt)) with { Key = "trail:" + signature }],
    };

    // Album card subtitle: the artist (similar albums carry their own artist), else the year, else the kind badge.
    static string AlbumSubtitle(Album a) =>
        a.Artists.Count > 0 ? a.Artists[0].Name
        : a.Year > 0 ? a.Year.ToString()
        : Loc.Get(Strings.Detail.Badge.Album);

    // "Merch" — product rows (thumb + name + price). The row opens the external shop in the OS browser.
    static Element MerchSection(IReadOnlyList<MerchItem> merch)
        => TrailingSection(Loc.Get(Strings.Artist.Merch), merch.Count, i => MerchRow(merch[i]),
            signature: "merch:" + merch.Count + ":" + (merch.Count > 0 ? merch[0].Name : ""));

    /// <summary>One merch row. Deliberately NOT <see cref="MediaCard.Row"/>: merch has nothing to play (that row's whole
    /// second affordance is a hover play FAB over the art) and its second line is a PRICE — the row's call to action,
    /// which therefore sits trailing in the accent link ink rather than under the title as a subtitle.</summary>
    static Element MerchRow(MerchItem item)
    {
        const float Thumb = WaveeSize.Thumb48;
        string? url = item.ShopUrl is { Length: > 0 } u ? u : null;
        return new BoxEl
        {
            Direction = 0, Height = 64f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.CardAll,
            Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverScale = WaveeMotion.ScaleSubtle.HoverIf(url is not null),
            PressScale = WaveeMotion.ScaleSubtle.PressIf(url is not null),
            // A merch entry with no shop link is a plain listing, not a dead button — don't announce one.
            Role = url is null ? AutomationRole.None : AutomationRole.Button,
            Focusable = url is not null, Cursor = url is null ? (CursorId?)null : CursorId.Hand,
            // Open the external shop through the IPlatformApp.OpenUri PAL seam (what HyperlinkButton uses); headless records.
            OnClick = url is null ? null : () => InputHooks.Current.Default.OpenUri?.Invoke(url),
            Children =
            [
                new BoxEl
                {
                    Width = Thumb, Height = Thumb, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                    Children = [Surfaces.Artwork(item.Image, item.Name.GetHashCode() & 0x7fffffff, Thumb, Thumb, Radii.Control)],
                },
                WaveeType.TrackTitle(item.Name) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new TextEl(item.Price.Length > 0 ? item.Price : Loc.Get(Strings.Artist.Buy))
                {
                    Size = 12f, LineHeight = 16f, Weight = 600, Shrink = 0f,
                    Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.L),
        Children = [WaveeType.RailHeader(title), body],
    };

    static Element AboutArtistSection(Artist artist, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.L),
        Children = [AboutCard(artist, h)],
    };

    // The "About the artist" card — compact identity + bio card; the whole card navigates to the artist, with Follow
    // on the right.
    static Element AboutCard(Artist artist, DetailHandlers h)
    {
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
            Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            OnClick = () => h.Go("artist:" + artist.Uri, artist.Name),
            HoverFill = Tok.FillCardDefault, ClipToBounds = true, Cursor = CursorId.Hand,
            Children =
            [
                new BoxEl
                {
                    Width = 84f, Height = 84f, Shrink = 0f, Corners = CornerRadius4.All(42f), ClipToBounds = true,
                    Children = [PersonPicture.Create("", 84f, displayName: artist.Name, imageSourcePath: artist.Image?.Url)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = Spacing.XS,
                    Children =
                    [
                        WaveeType.Eyebrow(Loc.Get(Strings.Detail.AboutTheArtist))
                            with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                            Children =
                            [
                                new TextEl(artist.Name) { Size = 20f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                artist.Verified ? Icon(Icons.Check, 12f, Tok.TextSecondary) : new BoxEl(),
                            ],
                        },
                        string.IsNullOrWhiteSpace(artist.Bio)
                            ? new BoxEl()
                            : new TextEl(artist.Bio!) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                Embed.Comp(() => new FollowButton(artist.Uri, artist.Name)),
            ],
        };
    }

    // ── per-section loading skeletons (static grey placeholders, same padding as the real Section) ──────────────────
    static Element TrailingSkeleton() => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Children =
        [
            CardSkeleton(),
            ChipsSkeleton(),
            RowsSkeleton(),
        ],
    };

    static Element CardSkeleton() => SectionSkeleton(
        new BoxEl { Height = 96f, Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault });

    static Element ChipsSkeleton() => SectionSkeleton(new BoxEl
    {
        Direction = 0, Gap = Spacing.S, ClipToBounds = true,
        Children = Enumerable.Range(0, 5).Select(_ => (Element)new BoxEl
        { Width = 132f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), Fill = Tok.FillCardDefault }).ToArray(),
    });

    // Reserves the shape the trailing sections now REALLY have: a short stack of 64-DIP media rows (was a horizontal
    // strip of 150-DIP cards, which reserved the wrong axis and settled with a visible jump).
    static Element RowsSkeleton() => SectionSkeleton(new BoxEl
    {
        Direction = 1, Gap = Spacing.XS,
        Children = Enumerable.Range(0, 3).Select(_ => (Element)new BoxEl
        {
            Height = 64f, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M,
            Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f),
            Corners = Radii.CardAll, Fill = Tok.FillCardSecondary,
            Children =
            [
                new BoxEl { Width = WaveeSize.Thumb48, Height = WaveeSize.Thumb48, Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardDefault },
                new BoxEl { Width = 160f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
            ],
        }).ToArray(),
    });

    static Element SectionSkeleton(Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.L),
        Children =
        [
            new BoxEl { Width = 160f, Height = 18f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
            body,
        ],
    };
}

/// <summary>A capped, expandable vertical stack under a section header — the trailing column's ONE list shape.
///
/// <para>Its own <see cref="Component"/> purely for the expand state: "Show all" must not push a re-render up into
/// <c>AlbumTrailing</c> (which owns the whole enrichment resource) just to lengthen one list. The rows are already
/// loaded, so expanding is free and there is nowhere else to send the user — a link that navigated to a fifth surface
/// to show four more albums would be worse than the horizontal shelf this replaced.</para>
///
/// <para>Ctor args freeze at mount (component-props contract), so the section that builds this keys it by a data
/// signature — see <c>TrailingSection</c>.</para></summary>
sealed class TrailingStack : Component
{
    /// <summary>How many rows show before "Show all" — shared with the section wrapper's documented cap.</summary>
    internal const int Cap = 5;

    readonly string _title;
    readonly int _count;
    readonly Func<int, Element> _rowAt;

    public TrailingStack(string title, int count, Func<int, Element> rowAt)
    {
        _title = title; _count = count; _rowAt = rowAt;
    }

    public override Element Render()
    {
        var (expanded, setExpanded) = UseState(false);
        int shown = expanded ? _count : Math.Min(_count, Cap);
        var rows = new Element[shown];
        for (int i = 0; i < shown; i++) rows[i] = _rowAt(i);

        var head = new List<Element>(3)
        {
            WaveeType.RailHeader(_title) with { Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (_count > shown)
            // The stock quiet link (HyperlinkButton at the Small rung) — the same control Home's modules use for the
            // same job, so "there is more here" reads identically on both pages.
            head.Add(HyperlinkButton.Create(Strings.Home.ShowAllCount(_count), () => setExpanded(true), size: ControlSize.Small));

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f, Children = head.ToArray() },
                new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows },
            ],
        };
    }
}
