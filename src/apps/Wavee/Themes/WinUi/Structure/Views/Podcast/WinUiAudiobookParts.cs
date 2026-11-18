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
using FluentGpu.Scroll.Runtime;
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
        bool seek = !lay.Stacked;   // Compact: the line shows your place; the contents rows (44 tall) are the targets, a 6-DIP segment cannot be one
        Element segments = Responsive.Of(w => Segments(w, chapters, place, playChapter, progress, finished, seek));
        return Card(AB.LineKey, new Edges4(AB.LinePadX, AB.LinePadTop, AB.LinePadX, AB.LinePadBottom), AB.LineGap, [head, segments]);
    }

    static Element Segments(float width, ImmutableArray<AudiobookChapterVm> chapters, IReadSignal<AudiobookPlace> place, Action<int> play,
        bool progress, bool finished, bool seek)
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
        // ONE tooltip over the line, its text the hovered segment's (a hundred-chapter book drew a hundred tooltip hosts that re-rendered with the page).
        var hover = new Signal<int>(-1);
        var labels = new string[n];
        for (int i = 0; i < n; i++)
        {
            widths[i] *= scale;
            starts[i] = x;
            x += widths[i] + AB.SegGap;
            int index = i;
            float segW = widths[i];
            bool done = chapters[i].Done;
            labels[i] = chapters[i].Label;
            // The fill is a compositor scale (a bound width relaid the line out on every position tick).
            var fill = new BoxEl
            {
                Width = segW, TransformOriginX = 0f,
                Transform = Prop.Of(() =>
                {
                    if (finished || done) return Affine2D.Identity;
                    var pl = place.Value;
                    return Affine2D.Scale(pl.Current == index ? MathF.Max(0.001f, pl.CurrentFill) : 0.001f, 1f);
                }),
                Height = AB.SegHeight, Corners = CornerRadius4.All(AB.SegRadius),
                Fill = finished ? Tok.FillControlStrong : Tok.AccentDefault,
            };
            var pill = new BoxEl
            {
                Width = segW, Height = AB.SegHeight, ZStack = true, Corners = CornerRadius4.All(AB.SegRadius), ClipToBounds = true,
                Fill = Tok.TextPrimary with { A = AB.SegTrackAlpha }, Children = [fill],
            };
            var seg = new BoxEl
            {
                Key = AB.SegKey + i, Width = segW, Height = AB.SegBand, Shrink = 0f, Direction = 1, Justify = FlexJustify.Center, Children = [pill],
                Corners = CornerRadius4.All(AB.SegRadius),
            };
            // One tree at every tier (a crossing keeps the segments): only the verb comes and goes.
            kids[i] = seek
                ? seg with
                {
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => play(index),
                    Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                    OnHoverMove = _ => hover.SetIfChanged(index),
                }
                : seg with { OnHoverMove = _ => hover.SetIfChanged(index) };
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
        var row = new BoxEl { Direction = 0, Gap = AB.SegGap, Height = AB.SegBand, Children = kids[..n], OnPointerExit = () => hover.SetIfChanged(-1) };
        return ToolTip.Wrap(new BoxEl { ZStack = true, Height = AB.SegBand, Children = [row, kids[n]] },
            Prop.Of<string?>(() => hover.Value >= 0 && hover.Value < labels.Length ? labels[hover.Value] : null));
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
        var rows = Embed.Comp(new RowsArgs(v, p, lay), static () => new RowsHost()) with { Key = AB.ContentsKey + ":rows" };
        return Card(AB.ContentsKey, new Edges4(AB.ContentsPadX, AB.ContentsPadTop, AB.ContentsPadX, AB.ContentsPadBottom), 0f, [head, rows]);
    }

    /// <summary>The page scroller's handle, provided by the page (null: a view mounted alone, every row mounts).</summary>
    public static readonly Context<ScrollHandle?> PageScroll = new(null);

    sealed record RowsArgs(AudiobookView V, AudiobookPageProps P, WinUiAudiobookLayout Lay);

    /// <summary>The contents' rows WINDOWED on the page scroll: a long book has a hundred chapters, and every mounted row costs each scroll frame's
    /// record. The rows are one height (<c>lay.RowMin</c>), so the window is arithmetic: the rows near the viewport mount (a chunk of overscan either
    /// side, re-cut only when the viewport crosses a chunk), spacers of the exact height stand for the rest, and the card keeps its full height (nothing
    /// under it moves).</summary>
    sealed class RowsHost : Component
    {
        NodeHandle _node;
        Action<NodeHandle>? _capture;
        Func<long>? _windowOf;
        RowsArgs _a = null!;
        ScrollHandle? _scroll;

        public override Element Render()
        {
            _a = UseProps<RowsArgs>();
            _scroll = UseContext(PageScroll);
            _capture ??= h => _node = h;
            _windowOf ??= WindowOf;
            var a = _a;
            int n = a.V.Chapters.Length;
            float h = a.Lay.RowMin;
            long window = UseComputed(_windowOf).Value;
            int first = (int)(window >> 32), last = (int)window;
            if (_scroll is null || n <= AB.WindowFrom) { first = 0; last = n - 1; }
            var kids = new List<Element>(last - first + 3);
            if (first > 0) kids.Add(new BoxEl { Key = "toc:above", Height = first * h, Shrink = 0f }.Skeletonized(false));
            for (int i = first; i <= last && i < n; i++) kids.Add(Row(a.V.Chapters[i], a.V, a.P, a.Lay));
            if (last < n - 1) kids.Add(new BoxEl { Key = "toc:below", Height = (n - 1 - last) * h, Shrink = 0f }.Skeletonized(false));
            return new BoxEl { Direction = 1, MinWidth = 0f, OnRealized = _capture, Children = [.. kids] };
        }

        /// <summary>The window as (first &lt;&lt; 32 | last), cut to chunks of <c>AB.WindowChunk</c> rows so it changes only when the viewport crosses one.</summary>
        long WindowOf()
        {
            var a = _a;
            int n = a.V.Chapters.Length;
            var scroll = _scroll;
            if (scroll is null || n <= AB.WindowFrom) return n - 1;
            double offset = scroll.Offset.Value, vh = scroll.ViewportSignal.Value;
            var scene = Context.Scene;
            if (scene is null || _node.IsNull || !scene.IsLive(_node) || vh <= 0) return AB.WindowChunk * 2 - 1;
            // The rows' top in the scroller's content: their window position plus the offset (the viewport's own top is inside the overscan).
            float top = (float)(scene.AbsoluteRect(_node).Y + offset);
            float h = a.Lay.RowMin;
            int from = (int)Math.Floor((offset - top) / h), to = (int)Math.Ceiling((offset + vh - top) / h);
            int first = Math.Max(0, (from / AB.WindowChunk - 1) * AB.WindowChunk);
            int last = Math.Min(n - 1, (to / AB.WindowChunk + 2) * AB.WindowChunk - 1);
            if (last < first) last = first;
            return ((long)first << 32) | (uint)last;
        }
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
        // Dot leaders, as a book's contents page sets them: a dotted rule (a dashed stroke of 1-DIP dots) toward the start time.
        var leader = new BoxEl
        {
            Height = AB.LeaderHeight, Grow = 1f, MinWidth = AB.LeaderMin, AlignSelf = FlexAlign.Center, OffsetY = AB.LeaderLift / 2f,
            BorderWidth = AB.LeaderHeight, BorderDashOn = AB.LeaderDot, BorderDashOff = AB.LeaderGap,
            BorderColor = Tok.TextPrimary with { A = done ? AB.LeaderDoneAlpha : AB.LeaderAlpha },
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
            Width = AB.NumberWidth, Shrink = 0f, Wrap = TextWrap.NoWrap, TextAlign = TextAlign.End, Color = cur ? Tok.AccentTextPrimary : Tok.TextSecondary,
        };
        var line = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = AB.RowGap, MinHeight = lay.RowMin, Grow = 1f, MinWidth = 0f,
            Padding = new Edges4(AB.RowPadLeft, 0f, AB.RowPadX, 0f),
            Children = [number, name, leader, left, starts],
        };
        return new BoxEl
        {
            Key = AB.RowKey + c.Index, Direction = 0, MinHeight = lay.RowMin, Height = lay.RowMin, MinWidth = 0f,
            Corners = new CornerRadius4(0f, Radii.Control, Radii.Control, 0f),
            Fill = cur ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = () => p.PlayChapter(index),
            Children = [new BoxEl { Width = AB.Margin, AlignSelf = FlexAlign.Stretch, Fill = margin, Shrink = 0f }, line],
        };
    }

    // ══ THE ABOUT CARD ══════════════════════════════════════════════════════════════════════════════════════════════════════════

    public static Element About(AudiobookView v, AudiobookPageProps p, WinUiAudiobookLayout lay)
    {
        var kids = new List<Element>(6) { BodyStrong(v.AboutTitle) };
        // The provider's HTML through the house rich text (the kit's renderer, ParseRich: paragraphs, bold, lists, links routed in-app; never a tag or
        // an entity on screen), in its expandable form: a blurb runs to ten paragraphs, so the card shows the first lines and the house "Read more".
        if (v.AboutHtml.Length > 0)
            kids.Add(Controls.ExpandableRichTextFlex(v.AboutHtml, Body("").Size, Tok.TextSecondary, Tok.AccentTextPrimary, AB.AboutLines, AB.AboutKey, p.OpenRoute));
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
        Key = key, Layout = AB.Block, Direction = 1, Gap = gap, Padding = padding, MinWidth = 0f,
        Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = kids,
    };
}
