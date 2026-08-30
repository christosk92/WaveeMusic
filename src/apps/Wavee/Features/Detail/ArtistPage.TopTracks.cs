using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The hero-adjacent two-column Top tracks ledger plus a narrow rail for artist-authored/time-sensitive objects.
sealed partial class ArtistPage : Component
{
    const float TopBandWideW = 760f;
    const float TopBandHysteresis = 24f;   // == DetailLayoutBreakpoints.TierHysteresisDip, by intent
    // Latched across renders so a slow drag across ~760 does not flip the supporting rail on every 0.5px
    // ResponsiveBox rebuild. A plain FIELD, not a signal: it is derived purely from `w`
    // (idempotent for a given width) and must not schedule a render, so writing it from the build lambda is not a
    // backwards write. One ArtistPage instance is alive at a time and it is reused across artist→artist hops — which
    // is correct, because the window width does not change on navigation.
    bool _topBandWide = true;
    bool TopBandWide(float w)
        => _topBandWide = _topBandWide ? w >= TopBandWideW - TopBandHysteresis : w >= TopBandWideW;

    // Top tracks retain the native two-column PagedShelf. The supporting rail carries ONE featured object: an
    // Artist pick (with the upcoming record riding it as a merged S3 footer row when both exist), or — when there
    // is no pick — a standalone Zune date-led upcoming card. Never both sections at once: stacking pick + upcoming
    // in the rail out-ran Top tracks and left a dead band beside it. Latest release stays its own wide banner
    // above Albums (see ArtistPage.cs Body).
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    PinnedItem? pinned, Image? artistImage, Image? artistBackground, string artistName,
                    ArtistPreRelease? upcoming,
                    Action<string, string?> go, Action<string> play, Func<ColorF> accent)
    {
        bool showTrackArtwork = !AppearancePrefs.TrackArtworkHidden(svc.Settings);
        bool classic = svc.Settings.Get(WaveeSettings.TrackRowStyle) == 1;
        return Responsive.Of(w =>
        {
            bool wide = TopBandWide(w);
            string popTitle = Loc.Get(Strings.Artist.TopTracks);
            Element tracks = Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc, popTitle, accent))
                with { SkeletonProxy = () => ArtistPopular.SkeletonShape(popular, popTitle, showTrackArtwork, classic) };
            Element featured = FeaturedColumn(pinned, artistImage, artistBackground, artistName, upcoming, go, play, accent, wide);
            bool hasFeatured = pinned is not null || upcoming is { IsUpcoming: true };

            if (!hasFeatured)
                return new BoxEl { Direction = 1, Children = [tracks] };

            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN,
                        MinWidth = 0f, Children = [tracks],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN,
                        MinWidth = 0f, Children = [featured],
                    },
                ],
            };
        }, fallback: 900f);
    }

    // The pick owns this column outright. A standalone Upcoming card renders here only when there is NO pick —
    // when both exist, the announcement moves to its own full-width band above Latest release (ArtistPage.cs Body),
    // because two stacked cards made the rail out-run Top tracks and left a dead band beside them. `wide` is folded
    // into the child's Key: a tier crossing remounts (props freeze at mount).
    Element FeaturedColumn(PinnedItem? pinned, Image? artistImage, Image? artistBackground, string artistName,
                           ArtistPreRelease? upcoming, Action<string, string?> go,
                           Action<string> play, Func<ColorF> accent, bool wide)
    {
        if (pinned is { } pick)
        {
            string target = RichText.RouteForUri(pick.TargetUri) ?? ("album:" + pick.TargetUri);
            return Section(Loc.Get(Strings.Artist.ArtistPick),
                MediaCard.ArtistPick(pick, artistName, artistImage, artistBackground,
                    onClick: () => go(target, pick.Title),
                    onPlay: () => play(pick.TargetUri),
                    accent: accent,
                    horizontal: !wide,
                    // A pinned item can point at any entity, so the kind comes from the uri — the same discrimination
                    // `target` above used, so the drag payload can never disagree with the click's destination.
                    drag: CardDrag(WaveeDragKindMap.OfUri(pick.TargetUri), pick.TargetUri, pick.Title, pick.Cover)))
                with { Key = "featured:pick:" + (wide ? "rail" : "band") };
        }
        if (upcoming is { IsUpcoming: true } next)
            return Section(Loc.Get(Strings.Artist.Upcoming), UpcomingCard(next, artistName, wide, go, accent))
                with { Key = "featured:upcoming:" + (wide ? "rail" : "band") };
        return new BoxEl();
    }

    // The Zune date-led upcoming card: the date is the headline, not a chip. Two arms on one tone plate —
    // P1 column when it sits in the wide featured rail (no pick), P2 horizontal band everywhere full-width:
    // the stacked featured slot, and the page-level band above Latest release that hosts it when the pick
    // owns the rail (ArtistPage.cs Body).
    static Element UpcomingCard(ArtistPreRelease p, string artistName, bool wide, Action<string, string?> go, Func<ColorF> accent)
    {
        // Either scheme can land here: preReleaseV2 hands back an ALBUM uri on every capture so far, but a
        // spotify:prerelease: one is equally valid (the two ids DIFFER — neither can be synthesised from the other).
        // RouteForUri routes both; the literal fallback keeps a uri it cannot classify on the album route rather than
        // on the generic "Coming soon" stub, which is where a bare spotify: uri lands.
        string route = RichText.RouteForUri(p.Uri) ?? ("album:" + p.Uri);
        // Sentence case throughout, including the release TYPE: the sibling tokens it used to match ("ALBUM" out of
        // KindLabel) are LOCALIZED strings, and upper-casing those is the exact defect the eyebrow role gave up.
        // Absent type → the bare word, never a dangling separator.
        string eyebrowText = p.Type is { Length: > 0 } type
            ? Loc.Get(Strings.Artist.Upcoming) + " · " + type
            : Loc.Get(Strings.Artist.Upcoming);
        ColorF tint = accent();

        TextEl eyebrow = WaveeType.Eyebrow(eyebrowText) with { Color = tint, MaxLines = 1 };
        TextEl title = WaveeType.PickQuote(p.Name) with
        {
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var metaKids = new List<Element>(2)
        {
            Ui.Caption(artistName) with
            {
                Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        };
        // Announced-but-undated is a real state on the wire — then the card announces without promising a day.
        // The "releases …" caption joins the meta only on the HORIZONTAL arm, where the big date sits away from the
        // copy at the row's far end; in the P1 column the date IS the headline two lines up, and repeating it in
        // caption ink directly underneath said the same fact twice.
        TextEl? bigDate = null;
        if (p.ReleaseAt is { } dated)
        {
            if (!wide)
                metaKids.Add(Ui.Caption(Strings.Detail.ReleasesOn(DetailFormat.ShortDate(dated))) with
                {
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                });
            bigDate = WaveeType.SurfaceDisplay(DetailFormat.ShortDate(dated)) with { MaxLines = 1 };
        }
        Element meta = new BoxEl { Direction = 1, MinWidth = 0f, Children = metaKids.ToArray() };

        // NO Play button, and there must never be one: a prerelease uri must never reach PlayAsync — there is
        // nothing to stream yet, and the sibling masthead's Play slot is taken here by the only action the record
        // can honour before it drops.
        //
        // PROPS FREEZE AT MOUNT, so the embed is keyed on the uri: this column is rebuilt whenever extras arrive,
        // and an unkeyed PreSaveButton would keep resolving (and pre-saving) whichever uri happened to be current
        // at mount.
        Element actions = new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            Children =
            [
                Embed.Comp(() => new PreSaveButton { Uri = p.Uri, Name = p.Name, Accent = accent })
                    with { Key = "presave:" + p.Uri },
                Button.Create(Loc.Get(Strings.Artist.View), () => go(route, p.Name),
                    ButtonAppearance.Outline, ControlSize.Small),
            ],
        }.Skeletonized(false);

        // The LIVE clock (UseInterval, auto-paused while the page is parked or the window minimized). Only inside
        // two weeks: further out, the big date alone speaks — a month of "327 hrs" is noise. Keyed on uri + instant
        // because ReleaseAt freezes at mount.
        Element? countdown = p.ReleaseAt is { } due
            && due > DateTimeOffset.UtcNow
            && due - DateTimeOffset.UtcNow <= TimeSpan.FromDays(14)
            ? Embed.Comp(() => new PreReleaseCountdown { ReleaseAt = due, Accent = accent, Bare = true })
                with { Key = "artist-upcoming:" + p.Uri + ":" + due.UtcTicks.ToString(CultureInfo.InvariantCulture) }
            : null;

        Element content;
        if (wide)
        {
            var col = new List<Element>(6) { eyebrow };
            if (bigDate is { } d) col.Add(d);
            col.Add(title);
            col.Add(meta);
            if (countdown is { } clock) col.Add(clock);
            col.Add(actions);
            content = new BoxEl
            {
                Direction = 1, Padding = Edges4.All(Spacing.L), Gap = Spacing.S,
                Children = col.ToArray(),
            };
        }
        else
        {
            // Exactly one elastic lane (the copy column) — cover, date and actions never give.
            var row = new List<Element>(4)
            {
                new BoxEl
                {
                    Width = 88f, Height = 88f, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Control),
                    Children =
                    [
                        Surfaces.Artwork(p.Cover, p.Uri.GetHashCode() & 0x7fffffff, 88f, 88f, Radii.Control, decodePx: 192),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                    Children = [eyebrow, title with { MaxLines = 1 }, meta],
                },
            };
            if (bigDate is { } d) row.Add(d with { Shrink = 0f });
            var actionCol = new List<Element>(2) { actions };
            if (countdown is { } clock) actionCol.Add(clock);
            row.Add(new BoxEl
            {
                Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.End, Shrink = 0f,
                Children = actionCol.ToArray(),
            });
            content = new BoxEl
            {
                Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center,
                Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
                MinWidth = 0f,
                Children = row.ToArray(),
            };
        }

        return new BoxEl
        {
            ZStack = true, ClipToBounds = true,
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    HitTestVisible = false,
                    // 0.16 is the Tok.AccentSubtle alpha.
                    Gradient = GradientDown(
                        new GradientStop(0f, tint with { A = 0.16f }),
                        new GradientStop(0.55f, tint with { A = 0.05f }),
                        new GradientStop(0.85f, tint with { A = 0f })),
                },
                content,
            ],
        };
    }

    // The "just dropped" wide banner — own top-level section pinned above Albums (see ArtistPage.cs Body), not a
    // narrow rail card sharing a column with Artist Pick. Cover + eyebrow (type · date · track count) + title read
    // left-to-right at full band width, with Play/View as an explicit trailing action pair (never a whole-card
    // onClick — a nested Play target inside a clickable card is the recurring source of accidental navigations
    // elsewhere in this file, so this follows UpcomingMasthead's leaf-buttons-only idiom instead of MediaCard.Compact's).
    // Instance (not static) so it can reach the page's `_acts` for its drag payload — the same reason CardMenu is.
    Element LatestReleaseBanner(Album al, Action<string, string?> go, Action<string> play, Func<ColorF> accent)
    {
        // KindLabel is LOCALIZED ("Album" / "Single" / "Compilation") — never caps-transformed.
        var metaParts = new List<string>(3) { KindLabel(al.Kind) };
        string date = ReleaseDateLabel(al);
        if (date.Length > 0) metaParts.Add(date);
        if (al.TrackCount > 0) metaParts.Add(Strings.Artist.TrackCount(al.TrackCount));
        string eyebrow = string.Join(" · ", metaParts);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(Spacing.M), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Draggable = CardDrag(WaveeResourceKind.Album, al.Uri, al.Name, al.Cover),
            Children =
            [
                new BoxEl
                {
                    Width = 72f, Height = 72f, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Control),
                    Children = [Surfaces.Artwork(al.Cover, al.Uri.GetHashCode() & 0x7fffffff, 72f, 72f, Radii.Control, decodePx: 144)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f,
                    Children =
                    [
                        // The eyebrow over a Subtitle (20/28/600) headline — this is the page's one "news" banner, so
                        // it takes the shelf-header rung rather than an off-ramp 17. Eyebrow 16 + gap 4 + subtitle
                        // 28 = 48, comfortably inside the 72-DIP cover beside it.
                        WaveeType.Eyebrow(eyebrow) with { Color = Tok.TextTertiary, MaxLines = 1 },
                        Ui.Subtitle(al.Name) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Shrink = 0f,
                    Children =
                    [
                        WaveeCta.Play(accent(), () => play(al.Uri), Loc.Get(Strings.Artist.Play)),
                        Button.Create(Loc.Get(Strings.Artist.View), () => go("album:" + al.Uri, al.Name),
                            ButtonAppearance.Outline, ControlSize.Small),
                    ],
                }.Skeletonized(false),
            ],
        };
    }

    static string ReleaseDateLabel(Album al)
    {
        if (al.ReleaseDate is { Length: > 0 } iso &&
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return (al.ReleaseDatePrecision ?? "").ToUpperInvariant() switch
            {
                "YEAR" => date.ToString("yyyy", CultureInfo.InvariantCulture),
                "MONTH" => date.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                _ => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
            };
        }
        return al.Year > 0 ? al.Year.ToString(CultureInfo.InvariantCulture) : "";
    }

    internal static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };
}
