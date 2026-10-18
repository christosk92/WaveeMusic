// ── Themes/Zune/Structure/Lists/ZuneLyricsLines.cs — Zune's lyrics lines style (themes plan 6.3; P5 Z3) ───────────────────────────
//
// The `LyricsLinesStyle` Zune registers under `StyleKeys.LyricsLines` (Z-Lyrics and its -Compact / -Small / -Huge twins):
//
//   LINE LEVEL   every line is one line of type: Zune never wipes word by word (a word-synced document is drawn as its lines; no glyph
//                wipe is set, so the core's frame driver, which writes a wipe only where one exists, leaves the lines alone).
//   ACCENT       the line being sung is the accent (`R.Ink.AccentAction` on the theme surfaces, the stage's accent signal where the stage
//                hands one); every other line is the surface's ink.
//   RAMP         the distance is OPACITY (the board's 1 · .45 · .24 · .14 · .11 ramp is the core's `TranscriptPresentation.RowOpacity`
//                springing on each line's emphasis); no scale, no blur ladder beyond the core's depth of field.
//   TRANSLATION  the secondary line (translation or romanization, the user's choice) under its lyric at 0.625 of the line in the 300 weight,
//                the secondary ink, travelling with its line.
//   RESYNC       a tap on a line seeks there and re-attaches the follow (the host's verb); the "resync" pill itself is the lyrics VIEW's
//                (`ViewKeys.LyricsView`, Z4c), which this style does not draw.
//
// The state line, the loading bars, the unsynced block and the podcast peek keep the reading surface's metrics (`host.Metrics`), in
// Zune's voice. WHAT THE CORE KEEPS: the document, the clock, the follow, the cascade, the depth of field and the node handles
// (`Lyrics.ViewCore`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>Zune's lyrics lines.</summary>
internal static class ZuneLyricsLines
{
    /// <summary>The style Zune registers under <c>StyleKeys.LyricsLines</c>. Stateless.</summary>
    public static LyricsLinesStyle Style { get; } = new(Message, Shimmer, Skeleton, Unsynced, Lines, Peek) { Offline = Offline };

    static Element Message(ILyricsHost host, string text) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.XXL, 0f, Spacing.XXL, 0f),
        Children =
        [
            new TextEl(text)
            {
                Size = ZuneListRules.LyricsMessageSize, LineHeight = ZuneListRules.LyricsMessageLine, Color = host.Ink.Secondary, Wrap = TextWrap.Wrap,
            },
        ],
    };

    /// <summary>The offline voice: the heading, the one-sentence reason and a quiet retry, centred on the surface's own ink.</summary>
    static Element Offline(ILyricsHost host, Action retry) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Gap = Spacing.XS, Padding = Edges4.All(Spacing.XXL),
        Children =
        [
            R.Type.Heading(Loc.Get(Strings.Lyrics.Offline.Title)) with { Color = host.Ink.Primary, Wrap = TextWrap.Wrap },
            R.Type.TrackMeta(Loc.Get(Strings.Lyrics.Offline.Body)) with
            {
                Color = host.Ink.Secondary, Wrap = TextWrap.Wrap, MaxWidth = ZuneListRules.LyricsOfflineCaptionMaxW,
            },
            new BoxEl { Height = Spacing.L },
            Button.Standard(Loc.Get(Strings.Common.Retry), retry),
        ],
    };

    /// <summary>The loading bars at the side pad the first lines will take (square: Zune's bars have no corners).</summary>
    static Element Shimmer(ILyricsHost host)
    {
        bool large = host.Large;
        var rows = new Element[Lyrics.Surface.ShimmerRows];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Width = Lyrics.Surface.ShimmerBarW(large, i), Height = Lyrics.Surface.ShimmerRowH(large),
                Fill = host.Ink.Skeleton, AlignSelf = FlexAlign.Start,
            };
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, Gap = Lyrics.Surface.ShimmerGap(large),
            Padding = new Edges4(host.Metrics.SidePad, Lyrics.Surface.ShimmerPadTop(large), host.Metrics.SidePad, 0f),
            Children = rows,
        };
    }

    static SkeletonStyle Skeleton(ILyricsHost host)
        => new(host.Ink.Skeleton, RowGap: Lyrics.Surface.ShimmerGap(host.Large), BarRadius: 0f, TextRatio: ZuneListRules.LyricsSkeletonTextRatio);

    /// <summary>An untimed (or suppressed) document: a reading block, left-aligned, wrapped, never cut; no highlight, no follow.</summary>
    static Element Unsynced(ILyricsHost host, Lyrics.Doc doc)
    {
        var m = Lyrics.Surface.Unsynced(host.Large, host.IsPodcast).Scaled(host.TypeScale);
        var ink = host.Ink.Primary with { A = Lyrics.Surface.UnsyncedAlpha };
        var rows = new Element[doc.Lines.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new BoxEl
            {
                Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
                Padding = new Edges4(m.SidePad, m.RowPad, m.SidePad, m.RowPad),
                Children =
                [
                    new TextEl(doc.Lines[i].Text)
                    {
                        Size = m.FontSize, Weight = m.Weight, Wrap = TextWrap.Wrap, LineHeight = m.LineHeight,
                        Color = ink, MaxLines = 0, Trim = TextTrim.None,
                    },
                ],
            };
        float pad = Lyrics.Surface.UnsyncedBlockPad(host.Large);
        return new ScrollEl
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, SuppressScrollBar = true,
            ScrollKey = "lyrics:unsynced:" + doc.TrackId,
            Content = new BoxEl { Direction = 1, Padding = new Edges4(0f, pad, 0f, pad), Children = rows },
        };
    }

    /// <summary>A timed document: the measured virtual list over the host's layout, one keyed row per line.</summary>
    static Element Lines(ILyricsHost host, Lyrics.Doc doc)
    {
        var lines = doc.Lines;
        var layout = host.EnsureLayout();
        return Virtual.Custom(
            lines.Count,
            layout,
            i =>
            {
                int idx = i;
                var line = lines[idx];
                var emphasis = host.EmphasisOf(idx);
                return Embed.Comp(() => new ZuneLyricsRow(host, idx, line, emphasis)) with { Key = host.RowKey(idx) };
            },
            keyOf: host.RowKey) with
        {
            Grow = 1f,
            MinHeight = 0f,
            MeasureAll = lines.Count <= Lyrics.Surface.MeasureAllCap,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            Handle = host.Scroll,
            OnRealized = host.SetViewport,
        };
    }

    /// <summary>The podcast peek: the transcript's opening lines, progressively blurred and transparent, with the gate on the dissolve.</summary>
    static Element Peek(ILyricsHost host, Lyrics.Doc doc)
    {
        var m = host.Metrics;
        bool blur = host.BlurEnabled;
        int count = Lyrics.TranscriptPeek.LineCount(doc.Lines.Count);
        var bands = new List<Element>(Lyrics.TranscriptPeek.Bands);
        var rows = new List<Element>(count);
        int band = 0;
        for (int i = 0; i < count; i++)
        {
            int next = Lyrics.TranscriptPeek.BandOf(i, count);
            if (next != band)
            {
                if (rows.Count > 0) bands.Add(PeekBand(band, rows, blur));
                rows.Clear();
                band = next;
            }
            rows.Add(PeekRow(host, doc.Lines[i].Text, m));
        }
        if (rows.Count > 0) bands.Add(PeekBand(band, rows, blur));
        return new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true, ZStack = true,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, MinHeight = 0f, MinWidth = 0f, Direction = 1, Justify = FlexJustify.Start,
                    Padding = new Edges4(0f, Lyrics.Surface.UnsyncedBlockPad(host.Large), 0f, 0f),
                    Children = bands.ToArray(),
                },
                PeekGate(host),
            ],
        };
    }

    static Element PeekBand(int band, List<Element> rows, bool blur) => new BoxEl
    {
        Direction = 1, Shrink = 0f, MinWidth = 0f, HitTestVisible = false,
        Blur = blur ? Lyrics.TranscriptPeek.SigmaOf(band) : 0f,
        Opacity = Lyrics.TranscriptPeek.OpacityOf(band),
        Children = rows.ToArray(),
    };

    static Element PeekRow(ILyricsHost host, string text, Lyrics.RowMetrics m) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
        Padding = new Edges4(m.SidePad, m.RowPad, m.SidePad, m.RowPad),
        Children =
        [
            new TextEl(text)
            {
                Size = m.FontSize, Weight = m.Weight, Wrap = TextWrap.Wrap, LineHeight = m.LineHeight,
                Color = host.Ink.Primary, MaxLines = 0, Trim = TextTrim.None,
            },
        ],
    };

    static Element PeekGate(ILyricsHost host)
    {
        var ink = host.Ink;
        var facts = host.PeekGate();
        var gate = new List<Element>(3)
        {
            R.Type.DenseMeta(Loc.Get(Strings.Podcast.PeekFollows)) with { Color = ink.Secondary, Wrap = TextWrap.Wrap, MaxLines = 0 },
            Button.Accent(Loc.Get(Strings.Podcast.PeekPlay), facts.Play),
        };
        if (facts.Meta.Length > 0)
            gate.Add(new TextEl(facts.Meta)
            {
                Size = ZuneListRules.LyricsPeekMetaSize, LineHeight = ZuneListRules.LyricsPeekMetaLine, Color = ink.Tertiary,
                Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            });
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, HitTestPassThrough = true,
            Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.L, 0f, Spacing.L, Spacing.XXL),
            Children = [new BoxEl { Shrink = 0f, MinWidth = 0f, Direction = 1, AlignItems = FlexAlign.Center, Gap = Spacing.S, Children = gate.ToArray() }],
        };
    }
}

/// <summary>One lyric line of Zune's synced list. Frozen at mount: the host, its index, its line and its emphasis signal (a document change
/// re-KEYS the row). The render tracks a value-gated shape memo over the emphasis and peeks the word; the opacity rung retargets the slab
/// spring from a mount-once signal effect, so the ramp moves without a render. The root's static opacity is the MOUNT seed only (the
/// spring owns the channel after realization).</summary>
internal sealed class ZuneLyricsRow : Component
{
    readonly ILyricsHost _host;
    readonly int _index;
    readonly Lyrics.Line _line;
    readonly Signal<int> _emphasis;
    readonly Func<int> _shape;
    readonly Action _retargetOpacity;
    readonly Action<NodeHandle> _onLine, _onDof;
    readonly Action _onClick;
    float _seedOpacity = float.NaN;

    static readonly SpringParams s_opacitySpring = SpringParams.FromResponse(0.18f, 1.0f);

    static readonly SpringParams s_scaleSpring = SpringParams.FromResponse(0.30f, 0.90f);

    public ZuneLyricsRow(ILyricsHost host, int index, Lyrics.Line line, Signal<int> emphasis)
    {
        _host = host;
        _index = index;
        _line = line;
        _emphasis = emphasis;
        _shape = () => Lyrics.RowRender.ShapeOf(emphasis.Value);
        _retargetOpacity = RetargetOpacity;
        _onLine = h => host.ReportLineNode(index, h);
        _onDof = h => host.ReportDofNode(index, h);
        _onClick = () => host.SeekToLine(index);
    }

    void RetargetOpacity()
    {
        bool podcast = _host.IsPodcast;
        bool ownsPlayback = !podcast || _host.ObservedPlaybackOwner;
        float to = Lyrics.TranscriptPresentation.RowOpacity(podcast, ownsPlayback, _emphasis.Value);
        var ctx = Context;
        if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
        anim.Spring(ctx.HostNode, AnimChannel.Opacity, to, s_opacitySpring);
    }

    public override Element Render()
    {
        var host = _host;
        int index = _index;
        var line = _line;
        int shape = UseComputed(_shape).Value;
        UseSignalEffect(_retargetOpacity);
        int e = _emphasis.Peek();
        bool podcast = host.IsPodcast;
        bool ownsPlayback = !podcast || host.ObservedPlaybackOwner;
        bool isActive = Lyrics.TranscriptPresentation.Active(podcast, ownsPlayback, e);
        bool neutral = Lyrics.TranscriptPresentation.Neutral(podcast, ownsPlayback);
        var ink = host.Ink;
        var m = host.Metrics;
        // the stage's lines in the board's rhythm (Z-Lyrics: the display face at 300, the line a ratio of the type, a tier's gap); the rail keeps the core's
        var facts = UseContext(ThemeFactsContext.Signal);
        var rhythm = host.OnStage && facts is not null && !host.IsPodcast ? ZuneStageTiers.StageOf(facts.Value.Tiers).LyricsRhythm : null;
        float laid = rhythm is null ? m.FontSize : MathF.Round(m.FontSize * rhythm.ActiveScale);
        if (rhythm is not null)
            m = m with { FontSize = laid, LineHeight = MathF.Round(laid * rhythm.LineRatio), RowPad = rhythm.RowPad, Weight = rhythm.Weight, SidePad = rhythm.ActiveScale > 1f ? 0f : m.SidePad };
        float reserve = Lyrics.RowRender.HasReserve(shape) ? Lyrics.Interlude.ReserveDip(host.Large) : 0f;
        if (float.IsNaN(_seedOpacity)) _seedOpacity = Lyrics.TranscriptPresentation.RowOpacity(podcast, ownsPlayback, e);
        float blur = host.Follow.Peek() == Lyrics.FollowMode.Following ? host.DofShownFor(index) : 0f;

        // The active line is the accent: the stage's own accent signal where it hands one, else the style's. A neutral surface (a reader
        // browsing an episode that does not play) highlights nothing.
        ColorF accent = host.Accent is { } stageAccent && isActive && !neutral ? stageAccent.Value : R.Ink.AccentAction;
        ColorF lineInk = ZuneListRules.LyricsLineInk(isActive && !neutral, accent, neutral ? ink.Secondary : ink.Primary);
        var weight = rhythm is null ? R.Type.LyricsLine("").ResolvedWeight : rhythm.Weight;

        Element main = new TextEl(line.Text)
        {
            Size = m.FontSize, Weight = weight, Wrap = TextWrap.Wrap, LineHeight = m.LineHeight,
            Color = lineInk, MaxLines = 0, Trim = TextTrim.None, OnRealized = _onLine,
            BrushTransitionMs = Design.Motion.Fast,   // the rest <-> accent flip never snaps in one frame
        };
        if (rhythm is not null) main = ((TextEl)main) with { FontFamily = R.Type.DisplayFace, CharSpacing = rhythm.Tracking };

        string? secondaryText = host.SecondaryLine.Value switch
        {
            Lyrics.Prefs.Translation => NonEmpty(line.Translation),
            Lyrics.Prefs.Romanization => NonEmpty(line.Romanization),
            _ => null,
        };
        Element[] column = secondaryText is null
            ? [main]
            : [main, new TextEl(secondaryText)
              {
                  Size = m.FontSize * Lyrics.Surface.SecondaryFontRatio, Weight = podcast ? weight : ZuneListRules.LyricsTranslationWeight,
                  Wrap = TextWrap.Wrap, LineHeight = m.LineHeight * Lyrics.Surface.SecondaryFontRatio,
                  Color = ink.Secondary, MaxLines = 0, Trim = TextTrim.None,
                  Margin = new Edges4(0f, Lyrics.Surface.SecondaryGapDip, 0f, 0f),
              }];

        // a row that is not sung rests at the core's size: its scale springs 1 / ActiveScale <-> 1 from the left edge, the layout never moves (the row has no side padding while it scales, so its left edge is the text's)
        float rest = rhythm is null || rhythm.ActiveScale <= 1f || isActive || neutral ? 1f : 1f / rhythm.ActiveScale;
        var scaleKey = DepKey.From((isActive ? 2 : 0) | (neutral ? 16 : 0));
        UseSpring(AnimChannel.ScaleX, rest, s_scaleSpring, scaleKey);
        UseSpring(AnimChannel.ScaleY, rest, s_scaleSpring, scaleKey);

        return new BoxEl
        {
            Direction = 1,
            ScaleX = rest, ScaleY = rest, TransformOriginX = 0f, TransformOriginY = 0.5f,
            Opacity = _seedOpacity,
            Shrink = 0f,
            Padding = new Edges4(m.SidePad, m.RowPad + reserve, m.SidePad, m.RowPad),
            Justify = FlexJustify.Center,
            AlignItems = FlexAlign.Stretch,
            Cursor = CursorId.Hand,
            OnClick = _onClick,
            Role = AutomationRole.Button,
            Focusable = true,
            AllowFocusOnInteraction = false,
            Children = [new BoxEl { Direction = 1, Blur = blur, OnRealized = _onDof, Children = column }],
        };
    }

    static string? NonEmpty(string? s) => s is { Length: > 0 } ? s : null;
}
