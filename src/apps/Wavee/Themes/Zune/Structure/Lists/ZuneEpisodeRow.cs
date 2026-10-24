// ── Themes/Zune/Structure/Lists/ZuneEpisodeRow.cs — Zune's podcast episode row (themes plan 6.3; P5 Z3) ────────────────────────────
//
// The show reader's row as Zune draws it (Z-Show, Z-Home-Podcasts: no dedicated row board, so built from Zune's parts, owner Q18): the cover
// at the Cover radius (2), the title in the TrackTitle role (accent while playing, the bars before it), the two-line blurb, the meta line in
// the TrackMeta role ("#n", the badges, "new", "23 min left", the date and the length, "played", the transcript mark), the 3-DIP progress
// rule (square), the "..." and the round accent play disc (Zune's one accent: the disc, the rule, "new" and "left" read the style's accent,
// not the page tone), the playing row's accent bar on the left edge; hover is the 5 % ink wash, pressed the pressed wash, selected the 12 %
// accent wash. No hairline between rows (Zune rows are bare).
//
// WHAT THE CORE KEEPS. `Episode.ReaderRow` (the reveal gate), every decision the row reads (`PlayedOf`, `InProgressOf`, `LeftOf`, `IsNew`,
// `ShownTitle`, `NumeralOf`, `ReaderPct`), the verbs (`RowContext`) and the host. The content is built ONCE per slot; every per-episode value
// is a bind over the slot's `RowItem`, so a recycle rebinds and allocates nothing. The geometry (art, padding, disc) is the core's
// `Episode.*`, so the row is exactly as tall as the core sized it.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

using RowContext = global::Wavee.Episode.RowContext;
using RowItem = global::Wavee.Episode.RowItem;

/// <summary>Zune's style for the episode row.</summary>
internal static class ZuneEpisodeRow
{
    /// <summary>The style Zune registers under <c>StyleKeys.EpisodeRow</c>. Stateless.</summary>
    public static readonly EpisodeRowStyle Style = new(Content, Seed, Failure);

    static readonly ContextMenuOptions s_menuOptions = new();

    /// <summary>Segoe Fluent's Lock (U+E72E), from its code point so the source stays ASCII.</summary>
    static readonly string LockGlyph = ((char)0xE72E).ToString();

    static readonly string SeedTitle = new((char)0x2007, ZuneListRules.EpisodeSeedTitleRun);
    static readonly string SeedDescription = new((char)0x2007, ZuneListRules.EpisodeSeedDescriptionRun);

    static readonly Func<ColorF> s_accent = static () => R.Ink.AccentDecor;

    static Element Content(in BoundItemScope<RowItem> item, RowContext ctx, bool narrow) => RowContent(in item, ctx, narrow, seed: false);

    static Element Seed(bool narrow)
    {
        var item = Episode.Fixed(default);
        return RowContent(in item, Episode.SeedContext, narrow, seed: true);
    }

    static Element Failure(Episode episode, bool narrow)
    {
        var item = Episode.Fixed(RowItem.Of(episode));
        return RowGrid(Art(in item, narrow ? Episode.RowArtNarrow : Episode.RowArt),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.S,
                Children = [R.Type.DenseMeta(Loc.Get(Strings.Podcast.Reader.Unavailable)) with { Wrap = TextWrap.Wrap, Color = Tok.TextSecondary }],
            },
            Button.Subtle(Loc.Get(Strings.Podcast.Reader.Retry), () => Episode.RetryRow(episode)));
    }

    static BoxEl RowGrid(Element art, Element copy, Element actions) => new()
    {
        Direction = 0, Gap = Episode.RowGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
        Padding = new Edges4(Episode.RowPadX + Episode.ToneBarWidth + ZuneListRules.EpisodeToneBarGap, Episode.RowPadY, Episode.RowPadX, Episode.RowPadY),
        Children = [art, copy, actions],
    };

    static Element RowContent(in BoundItemScope<RowItem> item, RowContext ctx, bool narrow, bool seed)
    {
        IReadSignal<RowItem> it = item.Item;
        Prop<ColorF> accentInk = Prop.Of(s_accent);
        Action<Episode> open = ctx.Open ?? Episode.OpenPageAction;

        var copy = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = ZuneListRules.EpisodeCopyGap,
            Opacity = item.Opacity(static r => Episode.PlayedOf(r.Episode) ? Episode.PlayedInk : 1f),
            Children = [TitleLine(in item, it, seed), Description(in item, seed), MetaLine(in item, accentInk, seed), Rule(in item, accentInk)],
        };
        var grid = RowGrid(Art(in item, narrow ? Episode.RowArtNarrow : Episode.RowArt), copy, Cluster(in item, it, ctx, narrow, seed));
        var root = new BoxEl
        {
            ZStack = true, MinWidth = 0f,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand,
            OnClick = item.Invoke(r => open(r.Episode)),
            Children =
            [
                // NOW PLAYING: the accent bar on the row's left edge, inset by the row's own padding.
                new BoxEl
                {
                    Width = Episode.ToneBarWidth, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Start,
                    Margin = new Edges4(0f, Episode.RowPadY, 0f, Episode.RowPadY), Fill = accentInk,
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
                Fill = Prop.Of(() => scope.IsSelected() ? R.Layer.SelectedRest : ColorF.Transparent),
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
                Children = [new BoxEl { Direction = 0, MinWidth = 0f, Children =
                [SelectorVisualsBound.BoundCheckLane(() => selection.Selecting.Value, scope.IsSelected, interact, Spacing.XS),
                 new BoxEl { ZStack = true, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = root.Children }] }],
            };
        }
        if (ctx.Overlay is { } overlay && ctx.Menu is { } menu)
            root = ContextSheets.Attach(root, overlay, () => menu(it.Peek().Episode), s_menuOptions);
        return root;
    }

    static Element Art(in BoundItemScope<RowItem> item, float edge)
    {
        var corners = R.Shape.All(ShapeRole.Cover);
        return new BoxEl
        {
            Width = edge, Height = edge, Shrink = 0f, Corners = corners, ClipToBounds = true,
            Opacity = item.Opacity(static r => Episode.PlayedOf(r.Episode) ? Episode.PlayedInk : 1f),
            Children =
            [
                new ImageEl
                {
                    Source = item.Image(static r => Episode.ArtOf(r.Episode)), Width = edge, Height = edge, Fit = ImageFit.Cover,
                    DecodePx = edge, Corners = corners,
                    Placeholder = item.Color(static r => Design.PlaceholderFor(Episode.ArtOf(r.Episode))),
                },
            ],
        };
    }

    /// <summary>The accent bars (now playing) · the title (accent while playing).</summary>
    static Element TitleLine(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, bool seed)
    {
        var role = R.Type.TrackTitle("");
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                seed ? new BoxEl() : item.ShowWhen(static r => Episode.IsNowPlaying(r.Episode),
                    () => Controls.Equalizer(Track.DeckPlaying, s_accent, ZuneListRules.EpisodeEqualizer)),
                new TextEl(seed ? SeedTitle : item.Text(static r => Episode.ShownTitle(r.Episode)))
                {
                    FontFamily = role.FontFamily, Size = role.Size, LineHeight = role.LineHeight, Weight = role.ResolvedWeight,
                    MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Grow = 1f, Basis = 0f, Shrink = 1f,
                    Color = Prop.Of(() => ZuneListRules.RowInk(Episode.IsNowPlaying(it.Value.Episode), R.Ink.AccentDecor, Tok.TextPrimary)),
                },
            ],
        };
    }

    static Element Description(in BoundItemScope<RowItem> item, bool seed)
    {
        var body = seed ? SeedDescription : item.Text(static r => Episode.DescriptionOf(r.Episode));
        var meta = R.Type.DenseMeta("");
        return new TextEl(body)
        {
            Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextSecondary, MaxLines = 2, Wrap = TextWrap.Wrap,
            Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            Visible = seed ? true : item.Show(static r => Episode.HasDescription(r.Episode)),
        };
    }

    /// <summary>"#n" · the badges · NEW · "23 min left" (in progress) · the date · the length (not in progress) · ✓ played · the transcript mark.</summary>
    static Element MetaLine(in BoundItemScope<RowItem> item, Prop<ColorF> accentInk, bool seed)
    {
        var meta = R.Type.TrackMeta("");
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = ZuneListRules.EpisodeMetaGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
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
                    Weight = 700, CharSpacing = ZuneListRules.EpisodeNewTracking, Color = accentInk, MaxLines = 1,
                    Case = R.Case.For(LabelKind.Label),
                    Visible = item.Show(static r => Episode.IsNew(in r)),
                },
                new TextEl(item.Text(static r => Episode.LeftOf(r.Episode)))
                {
                    Size = meta.Size, LineHeight = meta.LineHeight, Weight = 600, Color = accentInk, MaxLines = 1, Wrap = TextWrap.NoWrap,
                    Visible = item.Show(static r => Episode.InProgressOf(r.Episode)),
                },
                MetaText(seed ? Episode.s_dates.Get(Episode.SeedDateKey, Episode.s_dateFormat) : item.Text(static r => Episode.DateKeyOf(r.Episode), Episode.s_dates, Episode.s_dateFormat))
                    with { Visible = seed ? true : item.Show(static r => DateKeys.IsDayKey(Episode.DateKeyOf(r.Episode))) },
                MetaText(seed ? Episode.DurationLabel(Episode.SeedDurationMs) : item.Text(static r => Episode.DurationLabelOf(r.Episode)))
                    with { Visible = seed ? true : item.Show(static r => !Episode.InProgressOf(r.Episode) && r.Episode.IsValid && r.Episode.Knows(EpisodeFields.Duration) && r.Episode.DurationMs > 0) },
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                    Visible = item.Show(static r => Episode.PlayedOf(r.Episode)),
                    Children = [Icon(Icons.Check, ZuneListRules.FilmGlyph) with { Color = accentInk }, MetaText(Loc.Get(Strings.Podcast.Played))],
                },
                Icon(Icons.Document, ZuneListRules.FilmGlyph) with
                {
                    Color = Tok.TextTertiary, Visible = item.Show(static r => (Episode.FlagsOf(r.Episode) & EpisodeFlags.HasTranscript) != 0),
                },
            ],
        };
    }

    /// <summary>The 3-DIP accent rule (≤ 340, square) under an in-progress row's meta; the fill is a compositor scale from the left.</summary>
    static Element Rule(in BoundItemScope<RowItem> item, Prop<ColorF> accentInk) => new BoxEl
    {
        Direction = 0, Height = Episode.RuleHeight, MaxWidth = Episode.RuleMaxWidth,
        Fill = Prop.Of(static () => R.Layer.RowPressed), ClipToBounds = true, Shrink = 0f, Margin = new Edges4(0f, Spacing.XS, 0f, 0f),
        Visible = item.Show(static r => Episode.InProgressOf(r.Episode)),
        Children =
        [
            new BoxEl
            {
                Grow = 1f, Fill = accentInk, TransformOriginX = 0f,
                Transform = item.Value(static r => Affine2D.Scale(MathF.Max(0.001f, Episode.PlaybackProgressOf(r.Episode).Pct), 1f)),
            },
        ],
    };

    /// <summary>"..." (the row's menu) and the round accent disc, the "..." fading in with the row's hover; narrow: the disc alone.</summary>
    static Element Cluster(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, RowContext ctx, bool narrow, bool seed)
    {
        var focused = new Signal<bool>(false);
        Action<bool> focus = value => focused.Value = value;
        Element disc = Disc(in item, it, ctx, focus, seed);
        Element more = ActionCell(Icon(Icons.More, Episode.RowDiscGlyph, Tok.TextSecondary));
        return new BoxEl
        {
            Direction = (byte)(narrow ? 1 : 0), Gap = Spacing.XS, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                seed ? more : ToolTip.Wrap(((BoxEl)more) with { ClickRequestsContext = true, OnFocusChanged = focus }, Loc.Get(Strings.Common.More)),
                disc,
            ],
        };
    }

    static BoxEl ActionCell(Element glyph) => new()
    {
        Width = Episode.ActionBox, Height = Episode.ActionBox, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true,
        Children = [glyph],
    };

    /// <summary>The round accent disc: play, or pause on the playing row (through the host's verb).</summary>
    static Element Disc(in BoundItemScope<RowItem> item, IReadSignal<RowItem> it, RowContext ctx, Action<bool>? focus, bool seed)
    {
        Action<Episode> play = ctx.Play;
        var disc = new BoxEl
        {
            OnFocusChanged = focus,
            Width = Episode.RowDisc, Height = Episode.RowDisc, Shrink = 0f, Corners = Radii.Circle(Episode.RowDisc), Fill = Prop.Of(s_accent),
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HoverScale = R.Motion.ScaleEmphatic.Hover, PressScale = R.Motion.ScaleEmphatic.Press,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, BlocksDragArm = true,
            OnClick = item.Invoke(r => play(r.Episode)),
            Children =
            [
                Icon(Icons.Play, Episode.RowDiscGlyph) with
                {
                    Text = Prop.Of(() => Episode.IsNowPlaying(it.Value.Episode) && Track.DeckPlaying.Value ? Icons.Pause : Icons.Play),
                    Color = Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)),
                },
            ],
        };
        return seed ? disc : Controls.Named(disc, Loc.Get(Strings.Detail.Play));
    }

    static TextEl MetaText(Prop<string> text)
    {
        var meta = R.Type.TrackMeta("");
        return new TextEl(text) { Size = meta.Size, LineHeight = meta.LineHeight, Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap };
    }
}
