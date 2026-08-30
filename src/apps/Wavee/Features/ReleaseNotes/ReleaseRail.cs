using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.ReleaseNotes;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The right-hand release timeline: every release the index knows about, newest first.
///
/// <para>Two markers, and they answer different questions. <b>YOU</b> is the build that is RUNNING — the reader's own
/// version, so a page about 0.3.0 still says where they actually are. The <b>unread dot</b> is anything newer than
/// <c>ReleaseNotesLastSeen</c>, which the page advances the moment it opens; so the dots are what the reader has not
/// looked at, not what they have not installed.</para>
///
/// <para>A <see cref="Flow.For"/> boundary keyed on the version: opening an older release re-keys ONE row's selection,
/// not the whole rail.</para></summary>
static class ReleaseRail
{
    public const float Width = 208f;

    public static Element Create(ReleaseNotesIndex? index, string? selectedVersion, string runningVersion,
                                 string lastSeenVersion, Action<string> open)
    {
        ReleaseNotesIndexEntry[] entries = index?.Releases ?? [];
        if (entries.Length == 0) return new BoxEl { Width = 0f, HitTestVisible = false };

        // Snapshot the list ONCE: Flow.For's thunk must be cheap and must not re-derive the array per evaluation.
        // One row per VERSION, first (newest) wins: Flow.For keys on the version, and a duplicate key is a reconciler
        // fault. Real releases never share a semver, but a rehearsal feed (two quads of one semver) does — the rail
        // must not become the place that trips over it.
        var rows = new List<ReleaseNotesIndexEntry>(entries.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries) if (e is not null && seen.Add(e.Version ?? "")) rows.Add(e);

        return new BoxEl
        {
            Direction = 1, Gap = 6f, Width = Width, Shrink = 0f, MinHeight = 0f,
            Children =
            [
                WaveeType.Eyebrow(Loc.Get(Strings.WhatsNew.Releases)) with
                    { Color = Tok.TextTertiary, Margin = new Edges4(4f, 6f, 4f, 2f) },
                ScrollView(new BoxEl
                {
                    Direction = 1, Gap = 2f, MinWidth = 0f,
                    Children = [ Flow.For(() => (IReadOnlyList<ReleaseNotesIndexEntry>)rows, e => e.Version,
                        e => Row(e, selectedVersion, runningVersion, lastSeenVersion, open)) ],
                }) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, ScrollKey = "whatsnew:rail" },
                new TextEl(Loc.Get(Strings.WhatsNew.RailFoot))
                    { Size = 11.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(4f, 6f, 4f, 6f) },
            ],
        };
    }

    static Element Row(ReleaseNotesIndexEntry e, string? selected, string running, string lastSeen, Action<string> open)
    {
        bool isSelected = string.Equals(e.Version, selected, StringComparison.Ordinal);
        bool isYou = string.Equals(e.Version, running, StringComparison.Ordinal);
        bool isBeta = string.Equals(e.Channel, "beta", StringComparison.OrdinalIgnoreCase);
        bool unread = !isSelected && AppUpdateVersion.IsNewer(e.Version, lastSeen);

        var title = new List<Element>(3)
        {
            new TextEl(e.Version) { Size = 12.5f, Weight = 600, Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1 },
        };
        if (isYou) title.Add(YouPill());
        else if (unread) title.Add(new BoxEl { Width = 6f, Height = 6f, Shrink = 0f, Corners = CornerRadius4.All(3f), Fill = Tok.AccentDefault });
        if (isBeta) title.Add(BetaPill());

        return new BoxEl
        {
            Key = "rail:" + e.Version,
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Padding = new Edges4(8f, 8f, 8f, 8f), Corners = CornerRadius4.All(6f),
            Role = AutomationRole.NavigationItem, Cursor = CursorId.Hand, Focusable = true,
            OnClick = () => open(e.Version),
            Children =
            [
                new BoxEl
                {
                    Width = 9f, Height = 9f, Shrink = 0f, Corners = CornerRadius4.All(4.5f),
                    Margin = new Edges4(2f, 4f, 0f, 0f),
                    BorderWidth = 1.5f, BorderColor = isSelected ? Tok.AccentDefault : Tok.TextTertiary,
                    Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                },
                new BoxEl
                {
                    Direction = 1, Gap = 1f, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, MinWidth = 0f, Wrap = true, Children = title.ToArray() },
                        new TextEl(Subtitle(e))
                            { Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        }.Interactive(isSelected ? Interaction.ListRow : Interaction.Subtle);
    }

    static string Subtitle(ReleaseNotesIndexEntry e)
    {
        string date = ReleaseNotesText.Date(e.Date);
        if (e.Name is { Length: > 0 } name)
            return date.Length > 0 ? name + "  ·  " + date : name;
        return date;
    }

    static Element YouPill() => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(5f, 1f, 5f, 1f), Corners = CornerRadius4.All(6f), Fill = Tok.AccentDefault,
        Children = [ new TextEl(Loc.Get(Strings.WhatsNew.You)) { Size = 9.5f, Weight = 700, Color = Tok.TextOnAccentPrimary } ],
    };

    static Element BetaPill() => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(5f, 1f, 5f, 1f), Corners = CornerRadius4.All(6f), Fill = Tok.FillSubtleSecondary,
        Children = [ new TextEl(Loc.Get(Strings.WhatsNew.Beta)) { Size = 9.5f, Weight = 700, Color = Tok.TextTertiary } ],
    };
}
