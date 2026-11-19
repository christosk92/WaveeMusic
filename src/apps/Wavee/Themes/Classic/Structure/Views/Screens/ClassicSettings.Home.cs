// ── Themes/Classic/Structure/Views/Screens/ClassicSettings.Home.cs — the Settings home (settings rework 3.3, 7.1; board C-Home) ──────────────────────
//
// The home holds no controls (a second control for a dial would be a second home): state and doors, from `SettingsPageProps.Home.Read()`.
//   · At a glance   six clickable SettingsCards (the category's glyph, the label, the value at rest as the description, the chevron), in a grid of the tier's
//                   `GlanceColumns` (two at Standard, one at Compact); each opens its dial through the deep-link door, so its arrival plays.
//   · Suggestions   at most three, from the hint service (owner decision 14), as Classic's notice card (the toast card's plate): the sparkle, the line, its one
//                   action and its close. Drawing them records their show (`Home.SuggestionsShown()`, the same budget as the hint in the app).
//   · Recently changed  the last five dials written from any surface, as clickable rows ("Crossfade · On · from the queue", when).
// Each part is a group with its header (the same grammar as a category's), balanced over two columns at Huge. The grid's column count is a live prop of the
// SAME grid, and each tile glides into its new cell (`ClassicTierMotion.Reflow`), so a crossing re-flows the tiles instead of rebuilding them.

using System.Globalization;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

internal static partial class ClassicSettings
{
    /// <summary>The Settings home (see the file header): its own component, so a dismissed hint or a dial written elsewhere re-renders only the home.</summary>
    static Element HomeBody(SettingsPageProps props) => Embed.Comp(new HomeProps(props.Home, props.Revision), static () => new HomeView());

    sealed record HomeProps(SettingsHomeSource Home, int Revision);

    /// <summary>The grid's column tracks by count (one, two, three equal columns).</summary>
    static readonly TrackSize[][] s_glanceTracks =
    [
        [TrackSize.Star()],
        [TrackSize.Star(), TrackSize.Star()],
        [TrackSize.Star(), TrackSize.Star(), TrackSize.Star()],
    ];

    sealed class HomeView : SettingsComponent
    {
        public override Element Render()
        {
            var layout = UseLayout();
            var p = UseProps<HomeProps>();
            var home = p.Home;
            SettingsHomeFacts facts = home.Read();
            // Drawing the suggestions records their show (once per set: the budget counts a day's show, not a re-render).
            string shown = SuggestionIds(facts);
            UseEffect(() => { if (shown.Length > 0) home.SuggestionsShown(); }, DepKey.From(shown.GetHashCode()));

            var kids = new List<Element>(12)
            {
                SectionHeader(Loc.Get(Strings.Settings.Groups.Home.Glance), Icons.Home, Loc.Get(Strings.Theme.Classic.Settings.GlanceSub)),
                GlanceGrid(facts.Glance, layout),
            };
            if (facts.Suggestions.Length > 0)
            {
                kids.Add(SectionHeader(Loc.Get(Strings.Settings.Groups.Home.Suggestions), Icons.RefineSparkle, Loc.Get(Strings.Theme.Classic.Settings.SuggestionsSub)));
                foreach (HintProps hint in facts.Suggestions) kids.Add(NoticeCard(hint));
            }
            if (facts.Recent.Length > 0)
            {
                kids.Add(SectionHeader(Loc.Get(Strings.Settings.Groups.Home.Recent), Icons.History, Loc.Get(Strings.Theme.Classic.Settings.RecentSub)));
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                for (int i = 0; i < facts.Recent.Length; i++) kids.Add(RecentRow(facts.Recent[i], i, now));
            }
            return TabStack(kids.ToArray());
        }

        static string SuggestionIds(SettingsHomeFacts facts)
        {
            if (facts.Suggestions.Length == 0) return "";
            var ids = new string[facts.Suggestions.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = facts.Suggestions[i].Id;
            return string.Join('|', ids);
        }
    }

    /// <summary>The six tiles in the tier's grid; each tile is keyed by its fact and glides into its new cell on a crossing.</summary>
    static Element GlanceGrid(System.Collections.Immutable.ImmutableArray<SettingsGlanceTile> tiles, ClassicSettingsLayout layout)
    {
        var cells = new Element[tiles.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            SettingsGlanceTile tile = tiles[i];
            cells[i] = new BoxEl
            {
                Key = "settings:glance:" + tile.Id, Direction = 1, MinWidth = 0f, Animate = ClassicTierMotion.Reflow,
                Children =
                [
                    SettingsCard.Create(new SettingsCard.Options
                    {
                        Header = tile.Title,
                        Description = tile.Value,
                        HeaderIcon = Glyph(tile.Glyph),
                        IsClickEnabled = true,
                        IsActionIconVisible = true,
                        OnClick = tile.Open,
                        Style = SettingsCard.DefaultStyle with { MinHeight = ClassicSettingsRules.GlanceMinHeight },
                    }),
                ],
            };
        }
        int columns = Math.Clamp(layout.GlanceColumns, 1, s_glanceTracks.Length);
        return new GridEl
        {
            Columns = s_glanceTracks[columns - 1], ColGap = ClassicSettingsRules.CardSpacing, RowGap = ClassicSettingsRules.CardSpacing,
            AlignSelf = FlexAlign.Stretch, Children = cells,
        };
    }

    /// <summary>One suggestion as Classic's notice card (the toast card's plate): the accent sparkle, the line, its one action (a link: "Change quality") and
    /// its close; after an inline action, the confirmation and "Change". Keyed `settings:suggestion:&lt;id&gt;` (its parts `:act`, `:dismiss`, `:change`).</summary>
    static Element NoticeCard(HintProps hint)
    {
        string key = "settings:suggestion:" + hint.Id;
        var kids = new List<Element>(4)
        {
            Icon(Icons.RefineSparkle, ClassicSettingsRules.NoticeGlyph, Tok.AccentTextPrimary) with { Shrink = 0f },
            Body(hint.Done ? hint.DoneText ?? hint.Text : hint.Text) with { Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap },
        };
        if (hint.Done)
        {
            if (hint.Change is { } change && hint.ChangeLabel is { } changeLabel) kids.Add(LinkButton(changeLabel, change) with { Key = key + ":change" });
        }
        else if (hint.Act is { } act && hint.ActionLabel is { } label) kids.Add(LinkButton(label, act) with { Key = key + ":act" });
        if (hint.Dismiss is { } dismiss)
            kids.Add(ToolTip.Wrap(IconButton.Create(Icons.Cancel, dismiss, s_layout.Touch ? IconButton.DefaultStyle with { Size = s_layout.Target } : null)
                with { Key = key + ":dismiss", Shrink = 0f }, Loc.Get(Strings.Common.Close)));
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
            Padding = ClassicSettingsRules.NoticePadding, Corners = Radii.OverlayAll, Fill = Tok.FillSolidTertiary,
            BorderWidth = ClassicSettingsRules.NoticeBorder, BorderColor = Tok.StrokeSurfaceDefault,
            Children = kids.ToArray(),
        };
    }

    /// <summary>One Recently changed row: the dial's label, what it became and from where, and when; a click opens the dial (none for one that is not here now).</summary>
    static Element RecentRow(SettingsRecentRow recent, int index, long nowMs)
    {
        string what = recent.Value.Length > 0 && recent.From.Length > 0 ? recent.Value + " · " + recent.From : recent.Value + recent.From;
        return new BoxEl
        {
            Key = "settings:recent:" + index.ToString(CultureInfo.InvariantCulture), Direction = 1, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                SettingsCard.Create(new SettingsCard.Options
                {
                    Header = recent.Title,
                    Description = what.Length > 0 ? what : null,
                    Content = Caption(Ago(nowMs - recent.AtMs, recent.AtMs)) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap },
                    IsClickEnabled = recent.Open is not null,
                    IsActionIconVisible = recent.Open is not null,
                    OnClick = recent.Open,
                    Style = SettingsCard.DefaultStyle with { MinHeight = ClassicSettingsRules.RecentMinHeight },
                }),
            ],
        };
    }

    /// <summary>"2 min ago", "3 h ago", else the day it was.</summary>
    static string Ago(long agoMs, long atMs)
    {
        long minutes = Math.Max(1, agoMs / 60_000);
        if (minutes < 60) return Loc.Format("home.when.minAgo", ("n", minutes));
        long hours = minutes / 60;
        if (hours < 24) return Loc.Format("home.when.hAgo", ("n", hours));
        return DateTimeOffset.FromUnixTimeMilliseconds(atMs).ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }
}
