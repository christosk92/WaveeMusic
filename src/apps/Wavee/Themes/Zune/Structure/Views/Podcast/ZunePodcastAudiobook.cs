// ── Themes/Zune/Structure/Views/Podcast/ZunePodcastAudiobook.cs — Zune's audiobook page (podcast pages v2, concept `bookZune`) ────────────────────────
//
//   ZunePodcastKit.Page (one scroller over the page column)
//   └─ the book's name (120 light, lowercase, bleeding under the fade; 80 wrapping at Compact) and its subtitle (28/36 light, tertiary)
//      the pivots: chapters N · about · more by the author
//      the hero: the flat square cover · [the accent label (the kind word, a link to Browse › Audiobooks · "included in premium") · THE BIG NUMBER (the
//                whole book's percentage at 72, with the time left and the chapter beside it, the live line under it; the length before you start;
//                "finished" after) · continue / start / listen again (+ the sample before you start) · the speed · the sleep timer · "by … narrated by …" ·
//                in your library / download / share / ... · the premium line (check hours left, top up) or the price and buy]
//      the pivot's body:
//        chapters  the book line (the whole book cut by chapter length, filled to your place) over the contents page (number, name, dot leaders, the
//                  time the chapter starts; a 2-DIP margin line: brighter once read, the accent on the current one, which carries its time left)
//        about     About this book (the house rich text, "read more"), the sample, the facts, the rating (or "you finished this book · how was it?")
//        more      the house cards
//
// The live place binds only in the leaves that move. Loading: the same column over `AudiobookView.Placeholder`, derived flat. Numbers: `ZunePodcastRules`.

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
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZunePodcastKit;
using Z = Wavee.Themes.Zune.ZunePodcastRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's audiobook page.</summary>
internal static class ZunePodcastAudiobook
{
    public static Element Template(AudiobookPageProps p, in ViewContext ctx)
        => new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Children = [Embed.Comp(p, static () => new PageHost()) with { Key = "zune-podcast-audiobook" }] };

    enum Pivot { Chapters, About, More }

    sealed class PageHost : Component
    {
        readonly Signal<int> _pivot = new(0);
        Action<int>? _select;

        public override Element Render()
        {
            var p = UseProps<AudiobookPageProps>();
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            var lay = ZunePodcastTiers.Of(in facts);
            float gutter = ZuneTiers.GutterOf(in facts);
            _select ??= i => _pivot.Value = i;
            var v = p.View.Value;
            bool playing = p.Playing.Value;
            var shown = v.Load == PodcastLoad.Pending ? AudiobookView.Placeholder : v;
            int pivot = _pivot.Value;
            if (pivot == (int)Pivot.More && shown.Similar.IsDefaultOrEmpty) pivot = (int)Pivot.Chapters;
            var items = ImmutableArray.CreateBuilder<K.PivotItem>(3);
            items.Add(new K.PivotItem(Loc.Get(Strings.Podcast.Pages.Chapters),
                shown.Chapters.Length > 0 ? shown.Chapters.Length.ToString(System.Globalization.CultureInfo.CurrentCulture) : ""));
            items.Add(new K.PivotItem(Loc.Get(Strings.Podcast.Pages.About), ""));
            if (!shown.Similar.IsDefaultOrEmpty) items.Add(new K.PivotItem(Loc.Get(Strings.Podcast.Pages.MoreByTheAuthor), ""));

            Element real = Column(v, p, lay, playing, pivot);
            var region = new SkelRegionEl(
                Pending: () => p.View.Value.Load == PodcastLoad.Pending, Failed: static () => false,
                Content: () => real, ShimmerSource: () => Column(AudiobookView.Placeholder, p, lay, false, (int)Pivot.Chapters),
                OnFailed: null, Reveal: SkelReveal.Soft, Style: SkeletonStyle.Default, Group: null, SmoothResize: false);
            var kids = new List<Element>(4) { K.Title(shown.Title, lay, gutter) };
            if (shown.Subtitle.Length > 0) kids.Add(K.Sub(shown.Subtitle, gutter));
            kids.Add(K.Pivots("zb-pivots", items.ToImmutable(), pivot, _select, lay, gutter));
            kids.Add(region);
            return K.Page(p.RouteKey, new BoxEl { Key = "zune-book-column", Direction = 1, MinWidth = 0f, Children = kids.ToArray() }, gutter);
        }
    }

    static Element Column(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay, bool playing, int pivot)
    {
        Element body = (Pivot)pivot switch
        {
            Pivot.About => K.Body("zb-body:about", About(v, p, lay)),
            Pivot.More => K.Body("zb-body:more", K.Cards("zb-more", v.Similar, p.OpenUri, lay.Stacked, p.SimilarMenu, p.PlaySimilar)),
            _ => K.Body("zb-body:chapters", new BoxEl { Direction = 1, Gap = Z.SectionGap, MinWidth = 0f, Children = [BookLine(v, p, lay), Contents(v, p, lay)] }),
        };
        return new BoxEl { Direction = 1, MinWidth = 0f, Children = [Hero(v, p, lay, playing), body] };
    }

    // ══ THE HERO ════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Hero(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay, bool playing)
    {
        Element art = new BoxEl
        {
            Key = "zb-art", Width = lay.Art, Height = lay.Art, Shrink = 0f, AlignSelf = FlexAlign.Start, Layout = ZuneMotion.GroupResize,
            Children = [Controls.Artwork(v.ArtUrl, lay.Art, lay.Art, 0f, decodePx: (int)(lay.Art * 2f))],
        };
        var main = new List<Element>(7) { Label(v, p), Big(v, p), Up(v, p, lay, playing) };
        if (v.Authors.Length + v.Narrators.Length > 0) main.Add(Byline(v, p.OpenUri));
        main.Add(Commands(v, p, lay));
        if (Premium(v, p, lay) is { } premium) main.Add(premium);
        return new BoxEl
        {
            Key = "zb-hero", Direction = (byte)(lay.Stacked ? 1 : 0), Gap = lay.Stacked ? Z.HeroRowGap : Z.HeroColGap, MinWidth = 0f,
            AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Start, Margin = new Edges4(0f, 0f, 0f, Z.HeroBottom), Layout = ZuneMotion.GroupResize,
            Children = [art, new BoxEl { Direction = 1, Gap = Z.HeroLineGap, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Children = main.ToArray() }],
        };
    }

    /// <summary>"audiobook · included in premium": the kind word links to Browse › Audiobooks where Wavee routes it; the genres name no page (plain).</summary>
    static Element Label(AudiobookView v, AudiobookPageProps p)
    {
        var parts = new List<Element>(3);
        if (v.Kind.Length > 0)
            parts.Add(v.KindRoute is { Length: > 0 } route ? K.Link("zb-kind", v.Kind, () => p.OpenRoute(route), 0f) : K.Label(v.Kind));
        if (v.Included && v.IncludedTag.Length > 0) parts.Add(K.Label(v.IncludedTag));
        if (v.Genres.Length > 0) parts.Add(K.MetaWord(string.Join(" · ", v.Genres)) with { Color = Tok.TextTertiary, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f });
        return K.Meta("zb-label", parts);
    }

    /// <summary>The big number: the percentage (72) with the time left and the chapter beside it and the live line under it; the length before you
    /// start; "finished" after.</summary>
    static Element Big(AudiobookView v, AudiobookPageProps p)
    {
        var place = p.Place;
        TextEl big = new("")
        {
            FontFamily = R.Type.Panorama("").FontFamily, Size = Z.BigSize, LineHeight = Z.BigLine, Weight = Z.BigWeight,
            Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
        };
        TextEl words = new("")
        {
            Size = Z.BigWordsSize, LineHeight = Z.BigWordsLine, Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap,
            MinWidth = 0f, Shrink = 1f,
        };
        var kids = new List<Element>(2);
        switch (v.State)
        {
            case AudiobookState.InProgress:
                string chapter = K.Glue(v.UpTitle + v.UpName);
                kids.Add(new BoxEl
                {
                    Key = "zb-big", Direction = 0, Gap = Z.BigGap, AlignItems = FlexAlign.End, Wrap = true, MinWidth = 0f,
                    Children =
                    [
                        big with { Text = Prop.Of(() => place.Value.Percent ?? "") },
                        words with { Text = Prop.Of(() => place.Value.Left is { Length: > 0 } left ? left + K.Dot + chapter : chapter), Margin = new Edges4(0f, 0f, 0f, Z.BigGap / 2f) },
                    ],
                });
                kids.Add(new BoxEl { Direction = 0, MaxWidth = Z.BookBarMax, MinWidth = 0f, Children = [K.Line(() => place.Value.Fraction) with { Grow = 1f }] });
                break;
            case AudiobookState.Finished:
                kids.Add(new BoxEl
                {
                    Key = "zb-big", Direction = 0, Gap = Z.BigGap, AlignItems = FlexAlign.End, Wrap = true, MinWidth = 0f,
                    Children = [big with { Text = Loc.Get(Strings.Podcast.Pages.Finished) }, words with { Text = K.Glue(v.UpSub), Margin = new Edges4(0f, 0f, 0f, Z.BigGap / 2f) }],
                });
                break;
            default:
                kids.Add(new BoxEl
                {
                    Key = "zb-big", Direction = 0, Gap = Z.BigGap, AlignItems = FlexAlign.End, Wrap = true, MinWidth = 0f,
                    Children = [big with { Text = v.UpTitle }, words with { Text = K.Glue(v.UpSub), Margin = new Edges4(0f, 0f, 0f, Z.BigGap / 2f) }],
                });
                break;
        }
        return new BoxEl { Direction = 1, Gap = Z.UpSubGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Up(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay, bool playing)
    {
        float h = lay.Target;
        string glyph = playing ? ZunePlayerLayout.PauseSolid : v.State == AudiobookState.Finished ? Icons.Refresh : ZunePlayerLayout.PlaySolid;
        string word = playing ? Loc.Get(Strings.Podcast.Pages.Pause) : v.PrimaryLabel;
        var primary = K.Button("zb-primary", word, glyph, p.Play, h, v.PrimaryAccent, fill: lay.PrimaryFills);
        var first = new List<Element>(2) { primary };
        if (v.State == AudiobookState.NotStarted && v.SampleId is not null)
            first.Add(K.Button("zb-sample", v.SampleButton, Icons.Play, p.PlaySample, h));
        var tools = new List<Element>(2)
        {
            K.Menu("zb-speed", p.SpeedLabel, null, null, p.SpeedMenu, h, true, Loc.Get(Strings.Podcast.Pages.Speed)),
            K.Menu("zb-sleep", p.SleepLabel, p.SleepOn, Icons.Moon, p.SleepMenu, h, lay.Words, Loc.Get(Strings.Podcast.Pages.SleepTimer)),
        };
        // ONE tree at every tier (a crossing keeps the buttons): the primary's line and the tools' line, side by side or stacked.
        return new BoxEl
        {
            Key = "zb-up", Direction = (byte)(lay.Stacked ? 1 : 0), Gap = Z.CommandGap, Wrap = !lay.Stacked, AlignItems = lay.Stacked ? FlexAlign.Stretch : FlexAlign.Center,
            MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "zb-up:play", Direction = 0, Gap = Z.CommandGap, MinWidth = 0f, Children = first.ToArray() },
                new BoxEl { Key = "zb-up:tools", Direction = 0, Gap = Z.CommandGap, MinWidth = 0f, Children = tools.ToArray() },
            ],
        };
    }

    /// <summary>"by imogen hartley   narrated by daniel osei": the words lowercase and tertiary, the names in the primary ink, links where they have a page.</summary>
    static Element Byline(AudiobookView v, Action<string> open)
    {
        var kids = new List<Element>(2);
        if (v.Authors.Length > 0) kids.Add(Credit(v.ByLabel, v.Authors, open));
        if (v.Narrators.Length > 0) kids.Add(Credit(v.NarratedByLabel, v.Narrators, open));
        return new BoxEl { Key = "zb-byline", Direction = 0, Wrap = true, Gap = Z.BylineGap, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Credit(string label, ImmutableArray<AudiobookPersonVm> people, Action<string> open)
    {
        var kids = new List<Element>(people.Length + 1) { Byword(label) with { Color = Tok.TextTertiary } };
        for (int i = 0; i < people.Length; i++)
        {
            var person = people[i];
            string uri = person.Uri;
            var name = Byword(i < people.Length - 1 ? person.Name + "," : person.Name);
            kids.Add(uri.Length == 0 ? name : new BoxEl
            {
                Key = "zb-credit:" + uri, Shrink = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false,
                OnClick = () => open(uri), Children = [name with { HoverColor = Tok.TextSecondary, PressedColor = Tok.TextTertiary }],
            });
        }
        return new BoxEl { Direction = 0, Gap = Z.MetaGap, AlignItems = FlexAlign.Center, Shrink = 0f, Children = kids.ToArray() };
    }

    static TextEl Byword(string text) => new(text)
    {
        Size = Z.BylineSize, LineHeight = Z.BylineLine, Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.NoWrap, MaxLines = 1, Shrink = 0f,
    };

    static Element Commands(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay)
    {
        float h = lay.Target;
        string download = Loc.Get(Strings.Podcast.Pages.Download);
        var kids = new List<Element>(4)
        {
            K.Button("zb-library", v.LibraryLabel, v.InLibrary ? Icons.Check : Icons.Add, p.ToggleLibrary, h, on: v.InLibrary),
            K.Named(K.Button("zb-download", null, Icons.Download, p.Download, h), download, false),
        };
        if (p.Share is { } share) kids.Add(K.Named(K.Button("zb-share", null, Icons.Share, share, h), Loc.Get(Strings.Podcast.Pages.Share), false));
        kids.Add(K.More("zb-more-cmd", p.Menu, h));
        return new BoxEl { Key = "zb-cmds", Direction = 0, Gap = Z.CommandGap, Wrap = true, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>The premium line (the allowance: what it includes, "check hours left" and "top up hours", both in the browser), or the price, the list
    /// price struck through and "buy audiobook".</summary>
    static Element? Premium(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay)
    {
        var kids = new List<Element>(4);
        if (v.Included)
        {
            if (v.PremiumLine.Length == 0) return null;
            kids.Add(R.Type.TrackMeta(K.Glue(v.PremiumLine)) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f });
            kids.Add(K.Link("zb-hours", v.CheckHoursLabel, p.CheckHours, lay.Target));
            kids.Add(K.Link("zb-topup", v.TopUpLabel, p.TopUp, lay.Target));
        }
        else
        {
            if (v.Price.Length > 0) kids.Add(R.Type.TrackTitle(v.Price) with { Weight = Z.Strong });
            if (v.ListPrice.Length > 0) kids.Add(R.Type.TrackMeta(v.ListPrice) with { Color = Tok.TextTertiary, Strikethrough = true });
            if (v.BuyLabel.Length > 0 && p.Buy is { } buy) kids.Add(K.Button("zb-buy", v.BuyLabel, null, buy, lay.Target));
            if (kids.Count == 0) return null;
        }
        return new BoxEl { Key = "zb-premium", Direction = 0, Wrap = true, Gap = Z.PremiumGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    // ══ THE BOOK LINE ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element BookLine(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay)
    {
        var place = p.Place;
        bool progress = v.State == AudiobookState.InProgress;
        TextEl right = progress
            ? new TextEl("") { Text = Prop.Of(() => place.Value.InLeft), Size = Z.CountSize, LineHeight = Z.CountLine, Color = Tok.TextSecondary, Shrink = 0f, Case = R.Case.For(LabelKind.Label) }
            : new TextEl(v.LineRight) { Size = Z.CountSize, LineHeight = Z.CountLine, Color = Tok.TextSecondary, Shrink = 0f, Case = R.Case.For(LabelKind.Label) };
        var head = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.LineGap, MinWidth = 0f,
            Children =
            [
                new SpanTextEl(new TextSpan[] { new(v.LineTitle, Color: Tok.TextPrimary), new(" " + v.LineSub, Color: Tok.TextTertiary) })
                {
                    FontFamily = R.Type.Pivot("").FontFamily, Size = Z.MapHeadSize, LineHeight = Z.MapHeadLine, Weight = Z.Light,
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                },
                right,
            ],
        };
        var chapters = v.Chapters;
        var play = p.PlayChapter;
        bool finished = v.State == AudiobookState.Finished;
        bool seek = !lay.Stacked;
        Element segments = Responsive.Of(w => Segments(w, chapters, place, play, finished, seek));
        return new BoxEl { Key = "zb-line", Direction = 1, Gap = Z.LineGap, MinWidth = 0f, Children = [head, segments] };
    }

    static Element Segments(float width, ImmutableArray<AudiobookChapterVm> chapters, IReadSignal<AudiobookPlace> place, Action<int> play, bool finished, bool seek)
    {
        int n = chapters.Length;
        if (n == 0 || width <= 0f) return new BoxEl { Height = Z.MapBand };
        long total = 0;
        foreach (var c in chapters) total += Math.Max(1, c.LengthMs);
        float avail = Math.Max(0f, width - Z.SegGap * (n - 1));
        var widths = new float[n];
        float sum = 0f;
        for (int i = 0; i < n; i++) { widths[i] = Math.Max(Z.SegMin, avail * Math.Max(1, chapters[i].LengthMs) / total); sum += widths[i]; }
        float scale = sum > avail && sum > 0f ? avail / sum : 1f;
        var kids = new Element[n];
        for (int i = 0; i < n; i++)
        {
            float segW = widths[i] * scale;
            int index = i;
            bool done = chapters[i].Done;
            var fill = new BoxEl
            {
                Width = Prop.Of(() =>
                {
                    var pl = place.Value;
                    return segW * (finished || done ? 1f : pl.Current == index ? pl.CurrentFill : 0f);
                }),
                Height = Z.SegH, Fill = finished ? Prop.Of(static () => Tok.TextTertiary) : Prop.Of(static () => Tok.AccentDefault),
            };
            var seg = new BoxEl
            {
                Key = "zb-seg:" + i, Width = segW, Height = Z.MapBand, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center,
                Children = [new BoxEl { Width = segW, Height = Z.SegH, ClipToBounds = true, Fill = Prop.Of(static () => Tok.TextPrimary with { A = Z.SegTrackAlpha }), Children = [fill] }],
            };
            kids[i] = ToolTip.Wrap(seek
                ? seg with
                {
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => play(index),
                    Fill = ColorF.Transparent, HoverFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowHoverAlpha }),
                }
                : seg with { HitTestVisible = false }, chapters[i].Label);
        }
        return new BoxEl { Direction = 0, Gap = Z.SegGap, Height = Z.MapBand, Children = kids };
    }

    // ══ THE CONTENTS PAGE ═══════════════════════════════════════════════════════════════════════════════════════════════════

    static Element Contents(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay)
    {
        var head = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.End, Gap = Z.LineGap, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Z.FactsRowGap),
            Children =
            [
                K.Heading(v.ContentsTitle) with { Shrink = 0f },
                R.Type.TrackMeta(v.ContentsSub) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                R.Type.TrackMeta(v.StartsAtLabel) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Shrink = 0f },
            ],
        };
        var kids = new List<Element>(v.Chapters.Length + 1) { head };
        foreach (var c in v.Chapters) kids.Add(Row(c, v, p));
        return new BoxEl { Key = "zb-contents", Direction = 1, MinWidth = 0f, Children = kids.ToArray() };
    }

    static Element Row(AudiobookChapterVm c, AudiobookView v, AudiobookPageProps p)
    {
        bool cur = v.Current == c.Index && v.State == AudiobookState.InProgress;
        bool done = c.Done || v.State == AudiobookState.Finished;
        var place = p.Place;
        int index = c.Index;
        Prop<ColorF> margin = cur ? Prop.Of(static () => Tok.AccentDefault)
            : done ? Prop.Of(static () => Tok.TextPrimary with { A = Z.MarginDoneAlpha }) : Prop.Of(static () => Tok.TextPrimary with { A = Z.MarginRestAlpha });
        var name = new TextEl(c.Name)
        {
            FontFamily = R.Type.Pivot("").FontFamily, Size = c.Matter ? Z.MatterSize : Z.ContentsNameSize, LineHeight = Z.ContentsNameLine, Weight = Z.Light,
            Color = cur ? Prop.Of(static () => R.Ink.AccentDecor) : done || c.Matter ? (Prop<ColorF>)Tok.TextTertiary : Tok.TextPrimary,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        var leader = new BoxEl
        {
            Height = Z.LeaderH, Grow = 1f, MinWidth = Z.LeaderMin, AlignSelf = FlexAlign.Center, OffsetY = Z.LeaderLift,
            BorderWidth = Z.LeaderH, BorderDashOn = Z.LeaderDot, BorderDashOff = Z.LeaderGapDash,
            BorderColor = Prop.Of(() => Tok.TextPrimary with { A = done ? Z.LeaderDoneAlpha : Z.LeaderAlpha }),
        };
        var left = new TextEl("")
        {
            Text = cur ? Prop.Of(() => place.Value.Current == index ? place.Value.CurrentLeft : "") : "",
            Size = Z.CountSize, LineHeight = Z.CountLine, Color = Prop.Of(static () => R.Ink.AccentDecor), Shrink = 0f, Wrap = TextWrap.NoWrap, Case = R.Case.For(LabelKind.Label),
        };
        var starts = new TextEl(c.StartsAt)
        {
            Size = R.Type.TrackTitle("").Size, LineHeight = R.Type.TrackTitle("").LineHeight, Width = Z.StartsW, Shrink = 0f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End,
            Color = cur ? Tok.TextPrimary : done ? Tok.TextTertiary : Tok.TextSecondary,
        };
        var number = new TextEl(c.Number)
        {
            FontFamily = R.Type.Pivot("").FontFamily, Size = Z.ChNumSize, LineHeight = Z.ChNumLine, Weight = Z.Light, Width = Z.ContentsNumW, Shrink = 0f,
            Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End, Color = cur ? Prop.Of(static () => R.Ink.AccentDecor) : (Prop<ColorF>)Tok.TextTertiary,
        };
        return new BoxEl
        {
            Key = "zb-row:" + c.Index, Direction = 0, MinHeight = Z.ContentsRowMin, MinWidth = 0f,
            Fill = ColorF.Transparent, HoverFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowHoverAlpha }),
            PressedFill = Prop.Of(static () => Tok.TextPrimary with { A = Z.RowPressedAlpha }),
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => p.PlayChapter(index),
            Children =
            [
                new BoxEl { Width = Z.MarginW, AlignSelf = FlexAlign.Stretch, Fill = margin, Shrink = 0f },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.ContentsGap, Grow = 1f, MinWidth = 0f, Padding = new Edges4(Z.ContentsPadL, 0f, 0f, 0f),
                    Children = [number, name, leader, left, starts],
                },
            ],
        };
    }

    // ══ ABOUT ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    static Element About(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay)
    {
        var kids = new List<Element>(5);
        if (v.AboutHtml.Length > 0)
            kids.Add(Controls.ExpandableRichTextFlex(v.AboutHtml, Z.BodySize, Tok.TextSecondary, R.Ink.AccentDecor, Z.AboutLines, "zb-about-text", p.OpenRoute));
        if (v.SampleId is not null)
            kids.Add(new BoxEl
            {
                Key = "zb-sample-row", Direction = 0, AlignItems = FlexAlign.Center, Gap = Z.SampleGap, MinWidth = 0f,
                Children =
                [
                    K.Named(K.Button("zb-sample-play", null, ZunePlayerLayout.PlaySolid, p.PlaySample, lay.Target), v.SampleButton, false),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, MinWidth = 0f,
                        Children =
                        [
                            R.Type.TrackTitle(v.SampleTitle) with { Case = R.Case.For(LabelKind.Label) },
                            R.Type.TrackMeta(v.SampleSub) with { Case = R.Case.For(LabelKind.Label), Wrap = TextWrap.Wrap, MinWidth = 0f },
                        ],
                    },
                ],
            });
        if (v.Facts.Length > 0) kids.Add(K.Facts("zb-facts", v.Facts));
        if (Rating(v, p, lay) is { } rating) kids.Add(rating);
        return new BoxEl { Key = "zb-about", Direction = 1, Gap = Z.SectionGap, MinWidth = 0f, MaxWidth = Z.AboutMax, Children = kids.ToArray() };
    }

    static Element? Rating(AudiobookView v, AudiobookPageProps p, ZunePodcastLayout lay)
    {
        if (v.State == AudiobookState.Finished)
        {
            var lines = new List<Element>(2) { K.Heading(v.FinishedTitle) };
            if (v.FinishedSub.Length > 0) lines.Add(R.Type.TrackMeta(v.FinishedSub) with { Case = R.Case.For(LabelKind.Label) });
            return new BoxEl
            {
                Key = "zb-rate", Direction = 1, Gap = Z.FactsRowGap, MinWidth = 0f,
                Children = [.. lines, Controls.StarRow(0, p.Rate, Z.RatingStar, static () => R.Ink.AccentDecor)],
            };
        }
        if (v.Rating.Length == 0) return null;
        var column = new List<Element>(2) { Controls.StarRow((int)MathF.Round(float.TryParse(v.Rating, out float r) ? r : 0f), null, Z.RatingStar) };
        if (v.RatingsLine.Length > 0) column.Add(K.MetaWord(v.RatingsLine));
        Element rate = p.Rate is { } rateIt && v.State == AudiobookState.InProgress
            ? K.Button("zb-rate-btn", v.RateLabel, null, () => rateIt(5), lay.Target)
            : K.Button("zb-rate-btn", v.RateLabel, null, null, lay.Target);
        return new BoxEl
        {
            Key = "zb-rate", Direction = 0, Gap = Z.SectionHeadGap, AlignItems = FlexAlign.Center, Wrap = true, MinWidth = 0f,
            Children = [R.Type.Heading(v.Rating) with { Shrink = 0f }, new BoxEl { Direction = 1, Shrink = 0f, Children = column.ToArray() }, rate],
        };
    }
}
