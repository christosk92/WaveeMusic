using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Video;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// The Details rail body — now split into a PINNED hero (<see cref="NowPlayingHeroTile"/>, mounted separately by
/// <c>RightRail</c>) and everything that still scrolls (this class). Only the 324x324 cover-art tile was hoisted out;
/// title / meta / <c>SaveButton</c> / <c>NpvLyricsPeek</c> / credits / "Next up" are unchanged below, still built and
/// still scrolled by this component exactly as before.
///
/// <para><b>Why the split exists at all.</b> A composited video cannot live inside <c>ScrollView(...) with
/// { AutoEdgeFade = true }</c> (this class used to return exactly that, with the art tile as its first scrolled
/// child): <c>AutoEdgeFade</c> pushes an offscreen render target, and <c>DrawOp.DrawVideo</c> is a DestOut erase
/// against the BACK BUFFER — inside an offscreen layer the erase never lands and the video renders BLACK. See the
/// docked-video design's §1 for the other two reasons (the fling-lease disqualification, the rect-only ancestor
/// clip). Hoisting the tile out of the scroller is therefore mandatory, not cosmetic — do not re-nest it.</para>
///
/// <para><b>DELIBERATE BEHAVIOUR CHANGE:</b> the Details hero no longer scrolls away with the rest of the panel — it
/// is now always visible for as long as the Details rail is open, video docked or not. That is the accepted price of
/// the pinned slot (the docked-video design calls this out explicitly), not an oversight to "fix" later.</para>
/// </summary>
sealed class NowPlayingPanel : Component
{
    static readonly ColumnSet NextCols = new(Album: false, By: false, Date: false, Video: true, Plays: false, Heart: false, Thumb: false);

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var lib = UseContext(LibraryBridge.Slot);
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        bool showTrackArtwork = !AppearancePrefs.TrackArtworkHidden(svc?.Settings);

        var track = b?.CurrentTrack.Value;
        string artistUri = track is { Artists.Count: > 0 } ? track.Artists[0].Uri : "";
        string trackUri = track?.Uri ?? "";
        var infoL = UseResource(ct => LoadInfoAsync(svc, artistUri, trackUri, ct), (NowPlayingInfo?)null, (artistUri, trackUri)).Loadable;
        var loadState = (LoadState)infoL.State.Value;
        var info = infoL.Value.Value;
        // The uncapped kind-186 drawer, keyed on the track alone (the artist is irrelevant to it) and asked alongside
        // NPV, not instead of it: NPV still owns About / TopCities / merch. Its contributorsLimit:10 credit list is now
        // the FALLBACK, taken only when 186 has nothing to say (a 404, or offline).
        var fullCredits = UseResource(ct => LoadCreditsAsync(svc, trackUri, ct), (TrackCredits?)null, trackUri).Loadable.Value.Value;

        if (b is null) return new BoxEl { Grow = 1f };
        if (track is null) return Empty(Loc.Get(Strings.Player.NothingPlaying));

        // Cover colour for the hero is paint-only (bound Placeholder inside Hero) — do not Watch at panel scope.
        var sections = new List<Element>(8);
        if (info?.About is { } about)
        {
            sections.Add(AboutArtist(about, go));
            if (about.Extras?.TopCities is { Count: > 0 } cities)
                sections.Add(TopCities(cities));
        }
        else if (loadState == LoadState.Pending)
        {
            sections.Add(LoadingSection());
        }

        if (fullCredits is { Credits.Count: > 0 })
            sections.Add(Credits(fullCredits.Credits, fullCredits.Sources, go));
        else if (info?.Track?.Credits is { Count: > 0 } credits)
            sections.Add(Credits(credits, info.Track.CreditSources, go));

        var merch = info?.Track?.Merch is { Count: > 0 } tm ? tm : info?.About?.Extras?.Merch;
        if (merch is { Count: > 0 }) sections.Add(Merch(merch));

        var next = NextUp(b.Queue.Value);
        if (next.Count > 0) sections.Add(NextUpSection(next, b, lib, go, showTrackArtwork));

        var scrollBody = new BoxEl
        {
            Direction = 1,
            Children =
            [
                // The 324x324 art tile is no longer here — NowPlayingHeroTile below hosts it, pinned, outside this
                // ScrollView. HeroMeta is everything that was Hero() minus that tile: title/meta/SaveButton/lyrics
                // peek, still the first thing that scrolls.
                HeroMeta(track, lib, go),
                sections.Count == 0 ? new BoxEl()
                    : new BoxEl
                    {
                        Direction = 1, Gap = 16f,
                        Padding = new Edges4(Spacing.M, 0f, Spacing.M, Spacing.M),
                        Children = sections.ToArray(),
                    },
            ],
        };
        return ScrollView(scrollBody) with { Grow = 1f, AutoEdgeFade = true };
    }

    static async Task<NowPlayingInfo?> LoadInfoAsync(Services? svc, string artistUri, string trackUri, CancellationToken ct)
    {
        if (svc is null || artistUri.Length == 0 || trackUri.Length == 0) return null;
        try { return await svc.AlbumEnrichment.GetNowPlayingInfoAsync(artistUri, trackUri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    static async Task<TrackCredits?> LoadCreditsAsync(Services? svc, string trackUri, CancellationToken ct)
    {
        if (svc is null || trackUri.Length == 0) return null;
        try { return await svc.TrackCredits.GetAsync(trackUri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // The 324x324 cover-art tile, alone — moved verbatim out of the old Hero() so NowPlayingHeroTile (below) can
    // mount it PINNED, outside NowPlayingPanel's own ScrollView. Every property here (Placeholder=wash, BlurHash,
    // DecodePx=512, Cover fit, 1:1 aspect, Radii.Card corners on all four) is unchanged from before the split — the
    // panel must look and behave identically to the pre-split build whenever video is not docked here.
    internal static Element HeroArt(Track track)
    {
        string? url = track.Image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;
        // Bound placeholder: Watch is inside the thunk so a late grading paints without re-rendering the panel.
        Prop<ColorF> wash = Prop.Of(() => HeroWashColor(url));
        return new ImageEl
        {
            Source = url ?? "",
            Fit = ImageFit.Cover,
            AspectRatio = 1f,
            DecodePx = 512,
            Corners = CornerRadius4.All(Radii.Card),
            Placeholder = wash,
            BlurHash = track.Image?.BlurHash,
            AlignSelf = FlexAlign.Stretch,
        };
    }

    // Everything Hero() used to build BELOW the art tile — title, artist/album meta, SaveButton, NpvLyricsPeek —
    // unchanged, and still the first thing NowPlayingPanel's ScrollView scrolls.
    static Element HeroMeta(Track track, LibraryBridge? lib, Action<string, string?>? go)
    {
        var meta = new List<Element>(3);
        if (track.Artists.Count > 0)
            meta.Add(go is null
                // Body (14/20) for the artist line, Caption (12/16) for the album — the ramp's two prose rungs, in place
                // of the old 13/12 pair whose 13 was on no ramp at all.
                ? new TextEl(DetailFormat.ArtistNames(track.Artists)) { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                : HeroArtistLinks(track.Artists, go, Tok.TextSecondary));
        if (track.Album.Uri.Length > 0 && go is not null)
            meta.Add(new SpanTextEl([new TextSpan(track.Album.Name, OnClick: () => go("album:" + track.Album.Uri, track.Album.Name))])
            {
                Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        else if (track.Album.Name.Length > 0)
            meta.Add(new TextEl(track.Album.Name) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });

        return new BoxEl
        {
            Direction = 1,
            // LEFT/RIGHT S mirror the pinned tile's own inset (NowPlayingHeroTile) so the two stay flush; TOP M is
            // the Gap the art tile used to buy for free as Hero()'s first inter-child gap, now that the tile itself
            // owns no bottom padding of its own; BOTTOM L is unchanged from the pre-split Hero() padding.
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.L),
            Gap = Spacing.M,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f,
                            Children =
                            [
                                // Subtitle (20/28/600) — the panel's now-playing title rung (WaveeType.NowPlayingTitle's
                                // metrics). Was 22/850: an invented size and the app's only 850.
                                Ui.Subtitle(track.Title) with
                                {
                                    Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.CharacterEllipsis,
                                },
                                new BoxEl { Direction = 1, Gap = 2f, Children = meta.ToArray() },
                            ],
                        },
                        lib is null ? new BoxEl() : Embed.Comp(() => new SaveButton(track.Uri, 16f, 36f, track.Title)) with { Key = "save:" + track.Uri },
                    ],
                },
                Embed.Comp(() => new NpvLyricsPeek()) with { Key = "npv-lyrics:" + track.Id },
            ],
        };
    }

    static ColorF HeroWashColor(string? url)
    {
        if (url is { Length: > 0 })
            _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
        var palette = Surfaces.SchemeFor(url);
        ColorF accent = palette is { } p ? WaveePalette.Lift(WaveePalette.Accent(p)) : Tok.AccentDefault;
        return ColorF.Lerp(Tok.FillCardSecondary, accent, Tok.Theme == ThemeKind.Dark ? 0.18f : 0.10f);
    }

    static Element HeroArtistLinks(IReadOnlyList<ArtistRef> artists, Action<string, string?> go, ColorF color)
    {
        if (artists.Count == 0) return new BoxEl();
        var spans = new TextSpan[artists.Count * 2 - 1];
        int n = 0;
        for (int i = 0; i < artists.Count; i++)
        {
            if (i > 0) spans[n++] = new TextSpan(", ");
            var a = artists[i];
            spans[n++] = new TextSpan(a.Name, OnClick: () => go("artist:" + a.Uri, a.Name));
        }
        return new SpanTextEl(spans)
        {
            Size = 14f, LineHeight = 20f, Color = color, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f,
        };
    }

    static Element AboutArtist(Artist artist, Action<string, string?>? go)
    {
        const float heroH = 132f;
        static ColorF Scrim(float a) => ColorF.FromRgba(0, 0, 0) with { A = a };

        var facts = new List<Element>(3);
        if (artist.MonthlyListeners > 0) facts.Add(Fact(Count(artist.MonthlyListeners), Loc.Get(Strings.Artist.MetaMonthly)));
        if (artist.Followers > 0) facts.Add(Fact(Count(artist.Followers), Loc.Get(Strings.Artist.MetaFollowers)));
        if (artist.WorldRank > 0) facts.Add(Fact("#" + artist.WorldRank.ToString("N0"), Strings.Artist.WorldRank("").Trim()));

        Image? hero = artist.HeaderImage ?? artist.Image;
        string? heroUrl = hero?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;
        Element heroBand = heroUrl is { Length: > 0 }
            ? new BoxEl
            {
                Height = heroH, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(Radii.Card),
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 56f),
                Children =
                [
                    Ui.Image(heroUrl, ImageFit.Cover, 2.6f, 320, Radii.Card, Tok.FillSubtleSecondary, hero?.BlurHash),
                    new BoxEl
                    {
                        Height = heroH,
                        Corners = CornerRadius4.All(Radii.Card),
                        Gradient = GradientDown(
                            new GradientStop(0f, Scrim(0f)),
                            new GradientStop(0.55f, Scrim(0.12f)),
                            new GradientStop(1f, Scrim(0.62f))),
                    },
                ],
            }
            : new BoxEl
            {
                Height = 72f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = Tok.FillSubtleSecondary,
                Corners = CornerRadius4.All(Radii.Card),
                ClipToBounds = true,
                Children = [PersonPicture.Create("", 56f, displayName: artist.Name, imageSourcePath: artist.Image?.Url)],
            };

        var body = new List<Element>(5)
        {
            new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
                Children =
                [
                    // 18/24/600 — the ramp's BodyLarge rung at heading weight. This sits one step UNDER the section
                    // headers (Subtitle 20/28), which is exactly its role: a card heading inside a section.
                    new TextEl(artist.Name) { Size = 18f, LineHeight = 24f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    artist.Verified ? Icon(Icons.Check, 12f, Tok.AccentTextPrimary) : new BoxEl(),
                ],
            },
        };
        if (facts.Count > 0) body.Add(new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, Children = facts.ToArray() });
        if (!string.IsNullOrWhiteSpace(artist.Bio))
            body.Add(new TextEl(artist.Bio!) { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 5, Trim = TextTrim.CharacterEllipsis });
        body.Add(new BoxEl { Direction = 0, Children = [Embed.Comp(() => new FollowButton(artist.Uri, artist.Name)) with { Key = "follow:" + artist.Uri }] });

        return Section(Loc.Get(Strings.Detail.AboutTheArtist), new BoxEl
        {
            Direction = 1, Gap = 0f,
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary, ClipToBounds = true,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverFill = go is null ? Tok.FillCardSecondary : Tok.FillCardDefault,
            Cursor = go is null ? (CursorId?)null : CursorId.Hand,
            OnClick = go is null ? null : () => go("artist:" + artist.Uri, artist.Name),
            Children =
            [
                heroBand,
                new BoxEl { Direction = 1, Gap = 12f, Padding = Edges4.All(12f), Children = body.ToArray() },
            ],
        });
    }

    static Element TopCities(IReadOnlyList<TopCity> cities)
    {
        long max = Math.Max(1, cities.Max(c => c.Listeners));
        var rows = new List<Element>(Math.Min(5, cities.Count));
        for (int i = 0; i < cities.Count && rows.Count < 5; i++)
        {
            var c = cities[i];
            float frac = Math.Clamp(c.Listeners / (float)max, 0.08f, 1f);
            rows.Add(new BoxEl
            {
                Direction = 1, Gap = 4f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            new TextEl(c.Country is { Length: > 0 } country ? c.City + ", " + country : c.City)
                                { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(Count(c.Listeners)) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
                        ],
                    },
                    new BoxEl
                    {
                        Height = 4f, Corners = CornerRadius4.All(2f), Fill = Tok.FillSubtleSecondary, ClipToBounds = true,
                        Children = [new BoxEl { Width = 260f * frac, Height = 4f, Corners = CornerRadius4.All(2f), Fill = Tok.AccentDefault }],
                    },
                ],
            });
        }
        return Section(Loc.Get(Strings.Artist.ListenedMostIn), new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows.ToArray() });
    }

    static Element Credits(IReadOnlyList<TrackCredit> credits, IReadOnlyList<string> sources, Action<string, string?>? go)
    {
        var kids = new List<Element>(credits.Count + 2);
        foreach (var group in credits.GroupBy(c => string.IsNullOrWhiteSpace(c.RoleGroup) ? c.Role : c.RoleGroup!))
        {
            if (!string.IsNullOrWhiteSpace(group.Key))
                // The role-group name arrives from the server with its own casing — the eyebrow keeps it.
                kids.Add(WaveeType.Eyebrow(group.Key!) with { Color = Tok.TextTertiary });
            foreach (var c in group) kids.Add(CreditRow(c, go));
        }
        if (sources.Count > 0)
            kids.Add(new TextEl(Strings.Player.CreditsSource(string.Join(", ", sources)))
            {
                Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
            });
        return Section(Loc.Get(Strings.Player.Credits), new BoxEl { Direction = 1, Gap = 8f, Children = kids.ToArray() });
    }

    static Element CreditRow(TrackCredit c, Action<string, string?>? go)
    {
        Element name = c.Linkable && c.ArtistUri is { Length: > 0 } uri && go is not null
            ? new SpanTextEl([new TextSpan(c.Name, OnClick: () => go("artist:" + uri, c.Name))])
            {
                Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            }
            : new TextEl(c.Name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f,
            Children =
            [
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [name] },
                string.IsNullOrWhiteSpace(c.Role)
                    ? new BoxEl()
                    : new TextEl(c.Role) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    static Element Merch(IReadOnlyList<MerchItem> merch)
    {
        var rows = new List<Element>(Math.Min(4, merch.Count));
        for (int i = 0; i < merch.Count && rows.Count < 4; i++) rows.Add(MerchRow(merch[i]));
        return Section(Loc.Get(Strings.Artist.Merch), new BoxEl { Direction = 1, Gap = 8f, Children = rows.ToArray() });
    }

    static Element MerchRow(MerchItem item) => new BoxEl
    {
        Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(Spacing.S),
        Corners = CornerRadius4.All(Radii.Control), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
        Cursor = item.ShopUrl is { Length: > 0 } ? CursorId.Hand : (CursorId?)null,
        OnClick = item.ShopUrl is { Length: > 0 } url ? () => InputHooks.Current.Default.OpenUri?.Invoke(url) : null,
        Children =
        [
            new BoxEl
            {
                Width = 56f, Height = 56f, Shrink = 0f, Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                Children = [Surfaces.Artwork(item.Image, item.Name.GetHashCode() & 0x7fffffff, 56f, 56f, Radii.Control)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                Children =
                [
                    // BodyStrong name over a Caption price — two lines of 20 + 16 = 36, inside the 56 thumbnail beside it.
                    new TextEl(item.Name) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(item.Price.Length > 0 ? item.Price : Loc.Get(Strings.Artist.Buy)) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
        ],
    };

    static Element NextUpSection(IReadOnlyList<QueueEntry> next, PlaybackBridge b, LibraryBridge? lib,
                                 Action<string, string?>? go, bool showArtwork)
    {
        var rows = new List<Element>(next.Count);
        for (int i = 0; i < next.Count; i++)
        {
            var t = next[i].Track;
            var st = TrackRow.StateOf(b, lib, t);
            rows.Add(new BoxEl
            {
                Direction = 1, Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary,
                Children =
                [
                    TrackRow.ArtCard(t, st, NextCols, go,
                        onPlay: () => TrackRow.Invoke(b, t, () => b.Player.PlayTrackAsync(t)),
                        art: WaveeSize.ArtThumb,
                        showArtists: true,
                        explicitBadge: false,
                        showDuration: false,
                        kind: TrackRow.ArtCardKind.Rail,
                        showArtwork: showArtwork),
                ],
            });
        }
        return Section(Loc.Get(Strings.Player.NextUp), new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() });
    }

    static IReadOnlyList<QueueEntry> NextUp(IReadOnlyList<QueueEntry> queue)
        => queue.Where(e => e.Bucket is QueueBucket.UserQueue or QueueBucket.NextUp).Take(5).ToArray();

    // The rail's section header is WaveeType.RailHeader UNMODIFIED. It used to be overridden back down to 14, which
    // re-implemented BodyStrong and left the alias meaning two different things in two panels.
    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S,
        Children =
        [
            WaveeType.RailHeader(title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            body,
        ],
    };

    static Element Fact(string value, string label) => new BoxEl
    {
        // Two Caption rungs (600 over 400) in a Radii.Control pill on the 4-grid — was 12/800 over 10, in a 6-radius
        // pill with a 5px vertical pad.
        Direction = 1, Gap = Spacing.XXS, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = Radii.ControlAll, Fill = Tok.FillSubtleSecondary,
        Children =
        [
            new TextEl(value) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static Element LoadingSection() => Section(Loc.Get(Strings.Detail.AboutTheArtist), new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        Children =
        [
            new BoxEl { Height = 18f, Width = 180f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 12f, Width = 240f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
            new BoxEl { Height = 12f, Width = 210f, Corners = CornerRadius4.All(4f), Fill = Tok.FillSubtleSecondary },
        ],
    });

    // Rail scale (this panel is ~340 DIP wide) on the shared grammar.
    static Element Empty(string message) => EmptyState.Compact(message);

    static string Count(long n) => n.ToString("N0");
}

/// <summary>
/// The pinned Details hero — the square cover-art tile HOISTED OUT of <see cref="NowPlayingPanel"/>'s own
/// <c>ScrollView</c>, where it used to be the first scrolled child (<see cref="NowPlayingPanel.HeroArt"/> is that
/// same tile, moved verbatim). <c>RightRail</c> mounts this component separately, Shrink=0f, ABOVE
/// <see cref="NowPlayingPanel"/>'s own scrolled body, in the Details arm only. The hoist is what makes that slot
/// usable by the DOCKED VIDEO CARD, which cannot live inside a scroller at all: <c>AutoEdgeFade</c> erases the
/// composited hole, a scrolled <c>DrawVideo</c> disqualifies the fling lease, and ancestor clipping is rect-only
/// against the rail's rounded silhouette.
///
/// <para><b>This is the ART tile, and only the art tile.</b> It carries no video layer at all: while video is docked,
/// <c>RightRail.PinnedHero</c> mounts the rail's ONE docked card (<c>RightRail.DockedCap</c>) in this slot INSTEAD of
/// this component — a sibling that REPLACES the tile, never a layer over it. That is what lets the video keep its own
/// aspect at the full rail width in the Details body exactly as it does in every other body; a video layer inside this
/// tile could only ever be this tile's shape, which is a square inset <c>Spacing.S</c> per side.</para>
/// </summary>
sealed class NowPlayingHeroTile : Component
{
    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        var track = b?.CurrentTrack.Value;
        // Nothing playing: NowPlayingPanel itself shows Empty(...) with no hero at all, so the pinned slot must
        // collapse to nothing too — an empty square above "Nothing playing" would be a tile for no track.
        if (track is null) return new BoxEl();

        return new BoxEl
        {
            Shrink = 0f,
            // LEFT/TOP/RIGHT S, no bottom: the gap to whatever scrolls beneath is NowPlayingPanel.HeroMeta's own
            // top padding (see that method's comment) — this tile does not own the space between itself and the
            // scrolled content, so RightRail's two pinned tiles (this one and the Cap-face card) stay symmetric.
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
            // HeroArt alone — its own AspectRatio=1f ImageEl is what makes the tile square, at the rail's content
            // width. No ZStack and no video layer: see the class doc — the docked card REPLACES this whole component
            // in RightRail's hero slot rather than painting inside it.
            Children = [NowPlayingPanel.HeroArt(track)],
        };
    }
}
