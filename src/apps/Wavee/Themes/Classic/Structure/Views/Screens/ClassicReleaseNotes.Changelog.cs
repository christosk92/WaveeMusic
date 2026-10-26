// ── Themes/Classic/Structure/Views/Screens/ClassicReleaseNotes.Changelog.cs — the changelog: a group per section, a stock Expander per change (themes plan 6.1) ──
//
// WinUI's settings-page pattern (2026-10-26): a section is a group heading ("Added", its count) over a stack of the STOCK Expander, one per change. A change's
// bold lead is the expander's header (wrapping, so a long one never runs off a narrow window) and the rest of the sentence is its content, with the contributor
// stack under it; a change with nothing past its lead is a plain stock SettingsCard at the expander's height. What a link does is the caller's (`openUrl`: the browser).

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
    /// <summary>One section: its heading and count over an Expander per change. <paramref name="visible"/> is the SelectorBar's choice; a hidden group stays mounted,
    /// so the changes keep whether they are open.</summary>
    static Element ChangeGroup(ReleaseSection section, string version, Action<string> openUrl, bool visible)
    {
        var items = section.Items;
        var kids = new Element[items.Length + 1];
        kids[0] = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.End, Margin = new Edges4(0f, 0f, 0f, K.GroupHeadingBottom),
            Children =
            [
                new TextEl(Loc.Get(SectionTitleLocKey(section.Kind))) { Size = K.BodySize, LineHeight = K.BodyLine, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(section.Kind == "known" ? Strings.WhatsNew.IssueCount(items.Length) : Strings.WhatsNew.ChangeCount(items.Length)) { Size = K.MetaSize, LineHeight = K.MetaLine, Color = Tok.TextSecondary },
            ],
        };
        for (int i = 0; i < items.Length; i++)
            kids[i + 1] = ChangeItem(items[i], openUrl) with { Key = "chg:" + version + ":" + (items[i].Id is { Length: > 0 } id ? id : section.Kind + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        return new BoxEl
        {
            Key = "sec:" + version + ":" + section.Kind, Direction = 1, Gap = K.ChangeGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Visible = visible,
            Children = kids,
        };
    }

    /// <summary>One change: the stock Expander with the lead as its header and the rest as its content, or a stock SettingsCard when there is no rest.</summary>
    static Element ChangeItem(ReleaseItem item, Action<string> openUrl)
    {
        var tokens = MarkdownLite.Tokenize(item.Text);
        var (lead, rest) = SplitLead(tokens);
        if (item.Scope is { Length: > 0 } scope) lead = scope + ": " + lead;
        if (rest.Length == 0 && item.Contributors.Length == 0)
            // At the expander header's height (48), so a change with nothing to open lines up with the ones that have.
            return SettingsCard.Create(new SettingsCard.Options { Header = lead, Style = SettingsCard.DefaultStyle with { MinHeight = K.ChangeRowMinHeight } });

        var content = new List<Element>(2);
        if (rest.Length > 0)
            content.Add(RichTextBlock.Paragraph(ToSpans(rest, openUrl)) with
            {
                Size = K.BodySize, LineHeight = K.BodyLine, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, MaxWidth = K.ChangeTextMaxWidth,
            });
        if (item.Contributors.Length > 0) content.Add(AvatarStack(item.Contributors));
        var header = new TextEl(lead)
        {
            Size = K.BodySize, LineHeight = K.BodyLine, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f,
            Margin = new Edges4(0f, K.ChangeHeaderPadY, 0f, K.ChangeHeaderPadY),
        };
        var body = new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = content.ToArray() };
        return Embed.Comp(new Expander.ExpanderSlots(header, body, null), static () => new Expander());
    }

    /// <summary>A changelog sentence split at its bold lead ("**Lead.** the rest"): the lead as plain text and the rest's tokens. A sentence without a bold lead is
    /// all lead.</summary>
    static (string Lead, InlineToken[] Detail) SplitLead(InlineToken[] tokens)
    {
        if (tokens.Length > 0 && tokens[0].Kind == InlineKind.Bold)
        {
            int from = 1;
            var rest = new List<InlineToken>(tokens.Length);
            for (int i = from; i < tokens.Length; i++)
            {
                var tk = tokens[i];
                if (rest.Count == 0 && tk.Kind == InlineKind.Text)
                {
                    string trimmed = tk.Text.TrimStart();
                    if (trimmed.Length == 0) continue;
                    tk = tk with { Text = trimmed };
                }
                rest.Add(tk);
            }
            return (tokens[0].Text.Trim(), rest.ToArray());
        }
        return (Plain(tokens).Trim(), []);
    }

    /// <summary>A tokenized sentence as plain text (what each run displays, the markup gone).</summary>
    static string Plain(InlineToken[] tokens)
    {
        var all = new System.Text.StringBuilder();
        foreach (var tk in tokens) all.Append(tk.Text);
        return all.ToString();
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
