// ── Themes/WinUi/Structure/Views/Screens/WinUiSettings.Home.cs — the Settings home: the Windows 11 Settings home in Wavee's page (design 3.3, 7.2) ──────────
//
// The concept boards Main (Standard) and W-Compact. The home holds no controls (a second control for a dial would be a second home); it holds state and doors,
// each from `SettingsPageProps.Home.Read()`:
//   · "At a glance": the six facts as clickable cards in a grid (the category's glyph, the title, the value at rest, a chevron), 2 per row at Small and
//     Standard, 3 at Huge, 1 at Compact (`WinUiSettingsLayout.GlanceColumns`); each opens its dial (the deep-link door: its arrival plays). Keyed
//     `settings:glance:<id>`;
//   · "Suggestions": at most three, from the hint service, as Informational InfoBars (closable, one action button), worded and wired as in the app; the
//     show is recorded when they draw (`SuggestionsShown`, the hint budget's). Keyed `settings:suggestion:<id>`; the section is absent with none;
//   · "Recently changed": the last five dials written from anywhere, as clickable cards (the value and where the write came from, when, a chevron), each
//     opening its dial. Keyed `settings:recent:<dialId>`.
// The column reflows (`MotionRecipes.CardResizeHeight`) when a suggestion closes, so the sections under it glide instead of jumping.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using HomeS = Wavee.Strings.Theme.Winui.Screens.Settings;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSettings
{
    sealed record HomeProps(SettingsPageProps Page);

    /// <summary>The home (see the file header). It reads the home's facts, so a dismissed hint, a dial written anywhere or the page epoch re-render it.</summary>
    sealed class HomePage : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var home = UseProps<HomeProps>().Page.Home;
            var facts = home.Read();
            int shown = SuggestionStamp(facts.Suggestions);
            UseEffect(() => { if (shown != 0) home.SuggestionsShown(); }, DepKey.From(shown));

            var kids = new List<Element>(16)
            {
                SectionHeader("glance", Loc.Get(HomeS.Glance), Loc.Get(HomeS.GlanceSub), first: true),
                GlanceGrid(facts.Glance, layout),
            };
            if (!facts.Suggestions.IsEmpty)
            {
                kids.Add(SectionHeader("suggestions", Loc.Get(HomeS.Suggestions), Loc.Get(HomeS.SuggestionsSub), first: false));
                foreach (var s in facts.Suggestions) kids.Add(Suggestion(s));
            }
            kids.Add(SectionHeader("recent", Loc.Get(HomeS.Recent), Loc.Get(HomeS.RecentSub), first: false));
            if (facts.Recent.IsEmpty)
                kids.Add(DialCard(NewCard("", null, Caption(Loc.Get(HomeS.RecentNone)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f })
                    with { Form = CardForm.Left }) with { Key = "settings:recent:none" });
            else
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in facts.Recent)
                    if (seen.Add(r.DialId)) kids.Add(RecentCard(r, now));
            }
            return new BoxEl
            {
                Key = ColumnKey, Direction = 1, Gap = WinUiSettingsRules.CardSpacing, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Layout = MotionRecipes.CardResizeHeight, Children = kids.ToArray(),
            };
        }
    }

    /// <summary>A fingerprint of the suggestions on show (their ids), so the show is recorded once per set, not per render.</summary>
    static int SuggestionStamp(System.Collections.Immutable.ImmutableArray<HintProps> suggestions)
    {
        if (suggestions.IsEmpty) return 0;
        var hash = new HashCode();
        foreach (var s in suggestions) hash.Add(s.Id);
        int stamp = hash.ToHashCode();
        return stamp == 0 ? 1 : stamp;
    }

    /// <summary>"At a glance": the six facts as clickable cards, the tier's columns per row.</summary>
    static Element GlanceGrid(System.Collections.Immutable.ImmutableArray<SettingsGlanceTile> glance, WinUiSettingsLayout layout)
    {
        var tiles = new Element[glance.Length];
        for (int i = 0; i < tiles.Length; i++)
        {
            var t = glance[i];
            tiles[i] = new BoxEl
            {
                Key = "settings:glance:" + t.Id, Direction = 1, MinWidth = 0f, Layout = WinUiScreenMotion.Reflow,   // a crossing re-flows the grid: the cards glide
                Children = [DialCard(NewCard(t.Title, t.Value) with { Glyph = Glyph(t.Glyph), OnClick = t.Open, WrapLabel = false, OneLineDescription = true })],
            };
        }
        var columns = new TrackSize[Math.Max(1, layout.GlanceColumns)];
        for (int i = 0; i < columns.Length; i++) columns[i] = TrackSize.Star();
        return new GridEl
        {
            Key = "settings:glance", Columns = columns, ColGap = WinUiSettingsRules.GlanceGap, RowGap = WinUiSettingsRules.GlanceGap, AlignSelf = FlexAlign.Stretch,
            Children = tiles,
        };
    }

    /// <summary>A suggestion as an Informational InfoBar: its line, its one action, its close (the hint's own snooze). After an inline write it is the
    /// confirmation with its "Change" link.</summary>
    static Element Suggestion(HintProps s)
    {
        Element? action = s.Done
            ? s.Change is { } change && s.ChangeLabel is { } changeLabel ? LinkButton(changeLabel, change) with { Key = "hint:" + s.Id + ":change" } : null
            : s.Act is { } act && s.ActionLabel is { } label ? StdButton(label, act) with { Key = "hint:" + s.Id + ":act" } : null;
        return InfoBar.Create(InfoBarSeverity.Informational, s.Done ? s.DoneText ?? s.Text : s.Text, "", onClose: s.Dismiss,
                isClosable: s.Dismiss is not null, actionButton: action)
            with { Key = "settings:suggestion:" + s.Id };
    }

    /// <summary>A Recently changed card: the dial's title, its value and where the write came from, when, and a chevron (a door to the dial).</summary>
    static Element RecentCard(SettingsRecentRow r, long now)
    {
        string what = r.Value.Length > 0 && r.From.Length > 0 ? r.Value + " · " + r.From : r.Value.Length > 0 ? r.Value : r.From;
        var card = NewCard(r.Title, what.Length > 0 ? what : null) with { OnClick = r.Open, Trailing = Ago(Math.Max(0L, now - r.AtMs)), WrapLabel = false, OneLineDescription = true };
        return new BoxEl { Key = "settings:recent:" + r.DialId, Direction = 1, MinWidth = 0f, Children = [DialCard(card)] };
    }

    /// <summary>"Just now", "2 min ago", "3 h ago", "2 d ago" (the notification centre's words).</summary>
    static string Ago(long ageMs)
    {
        var (unit, count) = Notify.RelativeAge(ageMs);
        return unit switch
        {
            RelativeUnit.Now => Loc.Get(Strings.Friends.Now),
            RelativeUnit.Minutes => Strings.Friends.MinAgo(count),
            RelativeUnit.Hours => Strings.Friends.HrAgo(count),
            _ => Strings.Friends.DAgo(count),
        };
    }
}
