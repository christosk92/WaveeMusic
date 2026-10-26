// ── Themes/Zune/Structure/Views/Show/ZuneShowHead.cs — Zune's show visit head (themes plan 6.3; P5 Z6b2) ───────────────────────────────
//
// Board Z-Show's two-up block: "continue" (a 72 cover, the accent word, the episode's title, "6 min left · #808") beside "up next from your
// progress" (the next episode), flat on the page's ground. The head is the first screen the visitor needs: New → start-here doors + the full
// about; Returning → the continue block, up next, new since you were here; CaughtUp → one line with the usual release day; Unavailable → the
// hydrate failed and nothing local says otherwise. Until the membership answered AND progress settled it is a hero-height shimmer (never flash
// New at a Returning listener), revealed with a plain fade.
//
// WHAT THE CORE KEEPS (`Show.VisitHead`): the stamp memo that re-renders the head (visit, width arm, a fold of every episode it shows, the tone),
// the results-mode hide, the reveal region's pending read, the snapshot, and every verb (play, trailer, mark all played). The head's episodes
// arrive as slots; the reveal gate of each card is the core's (`Episode.Reveal`: the card once the identity is there, a shimmer while it is
// coming, "unavailable · retry" when its ask failed — never a card with an empty title).
//
// The cards are Zune's: no plate, no border, no tone gradient — a cover, words, and a 40 ring that plays. Words the theme writes are lowered by
// the case roles; an episode's title is never re-cased.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using static Wavee.Themes.Zune.ZuneShowRules;
using K = Wavee.Themes.Zune.ZuneDetailKit;

namespace Wavee.Themes.Zune;

internal static partial class ZuneShow
{
    /// <summary>The ICU select keys <c>podcast.caughtUpNext</c> takes (lowercase English weekday).</summary>
    static readonly string[] s_dayWords = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>The head's padded shell around its reveal region: a hero-height shimmer until the host's pending read clears.</summary>
    static Element HeadShell(Func<bool> pending, Func<Element> content) => new BoxEl
    {
        Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, HeadTop, 0f, SectionGap),
        Children =
        [
            new SkelRegionEl(Pending: pending, Failed: s_never, Content: content, ShimmerSource: s_headSeed,
                OnFailed: null, Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false),
        ],
    };

    static readonly Func<Element> s_headSeed = static () => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, MinWidth = 0f,
        Children =
        [
            new BoxEl { Width = HeadSeedTitleWidth, Height = ShowReaderRules.HeaderLine, Corners = CornerRadius4.All(SkelRadius) },
            new BoxEl { Height = HeroSeedHeight, Corners = R.Shape.All(ShapeRole.Card) },
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

    // ══ THE CARD: a cover, words, a ring ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The head's episode menu — the SAME one a list row attaches (the head context's <c>Menu</c>, built at OPEN time), for a head surface
    /// that owns its own right-click (a door, the continue card, an up-next card). Null without a menu seam.</summary>
    static Func<ContextMenuModel?>? MenuOf(ShowHeadArgs a, Episode e)
        => a.RowCtx is { Menu: { } menu } ? () => menu(e) : null;

    /// <summary><paramref name="el"/> with the head's episode menu attached to it (a right-click anywhere on the card).</summary>
    static Element WithMenu(ShowHeadArgs a, Episode e, BoxEl el)
        => MenuOf(a, e) is { } menu && a.RowCtx.Overlay is { } overlay ? ContextSheets.Attach(el, overlay, menu) : el;

    static string? DescriptionOf(Episode e) => Episode.HasDescription(e) ? Episode.DescriptionOf(e) : null;

    static string? ArtOf(Episode e) => e.Knows(EpisodeFields.Image) ? Controls.ArtUrl(e.ImageId) : null;

    /// <summary>The 40 ring that plays a card's episode: an outline disc, the glyph reading Pause while THAT episode is the one playing (a
    /// subscribing read, coarse-first: the glyph node re-skins, the card does not re-render). The accent disc is the continue card's.</summary>
    static Element PlayRing(Action play, string? playUri, bool filled)
    {
        EntityId playId = playUri is { Length: > 0 } ? EntityId.Parse(playUri) : default;   // parsed once, at build
        return Controls.Named(new BoxEl
        {
            Width = CardRing, Height = CardRing, Shrink = 0f, Corners = CornerRadius4.All(CardRing / 2f),
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Fill = filled ? Prop.Of(s_accent) : ColorF.Transparent,
            BorderWidth = filled ? 0f : ZuneDetailRules.RingStroke, BorderColor = Tok.TextPrimary,
            HoverFill = filled ? ColorF.Transparent : R.Layer.ChromeHover, PressedFill = filled ? ColorF.Transparent : R.Layer.ChromePressed,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
            BlocksDragArm = true, OnClick = play,
            Children =
            [
                Icon(Icons.Play, CardRingGlyph) with
                {
                    Text = Prop.Of(() => PlayRelation.ShowsPause(playId) ? Icons.Pause : Icons.Play),
                    Color = filled ? Prop.Of(() => ColorContrast.PickContrast(R.Ink.AccentAction)) : (Prop<ColorF>)Tok.TextPrimary,
                },
            ],
        }, Loc.Get(Strings.Detail.Play));
    }

    /// <summary>The card's cover: square at the Cover shape, the episode's art (a quiet plate while it has none).</summary>
    static BoxEl CardArt(string? url) => new()
    {
        Width = CardArt72, Height = CardArt72, Shrink = 0f, Corners = R.Shape.All(ShapeRole.Cover), ClipToBounds = true,
        Children = [Controls.Artwork(url, CardArt72, CardArt72, R.Shape.For(ShapeRole.Cover))],
    };

    /// <summary>The card's words: the small label (the accent where the card leads), the title on one line, then the quiet lines.</summary>
    static BoxEl CardWords(string label, bool lead, string title, string? why, Prop<string>? meta, string? metaText, bool live)
    {
        var kids = new List<Element>(4)
        {
            R.Type.MicroMeta(label) with
            {
                Size = CardLabelSize, LineHeight = CardLabelLine, Color = lead ? Prop.Of(s_accent) : Tok.TextSecondary,
                Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
            live ? Controls.TrimmedTitle(CardTitle(title, true), title) : CardTitle(title, false),
        };
        if (why is { Length: > 0 })
            kids.Add(R.Type.TrackMeta(why) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        if (meta is { } bound)
            kids.Add(R.Type.TrackMeta("") with { Text = bound, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        else if (metaText is { Length: > 0 })
            kids.Add(R.Type.TrackMeta(metaText) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
        return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = CardTextGap, Justify = FlexJustify.Center, Children = kids.ToArray() };
    }

    static TextEl CardTitle(string title, bool live) => R.Type.TrackTitle(title) with
    {
        Color = live ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };

    /// <summary>One card: the cover, the words, the ring. The whole card opens the episode (the hand, the Hyperlink role and one tab stop are its);
    /// the ring plays; the right-click is the head's episode menu.</summary>
    static Element Card(ShowHeadArgs a, Episode e, string key, Element lead, BoxEl words, Element? trailing, Action open)
    {
        var kids = new List<Element>(3) { lead, words };
        if (trailing is not null) kids.Add(trailing.Value);
        var card = new BoxEl
        {
            Key = key, Direction = 0, Gap = CardGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = CardArt72,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = open,
            Children = kids.ToArray(),
        };
        return WithMenu(a, e, card);
    }

    // ══ NEW: THE DOORS AND THE ABOUT ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Start here: up to three doors — a serial leads with episode 1, anything else with the latest; the trailer joins when there is
    /// one — and the full about with the facts line.</summary>
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
        if (n > 0) meta = Joined(meta, "#" + FormatCache.Int(n));
        return Card(a, e, "door:" + e.Slot, CardArt(ArtOf(e)),
            CardWords(label, lead, title, DescriptionOf(e), null, meta, live: true),
            PlayRing(() => a.Play(e), e.Uri.Text, filled: false), () => Episode.OpenPage(e));
    }

    /// <summary>The trailer door: its own row (outside the membership), played alone — not as the show context.</summary>
    static Element TrailerDoor(Episode t, ShowHeadArgs a)
        => Card(a, t, "door:trailer", CardArt(ArtOf(t)),
            CardWords(Loc.Get(Strings.Podcast.Trailer), false, t.Title, DescriptionOf(t),
                      null, t.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(t.DurationMs) : null, live: true),
            PlayRing(() => a.PlayAlone(t), t.Uri.Text, filled: false), () => Episode.OpenPage(t));

    /// <summary>The about: the full description at a reading measure (links route in-app) and the facts line.</summary>
    static Element About(ShowHeadArgs a)
    {
        var kids = new List<Element>(3) { SecHead(Pivot(Loc.Get(Strings.Podcast.About))) };
        if (a.About is { } about)
            kids.Add(new BoxEl
            {
                Direction = 1, MaxWidth = AboutMeasure, MinWidth = 0f,
                Children = [Controls.RichTextFlex(about, AboutSize, Tok.TextSecondary, R.Ink.AccentDecor, 0, DetailVerbs.NavigateKey)],
            });
        kids.Add(R.Type.DenseMeta(a.Meta) with
        {
            Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f,
        });
        return Column(kids, Spacing.M);
    }

    // ══ RETURNING: CONTINUE, UP NEXT, NEW SINCE ══════════════════════════════════════════════════════════════════════════

    /// <summary>Continue (the card on listen-next's resume, the up-next cards) and new since you were here.</summary>
    static Element ReturningHead(ShowHeadArgs a)
    {
        var cont = new List<Element>(3)
        {
            SecHead(Pivot(Loc.Get(Strings.Podcast.Continue)),
                    Loc.Get(Strings.Podcast.UpNextFromProgress) + " · " + Strings.Podcast.Ledger(a.Played, a.InProgress, a.ToGo),
                    stack: a.Narrow),
        };
        var tiles = new List<Element>(4);
        if (a.ResumeSlot > 0) tiles.Add(Hero(new Episode(a.ResumeSlot), a));
        if (a.UpNext.Length > 0)
        {
            var episodes = new Episode[a.UpNext.Length];
            for (int i = 0; i < episodes.Length; i++) episodes[i] = new Episode(a.UpNext[i]);
            var lead = MiniLeadOf(episodes);
            for (int i = 0; i < episodes.Length; i++) tiles.Add(Mini(episodes[i], lead, a));
        }
        if (tiles.Count > 0) cont.Add(EqualRows(tiles, a.Minis));
        var sections = new List<Element>(2);
        // New since can be all there is to come back to (up next never repeats it): then there is no "continue" section.
        if (cont.Count > 1) sections.Add(Column(cont, Spacing.M));
        if (a.Fresh.Length > 0) sections.Add(NewSince(a));
        return Column(sections, SectionGap);
    }

    /// <summary>The lead for the up-next set (<see cref="ShowReaderRules.MiniLead"/>), from what is KNOWN of its episodes: one that has not loaded
    /// yet is neutral, so a cold head does not decide on missing data.</summary>
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

    /// <summary>The continue card, behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>): the card once the episode's identity is
    /// there, a card-height shimmer while it is coming, "unavailable · retry" when its ask failed — never a card with an empty title.</summary>
    static Element Hero(Episode e, ShowHeadArgs a)
        => Episode.Reveal(e, () => HeroCard(e, a), s_heroSeed, () => HeroFailed(e));

    static readonly Func<Element> s_heroSeed = static () => new BoxEl { Height = CardArt72, Corners = R.Shape.All(ShapeRole.Card) };

    static Element HeroFailed(Episode e) => new BoxEl
    {
        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = CardArt72,
        Children =
        [
            R.Type.TrackMeta(Loc.Get(Strings.Podcast.Reader.Unavailable)) with
            {
                Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f, Grow = 1f, Basis = 0f,
            },
            Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)),
        ],
    };

    static readonly FormatCache<int> s_leftWords = new();
    static readonly Func<int, string> s_leftWordsFormat = static minutes => Strings.Podcast.Left(Episode.DurationWords(minutes));

    /// <summary>The continue card: the cover, the accent "continue", the title, "6 min left · #808" following the PLAYBACK clock like a list row's
    /// (<see cref="Episode.PlaybackProgressOf"/>, through a cache keyed by the whole minutes), a 2-dip accent track, and the accent resume ring.</summary>
    static Element HeroCard(Episode e, ShowHeadArgs a)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        string tail = n > 0 ? "#" + FormatCache.Int(n) : "";
        var line = new FormatCache<int>();
        Func<int, string> compose = minutes => Joined(s_leftWords.Get(minutes, s_leftWordsFormat), tail);
        Prop<string> meta = Prop.Of(() => line.Get(Episode.PlaybackProgressOf(e).LeftMinutes, compose));
        var words = CardWords(Loc.Get(Strings.Podcast.Continue), true, title, null, meta, null, live: true);
        words = words with { Children = [.. words.Children, HeroRule(e)] };
        return Card(a, e, "hero:" + e.Slot, CardArt(ArtOf(e)), words, PlayRing(() => a.Play(e), e.Uri.Text, filled: true), () => Episode.OpenPage(e));
    }

    /// <summary>The 2-dip track in the accent: a fill whose compositor scale follows the PLAYBACK clock (<see cref="Episode.PlaybackProgressOf"/>),
    /// like a list row's rule.</summary>
    static Element HeroRule(Episode e) => new BoxEl
    {
        Direction = 0, Height = HeroRuleHeight, MaxWidth = HeroTrackMax, Fill = Tok.FillSubtleTertiary, ClipToBounds = true, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = Prop.Of(s_accent), TransformOriginX = 0f,
                Transform = Prop.Of(() => Affine2D.Scale(MathF.Max(0.001f, Episode.PlaybackProgressOf(e).Pct), 1f)),
            },
        ],
    };

    /// <summary>An up-next card behind the reader's ONE reveal gate (<see cref="Episode.Reveal"/>). All three arms reserve the SAME
    /// <paramref name="lead"/> (<see cref="ShowReaderRules.MiniLead"/>), so the reveal cross-dissolves in place.</summary>
    static Element Mini(Episode e, ShowReaderRules.MiniLeadKind lead, ShowHeadArgs a)
        => Episode.Reveal(e, () => MiniCard(e, lead, a), () => MiniPlate(lead, "", null, s_miniSeedTitle, s_miniSeedMeta, trailing: null, onClick: null),
                          () => MiniFailed(e, lead));

    /// <summary>U+2007 FIGURE SPACE runs: the derived shimmer draws a bar only for a run that MEASURES (the row's own seed idiom).</summary>
    static readonly string s_miniSeedTitle = new((char)0x2007, MiniSeedTitleRun), s_miniSeedMeta = new((char)0x2007, MiniSeedMetaRun);

    static Element MiniFailed(Episode e, ShowReaderRules.MiniLeadKind lead)
        => MiniPlate(lead, "", null, Loc.Get(Strings.Podcast.Reader.Unavailable), "",
                     trailing: Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(e)), onClick: null);

    /// <summary>The up-next card: its lead (the 22 light numeral, or the cover) · the title on one line · "28 min · Sep 15" (or "N min left" when
    /// started). Without a numeral lead the number rides the meta line ("#35 · 32 min · Sep 27"), like a list row's. Opens the episode.</summary>
    static Element MiniCard(Episode e, ShowReaderRules.MiniLeadKind lead, ShowHeadArgs a)
    {
        int n = e.Knows(EpisodeFields.Title) ? e.Number : 0;
        string title = e.Knows(EpisodeFields.Title) ? Episode.TitleSansNumber(e.Title, n) : "";
        bool started = Episode.Rules.InProgress(Episode.ReaderPctOf(e));
        string length = started ? Episode.LeftLabel(e.ProgressMs, e.DurationMs)
                      : e.Knows(EpisodeFields.Duration) ? Episode.DurationLabel(e.DurationMs) : "";
        string meta = Joined(length, e.Knows(EpisodeFields.Published) ? Episode.DateLabel(e.PublishedAt) : "");
        if (lead != ShowReaderRules.MiniLeadKind.Numeral && n > 0) meta = Joined("#" + FormatCache.Int(n), meta);
        return MiniPlate(lead, n > 0 ? FormatCache.Int(n) : "", ArtOf(e), title, meta, trailing: null, onClick: () => Episode.OpenPage(e), a, e);
    }

    /// <summary>The mini's ONE plate — the card, its seed twin and its failure arm are the same geometry, so the reveal cross-dissolves in place. A
    /// plate with no <paramref name="onClick"/> is not a link. The lead column exists only when <paramref name="lead"/> says so — never a blank
    /// reserved gutter.</summary>
    static Element MiniPlate(ShowReaderRules.MiniLeadKind lead, string numeral, string? artUrl, string title, string meta,
                             Element? trailing, Action? onClick, ShowHeadArgs? head = null, Episode episode = default)
    {
        bool live = onClick is not null;
        var titleText = R.Type.TrackTitle(title) with
        {
            Color = live ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        };
        var kids = new List<Element>(3);
        if (lead == ShowReaderRules.MiniLeadKind.Numeral)
            kids.Add(new BoxEl
            {
                Width = MiniNumeralW, Shrink = 0f, Direction = 0, Justify = FlexJustify.End,
                Children = [R.Type.Numeral(numeral) with { Color = Tok.TextTertiary, MaxLines = 1 }],
            });
        else if (lead == ShowReaderRules.MiniLeadKind.Art)
            kids.Add(CardArt(artUrl));
        kids.Add(new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = CardTextGap, Justify = FlexJustify.Center,
            Children =
            [
                // Only a live card tips (the seed and the failure arm are not links, and their title is no name).
                live ? Controls.TrimmedTitle(titleText, title) : titleText,
                R.Type.TrackMeta(meta) with { MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            ],
        });
        if (trailing is not null) kids.Add(trailing.Value);
        var plate = new BoxEl
        {
            Direction = 0, Gap = CardGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = CardArt72, Children = kids.ToArray(),
        };
        if (onClick is null) return plate;
        var link = plate with
        {
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, Role = AutomationRole.Hyperlink, Focusable = true,
            AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        };
        return head is null ? link : WithMenu(head, episode, link);
    }

    // ══ NEW SINCE, CAUGHT UP ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"new since you were here  2" · mark all played — then the fresh episodes as reader rows (the SAME template over a fixed scope, so
    /// they are the list's pixels).</summary>
    static Element NewSince(ShowHeadArgs a)
    {
        var link = K.Link("new-since:mark", Loc.Get(Strings.Podcast.MarkAllPlayed), a.MarkFreshPlayed);
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

    /// <summary>"you're all caught up" and, when the cadence names one, the day new episodes usually land.</summary>
    static Element CaughtUpHead(ShowHeadArgs a)
    {
        var kids = new List<Element>(2) { Pivot(Loc.Get(Strings.Podcast.CaughtUp)) };
        if (a.Day is { } day)
            kids.Add(R.Type.DenseMeta(Strings.Podcast.CaughtUpNext(s_dayWords[(int)day])) with
            {
                Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f,
            });
        return Column(kids, Spacing.XS);
    }
}
