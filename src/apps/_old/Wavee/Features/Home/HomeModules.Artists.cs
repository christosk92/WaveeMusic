using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Home;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The top-artist podium + its disclosure ───────────────────────────────────────────────────────────────────────────
// A FIXED row on Home rather than a HomeGroupKind: its data is the account's own affinity ranking (userTopContent) and,
// per selection, that artist's overview — neither of which rides the home feed.
//
// Two resources, deliberately separate:
//   • the ranking — fetched once, transport-cached 30 min, so navigating back to Home never re-asks;
//   • the HUB's overview — keyed by uri, so re-centring is a new fetch that a 12 h store TTL usually answers
//     instantly. UseResource (not a bare await) is what makes that a stale-while-revalidate read: a warm artist
//     shows content on the same frame and revalidates underneath instead of shimmering a second time.
//
// The disclosure is the prototype's own `.expander` — a border-top plus a `1fr 342px` grid INSIDE the podium's card —
// and not the Expander control. The control always mounts a 48px header row (its PartRoot re-asserts Children, so the
// header cannot be removed), which on a module that already has its own head rendered as a bare grey bar with a chevron.
sealed class HomeArtistRow : Component
{
    bool _forward = true;

    // Manual double-click tracking for the podium avatars (#83 nav): the engine's DoubleTap gesture is reserved but
    // not yet routed (UseGesture.cs), and OnClick carries no click count, so a same-uri click inside
    // HomeArtistRowLayout.DoubleClickWindowMs is treated as a double. Instance state, not a hook — this component's
    // own click stream.
    (string? Uri, long Tick) _lastPodClick;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var overlay = UseContext(Overlay.Service);   // pod / Mixview-node "Go to artist" context menu (#83)
        var bridge = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var measuredWidth = UseMeasuredWidth(4f);
        if (svc is null) return new BoxEl();

        var top = UseResource(ct => svc.UserTop.GetTopArtistsAsync(ct),
            seed: (IReadOnlyList<RelatedArtist>)Array.Empty<RelatedArtist>(), deps: DepKey.Empty);
        var artists = top.Loadable.Value.Value ?? Array.Empty<RelatedArtist>();
        var userTop = UseResource(ct => svc.UserTop.GetTopTracksAsync(ct),
            seed: (IReadOnlyList<Track>)Array.Empty<Track>(), deps: DepKey.Empty);
        var userTopTracks = userTop.Loadable.Value.Value ?? Array.Empty<Track>();
        var userTopUris = new HashSet<string>(userTopTracks.Select(static t => t.Uri), StringComparer.Ordinal);

        // -1 = nothing selected ⇒ no disclosure at all. Selecting the open artist closes it again, which is the only way
        // to dismiss the pane without inventing a second control for it.
        var (selected, setSelected) = UseState(-1);
        string? selectedUri = (uint)selected < (uint)artists.Count ? artists[selected].Uri : null;

        // The Mixview HUB — LIFTED here from the panel (#83 D17 follow-up). null = "follow the podium selection";
        // it is only ever set away from null by a Mixview node click, and reset back to null in PodClick below —
        // a discrete user action, not a derived value, so there is no effect chasing `selected` around.
        //
        // `onHubChanged` is a STABLE forwarder (UseRef + UseMemo, the UseGesture idiom): UseState's setter is a fresh
        // closure every render, and pushing that directly into MixviewProps would defeat the record's own reuse
        // check on every unrelated re-render. The ref always holds the LATEST setter; the memoized lambda never
        // changes identity.
        var (hub, setHub) = UseState<RelatedArtist?>(null);
        var setHubRef = UseRef(setHub);
        setHubRef.Value = setHub;
        var onHubChanged = UseMemo(() => (Action<RelatedArtist>)(r => setHubRef.Value(r)), DepKey.Empty);

        RelatedArtist? picked = selectedUri is null ? null : artists[selected];
        RelatedArtist? effectiveHub = hub ?? picked;

        // ONE Rich-artist fetch for the hub, feeding BOTH the left pane (TopTracks/Facts/Play) and the Mixview
        // graph's related list — replacing the two duplicate fetches (this row's old per-`selectedUri` fetch plus
        // the panel's own per-hub fetch) that existed before the hub moved up here. KeepPreviousData=true is #83.3:
        // recentring cross-fades the graph instead of the related list — and therefore the ComponentEl itself —
        // blanking for the round trip (a BoxEl skeleton cannot positionally match a ComponentEl, so it used to
        // unmount/remount, which read as "disappears and reappears").
        string? hubUri = effectiveHub?.Uri;
        Artist? warm = hubUri is null ? null : svc.RealStore?.GetArtist(hubUri);
        if (HydrationLevels.Of(warm) < HydrationLevel.Rich) warm = null;
        var detail = UseResource(
            async ct => hubUri is null ? null : await svc.Library.GetArtistAsync(hubUri, HydrationLevel.Rich, ct).ConfigureAwait(false),
            seed: warm, deps: DepKey.From(StringComparer.Ordinal.GetHashCode(hubUri ?? "")),
            options: new ResourceOptions { KeepPreviousData = true });

        if (artists.Count == 0) return new BoxEl();

        float width = measuredWidth.Value;
        float effectiveWidth = width > 0.5f ? width : HomeModuleLayout.FallbackWidth;
        // The tier is resolved HERE, from this row's own measured width, and pushed down to Disclosure/MixviewPanel —
        // the D17 fix: no more `Responsive.Of` boundary for either to freeze props behind.
        int tier = HomeArtistRowLayout.NominalTierFor(effectiveWidth);

        // #82 — the ramp is a function of the measured width: FillRowVirtualLayout.Fit solves the same per-column
        // width every other Home/Browse row derives from its viewport (forced to exactly `artists.Count` columns,
        // since the podium always shows every artist — nothing here virtualizes), and HomeArtistRowLayout scales the
        // prototype's 76/60/46 ramp around it so the strip stretches to fill a wide card instead of packing left.
        const float PodChrome = Spacing.S;   // the "+8" RankedAvatar's own `w = max(artSize+8, 60)` adds per pod
        float podiumContentW = MathF.Max(0f, effectiveWidth - 2f * Spacing.M);
        var (_, fittedPodW) = FillRowVirtualLayout.Fit(podiumContentW, HomeArtistRowLayout.BaseArtSize(0) + PodChrome,
            9999f, Spacing.S, perPageOverride: artists.Count);
        float rampScale = HomeArtistRowLayout.RampScaleFor(fittedPodW, artists.Count, PodChrome);

        // Every pod reserves the TALLEST avatar's height for its art, so all the labels land on one line. The prototype
        // gets that from `align-items:flex-end` on a wrapping flex row; our flex engine does not reproduce bottom
        // alignment under wrap, and the result was a staircase. Reserving the slot makes it structural.
        float slot = HomeArtistRowLayout.ArtSize(0, rampScale);
        var strip = new List<Element>(artists.Count);
        for (int i = 0; i < artists.Count; i++)
        {
            int index = i;
            var a = artists[i];
            // The ring highlight now follows the HUB, not the raw podium selection — so re-centring Mixview onto one
            // of the OTHER top artists (a related artist can coincide with one) lights that pod up instead of the
            // one originally clicked. When the hub is some unrelated artist not in this strip, none light up, which
            // is correct: no pod is "current" any more.
            bool isHub = effectiveHub is not null && string.Equals(effectiveHub.Uri, a.Uri, StringComparison.Ordinal);
            var pod = HomeCards.RankedAvatar(a, i + 1, isHub, HomeArtistRowLayout.ArtSize(i, rampScale), slot,
                onSelect: () => PodClick(index, a.Uri, a.Name),
                menu: HomeCards.GoToArtistMenu(overlay, go, a.Uri, a.Name));
            strip.Add(pod is BoxEl b ? b with { Key = "home-topartist:" + a.Uri } : pod);
        }

        var podium = new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0f,
            Padding = Edges4.All(Spacing.M),
            Children = [.. strip],
        };

        // One card holding the podium and, when an artist is selected, the disclosure below a divider.
        var card = new BoxEl
        {
            Direction = 1, MinWidth = 0f, ClipToBounds = true,
            Animate = MotionRecipes.CardResizeHeight,
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = selectedUri is null
                ? [podium]
                : [podium,
                   new BoxEl
                   {
                       Key = "home-artist-disclosure:" + selectedUri,
                       Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                       Direction = 1, MinWidth = 0f,
                       Children =
                       [
                           new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
                           Disclosure(detail.Loadable, warm, effectiveHub!, selectedUri!, tier,
                               go, overlay, svc, bridge, lib, userTopUris, userTopTracks.Count, onHubChanged),
                       ],
                   }],
        };

        Element module = new BoxEl
        {
            Direction = 1, Gap = HomeModuleLayout.HeadGap, MinWidth = 0f,
            Children =
            [
                Surfaces.SectionHeader(Loc.Get(Strings.Home.TopArtists), Strings.Home.TopArtistsSub(artists.Count)),
                card,
            ],
        };
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            // This row's OWN bottom gap, not the shared HomeModuleLayout.Gap other Home rows also read (#82
            // compaction) — HomeArtistRowLayout.ModuleGap keeps the same 1080-DIP tier boundary at 32/24 instead of
            // 40/32, without touching the helper every other module shares.
            Padding = new Edges4(0f, 0f, 0f, HomeArtistRowLayout.ModuleGap(effectiveWidth)),
            Children = [module],
        };

        // Left-click stays select/re-hub (the surface's own purpose); double-click (the same pod within
        // HomeArtistRowLayout.DoubleClickWindowMs) opens the artist page instead — the pod's context menu
        // (HomeCards.GoToArtistMenu, wired above) is the primary route, this is the accelerator.
        void PodClick(int index, string uri, string name)
        {
            long now = Environment.TickCount64;
            if (HomeArtistRowLayout.IsDoubleClick(uri, _lastPodClick.Uri, _lastPodClick.Tick, now))
            {
                _lastPodClick = (null, 0);
                go?.Invoke("artist:" + uri, name);
                return;
            }
            _lastPodClick = (uri, now);
            int next = index == selected ? -1 : index;
            _forward = next >= 0 && (selected < 0 || next > selected);
            setSelected(next);
            setHub(null);   // re-hub to whichever top artist is now open (or nothing, once the disclosure closes)
        }
    }

    /// <summary>`.expander` — a `1fr 342px` grid: top tracks on the left, Mixview on the right, stacking under ~900px.
    /// <para>The 900 boundary is <see cref="HomeArtistRowLayout"/>'s, not a literal: the same tier decides BOTH this
    /// row's split AND which Mixview graph the panel draws (ring vs spine), so the two cannot drift apart. It is
    /// resolved by <see cref="HomeArtistRow"/> from ITS OWN measured width and passed in as <paramref name="tier"/> —
    /// this method builds a plain Element tree with no <c>Responsive.Of</c> boundary of its own (the D17 fix: a
    /// nested <c>ResponsiveBox</c> here froze `hub`/`svc`/`go`/… at first mount, which is why a Mixview click used to
    /// do nothing — see MixviewPanel's own doc comment).</para></summary>
    static Element Disclosure(Loadable<Artist?> loadable, Artist? warm, RelatedArtist hub, string ownerUri, int tier,
                              Action<string, string?>? go, IOverlayService? overlay, Services svc,
                              PlaybackBridge? bridge, LibraryBridge? lib,
                              IReadOnlySet<string> userTopUris, int userTopCount, Action<RelatedArtist> onHubChanged)
    {
        Element Content(Artist? artist)
        {
            var related = artist?.Extras?.Related ?? (IReadOnlyList<RelatedArtist>)Array.Empty<RelatedArtist>();
            // The LEFT pane now reads off the HUB, not the podium's original pick (#83): TopTracks, the section
            // header's monthly-listener facts and the Play button all follow wherever Mixview is currently centred.
            Element left = TopTracks(artist, hub, svc, go, bridge, lib, userTopUris, userTopCount);
            Element right = Embed.Comp(
                new MixviewProps(hub, related, tier, onHubChanged, go),
                static () => new MixviewPanel()) with { Key = "mixview:" + ownerUri };
            return tier == HomeArtistRowLayout.TierWide
                ? new BoxEl
                {
                    Direction = 0, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    Children =
                    [
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [left] },
                        new BoxEl { Width = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                        new BoxEl { Direction = 1, Width = 342f, Shrink = 0f, MinWidth = 0f, Children = [right] },
                    ],
                }
                : new BoxEl
                {
                    Direction = 1, MinWidth = 0f,
                    Children = [left, new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }, right],
                };
        }

        if (warm is not null && loadable.State.Value != (byte)LoadState.Ready) return Content(warm);
        return Skel.Region(loadable, Content, reveal: SkelReveal.StaggerRows,
            group: HomeSkeleton.Group, smoothResize: false);
    }

    // The canonical track cell's column set: # / heart / art / title / plays / duration / "…". The SAME cell the artist
    // page, search and every detail list render, so a track on Home behaves like a track everywhere — hover transport,
    // the live equalizer on the now-playing row, the per-row heart, the context menu.
    //
    // The prototype drew a 34px row with a play-count METER. Neither survives contact with the shared cell: nothing in
    // the app renders a track under 40px (the heart and "…" are 28px hit targets), and the canonical plays cell is a
    // right-aligned number — the meter existed only in the mock. A number that reads the same as the artist page beats a
    // bar that reads like nothing else.
    static readonly ColumnSet TrackCols = new(Album: false, By: false, Date: false, Video: false,
                                                       Plays: true, Heart: true, Thumb: true);
    static readonly ColumnSet TrackColsNoArt = TrackCols with { Thumb = false };
    static readonly TrackSize[] TrackColumns =
    [
        TrackSize.Px(36f),                      // # ↔ play
        TrackSize.Px(TrackRow.HeartCol),        // heart
        TrackSize.Px(TrackRow.ThumbSize),       // art
        TrackSize.Star(),                       // title
        TrackSize.Px(84f),                      // plays
        TrackSize.Px(52f),                      // duration
        TrackSize.Px(160f),                     // personal badge + trailing overflow
    ];
    static readonly TrackSize[] TrackColumnsNoArt =
    [
        TrackSize.Px(36f),
        TrackSize.Px(TrackRow.HeartCol),
        TrackSize.Star(),
        TrackSize.Px(84f),
        TrackSize.Px(52f),
        TrackSize.Px(160f),
    ];

    // `.exp-l` — padding 16/18/18, a head with a subdued fact and a Play, then the track rows.
    static Element TopTracks(Artist? a, RelatedArtist hub, Services svc, Action<string, string?>? go,
                             PlaybackBridge? bridge, LibraryBridge? lib,
                             IReadOnlySet<string> userTopUris, int userTopCount)
    {
        bool showArtwork = !AppearancePrefs.TrackArtworkHidden(svc.Settings);
        var rowCols = showArtwork ? TrackCols : TrackColsNoArt;
        var rowTracks = showArtwork ? TrackColumns : TrackColumnsNoArt;
        var kids = new List<Element>(8)
        {
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    BodyStrong(Loc.Get(Strings.Home.TopTracks)) with { MaxLines = 1 },
                    Facts(a) is { Length: > 0 } facts
                        ? Caption(facts) with
                        {
                            Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                        }
                        : new BoxEl(),
                    new BoxEl { Grow = 1f, MinWidth = 0f },
                    Button.Create(Loc.Get(Strings.Home.Play), () => _ = svc.Player.PlayAsync(hub.Uri, 0),
                        ButtonAppearance.Standard, glyph: Icons.Play),
                ],
            },
        };

        if (a?.TopTracks is { Count: > 0 } tracks)
        {
            // The cell's artist/album hyperlinks navigate through this; a null nav context makes them inert rather than
            // absent, so the row's geometry is the same either way.
            Action<string, string?> navigate = go ?? ((_, _) => { });
            int n = Math.Min(tracks.Count, 5);
            for (int i = 0; i < n; i++)
            {
                var t = tracks[i];
                var st = TrackRow.StateOf(bridge, lib, t);
                // `i`, not `i + 1`: TrackRow renders DisplayIndex + 1, so passing the ordinal made the list start at 2.
                kids.Add(TrackRow.Row(t, i, st, rowCols, rowTracks, TrackRow.RowHeight,
                             showTrackArtist: false,
                             navigate,
                             onPlay: () => TrackRow.Invoke(bridge, t, () => _ = svc.Player.PlayTrackAsync(t.Uri)),
                             onLike: t.Uri.Length > 0 ? () => lib?.ToggleSaved(t.Uri, t.Title) : null,
                              actionsCell: TrackActions(userTopUris.Contains(t.Uri), userTopCount))
                         with { Key = "home-toptrack:" + t.Uri + ":art=" + showArtwork });
            }
        }
        else
        {
            // Pending shows the row SHAPE rather than a spinner, so an expand never flashes empty and never jumps when
            // the overview lands.
            for (int i = 0; i < 5; i++)
                kids.Add(new BoxEl { Height = TrackRow.RowHeight, MinWidth = 0f, Children = [Body(" ")] }.Skeletonized(true));
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Padding = Edges4.All(Spacing.M),
            Children = [.. kids],
        };
    }

    static Element TrackActions(bool inUserTop, int topCount) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, MinWidth = 0f,
        Children =
        [
            inUserTop ? new BoxEl
            {
                Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                Corners = Radii.FullAll, Fill = Tok.SystemFillSuccessBackground,
                // A LOCALIZED, count-interpolated string ("In your top 5") — the alias's own case and tracking, nothing
                // added. The success green is a SEMANTIC colour, not the page accent, so it is outside the accent budget.
                Children = [WaveeType.Eyebrow(Strings.Home.InYourTop(topCount)) with
                {
                    Color = Tok.SystemFillSuccess,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                }],
            } : new BoxEl(),
            TrackRow.MoreButton(true),
        ],
    };

    /// <summary>"6.3M monthly listeners · #4 worldwide" — each half only when the server actually stated it. A world rank
    /// of 0 means "not ranked", and printing "#0 worldwide" states a fact that is not one.</summary>
    static string Facts(Artist? a)
    {
        if (a is null) return "";
        string listeners = a.MonthlyListeners > 0
            ? HomeCards.CompactNumber(a.MonthlyListeners) + " " + Loc.Get(Strings.Artist.MetaMonthly) : "";
        string rank = a.WorldRank > 0 ? Strings.Artist.WorldRank(a.WorldRank) : "";
        return listeners.Length > 0 && rank.Length > 0 ? listeners + " · " + rank
             : listeners.Length > 0 ? listeners : rank;
    }

    static string Mmss(long ms)
    {
        if (ms <= 0) return "";
        int total = (int)Math.Round(ms / 1000d);
        return (total / 60).ToString(System.Globalization.CultureInfo.CurrentCulture) + ":" + (total % 60).ToString("00");
    }
}

/// <summary>Mixview's re-pushed props. A record, so a re-render at the same width/hub is still equality-comparable —
/// but note that <see cref="OnHubChanged"/> makes exact coalescing conditional on the CALLER passing a stable
/// delegate (<see cref="HomeArtistRow"/> does, via a UseRef-backed forwarder); <see cref="Go"/> is the ambient nav
/// context, stable across renders on its own.</summary>
sealed record MixviewProps(RelatedArtist Hub, IReadOnlyList<RelatedArtist> Related, int Tier,
                           Action<RelatedArtist> OnHubChanged, Action<string, string?>? Go);

/// <summary>`.exp-r` — a head, then the node graph.
///
/// <para>The HUB itself now lives in <see cref="HomeArtistRow"/> (#83), not here. It used to be panel-local — stateful
/// specifically because the graph RECENTERS — but that meant clicking a Mixview node re-centred only the GRAPH while
/// everything else on the disclosure (TopTracks, the Play button, the monthly-listeners facts) stayed frozen on
/// whichever top artist the podium had originally selected: a surface that visibly disagreed with itself about who
/// was "current". This panel is now a thin presentational shell — <see cref="MixviewProps.Hub"/> and
/// <see cref="MixviewProps.Related"/> are pushed down from the row's own state, and a node click reports back through
/// <see cref="MixviewProps.OnHubChanged"/> rather than owning a signal of its own.</para>
///
/// <para>Two graphs, one panel: the <see cref="HomeArtistRowLayout.TierWide"/> arm keeps the radial ring, and the
/// narrow arm draws a vertical spine list instead of a ring the pane can only clip. The tier is DECIDED BY
/// <see cref="HomeArtistRow"/> from its own measured width and pushed down — the panel's own width (342 DIP in the
/// wide arm) says nothing about which arm the row is in.</para></summary>
sealed class MixviewPanel : Component
{
    const float SpineHubD = 40f;
    const float SpineNodeD = 32f;

    // Manual double-click tracking for ring/spine nodes (#83 nav) — same rationale as HomeArtistRow's own
    // _lastPodClick. Reset for free whenever a new top-level artist remounts this panel (its Key carries the
    // podium's selectedUri), so a recentre-then-navigate sequence can never straddle two different artists' picks.
    (string? Uri, long Tick) _lastNodeClick;

    public override Element Render()
    {
        var p = UseProps<MixviewProps>();
        var overlay = UseContext(Overlay.Service);
        // D17 fix: the graph used to measure its width via a nested `Responsive.Of`, whose ResponsiveBox froze the
        // `_build` closure (and therefore `hub`/`related`/`setHub`) at first mount — this is the bug #83 reports.
        // Resolving UseMeasuredWidth directly inside this component instead is the same fix already applied to the
        // podium (HomeArtistRow resolves it at its own top and only uses it for the module bottom gap).
        var measuredWidth = UseMeasuredWidth(4f);

        Element graph = p.Related.Count > 0
            ? (p.Tier == HomeArtistRowLayout.TierWide
                ? MixGraphRing(p.Hub, p.Related, measuredWidth.Value, NodeClick, HubClick, p.Go, overlay)
                : MixGraphSpine(p.Hub, p.Related, NodeClick, HubClick, p.Go, overlay))
            : new BoxEl { Height = 120f, MinWidth = 0f, Children = [Body(" ")] }.Skeletonized(true);

        // #83.4: the header used to report `related.Count` while the ring only ever DREW min(related.Count, 6) — a
        // "20 · fans also like" caption over an 6-node ring. Raising the drawn cap instead of shrinking the header
        // count was considered and rejected: at a typical ~318-DIP pane content width the ring's circumference
        // (2π · ringR ≈ 690 DIP) divided by each node's 104-DIP caption box is ≈6.6, so anything past 6 nodes starts
        // overlapping captions. Reporting the drawn count keeps the two in agreement without degrading the ring.
        int drawn = Math.Min(p.Related.Count, 6);

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            // 12 all round, matching the 12 the box already carried on one edge — the prototype's 12/14/14 was three
            // values for one inset on a 342-DIP pane. Left as-is (#82 explicitly leaves the Mixview pane padding).
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(Loc.Get(Strings.Home.Mixview)) with { MaxLines = 1 },
                        drawn > 0
                            ? Caption(Strings.Home.RelatedCount(drawn)) with
                            {
                                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                            }
                            : new BoxEl(),
                    ],
                },
                graph,
            ],
        };

        // Recentre, don't navigate: the click keeps you on Home and re-hubs the graph (AND the left pane, via
        // OnHubChanged) on the artist you picked. Left-click stays the surface's own gesture; a second click on the
        // SAME node within the double-click window opens the artist page instead.
        void NodeClick(RelatedArtist a)
        {
            long now = Environment.TickCount64;
            if (HomeArtistRowLayout.IsDoubleClick(a.Uri, _lastNodeClick.Uri, _lastNodeClick.Tick, now))
            {
                _lastNodeClick = (null, 0);
                p.Go?.Invoke("artist:" + a.Uri, a.Name);
                return;
            }
            _lastNodeClick = (a.Uri, now);
            p.OnHubChanged(a);
        }

        // The hub node's single click is already a no-op (it is the centre — there is nowhere to recentre to); kept
        // wired only so a SECOND click within the window can still complete a double-click to navigate.
        void HubClick()
        {
            long now = Environment.TickCount64;
            if (HomeArtistRowLayout.IsDoubleClick(p.Hub.Uri, _lastNodeClick.Uri, _lastNodeClick.Tick, now))
            {
                _lastNodeClick = (null, 0);
                p.Go?.Invoke("artist:" + p.Hub.Uri, p.Hub.Name);
                return;
            }
            _lastNodeClick = (p.Hub.Uri, now);
        }
    }

    /// <summary>`.mix` — the hub artist centred with its related artists on a ring, one stroked connector per edge.
    /// <para>Positions are computed here rather than laid out, because a radial arrangement has no flex expression: the
    /// graph is a ZStack whose children carry normalized JustifySelf/AlignSelf offsets via Margin. Connectors are
    /// <c>PolylineStrokeEl</c> — solid, because the leaf carries no dash pattern (the prototype's are dashed; flagged
    /// rather than faked).</para></summary>
    static Element MixGraphRing(RelatedArtist hub, IReadOnlyList<RelatedArtist> related, float measuredWidth,
                                Action<RelatedArtist> onNodeClick, Action onHubClick,
                                Action<string, string?>? go, IOverlayService? overlay)
    {
        // UseMeasuredWidth reports this COMPONENT's own outer rendered width — i.e. before its Padding is subtracted
        // — so the ring solves against the padded-away content width, matching what the removed Responsive.Of used
        // to measure from directly inside that padding.
        float w = measuredWidth > 2f * Spacing.M ? measuredWidth - 2f * Spacing.M : 314f;
        // `aspect-ratio: 1/.92`, plus one caption line: every node now carries its name below it, and the ring's
        // bottom node would otherwise put that name outside the box.
        float h = w * 0.92f + 18f;
        float cx = w * 0.5f, cy = h * 0.46f;
        float hubR = 34f, nodeR = 21f;
        float ringR = MathF.Min(cx, cy) - nodeR - 12f;
        int n = Math.Min(related.Count, 6);

        var layers = new List<Element>(n * 3 + 2);
        // Connectors first, so every node paints over its own edge.
        for (int i = 0; i < n; i++)
        {
            float ang = -MathF.PI / 2f + i * (MathF.Tau / n);
            float x = cx + MathF.Cos(ang) * ringR, y = cy + MathF.Sin(ang) * ringR;
            layers.Add(new PolylineStrokeEl
            {
                P0 = new Point2(cx, cy), P1 = new Point2(x, y), PointCount = 2,
                Color = Tok.TextTertiary with { A = 0.26f }, Thickness = 1f,
                Width = w, Height = h,
            });
        }
        layers.Add(Node(hub, cx, cy, hubR, isHub: true, onHubClick, HomeCards.GoToArtistMenu(overlay, go, hub.Uri, hub.Name)));
        layers.Add(NodeCap(hub.Name, cx, cy, hubR, isHub: true));
        for (int i = 0; i < n; i++)
        {
            float ang = -MathF.PI / 2f + i * (MathF.Tau / n);
            var r = related[i];
            float x = cx + MathF.Cos(ang) * ringR, y = cy + MathF.Sin(ang) * ringR;
            layers.Add(Node(r, x, y, nodeR, false, () => onNodeClick(r), HomeCards.GoToArtistMenu(overlay, go, r.Uri, r.Name)));
            layers.Add(NodeCap(r.Name, x, y, nodeR, isHub: false));
        }
        return new BoxEl { ZStack = true, Width = w, Height = h, MinWidth = 0f, Children = [.. layers] };
    }

    /// <summary>The narrow arm: the hub, then its related artists as a vertical list hanging off one rule. Same six-node
    /// cap as the ring, same recenter click — only the geometry changes, so the panel reads as the same object at both
    /// widths instead of a shrunken ring the pane clips.
    ///
    /// <para>No <c>Responsive.Of</c>, no ZStack, no trig: a column of rows is exactly what flex expresses. The rule is
    /// ONE full-height 1-DIP fill behind the whole list rather than a <c>PolylineStrokeEl</c> per row — a stroke element
    /// is what the ring needs for its diagonal edges; a vertical line is just a box the row cross-stretches, and one box
    /// beats six strokes that would each have to be told their own height.</para></summary>
    static Element MixGraphSpine(RelatedArtist hub, IReadOnlyList<RelatedArtist> related,
                                 Action<RelatedArtist> onNodeClick, Action onHubClick,
                                 Action<string, string?>? go, IOverlayService? overlay)
    {
        int n = Math.Min(related.Count, 6);
        var rows = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var r = related[i];
            rows.Add(SpineRow(r, onNodeClick, HomeCards.GoToArtistMenu(overlay, go, r.Uri, r.Name)));
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                SpineHub(hub, onHubClick, HomeCards.GoToArtistMenu(overlay, go, hub.Uri, hub.Name)),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
                    // Inset so the rule falls under the hub avatar's CENTRE — the edge it stands in for.
                    Padding = new Edges4(SpineHubD * 0.5f, 0f, 0f, 0f),
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 1f, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
                            Fill = Tok.TextTertiary with { A = 0.26f },
                        },
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [.. rows] },
                    ],
                },
            ],
        };
    }

    static Element SpineHub(RelatedArtist hub, Action onHubClick, MenuAttach? menu) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
        OnClick = onHubClick, Cursor = CursorId.Hand, Role = AutomationRole.Button,
        Children =
        [
            new BoxEl
            {
                Width = SpineHubD, Height = SpineHubD, Shrink = 0f,
                Corners = Radii.Circle(SpineHubD),
                // The ring's hub treatment, unchanged: the accent halo is what says "everything below hangs off this".
                BorderWidth = 3f, BorderColor = Tok.AccentDefault,
                Children = [Surfaces.Artwork(hub.Image, SpotifyExportMapper.Hash(hub.Uri), SpineHubD, SpineHubD, Radii.Full, decodePx: 128)],
            },
            BodyStrong(hub.Name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
        ],
    }.WithMenu(menu);

    static Element SpineRow(RelatedArtist r, Action<RelatedArtist> onNodeClick, MenuAttach? menu) => new BoxEl
    {
        Key = "mixview-spine:" + r.Uri,
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.XS, Spacing.XS),
        OnClick = () => onNodeClick(r), Cursor = CursorId.Hand, Role = AutomationRole.Button,
        Children =
        [
            new BoxEl
            {
                Width = SpineNodeD, Height = SpineNodeD, Shrink = 0f,
                Corners = Radii.Circle(SpineNodeD),
                Children = [Surfaces.Artwork(r.Image, SpotifyExportMapper.Hash(r.Uri), SpineNodeD, SpineNodeD, Radii.Full, decodePx: 128)],
            },
            Caption(r.Name) with
            {
                Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    }.WithMenu(menu);

    // A node placed by its CENTRE: the ZStack anchors top-left, so the margin carries centre-minus-radius.
    static Element Node(RelatedArtist a, float cx, float cy, float r, bool isHub, Action? onClick, MenuAttach? menu)
    {
        float d = r * 2f;
        return new BoxEl
        {
            Width = d, Height = d, Shrink = 0f,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
            Margin = new Edges4(cx - r, cy - r, 0f, 0f),
            Corners = Radii.Circle(d),
            BorderWidth = isHub ? 3f : 0f, BorderColor = Tok.AccentDefault,
            OnClick = onClick, Cursor = CursorId.Hand,
            Role = AutomationRole.Button,
            Children = [Surfaces.Artwork(a.Image, SpotifyExportMapper.Hash(a.Uri), d, d, Radii.Full, decodePx: 128)],
        }.WithMenu(menu);
    }

    /// <summary>`.node-cap` — `translate:-50% 0`, `left:x`, `top: y + size/2 + 5`, width 104, centred, 11/14 ink-2; the
    /// hub's is 600 weight in ink-1. A graph of bare avatars named nobody: the whole point of Mixview is WHICH artists
    /// link to this one.
    ///
    /// <para>The prototype's second line (`.node-cap i`) is its "via" — the reason for the edge. The server's related-artist
    /// payload carries no such field, so that line is omitted rather than filled with something invented.</para></summary>
    static Element NodeCap(string name, float cx, float cy, float r, bool isHub) => new BoxEl
    {
        Width = 104f, Shrink = 0f, HitTestVisible = false,
        AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
        // TextEl has no alignment of its own on this seam; centring is the parent's job.
        Direction = 0, Justify = FlexJustify.Center,
        Margin = new Edges4(cx - 52f, cy + r + 5f, 0f, 0f),
        Children =
        [
            Caption(name) with
            {
                Weight = (ushort)(isHub ? 600 : 400),
                Color = isHub ? Tok.TextPrimary : Tok.TextSecondary,
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        ],
    };
}
