// ── Themes/Zune/Structure/Views/Stage/ZuneStageArtistPane.cs — Zune's `Stage.ArtistPane` (P5 Z4c) ───────────────────────────────────────
//
// The stage's Artist pane in Zune's voice. Z-NowPlaying-Artist draws one block of it: the heading "about the artist" (20/28 at 600, the
// display face, the ink at 0.85), the bio (14/21 in the secondary ink) and the word actions under it ("follow", "go to artist", "more like
// this"). The rest of the pane (the artist's name and meta, the track's credits, Popular, Where people listen, Fans also like) has no
// board and is built from Zune's parts (owner Q18): no plates and no boxes, lowercase headings, flat rows that light on hover, the 2-px line
// for the cities, square covers. The core keeps everything it decided: what each section reads from the entity tables (the facts arrive as
// calls each section makes in its own render, so a publication re-renders only the section that paints it), the column split, the reading
// measure and how many avatars fit (`Stage.ArtistRules`). A fact that answers null (or no rows) is "no section": the pane leaves no gap for it.
//
//   ScrollEl[ header · Row[ About(heading · bio · read more) · actions | This song · Popular · Where people listen · Fans also like ] ]
//   narrow (PaneW < 1100): ScrollEl[ header · About · actions · This song · Popular ]
//
//   • All ink is StageInk; the accent is the context's AccentSet. Every size is a base DIP times the pane's type scale
//     (`Stage.Layout.QueueScale`), rounded (`ZuneStageArtistKit.Px`).
//   • "more like this" (board) has no verb on the pane's props: the actions are "follow" and "go to artist" (reported as a gap).
//   • Navigation CLOSES the stage first (V-U29).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

using AR = Wavee.Themes.Zune.ZuneStageArtistRules;
using Ink = Wavee.Theming.Roles.StageInk;
using K = Wavee.Themes.Zune.ZuneStageArtistKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Artist pane: the registered template of <see cref="ViewKeys.StageArtistPane"/> mounts the stateful root.</summary>
internal static class ZuneStageArtistPane
{
    /// <summary>The registered template of <see cref="ViewKeys.StageArtistPane"/>.</summary>
    public static Element Template(StageArtistPaneProps props, in ViewContext ctx)
        => Embed.Comp(props, static () => new ZuneStageArtistPaneRoot());
}

/// <summary>The pieces the pane's sections share.</summary>
internal static class ZuneStageArtistKit
{
    public static float Px(float v, float s) => MathF.Round(v * s);

    public static BoxEl NoSection() => new() { HitTestVisible = false };

    /// <summary>The window tier's target height for a word (36, 40 at Compact).</summary>
    public static float TargetOf(in TierFacts tiers) => ZuneStageTiers.StageOf(tiers).TargetH;

    /// <summary>A section heading in the stage's display face: 20/28 at 600, the ink at 0.85, lowercase through the heading case.</summary>
    public static TextEl SectionTitle(string title, float s) => new(title)
    {
        Size = Px(AR.SectionTitleSize, s), LineHeight = Px(AR.SectionTitleLine, s), Weight = AR.SectionTitleWeight, FontFamily = R.Type.DisplayFace,
        Color = Ink.Ink with { A = AR.SectionTitleInkA }, Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    /// <summary>A section: the heading over its body, no plate. The bottom padding is the gap, so an absent section leaves none.</summary>
    public static Element Section(string title, float s, Element body) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Gap = Px(AR.SectionGap, s), Padding = new Edges4(0f, 0f, 0f, Px(Spacing.XXL, s)),
        Children = [SectionTitle(title, s), body],
    };

    /// <summary>A word action: 13/18, lowercase through the command case, the secondary ink lifting to the ink on hover, no plate.</summary>
    public static BoxEl Word(string text, Action onClick, ColorF color, float target, float s) => new()
    {
        MinHeight = target, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [new TextEl(text) { Size = Px(AR.ActionSize, s), LineHeight = Px(AR.ActionLine, s), Weight = AR.ActionWeight, Color = color, HoverColor = Ink.Ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1 }],
    };
}

/// <summary>The Artist pane (the pane host mounts it under KeepAlive). Re-renders on the playing track's key, the layout and the scope; asks for the
/// artist's overview, its popular + related edges and the track's credits once per (artist, track, scope), never per render.</summary>
internal sealed class ZuneStageArtistPaneRoot : Component
{
    public override Element Render()
    {
        var pane = UseProps<StageArtistPaneProps>();
        var ctx = pane.Stage;
        var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        var L = ctx.Layout.Value;
        var target = pane.Target();                       // the track key, the scope (FIRST: a scope switch re-points the table reads below), the billed artists
        int a = target.Artist, t = target.Track;
        UseEffect(() => pane.Demand(a, t), DepKey.From(a, t, (int)target.Epoch, 0));

        float s = L.QueueScale, targetH = K.TargetOf(in tiers);
        if (!target.Valid)
            return new BoxEl
            {
                Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(Loc.Get(Strings.Player.NothingPlaying)) { Size = K.Px(AR.MetaSize, s), LineHeight = K.Px(AR.MetaLine, s), Color = Ink.InkTertiary }],
            };

        float paneW = L.PaneW, gap = K.Px(AR.ColumnGap, s);
        float measure = Stage.ArtistRules.ReadingMeasure(K.Px(AR.BioSize, s));
        Element header = Embed.Comp(new ZuneStageArtistHeader.Props(pane, a, s), static () => new ZuneStageArtistHeader());
        Element about = Embed.Comp(new ZuneStageArtistAbout.Props(pane, a, MathF.Min(paneW, measure), s), static () => new ZuneStageArtistAbout());
        Element actions = Embed.Comp(new ZuneStageArtistActions.Props(pane, a, targetH, s), static () => new ZuneStageArtistActions());
        Element song = Embed.Comp(new ZuneStageArtistSong.Props(pane, t, s), static () => new ZuneStageArtistSong());
        Element popular = Embed.Comp(new ZuneStageArtistPopular.Props(pane, a, s), static () => new ZuneStageArtistPopular());
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
                    new BoxEl { Direction = 1, Width = left, Shrink = 0f, MinWidth = 0f, Children = [about, actions] },
                    new BoxEl
                    {
                        Direction = 1, Width = right, Shrink = 0f, MinWidth = 0f,
                        Children =
                        [
                            song, popular,
                            Embed.Comp(new ZuneStageArtistCities.Props(pane, a, s), static () => new ZuneStageArtistCities()),
                            Embed.Comp(new ZuneStageArtistFans.Props(pane, a, right, s), static () => new ZuneStageArtistFans()),
                        ],
                    },
                ],
            };
        }
        else
            body = new BoxEl { Direction = 1, MinWidth = 0f, Children = [about, actions, song, popular] };

        return new ScrollEl
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "stageartist",
            Content = new BoxEl { Direction = 1, MinWidth = 0f, Gap = gap, Padding = new Edges4(0f, 0f, 0f, gap), Children = [header, body] },
        };
    }
}

/// <summary>The artist's name (the display face at 300, 40/48) with the verified badge, "listeners · rank", and the tour line. Subscribed to the
/// artist table only.</summary>
internal sealed class ZuneStageArtistHeader : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float S);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var f = p.Pane.Header(p.Artist);                  // the scope, the artist table
        var set = p.Pane.Stage.Accent.Value;
        if (f is null) return K.NoSection();
        float s = p.S;

        // name + the verified badge on one line; the name ellipsises, the badge never shrinks
        var nameRow = new List<Element>(2)
        {
            new TextEl(f.Name)
            {
                Size = K.Px(AR.NameSize, s), LineHeight = K.Px(AR.NameLine, s), Weight = AR.NameWeight, FontFamily = R.Type.DisplayFace, CharSpacing = AR.NameTracking,
                Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
            },
        };
        if (f.Verified)
            nameRow.Add(new BoxEl { Shrink = 0f, Children = [Controls.Named(InfoBadge.Icon(Icons.Accept, color: set.Fill), Loc.Get(Strings.Artist.Verified))] });

        var lines = new List<Element>(3)
        {
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(AR.VerifiedGap, s), MinWidth = 0f, Children = nameRow.ToArray() },
        };
        if (f.Meta.Length > 0)
            lines.Add(new TextEl(f.Meta) { Size = K.Px(AR.MetaSize, s), LineHeight = K.Px(AR.MetaLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (f.TourHeadline is { } headline)
        {
            var tour = new List<Element>(2);
            if (f.TourEyebrow.Length > 0)
                tour.Add(new TextEl(f.TourEyebrow) { Size = K.Px(AR.TourEyebrowSize, s), LineHeight = K.Px(AR.TourHeadlineLine, s), Color = set.Text, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Shrink = 0f });
            tour.Add(new TextEl(headline) { Size = K.Px(AR.TourHeadlineSize, s), LineHeight = K.Px(AR.TourHeadlineLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f });
            lines.Add(new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.S, s), MinWidth = 0f, Children = tour.ToArray() });
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Gap = K.Px(Spacing.XS, s), Children = lines.ToArray() };
    }
}

/// <summary>"about the artist": the WHOLE biography (its links route through the stage's close-then-navigate) at 14/21, held to the reading measure,
/// clamped to six lines behind "read more" / "show less" when it overflows; then up to four gallery photos. The open state is the artist it was
/// opened FOR, so the next artist starts clamped with no reset write.</summary>
internal sealed class ZuneStageArtistAbout : Component
{
    /// <param name="Brief">0: the pane's block (the bio clamped with "read more", the gallery strip); N: the Artist layout's (the bio's first N
    /// lines, no "read more", no strip: Z-NowPlaying-Artist).</param>
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float W, float S, int Brief = 0);

    readonly Signal<int> _openFor = new(0);
    readonly Action _toggle;
    readonly Action<string> _nav;
    Action<string>? _begin;
    Props? _p;
    int _artist;

    public ZuneStageArtistAbout()
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

        float s = p.S, size = K.Px(AR.BioSize, s), line = K.Px(AR.BioLine, s);
        float textW = p.W;
        bool brief = p.Brief > 0;
        bool clamps = !brief && Stage.ArtistRules.BioClamps(facts.BioChars, textW, size, Stage.ArtistRules.BioLines);
        bool open = !brief && _openFor.Value == p.Artist;

        var kids = new List<Element>(4) { K.SectionTitle(Loc.Get(Strings.Theme.Zune.Stage.AboutArtist), s) };
        if (facts.Bio.Length > 0)
        {
            Element text = Controls.RichText(facts.Bio, size, Ink.InkSecondary, set.Text, textW, brief ? p.Brief : clamps && !open ? Stage.ArtistRules.BioLines : 0, _nav);
            kids.Add(text.Is(out SpanTextEl span) ? span with { LineHeight = line } : text);
            if (clamps)
                kids.Add(new BoxEl
                {
                    AlignSelf = FlexAlign.Start, Padding = new Edges4(0f, Spacing.XXS, 0f, Spacing.XXS),
                    Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = _toggle,
                    Children = [new TextEl(Loc.Get(open ? Strings.Stage.Artist.ShowLess : Strings.Stage.Artist.ReadMore))
                    {
                        Size = K.Px(AR.ReadMoreSize, s), LineHeight = K.Px(AR.ReadMoreLine, s), Weight = AR.SectionTitleWeight, Color = set.Text, HoverColor = Ink.Ink,
                        Case = R.Case.For(LabelKind.Command),
                    }],
                });
        }
        int n = brief ? 0 : facts.GalleryUrls.Count;
        if (n > 0)
        {
            float gap = K.Px(AR.GalleryGap, s), tile = Stage.ArtistRules.TileW(p.W, gap, n);
            var tiles = new Element[n];
            for (int i = 0; i < n; i++)
                tiles[i] = new BoxEl { Shrink = 0f, HitTestVisible = false, Children = [Controls.Artwork(facts.GalleryUrls[i], tile, tile, R.Shape.Cover)] };
            kids.Add(new BoxEl { Direction = 0, Gap = gap, Margin = new Edges4(0f, K.Px(Spacing.S, s), 0f, 0f), Children = tiles });
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Gap = K.Px(AR.SectionGap, s), Padding = new Edges4(0f, 0f, 0f, K.Px(Spacing.M, s)), Children = kids.ToArray() };
    }
}

/// <summary>The word actions under the bio ("follow", "go to artist"): the follow toggle is the core's control (keyed on the uri: it carries it as
/// a mount-frozen field), the other a word. Absent until the artist's header facts land.</summary>
internal sealed class ZuneStageArtistActions : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float TargetH, float S);

    readonly Action _go;
    readonly Func<ColorF> _accent;
    Stage.StageCtx? _ctx;
    Action<string>? _begin;
    Props? _p;

    public ZuneStageArtistActions()
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
        if (f is null) return K.NoSection();

        string uri = f.Uri, name = f.Name;
        var actions = new List<Element>(2);
        if (uri.Length > 0)
            actions.Add(Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name, Accent = _accent }) with { Key = "stage-follow:" + uri });
        actions.Add(K.Word(Loc.Get(Strings.Stage.GoToArtist), _go, Ink.InkSecondary, p.TargetH, p.S));
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(AR.ActionGap, p.S), MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, K.Px(Spacing.XXL, p.S)),
            Children = actions.ToArray(),
        };
    }
}

/// <summary>"this song": the track's credits GROUPED BY PERSON, "Joey Tempest · Composer, Lyricist", each name a link when it resolves to an
/// artist. Absent until the credits answer.</summary>
internal sealed class ZuneStageArtistSong : Component
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
                new TextEl(person.Name) { Size = K.Px(AR.CreditSize, s), LineHeight = K.Px(AR.CreditLine, s), Weight = AR.CreditNameWeight, Color = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f },
            };
            if (person.Roles.Length > 0)
            {
                parts.Add(new TextEl("·") { Size = K.Px(AR.CreditSize, s), LineHeight = K.Px(AR.CreditLine, s), Color = Ink.InkTertiary, Shrink = 0f });
                parts.Add(new TextEl(Strings.Stage.Artist.Roles(person.Roles)) { Size = K.Px(AR.CreditSize, s), LineHeight = K.Px(AR.CreditLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f, Shrink = 1f });
            }
            rows[i] = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.S, s), MinWidth = 0f, MinHeight = K.Px(AR.CreditRowMinH, s),
                Padding = new Edges4(K.Px(Spacing.S, s), 0f, K.Px(Spacing.S, s), 0f), Corners = R.Shape.All(ShapeRole.Control),
                HoverFill = link ? Ink.GlassHover : ColorF.Transparent, PressedFill = link ? Ink.GlassPressed : ColorF.Transparent,
                BrushTransitionMs = Design.Motion.Faster,
                Role = link ? AutomationRole.Hyperlink : AutomationRole.Text, Focusable = link, AllowFocusOnInteraction = false,
                Cursor = link ? CursorId.Hand : (CursorId?)null, OnClick = link ? () => pane.GoToArtist(_begin, slot) : null,
                Children = parts.ToArray(),
            };
        }
        return K.Section(Loc.Get(Strings.Stage.Artist.ThisSong), s, new BoxEl { Direction = 1, MinWidth = 0f, Children = rows });
    }
}

/// <summary>"popular": the artist's top five, the playing one on the plate in accent text with the speaker glyph. A click plays the chart FROM that
/// row with the chart's own rows as the queue; the deck row toggles. The rows' text is asked once per popular-edge version.</summary>
internal sealed class ZuneStageArtistPopular : Component
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
        return K.Section(Loc.Get(Strings.Stage.Artist.Popular), s, new BoxEl { Direction = 1, MinWidth = 0f, Children = rows });
    }

    static Element Row(StageArtistPopularRow row, bool art, AccentSet set, float s)
    {
        bool now = row.Now;
        var kids = new List<Element>(4)
        {
            new BoxEl
            {
                Width = K.Px(AR.PopularNumberW, s), Shrink = 0f, Direction = 0, Justify = FlexJustify.Start, AlignItems = FlexAlign.Center, HitTestVisible = false,
                Children = [row.Playing
                    ? new TextEl(Icons.Volume) { Size = K.Px(AR.PopularDurationSize, s), FontFamily = Tok.Definition.Fonts.Icon, Color = set.Text }
                    : new TextEl(FormatCache.Int(row.Index + 1))
                    {
                        Size = K.Px(AR.PopularNumberSize, s), LineHeight = K.Px(AR.PopularNumberLine, s), Weight = AR.PopularNumberWeight, FontFamily = R.Type.DisplayFace,
                        Color = now ? set.Text : Ink.InkTertiary, Wrap = TextWrap.NoWrap,
                    }],
            },
        };
        if (art)
            kids.Add(new BoxEl { Shrink = 0f, HitTestVisible = false, Children = [Controls.Artwork(row.ImageUrl, K.Px(AR.PopularArt, s), K.Px(AR.PopularArt, s), R.Shape.Cover)] });
        Element title = new TextEl(row.Title)
        {
            Size = K.Px(AR.PopularTitleSize, s), LineHeight = K.Px(AR.PopularTitleLine, s), Color = now ? set.Text : Ink.Ink,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };
        var text = new List<Element>(2)
        {
            row.Explicit
                ? new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                              Children = [title, Controls.ExplicitBadge(K.Px(AR.ExplicitBadge, s), Ink.InkTertiary)] }
                : title,
        };
        if (row.Plays.Length > 0)
            text.Add(new TextEl(row.Plays) { Size = K.Px(AR.PopularPlaysSize, s), LineHeight = K.Px(AR.PopularPlaysLine, s), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1 });
        kids.Add(new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, HitTestVisible = false, Children = text.ToArray() });
        kids.Add(new TextEl(row.Duration) { Size = K.Px(AR.PopularDurationSize, s), LineHeight = K.Px(AR.PopularDurationLine, s), Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, Shrink = 0f });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = K.Px(Spacing.M, s), MinWidth = 0f, Height = K.Px(AR.PopularRowH, s),
            Padding = new Edges4(K.Px(Spacing.S, s), 0f, K.Px(Spacing.M, s), 0f), Corners = R.Shape.All(ShapeRole.Control),
            Fill = now ? Ink.Plate : ColorF.Transparent, HoverFill = now ? Ink.PlateHover : Ink.GlassHover, PressedFill = Ink.GlassPressed,
            BrushTransitionMs = Design.Motion.Faster,
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true, AllowFocusOnInteraction = false,
            OnClick = row.Click,
            Children = kids.ToArray(),
        };
    }
}

/// <summary>"where people listen": the top five cities, each a name and a compact count over a 2-px accent line proportional to the loudest city.</summary>
internal sealed class ZuneStageArtistCities : Component
{
    public sealed record Props(StageArtistPaneProps Pane, int Artist, float S);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var cities = p.Pane.Cities(p.Artist);             // the scope, the cities edge
        var set = p.Pane.Stage.Accent.Value;
        if (cities is null) return K.NoSection();

        float s = p.S, bar = K.Px(AR.CityBar, s);
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
                            new TextEl(cities[i].City) { Size = K.Px(AR.CityNameSize, s), LineHeight = K.Px(AR.CityNameLine, s), Color = Ink.Ink, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(cities[i].Listeners) { Size = K.Px(AR.CityCountSize, s), LineHeight = K.Px(AR.CityCountLine, s), Color = Ink.InkSecondary, Wrap = TextWrap.NoWrap, Shrink = 0f },
                        ],
                    },
                    // the line's ground is the plate rung, its fill the accent; widths by flex share, never a scale
                    new BoxEl
                    {
                        Direction = 0, Height = bar, ClipToBounds = true, Fill = Ink.Plate,
                        Children =
                        [
                            new BoxEl { Grow = MathF.Max(0.001f, frac), Basis = 0f, Height = bar, Fill = set.Fill },
                            new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Basis = 0f, Height = bar },
                        ],
                    },
                ],
            };
        }
        return K.Section(Loc.Get(Strings.Stage.Artist.TopCities), s, new BoxEl { Direction = 1, MinWidth = 0f, Gap = K.Px(Spacing.M, s), Children = rows });
    }
}

/// <summary>"fans also like": as many related artists as fit the column (up to 8), each an avatar over its name; a click opens the artist (closing
/// the stage). Their identities are asked once per related-edge version.</summary>
internal sealed class ZuneStageArtistFans : Component
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

        float s = p.S, avatar = K.Px(AR.FanAvatar, s), gap = K.Px(Spacing.M, s);
        float round = MathF.Min(R.Shape.For(ShapeRole.Avatar), avatar * 0.5f);
        int n = Stage.ArtistRules.FitCount(p.W, avatar, gap, f.Total, Stage.ArtistRules.FansMax);
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
                    Controls.Artwork(fan.ImageUrl, avatar, avatar, round),
                    new TextEl(fan.Name) { Size = K.Px(AR.FanNameSize, s), LineHeight = K.Px(AR.FanNameLine, s), Color = Ink.InkSecondary, HoverColor = Ink.Ink, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = avatar, MinWidth = 0f },
                ],
            };
        }
        return K.Section(Loc.Get(Strings.Stage.Artist.FansAlsoLike), s, new BoxEl { Direction = 0, Gap = gap, MinWidth = 0f, Children = tiles });
    }
}
