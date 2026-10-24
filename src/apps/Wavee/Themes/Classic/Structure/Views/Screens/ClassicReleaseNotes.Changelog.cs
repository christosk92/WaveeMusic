// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.Changelog.cs — the changelog: sections, rows, chips, avatars, spans (themes plan 6.1, T11c) ──
//
// From `Screens\ReleaseNotes.UI.cs` §4: one `### Added` group per section (a component for its LOCAL "Show all N" state, whose props are re-pushed because the issue
// states land after the document), the bullet row (an optional UPPERCASED scope eyebrow run + the sentence as ONE selectable span paragraph, the reference chips on
// their own wrapping row, the contributor stack), the chips, the INITIALS avatars and the bridge from the pure tokenizer to span runs. What a link does is the
// caller's (`openUrl`: the browser, or nothing inside a card).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using static Wavee.ReleaseNotes;
using K = Wavee.Themes.Classic.ClassicReleaseNotesRules;

namespace Wavee.Themes.Classic;

internal static partial class ClassicReleaseNotes
{
    /// <summary>One <c>### Added</c> group. A component for its LOCAL "Show all N" state, and its props are RE-PUSHED:
    /// the issue states land after the document, so a frozen section would show the tool's snapshot forever.</summary>
    sealed class SectionView : Component
    {
        /// <summary>Gates on the section and the states by REFERENCE: the loader replaces the states cache wholesale, so the
        /// issue-state republish re-renders the sections and a page re-render with the same document re-renders none.
        /// <see cref="OpenUrl"/> is a verb of the page (stable for its life) and takes no part in the comparison. The tier's record rides along: a crossing re-renders
        /// the section (its rows' least height, the "Show all" target, the chips' hit boxes).</summary>
        public sealed record Props(ReleaseSection Section, IssueStateCache? States, Action<string> OpenUrl, ClassicReleaseNotesLayout Layout)
        {
            public bool Equals(Props? other) => other is not null && ReferenceEquals(Section, other.Section) && ReferenceEquals(States, other.States) && Layout == other.Layout;
            public override int GetHashCode() => HashCode.Combine(Section, States, Layout);
        }

        readonly Signal<bool> _all = new(false);

        /// <summary><paramref name="key"/> is unique across the page (release + kind), or two stacked releases share one fold.</summary>
        public static Element Create(ReleaseSection section, IssueStateCache? states, Action<string> openUrl, string key, ClassicReleaseNotesLayout layout)
            => Embed.Comp(new Props(section, states, openUrl, layout), static () => new SectionView()) with { Key = key };

        public override Element Render()
        {
            var p = UseProps<Props>();
            var items = p.Section.Items;
            bool all = _all.Value;
            int shown = ShownRows(items.Length, all);

            var rows = new List<Element>(shown * 2);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) rows.Add(new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.Stretch });
                rows.Add(ChangelogRow(items[i], p.States, p.OpenUrl, p.Layout));
            }

            var (glyph, ink, wash) = Badge(p.Section.Kind);
            var header = new List<Element>(4)
            {
                new BoxEl
                {
                    Width = K.SectionBadge, Height = K.SectionBadge, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(6f), Fill = wash,
                    Children = [new TextEl(glyph) { Size = K.MetaSize, Weight = 700, Color = ink }],
                },
                new TextEl(Loc.Get(SectionTitleLocKey(p.Section.Kind))) { Size = K.BodySize, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)) { Size = K.MetaSize, Color = Tok.TextTertiary, Grow = 1f },
            };
            if (shown < items.Length)   // expands in place and never folds back while the page is open
                header.Add(Btn(Strings.WhatsNew.ShowAll(items.Length), () => _all.Value = true, ButtonAppearance.Subtle, ControlSize.Small, p.Layout.Target) with { Shrink = 0f });

            return new BoxEl
            {
                Direction = 1, Gap = K.SectionGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinHeight = MathF.Max(K.SectionHeaderMinHeight, p.Layout.Target), Children = header.ToArray() },
                    new BoxEl
                    {
                        Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                        Corners = CornerRadius4.All(Radii.Card), ClipToBounds = true,
                        Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                        Children = rows.ToArray(),
                    },
                ],
            };
        }

        /// <summary>Plain characters, not the icon font: "+ ~ ✓ − !" is what a changelog reads as (ch 28 §4.4).</summary>
        static (string Glyph, ColorF Ink, ColorF Wash) Badge(string? kind) => kind switch
        {
            "added" => ("+", Tok.SystemFillSuccess, Tok.SystemFillSuccessBackground),
            "changed" => ("~", Tok.AccentTextPrimary, Tok.AccentSubtle),
            "fixed" => ("✓", Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
            "removed" => ("−", Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
            "deprecated" => ("!", Tok.SystemFillCaution, Tok.SystemFillCautionBackground),
            "security" => ("!", Tok.SystemFillCritical, Tok.SystemFillCriticalBackground),
            _ => ("!", Tok.TextTertiary, Tok.FillSubtleSecondary),
        };
    }

    /// <summary>One bullet: an optional UPPERCASED scope eyebrow run + the sentence as ONE selectable span paragraph, then
    /// the reference chips on their own wrapping row (a span run cannot carry a box) and the contributor stack.</summary>
    static Element ChangelogRow(ReleaseItem item, IssueStateCache? states, Action<string> openUrl, ClassicReleaseNotesLayout layout)
    {
        var tokens = MarkdownLite.Tokenize(item.Text);
        var spans = new List<TextSpan>(tokens.Length + 1);
        if (item.Scope is { Length: > 0 } scope)
            spans.Add(new TextSpan(scope.ToUpperInvariant() + "   ", Weight: 700, Color: Tok.TextTertiary, Size: K.ScopeSize));
        spans.AddRange(ToSpans(tokens, openUrl));

        var refs = new List<Element>(item.Issues.Length + item.Prs.Length);
        foreach (var i in item.Issues) refs.Add(IssueChip(i, states?.Lookup(i), openUrl, layout.Target));
        foreach (var pr in item.Prs) refs.Add(PrChip(pr, openUrl, layout.Target));

        var denseBody = R.Type.DenseMeta("");
        var column = new List<Element>(2)
        {
            RichTextBlock.Paragraph(spans.ToArray()) with
            {
                Size = denseBody.Size, LineHeight = denseBody.LineHeight,
                Grow = 1f, MinWidth = 0f, MaxWidth = float.NaN, Wrap = TextWrap.Wrap,
            },
        };
        if (refs.Count > 0)
            column.Add(new BoxEl { Direction = 0, Gap = K.RowRefGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = refs.ToArray() });

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = layout.RowMinH > 0f ? FlexAlign.Center : FlexAlign.Start, Padding = new Edges4(Spacing.M, 9f, Spacing.M, 9f), MinHeight = layout.RowMinH,
            Children =
            [
                new BoxEl { Direction = 1, Gap = K.RowRefGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = column.ToArray() },
                AvatarStack(item.Contributors),
            ],
        };
    }

    /// <summary>"● #412 closed": the LIVE state when fetched, the release tool's snapshot otherwise — never nothing.</summary>
    static Element IssueChip(ReleaseIssue issue, IssueState? live, Action<string> openUrl, float target)
    {
        var state = IssueChipState(live, issue);
        ColorF dot = state switch { ChipState.Open => Tok.SystemFillSuccess, ChipState.NotPlanned => Tok.TextTertiary, _ => Tok.AccentDefault };
        string word = Loc.Get(state switch
        {
            ChipState.Open => Strings.WhatsNew.Issue.Open,
            ChipState.NotPlanned => Strings.WhatsNew.Issue.NotPlanned,
            _ => Strings.WhatsNew.Issue.Closed,
        });
        return Chip("#" + issue.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), word, dot,
            Links.IssueUrl(issue.Repo, issue.Number, issue.IsPullRequest), IssueChipTip(live, issue), openUrl, target);
    }

    /// <summary>A pull request is merged or it is not — there is no "not planned".</summary>
    static Element PrChip(ReleasePr pr, Action<string> openUrl, float target) => Chip("!" + pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Loc.Get(pr.Merged ? Strings.WhatsNew.Issue.Merged : Strings.WhatsNew.Issue.Open),
        pr.Merged ? Tok.AccentDefault : Tok.SystemFillSuccess, Links.IssueUrl(pr.Repo, pr.Number, pr: true), pr.Title, openUrl, target);

    /// <summary>A reference chip. Where the tier asks for a hit target (<paramref name="target"/> 40 at Compact) the chip keeps its 18-tall LOOK but the focusable, clickable
    /// node is a transparent box of the target's size around it: the hit area grows, never the picture.</summary>
    static Element Chip(string number, string word, ColorF dot, string url, string? tooltip, Action<string> openUrl, float target)
    {
        var look = new BoxEl
        {
            Shrink = 0f, Direction = 0, Gap = K.ChipGap, Height = K.ChipHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(K.ChipHeight / 2f),
            Children =
            [
                new BoxEl { Width = K.ChipDot, Height = K.ChipDot, Shrink = 0f, Corners = CornerRadius4.All(3.5f), Fill = dot },
                R.Type.MicroMeta(number) with { Weight = 600, Color = Tok.TextPrimary, FontFamily = "Cascadia Code", MaxLines = 1 },
                R.Type.MicroMeta(word) with { Color = Tok.TextTertiary, MaxLines = 1 },
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
        return tooltip is { Length: > 0 } t ? ToolTip.Wrap(chip, t) : chip;   // no title anywhere ⇒ no tooltip at all
    }

    /// <summary>INITIALS, not avatar images (no third-party image request per contributor). Empty ⇒ zero-size, hit-invisible.</summary>
    static Element AvatarStack(ReleaseContributor[] contributors)
    {
        if (contributors.Length == 0) return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        int shown = Math.Min(contributors.Length, K.AvatarMax);
        var kids = new List<Element>(shown + 1);
        for (int i = 0; i < shown; i++)
        {
            string login = contributors[i].Login;
            ColorF tint = K.AvatarTints[AvatarTintIndex(login)];
            kids.Add(ToolTip.Wrap(new BoxEl
            {
                Width = K.AvatarSize, Height = K.AvatarSize, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(K.AvatarSize / 2f), Fill = tint,
                BorderWidth = K.AvatarRing, BorderColor = Tok.FillSolidBase,             // cuts the disc out of the one behind it
                Margin = i == 0 ? default : new Edges4(-5f, 0f, 0f, 0f),
                // The initials take the ink the disc's own tint reads with (the kind's on-accent ink read 1.6-3.4 : 1 on half the tints; audit B8).
                Children = [new TextEl(Initials(login)) { Size = K.AvatarInitialsSize, Weight = 700, Color = ColorContrast.PickContrast(tint) }],
            }, contributors[i].FirstTime ? Strings.WhatsNew.FirstContribution(login) : login));
        }
        if (contributors.Length > shown)
            kids.Add(new TextEl("+" + (contributors.Length - shown).ToString(System.Globalization.CultureInfo.InvariantCulture))
                { Size = K.AvatarMoreSize, Weight = 600, Color = Tok.TextTertiary, Margin = new Edges4(4f, 0f, 0f, 0f) });
        return new BoxEl { Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    /// <summary>The one bridge from the pure tokenizer to span runs: a sentence is ONE paragraph whose bold / code / link
    /// runs are real spans. <paramref name="openUrl"/> decides what a link does (the browser, or nothing inside a card).</summary>
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
                // Monospace one step down (the WinUI inline-code convention); no plate — a span carries no box.
                InlineKind.Code => new TextSpan(t.Text, Weight: t.Bold ? (ushort)600 : (ushort)0, Size: K.CodeSpanSize, FontFamily: "Cascadia Code", Color: Tok.TextSecondary),
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
