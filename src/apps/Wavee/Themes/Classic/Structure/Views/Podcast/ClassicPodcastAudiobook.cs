// ── Themes/Classic/Structure/Views/Podcast/ClassicPodcastAudiobook.cs — Classic's audiobook page (podcast pages v2, concept "Audiobook", theme=classic) ───
//
// "The audiobook is the album page proper" (the concept's Classic note):
//   ClassicPodcastKit.Page
//   ├─ the cover column: cover · Audiobook (its Browse category) [Included in Premium] · title · subtitle · By … / Narrated by … (links where they have a
//   │    page) · ★ rating (count) · length · chapters · year · the full-width Continue in the cover's colour (Pause while it plays; Start listening, Listen
//   │    again) · where you are (Chapter 9 of 19 · name over the whole book's bar and "46% · 4 h 08 min left", live) · Sample (not started) · speed · sleep
//   │    timer · the hours notice (Included in Premium · 12 h a month, renews … · Check hours left · Top up hours) or the price and Buy · Your Library ·
//   │    Download · Share · ⋯
//   └─ the list column: the whole book (every chapter a segment drawn to its length, your place on it) · the contents (a #, Title, starts at table with
//        dot leaders, the current chapter's time left) · About this book (rich text, Read more) beside its facts and rating · More by the author
//
// Numbers: `ClassicPodcastRules`, the tier record `ClassicPodcastLayout`.

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using P = Wavee.Themes.Classic.ClassicPodcastRules;

namespace Wavee.Themes.Classic;

/// <summary>Classic's audiobook page.</summary>
internal static class ClassicPodcastAudiobook
{
    public static Element Template(AudiobookPageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, Children = [Embed.Comp(p, static () => new PageHost()) with { Key = "cpa-page" }] };

    sealed record Args(AudiobookPageProps P, AudiobookView V, ClassicPodcastLayout Lay, ColorF Accent, bool Playing);

    sealed class PageHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<AudiobookPageProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ClassicPodcastTiers.Of(in facts);
            var live = p.View.Value;
            bool loading = live.Load == PodcastLoad.Pending;
            var v = loading ? AudiobookView.Placeholder : live;
            bool playing = p.Playing.Value;
            uint tone = loading ? 0u : v.Tone;
            ColorF accent = ClassicPodcastKit.Accent(tone);
            var a = new Args(p, v, lay, accent, playing);
            var seed = a with { V = AudiobookView.Placeholder };
            Element column = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: () => p.View.Value.Load == PodcastLoad.Failed,
                Content: () => Column(a), ShimmerSource: () => Column(seed),
                OnFailed: () => Controls.Vacancy(Controls.VacancyVoice.Error), Reveal: SkelReveal.Soft,
                Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: true);
            Element main = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: static () => false,
                Content: () => MainColumn(a), ShimmerSource: () => MainColumn(seed), OnFailed: null, Reveal: SkelReveal.Soft,
                Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: true);
            return ClassicPodcastKit.Page(p.RouteKey, lay, tone, column, main);
        }
    }

    // ══ THE COLUMN ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Column(Args a)
    {
        var v = a.V;
        var p = a.P;
        var lay = a.Lay;
        var kids = new List<Element>(10)
        {
            new BoxEl
            {
                Key = "cpa-cover", Shrink = 0f, AlignSelf = FlexAlign.Start, Layout = ClassicTierMotion.Reflow,
                Children = [Controls.Artwork(v.ArtUrl, lay.Cover, lay.Cover, P.CoverRadius, decodePx: P.CoverDecode)],
            },
        };
        var tags = v.Included && v.IncludedTag.Length > 0 ? new[] { ClassicPodcastKit.Tag(v.IncludedTag, null, Design.Palette.ReadableInk(a.Accent)) } : [];
        string? route = v.KindRoute;
        var words = new List<Element>(6)
        {
            ClassicPodcastKit.Kind(v.Kind, route is { Length: > 0 } ? () => p.OpenRoute(route) : null, tags),
            ClassicPodcastKit.Title(v.Title),
        };
        if (v.Subtitle.Length > 0) words.Add(ClassicPodcastKit.Sub(v.Subtitle));
        var credits = new List<TextSpan>(8);
        Credit(credits, v.ByLabel, v.Authors, p.OpenUri);
        if (v.Authors.Length > 0 && v.Narrators.Length > 0) credits.Add(new TextSpan("\n"));
        Credit(credits, v.NarratedByLabel, v.Narrators, p.OpenUri);
        if (credits.Count > 0) words.Add(ClassicPodcastKit.Line([.. credits]) with { Key = "cpa-credits" });
        var meta = new List<TextSpan>(4);
        if (v.Rating.Length > 0)
        {
            meta.Add(ClassicPodcastKit.Strong(v.Rating));
            meta.Add(new TextSpan(" ★" + (v.RatingCount.Length > 0 ? " " + v.RatingCount : "")));
        }
        if (v.Meta.Length > 0) meta.Add(new TextSpan((meta.Count > 0 ? P.Dot : "") + string.Join(P.Dot, v.Meta)));
        if (meta.Count > 0) words.Add(ClassicPodcastKit.Line([.. meta]));
        kids.Add(new BoxEl { Key = "cpa-id", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Children = words.ToArray() });
        kids.Add(Up(a));
        if (Notice(a) is { } notice) kids.Add(notice);
        kids.Add(Commands(a));
        return new BoxEl { Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    static void Credit(List<TextSpan> spans, string label, ImmutableArray<AudiobookPersonVm> people, Action<string> open)
    {
        if (people.Length == 0) return;
        spans.Add(new TextSpan(label + " "));
        for (int i = 0; i < people.Length; i++)
        {
            if (i > 0) spans.Add(new TextSpan(", ", Weight: P.Strong, Color: Tok.TextPrimary));
            string uri = people[i].Uri;
            spans.Add(ClassicPodcastKit.Strong(people[i].Name, uri.Length > 0 ? () => open(uri) : null));
        }
    }

    static Element Up(Args a)
    {
        var v = a.V;
        var p = a.P;
        var lay = a.Lay;
        string glyph = a.Playing ? Icons.Pause : v.State == AudiobookState.Finished ? Icons.Refresh : Icons.Play;
        string label = a.Playing ? Loc.Get(Strings.Podcast.Pages.Pause) : v.PrimaryLabel;
        var kids = new List<Element>(4) { ClassicPodcastKit.Primary("cpa-primary", label, glyph, a.Accent, v.PrimaryAccent, p.Play, [], lay.Target) };
        // Where you are: "Chapter 9 of 19 · name" over the whole book's bar and its words (live), or what Start listening plays.
        Element where;
        if (v.State == AudiobookState.InProgress)
        {
            var spans = v.UpName.Length > 0
                ? new TextSpan[] { new(v.UpTitle, Color: Tok.TextSecondary), new(v.UpName, Weight: P.Strong, Color: Tok.TextPrimary) }
                : [new(v.UpTitle, Weight: P.Strong, Color: Tok.TextPrimary)];
            var place = p.Place;
            ColorF accent = a.Accent;
            where = new BoxEl
            {
                Key = "cpa-place", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f,
                Children =
                [
                    new SpanTextEl(spans) { Size = P.SubSize, LineHeight = P.SubLine, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                    new BoxEl
                    {
                        Direction = 0, Gap = P.UpSubGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                        Children =
                        [
                            ClassicPodcastKit.LiveBar(() => place.Value.Fraction, accent) with { Width = P.UpBarW, Shrink = 0f },
                            new TextEl("") { Text = Prop.Of(() => place.Value.BookLeft), Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextSecondary,
                                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f },
                        ],
                    },
                ],
            };
        }
        else
            where = ClassicPodcastKit.UpLabel("", v.UpTitle, v.UpSub, -1f, a.Accent);
        // The label changes form when the listener's place lands after the page (a length over the chapter count, then a chapter's name over the
        // book's bar): its slot eases to the new height and what stands under it rides the difference, instead of stepping down a line.
        kids.Add(new BoxEl { Key = "cpa-where", Direction = 1, MinWidth = 0f, Layout = ClassicTierMotion.Resize, Children = [where] });
        var tools = new List<Element>(3);
        if (v.State == AudiobookState.NotStarted && v.SampleId is not null)
            tools.Add(Button.Create(v.SampleButton, p.PlaySample, glyph: Icons.Play) with { Key = "cpa-sample", Height = lay.Target, Shrink = 0f, MinWidth = 0f });
        tools.Add(Embed.Comp(p, static () => new SpeedHost()) with { Key = "cpa-speed" });
        tools.Add(Embed.Comp(p, static () => new SleepHost()) with { Key = "cpa-sleep" });
        kids.Add(new BoxEl { Key = "cpa-tools", Direction = 0, Wrap = true, Gap = P.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = tools.ToArray() });
        return new BoxEl { Key = "cpa-up", Direction = 1, Gap = P.UpGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    sealed class SpeedHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<AudiobookPageProps>();
            string label = p.SpeedLabel.Value;
            var items = new List<MenuFlyoutItem>();
            foreach (var m in p.SpeedMenu()) items.Add(MenuFlyoutItem.RadioItem(m.Label, m.Checked, m.Invoke));
            return ToolTip.Wrap(DropDownButton.Create(label, items), Loc.Get(Strings.Podcast.Pages.Speed));
        }
    }

    sealed class SleepHost : Component
    {
        public override Element Render()
        {
            var p = UseProps<AudiobookPageProps>();
            string label = p.SleepLabel.Value;
            var items = new List<MenuFlyoutItem>();
            foreach (var m in p.SleepMenu()) items.Add(MenuFlyoutItem.RadioItem(m.Label, m.Checked, m.Invoke));
            return ToolTip.Wrap(DropDownButton.Create(label, items, glyph: Icons.Moon), Loc.Get(Strings.Podcast.Pages.SleepTimer));
        }
    }

    /// <summary>The hours notice (Included in Premium · 12 h a month, renews 12 Dec, then Check hours left and Top up hours, both to the browser), or the
    /// price pair and Buy for a book on sale.</summary>
    static Element? Notice(Args a)
    {
        var v = a.V;
        var p = a.P;
        var kids = new List<Element>(4);
        if (v.Included)
        {
            if (v.PremiumLine.Length == 0) return null;
            kids.Add(new BoxEl
            {
                Direction = 0, Gap = P.NoticeGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
                Children =
                [
                    Icon(Icons.Headphones, P.NoticeGlyph, Tok.TextSecondary),
                    new TextEl(v.PremiumLine) { Size = P.LineSize, LineHeight = P.LineHeight, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, Grow = 1f, Basis = 0f, MinWidth = 0f },
                ],
            });
            kids.Add(new BoxEl
            {
                Direction = 0, Wrap = true, Gap = P.FactsRowGap, MinWidth = 0f, Margin = new Edges4(P.NoticeGlyph + P.NoticeGap - P.TagPadX, 0f, 0f, 0f),
                Children = [HyperlinkButton.Create(v.CheckHoursLabel, p.CheckHours, size: ControlSize.Small) with { Key = "cpa-hours" },
                    HyperlinkButton.Create(v.TopUpLabel, p.TopUp, size: ControlSize.Small) with { Key = "cpa-topup" }],
            });
        }
        else
        {
            var price = new List<TextSpan>(2);
            if (v.Price.Length > 0) price.Add(ClassicPodcastKit.Strong(v.Price));
            if (v.ListPrice.Length > 0) price.Add(new TextSpan((price.Count > 0 ? "  " : "") + v.ListPrice, Color: Tok.TextTertiary));
            if (price.Count > 0) kids.Add(ClassicPodcastKit.Line([.. price]));
            if (v.BuyLabel.Length > 0 && p.Buy is { } buy) kids.Add(Button.Create(v.BuyLabel, buy) with { Key = "cpa-buy", Height = a.Lay.Target, Shrink = 0f, AlignSelf = FlexAlign.Start });
            if (kids.Count == 0) return null;
        }
        return new BoxEl
        {
            Key = "cpa-notice", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Padding = Edges4.All(P.NoticePad), Corners = Radii.ControlAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Children = kids.ToArray(),
        };
    }

    static Element Commands(Args a)
    {
        var v = a.V;
        var p = a.P;
        var lay = a.Lay;
        // The glyph buttons stay together as one group; where the column cannot hold the library button's words beside them, the group wraps to the
        // line under it and the button spans the column. The label is never cut (no shrink).
        var glyphs = new List<Element>(3) { ClassicPodcastKit.Glyph("cpa-download", Icons.Download, Loc.Get(Strings.Podcast.Pages.Download), p.Download, lay.Target) };
        if (p.Share is { } share) glyphs.Add(ClassicPodcastKit.Glyph("cpa-share", Icons.Share, Loc.Get(Strings.Podcast.Pages.Share), share, lay.Target));
        glyphs.Add(new BoxEl { Key = "cpa-more", Shrink = 0f, Children = [ClassicDetailKit.MoreButton(p.Menu, lay.Target)] });
        return new BoxEl
        {
            Key = "cpa-commands", Direction = 0, Wrap = true, Gap = P.CommandGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                Button.Create(v.LibraryLabel, p.ToggleLibrary, glyph: v.InLibrary ? Icons.Check : Icons.Add) with
                {
                    Key = "cpa-library", Height = lay.Target, Shrink = 0f, Grow = 1f, Justify = FlexJustify.Start,
                },
                new BoxEl { Key = "cpa-glyphs", Direction = 0, Gap = P.CommandGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = glyphs.ToArray() },
            ],
        };
    }

    // ══ THE LIST COLUMN ═════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element MainColumn(Args a)
    {
        var view = a.P.View;
        // What stands under the contents has no place of its own until something says how many chapters there are. It is held back (the loading
        // page's own placeholder) and drawn when the count is known, where it will stay: content a listener can read never moves.
        Element tail = new SkelRegionEl(
            Pending: () => view.Value.ChaptersUnsized, Failed: static () => false,
            Content: () => Tail(a with { V = view.Value }), ShimmerSource: () => Tail(a with { V = AudiobookView.Placeholder }), OnFailed: null,
            Reveal: SkelReveal.Soft,
            Style: SkeletonStyle.Default with { DeriveComponents = true }, Group: null, SmoothResize: true);
        return new BoxEl
        {
            Key = "cpa-main", Direction = 1, Gap = 0f, MinWidth = 0f, Padding = new Edges4(0f, a.Lay.Stacked ? 0f : P.ColumnPadTop, 0f, 0f),
            Children = [Line(a), Contents(a), tail],
        };
    }

    /// <summary>Under the contents: about the book, then the similar books.</summary>
    static Element Tail(Args a)
    {
        var v = a.V;
        var kids = new List<Element>(4) { ClassicPodcastKit.SectionHeader("cpa-about-head", v.AboutTitle), About(a) };
        if (!v.Similar.IsDefaultOrEmpty)
        {
            kids.Add(ClassicPodcastKit.SectionHeader("cpa-similar-head", v.SimilarTitle));
            kids.Add(ClassicPodcastKit.MoreLikeThis(v.Similar, a.P.OpenUri, a.Lay.Stacked, a.P.SimilarMenu, a.P.PlaySimilar));
        }
        return new BoxEl { Key = "cpa-tail", Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The whole book: its head ("The whole book · 19 chapters, drawn to length" … "3 h in · 4 h left") over the segments.</summary>
    static Element Line(Args a)
    {
        var v = a.V;
        var p = a.P;
        var place = p.Place;
        bool progress = v.State == AudiobookState.InProgress;
        TextEl right = progress
            ? new TextEl("") { Text = Prop.Of(() => place.Value.InLeft), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextSecondary, Shrink = 0f }
            : Caption(v.LineRight) with { Color = Tok.TextSecondary, Shrink = 0f };
        var head = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = P.TocGap, MinWidth = 0f,
            Children =
            [
                new SpanTextEl(new TextSpan[] { new(v.LineTitle, Weight: P.Strong, Color: Tok.TextPrimary), new(" " + v.LineSub, Color: Tok.TextTertiary) })
                    { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f },
                right,
            ],
        };
        var view = p.View;
        // Drawn to length once every length is known (the chapters land before their lengths: segments cut evenly would be re-cut under the eye).
        // Until then the band stands empty, and the segments fade in where they belong.
        Element segments = new SkelRegionEl(
            Pending: () => !view.Value.LineKnown, Failed: static () => false,
            Content: () => SegmentsOf(a with { V = view.Value }), ShimmerSource: static () => new BoxEl { Height = P.BookSegBand }, OnFailed: null,
            Reveal: SkelReveal.FadeOnly, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
        return ClassicPodcastEpisodeParts.Card("cpa-line", [head, segments], P.MapGap, new Edges4(P.MapPadX, P.MapPadTop, P.MapPadX, P.MapPadBottom));
    }

    static Element SegmentsOf(Args a)
    {
        var v = a.V;
        var chapters = v.Chapters;
        var place = a.P.Place;
        var play = a.P.PlayChapter;
        bool progress = v.State == AudiobookState.InProgress, finished = v.State == AudiobookState.Finished;
        bool seek = !a.Lay.Stacked;
        ColorF accent = a.Accent;
        return Responsive.Of(w => Segments(w, chapters, place, play, progress, finished, seek, accent));
    }

    static Element Segments(float width, ImmutableArray<AudiobookChapterVm> chapters, IReadSignal<AudiobookPlace> place, Action<int> play, bool progress,
        bool finished, bool seek, ColorF accent)
    {
        int n = chapters.Length;
        if (n == 0 || width <= 0f) return new BoxEl { Height = P.BookSegBand };
        long total = 0;
        foreach (var c in chapters) total += Math.Max(1, c.LengthMs);
        float avail = Math.Max(0f, width - P.BookSegGap * (n - 1));
        var widths = new float[n];
        float sum = 0f;
        for (int i = 0; i < n; i++) { widths[i] = Math.Max(P.BookSegMin, avail * Math.Max(1, chapters[i].LengthMs) / total); sum += widths[i]; }
        float scale = sum > avail && sum > 0f ? avail / sum : 1f;
        var starts = new float[n];
        float x = 0f;
        var kids = new Element[n];
        for (int i = 0; i < n; i++)
        {
            widths[i] *= scale;
            starts[i] = x;
            x += widths[i] + P.BookSegGap;
            int index = i;
            float segW = widths[i];
            bool done = chapters[i].Done;
            var fill = new BoxEl
            {
                Width = Prop.Of(() =>
                {
                    var pl = place.Value;
                    float f = finished || done ? 1f : pl.Current == index ? pl.CurrentFill : 0f;
                    return segW * f;
                }),
                Height = P.BookSegH, Corners = CornerRadius4.All(P.SegRadius), Fill = finished ? Tok.FillControlStrong : accent,
            };
            var seg = new BoxEl
            {
                Key = "cpa-seg:" + i, Width = segW, Height = P.BookSegBand, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center, Corners = CornerRadius4.All(P.SegRadius),
                Children = [new BoxEl { Width = segW, Height = P.BookSegH, ZStack = true, Corners = CornerRadius4.All(P.SegRadius), ClipToBounds = true,
                    Fill = Tok.TextPrimary with { A = P.BookSegTrackAlpha }, Children = [fill] }],
            };
            kids[i] = ToolTip.Wrap(seek
                ? seg with
                {
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => play(index),
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                }
                : seg with { HitTestVisible = false }, chapters[i].Label);
        }
        var thumb = new BoxEl
        {
            Width = P.BookThumbOuter, Height = P.BookThumbOuter, HitTestVisible = false, AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
            Corners = CornerRadius4.All(P.BookThumbOuter / 2f), Fill = Tok.FillControlSolid, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Visible = Prop.Of(() => progress && place.Value.Current >= 0),
            Transform = Prop.Of(() =>
            {
                var pl = place.Value;
                int c = Math.Clamp(pl.Current, 0, n - 1);
                return Affine2D.Translation(starts[c] + widths[c] * pl.CurrentFill - P.BookThumbOuter / 2f, 0f);
            }),
            Children = [new BoxEl { Width = P.BookThumbInner, Height = P.BookThumbInner, Corners = CornerRadius4.All(P.BookThumbInner / 2f), Fill = accent }],
        };
        return new BoxEl
        {
            ZStack = true, Height = P.BookSegBand,
            Children = [new BoxEl { Direction = 0, Gap = P.BookSegGap, Height = P.BookSegBand, Children = kids }, thumb],
        };
    }

    /// <summary>The contents: a #, Title, starts at table; dot leaders toward the start; the current chapter's own time left; a click plays the chapter.</summary>
    static Element Contents(Args a)
    {
        var v = a.V;
        var head = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = P.TocGap, MinWidth = 0f, Padding = new Edges4(P.ChPadX, 0f, P.ChPadX, P.TocHeadPadBottom),
            Children =
            [
                Caption("#") with { Width = P.TocNum, Shrink = 0f, TextAlign = TextAlign.Center, Color = Tok.TextTertiary },
                new SpanTextEl(new TextSpan[] { new(v.ContentsTitle, Weight: P.Strong, Color: Tok.TextPrimary), new("  " + v.ContentsSub, Color: Tok.TextTertiary) })
                    { Size = Caption("").Size, LineHeight = Caption("").LineHeight, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f },
                Caption(v.StartsAtLabel) with { Color = Tok.TextTertiary, Shrink = 0f },
            ],
        };
        var view = a.P.View;
        // The rows do not hold the page. While they are on their way their place is held by as many placeholder rows as the book has chapters (the
        // loading page's twelve until something says how many), and they arrive in it one behind the other.
        // A region's own run can come before the page's re-render hands it new closures, so what it mounts reads the view as it is NOW: rows that
        // arrive are there when the reveal looks for them.
        Element rows = new SkelRegionEl(
            Pending: () => view.Value.ChaptersPending, Failed: static () => false,
            Content: () => Rows(a with { V = view.Value }, view.Value.Chapters), ShimmerSource: () => Rows(a, view.Value.HeldChapters()), OnFailed: null,
            Reveal: SkelReveal.StaggerRows, Style: SkeletonStyle.Default, Group: null, SmoothResize: true);
        return new BoxEl
        {
            Key = "cpa-contents", Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, P.SectionTop, 0f, 0f),
            Children = [head, new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault, Margin = new Edges4(0f, 0f, 0f, P.FactsRowGap) }, rows],
        };
    }

    static Element Rows(Args a, ImmutableArray<AudiobookChapterVm> chapters)
    {
        var kids = new Element[chapters.Length];
        for (int i = 0; i < kids.Length; i++) kids[i] = Row(chapters[i], a);
        return new BoxEl { Key = "cpa-rows", Direction = 1, MinWidth = 0f, Children = kids };
    }

    static Element Row(AudiobookChapterVm c, Args a)
    {
        var v = a.V;
        bool cur = v.Current == c.Index && v.State == AudiobookState.InProgress;
        bool done = c.Done || v.State == AudiobookState.Finished;
        var place = a.P.Place;
        int index = c.Index;
        ColorF ink = Design.Palette.ReadableInk(a.Accent);
        var number = Body(c.Number) with { Width = P.TocNum, Shrink = 0f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.Center, Color = cur ? ink : Tok.TextTertiary };
        var name = Body(c.Name) with
        {
            Weight = cur ? P.Strong : Body("").Weight, Color = cur ? Tok.TextPrimary : done || c.Matter ? Tok.TextSecondary : Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        // The dots lead to the chapter's start: there are none until it is known (a chapter's name lands before its place in the book).
        var leader = new BoxEl
        {
            Height = 1f, Grow = 1f, MinWidth = P.TocGap, AlignSelf = FlexAlign.Center, BorderWidth = c.StartsAt.Length > 0 ? 1f : 0f, BorderDashOn = 1f,
            BorderDashOff = P.LedgerGap + 1f,
            BorderColor = Tok.TextPrimary with { A = done ? P.LedgerTrackAlpha : P.LedgerTrackAlpha + P.LedgerTrackAlpha },
        };
        var left = new TextEl("")
        {
            Text = cur ? Prop.Of(() => place.Value.Current == index ? place.Value.CurrentLeft : "") : "",
            Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = ink, Shrink = 0f, Wrap = TextWrap.NoWrap,
        };
        var starts = Body(c.StartsAt) with
        {
            Width = P.TocLen, Shrink = 0f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End, Color = cur ? Tok.TextPrimary : done ? Tok.TextTertiary : Tok.TextSecondary,
        };
        return new BoxEl
        {
            Key = "cpa-ch:" + c.Index, Direction = 0, AlignItems = FlexAlign.Center, Gap = P.TocGap, MinHeight = a.Lay.Stacked ? P.TocRowHCompact : P.TocRowH, MinWidth = 0f,
            Padding = new Edges4(P.ChPadX, 0f, P.ChPadX, 0f), Corners = Radii.ControlAll,
            Fill = cur ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => a.P.PlayChapter(index), BrushTransitionMs = Design.Motion.Faster,
            Children = [number, name, leader, left, starts],
        };
    }

    /// <summary>About this book: the provider's HTML (rich text, Read more), the sample, the facts and the rating; the facts beside it from Standard up.</summary>
    static Element About(Args a)
    {
        var v = a.V;
        var p = a.P;
        var text = new List<Element>(2);
        if (v.AboutHtml.Length > 0)
            text.Add(new BoxEl
            {
                Key = "cpa-about-text", Direction = 1, MinWidth = 0f,
                Children = [Controls.ExpandableRichTextFlex(v.AboutHtml, Body("").Size, Tok.TextSecondary, Tok.AccentTextPrimary, P.AboutLines, "cpa-about", p.OpenRoute)],
            });
        var side = new List<Element>(3);
        if (v.SampleId is not null && v.State != AudiobookState.NotStarted)
            side.Add(Button.Create(v.SampleButton, p.PlaySample, glyph: Icons.Play) with { Key = "cpa-about-sample", Shrink = 0f, AlignSelf = FlexAlign.Start });
        if (v.Facts.Length > 0) side.Add(ClassicPodcastKit.Facts(v.Facts, "cpa-facts"));
        if (Rating(a) is { } rating) side.Add(rating);
        var sideBox = new BoxEl
        {
            Key = "cpa-about-side", Direction = 1, Gap = P.ColumnGap, MinWidth = 0f, Width = a.Lay.AboutBeside ? P.AboutW : float.NaN, Shrink = 0f,
            Children = side.ToArray(),
        };
        return new BoxEl
        {
            Key = "cpa-about", Direction = a.Lay.AboutBeside ? (byte)0 : (byte)1, Gap = a.Lay.AboutBeside ? P.AboutGap : P.ColumnGap, AlignItems = FlexAlign.Start, MinWidth = 0f,
            Layout = ClassicTierMotion.Reflow,
            Children = [new BoxEl { Direction = 1, Grow = 1f, Basis = a.Lay.AboutBeside ? 0f : float.NaN, MinWidth = 0f, Children = text.ToArray() }, sideBox],
        };
    }

    static Element? Rating(Args a)
    {
        var v = a.V;
        var p = a.P;
        if (v.State == AudiobookState.Finished)
        {
            var lines = new List<Element>(2) { BodyStrong(v.FinishedTitle) };
            if (v.FinishedSub.Length > 0) lines.Add(Caption(v.FinishedSub) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f });
            var accent = a.Accent;
            lines.Add(Controls.StarRow(0, p.Rate, P.PlayGlyph + P.TagGap, () => Design.Palette.ReadableInk(accent)));
            return new BoxEl { Key = "cpa-rate", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f, Children = lines.ToArray() };
        }
        if (v.RatingsLine.Length == 0) return null;
        return new BoxEl
        {
            Key = "cpa-rating", Direction = 1, Gap = P.FactsRowGap, MinWidth = 0f,
            Children = [ClassicPodcastKit.Line(ClassicPodcastKit.Strong(v.Rating), new TextSpan(" ★" + P.Dot + v.RatingsLine))],
        };
    }
}
