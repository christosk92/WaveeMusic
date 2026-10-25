// ── Themes/WinUi/Structure/Views/Pages/WinUiArtistBanners.cs — WinUI's artist page banners (themes plan 6.1, T10b1) ────────
//
// From `Entities\Artist.Page.cs` §3.2-§3.3 (TopTracks.cs:106-253, Shelves.cs:27-51): the date-led upcoming card and its live countdown, the "just dropped"
// latest-release banner and the tour announcement. The section shell is `WinUiArtistSections.SectionBlock` (the profile page's sections draw it too).

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>Wavee WinUI's artist page banners.</summary>
internal static class WinUiArtistBanners
{
    // ── the upcoming release (TopTracks.cs:106-253) ──────────────────────────────────────────────────────────────────

    /// <summary>The date-led upcoming card, two arms: the rail COLUMN (<paramref name="wide"/>, no pick) and the full-width
    /// BAND. No Play, ever — a prerelease has nothing to stream; Pre-save and View only (W12).</summary>
    public static Element UpcomingCard(Artist a, Func<ColorF> accent, bool wide)
    {
        ColorF tint = accent();
        ref readonly ArtistPreRelease pre = ref a.PreRelease;
        string uri = ArtistVerbs.Text(pre.Uri);
        string name = ArtistVerbs.Text(pre.Name);
        string type = ArtistVerbs.Text(pre.Type);
        string? cover = Controls.ArtUrl(pre.Cover);
        int releaseAt = pre.ReleaseAt;
        string artistName = a.Name;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Sentence case: the type word is localized upstream and never caps-transformed.
        string eyebrowText = type.Length > 0 ? Loc.Get(Strings.Artist.Upcoming) + " · " + type : Loc.Get(Strings.Artist.Upcoming);
        // The eyebrow wraps (two lines at most) rather than run out of its box: in the Huge overview's third column the copy beside the cover, the
        // big date and the actions is under 100 wide ("Upcoming · Album" overflowed it after the 1920 crossing).
        TextEl eyebrow = R.Type.Eyebrow(eyebrowText) with { Color = tint, Wrap = TextWrap.WrapWholeWords, MaxLines = 2, MinWidth = 0f };
        TextEl title = R.Type.PickQuote(name) with
        {
            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        string? dateText = releaseAt > 0 ? Track.Format.ShortDate(releaseAt, now) : null;
        Element artistLine = Caption(artistName) with
        {
            Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        // The "releases …" caption joins the meta only on the BAND arm; in the column the big date IS the headline.
        Element meta = new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children = !wide && dateText is not null
                ? [artistLine, Caption(Strings.Detail.ReleasesOn(dateText)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }]
                : [artistLine],
        };
        TextEl? bigDate = dateText is null ? null : R.Type.SurfaceDisplay(dateText) with { MaxLines = 1 };

        Element actions = new BoxEl
        {
            Direction = 0, Gap = Spacing.S,
            Children =
            [
                Embed.Comp(() => new Controls.PreSaveButton { Uri = uri, Name = name, Accent = accent }) with { Key = "presave:" + uri },
                Button.Create(Loc.Get(Strings.Artist.View), () => ArtistVerbs.OpenEntity(uri, name),
                    ButtonAppearance.Outline, ControlSize.Small),
            ],
        }.Skeletonized(false);

        // The live clock only inside two weeks; keyed on uri + instant because the instant freezes at mount.
        Element? countdown = ArtistSections.ShowsCountdown(releaseAt, now)
            ? Embed.Comp(new UpcomingClockProps(releaseAt), static () => new UpcomingClock())
                with { Key = "artist-upcoming:" + uri + ":" + releaseAt.ToString(CultureInfo.InvariantCulture) }
            : null;

        Element content;
        if (wide)
        {
            int n = 4 + (bigDate is null ? 0 : 1) + (countdown is null ? 0 : 1);
            var col = new Element[n];
            int k = 0;
            col[k++] = eyebrow;
            if (bigDate is not null) col[k++] = bigDate.Value;
            col[k++] = title;
            col[k++] = meta;
            if (countdown is not null) col[k++] = countdown.Value;
            col[k] = actions;
            content = new BoxEl { Direction = 1, Padding = Edges4.All(Spacing.L), Gap = Spacing.S, Children = col };
        }
        else
        {
            Element coverBox = new BoxEl
            {
                Shrink = 0f,
                Children = [Controls.Artwork(cover, WinUiArtistRules.UpcomingCover, WinUiArtistRules.UpcomingCover, Radii.Control, decodePx: WinUiArtistRules.UpcomingCoverDecode)],
            };
            Element copy = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XS,
                Children = [eyebrow, title with { MaxLines = 1 }, meta],
            };
            Element actionCol = new BoxEl
            {
                Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.End, Shrink = 0f,
                Children = countdown is null ? [actions] : [actions, countdown.Value],
            };
            content = new BoxEl
            {
                Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
                Children = bigDate is null ? [coverBox, copy, actionCol] : [coverBox, copy, bigDate.Value with { Shrink = 0f }, actionCol],
            };
        }

        return new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    HitTestVisible = false,
                    Gradient = GradientDown(
                        new GradientStop(0f, tint with { A = 0.16f }),
                        new GradientStop(0.55f, tint with { A = 0.05f }),
                        new GradientStop(0.85f, tint with { A = 0f })),
                },
                content,
            ],
        };
    }

    sealed record UpcomingClockProps(int ReleaseAt);

    /// <summary>The pre-release countdown's LIVE host: <c>Controls.PreReleaseCountdown</c> is a static composition, so
    /// this one-second interval (auto-paused while parked or minimized) is what re-renders it.</summary>
    sealed class UpcomingClock : Component
    {
        public override Element Render()
        {
            var p = UseProps<UpcomingClockProps>();
            var tick = UseSignal(0);
            _ = tick.Value;
            UseInterval(() => tick.Value = tick.Peek() + 1, 1000f);
            long left = Math.Max(0L, p.ReleaseAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Controls.PreReleaseCountdown(TimeSpan.FromSeconds(left));
        }
    }

    // ── the latest-release banner and the tour banner ────────────────────────────────────────────────────────────────

    /// <summary>The "just dropped" full-width banner: ONE shared media surface (<see cref="Shape.Row"/> at a 72 cover) with
    /// the eyebrow "Single · date · N tracks" over the title. Contract: the whole row opens the release
    /// (<see cref="Track.GoToAlbum"/>), the hover play affordance toggles its context
    /// (<see cref="ArtistVerbs.PlayOrToggle(EntityId)"/>), the "…" is the container menu, and the row is a drag
    /// source. There is no separate View button.</summary>
    public static Element LatestBanner(Album al, ColorF accent)
    {
        string title = al.Title;
        string uri = al.Uri.Text;
        string? cover = Controls.ArtUrl(al.ImageId);
        string date = DiscoCardText.ReleaseDateLabel(al);
        int tracks = al.TrackCount;
        string eyebrow = Detail.Text.KindLabel(al.Kind)
                         + (date.Length > 0 ? " · " + date : "")
                         + (tracks > 0 ? " · " + Strings.Artist.TrackCount(tracks) : "");
        var album = al;
        var id = al.Id;
        var data = new Controls.CardData(uri, title, null, cover,
            OnClick: () => Track.GoToAlbum(album),
            OnPlay: () => ArtistVerbs.PlayOrToggle(id),
            Drag: Drag.Source(() => new DragPayload(DragKind.Album, uri, uri, title,
                new EntityRef(EntityKind.Album, album.Slot), ArtUrl: cover)))
        {
            Eyebrow = R.Type.Eyebrow(eyebrow) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Menu = () => Menus.Container(ActionTarget.ForAlbum(album.Uri, title), cover, eyebrow),
        };
        return Controls.Surface(data, Shape.Row(WinUiArtistRules.LatestCover) with { Plate = PlateKind.Tile });
    }

    /// <summary>The tour announcement (W20, Shelves.cs:27-51): a 44 accent circle, the commit-derived eyebrow /
    /// headline / subline, a chevron; the card opens the artist's concert schedule. Not a pivot destination.</summary>
    public static Element TourBanner(Artist a, ColorF accent)
    {
        var artist = a;
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.L, Padding = Edges4.All(Spacing.L),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
            OnClick = () => ArtistVerbs.OpenSchedule(artist),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered,
            Cursor = CursorId.Hand,
            Children =
            [
                new BoxEl
                {
                    Width = WinUiArtistRules.TourBadge, Height = WinUiArtistRules.TourBadge, Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(WinUiArtistRules.TourBadge / 2f), Fill = accent,
                    Children = [Icon(a.IsTourLive ? Icons.RadioTower : Icons.Calendar, WinUiArtistRules.TourIcon, Tok.TextOnAccentPrimary)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiArtistRules.TourGap,
                    Children =
                    [
                        R.Type.Eyebrow(ArtistVerbs.Text(a.TourEyebrowId)) with { Color = R.Ink.AccentDecor },
                        new TextEl(ArtistVerbs.Text(a.TourHeadlineId))
                            { Size = WinUiArtistRules.TourHeadline, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        R.Type.DenseMeta(ArtistVerbs.Text(a.TourSublineId)) with
                        {
                            Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                Icon(Icons.ChevronRight, WinUiArtistRules.TourChevron, Tok.TextSecondary),
            ],
        };
    }

    // ── the concert stub (a shelf card) ──

    /// <summary>A concert stub: the date lane (month in the accent over the day), the venue over its city, on a card; a click opens the concert.</summary>
    public static Element Stub(Concert c, Action onClick, float width)
    {
        var date = c.LocalDate;
        var (venue, city) = Concert.StubFacts(c);
        return new BoxEl
        {
            Key = "concert-stub:" + c.Slot.ToString(CultureInfo.InvariantCulture),
            Direction = 0, Width = width, Shrink = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(Spacing.M), Corners = Radii.CardAll, Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetBordered, Cursor = CursorId.Hand,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Width = WinUiArtistRules.StubDateW, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        R.Type.Eyebrow(date.ToString("MMM", CultureInfo.CurrentCulture)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                        new TextEl(date.Day.ToString(CultureInfo.CurrentCulture))
                        {
                            Size = WinUiArtistRules.StubDaySize, LineHeight = WinUiArtistRules.StubDayLine, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = WinUiArtistRules.StubTextGap,
                    Children =
                    [
                        new TextEl(venue)
                        {
                            Size = WinUiArtistRules.StubVenueSize, LineHeight = WinUiArtistRules.StubVenueLine, Weight = 600, Color = Tok.TextPrimary, MinWidth = 0f,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        new BoxEl
                        {
                            Direction = 0, Gap = WinUiArtistRules.StubPinGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children =
                            [
                                Icon(Icons.MapPin, WinUiArtistRules.StubPinSize, Tok.TextSecondary) with { Shrink = 0f },
                                new TextEl(city)
                                {
                                    Size = WinUiArtistRules.StubCitySize, LineHeight = WinUiArtistRules.StubCityLine, Color = Tok.TextSecondary, MinWidth = 0f,
                                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
