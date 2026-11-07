// ── Themes/WinUi/Structure/Views/Episode/WinUiEpisode.cs — WinUI's episode page (W-Episode, -Compact, -Small, -Huge; winui show pages) ───────────────────
//
// Until this lane the episode page's keys were Classic's stand-ins under WinUI's colours: a play pill alone on the action row, the satellites as a row of
// bare glyphs under it, Classic's underlined band pivot and one stacked column. W-Episode draws:
//
//   the header (WinUI's show header, `WinUiDetailShow.cs`): "Episode · <the show>" (the show a link), the title, "23 Sep 2026 · 54 min", then the action
//      row: the accent primary ("Pause", "Resume · 31 min left", "Play"), "✓ Mark as played" and "Save to Your Episodes" as standard buttons with their
//      glyphs, share and "..." as subtle glyphs. At Compact (W-Episode-Compact) the primary fills the row and the mark and the save are 40 squares.
//   the reader: a SelectorBar (About · Chapters · Transcript · Comments, the spy lights the section in view) over the sections; at Standard and Huge two
//      columns, About and Chapters (and "More from the show" at Huge) on the left, the Transcript on the right; at Small About beside Chapters, the
//      Transcript and the Comments under them; at Compact one column.
//
// The discussion (the comments' thread, cards, flyout) keeps the stand-in's pictures: no WinUI board draws a thread.
//
// What stays core: the page host and its demand, the saved / played / resume decisions, the scroll spy (anchors, the viewport, the active section), the
// chapter lease, the transcript view, the discussion. A template draws and never names `Playback`, `Entities`, `Store`, `Shell` or `Spotify` (WVTH002).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using E = Wavee.Themes.WinUi.WinUiEpisodeRules;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's episode family: the registration, the header's slots and the reader.</summary>
internal static class WinUiEpisode
{
    /// <summary>Registers WinUI's own episode keys; the discussion's stay the stand-in's.</summary>
    internal static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.EpisodePrimary, Primary, "winui.episode.primary");
        views.Add(ViewKeys.EpisodeAttribution, Attribution, "winui.episode.attribution");
        views.Add(ViewKeys.EpisodeActions, Actions, "winui.episode.actions");
        views.Add(ViewKeys.EpisodeSave, Save, "winui.episode.save");
        views.Add(ViewKeys.EpisodeRailAbout, RailAbout, "winui.episode.rail-about");
        views.Add(ViewKeys.EpisodeRating, Rating, "winui.episode.rating");
        views.Add(ViewKeys.EpisodeReader, ReaderTemplate, "winui.episode.reader");
        views.Add(ViewKeys.EpisodeAbout, AboutTemplate, "winui.episode.about");
        views.Add(ViewKeys.EpisodeChapters, ChaptersTemplate, "winui.episode.chapters");
    }

    static bool Compact(in ViewContext ctx) => WinUiDetailTiers.Of(ctx.Tier()).Tier == WidthTier.Compact;

    static float Target(in ViewContext ctx) => WinUiDetailTiers.Of(ctx.Tier()).Target;

    /// <summary>A quiet line (a blocked episode, the empty and preview notes): Body, secondary, wrapping.</summary>
    static Element Quiet(string text) => Body(text) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f };

    // ══ 1. THE HEADER'S SLOTS ═══════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The primary: the accent button with the play or pause glyph and the host's label ("Pause", "Resume · 31 min left", "Play"); 40 tall at
    /// Compact, where the action row lets it fill the row. A blocked episode says why instead.</summary>
    static Element Primary(EpisodePrimaryProps p, in ViewContext ctx)
    {
        if (p.Blocked is { } blocked) return Quiet(blocked);
        var size = Compact(in ctx) ? ControlSize.Large : ControlSize.Medium;
        return Button.Create(p.Label, p.Play, ButtonAppearance.Accent, size, glyph: p.Playing ? Icons.Pause : Icons.Play) with { Key = "episode:primary" };
    }

    /// <summary>The show's name on the eyebrow's line ("Episode · Coffee & Code"): a link in the accent text ink, the eyebrow's Caption.</summary>
    static Element Attribution(EpisodeAttributionProps p, in ViewContext ctx)
    {
        // The shared row text grows into a flex row; on the eyebrow's line it takes its own width after "Episode ·".
        Element row = Controls.RichTextRow(p.Html, Caption("").Size, Tok.AccentTextPrimary, Tok.AccentTextPrimary, p.Open);
        return row is SpanTextEl text ? text with { Grow = 0f, Basis = float.NaN, Shrink = 1f, MinWidth = 0f } : row;
    }

    /// <summary>The satellites: "✓ Mark as played" (standard, its glyph and word), the save, share and "..." (subtle glyphs); at Compact the mark and the save
    /// are 40 squares and share leaves the row (it is in the menu), W-Episode-Compact.</summary>
    static Element Actions(EpisodeActionsProps p, in ViewContext ctx)
    {
        bool compact = Compact(in ctx);
        float target = Target(in ctx);
        string markName = Loc.Get(p.Completed ? Strings.Podcast.Menu.MarkUnplayed : Strings.Podcast.Menu.MarkPlayed);
        string markGlyph = p.Completed ? Icons.Undo : Icons.Check;
        var kids = new List<Element>(4)
        {
            compact
                ? Controls.Named(Square(markGlyph, p.ToggleCompleted, target), markName) with { Key = "episode:mark" }
                : Button.Create(markName, p.ToggleCompleted, ButtonAppearance.Standard, glyph: markGlyph) with { Key = "episode:mark", Shrink = 0f },
            p.Save,
        };
        if (!compact) kids.Add(Controls.Named(Controls.IconAction(Icons.Share, p.CopyLink, size: target), Loc.Get(Strings.Menu.CopyLink)) with { Key = "episode:share" });
        kids.Add(WinUiDetailKit.MoreButton(p.Menu, target) with { Key = "episode:more" });
        return new BoxEl
        {
            Direction = 0, Gap = WinUiDetailRules.ActionGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>A bordered square glyph button (W-Episode-Compact's mark and save).</summary>
    static BoxEl Square(string glyph, Action? onClick, float target) => Controls.IconAction(glyph, onClick, size: target, isEnabled: onClick is not null) with
    {
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
    };

    /// <summary>The save: "Save to Your Episodes" (a standard button with the host's glyph and name), the 40 square at Compact. The state is the host's.</summary>
    static Element Save(EpisodeSaveProps p, in ViewContext ctx)
    {
        if (Compact(in ctx)) return Controls.Named(Square(p.Glyph, p.Click, Target(in ctx)), p.Name) with { Key = "episode:save" };
        return Button.Create(p.Name, p.Click ?? s_noop, ButtonAppearance.Standard, glyph: p.Glyph, isEnabled: p.Click is not null) with { Key = "episode:save", Shrink = 0f };
    }

    static readonly Action s_noop = static () => { };

    /// <summary>The rail's short About (a structure with a rail draws it; WinUI's header has none, the reader's About holds the prose): Body, five lines.</summary>
    static Element RailAbout(EpisodeRailAboutProps p, in ViewContext ctx)
        => Controls.ExpandableRichTextFlex(p.Prose, E.AboutSize, Tok.TextSecondary, Tok.AccentTextPrimary, E.AboutLines, p.Key, p.Open);

    /// <summary>The show rating popover: the title (Subtitle), the stars (disabled while busy or offline) and the error line.</summary>
    static Element Rating(ShowRatingProps p, in ViewContext ctx) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Width = E.RatingWidth, Padding = Edges4.All(Spacing.L),
        Children =
        [
            Subtitle(p.Title) with { Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            Controls.StarRow(p.Rating, p.Rate, E.RatingStar, p.Tone ?? s_accent),
            Caption(p.Error) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
        ],
    };

    static readonly Func<ColorF> s_accent = static () => Tok.AccentDefault;

    // ══ 2. THE READER ══════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeReader"/>: the pivot over the sections' scroll, the columns by the tier.</summary>
    static Element ReaderTemplate(EpisodeReaderProps p, in ViewContext ctx)
    {
        var tier = WinUiDetailTiers.Of(ctx.Tier()).Tier;
        float pad = tier == WidthTier.Compact ? ShowReaderRules.PadNarrow : ShowReaderRules.Pad;
        Element about = Section(p, 0, Loc.Get(Strings.Podcast.Reader.About), new BoxEl
        {
            Direction = 1, Gap = E.SectionGap, MinWidth = 0f, Children = [p.About, p.Related],
        });
        Element chapters = Section(p, 1, Loc.Get(Strings.Podcast.Reader.Chapters), p.Chapters);
        bool twoUp = tier >= WidthTier.Standard;
        Element transcript = Section(p, 2, Loc.Get(Strings.Podcast.Reader.Transcript), new BoxEl
        {
            // The transcript view manages its own (virtualized) scroll and wants a bounded box: a nested scroller in the page's.
            Direction = 1, Height = twoUp ? E.TranscriptHeight : E.TranscriptStackedHeight, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            Children = [p.Transcript],
        });
        Element comments = Section(p, 3, null, p.Comments);

        // ONE tree at every tier: a row of two columns whose direction and gap come from the tier (a column at Compact), then what stands under it. The
        // sections keep their parents across a crossing (Standard / Small move only the transcript between the right column and the stack under it).
        Element body;
        if (tier == WidthTier.Compact)
            body = Stack([about, chapters, transcript, comments]);
        else
        {
            float gap = tier >= WidthTier.Huge ? E.HugeColumnGap : E.ColumnGap;
            Element left = Column("ep:col:left", E.LeftGrow, twoUp ? [about, chapters] : [about]);
            Element right = Column("ep:col:right", E.RightGrow, twoUp ? [transcript] : [chapters]);
            var rows = new List<Element>(3)
            {
                new BoxEl { Key = "ep:columns", Direction = 0, Gap = gap, AlignItems = FlexAlign.Start, MinWidth = 0f, Children = [left, right] },
            };
            if (!twoUp) rows.Add(transcript);
            rows.Add(comments);
            body = Stack(rows.ToArray());
        }

        Element sections = new BoxEl
        {
            Key = "ep:sections", Direction = 1, MinWidth = 0f, Padding = new Edges4(pad, E.PivotGap, pad, R.Space.DockReserve + E.ReaderBottom), Children = [body],
        };
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl
                {
                    Key = "ep:pivot", Direction = 1, Shrink = 0f, MinWidth = 0f, Padding = new Edges4(pad, 0f, pad, 0f),
                    Children =
                    [
                        Embed.Comp(new PivotProps(p.SectionClicks, p.Active), static () => new EpisodePivot()),
                        new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Shrink = 0f },
                    ],
                },
                ScrollView(sections) with
                {
                    Key = "episode-scroll:" + p.Key, Grow = 1f, MinHeight = 0f, ScrollKey = p.ScrollKey,
                    AutoEdgeFade = true, AutoEdgeFadeBand = E.EdgeFadeBand, OnRealized = p.CaptureViewport, Handle = p.Scroll,
                },
            ],
        };
    }

    static Element Stack(Element[] kids) => new BoxEl { Direction = 1, Gap = E.SectionGap, MinWidth = 0f, Children = kids };

    static Element Column(string key, float grow, Element[] kids) => new BoxEl
    {
        Key = key, Direction = 1, Gap = E.SectionGap, Grow = grow, Basis = 0f, MinWidth = 0f, Children = kids,
    };

    /// <summary>One section: its anchor (the outer box: a pivot click parks its top, the spy reads it), the Subtitle heading (the comments write their
    /// own) and the body.</summary>
    static Element Section(EpisodeReaderProps p, int index, string? heading, Element body) => new BoxEl
    {
        Key = "episode-section:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Direction = 1, Gap = E.HeadGap, MinWidth = 0f, OnRealized = p.Anchors[index],
        Children = heading is null ? [body] : [Head(heading), body],
    };

    static TextEl Head(string text) => new(text)
    {
        FontFamily = WinUiShowRules.DisplayFace, Size = E.HeadSize, LineHeight = E.HeadLine, Weight = WinUiShowRules.VoiceWeight, Color = Tok.TextPrimary,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    sealed record PivotProps(Action[] Clicks, IReadSignal<int> Active);

    /// <summary>The pivot: a SelectorBar (About · Chapters · Transcript · Comments) whose selection follows the spy's active section; a pick scrolls there.</summary>
    sealed class EpisodePivot : Component
    {
        readonly Signal<int> _selected = new(0);
        IReadSignal<int>? _active;
        Action[] _clicks = [];
        readonly Action<int> _pick;
        readonly Action _follow;

        public EpisodePivot()
        {
            _pick = i => { if ((uint)i < (uint)_clicks.Length) _clicks[i](); };
            _follow = () =>
            {
                int a = _active?.Value ?? 0;
                if (_selected.Peek() != a) _selected.Value = a;
            };
        }

        public override Element Render()
        {
            var p = UseProps<PivotProps>();
            _active = p.Active;
            _clicks = p.Clicks;
            UseEffect(_follow);
            string[] labels =
            [
                Loc.Get(Strings.Theme.Winui.Detail.About), Loc.Get(Strings.Podcast.Reader.Chapters),
                Loc.Get(Strings.Podcast.Reader.Transcript), Loc.Get(Strings.Podcast.Reader.Comments),
            ];
            return WinUiChoice.Pivot(labels, _selected, E.PivotHeight, _pick);
        }
    }

    // ══ 3. ABOUT AND THE CHAPTERS ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeAbout"/>: the prose (Body 14, "Read more" past five lines), its recognised chapter
    /// list, the quiet notes.</summary>
    static Element AboutTemplate(EpisodeAboutProps p, in ViewContext ctx)
    {
        var children = new List<Element>
        {
            Skel.Region(p.Description,
                () => Controls.RichTextFlex(p.Seed, E.AboutSize, Tok.TextSecondary, Tok.AccentTextPrimary, E.AboutLines),
                text => AboutBody(p, text),
                onFailed: () => new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Start,
                    Children = [Quiet(Loc.Get(Strings.Podcast.Reader.Unavailable)), Button.Create(Loc.Get(Strings.Podcast.Reader.Retry), p.Retry, ButtonAppearance.Standard)],
                },
                isEmpty: string.IsNullOrWhiteSpace,
                onEmpty: () => Quiet(Loc.Get(Strings.Podcast.Reader.Empty))),
        };
        if (p.PreviewOnly) children.Add(Quiet(Loc.Get(Strings.Podcast.Reader.Preview)));
        if (p.Paywalled) children.Add(Quiet(Loc.Get(Strings.Podcast.Reader.Paywall)));
        return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0f, Children = children.ToArray() };
    }

    static Element AboutBody(EpisodeAboutProps p, string text)
    {
        var (prose, chapters) = RichTextBlocks.Split(text);
        var rows = new List<Element> { Controls.ExpandableRichTextFlex(prose, E.AboutSize, Tok.TextSecondary, Tok.AccentTextPrimary, E.AboutLines, p.Uri, p.Open) };
        if (chapters.Length > 0)
        {
            var links = new Element[chapters.Length];
            for (int i = 0; i < links.Length; i++)
            {
                var link = chapters[i];
                int startMs = link.StartMs;
                links[i] = ChapterRow(p.Uri + ":notes-chapter:" + i, startMs, link.Title, active: false, p.Playing, () => p.SeekTo(startMs));
            }
            rows.Add(new BoxEl { Direction = 1, MinWidth = 0f, Children = links });
        }
        return new BoxEl { Direction = 1, Gap = Spacing.M, MinWidth = 0f, Children = rows.ToArray() };
    }

    /// <summary>The registered template of <see cref="ViewKeys.EpisodeChapters"/>.</summary>
    static Element ChaptersTemplate(EpisodeChaptersProps p, in ViewContext ctx)
    {
        var rows = new Element[p.Rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var c = p.Rows[i];
            int startMs = c.StartMs;
            rows[i] = ChapterRow(c.Key, startMs, c.Title, c.Active, p.Playing, () => p.SeekTo(startMs));
        }
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = rows };
    }

    /// <summary>One chapter (W-Episode): a 40 row, the time (Body, tertiary; the accent while it plays) in its column, the title (Body, primary), the playing
    /// row on the selected plate with the accent square at its end; a click seeks.</summary>
    static Element ChapterRow(string key, int startMs, string title, bool active, IReadSignal<bool> playing, Action onClick) => new BoxEl
    {
        Key = key, Direction = 0, MinWidth = 0f, Height = E.ChapterRowH, AlignItems = FlexAlign.Center, Gap = Spacing.M,
        Padding = new Edges4(E.ChapterPadX, 0f, E.ChapterPadX, 0f), Corners = Radii.ControlAll,
        Fill = active ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Body(PodcastReaderRules.Timestamp(startMs)) with
            {
                Width = E.ChapterTimeW, Shrink = 0f, Color = active ? Tok.AccentTextPrimary : Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
            title.Length > 0
                ? Body(title) with { Grow = 1f, Basis = 0f, MinWidth = 0f, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis }
                : new BoxEl { Grow = 1f },
            active ? Controls.Equalizer(playing, Tok.AccentTextPrimary, E.ChapterEqualizer) : new BoxEl { Width = E.ChapterEqualizer, Shrink = 0f },
        ],
    };
}
