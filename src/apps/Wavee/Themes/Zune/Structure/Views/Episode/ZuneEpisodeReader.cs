// ── Themes/Zune/Structure/Views/Episode/ZuneEpisodeReader.cs — Zune's episode reader (themes plan 6.3; P5 Z6b2) ───────────────────────────
//
// Board Z-Episode's right column: about · chapters · transcript · comments as ONE scroll-spy page — every section mounted STACKED in one scroll
// (no tab switch, no unmount) under its 26/32 light heading, the reading measure 760. The pivot is not drawn here: the four words are the band's
// row 2 (the page publishes them through the `PageChromeSink`, a click scrolls its section, the spy lights the current one), so the reader is
// the scroll alone and the band is outside its viewport.
//
//   about       the description at 15/22 with the inline "more" (and, when the show notes carry a CHAPTERS list, its seek list);
//   chapters    the BIG NUMERAL rows (Z-Episode: a 22 light numeral in a 44 column, the title at 15, the start time at 12 under it), the playing
//               chapter in the accent on the selected wash, a click seeks;
//   transcript  the heading over the core's transcript view (it manages its own virtualized scroll inside this scroll);
//   comments    the discussion thread (`ZuneEpisodeDiscussion.cs`), which writes its own heading.
//
// Board versus contract: the board lays chapters and transcript side by side; the page's contract is a stack of anchored sections the spy
// reads one top at a time, so the stack stays (a side-by-side pair would give two sections the same top).

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
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.Zune.ZuneEpisodeRules;
using K = Wavee.Themes.Zune.ZuneDetailKit;

namespace Wavee.Themes.Zune;

/// <summary>Zune's episode reader.</summary>
internal static class ZuneEpisodeReader
{
    /// <summary>The registered template of <see cref="ViewKeys.EpisodeReader"/>.</summary>
    public static Element ReaderTemplate(EpisodeReaderProps p, in ViewContext ctx)
    {
        Element sections = new BoxEl
        {
            Key = ReaderKey, Direction = 1, MinWidth = 0, Gap = R.Space.SectionGap,
            Padding = new Edges4(0, ReaderTop, 0, R.Space.DockReserve + ReaderBottom),
            Children =
            [
                Section(p, 0, Loc.Get(Strings.Podcast.Tabs.About), new BoxEl
                {
                    Direction = 1, Gap = Spacing.XL, MinWidth = 0, Children = [p.About, p.Related],
                }),
                Section(p, 1, Loc.Get(Strings.Podcast.Tabs.Chapters), p.Chapters),
                Section(p, 2, Loc.Get(Strings.Podcast.Tabs.Transcript), new BoxEl
                {
                    // The transcript view manages its own internal (virtualized) scroll — it is shared with the rail / expanded player and expects a
                    // bounded ancestor. A scroll within THIS page scroll (rather than an unbounded natural height) is the honest tradeoff: every
                    // other section is truly stacked, this one nests its own scroller.
                    Direction = 1, Height = TranscriptHeight, MinHeight = 0, MinWidth = 0, ClipToBounds = true,
                    Children = [p.Transcript],
                }),
                Section(p, 3, null, p.Comments),
            ],
        };

        return new BoxEl
        {
            Direction = 1, Grow = 1, MinWidth = 0, MinHeight = 0, ClipToBounds = true, Children =
            [
                ScrollView(sections) with
                {
                    Key = "episode-scroll:" + p.Key, Grow = 1, MinHeight = 0, ScrollKey = p.ScrollKey,
                    AutoEdgeFade = true, AutoEdgeFadeBand = EdgeFadeBand,
                    OnRealized = p.CaptureViewport, Handle = p.Scroll,
                },
            ],
        };
    }

    /// <summary>One section: its anchor (the outer box IS the anchor, so its top is exactly what a row-2 click parks at and what the spy reads), the
    /// heading (comments write their own) and the body. The words are a heading the theme writes, lowered.</summary>
    static Element Section(EpisodeReaderProps p, int index, string? heading, Element body) => new BoxEl
    {
        Key = "episode-section:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = ReaderMeasure, OnRealized = p.Anchors[index],
        Children = heading is null ? [body] : [K.Heading(heading), body],
    };

    // ══ THE ABOUT SECTION ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeAbout"/>.</summary>
    public static Element AboutTemplate(EpisodeAboutProps p, in ViewContext ctx)
    {
        var children = new List<Element>
        {
            Skel.Region(p.Description,
                () => Controls.RichTextFlex(p.Seed, AboutSize, Tok.TextSecondary, R.Ink.AccentDecor, SeedLines),
                text => AboutBody(p, text),
                onFailed: () => ZuneEpisode.Failed(p.Retry),
                isEmpty: string.IsNullOrWhiteSpace,
                onEmpty: () => ZuneEpisode.Quiet(Loc.Get(Strings.Podcast.Reader.Empty))),
        };
        if (p.PreviewOnly) children.Add(ZuneEpisode.Quiet(Loc.Get(Strings.Podcast.Reader.Preview)));
        if (p.Paywalled) children.Add(ZuneEpisode.Quiet(Loc.Get(Strings.Podcast.Reader.Paywall)));
        return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = ReaderMeasure, Children = children.ToArray() };
    }

    /// <summary>The collapsible prose PLUS, when the show-notes text carries the CHAPTERS(hh:mm:ss) / bare (hh:mm:ss) convention
    /// (<see cref="RichTextBlocks"/>), a seek list under it — each entry starts the episode at that position if it is not already playing, or
    /// seeks it there if it is (the host's seek verb).</summary>
    static Element AboutBody(EpisodeAboutProps p, string text)
    {
        var (prose, chapters) = RichTextBlocks.Split(text);
        var rows = new List<Element>
        {
            Controls.ExpandableRichTextFlex(prose, AboutSize, Tok.TextSecondary, R.Ink.AccentDecor, AboutLines, p.Uri, p.Open),
        };
        if (chapters.Length > 0)
        {
            var links = new Element[chapters.Length];
            for (int i = 0; i < links.Length; i++)
            {
                var link = chapters[i];
                int startMs = link.StartMs;
                links[i] = ChapterRow(p.Uri + ":notes-chapter:" + i, i, startMs, link.Title, active: false, p.Playing, () => p.SeekTo(startMs));
            }
            rows.Add(new BoxEl { Direction = 1, MinWidth = 0, Children = links });
        }
        return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, Children = rows.ToArray() };
    }

    // ══ THE CHAPTERS ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeChapters"/>.</summary>
    public static Element ChaptersTemplate(EpisodeChaptersProps p, in ViewContext ctx)
    {
        var rows = new Element[p.Rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var chapter = p.Rows[i];
            int startMs = chapter.StartMs;
            rows[i] = ChapterRow(chapter.Key, i, startMs, chapter.Title, chapter.Active, p.Playing, () => p.SeekTo(startMs));
        }
        return new BoxEl { Direction = 1, MinWidth = 0, MaxWidth = ReaderMeasure, Children = rows };
    }

    /// <summary>One chapter row, shared by the Chapters section and the description's recognised CHAPTERS seek list (Z-Episode): the big
    /// numeral (22 light, tertiary; the accent while playing) in its own column, the title at 15 (the accent while playing) and the start time at
    /// 12 under it — with the equalizer before the time while it plays. The playing row sits on the selected wash; a click seeks.</summary>
    static Element ChapterRow(string key, int index, int startMs, string title, bool active, IReadSignal<bool> playing, Action onClick)
    {
        var words = new List<Element>(2);
        if (title.Length > 0)
            words.Add(R.Type.TrackTitle(title) with
            {
                Color = active ? Prop.Of(static () => R.Ink.AccentDecor) : (Prop<ColorF>)Tok.TextPrimary,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0,
            });
        var time = R.Type.MicroMeta(PodcastReaderRules.Timestamp(startMs)) with
        {
            Size = ChapterTimeSize, LineHeight = ChapterTimeLine, Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
        };
        words.Add(active
            ? new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0,
                Children = [Controls.Equalizer(playing, static () => R.Ink.AccentAction, ChapterEqualizer), time],
            }
            : time);
        return new BoxEl
        {
            Key = key, Direction = 0, MinWidth = 0, MinHeight = ChapterRowH, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Fill = active ? Prop.Of(static () => R.Layer.SelectedRest) : ColorF.Transparent,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Width = ChapterNumeralW, Shrink = 0, Direction = 0, Padding = new Edges4(ChapterNumeralInset, 0, 0, 0),
                    Children =
                    [
                        R.Type.Numeral((index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)) with
                        {
                            Color = active ? Prop.Of(static () => R.Ink.AccentDecor) : (Prop<ColorF>)Tok.TextTertiary, MaxLines = 1,
                        },
                    ],
                },
                new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Justify = FlexJustify.Center, Children = words.ToArray() },
            ],
        };
    }
}
