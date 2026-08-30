using System;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core.ReleaseNotes;

namespace Wavee;

/// <summary>The GitHub reference chips a changelog line carries: <c>● #412 closed</c>, <c>■ !430 merged</c>.
///
/// <para>The state a chip shows is the LIVE one when the page managed to fetch it (<see cref="IssueState"/> from
/// <c>ReleaseNotesStore.RefreshIssueStatesAsync</c>) and the SNAPSHOT the release tool baked in otherwise — never
/// nothing. That is the whole point of the chip: "the fix shipped, and here is where the report stands today", which
/// a static changelog line cannot say.</para></summary>
static class IssueChip
{
    const float Height = 18f;

    /// <summary>An issue reference. <paramref name="live"/> is the freshly fetched state (null ⇒ fall back to the
    /// document's own snapshot).</summary>
    public static Element Create(ReleaseIssue issue, IssueState? live, Action<string> openUrl)
    {
        string state = live?.State ?? issue.State;
        string? reason = live?.StateReason ?? issue.StateReason;
        bool open = string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);
        bool notPlanned = !open && string.Equals(reason, "not_planned", StringComparison.OrdinalIgnoreCase);

        ColorF dot = open ? Tok.SystemFillSuccess : notPlanned ? Tok.TextTertiary : Tok.AccentDefault;
        string word = Loc.Get(open
            ? Strings.WhatsNew.Issue.Open
            : notPlanned ? Strings.WhatsNew.Issue.NotPlanned : Strings.WhatsNew.Issue.Closed);
        string url = ReleaseNotesText.IssueUrl(issue.Repo, issue.Number, issue.IsPullRequest);
        string tip = live?.Title is { Length: > 0 } lt ? lt : issue.Title;

        return Chip("#" + issue.Number.ToString(CultureInfo.InvariantCulture), word, dot, url, tip, openUrl);
    }

    /// <summary>A pull-request reference. PRs have no "not planned" — they are merged or they are not.</summary>
    public static Element Pr(ReleasePr pr, Action<string> openUrl)
    {
        ColorF dot = pr.Merged ? Tok.AccentDefault : Tok.SystemFillSuccess;
        string word = Loc.Get(pr.Merged ? Strings.WhatsNew.Issue.Merged : Strings.WhatsNew.Issue.Open);
        string url = ReleaseNotesText.IssueUrl(pr.Repo, pr.Number, pr: true);
        return Chip("!" + pr.Number.ToString(CultureInfo.InvariantCulture), word, dot, url, pr.Title, openUrl);
    }

    static Element Chip(string number, string word, ColorF dot, string url, string? tooltip, Action<string> openUrl)
    {
        // Role/Cursor/OnClick live only on BoxEl — the two TextEl children carry colour and nothing else, and the
        // Hand cursor resolves up the ancestor chain to this box.
        var chip = new BoxEl
        {
            Shrink = 0f, Direction = 0, Gap = 4f, Height = Height,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(6f, 0f, 8f, 0f), Corners = CornerRadius4.All(Height / 2f),
            Role = AutomationRole.Hyperlink, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => openUrl(url),
            Children =
            [
                new BoxEl { Width = 7f, Height = 7f, Shrink = 0f, Corners = CornerRadius4.All(3.5f), Fill = dot },
                new TextEl(number) { Size = 11f, Weight = 600, Color = Tok.TextPrimary, FontFamily = "Cascadia Code", MaxLines = 1 },
                new TextEl(word) { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        }.Interactive(Interaction.Control);

        return tooltip is { Length: > 0 } t ? ToolTip.Wrap(chip, t) : chip;
    }
}
