// ── Themes/Zune/Structure/Views/Screens/ZuneSettings.Home.cs — the Settings home: "your setup", the suggestions, recently changed (settings rework phase 4) ─
//
// Board: the settings concept's Z-Home (Standard) and Z-Compact (the suggestion line under the pill; the drill-in summaries are `ZuneSettings.cs`'s). The facts are the
// core's (`SettingsPageProps.Home.Read()`: the six facts at a glance with their doors, the hint service's top three suggestions, the five recent writes); this file
// draws them in Zune's voices:
//   · your setup         the 40/48 heading, then label | value rows (the label in the body voice, the value in the 22/28 light voice), each value a door to its
//                        dial (its category opens and the arrival plays);
//   · suggestions        a 22/28 heading over one lowercase line per suggestion in the meta voice, its action an accent word and "not now" a quiet one (never a
//                        card, never a box: design 7.3). The page lays them out itself, so it reports their show once it draws them (`SuggestionsShown`);
//   · recently changed   a 22/28 heading over two-line rows: the dial's label over "its value · where from · when", each a door to the dial.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Z = Wavee.Themes.Zune.ZuneSettingsRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSettings
{
    /// <summary>The home's column (Small and up): "your setup", then the suggestions and the recent changes where there are any.</summary>
    static Element HomeColumn(ZuneKit k, SettingsHomeFacts home, ImmutableArray<SettingsCategoryEntry> cats)
    {
        var lay = k.Lay;
        var kids = new List<Element>(4) { SectionHeading(Loc.Get(Strings.Theme.Zune.Screens.SettingsYourSetup)) };

        var glance = new Element[home.Glance.Length];
        for (int i = 0; i < glance.Length; i++) glance[i] = GlanceRow(k, home.Glance[i]);
        kids.Add(new BoxEl
        {
            Key = "zs:glance", Direction = 1, Gap = Z.HomeRowGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Margin = new Edges4(0f, Z.HomeHeadGap, 0f, 0f),
            Children = glance,
        });

        if (home.Suggestions.Length > 0)
            kids.Add(HomeBlock("zs:home:suggestions", Loc.Get(Strings.Settings.Groups.Home.Suggestions),
                SuggestionsLines(k, SettingsHomeSource.Shared, home.Suggestions, "zs:suggest:")));

        if (home.Recent.Length > 0)
        {
            var rows = new Element[home.Recent.Length];
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (int i = 0; i < rows.Length; i++) rows[i] = RecentRow(k, home.Recent[i], now);
            kids.Add(HomeBlock("zs:home:recent", Loc.Get(Strings.Settings.Groups.Home.Recent),
                new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = rows }));
        }

        return new BoxEl
        {
            Key = "zs:column", Direction = 1, AlignSelf = FlexAlign.Start, MinWidth = 0f,
            Basis = lay.ColumnW, MaxWidth = lay.ColumnW, Grow = lay.ColumnGrows ? 1f : 0f, Shrink = 1f, Animate = ZuneMotion.ColumnCollapse,
            Children = kids.ToArray(),
        };
    }

    /// <summary>A block of the home: its 22/28 light heading over its content, the blocks' air above it.</summary>
    static Element HomeBlock(string key, string title, Element content) => new BoxEl
    {
        Key = key, Direction = 1, Gap = Z.HomeBlockHeadGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Margin = new Edges4(0f, Z.HomeBlockGap, 0f, 0f),
        Children = [ZuneText.Group(title), content],
    };

    /// <summary>One fact at a glance: the label | the value in the 22/28 light voice, the value a door to its dial. Stacked where the rows stack.</summary>
    static Element GlanceRow(ZuneKit k, SettingsGlanceTile tile)
    {
        var lay = k.Lay;
        bool stack = lay.StacksRows;
        Action open = tile.Open;
        return new BoxEl
        {
            Key = "zs:glance:" + tile.Id, Direction = (byte)(stack ? 1 : 0), Gap = stack ? Z.RowStackGap : Z.RowColumnGap, AlignItems = stack ? FlexAlign.Stretch : FlexAlign.Center,
            AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Shrink = 0f, MinWidth = 0f, Width = stack ? float.NaN : lay.LabelW,
                    Children = [ZuneText.Label(tile.Title) with { Color = Tok.TextSecondary }],
                },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, Shrink = 1f, Basis = stack ? float.NaN : 0f, MinWidth = 0f, MinHeight = lay.Target, AlignItems = FlexAlign.Center,
                    Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
                    Children = [ZuneText.Cased(R.Type.SheetTitle(tile.Value), LabelKind.Label) with { Color = Tok.TextPrimary, HoverColor = R.Ink.AccentDecor, MinWidth = 0f, Wrap = TextWrap.WrapWholeWords, Shrink = 1f }],
                }.Interactive(Interaction.Subtle),
            ],
        };
    }

    /// <summary>One recent write: the dial's label over "its value · from where · when" in the hint voice; a door to the dial while it is here.</summary>
    static Element RecentRow(ZuneKit k, SettingsRecentRow r, long now)
    {
        var parts = new List<string>(3);
        if (r.Value.Length > 0) parts.Add(r.Value);
        if (r.From.Length > 0) parts.Add(r.From);
        parts.Add(Ago(Math.Max(0, now - r.AtMs)));
        Action? open = r.Open;
        return new BoxEl
        {
            Key = "zs:recent:" + r.DialId, Direction = 1, Gap = Z.RecentLineGap, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = k.Lay.Target,
            Padding = new Edges4(0f, Z.RecentPadY, 0f, Z.RecentPadY),
            Role = open is null ? AutomationRole.None : AutomationRole.Hyperlink, Focusable = open is not null, Cursor = open is null ? (CursorId?)null : CursorId.Hand, OnClick = open,
            Children =
            [
                ZuneText.Label(r.Title) with { Color = Tok.TextPrimary, HoverColor = open is null ? Tok.TextPrimary : R.Ink.AccentDecor },
                ZuneText.Hint(string.Join(SummarySeparator, parts)),
            ],
        }.Interactive(Interaction.Subtle, open is not null);
    }

    /// <summary>The separator between the words of a summary line ("on · from the queue · today").</summary>
    const string SummarySeparator = " · ";

    /// <summary>"now", "2 min ago", "3 h ago", "2 d ago" (the notification centre's words).</summary>
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

    // ── suggestions ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The suggestions as Zune lines (one each): reports their show once drawn (the page lays them out itself, contract 1.116).</summary>
    static Element SuggestionsLines(ZuneKit k, SettingsHomeSource source, ImmutableArray<HintProps> suggestions, string keyPrefix)
        => Embed.Comp(new SuggestionsProps(k.Lay, source, suggestions, keyPrefix), static () => new SuggestionsView());

    sealed record SuggestionsProps(ZuneSettingsLayout Lay, SettingsHomeSource Source, ImmutableArray<HintProps> Suggestions, string KeyPrefix);

    /// <summary>The suggestions' lines; records their show on mount (the same once-a-day budget as a hint in the app).</summary>
    sealed class SuggestionsView : Component
    {
        public override Element Render()
        {
            var p = UseProps<SuggestionsProps>();
            var source = p.Source;
            UseEffect(() => source.SuggestionsShown(), DepKey.Empty);
            var k = new ZuneKit(p.Lay);
            var lines = new Element[p.Suggestions.Length];
            for (int i = 0; i < lines.Length; i++) lines[i] = SuggestionLine(k, p.Suggestions[i], p.KeyPrefix);
            return new BoxEl { Direction = 1, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, Children = lines };
        }
    }

    /// <summary>One suggestion as Zune writes a hint (design 7.3): a lowercase line in the meta voice, its action an accent word and "not now" a quiet one.</summary>
    static Element SuggestionLine(ZuneKit k, HintProps p, string keyPrefix)
    {
        var kids = new List<Element>(3)
        {
            ZuneText.Cased(R.Type.DenseMeta(p.Done ? p.DoneText ?? p.Text : p.Text), LabelKind.Label) with
            {
                Color = Tok.TextSecondary, Wrap = TextWrap.WrapWholeWords, MinWidth = 0f, Shrink = 1f, Margin = new Edges4(0f, 0f, Z.ControlGap, 0f),
            },
        };
        if (p.Done)
        {
            if (p.Change is { } change && p.ChangeLabel is { } changeLabel) kids.Add(k.Cmd(changeLabel, change, accent: true) with { Key = keyPrefix + p.Id + ":change" });
        }
        else
        {
            if (p.Act is { } act && p.ActionLabel is { } label) kids.Add(k.Cmd(label, act, accent: true) with { Key = keyPrefix + p.Id + ":act" });
            if (p.Dismiss is { } dismiss) kids.Add(k.Cmd(p.DismissLabel ?? Loc.Get(Strings.Hints.NotNow), dismiss) with { Key = keyPrefix + p.Id + ":dismiss" });
        }
        return new BoxEl
        {
            Key = keyPrefix + p.Id, Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Children = kids.ToArray(),
        };
    }
}
