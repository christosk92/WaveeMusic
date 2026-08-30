using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
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
//   • the SELECTED artist's overview — keyed by uri, so switching artists is a new fetch that a 12 h store TTL usually
//     answers instantly. UseResource (not a bare await) is what makes that a stale-while-revalidate read: a warm artist
//     shows content on the same frame and revalidates underneath instead of shimmering a second time.
//
// The disclosure is the prototype's own `.expander` — a border-top plus a `1fr 342px` grid INSIDE the podium's card —
// and not the Expander control. The control always mounts a 48px header row (its PartRoot re-asserts Children, so the
// header cannot be removed), which on a module that already has its own head rendered as a bare grey bar with a chevron.
sealed class HomeArtistRow : Component
{
    bool _forward = true;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
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

        // The warm seed is a PRESENCE question, not an age one: paint the resident artist immediately iff it already
        // carries what the expander shows (overview facets = Rich). Freshness/TTL belongs to the hydration ledger the
        // GetArtistAsync below goes through — this is why ArtistStatsCache died (hydration-facade-plan.md §1.6).
        Artist? warm = selectedUri is null ? null : svc.RealStore?.GetArtist(selectedUri);
        if (HydrationLevels.Of(warm) < HydrationLevel.Rich) warm = null;
        var detail = UseResource(
            async ct => selectedUri is null ? null : await svc.Library.GetArtistAsync(selectedUri, HydrationLevel.Rich, ct).ConfigureAwait(false),
            seed: warm, deps: DepKey.From(StringComparer.Ordinal.GetHashCode(selectedUri ?? "")));

        if (artists.Count == 0) return new BoxEl();

        // Every pod reserves the TALLEST avatar's height for its art, so all the labels land on one line. The prototype
        // gets that from `align-items:flex-end` on a wrapping flex row; our flex engine does not reproduce bottom
        // alignment under wrap, and the result was a staircase. Reserving the slot makes it structural.
        float slot = ArtSize(0);
        var strip = new List<Element>(artists.Count);
        for (int i = 0; i < artists.Count; i++)
        {
            int index = i;
            var a = artists[i];
            var pod = HomeCards.RankedAvatar(a, i + 1, index == selected, ArtSize(i), slot,
                () =>
                {
                    int next = index == selected ? -1 : index;
                    _forward = next >= 0 && (selected < 0 || next > selected);
                    setSelected(next);
                });
            strip.Add(pod is BoxEl b ? b with { Key = "home-topartist:" + a.Uri } : pod);
        }

        var podium = new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.S, MinWidth = 0f,
            Padding = Edges4.All(Spacing.L),
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
                           Disclosure(detail.Loadable, warm, artists[selected], go, svc, bridge, lib,
                               userTopUris, userTopTracks.Count),
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
        float width = measuredWidth.Value;
        float effectiveWidth = width > 0.5f ? width : HomeModuleLayout.FallbackWidth;
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Padding = new Edges4(0f, 0f, 0f, HomeModuleLayout.Gap(effectiveWidth)),
            Children = [module],
        };
    }

    // `size = i === 0 ? 76 : i < 3 ? 60 : 46` — rank encoded as scale, so the ordering is legible without reading the
    // pills.
    static float ArtSize(int i) => i == 0 ? 76f : i < 3 ? 60f : 46f;

    /// <summary>`.expander` — a `1fr 342px` grid: top tracks on the left, Mixview on the right, stacking under ~900px.
    /// <para>The 900 boundary is <see cref="HomeArtistRowLayout"/>'s, not a literal: the same tier decides BOTH this
    /// row's split AND which Mixview graph the panel draws (ring vs spine), so the two cannot drift apart.</para></summary>
    static Element Disclosure(Loadable<Artist?> loadable, Artist? warm, RelatedArtist picked,
                              Action<string, string?>? go, Services svc, PlaybackBridge? bridge, LibraryBridge? lib,
                              IReadOnlySet<string> userTopUris, int userTopCount)
    {
        Element Content(Artist? artist) => Responsive.Of(width =>
        {
            int tier = HomeArtistRowLayout.NominalTierFor(width);
            Element left = TopTracks(artist, picked, svc, go, bridge, lib, userTopUris, userTopCount);
            // Props are RE-PUSHED (Embed.Comp(props, …)) rather than captured in the factory: a width change re-renders
            // this responsive box but REUSES the panel instance, and a reused ComponentEl never re-runs its factory —
            // a plain field would freeze `Tier` at whatever the first measured frame happened to be.
            Element right = Embed.Comp(
                new MixviewProps(picked, artist?.Extras?.Related ?? (IReadOnlyList<RelatedArtist>)Array.Empty<RelatedArtist>(), tier),
                static () => new MixviewPanel()) with { Key = "mixview:" + picked.Uri };
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
        }, fallback: 1100f);

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
    static Element TopTracks(Artist? a, RelatedArtist picked, Services svc, Action<string, string?>? go,
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
                    Button.Create(Loc.Get(Strings.Home.Play), () => _ = svc.Player.PlayAsync(picked.Uri, 0),
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
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            Padding = Edges4.All(Spacing.L),
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
        return listeners.Length > 0 && rank.Length > 0 ? listeners + " \u00b7 " + rank
             : listeners.Length > 0 ? listeners : rank;
    }

    static string Mmss(long ms)
    {
        if (ms <= 0) return "";
        int total = (int)Math.Round(ms / 1000d);
        return (total / 60).ToString(System.Globalization.CultureInfo.CurrentCulture) + ":" + (total % 60).ToString("00");
    }
}

/// <summary>Mixview's re-pushed props. A record so a fresh-but-equal push (the common case: a re-render at the same
/// width) is coalesced and never re-renders the panel.</summary>
sealed record MixviewProps(RelatedArtist Picked, IReadOnlyList<RelatedArtist> InitialRelated, int Tier);

/// <summary>`.exp-r` — a head, then the node graph. Stateful because the graph RECENTERS: clicking a related artist
/// makes it the new hub instead of navigating away, which needs a hub of its own plus that hub's related list.
///
/// <para>Two graphs, one panel: the <see cref="HomeArtistRowLayout.TierWide"/> arm keeps the radial ring, and the
/// narrow arm draws a vertical spine list instead of a ring the pane can only clip. The tier is DECIDED BY THE
/// DISCLOSURE and pushed down — the panel's own width (342 DIP in the wide arm) says nothing about which arm the row
/// is in.</para></summary>
sealed class MixviewPanel : Component
{
    const float SpineHubD = 40f;
    const float SpineNodeD = 32f;

    public override Element Render()
    {
        var p = UseProps<MixviewProps>();
        var svc = UseContext(Services.Slot);
        // The hub is LOCAL: the podium's pick only seeds it. The mount Key carries picked.Uri, so choosing a different
        // top artist remounts this panel and the seed re-applies — no manual reset.
        var (hub, setHub) = UseState(p.Picked);

        // The seeded hub needs no fetch at all: the disclosure's own overview already carried its related list. Any
        // OTHER hub goes through the same warm-seed + Rich read the disclosure uses, so a resident artist recenters on
        // the same frame and revalidates underneath.
        string? fetchUri = hub.Uri == p.Picked.Uri ? null : hub.Uri;
        Artist? warm = fetchUri is null ? null : svc?.RealStore?.GetArtist(fetchUri);
        if (HydrationLevels.Of(warm) < HydrationLevel.Rich) warm = null;
        var hubDetail = UseResource(
            async ct => fetchUri is null || svc is null ? null
                      : await svc.Library.GetArtistAsync(fetchUri, HydrationLevel.Rich, ct).ConfigureAwait(false),
            seed: warm, deps: DepKey.From(StringComparer.Ordinal.GetHashCode(fetchUri ?? "")));

        var related = fetchUri is null
            ? p.InitialRelated
            : hubDetail.Loadable.Value.Value?.Extras?.Related ?? (IReadOnlyList<RelatedArtist>)Array.Empty<RelatedArtist>();

        Element graph = related.Count > 0
            ? (p.Tier == HomeArtistRowLayout.TierWide
                ? MixGraphRing(hub, related, setHub)
                : MixGraphSpine(hub, related, setHub))
            : new BoxEl { Height = 120f, MinWidth = 0f, Children = [Body(" ")] }.Skeletonized(true);

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            // 12 all round, matching the 12 the box already carried on one edge — the prototype's 12/14/14 was three
            // values for one inset on a 342-DIP pane.
            Padding = Edges4.All(Spacing.M),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                    Children =
                    [
                        BodyStrong(Loc.Get(Strings.Home.Mixview)) with { MaxLines = 1 },
                        // One count, not "20 of 20": both numbers were the same value, which read as a broken fraction.
                        related.Count > 0
                            ? Caption(Strings.Home.RelatedCount(related.Count)) with
                            {
                                Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
                            }
                            : new BoxEl(),
                    ],
                },
                graph,
            ],
        };
    }

    /// <summary>`.mix` — the hub artist centred with its related artists on a ring, one stroked connector per edge.
    /// <para>Positions are computed here rather than laid out, because a radial arrangement has no flex expression: the
    /// graph is a ZStack whose children carry normalized JustifySelf/AlignSelf offsets via Margin. Connectors are
    /// <c>PolylineStrokeEl</c> — solid, because the leaf carries no dash pattern (the prototype's are dashed; flagged
    /// rather than faked).</para></summary>
    static Element MixGraphRing(RelatedArtist hub, IReadOnlyList<RelatedArtist> related, Action<RelatedArtist> setHub)
        => Responsive.Of(width =>
        {
            float w = width > 1f ? width : 314f;
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
            layers.Add(Node(hub, cx, cy, hubR, isHub: true, null));
            layers.Add(NodeCap(hub.Name, cx, cy, hubR, isHub: true));
            for (int i = 0; i < n; i++)
            {
                float ang = -MathF.PI / 2f + i * (MathF.Tau / n);
                var r = related[i];
                float x = cx + MathF.Cos(ang) * ringR, y = cy + MathF.Sin(ang) * ringR;
                // Recenter, don't navigate: the click keeps you on Home and re-hubs the graph on the artist you picked.
                layers.Add(Node(r, x, y, nodeR, false, () => setHub(r)));
                layers.Add(NodeCap(r.Name, x, y, nodeR, isHub: false));
            }
            return new BoxEl { ZStack = true, Width = w, Height = h, MinWidth = 0f, Children = [.. layers] };
        }, fallback: 314f);

    /// <summary>The narrow arm: the hub, then its related artists as a vertical list hanging off one rule. Same six-node
    /// cap as the ring, same recenter click — only the geometry changes, so the panel reads as the same object at both
    /// widths instead of a shrunken ring the pane clips.
    ///
    /// <para>No <c>Responsive.Of</c>, no ZStack, no trig: a column of rows is exactly what flex expresses. The rule is
    /// ONE full-height 1-DIP fill behind the whole list rather than a <c>PolylineStrokeEl</c> per row — a stroke element
    /// is what the ring needs for its diagonal edges; a vertical line is just a box the row cross-stretches, and one box
    /// beats six strokes that would each have to be told their own height.</para></summary>
    static Element MixGraphSpine(RelatedArtist hub, IReadOnlyList<RelatedArtist> related, Action<RelatedArtist> setHub)
    {
        int n = Math.Min(related.Count, 6);
        var rows = new List<Element>(n);
        for (int i = 0; i < n; i++)
        {
            var r = related[i];
            rows.Add(SpineRow(r, setHub));
        }

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f,
            Children =
            [
                SpineHub(hub),
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

    static Element SpineHub(RelatedArtist hub) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
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
    };

    static Element SpineRow(RelatedArtist r, Action<RelatedArtist> setHub) => new BoxEl
    {
        Key = "mixview-spine:" + r.Uri,
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.XS, Spacing.XS),
        OnClick = () => setHub(r), Cursor = CursorId.Hand, Role = AutomationRole.Button,
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
    };

    // A node placed by its CENTRE: the ZStack anchors top-left, so the margin carries centre-minus-radius.
    static Element Node(RelatedArtist a, float cx, float cy, float r, bool isHub, Action? onClick)
    {
        float d = r * 2f;
        return new BoxEl
        {
            Width = d, Height = d, Shrink = 0f,
            AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
            Margin = new Edges4(cx - r, cy - r, 0f, 0f),
            Corners = Radii.Circle(d),
            BorderWidth = isHub ? 3f : 0f, BorderColor = Tok.AccentDefault,
            OnClick = onClick, Cursor = isHub ? CursorId.Arrow : CursorId.Hand,
            Role = isHub ? AutomationRole.None : AutomationRole.Button,
            Children = [Surfaces.Artwork(a.Image, SpotifyExportMapper.Hash(a.Uri), d, d, Radii.Full, decodePx: 128)],
        };
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
