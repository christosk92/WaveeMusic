// ── Themes/Classic/Structure/Views/Stage/ClassicStageArtistPane.cs — Classic's `Stage.ArtistPane` (themes plan 6.1, T7a2) ──────
//
// From `Shell\Stage.Artist.UI.cs`: the stage's Artist pane, the header band (the artist's photo across the pane, name, verified, listeners · rank,
// tour, Follow / Go to artist), then About (the whole bio, Read more) + the gallery strip beside This song (credits grouped by person), Popular
// (top five, playable), Where people listen and Fans also like. What the sections read from the entity tables, and the decisions (the column
// split, the header height, the reading measure, how many avatars fit: `Stage.ArtistRules`), are the core's: they arrive as facts the sections
// call inside their own renders, so a publication re-renders only the section that paints it; the root re-renders on a track, layout or
// scope change. A fact that answers null (or no rows) is "no section": the pane leaves no gap for it.
//
//   ScrollEl[ header · Row[ About(bio · gallery) | This song · Popular · Where people listen · Fans also like ] ]
//   narrow (PaneW < 1100): ScrollEl[ header · This song · Popular · About ]
//
//   • All ink is StageInk (Ink · Plate · Stroke · Veil); the accent is the context's AccentSet. No white literal anywhere.
//   • The header photo is laid out at the pane's own width and a height tied to the pane (0.3 · PaneH, clamped), cover-fit:
//     never a fixed 1200 × 240 slot leaving grey beside it.
//   • Every size is a base DIP times the pane's type scale (`Stage.Layout.QueueScale`), rounded (`ClassicStageArtistKit.Px`).
//   • Loading, empty and error states are the pane's own: nothing playing says so; a section whose facts have not landed (or that has none)
//     is absent and takes no space; navigation CLOSES the stage first (V-U29).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

using AG = Wavee.Themes.Classic.ClassicStageArtistGeometry;
using Ink = Wavee.Theming.Roles.StageInk;
using K = Wavee.Themes.Classic.ClassicStageArtistKit;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's Artist pane: the registered template of <see cref="ViewKeys.StageArtistPane"/> mounts the stateful root.</summary>
internal static class ClassicStageArtistPane
{
    /// <summary>The registered template of <see cref="ViewKeys.StageArtistPane"/>.</summary>
    public static Element Template(StageArtistPaneProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ClassicStageArtistPaneRoot());
}

/// <summary>The pieces the pane's sections share.</summary>
internal static class ClassicStageArtistKit
{
    public static float Px(float v, float s) => MathF.Round(v * s);

    public static BoxEl NoSection() => new() { HitTestVisible = false };

    /// <summary>A section title in the stage's display face, 20/28 × scale.</summary>
    public static TextEl SectionTitle(string title, float s) => new(title)
    {
        Size = Px(AG.SectionTitleSize, s), LineHeight = Px(AG.SectionTitleLine, s), Weight = 600, FontFamily = ClassicStageLayerGeometry.DisplayFace, Color = Ink.Ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A right-column section: the title over the body on the stage's quiet plate (ink at a low alpha, a hairline
    /// that inverts with the ink — a step on either arm). The bottom padding is the gap, so an absent section leaves none.</summary>
    public static Element PlateSection(string title, float s, Element body) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Px(Spacing.XXL, s)),
        Children =
        [
            new BoxEl
            {
                Direction = 1, MinWidth = 0f, Gap = Px(Spacing.M, s), Padding = Edges4.All(Px(Spacing.L, s)), Corners = Radii.CardAll,
                Fill = Ink.Plate, BorderWidth = 1f, BorderColor = Ink.Stroke,
                Children = [SectionTitle(title, s), body],
            },
        ],
    };

    /// <summary>The stage veil at alpha <paramref name="a"/> (Veil is the ink's opposite on either arm).</summary>
    public static ColorF Shade(float a) => ClassicStageLayerKit.Shade(a);
}

/// <summary>The Artist pane (the pane host mounts it under KeepAlive). Re-renders on the playing track's key, the layout and the scope; asks for the
/// artist's overview, its popular + related edges and the track's credits once per (artist, track, scope), never per render.</summary>
internal sealed class ClassicStageArtistPaneRoot : Component
{
    public override Element Render()
    {
        var pane = UseProps<StageArtistPaneProps>();
        var ctx = pane.Stage;
        var L = ctx.Layout.Value;
        var target = pane.Target();                       // the track key, the scope (FIRST: a scope switch re-points the table reads below), the billed artists
        int a = target.Artist, t = target.Track;
        UseEffect(() => pane.Demand(a, t), DepKey.From(a, t, (int)target.Epoch, 0));

        float s = L.QueueScale;
        if (!target.Valid)
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(Loc.Get(Strings.Player.NothingPlaying)) { Size = K.Px(AG.ReadMoreSize, s), LineHeight = K.Px(AG.ReadMoreLine, s), Color = Ink.InkTertiary }],
            };

        float paneW = L.PaneW, gap = K.Px(Spacing.XXXL, s);
        float measure = Stage.ArtistRules.ReadingMeasure(K.Px(AG.BioSize, s));
        Element header = Embed.Comp(new ClassicArtistHeader.Props(pane, a, paneW, Stage.ArtistRules.HeaderHeight(L.PaneH, s), s), static () => new ClassicArtistHeader());
        Element song = Embed.Comp(new ClassicArtistSong.Props(pane, t, s), static () => new ClassicArtistSong());
        Element popular = Embed.Comp(new ClassicArtistPopular.Props(pane, a, s), static () => new ClassicArtistPopular());
        Element body;
        if (Stage.ArtistRules.TwoColumns(paneW))
        {
            float left = Stage.ArtistRules.LeftColumnW(paneW, gap, measure);
            float right = MathF.Max(0f, paneW - gap - left);
            body = new BoxEl
            {
                Direction = 0, Gap = gap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 1, Width = left, Shrink = 0f, MinWidth = 0f,
                                Children = [Embed.Comp(new ClassicArtistAbout.Props(pane, a, left, measure, s), static () => new ClassicArtistAbout())] },
                    new BoxEl
                    {
                        Direction = 1, Width = right, Shrink = 0f, MinWidth = 0f,
                        Children =
                        [
                            song, popular,
                            Embed.Comp(new ClassicArtistCities.Props(pane, a, s), static () => new ClassicArtistCities()),
                            Embed.Comp(new ClassicArtistFans.Props(pane, a, right, s), static () => new ClassicArtistFans()),
                        ],
                    },
                ],
            };
        }
        else
            body = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children = [song, popular, Embed.Comp(new ClassicArtistAbout.Props(pane, a, paneW, measure, s), static () => new ClassicArtistAbout())],
            };

        return new ScrollEl
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageartist",
            Content = new BoxEl { Direction = 1, MinWidth = 0f, Gap = gap, Padding = new Edges4(0f, 0f, 0f, gap), Children = [header, body] },
        };
    }
}

/// <summary>The artist's photo across the pane (HeroImageId — the landscape header, else the portrait), the stage veil rising from the bottom, and
/// over it: name (48 display × scale) + the verified badge, "listeners · rank", the tour line, then Follow and Go to artist. Subscribed to the
/// artist table only.</summary>
internal sealed class ClassicArtistHeader : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float W, float H, float S);

    readonly Action _go;
    readonly Func<ColorF> _accent;
    Stage.StageCtx? _ctx;
    Action<string>? _begin;
    Props? _p;

    public ClassicArtistHeader()
    {
        _go = () => { if (_p is { } p) p.Pane.GoToArtist(_begin, p.Artist); };
        _accent = () => _ctx is { } c ? c.Accent.Value.Fill : Tok.AccentDefault;   // read by FollowToggle's own render
    }

    public override Element Render()
    {
        var p = UseProps<Props>();
        _p = p;
        _ctx = p.Pane.Stage;
        _begin = UseContext(SharedTransition.Begin);
        var f = p.Pane.Header(p.Artist);                  // the scope, the artist table
        var set = _ctx.Accent.Value;
        if (f is null) return K.NoSection();
        float s = p.S;

        // name + the verified badge on one line; the name ellipsises, the badge never shrinks
        var nameRow = new List<Element>(2)
        {
            new TextEl(f.Name)
            {
                Size = K.Px(AG.NameSize, s), LineHeight = K.Px(AG.NameLine, s), Weight = 700, FontFamily = ClassicStageLayerGeometry.DisplayFace, Color = Ink.Ink,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        };
        if (f.Verified)
            nameRow.Add(new BoxEl { Shrink = 0f, Children = [Controls.Named(InfoBadge.Icon(Icons.Accept, color: set.Fill), Loc.Get(Strings.Artist.Verified))] });

        var lines = new List<Element>(3)
        {
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.M, s), MinWidth = 0f, Children = nameRow.ToArray() },
        };
        if (f.Meta.Length > 0)
            lines.Add(new TextEl(f.Meta) { Size = K.Px(AG.MetaSize, s), LineHeight = K.Px(AG.MetaLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (f.TourHeadline is { } headline)
        {
            var tour = new List<Element>(2);
            if (f.TourEyebrow.Length > 0)
                tour.Add(R.Type.Eyebrow(f.TourEyebrow) with { Size = K.Px(AG.TourEyebrowSize, s), Color = set.Text, MaxLines = 1, Shrink = 0f });
            tour.Add(new TextEl(headline) { Size = K.Px(AG.TourHeadlineSize, s), LineHeight = K.Px(AG.TourHeadlineLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
            lines.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.S, s), MinWidth = 0f, Children = tour.ToArray() });
        }

        string uri = f.Uri, name = f.Name;
        var actions = new List<Element>(2);
        if (uri.Length > 0)   // keyed on the uri: FollowToggle carries it as a mount-frozen field
            actions.Add(Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name, Accent = _accent }) with { Key = "stage-follow:" + uri });
        actions.Add(Button.Create(Loc.Get(Strings.Stage.GoToArtist), _go, ButtonAppearance.Standard, ControlSize.Medium));

        float pad = K.Px(Spacing.XXL, s);
        return new BoxEl
        {
            Height = p.H, Corners = Radii.CardAll, ClipToBounds = true, ZStack = true, Fill = Ink.Plate, BorderWidth = 1f, BorderColor = Ink.Stroke,
            Children =
            [
                // Controls.Artwork returns Element (no Align/Justify): a stretched BoxEl hosts it (V-U6). Laid out at the
                // pane's own width × the band's height and cover-fit, so the photo fills the band edge to edge.
                new BoxEl { AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                            Children = [Controls.Artwork(f.PhotoUrl, p.W, p.H, 0f)] },
                // the stage veil rising from the bottom (Veil is the ink's opposite on either arm)
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Gradient = GradientDown(new GradientStop(0f, K.Shade(0f)), new GradientStop(AG.VeilMidStop, K.Shade(AG.VeilMidAlpha)), new GradientStop(1f, K.Shade(AG.VeilFootAlpha))),
                },
                new BoxEl
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch, Direction = 0, AlignItems = FlexAlign.End, Gap = K.Px(Spacing.XL, s),
                    Padding = new Edges4(pad, 0f, pad, pad),
                    Children =
                    [
                        new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = K.Px(Spacing.XS, s), Children = lines.ToArray() },
                        new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = actions.ToArray() },
                    ],
                },
            ],
        };
    }
}

/// <summary>"About": the WHOLE biography (its links route through the stage's close-then-navigate) at 18/28 × scale, held to ~68 characters, clamped
/// to six lines behind Read more / Show less when it overflows; then up to four gallery photos. The open state is the artist it was opened FOR,
/// so the next artist starts clamped with no reset write.</summary>
internal sealed class ClassicArtistAbout : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float W, float Measure, float S);

    readonly Signal<int> _openFor = new(0);
    readonly Action _toggle;
    readonly Action<string> _nav;
    Action<string>? _begin;
    Props? _p;
    int _artist;

    public ClassicArtistAbout()
    {
        _toggle = () => _openFor.Value = _openFor.Peek() == _artist ? 0 : _artist;
        _nav = key => _p?.Pane.OpenLink(_begin, key);
    }

    public override Element Render()
    {
        _begin = UseContext(SharedTransition.Begin);
        var p = UseProps<Props>();
        _p = p;
        var facts = p.Pane.About(p.Artist);               // the scope, the artist table, the gallery edge
        var set = p.Pane.Stage.Accent.Value;
        _artist = p.Artist;
        if (facts is null) return K.NoSection();

        float s = p.S, size = K.Px(AG.BioSize, s), line = K.Px(AG.BioLine, s);
        float textW = MathF.Min(p.W, p.Measure);
        bool clamps = Stage.ArtistRules.BioClamps(facts.BioChars, textW, size, Stage.ArtistRules.BioLines);
        bool open = _openFor.Value == p.Artist;

        var kids = new List<Element>(4) { K.SectionTitle(Loc.Get(Strings.Stage.Artist.About), s) };
        if (facts.Bio.Length > 0)
        {
            Element text = Controls.RichText(facts.Bio, size, Ink.Ink, set.Text, textW, clamps && !open ? Stage.ArtistRules.BioLines : 0, _nav);
            kids.Add(text is SpanTextEl span ? span with { LineHeight = line } : text);
            if (clamps)
                kids.Add(new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = _toggle,
                    Children = [new TextEl(Loc.Get(open ? Strings.Stage.Artist.ShowLess : Strings.Stage.Artist.ReadMore)) { Size = K.Px(AG.ReadMoreSize, s), LineHeight = K.Px(AG.ReadMoreLine, s), Weight = 600, Color = set.Text, HoverColor = Ink.Ink }],
                });
        }
        int n = facts.GalleryUrls.Count;
        if (n > 0)
        {
            float gap = K.Px(Spacing.S, s), tile = Stage.ArtistRules.TileW(p.W, gap, n);
            var tiles = new Element[n];
            for (int i = 0; i < n; i++)
                tiles[i] = new BoxEl { Shrink = 0f, HitTestVisible = false, Children = [Controls.Artwork(facts.GalleryUrls[i], tile, tile, Radii.Card)] };
            kids.Add(new BoxEl { Direction = 0, Gap = gap, Margin = new Edges4(0f, K.Px(Spacing.S, s), 0f, 0f), Children = tiles });
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Gap = K.Px(Spacing.M, s), Padding = new Edges4(0f, 0f, 0f, K.Px(Spacing.XXL, s)), Children = kids.ToArray() };
    }
}

/// <summary>"This song": the track's credits GROUPED BY PERSON — "Joey Tempest · Composer, Lyricist" — each name a link when it resolves to an
/// artist. Absent until the credits answer.</summary>
internal sealed class ClassicArtistSong : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Track, float S);

    Action<string>? _begin;

    public override Element Render()
    {
        _begin = UseContext(SharedTransition.Begin);
        var p = UseProps<Props>();
        var people = p.Pane.Song(p.Track);                // the scope, the credits and artist edges
        if (people is null) return K.NoSection();

        float s = p.S;
        var pane = p.Pane;
        var rows = new Element[people.Count];
        for (int i = 0; i < people.Count; i++)
        {
            var person = people[i];
            int slot = person.ArtistSlot;
            bool link = slot > 0;
            var parts = new List<Element>(3)
            {
                new TextEl(person.Name) { Size = K.Px(AG.CreditSize, s), LineHeight = K.Px(AG.CreditLine, s), Weight = 600, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            };
            if (person.Roles.Length > 0)
            {
                parts.Add(new TextEl("·") { Size = K.Px(AG.CreditSize, s), LineHeight = K.Px(AG.CreditLine, s), Color = Ink.InkTertiary, Shrink = 0f });
                parts.Add(new TextEl(Strings.Stage.Artist.Roles(person.Roles)) { Size = K.Px(AG.CreditSize, s), LineHeight = K.Px(AG.CreditLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f, Shrink = 1f });
            }
            rows[i] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.S, s), MinWidth = 0f, MinHeight = K.Px(AG.CreditRowMinH, s),
                Padding = new Edges4(K.Px(Spacing.S, s), 0f, K.Px(Spacing.S, s), 0f), Corners = Radii.ControlAll,
                Fill = Ink.GlassRest, HoverFill = link ? Ink.GlassHover : Ink.GlassRest, PressedFill = link ? Ink.GlassPressed : Ink.GlassRest,
                BrushTransitionMs = Design.Motion.Faster,
                Role = link ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = link, AllowFocusOnInteraction = false,
                Cursor = link ? CursorId.Hand : (CursorId?)null, OnClick = link ? () => pane.GoToArtist(_begin, slot) : null,
                Children = parts.ToArray(),
            };
        }
        return K.PlateSection(Loc.Get(Strings.Stage.Artist.ThisSong), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XXS, Children = rows });
    }
}

/// <summary>"Popular": the artist's top five, the playing one on the plate in accent text with the speaker glyph. A click plays the chart FROM that
/// row with the chart's own rows as the queue; the deck row toggles. The rows' text is asked once per popular-edge version.</summary>
internal sealed class ClassicArtistPopular : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float S);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var pane = p.Pane;
        var f = pane.Popular(p.Artist);                   // the scope, the popular edge, the track rows, the deck's current id
        int artistSlot = p.Artist;
        UseEffect(() => pane.EnsurePopular(artistSlot), DepKey.From(artistSlot, (int)f.Version, (int)f.Epoch, 0));
        var set = pane.Stage.Accent.Value;
        if (f.Rows.Count == 0) return K.NoSection();

        float s = p.S;
        var rows = new Element[f.Rows.Count];
        for (int i = 0; i < rows.Length; i++) rows[i] = Row(f.Rows[i], f.ShowArt, set, s);
        return K.PlateSection(Loc.Get(Strings.Stage.Artist.Popular), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = Spacing.XXS, Children = rows });
    }

    static Element Row(StageArtistPopularRow row, bool art, AccentSet set, float s)
    {
        bool now = row.Now;
        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Width = K.Px(AG.PopularNumberW, s), Shrink = 0f, Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Children = [row.Playing
                    ? new TextEl(Icons.Volume) { Size = K.Px(AG.PopularNumberSize, s), FontFamily = Tok.Definition.Fonts.Icon, Color = set.Text }
                    : new TextEl(FormatCache.Int(row.Index + 1)) { Size = K.Px(AG.PopularNumberSize, s), LineHeight = K.Px(AG.PopularNumberLine, s), Color = now ? set.Text : Ink.InkTertiary, Wrap = TextWrap.NoWrap }],
            },
        };
        if (art)
            kids.Add(new BoxEl { Shrink = 0f, HitTestVisible = false, Children = [Controls.Artwork(row.ImageUrl, K.Px(AG.PopularArt, s), K.Px(AG.PopularArt, s), Radii.Control)] });
        Element title = new TextEl(row.Title)
        {
            Size = K.Px(AG.PopularTitleSize, s), LineHeight = K.Px(AG.PopularTitleLine, s), Weight = 600, Color = now ? set.Text : Ink.Ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        var text = new List<Element>(2)
        {
            row.Explicit
                ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                              Children = [title, Controls.ExplicitBadge(K.Px(AG.ExplicitBadge, s), Ink.InkTertiary)] }
                : title,
        };
        if (row.Plays.Length > 0)
            text.Add(new TextEl(row.Plays) { Size = K.Px(AG.PopularPlaysSize, s), LineHeight = K.Px(AG.PopularPlaysLine, s), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1 });
        kids.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, Gap = 1f, HitTestVisible = false, Children = text.ToArray() });
        kids.Add(new TextEl(row.Duration) { Size = K.Px(AG.PopularDurationSize, s), LineHeight = K.Px(AG.PopularDurationLine, s), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.M, s), MinWidth = 0f, Height = K.Px(AG.PopularRowH, s),
            Padding = new Edges4(K.Px(Spacing.S, s), 0f, K.Px(Spacing.M, s), 0f), Corners = Radii.ControlAll,
            Fill = now ? Ink.Plate : Ink.GlassRest, HoverFill = now ? Ink.PlateHover : Ink.GlassHover, PressedFill = Ink.GlassPressed,
            BorderWidth = now ? 1f : 0f, BorderColor = Ink.Stroke, BrushTransitionMs = Design.Motion.Faster,
            PressScale = R.Motion.ScaleSubtle.Press,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = row.Click,
            Children = kids.ToArray(),
        };
    }
}

/// <summary>"Where people listen": the top five cities, each a name · compact count over a 4-DIP accent bar proportional to the loudest city (the
/// artist page's CityBars, in the stage's ink).</summary>
internal sealed class ClassicArtistCities : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float S);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var cities = p.Pane.Cities(p.Artist);             // the scope, the cities edge
        var set = p.Pane.Stage.Accent.Value;
        if (cities is null) return K.NoSection();

        float s = p.S, bar = K.Px(AG.CityBar, s);
        var rows = new Element[cities.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            float frac = cities[i].Fraction;
            rows[i] = new BoxEl
            {
                Direction = 1, Gap = K.Px(Spacing.XS, s), MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.S, s), MinWidth = 0f,
                        Children =
                        [
                            new TextEl(cities[i].City) { Size = K.Px(AG.CityNameSize, s), LineHeight = K.Px(AG.CityNameLine, s), Color = Ink.Ink, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(cities[i].Listeners) { Size = K.Px(AG.CityCountSize, s), LineHeight = K.Px(AG.CityCountLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, Shrink = 0f },
                        ],
                    },
                    // the bar's ground is the plate rung, its fill the accent; widths by flex share, never a scale
                    new BoxEl
                    {
                        Direction = 0, Height = bar, Corners = CornerRadius4.All(bar * 0.5f), ClipToBounds = true, Fill = Ink.Plate,
                        Children =
                        [
                            new BoxEl { Grow = MathF.Max(0.001f, frac), Basis = 0f, Height = bar, Corners = CornerRadius4.All(bar * 0.5f), Fill = set.Fill },
                            new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Basis = 0f, Height = bar },
                        ],
                    },
                ],
            };
        }
        return K.PlateSection(Loc.Get(Strings.Stage.Artist.TopCities), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = K.Px(Spacing.M, s), Children = rows });
    }
}

/// <summary>"Fans also like": as many related artists as fit the column (≤ 8), each a round avatar over its name; a click opens the artist (closing
/// the stage). Their identities are asked once per related-edge version.</summary>
internal sealed class ClassicArtistFans : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float W, float S);

    Action<string>? _begin;

    public override Element Render()
    {
        _begin = UseContext(SharedTransition.Begin);
        var p = UseProps<Props>();
        var pane = p.Pane;
        var f = pane.Fans(p.Artist);                      // the scope, the related edge, the artist table
        int artistSlot = p.Artist;
        UseEffect(() => pane.EnsureRelated(artistSlot), DepKey.From(artistSlot, (int)f.Version, (int)f.Epoch, 0));

        float s = p.S, avatar = K.Px(AG.FanAvatar, s), gap = K.Px(Spacing.M, s);
        float inner = p.W - 2f * K.Px(Spacing.L, s) - 2f;     // the plate's padding and hairline
        int n = Stage.ArtistRules.FitCount(inner, avatar, gap, f.Total, Stage.ArtistRules.FansMax);
        n = Math.Min(n, f.Fans.Count);
        if (n == 0) return K.NoSection();
        var tiles = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var fan = f.Fans[i];
            int slot = fan.Slot;
            tiles[i] = new BoxEl
            {
                Direction = 1, Width = avatar, Shrink = 0f, Gap = K.Px(Spacing.XS, s), AlignItems = FlexAlign.Center,
                Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                PressScale = R.Motion.ScaleSubtle.Press, OnClick = () => pane.GoToArtist(_begin, slot),
                Children =
                [
                    Controls.Artwork(fan.ImageUrl, avatar, avatar, avatar * 0.5f),
                    new TextEl(fan.Name) { Size = K.Px(AG.FanNameSize, s), LineHeight = K.Px(AG.FanNameLine, s), Color = Ink.InkSecondary, HoverColor = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = avatar, MinWidth = 0f },
                ],
            };
        }
        return K.PlateSection(Loc.Get(Strings.Stage.Artist.FansAlsoLike), s, new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = tiles });
    }
}
