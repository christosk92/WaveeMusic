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
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The biography + profile-facts + "listened most in" two-column band.
sealed partial class ArtistPage : Component
{
    int _biographyMode;
    bool _biographyModeInitialized;

    bool BiographyWide(float width)
    {
        _biographyMode = DetailLayoutBreakpoints.ModeFor(width, _biographyMode, _biographyModeInitialized);
        _biographyModeInitialized = true;
        return _biographyMode == 0;
    }

    // ── biography + profile facts + listened-most-in (2-column band) ─────────────────────────────────────
    Element BiographyBand(Artist a, int albums, int singles, ArtistExtras? extras, int relatedCount, Action<string, string?> go) =>
        Responsive.Of(w =>
        {
            bool wide = BiographyWide(w);
            var left = new BoxEl
            {
                Direction = 1, Gap = Spacing.L, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN,
                MinWidth = 0f, ClipToBounds = true,
                Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
                Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    AccentHeader(Loc.Get(Strings.Artist.Biography)),
                    a.Bio is { Length: > 0 }
                        ? RichText.Of(a.Bio, 14f, Tok.TextSecondary, Tok.AccentTextPrimary, w * (wide ? 0.62f : 1f) - 60f, 14, key => go(key, null))
                        : new BoxEl(),
                    extras?.ExternalLinks is { Count: > 0 } links ? ExternalLinkPills(links) : new BoxEl(),
                    extras?.TopCities is { Count: > 0 } cities ? TopCitiesList(cities) : new BoxEl(),
                ],
            };
            // Profile-facts tiles — a zero/absent stat drops its WHOLE tile (never "0 Albums" / "0 Upcoming concerts"); if
            // none survive, the whole column is dropped so the biography takes the full width.
            var tiles = new List<Element>(7);
            void Tile(long value, string label) { if (value > 0) tiles.Add(StatTile(Count(value), label)); }
            Tile(a.MonthlyListeners, Loc.Get(Strings.Artist.Stat.Monthly));
            Tile(a.Followers, Loc.Get(Strings.Artist.Stat.Followers));
            Tile(albums, Loc.Get(Strings.Artist.Stat.Albums));
            Tile(singles, Loc.Get(Strings.Artist.Stat.Singles));
            Tile(extras?.Concerts?.Count ?? 0, Loc.Get(Strings.Artist.Stat.Concerts));
            Tile(relatedCount, Loc.Get(Strings.Artist.Stat.Related));
            var right = new BoxEl
            {
                Direction = 1, Gap = Spacing.M, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN,
                MinWidth = 0f,
                Children =
                [
                    AccentHeader(Loc.Get(Strings.Artist.ProfileFacts)),
                    new BoxEl { Direction = 0, Gap = Spacing.M, Wrap = true, Children = tiles.ToArray() },
                ],
            };
            return new BoxEl
            {
                Key = wide ? "artist-biography:wide" : "artist-biography:stacked",
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Children = tiles.Count > 0 ? new Element[] { left, right } : new Element[] { left },
            };
        }, fallback: 900f);

    static Element ExternalLinkPills(IReadOnlyList<ExternalLink> links) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, Wrap = true,
        Children = links.Select(l => (Element)new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(12f, 7f, 14f, 7f), Corners = CornerRadius4.All(Radii.Full),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillSubtleSecondary,
            Children = [ Icon(Icons.Link, 13f, Tok.TextSecondary), new TextEl(l.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary } ],
        }).ToArray(),
    };

    Element TopCitiesList(IReadOnlyList<TopCity> cities)
    {
        long max = 1;
        foreach (var c in cities) if (c.Listeners > max) max = c.Listeners;
        var rows = new List<Element>(cities.Count + 1) { WaveeType.Eyebrow(Loc.Get(Strings.Artist.ListenedMostIn)) with { Color = Tok.TextTertiary } };
        foreach (var c in cities) rows.Add(CityBarRow(c, max));
        return new BoxEl { Direction = 1, Gap = Spacing.S, Children = rows.ToArray() };
    }

    Element CityBarRow(TopCity c, long max)
    {
        float frac = max > 0 ? (float)((double)c.Listeners / max) : 0f;
        return new BoxEl
        {
            Direction = 1, Gap = 4f, Height = 34f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(c.City) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Count(c.Listeners)) { Size = 13f, Color = Tok.TextSecondary },
                    ] },
                new BoxEl { Direction = 0, Height = 4f,
                    Children =
                    [
                        new BoxEl { Grow = MathF.Max(0.001f, frac), Height = 4f, Corners = CornerRadius4.All(2f), Fill = _accent },
                        new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Height = 4f },
                    ] },
            ],
        };
    }

    static Element StatTile(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = Spacing.XS, Grow = 1f, Basis = 140f,
        Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.L),
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        // Title (28/36/600) over Caption — a stat tile's number is a heading, so it takes the heading rung rather than
        // an off-ramp 26/800. Basis is 140, so a 28px figure has room.
        Children = [Ui.Title(value) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, Ui.Caption(label)],
    };

}
