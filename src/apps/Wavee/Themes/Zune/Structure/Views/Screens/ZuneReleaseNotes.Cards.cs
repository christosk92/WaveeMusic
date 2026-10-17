// ── Themes/Zune/Structure/Views/Screens/ZuneReleaseNotes.Cards.cs — the highlights and the changelog (themes plan 6.3, P5 Z8b) ──────────────────────
//
// Board Z-ReleaseNotes: the highlights as panels (a swatch, a 15/20 title, three lines of the text, the accent "read more"; the Store card with its round button) in a grid of the
// tier's columns; the changelog as groups (a 22/300 heading with its count, a line per change: the sentence with its scope, the reference chips, the contributors' initials).
// A highlight card is shared by the page and the after-update plate. What a link does is the caller's (`openUrl`: the browser, or nothing inside a card).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Zune.ZuneReleaseNotesRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneReleaseNotes
{
    // The grid tracks (shared, never mutated: a grid keeps its track array by reference).
    static readonly Dictionary<int, TrackSize[]> s_tracks = new();

    static TrackSize[] Tracks(int columns)
    {
        if (!s_tracks.TryGetValue(columns, out var tracks))
        {
            tracks = new TrackSize[columns];
            for (int i = 0; i < columns; i++) tracks[i] = TrackSize.Star();
            s_tracks[columns] = tracks;
        }
        return tracks;
    }

    /// <summary>Three is a cap, not a layout. No highlights: a 0-height, hit-invisible box (the heading AND the row vanish). ONE grid at every tier whose track count is the
    /// record's, each card in a cell keyed by the highlight, so a crossing moves the same cards into their new cells.</summary>
    static Element Strip(IReadOnlyList<HighlightItem> highlights, Action<int> open, Action openStore, ZuneReleaseNotesLayout layout)
    {
        if (highlights.Count == 0) return new BoxEl { Height = 0f, HitTestVisible = false };
        int n = Math.Min(highlights.Count, HighlightMax);
        var cards = new Element[n];
        for (int i = 0; i < n; i++)
        {
            int idx = i;   // the row's own index, not the closed-over loop variable
            cards[i] = new BoxEl
            {
                Key = "zrn:hl:" + SlideId(highlights[i], i), Direction = 1, MinWidth = 0f, Animate = ZuneMotion.ColumnCollapse,
                Children = [Card(highlights[i], () => open(idx), openStore, layout, compact: false)],
            };
        }
        return new BoxEl
        {
            Key = "zrn:highlights", Direction = 1, Gap = K.StripGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                ZuneText.Heading(Loc.Get(Strings.WhatsNew.Highlights)) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new GridEl { Key = "zrn:hl:grid", Columns = Tracks(Math.Max(1, layout.HighlightColumns)), ColGap = layout.HighlightGap, RowGap = layout.HighlightGap, AlignSelf = FlexAlign.Stretch, Children = cards },
            ],
        };
    }

    /// <summary>One highlight panel (shared by the page's grid and the after-update plate's row). The whole panel is the hit region (at least the tier's target tall); the Store
    /// card keeps its round button a SIBLING of the hit region, so a card never nests a button in a button.</summary>
    internal static Element Card(HighlightItem item, Action open, Action openStore, ZuneReleaseNotesLayout layout, bool compact)
    {
        var h = item.Highlight;
        bool store = HighlightVisibility.IsStore(h);
        var k = KitOf(layout);
        var body = new List<Element>(5)
        {
            Media(item.Poster, store),
            ZuneText.Hint(Loc.Get(KindPillLocKey(h.Kind, store))) with { Color = store ? R.Ink.AccentAction : Tok.TextTertiary, MaxLines = 1 },
            R.Type.TrackTitle(h.Title) with { Wrap = TextWrap.Wrap, MaxLines = K.CardTitleLines, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            RichTextBlock.Paragraph(ToSpans(MarkdownLite.Tokenize(h.Body), IgnoreLink), isTextSelectionEnabled: false)
                with { Size = R.Type.TrackMeta("").Size, LineHeight = R.Type.TrackMeta("").LineHeight, MaxLines = compact ? K.CardBodyLinesCompact : K.CardBodyLines, Wrap = TextWrap.Wrap, Trim = TextTrim.WordEllipsis, Color = Tok.TextSecondary },
        };
        // "read more" on every non-store card, overflowing or not: a short card gives no other cue that a click opens anything. A video highlight says so.
        if (!store)
            body.Add(R.Type.DenseMeta(Loc.Get(Strings.WhatsNew.ReadMore) + (IsVideo(h) ? "  ▶" : "")) with { Color = R.Ink.AccentAction, MaxLines = 1 });

        var panel = new BoxEl
        {
            Direction = 1, Gap = Spacing.S, MinWidth = 0f, MinHeight = MathF.Max(K.CardMinHeight, layout.Target), AlignSelf = FlexAlign.Stretch,
            Padding = Edges4.All(K.CardPad), Fill = R.Layer.FloatingPane, Grow = 1f,
        };
        if (!store)
            return panel.Interactive(Interaction.Subtle) with
            {
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open, Children = body.ToArray(),
            };
        return panel with
        {
            Padding = default,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.S, MinWidth = 0f, Padding = new Edges4(K.CardPad, K.CardPad, K.CardPad, 0f),
                    Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open, Children = body.ToArray(),
                },
                new BoxEl { Direction = 0, Padding = Edges4.All(K.CardPad), Children = [k.Go(Loc.Get(Strings.WhatsNew.StoreCta), openStore)] },
            ],
        };
    }

    /// <summary>The panel's picture: the poster when the loader found one, else the board's swatch (a square of the accent: a card that loses its picture reads as broken).</summary>
    static Element Media(string? poster, bool store)
    {
        if (poster is { Length: > 0 })
            return new ImageEl
            {
                Source = poster, Fit = ImageFit.Cover, Height = K.PosterHeight, AlignSelf = FlexAlign.Stretch, DecodePx = K.PosterDecodePx,
                Placeholder = Tok.FillSubtleSecondary, Corners = CornerRadius4.All(R.Shape.Cover),
            };
        return new BoxEl { Width = K.SwatchSize, Height = K.SwatchSize, Shrink = 0f, Fill = store ? R.Ink.AccentAction : R.Ink.AccentDecor, HitTestVisible = false };
    }

    static bool IsVideo(ReleaseHighlight h) => h.Media is { } m && string.Equals(m.Kind, "video", StringComparison.OrdinalIgnoreCase);

    // ══ the changelog ════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One group (`### Added`). A component for its LOCAL "show all N" state, and its props are RE-PUSHED: the issue states land after the document, so a frozen section
    /// would show the tool's snapshot forever.</summary>
    sealed class SectionView : Component
    {
        /// <summary>Gates on the section and the states by REFERENCE: the loader replaces the states cache wholesale, so the issue-state republish re-renders the sections and a page
        /// re-render with the same document re-renders none. <see cref="OpenUrl"/> is a verb of the page and takes no part in the comparison.</summary>
        public sealed record Props(ReleaseSection Section, IssueStateCache? States, Action<string> OpenUrl, ZuneReleaseNotesLayout Layout)
        {
            public bool Equals(Props? other) => other is not null && ReferenceEquals(Section, other.Section) && ReferenceEquals(States, other.States) && Layout == other.Layout;
            public override int GetHashCode() => HashCode.Combine(Section, States, Layout);
        }

        readonly Signal<bool> _all = new(false);

        /// <summary><paramref name="key"/> is unique across the page (release + kind), or two stacked releases share one fold.</summary>
        public static Element Create(ReleaseSection section, IssueStateCache? states, Action<string> openUrl, string key, ZuneReleaseNotesLayout layout)
            => Embed.Comp(new Props(section, states, openUrl, layout), static () => new SectionView()) with { Key = key };

        public override Element Render()
        {
            var p = UseProps<Props>();
            var k = KitOf(p.Layout);
            var items = p.Section.Items;
            bool all = _all.Value;
            int shown = ShownRows(items.Length, all);

            var rows = new List<Element>(shown);
            for (int i = 0; i < shown; i++) rows.Add(ChangelogRow(items[i], p.States, p.OpenUrl, p.Layout));

            var header = new List<Element>(3)
            {
                ZuneText.Group(Loc.Get(SectionTitleLocKey(p.Section.Kind))) with { Shrink = 0f, Wrap = TextWrap.NoWrap },
                ZuneText.Hint(items.Length.ToString(CultureInfo.InvariantCulture)),
            };
            if (shown < items.Length)   // expands in place and never folds back while the page is open
                header.Add(k.Cmd(Strings.WhatsNew.ShowAll(items.Length), () => _all.Value = true));
            return new BoxEl
            {
                Direction = 1, Gap = K.SectionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.End, MinHeight = K.SectionHeaderHeight, Wrap = true, Children = header.ToArray() },
                    new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = rows.ToArray() },
                ],
            };
        }
    }

    /// <summary>One change: the sentence as ONE selectable span paragraph (the scope as a tertiary lead run), then the reference chips on their own wrapping row and the
    /// contributors' initials.</summary>
    static Element ChangelogRow(ReleaseItem item, IssueStateCache? states, Action<string> openUrl, ZuneReleaseNotesLayout layout)
    {
        var tokens = MarkdownLite.Tokenize(item.Text);
        var spans = new List<TextSpan>(tokens.Length + 1);
        if (item.Scope is { Length: > 0 } scope)
            spans.Add(new TextSpan(scope + "   ", Weight: K.ScopeWeight, Color: Tok.TextTertiary));
        spans.AddRange(ToSpans(tokens, openUrl));

        var refs = new List<Element>(item.Issues.Length + item.Prs.Length);
        foreach (var i in item.Issues) refs.Add(IssueChip(i, states?.Lookup(i), openUrl, layout.Target));
        foreach (var pr in item.Prs) refs.Add(PrChip(pr, openUrl, layout.Target));

        var meta = R.Type.DenseMeta("");
        var column = new List<Element>(2)
        {
            RichTextBlock.Paragraph(spans.ToArray()) with { Size = meta.Size, LineHeight = meta.LineHeight, Grow = 1f, MinWidth = 0f, MaxWidth = float.NaN, Wrap = TextWrap.Wrap, Color = Tok.TextSecondary },
        };
        if (refs.Count > 0)
            column.Add(new BoxEl { Direction = 0, Gap = K.RowRefGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = refs.ToArray() });
        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, K.RowPadY, 0f, K.RowPadY), MinHeight = K.RowMinHeight,
            Children =
            [
                new BoxEl { Direction = 1, Gap = K.RowRefGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = column.ToArray() },
                AvatarStack(item.Contributors),
            ],
        };
    }

    /// <summary>"#412 closed": the LIVE state when fetched, the release tool's snapshot otherwise, never nothing.</summary>
    static Element IssueChip(ReleaseIssue issue, IssueState? live, Action<string> openUrl, float target)
    {
        var state = IssueChipState(live, issue);
        ColorF ink = state switch { ChipState.Open => Tok.SystemFillSuccess, ChipState.NotPlanned => Tok.TextTertiary, _ => R.Ink.AccentAction };
        string word = Loc.Get(state switch
        {
            ChipState.Open => Strings.WhatsNew.Issue.Open,
            ChipState.NotPlanned => Strings.WhatsNew.Issue.NotPlanned,
            _ => Strings.WhatsNew.Issue.Closed,
        });
        return Chip("#" + issue.Number.ToString(CultureInfo.InvariantCulture), word, ink,
            Links.IssueUrl(issue.Repo, issue.Number, issue.IsPullRequest), IssueChipTip(live, issue), openUrl, target);
    }

    /// <summary>A pull request is merged or it is not: there is no "not planned".</summary>
    static Element PrChip(ReleasePr pr, Action<string> openUrl, float target) => Chip("!" + pr.Number.ToString(CultureInfo.InvariantCulture),
        Loc.Get(pr.Merged ? Strings.WhatsNew.Issue.Merged : Strings.WhatsNew.Issue.Open),
        pr.Merged ? R.Ink.AccentAction : Tok.SystemFillSuccess, Links.IssueUrl(pr.Repo, pr.Number, pr: true), pr.Title, openUrl, target);

    /// <summary>A reference chip: the number and the state word on a 6 % plate. Where the tier asks for a hit target the chip keeps its LOOK but the focusable, clickable node is a
    /// transparent box of the target's size around it: the hit area grows, never the picture.</summary>
    static Element Chip(string number, string word, ColorF ink, string url, string? tooltip, Action<string> openUrl, float target)
    {
        var look = new BoxEl
        {
            Shrink = 0f, Direction = 0, Gap = K.ChipGap, MinHeight = K.ChipHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(K.ChipPadX, 0f, K.ChipPadX, 0f), Fill = Tok.FillSubtleSecondary,
            Children =
            [
                R.Type.MicroMeta(number) with { Color = Tok.TextPrimary, FontFamily = K.Mono, MaxLines = 1 },
                R.Type.MicroMeta(word) with { Color = ink, MaxLines = 1 },
            ],
        };
        BoxEl chip = target > 0f
            ? new BoxEl
            {
                Shrink = 0f, Direction = 0, MinHeight = target, MinWidth = target, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Focusable = true, OnClick = () => openUrl(url),
                Children = [look with { HitTestVisible = false }],
            }
            : (look with { Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Focusable = true, OnClick = () => openUrl(url) }).Interactive(Interaction.Control);
        return tooltip is { Length: > 0 } t ? ToolTip.Wrap(chip, t) : chip;   // no title anywhere: no tooltip at all
    }

    /// <summary>INITIALS, not avatar images (no third-party image request per contributor). Empty: zero-size, hit-invisible.</summary>
    static Element AvatarStack(ReleaseContributor[] contributors)
    {
        if (contributors.Length == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        int shown = Math.Min(contributors.Length, K.AvatarMax);
        var kids = new List<Element>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            string login = contributors[i].Login;
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Width = K.AvatarSize, Height = K.AvatarSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(ZuneControls.Round), Fill = K.AvatarTints[AvatarTintIndex(login)],
                BorderWidth = K.AvatarRing, BorderColor = R.Layer.ShellGround,   // cuts the disc out of the one behind it
                Margin = i == 0 ? default : new Edges4(-K.AvatarOverlap, 0f, 0f, 0f),
                Children = [new TextEl(Initials(login)) { Size = R.Type.MicroMeta("").Size, Weight = K.AvatarWeight, Color = Tok.TextOnAccentPrimary }],
            }, contributors[i].FirstTime ? Strings.WhatsNew.FirstContribution(login) : login));
        }
        if (contributors.Length > shown)
            kids.Add(R.Type.MicroMeta("+" + (contributors.Length - shown).ToString(CultureInfo.InvariantCulture)) with { Color = Tok.TextTertiary, Margin = new Edges4(Spacing.XS, 0f, 0f, 0f) });
        return new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    /// <summary>The one bridge from the pure tokenizer to span runs: a sentence is ONE paragraph whose bold / code / link runs are real spans. <paramref name="openUrl"/> decides
    /// what a link does (the browser, or nothing inside a card).</summary>
    static TextSpan[] ToSpans(InlineToken[] tokens, Action<string> openUrl)
    {
        if (tokens.Length == 0) return [];
        var spans = new TextSpan[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            spans[i] = t.Kind switch
            {
                InlineKind.Bold => RichTextBlock.Bold(t.Text),
                // Monospace one step down; no plate: a span carries no box.
                InlineKind.Code => new TextSpan(t.Text, Weight: t.Bold ? (ushort)K.CodeBoldWeight : (ushort)0, Size: K.CodeSpanSize, FontFamily: K.Mono, Color: Tok.TextSecondary),
                InlineKind.Link or InlineKind.Url => SpanLink(t.Text, t.Target ?? t.Text, openUrl),
                InlineKind.Issue => SpanLink(t.Text, Links.IssueUrl(t.Repo, t.Number, pr: false), openUrl),
                InlineKind.Pr => SpanLink(t.Text, Links.IssueUrl(t.Repo, t.Number, pr: true), openUrl),
                InlineKind.Mention => SpanLink(t.Text, "https://github.com/" + (t.Target ?? t.Text.TrimStart('@')), openUrl),
                _ => RichTextBlock.Run(t.Text),
            };
        }
        return spans;
    }

    static TextSpan SpanLink(string text, string url, Action<string> open) => RichTextBlock.Hyperlink(text, () => open(url));
}
