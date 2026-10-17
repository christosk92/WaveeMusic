// ── Themes/Classic/Structure/Lists/ClassicEpisodeRow.cs — Classic's episode row (themes plan 6.1, T8c) ───────────────────────────
//
// From `Entities/Episode.UI.cs` ("4. THE ROW"): the reader row of a podcast episode (W3) as the structure draws it: the art, the
// title line (the equalizer before the playing row's title, the title in the tone), the two-line blurb, the meta line (the "#n", the
// badges, "new", "23 min left", the date and the length, "played", the transcript mark), the 3-DIP progress rule, the cluster (the
// "..." menu and the 36 tone disc), the now-playing tone bar, the hover and pressed fills and, hosted in a selection surface, the
// check lane. `StyleKeys.EpisodeRow` carries the three pieces the core row composes: the CONTENT (a bound slot's tree), the SEED (the
// cold skeleton's twin, figure-space bars in the real geometry) and the FAILURE row (the unavailable copy and Retry).
//
// ONE ROW, BOUND. The content is built ONCE per slot (the list's template, re-run only when the slot's kind or the reader's width arm
// flips), and every per-episode value is a bind over the slot's equality-gated `RowItem` (the handle, its row version and the host's
// marks), so a recycle re-binds and allocates nothing. The visit head's handful of eager rows reuse the SAME template over a fixed
// scope (`Episode.Fixed`), so they are the list's pixels.
//
// WHAT THE CORE KEEPS. `Episode.ReaderRow` (the reveal gate: pending -> the seed, failed -> the failure row, ready -> the content;
// `SkelRegionEl` over `ReaderLoadState`), the row's own decisions (`PlayedOf`, `InProgressOf`, `LeftOf`, `IsNew`, `ShownTitle`, the
// completion rule `ReaderPct`: a completed episode reads 1, so "played", the rule and the host's filter and ledger never disagree),
// the verbs (`RowContext`: the disc's play, the row's open, the menu, the selection seam) and the host that builds the context. The
// template draws and never names `Playback`, `Entities` or `Store` (WVTH002); numbers are `Rules\ClassicEpisodeRowLayout.cs` (WVTH003).
//
// STATES (W3): REST (art 64 r6, title 14/600 <= 2 + chips, blurb <= 2, "#n", date, length), HOVER (the whole row FillSubtleSecondary,
// the cluster fades in over 120 ms), IN PROGRESS ("23 min left" 12/600 in the tone and the rule), PLAYED (art and copy at 85 %,
// "played"), NOW PLAYING (the tone bar, the title in the tone, the equalizer), NEW ("new" before the date), ROW = LINK (the whole row
// is a Hyperlink tab stop that opens the episode page; the disc plays), NARROW (art 48, the cluster is the disc alone).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

using RowContext = global::Wavee.Episode.RowContext;
using RowItem = global::Wavee.Episode.RowItem;
using RowMarks = global::Wavee.Episode.RowMarks;

/// <summary>Classic's style for the episode row.</summary>
internal static class ClassicEpisodeRow
{
    /// <summary>The style Classic registers under <c>StyleKeys.EpisodeRow</c>. Stateless: every host and a test share it.</summary>
    public static readonly EpisodeRowStyle Style = new(Content, Seed, Failure);

    static readonly ContextMenuOptions s_menuOptions = new();

    /// <summary>Segoe Fluent's Lock (U+E72E) — not in the engine's glyph table; built from its code point so the source stays ASCII
    /// (the <c>WaveeIcons</c> convention).</summary>
    static readonly string LockGlyph = ((char)0xE72E).ToString();

    /// <summary>U+2007 FIGURE SPACE runs: a derived shimmer draws a bar only for a run that MEASURES.</summary>
    static readonly string SeedTitle = new((char)0x2007, ClassicEpisodeRowLayout.SeedTitleRun);
    static readonly string SeedDescription = new((char)0x2007, ClassicEpisodeRowLayout.SeedDescriptionRun);

    // The three templates the core row composes.

    static Element Content(in BoundItemScope<RowItem> item, RowContext ctx, bool narrow)
        => ReaderRowContent(in item, ctx, narrow, seed: false);

    /// <summary>The cold skeleton's row: figure-space title and blurb bars, "Jan 1 · 3 min", the art tile — the real row's
    /// geometry, no cluster (hidden at rest), no rule (the seed invents no progress).</summary>
    static Element Seed(bool narrow)
    {
        var item = Episode.Fixed(default);
        return ReaderRowContent(in item, Episode.SeedContext, narrow, seed: true);
    }

    static Element Failure(Episode episode, bool narrow)
    {
        var item = Episode.Fixed(RowItem.Of(episode));
        return RowGrid(Art(in item, narrow ? Episode.RowArtNarrow : Episode.RowArt),
            new BoxEl { Direction = 1, Grow = 1, Basis = 0, MinWidth = 0, Gap = Spacing.S,
                Children = [R.Type.DenseMeta(Loc.Get(Strings.Podcast.Reader.Unavailable)) with
                {
                    Wrap = TextWrap.Wrap, Color = Tok.TextSecondary,
                }] },
            Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(episode)));
    }


    static BoxEl RowGrid(Element art, Element copy, Element actions) => new()
    {
        Direction = 0, Gap = Episode.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0,
        Padding = new Edges4(Episode.RowPadX + Episode.ToneBarWidth + 8f, Episode.RowPadY, Episode.RowPadX, Episode.RowPadY), Children = [art, copy, actions],
    };

    static Element ReaderRowContent(in BoundItemScope<RowItem> item, RowContext ctx, bool narrow, bool seed)
    {
        IReadSignal<RowItem> it = item.Item;
        Func<ColorF> tone = ctx.Tone;
        Prop<ColorF> toneInk = Prop.Of(tone);
        Action<Episode> open = ctx.Open ?? Episode.OpenPageAction;

        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ClassicEpisodeRowLayout.CopyGap,
            Opacity = item.Opacity(static r => Episode.PlayedOf(r.Episode) ? Episode.PlayedInk : 1f),
            Children = [TitleLine(in item, it, tone, seed), Description(in item, seed), MetaLine(in item, toneInk, seed), Rule(in item, toneInk)],
        };
        var grid = RowGrid(Art(in item, narrow ? Episode.RowArtNarrow : Episode.RowArt), copy, Cluster(in item, it, ctx, narrow, seed));
        var root = new BoxEl
        {
            ZStack = true, MinWidth = 0f, Corners = CornerRadius4.All(Episode.RowRadius),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            OnClick = item.Invoke(r => open(r.Episode)),
            Children =
            [
                // The top hairline, dropped under a month header (the prototype's `.group + .erow`).
                new BoxEl
                {
                    Height = 1f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Stretch, HitTestVisible = false,
                    Fill = Tok.StrokeDividerDefault, Visible = item.Show(static r => (r.Marks & RowMarks.NoRule) == 0),
                },
                // NOW PLAYING: the 3-DIP tone bar on the row's left edge, inset by the row's own padding.
                new BoxEl
                {
                    Width = Episode.ToneBarWidth, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(0f, Episode.RowPadY, 0f, Episode.RowPadY), Corners = CornerRadius4.All(2f), Fill = toneInk,
                    HitTestVisible = false, Visible = Prop.Of(() => Episode.IsNowPlaying(it.Value.Episode)),
                },
                grid,
            ],
        };
        if (ctx.Selection is { } selection)
        {
            var scope = item.Row;
            var interact = scope.OnInteraction;
            root = root with
            {
                Role = AutomationRole.Button, Focusable = false, OnClick = null,
                Fill = Prop.Of(() => scope.IsSelected() ? R.Layer.RowHover : ColorF.Transparent),
                OnFocusChanged = scope.OnFocusChanged,
                OnPointerReleased = args =>
                {
                    if (selection.Selecting.Peek() || (args.Mods & (KeyModifiers.Ctrl | KeyModifiers.Shift)) != 0)
                        interact(ItemContainerTrigger.Tap, SelectorVisualsBound.MultiSelectMods(selection.Selecting.Peek(), args.Mods));
                    else open(it.Peek().Episode);
                },
                OnKeyDown = args =>
                {
                    if (args.KeyCode == Keys.Enter)
                    {
                        if (selection.Selecting.Peek()) interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(true, args.Mods));
                        else open(it.Peek().Episode);
                        args.Handled = true;
                    }
                    else if (args.KeyCode == Keys.Space && !args.IsRepeat)
                    { selection.Selecting.Value = true; interact(ItemContainerTrigger.SpaceKey, SelectorVisualsBound.MultiSelectMods(true, args.Mods)); args.Handled = true; }
                    else if (args.KeyCode == Keys.Escape) { selection.Clear(); args.Handled = true; }
                },
                Children = [new BoxEl { Direction = 0, MinWidth = 0, Children =
                [SelectorVisualsBound.BoundCheckLane(() => selection.Selecting.Value, scope.IsSelected, interact, 4f),
                 new BoxEl { ZStack = true, Grow = 1, Basis = 0, MinWidth = 0, Children = root.Children }] }],
            };
        }
        if (ctx.Overlay is { } overlay && ctx.Menu is { } menu)
            root = ContextSheets.Attach(root, overlay, () => menu(it.Peek().Episode), s_menuOptions);
        return root;
    }

    static Element Art(in BoundItemScope<RowItem> item, float edge) => new BoxEl
    {
        Width = edge, Height = edge, Shrink = 0f, Corners = CornerRadius4.All(ClassicEpisodeRowLayout.ArtRadius), ClipToBounds = true,
        Opacity = item.Opacity(static r => Episode.PlayedOf(r.Episode) ? Episode.PlayedInk : 1f),
        Children =
        [
            new ImageEl
            {
                Source = item.Image(static r => Episode.ArtOf(r.Episode)), Width = edge, Height = edge, Fit = ImageFit.Cover,
                DecodePx = edge, Corners = CornerRadius4.All(ClassicEpisodeRowLayout.ArtRadius),
                Placeholder = item.Color(static r => Design.PlaceholderFor(Episode.ArtOf(r.Episode))),
            },
        ],
    };

    /// <summary>The equalizer (now playing) · the title (tone while playing) · the badge chips, on one centred line.</summary>
    static Element TitleLine(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, Func<ColorF> tone, bool seed = false) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
        Children =
        [
            seed ? new BoxEl() : item.ShowWhen(static r => Episode.IsNowPlaying(r.Episode), () => Controls.Equalizer(Track.DeckPlaying, tone, ClassicEpisodeRowLayout.EqualizerHeight)),
            new TextEl(seed ? SeedTitle : item.Text(static r => Episode.ShownTitle(r.Episode)))
            {
                Size = ClassicEpisodeRowLayout.TitleSize, LineHeight = ClassicEpisodeRowLayout.TitleLine, Weight = 600, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
                MinWidth = 0f, Grow = 1f, Basis = 0f, Shrink = 1f,
                Color = Prop.Of(() => Episode.IsNowPlaying(it.Value.Episode) ? tone() : Tok.TextPrimary),
            },
        ],
    };

    static Element Description(in BoundItemScope<RowItem> item, bool seed = false)
    {
        var body = seed ? SeedDescription : item.Text(static r => Episode.DescriptionOf(r.Episode));
        return new TextEl(body)
        {
            Size = R.Type.DenseMeta("").Size, LineHeight = R.Type.DenseMeta("").LineHeight,
            Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Visible = seed ? true : item.Show(static r => Episode.HasDescription(r.Episode)),
        };
    }

    /// <summary>NEW · "23 min left" (in progress) · date · length (not in progress) · ✓ played · the transcript mark.</summary>
    static Element MetaLine(in BoundItemScope<RowItem> item, Prop<ColorF> toneInk, bool seed = false) => new BoxEl
    {
        Direction = 0, Wrap = true, Gap = ClassicEpisodeRowLayout.MetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Margin = new Edges4(0f, 2f, 0f, 0f),
        Children =
        [
            MetaText(item.Text(static r => Episode.NumeralOf(r) is { Length: > 0 } n ? "#" + n : ""))
                with { Visible = item.Show(static r => Episode.NumeralOf(r).Length > 0) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Bonus), tone: true)
                with { Visible = item.Show(static r => Episode.KindOf(r.Episode) == EpisodeKind.Bonus) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Trailer), tone: true)
                with { Visible = item.Show(static r => Episode.KindOf(r.Episode) == EpisodeKind.Trailer) },
            Controls.Chip(Episode.ExplicitMark)
                with { Visible = item.Show(static r => (Episode.FlagsOf(r.Episode) & EpisodeFlags.Explicit) != 0) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Video), Icons.Movie)
                with { Visible = item.Show(static r => (Episode.FlagsOf(r.Episode) & EpisodeFlags.Video) != 0) },
            Controls.Chip(Loc.Get(Strings.Podcast.Badge.Subscribers), LockGlyph)
                with { Visible = item.Show(static r => (Episode.FlagsOf(r.Episode) & EpisodeFlags.Paywalled) != 0) },
            R.Type.MicroMeta(Loc.Get(Strings.Podcast.New)) with
            {
                Weight = 700, CharSpacing = 60f, Color = toneInk, MaxLines = 1,
                Visible = item.Show(static r => Episode.IsNew(in r)),
            },
            new TextEl(item.Text(static r => Episode.LeftOf(r.Episode)))
            {
                Size = Ui.Caption("").Size, LineHeight = Ui.Caption("").LineHeight,
                Weight = 600, Color = toneInk, MaxLines = 1, Wrap = TextWrap.NoWrap,
                Visible = item.Show(static r => Episode.InProgressOf(r.Episode)),
            },
            MetaText(seed ? Episode.s_dates.Get(Episode.SeedDateKey, Episode.s_dateFormat) : item.Text(static r => Episode.DateKeyOf(r.Episode), Episode.s_dates, Episode.s_dateFormat))
                with { Visible = seed ? true : item.Show(static r => DateKeys.IsDayKey(Episode.DateKeyOf(r.Episode))) },
            MetaText(seed ? Episode.DurationLabel(Episode.SeedDurationMs) : item.Text(static r => Episode.DurationLabelOf(r.Episode))) with { Visible = seed ? true : item.Show(static r => !Episode.InProgressOf(r.Episode) && r.Episode.IsValid && r.Episode.Knows(EpisodeFields.Duration) && r.Episode.DurationMs > 0) },
            new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Visible = item.Show(static r => Episode.PlayedOf(r.Episode)),
                Children = [Icon(Icons.Check, 12f) with { Color = toneInk }, MetaText(Loc.Get(Strings.Podcast.Played))],
            },
            Icon(Icons.Document, 12f) with
            {
                Color = Tok.TextTertiary, Visible = item.Show(static r => (Episode.FlagsOf(r.Episode) & EpisodeFlags.HasTranscript) != 0),
            },
        ],
    };

    /// <summary>The 3-DIP tone rule (≤ 340) under an in-progress row's meta; the fill is a compositor scale from the left.</summary>
    static Element Rule(in BoundItemScope<RowItem> item, Prop<ColorF> toneInk) => new BoxEl
    {
        Direction = 0, Height = Episode.RuleHeight, MaxWidth = Episode.RuleMaxWidth, Corners = CornerRadius4.All(ClassicEpisodeRowLayout.RuleCorner),
        Fill = Tok.FillSubtleTertiary, ClipToBounds = true, Shrink = 0f, Margin = new Edges4(0f, 4f, 0f, 0f),
        Visible = item.Show(static r => Episode.InProgressOf(r.Episode)),
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = toneInk, TransformOriginX = 0f,
                Transform = item.Value(static r => Affine2D.Scale(MathF.Max(0.001f, Episode.PlaybackProgressOf(r.Episode).Pct), 1f)),
            },
        ],
    };

    /// <summary>Queue · mark · ♥ (Your Episodes) · ⋯ (the row's menu) + the disc, fading in
    /// with the row's hover; narrow: the disc alone, always visible.</summary>
    static Element Cluster(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, RowContext ctx, bool narrow, bool seed = false)
    {
        var focused = new Signal<bool>(false);
        Action<bool> focus = value => focused.Value = value;
        Element disc = Disc(in item, it, ctx, focus, seed);
        return new BoxEl
        {
            Direction = (byte)(narrow ? 1 : 0), Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                // ⋯ raises the row's own context request — the ONE menu the row attached above.
                seed ? ActionCell(Icon(Icons.More, 16f, Tok.TextSecondary)) : ToolTip.Wrap(ActionCell(Icon(Icons.More, 16f, Tok.TextSecondary)) with { ClickRequestsContext = true, OnFocusChanged = focus },
                             Loc.Get(Strings.Common.More)),
                disc,
            ],
        };
    }

    static BoxEl ActionCell(Element glyph) => new()
    {
        Width = Episode.ActionBox, Height = Episode.ActionBox, Shrink = 0f, Corners = CornerRadius4.All(5f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true,
        Children = [glyph],
    };

    /// <summary>The 36 tone disc: play, or pause on the playing row (<c>Episode.Invoke</c> through the host's verb).</summary>
    static Element Disc(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, RowContext ctx, Action<bool>? focus = null, bool seed = false)
    {
        Func<ColorF> tone = ctx.Tone;
        Action<Episode> play = ctx.Play;
        var disc = new BoxEl
        {
            OnFocusChanged = focus,
            Width = Episode.RowDisc, Height = Episode.RowDisc, Shrink = 0f, Corners = Radii.Circle(Episode.RowDisc), Fill = Prop.Of(tone),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true,
            OnClick = item.Invoke(r => play(r.Episode)),
            Children =
            [
                Icon(Icons.Play, Episode.RowDiscGlyph) with
                {
                    Text = Prop.Of(() => Episode.IsNowPlaying(it.Value.Episode) && Track.DeckPlaying.Value ? Icons.Pause : Icons.Play),
                    Color = Prop.Of(() => ColorContrast.PickContrast(tone())),
                },
            ],
        };
        return seed ? disc : Controls.Named(disc, Loc.Get(Strings.Detail.Play));
    }

    static TextEl MetaText(Prop<string> text) => new(text)
    {
        Size = Ui.Caption("").Size, LineHeight = Ui.Caption("").LineHeight,
        Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
    };
}
