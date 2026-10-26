// ── Themes/Zune/Structure/Views/Library/ZuneLibraryEpisodesFeed.cs — the podcasts library's Your Episodes feed (Z-Library-Podcasts) ───────────────
//
// Beside the pinned "your episodes" row: the saved episodes as a feed grouped by when they came out (today · this week · earlier), each a 56 cover, the title over "show · length"
// and where it stands at the end ("12 min left" in the accent, "unplayed", "✓ played"), over a head of "N saved · 6 hr 12 min" and the four filter words (all · unplayed · in progress ·
// played). A row plays (toggle) the episode; its "…" opens the episode's page. The rows are the host's plain `LibraryEpisodesFeed` (reactive); the filter is this view's own state.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's Your Episodes feed.</summary>
internal static class ZuneLibraryEpisodesFeed
{
    public static Element Body(LibrarySavedEpisodesProps props)
        => Embed.Comp(props, static () => new FeedView()) with { Key = "lib:episodes:feedview" };

    sealed class FeedView : Component
    {
        readonly Signal<int> _filter = new(0);

        public override Element Render()
        {
            var p = UseProps<LibrarySavedEpisodesProps>();
            var feed = p.Feed!();
            // Nothing saved yet: the host's own content (the quiet "empty" line) says so.
            if (feed.Rows.Count == 0) return p.Content();
            int filter = _filter.Value;
            var shown = new List<LibraryEpisodeRow>(feed.Rows.Count);
            foreach (var r in feed.Rows) if (filter == 0 || r.Status == (filter == 1 ? 0 : filter == 2 ? 1 : 2)) shown.Add(r);

            var kids = new List<Element>(shown.Count + 3);
            for (int bucket = 0; bucket < 3; bucket++)
            {
                int start = kids.Count;
                int count = 0;
                foreach (var r in shown) if (r.Bucket == bucket) count++;
                if (count == 0) continue;
                kids.Add(Heading(bucket, count));
                foreach (var r in shown) if (r.Bucket == bucket) kids.Add(Row(r, p));
            }
            if (shown.Count == 0)
                kids.Add(new BoxEl { Padding = new Edges4(0f, LR.FeedGroupTop, 0f, 0f), Children = [R.Type.DenseMeta(Loc.Get(Strings.Podcast.NoEpisodes)) with { Color = Tok.TextTertiary }] });

            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f,
                Children =
                [
                    Head(feed, filter),
                    new ScrollEl
                    {
                        Grow = 1f, MinHeight = 0f, SuppressScrollBar = true, ScrollKey = "lib:episodes:feed:scroll",
                        Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = kids.ToArray() },
                    },
                ],
            };
        }

        Element Head(LibraryEpisodesFeed feed, int filter)
        {
            long minutes = feed.TotalMs / 60_000L;
            string time = minutes >= 60 ? Strings.Detail.DurationHrMin(minutes / 60, minutes % 60) : Strings.Detail.DurationMin(Math.Max(1, minutes));
            string[] words = [Strings.Podcast.Filter.All, Strings.Podcast.Filter.Unplayed, Strings.Podcast.Filter.InProgress, Strings.Podcast.Filter.Played];
            var kids = new Element[words.Length];
            for (int i = 0; i < words.Length; i++)
            {
                int code = i;
                kids[i] = new BoxEl
                {
                    Key = "lib:episodes:filter:" + i, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Height = LR.HeadH,
                    // The last word stands on the page's edge, whose clip cuts its touch slop there: it reaches left, into the gap, instead.
                    HitSlop = i == words.Length - 1 ? Wavee.Theming.TouchTargetPolicy.OneSide(LR.HeadH, left: true) : default,
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => _filter.Value = code,
                    Children = [ZuneLibraryKit.WordText(Loc.Get(words[i]), active: i == filter)],
                };
            }
            return new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.WordGap, MinWidth = 0f, Shrink = 0f, Height = LR.HeadH,
                Children =
                [
                    R.Type.DenseMeta(Strings.Theme.Zune.Collection.Library.SavedMeta(feed.Rows.Count, time)) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f, Case = R.Case.For(LabelKind.Label),
                    },
                    new BoxEl { Grow = 1f, MinWidth = 0f },
                    .. kids,
                ],
            };
        }

        static Element Heading(int bucket, int count) => new BoxEl
        {
            Key = "lib:episodes:group:" + bucket, Direction = 0, AlignItems = FlexAlign.End, Gap = LR.GroupHeadingGap, MinWidth = 0f, Shrink = 0f, Height = LR.FeedGroupH,
            Padding = new Edges4(0f, LR.FeedGroupTop, 0f, 0f),
            Children =
            [
                R.Type.QuickplayHead(Loc.Get(bucket == 0 ? Strings.Theme.Zune.Collection.Library.FeedToday : bucket == 1 ? Strings.Theme.Zune.Collection.Library.FeedWeek : Strings.Theme.Zune.Collection.Library.FeedEarlier)) with
                {
                    Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label),
                },
                R.Type.MicroMeta(FormatCache.Int(count)) with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap },
            ],
        };

        static Element Row(LibraryEpisodeRow r, LibrarySavedEpisodesProps p)
        {
            bool lit = r.Playing || r.Status == 1;
            string line = r.Length.Length > 0 ? (r.Show.Length > 0 ? r.Show + " · " + r.Length : r.Length) : r.Show;
            TextEl status = r.Status switch
            {
                1 => R.Type.DenseMeta(r.Left) with { Color = Tok.AccentTextPrimary },
                2 => R.Type.DenseMeta("✓ " + Loc.Get(Strings.Podcast.Filter.Played)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) },
                _ => R.Type.DenseMeta(Loc.Get(Strings.Podcast.Filter.Unplayed)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) },
            };
            string uri = r.Uri, title = r.Title;
            return new BoxEl
            {
                Key = "lib:episodes:row:" + uri, Direction = 0, AlignItems = FlexAlign.Center, Gap = LR.FeedRowGap, Height = LR.FeedRowH, Shrink = 0f, MinWidth = 0f,
                HoverFill = R.Layer.ChromeHover, HoverDurationMs = 0f, Corners = R.Shape.All(ShapeRole.Cover),
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => p.PlayEpisode?.Invoke(uri),
                Children =
                [
                    Controls.Artwork(r.Cover.Length > 0 ? r.Cover : null, LR.FeedArt, LR.FeedArt, R.Shape.For(ShapeRole.Cover), decodePx: LR.FeedArtDecodePx),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f,
                        Children =
                        [
                            R.Type.TrackTitle(title) with { Color = lit ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            R.Type.DenseMeta(line) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        ],
                    },
                    status with { MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f },
                    new BoxEl
                    {
                        Width = LR.FeedMore, Height = LR.FeedMore, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        // The row's end is the feed's clipped edge: the "⋯"'s touch slop reaches left instead.
                        HitSlop = Wavee.Theming.TouchTargetPolicy.OneSide(LR.FeedMore, left: true),
                        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, OnClick = () => p.OpenEpisode?.Invoke(uri, title),
                        Children = [Icon(Icons.More, LR.ViewMoreGlyph, Tok.TextSecondary)],
                    },
                ],
            };
        }
    }
}
