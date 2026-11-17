// ── Themes/WinUi/Structure/Views/Podcast/WinUiAudiobookParts.cs — the book-only parts of WinUI's audiobook page (podcast pages v2) ────────────────
//
// A book's chapters are never a track list (concept "A contents page and a book line"): THE BOOK LINE draws the whole book as one bar cut by chapter
// length, your place on it with a thumb; THE CONTENTS PAGE is set like a book's (number, title, dot leaders, the time the chapter starts where the
// page number would be), a thin margin line shows how far you have read (done brighter, the current chapter in the accent, which also carries its time
// left), front and back matter set quieter. THE ABOUT CARD: About this book, the sample, the facts, and the rating card (or "You finished this book ·
// How was it?"). THE BYLINE: "By <authors>  Narrated by <narrators>".
//
// Every text is the host's; the live place (`AudiobookPlace`) binds only in the leaves that move (a segment's fill, the thumb, the current row's left).

using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using AB = Wavee.Themes.WinUi.WinUiPodcastAudiobookRules;

namespace Wavee.Themes.WinUi;

/// <summary>The audiobook page's book-only parts.</summary>
internal static class WinUiAudiobookParts
{
    // ══ THE BYLINE ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>"By Imogen Hartley   Narrated by Daniel Osei": the names semibold in the primary ink, links where they have a page.</summary>
    public static Element Byline(AudiobookView v, Action<string> open)
    {
        var kids = new List<Element>(2);
        if (v.Authors.Length > 0) kids.Add(Credit(v.ByLabel, v.Authors, open));
        if (v.Narrators.Length > 0) kids.Add(Credit(v.NarratedByLabel, v.Narrators, open));
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = AB.BylineGap, Margin = new Edges4(0f, AB.BylineTop - AB.IdGap, 0f, 0f), MinWidth = 0f,
            Children = [.. kids],
        };
    }

    static Element Credit(string label, ImmutableArray<AudiobookPersonVm> people, Action<string> open)
    {
        var spans = new List<TextSpan>(people.Length * 2 + 1) { new(label + " ") };
        for (int i = 0; i < people.Length; i++)
        {
            if (i > 0) spans.Add(new TextSpan(", ", Weight: AB.Strong, Color: Tok.TextPrimary));
            var person = people[i];
            string uri = person.Uri;
            spans.Add(new TextSpan(person.Name, Weight: AB.Strong, Color: Tok.TextPrimary,
                OnClick: uri.Length > 0 ? () => open(uri) : null, IsLink: uri.Length > 0));
        }
        return new SpanTextEl(spans.ToArray()) { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Shrink = 1f };
    }

    // ══ THE BOOK LINE ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The line card: its head ("The whole book · 19 chapters, drawn to length" … "3 hr in · 9 hr left") over the segments.</summary>
    public static Element Line(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        var place = p.Place;
        bool progress = v.State == AudiobookState.InProgress;
        TextEl right = progress
            ? new TextEl("") { Text = Prop.Of(() => place.Value.InLeft), Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.TextSecondary, Shrink = 0f }
            : Caption(v.LineRight) with { Color = Tok.TextSecondary, Shrink = 0f };
        var head = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = AB.RowGap, MinWidth = 0f,
            Children =
            [
                new SpanTextEl(new TextSpan[] { new(v.LineTitle, Weight: AB.Strong, Color: Tok.TextPrimary), new(" " + v.LineSub, Color: Tok.TextTertiary) })
                {
                    Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                },
                right,
            ],
        };
        var chapters = v.Chapters;
        var playChapter = p.PlayChapter;
        bool finished = v.State == AudiobookState.Finished;
        Element segments = Responsive.Of(w => Segments(w, chapters, place, playChapter, progress, finished));
        return Card(AB.LineKey, new Edges4(AB.LinePadX, AB.LinePadTop, AB.LinePadX, AB.LinePadBottom), AB.LineGap, [head, segments]);
    }

    static Element Segments(float width, ImmutableArray<AudiobookChapterVm> chapters, IReadSignal<AudiobookPlace> place, Action<int> play,
        bool progress, bool finished)
    {
        int n = chapters.Length;
        if (n == 0 || width <= 0f) return new BoxEl { Height = AB.SegBand };
        long total = 0;
        foreach (var c in chapters) total += Math.Max(1, c.LengthMs);
        float avail = Math.Max(0f, width - AB.SegGap * (n - 1));
        var widths = new float[n];
        float sum = 0f;
        for (int i = 0; i < n; i++) { widths[i] = Math.Max(AB.SegMin, avail * Math.Max(1, chapters[i].LengthMs) / total); sum += widths[i]; }
        float scale = sum > avail && sum > 0f ? avail / sum : 1f;
        var starts = new float[n];
        float x = 0f;
        var kids = new Element[n + 1];
        for (int i = 0; i < n; i++)
        {
            widths[i] *= scale;
            starts[i] = x;
            x += widths[i] + AB.SegGap;
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
                Height = AB.SegHeight, Corners = CornerRadius4.All(AB.SegRadius),
                Fill = finished ? Tok.FillControlStrong : Tok.AccentDefault,
            };
            var pill = new BoxEl
            {
                Width = segW, Height = AB.SegHeight, ZStack = true, Corners = CornerRadius4.All(AB.SegRadius), ClipToBounds = true,
                Fill = Tok.TextPrimary with { A = AB.SegTrackAlpha }, Children = [fill],
            };
            kids[i] = ToolTip.Wrap(new BoxEl
            {
                Key = AB.SegKey + i, Width = segW, Height = AB.SegBand, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center,
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => play(index),
                Children = [pill],
            }, chapters[i].Label);
        }
        // the thumb: one node over the row, placed at your place on the current chapter's segment
        kids[n] = new BoxEl
        {
            Width = AB.ThumbOuter, Height = AB.ThumbOuter, HitTestVisible = false, AlignSelf = FlexAlign.Center,
            Corners = CornerRadius4.All(AB.ThumbOuter / 2f), Fill = Tok.FillControlStrong, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Visible = Prop.Of(() => progress && place.Value.Current >= 0),
            Transform = Prop.Of(() =>
            {
                var pl = place.Value;
                int c = Math.Clamp(pl.Current, 0, n - 1);
                return Affine2D.Translation(starts[c] + widths[c] * pl.CurrentFill - AB.ThumbOuter / 2f, 0f);
            }),
            Children = [new BoxEl { Width = AB.ThumbInner, Height = AB.ThumbInner, Corners = CornerRadius4.All(AB.ThumbInner / 2f), Fill = Tok.AccentDefault }],
        };
        var row = new BoxEl { Direction = 0, Gap = AB.SegGap, Height = AB.SegBand, Children = kids[..n] };
        return new BoxEl { ZStack = true, Height = AB.SegBand, Children = [row, kids[n]] };
    }

    // ══ THE CONTENTS PAGE ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public static Element Contents(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        var head = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = AB.RowGap, MinWidth = 0f,
            Padding = new Edges4(0f, AB.ContentsHeadPad, AB.ContentsHeadPad, AB.ContentsHeadBottom),
            Children =
            [
                BodyStrong(v.ContentsTitle) with { Shrink = 0f },
                Caption(v.ContentsSub) with { Color = Tok.TextSecondary, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Caption(v.StartsAtLabel) with { Color = Tok.TextSecondary, Shrink = 0f },
            ],
        };
        var kids = new List<Element>(v.Chapters.Length + 1) { head };
        foreach (var c in v.Chapters) kids.Add(Row(c, v, p, lay));
        return Card(AB.ContentsKey, new Edges4(AB.ContentsPadX, AB.ContentsPadTop, AB.ContentsPadX, AB.ContentsPadBottom), 0f, [.. kids]);
    }

    static Element Row(AudiobookChapterVm c, AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        bool cur = v.Current == c.Index && v.State == AudiobookState.InProgress;
        bool done = c.Done || v.State == AudiobookState.Finished;
        var place = p.Place;
        int index = c.Index;
        ColorF margin = cur ? Tok.AccentDefault : done ? Tok.TextPrimary with { A = AB.MarginDone } : Tok.TextPrimary with { A = AB.MarginRest };
        ColorF nameInk = cur ? Tok.TextPrimary : done || c.Matter ? Tok.TextSecondary : Tok.TextPrimary;
        var name = new TextEl(c.Name)
        {
            Size = c.Matter ? AB.MatterSize : AB.NameSize, LineHeight = AB.NameLine, Weight = cur ? AB.Strong : (ushort)0, Color = nameInk,
            Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f,
        };
        var leader = new BoxEl
        {
            Height = AB.LeaderHeight, Grow = 1f, MinWidth = AB.LeaderMin, AlignSelf = FlexAlign.Center, OffsetY = AB.LeaderLift / 2f,
            Fill = Tok.TextPrimary with { A = done ? AB.LeaderDoneAlpha : AB.LeaderAlpha },
        };
        var left = new TextEl("")
        {
            Text = cur ? Prop.Of(() => place.Value.Current == index ? place.Value.CurrentLeft : "") : "",
            Size = Caption("").Size, LineHeight = Caption("").LineHeight, Color = Tok.AccentTextPrimary, Shrink = 0f, Wrap = TextWrap.NoWrap,
        };
        var starts = Body(c.StartsAt) with
        {
            Width = AB.StartWidth, Shrink = 0f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End,
            Color = cur ? Tok.TextPrimary : done ? Tok.TextTertiary : Tok.TextSecondary,
        };
        var number = Body(c.Number) with
        {
            Width = AB.NumberWidth, Shrink = 0f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End, Color = cur ? Tok.AccentTextPrimary : Tok.TextTertiary,
        };
        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = AB.RowGap, MinHeight = lay.RowMin, Grow = 1f, MinWidth = 0f,
            Padding = new Edges4(AB.RowPadLeft, 0f, AB.RowPadX, 0f),
            Children = [number, name, leader, left, starts],
        };
        return ToolTip.Wrap(new BoxEl
        {
            Key = AB.RowKey + c.Index, Direction = 0, MinHeight = lay.RowMin, MinWidth = 0f,
            Corners = new CornerRadius4(0f, Radii.Control, Radii.Control, 0f),
            Fill = cur ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => p.PlayChapter(index),
            Children = [new BoxEl { Width = AB.Margin, AlignSelf = FlexAlign.Stretch, Fill = margin, Shrink = 0f }, line],
        }, c.Length);
    }

    // ══ THE ABOUT CARD ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    public static Element About(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        var kids = new List<Element>(6) { BodyStrong(v.AboutTitle) };
        if (v.About.Length > 0)
        {
            var paragraphs = new Element[v.About.Length];
            for (int i = 0; i < paragraphs.Length; i++) paragraphs[i] = Body(v.About[i]) with { Color = Tok.TextSecondary, MinWidth = 0f };
            kids.Add(new BoxEl { Direction = 1, Gap = AB.ParagraphGap, MinWidth = 0f, Children = paragraphs });
        }
        if (v.SampleId is not null) kids.Add(SampleRow(v, p));
        if (v.Facts.Length > 0) kids.Add(Facts(v.Facts));
        if (RatingCard(v, p) is { } rating) kids.Add(rating);
        return Card(AB.AboutKey, Edges4.All(AB.AboutPad), AB.AboutGap, [.. kids]) with
        {
            Width = lay.TwoColumns ? AB.AboutWidth : float.NaN, Shrink = 0f,
        };
    }

    static Element SampleRow(AudiobookView v, AudiobookPageProps p)
    {
        var button = Controls.IconAction(Icons.Play, p.PlaySample, size: AB.SampleButton) with
        {
            Key = AB.SampleKey, Corners = CornerRadius4.All(AB.SampleButton / 2f),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
        };
        var words = new List<Element>(2) { Body(v.SampleTitle) };
        if (v.SampleSub.Length > 0) words.Add(Caption(v.SampleSub) with { Color = Tok.TextSecondary });
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = AB.SampleGap, MinWidth = 0f,
            Children = [Controls.Named(button, v.SampleButton), new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, Children = [.. words] }],
        };
    }

    static Element Facts(ImmutableArray<PodcastFact> facts)
    {
        var rows = new Element[facts.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = AB.FactsColGap, MinWidth = 0f,
                Children =
                [
                    Body(facts[i].Label) with { Width = AB.FactsLabel, Shrink = 0f, Color = Tok.TextTertiary },
                    Body(facts[i].Value) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
                ],
            };
        return new BoxEl { Direction = 1, Gap = AB.FactsRowGap, MinWidth = 0f, Children = rows };
    }

    static Element? RatingCard(AudiobookView v, AudiobookPageProps p)
    {
        Element stars;
        Element words;
        Element? trailing = null;
        bool ask = v.State == AudiobookState.Finished;
        if (ask)
        {
            var lines = new List<Element>(2) { BodyStrong(v.FinishedTitle) };
            if (v.FinishedSub.Length > 0) lines.Add(Caption(v.FinishedSub) with { Color = Tok.TextSecondary });
            words = new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, Children = [.. lines] };
            stars = Controls.StarRow(0, p.Rate, AB.RateStar, static () => Tok.AccentTextPrimary);
            return Plate(true, [words, stars]);
        }
        if (v.Rating.Length == 0) return null;
        var big = new TextEl(v.Rating) { Size = AB.RateBig, LineHeight = AB.RateBigLine, Weight = AB.Strong, Shrink = 0f };
        var column = new List<Element>(2) { Controls.StarRow((int)MathF.Round(float.TryParse(v.Rating, out float r) ? r : 0f), null, AB.RateStar) };
        if (v.RatingsLine.Length > 0) column.Add(Caption(v.RatingsLine) with { Color = Tok.TextSecondary });
        words = new BoxEl { Direction = 1, Grow = 1f, MinWidth = 0f, Children = [.. column] };
        if (p.Rate is { } rate && v.State == AudiobookState.InProgress)
            trailing = Button.Create(v.RateLabel, () => rate(5)) with { Shrink = 0f };
        else
            trailing = Button.Create(v.RateLabel, static () => { }, isEnabled: false) with { Shrink = 0f };
        return Plate(false, [big, words, trailing]);
    }

    static Element Plate(bool ask, Element[] kids) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = AB.RateGap, MinWidth = 0f,
        Padding = new Edges4(AB.RatePadX, AB.RatePad, AB.RatePadX, AB.RatePad), Corners = CornerRadius4.All(Radii.Control),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = ask ? Tok.AccentDefault : Tok.StrokeCardDefault,
        Children = kids,
    };

    // ══ SHARED ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A WinUI card: the card fill, its 1-DIP stroke, the card radius, no shadow.</summary>
    public static BoxEl Card(string key, Edges4 padding, float gap, Element[] kids) => new()
    {
        Key = key, Direction = 1, Gap = gap, Padding = padding, MinWidth = 0f,
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = kids,
    };
}
