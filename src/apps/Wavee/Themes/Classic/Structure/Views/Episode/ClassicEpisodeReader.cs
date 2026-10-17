// ── Themes/Classic/Structure/Views/Episode/ClassicEpisodeReader.cs — Classic's episode reader (themes plan 6.1, T9c) ───────────────────────
//
// From `Entities\Episode.Page.cs` (`EpisodeReader`, `ChapterReader`): About · Chapters · Transcript · Comments as ONE scroll-spy pivot (plan
// §5.1) — the pivot band over a scroll of stacked sections — the About section's description region with its recognised chapter-seek list, and
// the chapters list. The spy (the anchors, the viewport, the active section), the chapter lease, the saved / playing / resume decisions and
// the transcript view stay core; this file draws what the host hands it.
//
// The pivot sits in a `ClassicDetailBand.Band`-geometry row (the same header height and pivot metrics Show / Album / Artist's own bands use), which is
// why the band helpers are shared (`ClassicDetailBand`): the artist and profile pages build the same band.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's episode reader.</summary>
internal static class ClassicEpisodeReader
{
    /// <summary>The registered template of <see cref="ViewKeys.EpisodeReader"/>.</summary>
    public static Element ReaderTemplate(EpisodeReaderProps p, in ViewContext ctx)
    {
        (string Label, Action OnClick)[] pivotItems =
        [
            (Loc.Get(Strings.Podcast.Reader.About), p.SectionClicks[0]),
            (Loc.Get(Strings.Podcast.Reader.Chapters), p.SectionClicks[1]),
            (Loc.Get(Strings.Podcast.Reader.Transcript), p.SectionClicks[2]),
            (Loc.Get(Strings.Podcast.Reader.Comments), p.SectionClicks[3]),
        ];
        Element band = new BoxEl
        {
            Height = Detail.BandLayout.Height, Shrink = 0, MinWidth = 0, Direction = 1, Children =
            [
                ClassicDetailBand.Band(float.NaN, Spacing.L, [ClassicDetailBand.Pivot(pivotItems, p.Active, static () => Tok.AccentDefault)])
                    with { Grow = 1, Basis = 0, MinWidth = 0 },
                ClassicDetailBand.BandHairline(),
            ],
        };

        Element sections = new BoxEl
        {
            Direction = 1, MinWidth = 0, Padding = Edges4.All(Spacing.L), Gap = Spacing.XXL, Children =
            [
                Section(p, 0, PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.About)), new BoxEl
                {
                    Direction = 1, Gap = Spacing.XL, MinWidth = 0, Children = [p.About, p.Related],
                }),
                Section(p, 1, PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.Chapters)), p.Chapters),
                Section(p, 2, PodcastReaderUI.Heading(Loc.Get(Strings.Podcast.Reader.Transcript)), new BoxEl
                {
                    // The transcript view manages its own internal (virtualized) scroll — Lyrics.Transcript.cs's
                    // ViewCore is shared with the rail/expanded player and expects a bounded ancestor. A scroll
                    // within THIS page scroll (rather than an unbounded natural height) is the honest tradeoff:
                    // every other section is truly stacked/natural-height, this one nests its own scroller.
                    Direction = 1, Height = ClassicEpisodeRules.TranscriptHeight, MinHeight = 0, MinWidth = 0, ClipToBounds = true,
                    Children = [p.Transcript],
                }),
                Section(p, 3, null, p.Comments),
            ],
        };

        return new BoxEl
        {
            Direction = 1, Grow = 1, MinWidth = 0, MinHeight = 0, ClipToBounds = true, Children =
            [
                band,
                ScrollView(sections) with
                {
                    Key = "episode-scroll:" + p.Key, Grow = 1, MinHeight = 0, ScrollKey = p.ScrollKey,
                    AutoEdgeFade = true, AutoEdgeFadeBand = ClassicEpisodeRules.EdgeFadeBand,
                    OnRealized = p.CaptureViewport, Handle = p.Scroll,
                },
            ],
        };
    }

    // Comments already renders its own "Comments (n)" heading (the discussion thread) — no double heading.
    // OnRealized captures this section's anchor node for the spy / GoToSection — the outer box IS the anchor, so
    // its top is exactly what a pivot click parks under the band.
    static Element Section(EpisodeReaderProps p, int index, Element? heading, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = ClassicEpisodeRules.ReaderMeasure, OnRealized = p.Anchors[index],
        Children = heading is null ? [body] : [heading, body],
    };

    // ══ THE ABOUT SECTION ══════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeAbout"/>.</summary>
    public static Element AboutTemplate(EpisodeAboutProps p, in ViewContext ctx)
    {
        var children = new List<Element>
        {
            Skel.Region(p.Description,
                () => Controls.RichTextFlex(p.Seed, 14, Tok.TextSecondary, Tok.AccentTextPrimary, 8),
                text => AboutBody(p, text),
                onFailed: () => PodcastReaderUI.Failed(p.Retry),
                isEmpty: string.IsNullOrWhiteSpace,
                onEmpty: () => PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Empty))),
        };
        if (p.PreviewOnly) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Preview)));
        if (p.Paywalled) children.Add(PodcastReaderUI.Quiet(Loc.Get(Strings.Podcast.Reader.Paywall)));
        return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0, MaxWidth = ClassicEpisodeRules.ReaderMeasure, Children = children.ToArray() };
    }

    /// <summary>The 8-line collapsible prose PLUS, when the show-notes text carries the CHAPTERS(hh:mm:ss)/bare
    /// (hh:mm:ss) convention (<see cref="RichTextBlocks"/>), a seek list under it — each entry starts the episode
    /// at that position if it is not already playing, or seeks it there if it is (the host's seek verb; PlayEpisode is a
    /// no-op restart when it is already the current episode).</summary>
    static Element AboutBody(EpisodeAboutProps p, string text)
    {
        var (prose, chapters) = RichTextBlocks.Split(text);
        var rows = new List<Element>
        {
            Controls.ExpandableRichTextFlex(prose, 14, Tok.TextSecondary, Tok.AccentTextPrimary, 8, p.Uri, p.Open),
        };
        if (chapters.Length > 0)
        {
            var links = new Element[chapters.Length];
            for (int i = 0; i < links.Length; i++)
            {
                var link = chapters[i];
                int startMs = link.StartMs;
                links[i] = ChapterRow(p.Uri + ":notes-chapter:" + i, startMs, link.Title, active: false, p.Playing, () => p.SeekTo(startMs));
            }
            rows.Add(new BoxEl { Direction = 1, Gap = Spacing.XXS, MinWidth = 0, Children = links });
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
            rows[i] = ChapterRow(chapter.Key, startMs, chapter.Title, chapter.Active, p.Playing, () => p.SeekTo(startMs));
        }
        return new BoxEl { Direction = 1, Gap = Spacing.XS, MinWidth = 0, MaxWidth = ClassicEpisodeRules.ReaderMeasure, Children = rows };
    }

    /// <summary>One chapter/timestamp row, shared by the Chapters section and the description's recognised
    /// CHAPTERS seek list: a fixed-width timestamp, the title, the active tint, and a click that seeks.</summary>
    static Element ChapterRow(string key, int startMs, string title, bool active, IReadSignal<bool> playing, Action onClick) => new BoxEl
    {
        Key = key, Direction = 0, MinWidth = 0, Gap = Spacing.M,
        Padding = Edges4.All(Spacing.M), Corners = Radii.ControlAll,
        Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            active
                ? new BoxEl { Width = ClassicEpisodeRules.ChapterStampWidth, Shrink = 0, Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Children =
                    [Controls.Equalizer(playing, Tok.AccentTextPrimary, ClassicEpisodeRules.ChapterStampSize),
                     new TextEl(PodcastReaderRules.Timestamp(startMs)) { Size = ClassicEpisodeRules.ChapterStampSize, Color = Tok.AccentTextPrimary }] }
                : new TextEl(PodcastReaderRules.Timestamp(startMs))
                    { Width = ClassicEpisodeRules.ChapterStampWidth, Shrink = 0, Size = ClassicEpisodeRules.ChapterStampSize, Color = Tok.TextSecondary },
            title.Length > 0
                ? new TextEl(title) { Grow = 1, Basis = 0, MinWidth = 0, Wrap = TextWrap.Wrap, Size = ClassicEpisodeRules.ChapterTitleSize,
                    LineHeight = ClassicEpisodeRules.ChapterTitleLine, Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary }
                : new BoxEl(),
        ],
    };
}
