using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The expanded row's DETAILS STRIP: everything the track carries, set as flat typography above the versions
/// section inside the drawer. No tiles, no chips, no fills, no rules — three lines of type and the whitespace between
/// them.
///
/// <para>WHY FLAT AND NOT A BENTO. The strip used to be nine filled tiles plus a row of bordered pills, and it was
/// chrome-crowded in a place that can least afford it: the drawer already carries a connector rail, a self-row plate,
/// artwork, waveform bars and a format button, so nine more rounded rectangles turned "what is this song" into a wall
/// of boxes with values hidden inside them. The information never needed the boxes — a play count and a tempo are
/// GLANCEABLE, and what makes them glanceable is size, not a container. So the values got the size (28/36 display
/// numerals, <see cref="WaveeType.StatHero"/>) and the containers went away.</para>
///
/// <para>THREE LINES, THREE JOBS. The hero row is the four figures a reader opens a row to see — Plays, BPM, Key,
/// Duration — each a big numeral over its own sentence-case caption. The prose line is everything that is a SENTENCE
/// rather than a figure (added, album, released, added by, ISRC), joined by middots at the caption rung, because a
/// date and an album name read as text and boxing text is what made the old strip look like a form. The genre line is
/// the descriptors and flags, one rung quieter still, because those are what the song is ABOUT rather than what it
/// is.</para>
///
/// <para>WHAT SURVIVED THE BENTO. The reflow property, which is the whole reason the old shape was chosen: these are
/// still <c>Wrap = true</c> flex rows, so <c>FlexLayout.ArrangeWrap</c> fills each line edge to edge and the strip
/// reflows over the same 400–1400 DIP range with no arm — four stats on one line when the list is wide, two when the
/// queue pane is open. And the section no longer needs an eyebrow to name it: the numerals self-head it, which is one
/// less label competing with the "Versions and formats" caption below.</para>
///
/// <para>Motion: each hero stat is KEYED BY ITS FACT and fades up; the row staggers them left to right on the house
/// <see cref="WaveeMotion.MastheadStaggerMs"/> cadence, and reduced motion is a VALUE (the stagger reads 0), never a
/// branch that changes what is authored. The stats also FLIP, so a fact that lands late — kind 222 tempo, kind 185
/// plays — reflows its neighbours smoothly instead of snapping them narrower. The prose and genre lines fade and FLIP
/// as WHOLE LINES: a middot-joined sentence that staggered word by word would read as a teleprompter.</para></summary>
internal static class TrackFactsStrip
{
    /// <summary>The air BETWEEN two hero stats. There is no separator to carry the boundary any more, so the gap is
    /// the boundary — and at 20 DIP two numerals still read as one row rather than as two.
    ///
    /// <para>It is spent TWICE (the row's <c>Gap</c> plus a right margin on every stat) because <c>BoxEl</c> publishes
    /// ONE uniform <c>Gap</c> that a wrapping row spends on both axes: asking for the ~40 DIP the horizontal rhythm
    /// wants would put 40 DIP between wrapped LINES too, which double-spaces the strip the moment the drawer narrows.
    /// Gap 20 + margin 20 buys 40 across and keeps 20 down.</para></summary>
    const float StatGap = Spacing.XL;

    static readonly LayoutTransition TileReflow = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(Expressive.Fast, Easing.SmoothOut),
        SizeMode.Reveal);

    /// <summary>Build the strip, or an empty box when the track states nothing at all (a shimmer row, an empty slot).
    ///
    /// <paramref name="go"/> routes the ONE link this strip carries (the album), through the same
    /// <c>RichText.RouteForUri</c> table the row's own album lane and metadata subline use — an episode row's "album"
    /// is its SHOW, and the table is what keeps those two from disagreeing.</summary>
    internal static Element Build(Track track, TrackFactsOptions options, Action<string, string?> go)
    {
        var facts = TrackExpandedFacts.For(track, options);
        if (facts.Count == 0) return new BoxEl();

        var heroes = new List<Element>(4);
        var prose = new List<Element>(10);
        var genres = new List<Element>(10);

        for (int i = 0; i < facts.Count; i++)
        {
            var f = facts[i];
            switch (f.Form)
            {
                case TrackFactForm.Chips:
                    if (f.Chips is { Count: > 0 } tags)
                        for (int t = 0; t < tags.Count; t++)
                            Join(genres, GenreRun(tags[t]));
                    break;
                case TrackFactForm.Flag:
                    Join(genres, FlagRun(f.Kind));
                    break;
                default:
                    // Value / Link / Pending. The split is by ROLE, not by form: a figure goes to the hero row and a
                    // sentence goes to the prose line, and TrackExpandedFacts owns which is which so the strip cannot
                    // drift from the ordering rules beside it.
                    if (TrackExpandedFacts.IsHeroFact(f.Kind)) heroes.Add(Stat(f));
                    else Join(prose, ProseRun(f, go));
                    break;
            }
        }

        // Reduced motion as a VALUE, the house idiom — the authored tree is identical either way.
        float stagger = Motion.ReducedMotion ? 0f : WaveeMotion.MastheadStaggerMs;

        var body = new List<Element>(3);
        if (heroes.Count > 0)
            body.Add(new BoxEl
            {
                Key = "facts-hero", Direction = 0, Wrap = true, Gap = StatGap, MinWidth = 0f,
                Stagger = stagger, Children = heroes.ToArray(),
            });
        if (prose.Count > 0)
            body.Add(new BoxEl
            {
                Key = "facts-prose", Enter = DetailRail.FadeUp, Layout = TileReflow,
                Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                Children = prose.ToArray(),
            });
        if (genres.Count > 0)
            body.Add(new BoxEl
            {
                Key = "facts-genres", Enter = DetailRail.FadeUp, Layout = TileReflow,
                Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
                Children = genres.ToArray(),
            });

        return new BoxEl
        {
            Key = "facts", Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            // A whole step between the three lines, where the tiled strip used one: with no fills to draw the
            // boundaries, the only thing separating "the figures" from "the sentence" is the air between them.
            Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.S),
            Children = body.ToArray(),
        };
    }

    /// <summary>One hero figure: the numeral over its caption. Keyed by the fact KIND (not by its value), so a value
    /// that changes cross-swaps in place while a fact that ARRIVES fades up as a new stat.
    ///
    /// <para><c>Shrink = 0</c>: a stat is two short runs and has nothing to give up — under pressure the wrap row must
    /// move it to the next line, not crush "1,204,882" into "1,20…". <c>Gap = 0</c> between the two runs for the same
    /// reason the pair reads as a pair at all: the 28/36 line box already carries its own leading under the digits,
    /// and an explicit gap on top of it double-spaces them apart.</para></summary>
    static Element Stat(in TrackFact f)
    {
        bool pending = f.Form == TrackFactForm.Pending;
        // A pending fact already CARRIES its em dash (TrackExpandedFacts.Dash) — the strip does not mint a second one,
        // and it does not split a dash into a value and a unit either.
        var split = pending ? default : TrackExpandedFacts.HeroSplit(f);
        return new BoxEl
        {
            Key = "fact:" + f.Kind, Enter = DetailRail.FadeUp, Layout = TileReflow,
            Direction = 1, Gap = 0f, Shrink = 0f, MinWidth = 0f,
            Margin = new Edges4(0f, 0f, StatGap, 0f),
            Children =
            [
                pending ? WaveeType.StatHero(f.Value, null) : WaveeType.StatHero(split.Value, split.Unit),
                // Caption rung at 600 — the metrics of an eyebrow with none of its tracking, which is exactly what the
                // Eyebrow doc prescribes for text that wants this RUNG without claiming to be a section label. The
                // string is the same sentence-case loc label the tiles used; the caption is what a stat is READ by.
                Ui.Caption(Loc.Get(TrackExpandedFacts.LabelKey(f.Kind))) with
                {
                    Weight = 600, Color = Tok.TextTertiary,
                    MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
            // The pending dash is a state, not a value: dimming the whole stat is what tells the reader "we asked and
            // are still waiting" without spending a second line of copy on it.
            Opacity = pending ? 0.6f : 1f,
        };
    }

    /// <summary>One item on the prose line. Facts whose value is self-describing (an album name, an ISRC) stand alone;
    /// the three that are not — a date, a date, a person — take their label as a TERTIARY lead-in, because "Saturday,
    /// 28 September 2024" and "12 March 2019" sitting middot-separated in the same sentence are otherwise two
    /// indistinguishable dates.
    ///
    /// <para>Label and value shape as ONE paragraph rather than as two flex children, so the wrap row can never break
    /// "Released" onto one line and its date onto the next.</para></summary>
    static Element ProseRun(in TrackFact f, Action<string, string?> go)
    {
        if (f.Form == TrackFactForm.Link) return LinkValue(f, go);
        if (!NeedsLabel(f.Kind)) return ProseText(f.Value);

        var caption = Ui.Caption("");
        return new SpanTextEl(
        [
            new TextSpan(Loc.Get(TrackExpandedFacts.LabelKey(f.Kind)) + " ", Color: Tok.TextTertiary),
            new TextSpan(f.Value, Color: Tok.TextSecondary),
        ])
        {
            Size = caption.Size,
            Weight = caption.ResolvedWeight,
            LineHeight = caption.LineHeight,
            LineStacking = caption.LineStacking,
            LineBounds = caption.LineBounds,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
        };
    }

    /// <summary>Which facts cannot say what they are from their value alone. Two dates and a person's name — the rest
    /// (an album, an ISRC) are unmistakable and a label in front of them is just noise in a sentence.</summary>
    static bool NeedsLabel(TrackFactKind kind) =>
        kind is TrackFactKind.Added or TrackFactKind.Released or TrackFactKind.AddedBy;

    /// <summary>A bare prose value at the caption rung. Single-line and shrinkable rather than the old two-line wrap:
    /// on one flowing line an internally-wrapped item would break the middot rhythm, and the flex wrap already gives a
    /// long value its own line before it is ever asked to ellipsise.</summary>
    static Element ProseText(string s) => Ui.Caption(s).Secondary() with
    {
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>The album value as a hyperlink span — the existing <c>RichText</c>/<c>SpanTextEl</c> idiom, identical
    /// to <c>TrackRow.AlbumLink</c>, so the strip's album and the row's album lane open the same page. It carries no
    /// accent and no underline: on a line of middot-joined facts the album is one fact among several, and the pointer
    /// (the engine resolves the Hand cursor for a span with an <c>OnClick</c>) is the affordance.</summary>
    static Element LinkValue(in TrackFact f, Action<string, string?> go)
    {
        string name = f.Value;
        Action? open = null;
        if (RichText.RouteForUri(f.LinkUri) is { } route)
            open = () => go(route, name.Length > 0 ? name : null);

        var caption = Ui.Caption("");
        return new SpanTextEl([new TextSpan(name, OnClick: open)])
        {
            Size = caption.Size,
            Weight = caption.ResolvedWeight,
            LineHeight = caption.LineHeight,
            LineStacking = caption.LineStacking,
            LineBounds = caption.LineBounds,
            Color = Tok.TextSecondary,
            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
        };
    }

    /// <summary>One descriptor or flag label, at the quietest rung the strip uses. Descriptors no longer take the
    /// accent-subtle pill the Liked lens uses: a pill is a CONTROL you can press, and these are not — the row's own
    /// lens filters are, and borrowing their look for six inert words was the loudest thing in the drawer.</summary>
    static Element GenreRun(string label) => Ui.Caption(label).Tertiary() with
    {
        Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MaxLines = 1, MinWidth = 0f, Shrink = 1f,
    };

    /// <summary>A flag, with its mark. The glyph leads the label as an inline pair so a wrap can never strand the icon
    /// at the end of a line; a flag with no glyph (Explicit) is just another word on the genre line.</summary>
    static Element FlagRun(TrackFactKind kind)
    {
        string label = Loc.Get(TrackExpandedFacts.LabelKey(kind));
        if (FlagGlyph(kind) is not { Length: > 0 } glyph) return GenreRun(label);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XXS, Shrink = 0f, MinWidth = 0f,
            Children = [Icon(glyph, 11f, Tok.TextTertiary), GenreRun(label)],
        };
    }

    /// <summary>Join one run onto a line, minting the middot that separates it from the previous one. Separators are
    /// CHILDREN, the same idiom the track row's meta line and the drawer's version rows use, so the wrap row measures
    /// them and the line packs honestly.</summary>
    static void Join(List<Element> line, Element run)
    {
        if (line.Count > 0)
            line.Add(Ui.Caption("·").Tertiary() with { Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f });
        line.Add(run);
    }

    /// <summary>The flag's leading glyph. Explicit gets none: its label IS the mark, and the row above already carries
    /// the E badge — a second icon for the same fact is noise.</summary>
    static string? FlagGlyph(TrackFactKind kind) => kind switch
    {
        TrackFactKind.Video => Icons.Movie,
        TrackFactKind.LocalFile => Icons.Folder,
        TrackFactKind.Unavailable => Icons.Info,
        _ => null,
    };
}
