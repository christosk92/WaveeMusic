// ── Themes/Zune/Structure/Views/Artist/ZuneArtistBanners.cs — the artist page's rail and banners (themes plan 6.3; P5 Z6a2) ──────────────
//
// Board Z-Artist's rail beside the chart: "latest release" (a 96 square art, the title in the card voice, "album · 2025 · 22 songs" under it) and
// "artist pick" (a 64 art, the title, the artist's quote under it), each under a 26/32 light heading and each ONE shared media surface (Zune's
// own: square, a 5 % wash on hover, the play ring on the art, the corner "..." menu). An upcoming release (a pre-release) takes the same place
// above them: its art, the title, "upcoming · type · date" and, inside two weeks, the live countdown, with Pre-save beside the row. The tour
// announcement is a flat row (a 40 accent disc, the core's eyebrow / headline / subline, a chevron); there is no card.
//
// What a card opens, plays or puts in its menu is `ArtistVerbs` and the handles' own commands; the clickable shell, the play ring, the drag and
// the menu are the surface's. Words the theme writes take their casing from the case roles; titles, names and the artist's quote are data.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Zune.ZuneArtistRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's artist rail items and banners.</summary>
internal static class ZuneArtistBanners
{
    /// <summary>The surface's own 8 padding leaves the rail's art 8 in from the heading: a rail row stands flush, its art on the heading's x and
    /// 10 under it (the board), so the wash on hover overhangs the column by the padding.</summary>
    static Element Flush(Element row) => new BoxEl { Direction = 1, MinWidth = 0f, Margin = Edges4.All(-SurfaceGeometry.RowPad), Children = [row] };

    // ── the latest release ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>"album · 2025 · 22 songs": the kind, the year (when known) and the track count (when known) as one lowercase meta line.</summary>
    static string KindLine(Album al)
    {
        string line = Detail.Text.KindLabel(al.Kind);
        if (al.Year > 0) line += " · " + al.Year.ToString(CultureInfo.CurrentCulture);
        if (al.TrackCount > 0) line += " · " + Strings.Artist.TrackCount(al.TrackCount);
        return line;
    }

    /// <summary>The latest release as a row: its art, the title and the kind line. The whole row opens it; the ring on the art plays it.</summary>
    public static Element Latest(Album al, float art, bool stacked = false)
    {
        string title = al.Title;
        string uri = al.Uri.Text;
        string? cover = Controls.ArtUrl(al.ImageId);
        string kind = KindLine(al);
        var album = al;
        var id = al.Id;
        var data = new Controls.CardData(uri, title, R.Type.TrackMeta(kind) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, cover,
            OnClick: () => Track.GoToAlbum(album),
            OnPlay: () => ArtistVerbs.PlayOrToggle(id),
            Drag: Drag.Source(() => new DragPayload(DragKind.Album, uri, uri, title, new EntityRef(EntityKind.Album, album.Slot), ArtUrl: cover)))
        {
            Menu = () => Menus.Container(ActionTarget.ForAlbum(album.Uri, title), cover, kind),
        };
        // Small's rail is narrow (220): the cover stands over its words (Z-Artist-Small), a shelf card at the art's width.
        if (stacked) return Controls.Surface(data, Shape.Shelf(captionLines: 1), art);
        return Flush(Controls.Surface(data, ZuneListRules.RailRow(art)));
    }

    // ── the artist pick ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The artist-authored pinned item as a row: its art, the title, the artist's quote under it (two lines). Pre-save stands at the end
    /// while the record is not out; the row opens the record, the ring plays it, the menu is the record's own.</summary>
    public static Element Pick(Artist a, Func<ColorF> accent, float art)
    {
        ref readonly ArtistPick pick = ref a.Pick;
        string title = ArtistVerbs.Text(pick.Title);
        string kind = ArtistVerbs.Text(pick.Subtitle);
        string comment = ArtistVerbs.Text(pick.Comment.IsEmpty ? pick.Eyebrow : pick.Comment);
        string item = ArtistVerbs.Text(pick.ItemUri);
        string target = item.Length > 0 ? item : ArtistVerbs.Text(pick.Uri);
        string? cover = Controls.ArtUrl(pick.Cover);
        int releaseAt = pick.ReleaseAt;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var targetUri = EntityUri.Parse(target);
        string quote = comment.Length > 0 ? comment : kind;

        Element? line = quote.Length == 0 ? null : R.Type.TrackMeta(quote) with
        {
            Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var data = new Controls.CardData(target, title, line, cover,
            OnClick: () => ArtistVerbs.OpenEntity(targetUri, title),
            OnPlay: () => ArtistVerbs.PlayOrToggle(target),
            Drag: Drag.Source(() => new DragPayload(Drag.KindOfUri(target), target, target, title, ArtUrl: cover)))
        {
            Menu = () => ArtistVerbs.PickMenu(targetUri, title, cover, kind),
            Trailing = releaseAt > now
                ? Embed.Comp(() => new Controls.PreSaveButton { Uri = target, Name = title, Accent = accent }) with { Key = "zune-pick-presave:" + target }
                : null,
        };
        return Flush(Controls.Surface(data, ZuneListRules.RailRow(art)));
    }

    // ── the upcoming release ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An upcoming release: a pre-release has nothing to stream, so no play ring: the row opens it (View), Pre-save and the countdown
    /// (inside two weeks) stand under it.</summary>
    public static Element Upcoming(Artist a, Func<ColorF> accent, float art)
    {
        ref readonly ArtistPreRelease pre = ref a.PreRelease;
        string uri = ArtistVerbs.Text(pre.Uri);
        string name = ArtistVerbs.Text(pre.Name);
        string type = ArtistVerbs.Text(pre.Type);
        string? cover = Controls.ArtUrl(pre.Cover);
        int releaseAt = pre.ReleaseAt;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string? date = releaseAt > 0 ? Track.Format.ShortDate(releaseAt, now) : null;
        string meta = type.Length > 0 && date is not null ? type + " · " + date : type.Length > 0 ? type : date ?? "";

        var data = new Controls.CardData(uri, name, meta.Length == 0 ? null : R.Type.TrackMeta(meta) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, cover,
            OnClick: () => ArtistVerbs.OpenEntity(uri, name));
        Element row = Flush(Controls.Surface(data, ZuneListRules.RailRow(art)));

        Element actions = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.RailItemGap, MinWidth = 0f,
            Children =
            [
                Embed.Comp(() => new Controls.PreSaveButton { Uri = uri, Name = name, Accent = accent }) with { Key = "zune-presave:" + uri },
                ArtistSections.ShowsCountdown(releaseAt, now)
                    ? Embed.Comp(new ClockProps(releaseAt), static () => new Clock())
                        with { Key = "zune-upcoming:" + uri + ":" + releaseAt.ToString(CultureInfo.InvariantCulture) }
                    : new BoxEl(),
            ],
        }.Skeletonized(false);
        return new BoxEl { Direction = 1, Gap = Rules.RailItemGap, MinWidth = 0f, Children = [row, actions] };
    }

    sealed record ClockProps(int ReleaseAt);

    /// <summary>The pre-release countdown's LIVE host: <c>Controls.PreReleaseCountdown</c> is a static composition, so this one-second interval
    /// (paused while parked or minimized) is what re-renders it.</summary>
    sealed class Clock : Component
    {
        public override Element Render()
        {
            var p = UseProps<ClockProps>();
            var tick = UseSignal(0);
            _ = tick.Value;
            UseInterval(() => tick.Value = tick.Peek() + 1, 1000f);
            long left = Math.Max(0L, p.ReleaseAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Controls.PreReleaseCountdown(TimeSpan.FromSeconds(left));
        }
    }

    // ── the tour announcement ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tour announcement: a flat row, a 40 accent disc with its glyph, the core's eyebrow / headline / subline (data), a chevron. It
    /// opens the artist's concert schedule.</summary>
    public static Element Tour(Artist a)
    {
        var artist = a;
        return new BoxEl
        {
            Key = "zune-artist-tour", Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.HeadInlineGap, MinWidth = 0f,
            Padding = new Edges4(Rules.RailItemGap, Rules.RailItemGap, Rules.RailItemGap, Rules.RailItemGap),
            HoverFill = R.Layer.RowHover, PressedFill = R.Layer.RowPressed,
            OnClick = () => ArtistVerbs.OpenSchedule(artist),
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = Design.FocusInsetRow, Cursor = CursorId.Hand,
            Children =
            [
                new BoxEl
                {
                    Width = Rules.TourBadge, Height = Rules.TourBadge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(Radii.Full), Fill = Prop.Of(static () => R.Ink.AccentAction),
                    Children =
                    [
                        new TextEl(a.IsTourLive ? Icons.RadioTower : Icons.Calendar)
                        {
                            Size = Rules.TourGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                            Color = Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)),
                        },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.TourGap,
                    Children =
                    [
                        R.Type.MicroMeta(ArtistVerbs.Text(a.TourEyebrowId)) with
                        {
                            Color = Prop.Of(static () => R.Ink.AccentAction), MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        R.Type.TrackTitle(ArtistVerbs.Text(a.TourHeadlineId)) with
                        {
                            Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        R.Type.DenseMeta(ArtistVerbs.Text(a.TourSublineId)) with
                        {
                            Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                    ],
                },
                Icon(Icons.ChevronRight, Rules.TourChevron, Tok.TextSecondary),
            ],
        };
    }
}
