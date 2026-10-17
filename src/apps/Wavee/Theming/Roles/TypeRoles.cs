// ── Theming/Roles/TypeRoles.cs — the type roles of a style, and the factory a view builds text with (themes plan 5.4) ────
//
// Before T2a these were `Design.Type.*`: 26 factories over the engine's type ramp ("alias = rung + a few deviations"). The
// roles keep exactly that shape, as DATA a style supplies:
//   · `TextRole`   one role = the engine ramp rung it starts from + the deviations (face, size, line height, weight, tracking,
//                  auto-fit floor, secondary ink). `Make(text)` is the whole body of the old alias, so Classic's table (the same
//                  numbers, Themes\Classic\Style\ClassicRoles.cs) builds the same element the old alias did.
//   · `TypeRoles`  the table of roles (a record: equal tables compare equal, which is what keeps `Tok.Use` from calling a
//                  definition swap a change when nothing changed).
//   · `TypeFactory` what `R.Type` returns: the old call syntax (`R.Type.MicroMeta(s)`) over the active table. The table cannot
//                  carry the methods itself, because a role named `TrackTitle` is both a property of the table and a call.
//
// The three-part contract of the old file still holds for every role: a role resolves a SIZE, a LINE HEIGHT and a WEIGHT
// (from its rung unless it states them), never a bare size. COLOUR is not a type role: `Ui.*` rungs ship their own ink and
// the call site sets the rest (colour roles are T2b).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Theming.Roles;

/// <summary>The rung of the engine's type ramp (<c>Ui.Caption</c> .. <c>Ui.Display</c>) a role starts from. The ramp is the
/// active <c>ThemeDefinition.Type</c>, so a style that changes the ramp changes every role built on it.</summary>
public enum TypeRung : byte { Caption, Body, BodyStrong, BodyLarge, Subtitle, Title, TitleLarge, Display }

/// <summary>One text role: a ramp rung and the deviations from it. A null deviation keeps what the rung publishes.</summary>
/// <param name="Rung">The ramp rung the role starts from.</param>
/// <param name="Face">The font family (null = the rung's, the UI face).</param>
/// <param name="Size">The size in DIP (null = the rung's).</param>
/// <param name="LineHeight">The line height in DIP (null = the rung's). A role that changes <paramref name="Size"/> states this too.</param>
/// <param name="Weight">The font weight, 1-999 (null = the rung's).</param>
/// <param name="Tracking">The character spacing in 1/1000 em (null = none).</param>
/// <param name="MinSize">The auto-fit floor in DIP (null = off): the glyph run shrinks toward it before it wraps.</param>
/// <param name="Secondary">True to set the secondary ink after the deviations (the rung's own ink otherwise).</param>
public sealed record TextRole(
    TypeRung Rung,
    string? Face = null,
    float? Size = null,
    float? LineHeight = null,
    ushort? Weight = null,
    float? Tracking = null,
    float? MinSize = null,
    bool Secondary = false)
{
    /// <summary>The element for <paramref name="text"/>: the rung, then (when the role deviates) ONE copy carrying every
    /// deviation, then the secondary ink. One copy per role is what the old aliases did, so a text node costs the same.</summary>
    public TextEl Make(string text)
    {
        TextEl el = Rung switch
        {
            TypeRung.Caption => Ui.Caption(text),
            TypeRung.Body => Ui.Body(text),
            TypeRung.BodyStrong => Ui.BodyStrong(text),
            TypeRung.BodyLarge => Ui.BodyLarge(text),
            TypeRung.Subtitle => Ui.Subtitle(text),
            TypeRung.Title => Ui.Title(text),
            TypeRung.TitleLarge => Ui.TitleLarge(text),
            _ => Ui.Display(text),
        };
        if (Face is not null || Size.HasValue || LineHeight.HasValue || Weight.HasValue || Tracking.HasValue || MinSize.HasValue)
        {
            el = el with
            {
                FontFamily = this.Face ?? el.FontFamily,
                Size = this.Size ?? el.Size,
                LineHeight = this.LineHeight ?? el.LineHeight,
                Weight = this.Weight ?? el.Weight,
                CharSpacing = this.Tracking ?? el.CharSpacing,
                MinSize = this.MinSize ?? el.MinSize,
            };
        }
        return Secondary ? el.Secondary() : el;
    }
}

/// <summary>The look of the page-title facet row (<c>SelectorBar</c> restyled into a pivot): the type and geometry of its
/// words. The ink (rest, selected, hover, press, fills) is read from the tokens where the style is built until the colour
/// roles land (T2b). No pill: the selection is the weight and the ink.</summary>
public sealed record FacetRole(
    float LabelSize, float LineHeight, string? Face, float Tracking, int RestWeight, int SelectedWeight,
    Edges4 ItemPadding, float ItemHeight, float ItemGap, float LeadingInset);

/// <summary>The size a composition gives its panorama titles (<see cref="TypeRoles.WithPanorama"/>): the engine ramp's Display rung
/// (68/92) or its Title Large rung (40/52). The style's face and tracking for the panorama are kept either way.</summary>
public enum PanoramaSize : byte { Display68, TitleLarge40 }

/// <summary>The type roles of a style. Every style defines all of them (themes plan S2).</summary>
/// <remarks>The twelve roles after <c>FacetCompact</c> are the vocabulary a pivot-and-panorama structure reads (themes plan 5.4, contract
/// 1.26). A style that draws no panorama or pivot still gives each one a value: the closest voice it already has, so a structure that
/// reads one under that style draws the style's own type.
/// <list type="bullet">
/// <item><c>Panorama</c>: the page's name set larger than the column, the one voice that may bleed past it.</item>
/// <item><c>PanoramaCompact</c>: the panorama title where the page is too narrow for it (below <c>StyleMetrics.PanoramaCompactBelow</c>).</item>
/// <item><c>Hero</c>: a record page's hero title (an album, a playlist, Liked Songs): the record's name, not the place's.</item>
/// <item><c>Pivot</c> / <c>SubPivot</c>: a first-row pivot (the band's destinations, a page's primary pivots) / a second-row one (views,
/// categories, in-page tabs).</item>
/// <item><c>Numeral</c> / <c>NumeralLarge</c>: a row's number (a track's position, a chart rank) / a number that leads a feature row (the
/// artist page's Popular chart, an episode's chapters).</item>
/// <item><c>NowPlayingDisplay</c>: the full-screen now-playing title set as type.</item>
/// <item><c>Wordmark</c>: the app's name where the frame writes it.</item>
/// <item><c>LyricsLine</c>: a lyrics line on the full-size lyrics surface.</item>
/// <item><c>Heading</c>: a section heading inside a page.</item>
/// <item><c>QuickplayHead</c>: the head of a Home quickplay group.</item>
/// </list></remarks>
public sealed record TypeRoles(
    string DisplayFace,
    TextRole TrackTitle, TextRole CardTitle, TextRole TrackMeta, TextRole Eyebrow, TextRole MicroMeta, TextRole DenseMeta,
    TextRole DenseTitle, TextRole SheetTitle, TextRole RailHeader, TextRole ModuleHeader, TextRole PageHero, TextRole DetailHero,
    TextRole SurfaceDisplay, TextRole HeroTitle, TextRole ArtistDisplay, TextRole ArtistTitle, TextRole ArtistCompactTitle,
    TextRole NowPlayingTitle, TextRole PickQuote, TextRole FoldTitle, TextRole PivotLabel, TextRole NpvLyric, TextRole StatHero,
    FacetRole FacetTitle, FacetRole FacetCompact,
    TextRole Panorama, TextRole PanoramaCompact, TextRole Hero, TextRole Pivot, TextRole SubPivot, TextRole Numeral,
    TextRole NumeralLarge, TextRole NowPlayingDisplay, TextRole Wordmark, TextRole LyricsLine, TextRole Heading, TextRole QuickplayHead)
{
    /// <summary>This table with the panorama title on the ramp rung <paramref name="size"/> names: the rung's size, line height and weight,
    /// the style's face and tracking. A composition option that sizes the panorama applies it to the style's table (themes plan 6.4.2),
    /// so every call starts from the style's own role and the result never depends on an earlier call.</summary>
    public TypeRoles WithPanorama(PanoramaSize size) => this with
    {
        Panorama = Panorama with
        {
            Rung = size == PanoramaSize.TitleLarge40 ? TypeRung.TitleLarge : TypeRung.Display,
            Size = null, LineHeight = null, Weight = null, MinSize = null,
        },
    };

    /// <summary>This table with the big numerals on (<paramref name="on"/> true: the table as it is) or off: both numeral roles become the
    /// body rung (14/20), set in the call site's ink like every role. Applied to the style's table (themes plan 6.4.2), so "on" is the
    /// style's own numerals and never a restore of an earlier "off".</summary>
    public TypeRoles WithBigNumerals(bool on) => on ? this : this with
    {
        Numeral = new TextRole(TypeRung.Body),
        NumeralLarge = new TextRole(TypeRung.Body),
    };
}

/// <summary>The call syntax of the type roles: <c>R.Type.MicroMeta(text)</c> builds the active style's role. A view reads
/// <c>R.Type</c> where it builds the element (the table is re-read on every call, so a definition swap reaches the next
/// render), never into a field.</summary>
public readonly struct TypeFactory
{
    private readonly TypeRoles _roles;

    internal TypeFactory(TypeRoles roles) => _roles = roles;

    /// <summary>The display face every display-voice role sets.</summary>
    public string DisplayFace => _roles.DisplayFace;

    /// <summary>THE tracking every eyebrow carries (the Eyebrow role's), owned by that role and authored nowhere else.</summary>
    public float EyebrowTracking => _roles.Eyebrow.Tracking ?? 0f;

    /// <summary>Track / album / playlist titles in lists.</summary>
    public TextEl TrackTitle(string s) => _roles.TrackTitle.Make(s);

    /// <summary>A media card's headline (a playlist tile, a mix card, a feed card, a shelf cell).</summary>
    public TextEl CardTitle(string s) => _roles.CardTitle.Make(s);

    /// <summary>Artist, duration, metadata: the caption rung in the secondary ink.</summary>
    public TextEl TrackMeta(string s) => _roles.TrackMeta.Make(s);

    /// <summary>The small label that names what a card or section IS. Takes the string's own casing; the colour is the call site's.</summary>
    public TextEl Eyebrow(string s) => _roles.Eyebrow.Make(s);

    /// <summary>Badges, counts, timestamps, chart marks: the rung below caption.</summary>
    public TextEl MicroMeta(string s) => _roles.MicroMeta.Make(s);

    /// <summary>Credits, list metadata, drawer body: between caption and body.</summary>
    public TextEl DenseMeta(string s) => _roles.DenseMeta.Make(s);

    /// <summary>A title in a dense list: <see cref="DenseMeta"/>'s weight pair.</summary>
    public TextEl DenseTitle(string s) => _roles.DenseTitle.Make(s);

    /// <summary>A sheet or card heading, between body-strong and subtitle.</summary>
    public TextEl SheetTitle(string s) => _roles.SheetTitle.Make(s);

    /// <summary>Section and rail headers.</summary>
    public TextEl RailHeader(string s) => _roles.RailHeader.Make(s);

    /// <summary>A Home module header: the rail header's metrics in the display face.</summary>
    public TextEl ModuleHeader(string s) => _roles.ModuleHeader.Make(s);

    /// <summary>A module header plus its subdued fact, shaped as ONE paragraph so the small run sits on the heading's baseline.</summary>
    public SpanTextEl ModuleHeader(string title, string meta) => Paired(_roles.ModuleHeader, title, meta);

    /// <summary>A rail heading plus baseline-aligned compact metadata, in the UI face.</summary>
    public SpanTextEl RailHeader(string title, string meta) => Paired(_roles.RailHeader, title, meta);

    /// <summary>Page hero (playlist / album name).</summary>
    public TextEl PageHero(string s) => _roles.PageHero.Make(s);

    /// <summary>The two-column detail rail's identity title, and the vertical hero's.</summary>
    public TextEl DetailHero(string s) => _roles.DetailHero.Make(s);

    /// <summary>A library surface's masthead: the name of a place rather than of a record.</summary>
    public TextEl SurfaceDisplay(string s) => _roles.SurfaceDisplay.Make(s);

    /// <summary>The daylist card's title: the surface masthead's cut at the heading weight.</summary>
    public TextEl HeroTitle(string s) => _roles.HeroTitle.Make(s);

    /// <summary>Wide artist identity display.</summary>
    public TextEl ArtistDisplay(string s) => _roles.ArtistDisplay.Make(s);

    /// <summary>Medium artist identity title.</summary>
    public TextEl ArtistTitle(string s) => _roles.ArtistTitle.Make(s);

    /// <summary>Compact artist identity title.</summary>
    public TextEl ArtistCompactTitle(string s) => _roles.ArtistCompactTitle.Make(s);

    /// <summary>Now-playing track title.</summary>
    public TextEl NowPlayingTitle(string s) => _roles.NowPlayingTitle.Make(s);

    /// <summary>The artist speaking: the pick card's quote, set as editorial typography.</summary>
    public TextEl PickQuote(string s) => _roles.PickQuote.Make(s);

    /// <summary>The Fold tile's crop: named for the surface it sits on rather than the voice it carries.</summary>
    public TextEl FoldTitle(string s) => _roles.FoldTitle.Make(s);

    /// <summary>A pivot tab header (All / Music / Podcasts / Artists).</summary>
    public TextEl PivotLabel(string s) => _roles.PivotLabel.Make(s);

    /// <summary>The now-playing view's lyrics-peek reel.</summary>
    public TextEl NpvLyric(string s) => _roles.NpvLyric.Make(s);

    /// <summary>The panorama title (a pivot-and-panorama structure's page name).</summary>
    public TextEl Panorama(string s) => _roles.Panorama.Make(s);

    /// <summary>The panorama title on a page too narrow for <see cref="Panorama"/>.</summary>
    public TextEl PanoramaCompact(string s) => _roles.PanoramaCompact.Make(s);

    /// <summary>A record page's hero title.</summary>
    public TextEl Hero(string s) => _roles.Hero.Make(s);

    /// <summary>A first-row pivot.</summary>
    public TextEl Pivot(string s) => _roles.Pivot.Make(s);

    /// <summary>A second-row pivot.</summary>
    public TextEl SubPivot(string s) => _roles.SubPivot.Make(s);

    /// <summary>A row's number.</summary>
    public TextEl Numeral(string s) => _roles.Numeral.Make(s);

    /// <summary>A number that leads a feature row.</summary>
    public TextEl NumeralLarge(string s) => _roles.NumeralLarge.Make(s);

    /// <summary>The full-screen now-playing title set as type.</summary>
    public TextEl NowPlayingDisplay(string s) => _roles.NowPlayingDisplay.Make(s);

    /// <summary>The app's name where the frame writes it.</summary>
    public TextEl Wordmark(string s) => _roles.Wordmark.Make(s);

    /// <summary>A lyrics line on the full-size lyrics surface.</summary>
    public TextEl LyricsLine(string s) => _roles.LyricsLine.Make(s);

    /// <summary>A section heading inside a page.</summary>
    public TextEl Heading(string s) => _roles.Heading.Make(s);

    /// <summary>The head of a Home quickplay group.</summary>
    public TextEl QuickplayHead(string s) => _roles.QuickplayHead.Make(s);

    /// <summary>A glanceable stat: one number the reader takes in without reading. <paramref name="unit"/> is the small run
    /// that shares the numeral's baseline (built as ONE paragraph); null yields the numeral alone.</summary>
    public SpanTextEl StatHero(string value, string? unit)
    {
        TextEl heading = _roles.StatHero.Make("");
        TextEl caption = Ui.Caption("");
        TextSpan[] spans = unit is { Length: > 0 }
            ? [new TextSpan(value),
               new TextSpan("  " + unit, Weight: caption.ResolvedWeight, Color: Tok.TextSecondary, Size: caption.Size)]
            : [new TextSpan(value)];
        return Baseline(spans, heading);
    }

    /// <summary>The page-title facet row as a <c>SelectorBar</c> style: the stock bar restyled into the page's title pivot.
    /// A PROPERTY: every colour is a live token read, so a theme flip re-resolves it at the next render.</summary>
    public SelectorBarStyle FacetTitleStyle => Facet(_roles.FacetTitle);

    /// <summary><see cref="FacetTitleStyle"/> one rung down, for a facet row too narrow for the full words.</summary>
    public SelectorBarStyle FacetCompactStyle => Facet(_roles.FacetCompact);

    private static SelectorBarStyle Facet(FacetRole f) => new()
    {
        LabelSize = f.LabelSize,
        LineHeight = f.LineHeight,
        FontFamily = f.Face,
        CharSpacing = f.Tracking,
        RestWeight = f.RestWeight,
        SelectedWeight = f.SelectedWeight,
        RestColor = Tok.TextSecondary,
        SelectedColor = Tok.TextPrimary,
        HoverColor = Tok.TextPrimary,
        PressedColor = Tok.TextSecondary,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        ShowPill = false,
        ItemPadding = f.ItemPadding,
        ItemHeight = f.ItemHeight,
        ItemGap = f.ItemGap,
        LeadingInset = f.LeadingInset,
    };

    // The ONE baseline-paired construction: a heading run and a small run in one paragraph. NoWrap + CharacterEllipsis +
    // MaxLines 1 + MinWidth 0 + Shrink 1, because a baseline-paired span must never wrap (the small run would land alone on
    // line 2 with no heading to sit on). The heading's metrics are the role's, built once from an empty string.
    private static SpanTextEl Paired(TextRole role, string title, string meta)
    {
        TextEl heading = role.Make("");
        TextEl caption = Ui.Caption("");
        return Baseline(
        [
            new TextSpan(title),
            new TextSpan("  " + meta, Weight: caption.ResolvedWeight, Color: Tok.TextTertiary, Size: caption.Size),
        ], heading);
    }

    private static SpanTextEl Baseline(TextSpan[] spans, TextEl heading) => new(spans)
    {
        FontFamily = heading.FontFamily,
        CharSpacing = heading.CharSpacing,
        Size = heading.Size,
        Weight = heading.ResolvedWeight,
        LineHeight = heading.LineHeight,
        LineStacking = heading.LineStacking,
        LineBounds = heading.LineBounds,
        Wrap = TextWrap.NoWrap,
        Trim = TextTrim.CharacterEllipsis,
        MaxLines = 1,
        MinWidth = 0f,
        Shrink = 1f,
    };
}
