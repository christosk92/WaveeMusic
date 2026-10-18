// ── Themes/Zune/Structure/Views/Library/ZuneLibraryPage.Episodes.cs — the podcasts library's pinned "your episodes" row (Z-Library-Podcasts*) ─────
//
// Boards Z-Library-Podcasts (320), -Small (220), -Huge (340): the shows list's first row is "your episodes" with how many are saved, on a faint plate while it is the body; under it
// the "followed shows" label, then the shows. The feed (or the picked show's pane) is the column beside. Both bodies stay reachable: the row writes the host's mode, a show picked in
// the list switches back to its pane. The mode words of the old head are gone with this arm (the row IS the switch).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

internal static partial class ZuneLibraryPage
{
    /// <summary>The page draws the pinned row: the host published it (the split podcasts page) and no search replaces the columns.</summary>
    static bool HasEpisodes(LibraryPageProps p) => p.Episodes is not null && !p.Collapsed && !p.FullSearch;

    /// <summary>"your episodes" over its count, bleeding its plate 8 past the column as the board's does.</summary>
    static Element EpisodesRow(LibraryPageProps p, ZuneLibraryLayout l)
    {
        var e = p.Episodes!;
        return new BoxEl
        {
            Key = "lib:episodes:row", Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.SpaceBetween, Height = LR.EpisodesRowH, Shrink = 0f, MinWidth = 0f,
            Padding = new Edges4(LR.EpisodesRowPad, 0f, LR.EpisodesRowPad, 0f), Margin = new Edges4(-LR.EpisodesRowPad, 0f, -LR.EpisodesRowPad, 0f),
            Fill = Prop.Of(() => e.Active() ? Tok.FillSubtleTertiary : ColorF.Transparent), HoverFill = R.Layer.ChromeHover, HoverDurationMs = 0f, BrushTransitionMs = Design.Motion.Fast,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = e.Open,
            Children =
            [
                R.Type.TrackTitle(Loc.Get(Strings.Podcast.Reader.YourEpisodes)) with
                {
                    Color = Tok.TextPrimary, Weight = LR.EpisodesLabelWeight, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 1f, MinWidth = 0f,
                },
                R.Type.MicroMeta("") with
                {
                    Text = Prop.Of(() => e.Count.Current() is var n && n > 0 ? FormatCache.Int(n) : ""), Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                },
            ],
        };
    }

    static Element ShowsLabel() => new BoxEl
    {
        Key = "lib:episodes:label", Direction = 0, AlignItems = FlexAlign.Center, Height = LR.EpisodesRowH, Shrink = 0f, MinWidth = 0f,
        Children =
        [
            R.Type.MicroMeta(Loc.Get(Strings.Podcast.Reader.FollowedShows)) with
            {
                Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
        ],
    };

    /// <summary>Your Episodes as the right column: the "your episodes" heading (26 light) and the saved count, over the host's feed.</summary>
    static Element EpisodesFeed(LibraryPageProps p, ZuneLibraryLayout l)
    {
        var e = p.Episodes!;
        return Section with
        {
            Key = "lib:episodes:feed", Grow = 1f, Basis = 0f, MinWidth = 0f, Animate = Design.Reveal.Resize,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.End, Gap = LR.GroupHeadingGap, MinWidth = 0f, Shrink = 0f, Height = LR.GroupHeadingH,
                    Padding = new Edges4(0f, 0f, 0f, LR.GroupHeadingPadBottom),
                    Children =
                    [
                        R.Type.QuickplayHead(Loc.Get(Strings.Podcast.Reader.YourEpisodes)) with
                        {
                            Size = LR.BoardHeadSize, LineHeight = LR.BoardHeadLine, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label),
                        },
                        R.Type.DenseMeta("") with
                        {
                            Text = Prop.Of(() => e.Count.Current() is var n && n > 0 ? Strings.Podcast.EpisodeCount(n) : ""), Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                            Case = R.Case.For(LabelKind.Label),
                        },
                    ],
                },
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Children = [p.SavedEpisodes()] },
            ],
        };
    }
}
