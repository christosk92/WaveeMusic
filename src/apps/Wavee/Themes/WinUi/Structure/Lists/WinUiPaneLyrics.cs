// ── Themes/WinUi/Structure/Lists/WinUiPaneLyrics.cs — the side pane's synced lyrics (board W-PaneLyrics; P6 player polish) ────────────────
//
// The rail's own form of the lines (`LyricsLinesStyle.Rail` = `WinUiPaneLyricsRules.Form`; the core host says `ILyricsHost.OnRail`). The stage
// keeps `WinUiLyricsRow` (its blur ladder, halo and pill); the pane reads like the board instead:
//
//   ink      a sung line in the tertiary ink, an upcoming one in the secondary, the active line in the primary; a word-synced active line
//            draws its sung words in the primary and its unsung ones in the tertiary (the secondary on the mini's picture), so the wipe
//            reads as it moves; no halo
//   fade     every line but the active one faded by its distance from it (`WinUiPaneLyricsRules.RowOpacity`; a sung line dimmer than an
//            upcoming one), retargeted on a spring from this row's own emphasis without a render
//   blur     the core's rail depth-of-field ladder at the user's blur strength, written on the content box this row reports (and re-declared
//            from the ramp's live value on a render, so a shape render never stomps a σ in flight)
//   type    18/24 semibold; the active line reads 20/28 as a springing 20/18 scale of the same run, left-anchored, so a hand-off never
//            re-wraps a row or re-lays the column out (the trailing room it grows into is reserved on every row)
//   column   24 either side, 14 between lines; the core follows the active line at the form's band (a quarter down)
//   second   the translation / romanization under every row (14/20, secondary), so a row's height never depends on being active
//   mini     the mini player's mount (`ILyricsHost.FlatRows`): every row but the active one draws the primary ink and is faded to
//            `Lyrics.MiniRows.Ink` by its OPACITY (`Lyrics.MiniRows.Opacity`, the contract's way, as Classic and Zune do), never by a dimmed ink:
//            a faded context line, not body copy written in a 42 % ink (the core gives that mount no depth blur).
//
// The root's static `Opacity` is the MOUNT seed and never changes afterwards, so the reconciler's column diff never rewrites the channel: the
// fade spring owns it outright after realization.
//
// THE CORE'S HANDLES. The row reports its text node (the render-thread wipe) and its content box (the hand-off cascade's compensating
// translate and the depth blur's σ ride it: without the translate the column would jump on every line). No halo node: the core skips a
// missing one.

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
            AutoEdgeFade = !host.BoxFades,
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
    readonly Signal<int> _emphasis;
    readonly Func<int> _shape;
    readonly Action _retargetOpacity;
    float _seedOpacity = float.NaN;   // the root's static Opacity, fixed at the first render (the fade spring owns the channel after)
    readonly Action<NodeHandle> _onLine, _onDof;
    readonly Action _onClick;

    static readonly SpringParams s_scaleSpring = new(100f, 2f * MathF.Sqrt(100f * 2f), 2f);
    /// <summary>The row fade: critically damped, settling in the pane's own colour-fade time (<see cref="Design.Motion.Fast"/>).</summary>
    static readonly SpringParams s_fadeSpring = SpringParams.FromResponse(Design.Motion.Fast / 1000f);

    public WinUiPaneLyricsRow(ILyricsHost host, int index, Lyrics.Line line, Signal<int> emphasis)
    {
        _host = host;
        _index = index;
        _line = line;
        _emphasis = emphasis;
        // The render tracks only what changes the picture: active, sung (past) and the interlude reserve.
        _shape = () =>
        {
            int e = emphasis.Value;
            int s = Lyrics.Emphasis.DistOf(e) == 0 ? ActiveBit : 0;
            if ((e & Lyrics.Emphasis.PastBit) != 0) s |= PastBit;
            if (Lyrics.Emphasis.HasReserve(e)) s |= ReserveBit;
            return s;
        };
        _retargetOpacity = RetargetOpacity;
        _onLine = h => host.ReportLineNode(index, h);
        _onDof = h => host.ReportDofNode(index, h);
        _onClick = () => host.SeekToLine(index);
    }

    /// <summary>The row's fade for its packed emphasis: the mini player's flat picture where its mount asks for one
    /// (<see cref="Lyrics.MiniRows.Opacity"/>), else the pane's distance ladder (<see cref="PR.RowOpacity"/>).</summary>
    float RowOpacity(int emphasis)
    {
        float flat = Lyrics.MiniRows.Opacity(_host, emphasis, 1f);
        if (!float.IsNaN(flat)) return flat;
        return _host.FlatRows ? 1f : PR.RowOpacity(Lyrics.Emphasis.DistOf(emphasis), (emphasis & Lyrics.Emphasis.PastBit) != 0);
    }

    /// <summary>The fade, delivered to the slab without a render: tracks THIS line's packed word (its distance changes on every line, the shape
    /// only on its own hand-off). The mount run (inside the first render) finds no host node yet and leaves the static seed in place; the first
    /// retarget after realization springs from that seed.</summary>
    void RetargetOpacity()
    {
        float to = RowOpacity(_emphasis.Value);
        var ctx = Context;
        if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
        anim.Spring(ctx.HostNode, AnimChannel.Opacity, to, s_fadeSpring);
    }

    public override Element Render()
    {
        var host = _host;
        var line = _line;
        int shape = UseComputed(_shape).Value;
        UseSignalEffect(_retargetOpacity);
        bool podcast = host.IsPodcast;
        bool active = (shape & ActiveBit) != 0;
        bool past = !active && (shape & PastBit) != 0;
        float reserve = (shape & ReserveBit) != 0 ? Lyrics.Interlude.ReserveDip(host.Large) : 0f;

        // The active line grows (a spring on the scale, never a re-layout).
        // The scale's origin is the row's left edge; the matching translate keeps the run's first glyph on the column's 24 (both spring on the
        // same parameters from the same edge, so they move as one).
        float scale = active ? PR.ActiveScale : 1f;
        // A mount in a box narrower than the pane (the mini player, #197) scales the type and its rhythm and lays the rows on its gutter.
        float k = host.TypeScale, side = host.Gutter ?? PR.SidePad;
        float shift = active ? -side * (PR.ActiveScale - 1f) : 0f;
        var key = DepKey.From(active ? 1 : 0);
        UseSpring(AnimChannel.ScaleX, scale, s_scaleSpring, key);
        UseSpring(AnimChannel.ScaleY, scale, s_scaleSpring, key);
        UseSpring(AnimChannel.TranslateX, shift, s_scaleSpring, key);
        // Compact is the touch tier: a one-line row is a 40 target there (16 between lines instead of the board's 14).
        var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
        float rowPad = MathF.Round(PR.RowPadOf(facts.Policy is null ? WidthTier.Standard : facts.Window) * k);

        var ink = host.Ink;   // the theme's text rungs on the pane; the stage's arm where a mount reads over imagery (the mini player's)
        // The mini player's picture of the song (ILyricsHost.FlatRows): every line but the active one at one ink, sung or not: the primary ink,
        // faded by the row's opacity (the contract's Lyrics.MiniRows.Opacity). A 42 % ink at full opacity drew the same pixels but declared
        // the context lines body copy, which no veil can hold at 4.5:1 (white at 42 % reads 3.9:1 even on black).
        ColorF rest = host.FlatRows ? ink.Primary : past ? ink.Tertiary : ink.Secondary;
        if (float.IsNaN(_seedOpacity)) _seedOpacity = RowOpacity(_emphasis.Peek());
        // The depth blur's σ as the core's ramp holds it now (0 while the user owns the scroll), so a shape render re-asserts the live value.
        float blur = host.Follow.Peek() == Lyrics.FollowMode.Following ? host.DofShownFor(_index) : 0f;
        Element text;
        if (line.IsWordByWord && line.Syllables.Count > 0)
        {
            // Always a wiped run (an in-place update when the line leaves the voice slot, never a node swap); off the active line both sides
            // carry the same ink, so the core's settled split shows nothing.
            float split = active ? Lyrics.Wipe.SplitOf(host, _index, line, (long)host.Now.Peek(), lead: !podcast) : 0f;
            ColorF sung = active ? ink.Primary : rest;
            // The active line's unsung words in the tertiary ink on the pane: in the secondary (the upcoming lines' ink) the sung and unsung
            // halves of the line differed by a fifth of white and the wipe did not read; the upcoming lines' fade keeps them under these words.
            // The mini player's picture keeps the secondary: its context lines show 42 % of white, which the tertiary would sink into.
            ColorF unsung = active ? host.FlatRows ? ink.Secondary : ink.Tertiary : rest;
            text = LineText(line.Text, sung, new GlyphWipe(Before: sung, After: unsung, Split: split, Softness: host.SoftnessOfLine(_index), Lift: 0f));
        }
        else
        {
            text = LineText(line.Text, active ? ink.Primary : rest, null) with { BrushTransitionMs = Design.Motion.Fast };
        }

        string? second = host.SecondaryLine.Value switch
        {
            Lyrics.Prefs.Translation => NonEmpty(line.Translation),
            Lyrics.Prefs.Romanization => NonEmpty(line.Romanization),
            _ => null,
        };

        // The content box the core's hand-off cascade translates and its depth-of-field ramp blurs (reported once); the secondary line sits
        // inside it, so it blurs and travels with its lyric.
        var content = new BoxEl
        {
            Direction = 1, Blur = blur, OnRealized = _onDof,
            Children = second is null
                ? [text]
                : [text, new TextEl(second)
                {
                    Size = PR.SecondSize * k, LineHeight = MathF.Round(PR.SecondLine * k), Weight = PR.SecondWeight, Wrap = TextWrap.Wrap, MaxLines = 0,
                    Trim = TextTrim.None, Color = ink.Secondary, Margin = new Edges4(0f, PR.SecondGap * k, 0f, 0f),
                }],
        };

        return new BoxEl
        {
            Direction = 1,
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = shift,
            Opacity = _seedOpacity,
            Shrink = 0f,
            // PAD, not margin: the measured seam reads the border box. The trailing room keeps the grown active run inside the column.
            Padding = new Edges4(side, rowPad + reserve, side + PR.ScaleRoom * k, rowPad),
            Justify = FlexJustify.Center,
            AlignItems = FlexAlign.Stretch,
            TransformOriginX = 0f,
            TransformOriginY = 0.5f,
            // A mount whose lines are a picture (the mini player's, ILyricsHost.Inert) takes no click: the window's click and wheel are its own.
            Cursor = _host.Inert ? null : CursorId.Hand,
            OnClick = _host.Inert ? null : _onClick,
            Role = _host.Inert ? AutomationRole.None : AutomationRole.Button,
            Focusable = !_host.Inert,
            AllowFocusOnInteraction = false,
            Children = [content],
        };

        TextEl LineText(string s, ColorF color, GlyphWipe? wipe) => new(s)
        {
            Size = PR.LineSize * k, Weight = PR.LineWeight, Wrap = TextWrap.Wrap, LineHeight = MathF.Round(PR.LineHeight * k),
            Color = color, MaxLines = 0, Trim = TextTrim.None, Wipe = wipe, OnRealized = _onLine,
        };
    }

    static string? NonEmpty(string? s) => s is { Length: > 0 } ? s : null;
}
