// ── Themes/WinUi/Structure/Views/Show/WinUiShowHead.cs — WinUI's show visit head (W6b, winui menus/podcasts) ─────────────────────────────────────
//
// The first screen the visitor needs, in WinUI's voice: New -> "Start here" (the doors: WinUI cards) and "About"; Returning -> "Continue" (a WinUI card
// with the minutes left as Subtitle, an accent ProgressBar and the accent Resume button), the up-next cards and "New since you were here" (the
// episode rows); CaughtUp -> one line; Unavailable -> the hydrate failed. The accent marks progress and the primary, never the cover's tone.
//
// WHAT THE CORE KEEPS (`Show.VisitHead`): the stamp memo that re-renders the head, the results-mode hide, the reveal region's pending read, the
// snapshot and every verb. The head's episodes arrive as slots behind the core's reveal gate (`Episode.Reveal`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.WinUi.WinUiShowRules;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiShow
{
    /// <summary>The ICU select keys <c>podcast.caughtUpNext</c> takes (lowercase English weekday).</summary>
    static readonly string[] s_dayWords = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>The head's padded shell around its reveal region: a hero-height shimmer until the host's pending read clears. The visit the head lands
    /// as decides its height (a new listener's doors and about, a returning one's continue hero, a caught-up line), so the region eases from the
    /// shimmer's height to the head's (SmoothResize) and the episodes under it ride it (audit FU2d: they dropped 52 at once in the landing frame).</summary>
    static Element HeadShell(Func<bool> pending, Func<Element> content) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, HeadTop, 0f, SectionGap),
        Children =
        [
            new SkelRegionEl(Pending: pending, Failed: s_never, Content: content, ShimmerSource: s_headSeed,
                OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: true),
        ],
    };

    static readonly Func<Element> s_headSeed = static () => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0f,
        Children =
        [
            new BoxEl { Width = HeadSeedTitleWidth, Height = ShowReaderRules.HeaderLine, Corners = Radii.ControlAll },
            new BoxEl { Height = HeroSeedHeight, Corners = Radii.CardAll },
        ],
    };

    static Element Head(ShowHeadArgs a) => a.Kind switch
    {
        ShowReaderRules.Head.New => NewHead(a),
        ShowReaderRules.Head.Returning => ReturningHead(a),
        ShowReaderRules.Head.CaughtUp => CaughtUpHead(a),
        ShowReaderRules.Head.Unavailable => QuietLine(Loc.Get(Strings.Podcast.ProgressUnavailable)),
        _ => s_headSeed(),
    };

    /// <summary>W2: start here (up to three doors — a serial leads with episode 1, anything else with the latest; the
    /// trailer joins when there is one) and the full about with the facts line.</summary>
    static Element NewHead(ShowHeadArgs a)
    {
        bool serial = a.Serial;
        // Episode 1 is a door only when the membership is COMPLETE — the oldest resident of a partial list is not it.
        Element? first = a.FirstSlot > 0 && a.FirstSlot != a.LatestSlot
            ? EpisodeDoor(new Episode(a.FirstSlot), Loc.Get(serial ? Strings.Podcast.BeginHere : Strings.Podcast.WhereItBegan),
                          lead: serial, dated: false, a)
            : null;
        Element? latest = a.LatestSlot > 0
            ? EpisodeDoor(new Episode(a.LatestSlot), Loc.Get(Strings.Podcast.Latest), lead: !serial || first is null, dated: true, a)
            : null;
        Element? trailer = a.TrailerSlot > 0 && new Episode(a.TrailerSlot).Knows(EpisodeFields.Title) ? TrailerDoor(new Episode(a.TrailerSlot), a) : null;

        Element?[] order = serial ? [first, trailer, latest] : [latest, trailer, first];
        var doors = new List<Element>(3);
        foreach (var door in order)
            if (door is not null) doors.Add(door.Value);

        var sections = new List<Element>(2);
        if (doors.Count > 0)
            sections.Add(Column(
            [
                SecHead(Pivot(Loc.Get(Strings.Podcast.StartHere)), Loc.Get(serial ? Strings.Podcast.SerialHint : Strings.Podcast.EpisodicHint), stack: a.Narrow),
                EqualRows(doors, a.Doors),
            ], Spacing.M));
        sections.Add(About(a));
        return Column(sections, SectionGap);
    }

    static Element EpisodeDoor(Episode e, string label, bool lead, bool dated, ShowHeadArgs a)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        string length = e.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(e.DurationMs) : "";
        string meta = dated && e.Knows(EpisodeFields.Published) ? Joined(Episode.DateLabel(e.PublishedAt), length) : length;
        return Controls.Door(new DoorData(label, title, DescriptionOf(e), meta, lead, n > 0 ? FormatCache.Int(n) : null,
            Play: () => a.Play(e), Open: () => Episode.OpenPage(e), Tone: s_accent,
            Menu: MenuOf(a, e), PlayUri: e.Uri.Text), a.RowCtx.Overlay);
    }

    /// <summary>The trailer door: its own row (outside the membership), played alone — not as the show context.</summary>
    static Element TrailerDoor(Episode t, ShowHeadArgs a)
        => Controls.Door(new DoorData(Loc.Get(Strings.Podcast.Trailer), t.Title, DescriptionOf(t),
            t.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(t.DurationMs) : null, Lead: false, Numeral: null,
            Play: () => a.PlayAlone(t), Open: () => Episode.OpenPage(t), Tone: s_accent,
            Menu: MenuOf(a, t), PlayUri: t.Uri.Text), a.RowCtx.Overlay);

    /// <summary>The head's episode menu — the SAME one a list row attaches (the head context's <c>Menu</c>, built at OPEN
    /// time), for a head surface that owns its own right-click (a door, the hero, an up-next card). Null without a menu seam.</summary>
    static Func<ContextMenuModel?>? MenuOf(ShowHeadArgs a, Episode e)
        => a.RowCtx is { Menu: { } menu } ? () => menu(e) : null;

    /// <summary><paramref name="el"/> with the head's episode menu attached to it (a right-click anywhere on the card).</summary>
    static Element WithMenu(ShowHeadArgs a, Episode e, BoxEl el)
        => MenuOf(a, e) is { } menu && a.RowCtx.Overlay is { } overlay ? ContextSheets.Attach(el, overlay, menu) : el;

    static string? DescriptionOf(Episode e) => Episode.HasDescription(e) ? Episode.DescriptionOf(e) : null;

    /// <summary>W2's about: the full description at a reading measure (links route in-app) and the facts line.</summary>
    static Element About(ShowHeadArgs a)
    {
        var kids = new List<Element>(3) { SecHead(Pivot(Loc.Get(Strings.Podcast.About))) };
        if (a.About is { } about)
            kids.Add(new BoxEl
            {
                Direction = 1, MaxWidth = AboutMeasure, MinWidth = 0f,
                Children = [Controls.RichTextFlex(about, AboutSize, Tok.TextSecondary, Tok.AccentTextPrimary, 0, DetailVerbs.NavigateKey)],
            });
        kids.Add(Caption(a.Meta) with
        {
            Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f,
        });
        return Column(kids, Spacing.M);
    }

    /// <summary>W1: continue (the hero on listen-next's resume, the up-next minis) and new since you were here.</summary>
    static Element ReturningHead(ShowHeadArgs a)
    {
        var cont = new List<Element>(3)
        {
            SecHead(Pivot(Loc.Get(Strings.Podcast.Continue)),
                    Loc.Get(Strings.Podcast.UpNextFromProgress) + " · " + Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo),
                    stack: a.Narrow),
        };
        if (a.ResumeSlot > 0) cont.Add(Hero(new Episode(a.ResumeSlot), a));
        if (a.UpNext.Length > 0)
        {
            var episodes = new Episode[a.UpNext.Length];
            for (int i = 0; i < episodes.Length; i++) episodes[i] = new Episode(a.UpNext[i]);
            var lead = MiniLeadOf(episodes);
            var minis = new List<Element>(episodes.Length);
            for (int i = 0; i < episodes.Length; i++) minis.Add(Mini(episodes[i], lead, a));
            cont.Add(EqualRows(minis, a.Minis));
        }
        var sections = new List<Element>(2);
        // New since can be all there is to come back to (up next never repeats it): then there is no "continue" section.
        if (cont.Count > 1) sections.Add(Column(cont, Spacing.M));
        if (a.Fresh.Length > 0) sections.Add(NewSince(a));
        return Column(sections, SectionGap);
    }

    /// <summary>The lead for the up-next set (<see cref="ShowReaderRules.MiniLead"/>), from what is KNOWN of its
    /// episodes: one that has not loaded yet is neutral, so a cold head does not decide on missing data (the head rebuilds
    /// as each lands — its fold carries every episode's version — and the placeholders reserve the lead decided so far).</summary>
    static ShowReaderRules.MiniLeadKind MiniLeadOf(Episode[] episodes)
    {
        bool numbered = true, art = true;
        foreach (var e in episodes)
        {
            if (!e.IsValid || !e.Knows(EpisodeFields.Title)) continue;
            numbered &= e.Number > 0;
            art &= e.Knows(EpisodeFields.Image) && Controls.ArtUrl(e.ImageId) is { Length: > 0 };
        }
        return ShowReaderRules.MiniLead(numbered, art);
    }

    /// <summary>The continue hero, behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>): the card once the
    /// episode's identity is there, a card-height shimmer while it is coming, "unavailable · retry" when its ask failed —
    /// never a card with an empty title. The owner rebuilds the head when the episode's version moves (HeadFold), so the
    /// fixed handle here is enough.</summary>
    static Element Hero(Episode e, ShowHeadArgs a)
        => Episode.Reveal(e, () => HeroCard(e, a), s_heroSeed, () => HeroFailed(e));

    static readonly Func<Element> s_heroSeed = static () => new BoxEl { Height = HeroSeedHeight, Corners = Radii.CardAll };

    static Element HeroFailed(Episode e) => new BoxEl
    {
        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = Edges4.All(CardPad),
        Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = WinUiChoiceRules.ViewStroke, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(Loc.Get(Strings.Podcast.Reader.Unavailable)) { Size = QuietSize, LineHeight = FailedLineHeight, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Grow = 1f, Basis = 0f },
            Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)),
        ],
    };

    /// <summary>The continue hero's card: 96 art · the title · "17 min" Display 30 in the tone and Classic's <see cref="VoiceWeight"/>, then "left of 31 min"
    /// · a 4-DIP track ≤ 420 · Resume. The card opens the episode (the row rule); the pill resumes it.</summary>
    static Element HeroCard(Episode e, ShowHeadArgs a)
    {
        Func<ColorF> tone = s_accent;
        bool narrow = a.Narrow;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, e.Number) : "";
        string line = Strings.Podcast.LeftOf(TemplateMarkText, Episode.DurationLabel(e.DurationMs));
        int at = line.IndexOf(TemplateMark);
        // "17 min left of 31 min": the minutes follow the PLAYBACK clock like a list row's (Episode.PlaybackProgressOf —
        // the playing episode reads the player's position), through ONE reused span buffer; a fire that says the same
        // thing re-shapes nothing. A template with no mark has nothing to count down.
        string before = at > 0 ? line[..at] : "", after = at >= 0 && at + 1 < line.Length ? line[(at + 1)..] : "";
        var buffer = new SpanBuffer();
        Prop<TextSpans> spans = at < 0
            ? (TextSpans)new[] { new TextSpan(line) }
            : Prop.Of(() =>
            {
                buffer.Clear();
                if (before.Length > 0) buffer.Add(new TextSpan(before, Weight: 400, Color: Tok.TextSecondary, Size: HeroLeftSize));
                buffer.Add(new TextSpan(s_leftWords.Get(Episode.PlaybackProgressOf(e).LeftMinutes, s_leftWordsFormat)));
                if (after.Length > 0) buffer.Add(new TextSpan(after, Weight: 400, Color: Tok.TextSecondary, Size: HeroLeftSize));
                return buffer.Current;
            });
        var copy = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                new TextEl(title)
                {
                    Size = HeroTitleSize, LineHeight = HeroTitleLine, Weight = 600, Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.Wrap,
                    Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new SpanTextEl(spans)
                {
                    FontFamily = DisplayFace, Size = HeroLeftDisplay, LineHeight = HeroLeftDisplayLine, Weight = VoiceWeight,
                    Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
                new BoxEl { Direction = 1, MaxWidth = HeroTrackMax, MinWidth = 0f, Children = [HeroRule(e, tone)] },
            ],
        };
        float edge = narrow ? HeroArtNarrow : HeroArt;
        var cover = new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(ArtCorner), ClipToBounds = true,
            Children = [Controls.Artwork(e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null, edge, edge, ArtCorner)],
        };
        Element pill = new BoxEl
        {
            Shrink = 0f,
            Children = [Button.Create(Loc.Get(Strings.Podcast.Resume), () => a.Play(e), ButtonAppearance.Accent, glyph: Icons.Play)],
        };
        Element body = narrow
            ? new BoxEl
            {
                Direction = 1, Gap = Spacing.M, MinWidth = 0f,
                Children = [new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [cover, copy] }, pill],
            }
            : new BoxEl { Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [cover, copy, pill] };
        return WithMenu(a, e, new BoxEl
        {
            Direction = 1, MinWidth = 0f, Padding = Edges4.All(CardPad), Corners = Radii.CardAll, Fill = Tok.FillCardDefault,
            HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillCardDefault, BrushTransitionMs = Design.Motion.Faster,
            BorderWidth = WinUiChoiceRules.ViewStroke, BorderColor = Tok.StrokeCardDefault,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = () => Episode.OpenPage(e),
            Children = [body],
        });
    }

    /// <summary>The hero's 4-DIP track in the tone: a fill whose compositor scale follows the PLAYBACK clock
    /// (<see cref="Episode.PlaybackProgressOf"/>), like a list row's rule — the persisted progress it used to be built from
    /// stood still while the episode played.</summary>
    static Element HeroRule(Episode e, Func<ColorF> tone) => new BoxEl
    {
        Direction = 0, Height = HeroRuleHeight, Corners = CornerRadius4.All(RuleCorner), Fill = Tok.FillSubtleTertiary,
        ClipToBounds = true, Shrink = 0f,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = Prop.Of(tone), TransformOriginX = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, Episode.PlaybackProgressOf(e).Pct), 1f)),
            },
        ],
    };

    static readonly FormatCache<int> s_leftWords = new();
    static readonly Func<int, string> s_leftWordsFormat = static minutes => Episode.DurationWords(minutes);

    /// <summary>An up-next mini card behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>): the card once
    /// the episode's identity is there, a chip-shaped shimmer while it is coming, "unavailable · retry" when its ask
    /// failed — an unresolved chip is never an empty plate. All three reserve the SAME <paramref name="lead"/>
    /// (<see cref="ShowReaderRules.MiniLead"/>), so the reveal cross-dissolves in place.</summary>
    static Element Mini(Episode e, ShowReaderRules.MiniLeadKind lead, ShowHeadArgs a)
        => Episode.Reveal(e, () => MiniCard(e, lead, a), () => MiniPlate(lead, "", null, s_miniSeedTitle, s_miniSeedMeta, trailing: null, onClick: null),
                          () => MiniFailed(e, lead));

    /// <summary>U+2007 FIGURE SPACE runs: the derived shimmer draws a bar only for a run that MEASURES (the row's own
    /// seed idiom, Episode.UI.cs).</summary>
    static readonly string s_miniSeedTitle = new((char)0x2007, MiniSeedTitleRun), s_miniSeedMeta = new((char)0x2007, MiniSeedMetaRun);

    static Element MiniFailed(Episode e, ShowReaderRules.MiniLeadKind lead)
        => MiniPlate(lead, "", null, Loc.Get(Strings.Podcast.Reader.Unavailable), "",
                     trailing: Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)), onClick: null);

    /// <summary>The up-next mini card: its lead (the numeral 22/300, or the 40 × 40 cover) · the title 13/600 on one line ·
    /// "28 min · Sep 15" (or "N min left" when started). Without a numeral lead the number rides the meta line
    /// ("#35 · 32 min · Sep 27"), like a list row's. Opens the episode; right-click is the episode menu.</summary>
    static Element MiniCard(Episode e, ShowReaderRules.MiniLeadKind lead, ShowHeadArgs a)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        bool started = Episode.Rules.InProgress(Episode.ReaderPctOf(e));
        string length = started ? Episode.LeftLabel(e.ProgressMs, e.DurationMs)
                      : e.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(e.DurationMs) : "";
        string meta = Joined(length, e.Knows(EpisodeFields.Published) ? Episode.DateLabel(e.PublishedAt) : "");
        if (lead != ShowReaderRules.MiniLeadKind.Numeral && n > 0) meta = Joined("#" + FormatCache.Int(n), meta);
        string? art = e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;
        return MiniPlate(lead, n > 0 ? FormatCache.Int(n) : "", art, title, meta, trailing: null, onClick: () => Episode.OpenPage(e), a, e);
    }

    /// <summary>The mini's ONE plate — the card, its seed twin and its failure arm are the same geometry, so the reveal
    /// cross-dissolves in place. A plate with no <paramref name="onClick"/> is not a link. A (B) surface (lead · title ·
    /// meta): it takes only the shared rules, the hand and the tab stop of the click owner it is, and a tooltip on the
    /// title only while that is cut. The lead column exists only when <paramref name="lead"/> says so — never a blank
    /// reserved gutter.</summary>
    static Element MiniPlate(ShowReaderRules.MiniLeadKind lead, string numeral, string? artUrl, string title, string meta,
                             Element? trailing, Action? onClick, ShowHeadArgs? head = null, Episode episode = default)
    {
        var titleText = R.Type.DenseTitle(title) with
        {
            Color = onClick is null ? Tok.TextSecondary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(3);
        if (lead == ShowReaderRules.MiniLeadKind.Numeral)
            kids.Add(new BoxEl
            {
                Width = MiniNumeral, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                Children = [new TextEl(numeral) { FontFamily = DisplayFace, Size = MiniNumeralSize, LineHeight = MiniNumeralLine, Weight = VoiceWeight, Color = Tok.TextTertiary, MaxLines = 1 }],
            });
        else if (lead == ShowReaderRules.MiniLeadKind.Art)
            kids.Add(new BoxEl
            {
                Width = MiniArt, Height = MiniArt, Shrink = 0f, Corners = CornerRadius4.All(ArtCorner), ClipToBounds = true,
                Children = [Controls.Artwork(artUrl, MiniArt, MiniArt, ArtCorner)],
            });
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                // Only a live card tips (the seed and the failure arm are not links, and their title is no name).
                onClick is null ? titleText : Controls.TrimmedTitle(titleText, title),
                Caption(meta) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        });
        if (trailing is not null) kids.Add(trailing.Value);
        var plate = new BoxEl
        {
            Direction = 0, Gap = MiniGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Padding = new Edges4(MiniPadX, MiniPadY, MiniPadX, MiniPadY),
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = WinUiChoiceRules.ViewStroke, BorderColor = Tok.StrokeCardDefault,
            Children = kids.ToArray(),
        };
        if (onClick is null) return plate;
        // The hand and the tab stop are the surface rule's; the role stays Hyperlink (the card navigates to the episode).
        var mode = SurfaceRules.Ownership(inSlot: false, hasClick: true);
        var link = plate with
        {
            HoverFill = Tok.FillCardSecondary, Role = AutomationRole.Hyperlink, Focusable = mode.OwnsFocus,
            Cursor = SurfaceRules.Cursor(in mode), OnClick = onClick,
        };
        return head is null ? link : WithMenu(head, episode, link);
    }

    /// <summary>"New since you were here  2" · Mark all played — then the fresh episodes as reader rows (the SAME
    /// template over a fixed scope, so they are the list's pixels).</summary>
    static Element NewSince(ShowHeadArgs a)
    {
        Element link = HyperlinkButton.Create(Loc.Get(Strings.Podcast.MarkAllPlayed), a.MarkFreshPlayed, size: ControlSize.Small);
        var kids = new List<Element>(a.Fresh.Length + 1)
        {
            new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M),
                Children = [SecHead(Pivot(Loc.Get(Strings.Podcast.NewSince), FormatCache.Int(a.Fresh.Length)), null, link)],
            },
        };
        for (int i = 0; i < a.Fresh.Length; i++)
            kids.Add(Episode.ReaderRow(a.EpisodeStyle, Episode.Fixed(Episode.RowItem.Of(new Episode(a.Fresh[i]),
                Episode.RowMarks.Fresh | (i == 0 ? Episode.RowMarks.NoRule : Episode.RowMarks.None))), a.RowCtx, a.Narrow));
        return Column(kids, 0f);
    }

    /// <summary>"You're all caught up" and, when the cadence names one, the day new episodes usually land.</summary>
    static Element CaughtUpHead(ShowHeadArgs a)
    {
        var kids = new List<Element>(2) { Pivot(Loc.Get(Strings.Podcast.CaughtUp)) };
        if (a.Day is { } day)
            kids.Add(Caption(Strings.Podcast.CaughtUpNext(s_dayWords[(int)day])) with
            {
                Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f,
            });
        return Column(kids, Spacing.XS);
    }
}
