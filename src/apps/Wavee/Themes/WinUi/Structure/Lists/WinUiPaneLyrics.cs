// ── Themes/WinUi/Structure/Lists/WinUiPaneLyrics.cs — the side pane's synced lyrics (board W-PaneLyrics; P6 player polish) ────────────────
//
// The rail's own form of the lines (`LyricsLinesStyle.Rail` = `WinUiPaneLyricsRules.Form`; the core host says `ILyricsHost.OnRail`). The stage
// keeps `WinUiLyricsRow` (its blur ladder, halo and pill); the pane reads like the board instead:
//
//   ink      a sung line in the tertiary ink, an upcoming one in the secondary, the active line in the primary (a word-synced line wipes
//            from the primary into the tertiary as it is sung); no opacity ladder, no depth blur, no halo
//   type     18/24 semibold; the active line reads 20/28 as a springing 20/18 scale of the same run, left-anchored, so a hand-off never
//            re-wraps a row or re-lays the column out (the trailing room it grows into is reserved on every row)
//   column   24 either side, 14 between lines; the core follows the active line at the form's band (a quarter down)
//   second   the translation / romanization under every row (14/20, secondary), so a row's height never depends on being active
//
// THE CORE'S HANDLES. The row reports its text node (the render-thread wipe) and its content box (the hand-off cascade's compensating
// translate rides it: without it the column would jump on every line). No halo node: the core skips a missing one.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using Wavee.Theming;
using Wavee.Theming.Contracts;

using PR = Wavee.Themes.WinUi.WinUiPaneLyricsRules;

namespace Wavee.Themes.WinUi;

/// <summary>The side pane's synced lyrics list.</summary>
internal static class WinUiPaneLyrics
{
    /// <summary>A timed document on the pane: the measured virtual list over the host's layout, one keyed pane row per line.</summary>
    public static Element Lines(ILyricsHost host, Lyrics.Doc doc)
    {
        var lines = doc.Lines;
        var layout = host.EnsureLayout();
        return Virtual.Custom(
            lines.Count,
            layout,
            i =>
            {
                int idx = i;
                return Embed.Comp(() => new WinUiPaneLyricsRow(host, idx, lines[idx], host.EmphasisOf(idx))) with { Key = host.RowKey(idx) };
            },
            keyOf: host.RowKey) with
        {
            Grow = 1f,
            MinHeight = 0f,
            // Realize the WHOLE document: every row must be measured before follow geometry is trusted.
            MeasureAll = lines.Count <= Lyrics.Surface.MeasureAllCap,
            AutoEdgeFade = true,
            SuppressScrollBar = true,
            Handle = host.Scroll,
            OnRealized = host.SetViewport,
        };
    }
}

/// <summary>One lyric line of the pane (see the file header). Frozen at mount like the stage's row: a document change re-keys it.</summary>
internal sealed class WinUiPaneLyricsRow : Component
{
    const int ActiveBit = 1, PastBit = 2, ReserveBit = 4;

    readonly ILyricsHost _host;
    readonly int _index;
    readonly Lyrics.Line _line;
    readonly Func<int> _shape;
    readonly Action<NodeHandle> _onLine, _onDof;
    readonly Action _onClick;

    static readonly SpringParams s_scaleSpring = new(100f, 2f * MathF.Sqrt(100f * 2f), 2f);

    public WinUiPaneLyricsRow(ILyricsHost host, int index, Lyrics.Line line, Signal<int> emphasis)
    {
        _host = host;
        _index = index;
        _line = line;
        // The render tracks only what changes the picture: active, sung (past) and the interlude reserve.
        _shape = () =>
        {
            int e = emphasis.Value;
            int s = Lyrics.Emphasis.DistOf(e) == 0 ? ActiveBit : 0;
            if ((e & Lyrics.Emphasis.PastBit) != 0) s |= PastBit;
            if (Lyrics.Emphasis.HasReserve(e)) s |= ReserveBit;
            return s;
        };
        _onLine = h => host.ReportLineNode(index, h);
        _onDof = h => host.ReportDofNode(index, h);
        _onClick = () => host.SeekToLine(index);
    }

    public override Element Render()
    {
        var host = _host;
        var line = _line;
        int shape = UseComputed(_shape).Value;
        bool podcast = host.IsPodcast;
        bool ownsPlayback = !podcast || host.ObservedPlaybackOwner;
        bool neutral = Lyrics.TranscriptPresentation.Neutral(podcast, ownsPlayback);
        bool active = !neutral && (shape & ActiveBit) != 0;
        bool past = !neutral && !active && (shape & PastBit) != 0;
        float reserve = (shape & ReserveBit) != 0 ? Lyrics.Interlude.ReserveDip(host.Large) : 0f;

        // The active line grows (a spring on the scale, never a re-layout).
        // The scale's origin is the row's left edge; the matching translate keeps the run's first glyph on the column's 24 (both spring on the
        // same parameters from the same edge, so they move as one).
        float scale = active ? PR.ActiveScale : 1f;
        float shift = active ? -PR.SidePad * (PR.ActiveScale - 1f) : 0f;
        var key = DepKey.From(active ? 1 : 0);
        UseSpring(AnimChannel.ScaleX, scale, s_scaleSpring, key);
        UseSpring(AnimChannel.ScaleY, scale, s_scaleSpring, key);
        UseSpring(AnimChannel.TranslateX, shift, s_scaleSpring, key);
        // Compact is the touch tier: a one-line row is a 40 target there (16 between lines instead of the board's 14).
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        float rowPad = PR.RowPadOf(facts.Policy is null ? WidthTier.Standard : facts.Window);

        ColorF rest = neutral ? Tok.TextSecondary : past ? Tok.TextTertiary : Tok.TextSecondary;
        Element text;
        if (line.IsWordByWord && line.Syllables.Count > 0)
        {
            // Always a wiped run (an in-place update when the line leaves the voice slot, never a node swap); off the active line both sides
            // carry the same ink, so the core's settled split shows nothing.
            bool highlight = active && Lyrics.TranscriptPresentation.WordHighlight(podcast, ownsPlayback, line);
            float split = highlight ? Lyrics.Wipe.ComputeSplit(line, (long)host.Now.Peek()) : 0f;
            if (!podcast && split > 0f && split < 1f) split = Math.Clamp(split + Lyrics.Wipe.LeadFrac, 0f, 1f);
            ColorF sung = active ? Tok.TextPrimary : rest;
            ColorF unsung = active ? Tok.TextTertiary : rest;
            text = LineText(line.Text, sung, new GlyphWipe(Before: sung, After: unsung, Split: split, Softness: host.SoftnessOfLine(_index), Lift: 0f));
        }
        else
        {
            text = LineText(line.Text, active ? Tok.TextPrimary : rest, null) with { BrushTransitionMs = Design.Motion.Fast };
        }

        string? second = host.SecondaryLine.Value switch
        {
            Lyrics.Prefs.Translation => NonEmpty(line.Translation),
            Lyrics.Prefs.Romanization => NonEmpty(line.Romanization),
            _ => null,
        };

        // The content box the core's hand-off cascade translates (reported once; no blur: the pane is crisp).
        var content = new BoxEl
        {
            Direction = 1, OnRealized = _onDof,
            Children = second is null
                ? [text]
                : [text, new TextEl(second)
                {
                    Size = PR.SecondSize, LineHeight = PR.SecondLine, Weight = PR.SecondWeight, Wrap = TextWrap.Wrap, MaxLines = 0, Trim = TextTrim.None,
                    Color = Tok.TextSecondary, Margin = new Edges4(0f, PR.SecondGap, 0f, 0f),
                }],
        };

        return new BoxEl
        {
            Direction = 1,
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = shift,
            Shrink = 0f,
            // PAD, not margin: the measured seam reads the border box. The trailing room keeps the grown active run inside the column.
            Padding = new Edges4(PR.SidePad, rowPad + reserve, PR.SidePad + PR.ScaleRoom, rowPad),
            Justify = FlexJustify.Center,
            AlignItems = FlexAlign.Stretch,
            TransformOriginX = 0f,
            TransformOriginY = 0.5f,
            Cursor = CursorId.Hand,
            OnClick = _onClick,
            Role = AutomationRole.Button,
            Focusable = true,
            AllowFocusOnInteraction = false,
            Children = [content],
        };

        TextEl LineText(string s, ColorF color, GlyphWipe? wipe) => new(s)
        {
            Size = PR.LineSize, Weight = PR.LineWeight, Wrap = TextWrap.Wrap, LineHeight = PR.LineHeight,
            Color = color, MaxLines = 0, Trim = TextTrim.None, Wipe = wipe, OnRealized = _onLine,
        };
    }

    static string? NonEmpty(string? s) => s is { Length: > 0 } ? s : null;
}
