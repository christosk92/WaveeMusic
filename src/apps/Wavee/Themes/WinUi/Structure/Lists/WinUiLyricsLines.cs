// ── Themes/WinUi/Structure/Lists/WinUiLyricsLines.cs — WinUI's lyrics lines style (themes plan 6.2; P6 W3) ───────────────
//
// From `Shell/Lyrics.UI.cs`: the `LyricsLinesStyle` the structure registers under `StyleKeys.LyricsLines`. The core host (`Lyrics.ViewCore`)
// decides which face the document gets and calls these pictures, handing itself as the `ILyricsHost`:
//
//   Message    a centred one-line state ("No lyrics available", "Nothing playing", "No transcript")
//   Shimmer    the loading bars, at the exact side pad the first lines will take (flat: nothing on them moves)
//   Skeleton   the skeleton region's style
//   Unsynced   an untimed (or suppressed) document: a reading block at reading type, left-aligned, wrapped, never ellipsised - no
//              highlight, no follow, no wipe, no blur ladder
//   Lines      a timed document: the measured virtual list and its rows (`WinUiLyricsRow`)
//
// THE ROW. One lyric line. Frozen at mount: the host, its index, its `Lyrics.Line`, its emphasis signal and its halo signal - a genuine
// document change re-KEYS the row. Live values only through signals: its OWN packed emphasis (the render tracks a value-gated
// `RowRender.ShapeOf` memo over it and PEEKS the word for the rest seeds; the opacity rung retargets the slab spring from a mount-once
// signal effect), the host's shared `Now` (peeked), `SecondaryLine` (`.Value`), `HaloScale` (`.Value`, inside the active branch only) and the
// follow mode (peeked). Every handler, bind and spring spec is a mount-once field: a render allocates the element records and their
// children arrays, nothing else.
//
// The root's static `Opacity` is the MOUNT seed and never changes afterwards (`_seedOpacity`), so the reconciler's column diff never
// rewrites the channel: after realization the spring owns it outright (the slab writes every tick and its settled value back into the paint
// column), and a structural re-render cannot snap a row mid-fade or stomp a rung the spring already reached without a render.

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
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's lyrics lines: the state line, the loading bars, the unsynced block and the synced list with its rows.</summary>
internal static class WinUiLyricsLines
{
    /// <summary>The style WinUI registers under <c>StyleKeys.LyricsLines</c>. Stateless: the rail, the stage and the reader share it.</summary>
    public static LyricsLinesStyle Style { get; } = new(Message, Shimmer, Skeleton, Unsynced, Lines) { Offline = Offline, StageDepthBlur = true, Rail = WinUiPaneLyricsRules.Form,
        StageRows = WinUiLyricsRules.StageRows };

    // ── the state line and the loading bars ─────────────────────────────────────────────────────────────────────────

    /// <summary>A centred one-line state ("No lyrics available", "Nothing playing").</summary>
    static Element Message(ILyricsHost host, string text) => new BoxEl
    {
        // Shrink: the view's other root, in the same row slot (G-254).
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.XXL, 0f, Spacing.XXL, 0f),
        Children =
        [
            new TextEl(text)
            {
                Size = WinUiLyricsRules.MessageSize, LineHeight = WinUiLyricsRules.MessageLineHeight,
                Color = host.Ink.Secondary, Wrap = TextWrap.Wrap,
            },
        ],
    };

    /// <summary>The OFFLINE voice (tuned R2, board C-Rail state=offline): "Lyrics need a connection", the one-sentence reason and a quiet
    /// Retry, centred in the reading surface on the surface's own ink (the rail's theme rungs, the stage's ladder). The vacancy grammar:
    /// headline, caption, at most one Standard action.</summary>
    static Element Offline(ILyricsHost host, Action retry) => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Gap = Spacing.XS, Padding = Edges4.All(Spacing.XXL),
        Children =
        [
            Ui.Subtitle(Loc.Get(Strings.Lyrics.Offline.Title)) with { Color = host.Ink.Primary, Wrap = TextWrap.Wrap },
            R.Type.TrackMeta(Loc.Get(Strings.Lyrics.Offline.Body)) with
            {
                Color = host.Ink.Secondary, Wrap = TextWrap.Wrap, MaxWidth = WinUiLyricsRules.OfflineCaptionMaxW,
            },
            new BoxEl { Height = Spacing.L },
            Button.Standard(Loc.Get(Strings.Common.Retry), retry),
        ],
    };

    /// <summary>The loading bars, at the exact side pad the first lines will take. They are flat: nothing on them
    /// animates, and the engine adds no motion to a placeholder.</summary>
    static Element Shimmer(ILyricsHost host)
    {
        bool large = host.Large;
        var rows = new Element[Lyrics.Surface.ShimmerRows];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new BoxEl
            {
                Width = Lyrics.Surface.ShimmerBarW(large, i), Height = Lyrics.Surface.ShimmerRowH(large),
                Corners = CornerRadius4.All(Lyrics.Surface.ShimmerRadius),
                Fill = host.Ink.Skeleton,
                AlignSelf = FlexAlign.Start,
            };
        }
        return new BoxEl
        {
            Grow = 1f, MinHeight = 0f, Direction = 1, Gap = Lyrics.Surface.ShimmerGap(large),
            Padding = new Edges4(host.Metrics.SidePad, Lyrics.Surface.ShimmerPadTop(large), host.Metrics.SidePad, 0f),
            Children = rows,
        };
    }

    /// <summary>The derived-shimmer style is INERT under a custom shimmer source - the visible gap is <see cref="Shimmer"/>'s own.</summary>
    static SkeletonStyle Skeleton(ILyricsHost host)
        => new(host.Ink.Skeleton, RowGap: Lyrics.Surface.ShimmerGap(host.Large), BarRadius: Lyrics.Surface.ShimmerRadius,
            TextRatio: WinUiLyricsRules.SkeletonTextRatio);

    // ── the unsynced block ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>An untimed (or suppressed) document: a reading block at reading type, left-aligned, wrapped, never ellipsised - no
    /// highlight, no follow, no wipe, no blur ladder.</summary>
    static Element Unsynced(ILyricsHost host, Lyrics.Doc doc)
    {
        // A mount in a small box (the mini player's: ILyricsHost.Gutter) reads its unsynced lines at the pane rows' type (the rows it draws when
        // the song is timed, WinUiPaneLyrics) on its own gutter, so the face keeps one column and one size whether or not the song is timed.
        var m = host.Gutter is { } gutter
            ? new Lyrics.RowMetrics(WinUiPaneLyricsRules.LineSize * host.TypeScale, MathF.Round(WinUiPaneLyricsRules.LineHeight * host.TypeScale),
                MathF.Round(WinUiPaneLyricsRules.RowPad * host.TypeScale), gutter, WinUiPaneLyricsRules.LineWeight)
            : Lyrics.Surface.Unsynced(host.Large, host.IsPodcast).Scaled(host.TypeScale);
        var ink = host.Ink.Primary with { A = Lyrics.Surface.UnsyncedAlpha };   // an ABSOLUTE alpha, as 0.2.9 set it
        var rows = new Element[doc.Lines.Count];
        for (int i = 0; i < rows.Length; i++)
        {
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
        }
        float pad = Lyrics.Surface.UnsyncedBlockPad(host.Large);
        return new ScrollEl
        {
            Grow = 1f, MinHeight = 0f, AutoEdgeFade = !host.BoxFades, SuppressScrollBar = true,
            ScrollKey = "lyrics:unsynced:" + doc.TrackId,
            Content = new BoxEl { Direction = 1, Padding = new Edges4(0f, pad, 0f, pad), Children = rows },
        };
    }

    // ── the synced list ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A timed document: the measured virtual list over the host's layout, one keyed row per line.</summary>
    static Element Lines(ILyricsHost host, Lyrics.Doc doc)
    {
        if (host.OnRail) return WinUiPaneLyrics.Lines(host, doc);   // the side pane's own crisp form (W-PaneLyrics)
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
                var glow = host.GlowOf(idx);
                return Embed.Comp(() => new WinUiLyricsRow(host, idx, line, emphasis, glow)) with { Key = host.RowKey(idx) };
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

/// <summary>One lyric line of the synced list (see the file header).</summary>
internal sealed class WinUiLyricsRow : Component
{
    readonly ILyricsHost _host;
    readonly int _index;
    readonly Lyrics.Line _line;
    readonly Signal<int> _emphasis;
    readonly Prop<float> _glowOpacity;
    readonly Func<int> _shape;
    readonly Action _retargetOpacity;
    readonly Action<NodeHandle> _onLine, _onGlow, _onDof;
    readonly Action _onClick;
    float _seedOpacity = float.NaN;   // the root's static Opacity, fixed at the first render

    // Critically damped at AMLL's stiffness/mass; the brightness hand-off is symmetric (one response).
    static readonly SpringParams s_opacitySpring = SpringParams.FromResponse(0.18f, 1.0f);
    static readonly SpringParams s_scaleSpring = new(100f, 2f * MathF.Sqrt(100f * 2f), 2f);

    public WinUiLyricsRow(ILyricsHost host, int index, Lyrics.Line line, Signal<int> emphasis, FloatSignal? glowFade)
    {
        _host = host;
        _index = index;
        _line = line;
        _emphasis = emphasis;
        _glowOpacity = GlowOpacityOf(glowFade, host.Ink.BloomScale);
        _shape = () => Lyrics.RowRender.ShapeOf(emphasis.Value);
        _retargetOpacity = RetargetOpacity;
        _onLine = h => host.ReportLineNode(index, h);
        _onGlow = h => host.ReportGlowNode(index, h);
        _onDof = h => host.ReportDofNode(index, h);
        _onClick = () => host.SeekToLine(index);
    }

    /// <summary>The halo layer's opacity: the host's per-line fade signal, bound (a per-frame write is a compositor-only column
    /// store), scaled by the ink's bloom once at mount.</summary>
    static Prop<float> GlowOpacityOf(FloatSignal? glowFade, float k)
    {
        if (glowFade is not { } s) return 0f;
        return k >= 1f ? (Prop<float>)s : Prop.Of(() => s.Value * k);
    }

    /// <summary>The opacity rung, delivered to the slab without a render. Tracks THIS line's packed word only; the mount run (inside
    /// the first render) finds no host node yet and leaves the static seed in place - the first retarget after realization springs
    /// from that seed (velocity-continuous on a live track).</summary>
    void RetargetOpacity()
    {
        float to = RowOpacity(_host, _host.IsPodcast, _emphasis.Value);
        var ctx = Context;
        if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
        anim.Spring(ctx.HostNode, AnimChannel.Opacity, to, s_opacitySpring);
    }

    /// <summary>The row's opacity rung: the stage's music rows take the board's fade over the ring (<see cref="WinUiLyricsRules.StageOpacity"/>,
    /// transparent past it), every other surface the core's ladder.</summary>
    static float RowOpacity(ILyricsHost host, bool podcast, int emphasis)
        => BoardRows(host, podcast)
            ? WinUiLyricsRules.StageOpacity(Lyrics.Emphasis.DistOf(emphasis))
            : Lyrics.Emphasis.OpacityOf(emphasis);

    /// <summary>The stage's music rows draw the board (W-Lyrics): the type, fade, scale and ink of <see cref="WinUiLyricsRules.StageRows"/>.</summary>
    static bool BoardRows(ILyricsHost host, bool podcast) => host.OnStage && !podcast;

    public override Element Render()
    {
        var host = _host;
        int index = _index;
        var line = _line;
        // The render tracks the SHAPE (a memo that notifies only when a tree-changing bit flips) and peeks the word.
        int shape = UseComputed(_shape).Value;
        UseSignalEffect(_retargetOpacity);
        int e = _emphasis.Peek();
        bool podcast = host.IsPodcast;
        bool isActive = Lyrics.Emphasis.DistOf(e) == 0;
        bool near = Lyrics.RowRender.IsNear(shape);
        bool large = host.Large;
        var ink = host.Ink;
        var m = host.Metrics;
        float reserve = Lyrics.RowRender.HasReserve(shape) ? Lyrics.Interlude.ReserveDip(large) : 0f;

        // Distance is carried by OPACITY + DoF; scale is a flat 0.98, left-anchored. Reduced motion is a VALUE folded into the
        // spring key, so the first render after an OS flip retargets.
        bool reduce = Design.Reduced;
        bool board = BoardRows(host, podcast);
        // The board's rows lay out at the ACTIVE size and an inactive row scales down to the line size (28 under 34): the step glides on the
        // scale spring, the row never relayouts.
        float scale = board ? (isActive || reduce ? 1f : WinUiLyricsRules.StageInactiveScale) : Lyrics.Surface.ScaleFor(isActive, reduce);
        if (float.IsNaN(_seedOpacity)) _seedOpacity = RowOpacity(host, podcast, e);
        float blur = host.Follow.Peek() == Lyrics.FollowMode.Following ? host.DofShownFor(index) : 0f;

        // The scale springs retarget on the active flip (the only input of the scale rest), never per rung.
        var key = DepKey.From((isActive ? 2 : 0) | (reduce ? 8 : 0));
        UseSpring(AnimChannel.ScaleX, scale, s_scaleSpring, key);
        UseSpring(AnimChannel.ScaleY, scale, s_scaleSpring, key);

        Element textEl;
        float lift = Lyrics.Wipe.LiftFor(large, reduce);
        if (line.IsWordByWord && line.Syllables.Count > 0)
        {
            // ALWAYS a two-child ZStack [glow, main], in every state: a line leaving the voice slot is an in-place
            // update, never a child-type swap (a re-shape + blur-cache miss = the lines-above-active flicker).
            float split = Lyrics.Wipe.SplitOf(host, index, line, (long)host.Now.Peek(), lead: !podcast);
            float softness = host.SoftnessOfLine(index);
            // Podcasts: the role-driven rung (accent on the sung glyph, plain ink otherwise - never a translucent wash). Music
            // keeps its established alpha-faded unsung look untouched.
            ColorF sung = podcast ? RungInk(ink, Lyrics.TranscriptPresentation.InkOf(Lyrics.TranscriptPresentation.RoleOf(e), sungPart: true))
                : ink.Primary;
            ColorF unsung = podcast ? RungInk(ink, Lyrics.TranscriptPresentation.InkOf(Lyrics.TranscriptPresentation.RoleOf(e), sungPart: false))
                : board && !isActive ? sung   // the board's far lines are plain ink at their row's fade; only the active line dims its unsung part
                : sung with { A = Lyrics.Wipe.UnsungAlpha };
            var mainWipe = new GlyphWipe(Before: sung, After: unsung, Split: split, Softness: softness, Lift: lift);
            Element main = LineText(line.Text, sung, mainWipe, _onLine);
            // The bloom glyphs mount only NEAR the focus (still dim + blurred, so the swap never pops on the focal row); same
            // split, feather and lift as the main layer, so the halo never floats out from under it.
            var bloom = ink.Bloom;
            var glowWipe = new GlyphWipe(Before: bloom, After: bloom with { A = 0f }, Split: split, Softness: softness, Lift: lift);
            Element glowText = near ? LineText(line.Text, bloom, glowWipe, _onGlow) : LineText("", bloom, null, _onGlow);
            Element glow = new BoxEl { Opacity = _glowOpacity, HitTestVisible = false, Children = [glowText] };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }
        else
        {
            // Line-level lyrics: the same persistent ZStack. σ on the ACTIVE row only - at dist >= 1 the parent already carries a
            // DoF σ and a nested blur is a second offscreen blur pass.
            Element glow = new BoxEl
            {
                Blur = isActive ? Lyrics.Surface.LineSyncedHaloSigma(large) * host.HaloScale.Value : 0f,
                Opacity = _glowOpacity,
                HitTestVisible = false,
                Children = [LineText(near ? line.Text : "", ink.Bloom with { A = Lyrics.Surface.LineSyncedHaloTextAlpha }, null, null)],
            };
            // Podcasts: the same role rule as the word-synced branch (the unsung/"After" reading - this branch never shows the
            // sung/"Before" rung, there is no wipe). Music keeps its plain active/inactive split.
            ColorF lineInk = podcast
                ? RungInk(ink, Lyrics.TranscriptPresentation.InkOf(Lyrics.TranscriptPresentation.RoleOf(e), sungPart: false))
                : isActive || board ? ink.Primary : ink.Secondary;
            Element main = LineText(line.Text, lineInk, null, _onLine) with
            {
                BrushTransitionMs = Design.Motion.Fast,   // the Secondary<->Primary flip never snaps in one frame
            };
            textEl = new BoxEl { ZStack = true, Children = [glow, main] };
        }

        // The secondary layer, read from the shared mode signal (never a frozen int).
        string? secondaryText = host.SecondaryLine.Value switch
        {
            Lyrics.Prefs.Translation => NonEmpty(line.Translation),
            Lyrics.Prefs.Romanization => NonEmpty(line.Romanization),
            _ => null,
        };

        // Own DoF on a persistent INNER wrapper, separate from the scale/opacity track owner - the σ ramp and the cascade write THIS
        // node. The secondary line sits INSIDE it, so it blurs, fades and travels with its lyric.
        var textColumn = new BoxEl { Direction = 1, Children = secondaryText is null ? [textEl] : [textEl, SecondaryText(secondaryText)] };
        // THE STAGE adds the ListView-style accent PILL as a ZStack layer INSIDE dofContent (it travels with the row's σ/cascade
        // owner). The accent is a mount-time signal INSTANCE on the host (null on the rail/reader => the plain column, unchanged),
        // so the tree shape never flips for a mounted row.
        Element dofContent = host.Accent is { } accentSig
            ? new BoxEl
            {
                ZStack = true,
                Blur = blur,
                OnRealized = _onDof,
                Children =
                [
                    textColumn,
                    // THE PILL (ListView accent bar): in the gutter, vertically centred on the row (V-U9: vertical = AlignSelf,
                    // horizontal = JustifySelf); accent on the active line, else clear, cross-faded through BrushTransitionMs like
                    // the ink flip above. `accentSig.Value` is evaluated ONLY on the active row - the conditional
                    // short-circuits before the read on every other row - so exactly ONE row subscribes to the slab's cross-fade
                    // and re-renders at 30 Hz for 600 ms per track change (V-U51).
                    new BoxEl
                    {
                        Width = WinUiLyricsRules.PillWidth, Height = WinUiLyricsRules.PillHeight,
                        Corners = Radii.Circle(WinUiLyricsRules.PillRadius), HitTestVisible = false,
                        AlignSelf = FlexAlign.Center, JustifySelf = FlexAlign.Start,
                        OffsetX = -(m.SidePad - Spacing.S),
                        Fill = isActive ? accentSig.Value : ColorF.Transparent,
                        BrushTransitionMs = Design.Motion.Fast,
                    },
                ],
            }
            : new BoxEl { Direction = 1, Blur = blur, OnRealized = _onDof, Children = textColumn.Children };

        return new BoxEl
        {
            Direction = 1,
            // The scale REST target: at settle the slab and the element agree, so a later shape render cannot snap the row or
            // flash a newly realized one. Opacity is the frozen mount seed (see the file header).
            ScaleX = scale,
            ScaleY = scale,
            Opacity = _seedOpacity,
            Shrink = 0f,
            // PAD, not margin: the measured seam reads the border box.
            Padding = new Edges4(m.SidePad, m.RowPad + reserve, m.SidePad, m.RowPad),
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
            Children = [dofContent],
        };

        // Wrapped, unbounded, never trimmed on BOTH surfaces; built ONCE with its wipe and its realization report (no `with` clone
        // per layer). `ViewCore` measures the wipe on this node's own laid style (`Lyrics.Wipe.DrawnLayout`).
        TextEl LineText(string text, ColorF color, GlyphWipe? wipe, Action<NodeHandle>? onRealized) => new(text)
        {
            Size = m.FontSize, Weight = m.Weight, Wrap = TextWrap.Wrap, LineHeight = m.LineHeight,
            Color = color, MaxLines = 0, Trim = TextTrim.None,
            Wipe = wipe, OnRealized = onRealized,
        };

        // No wipe, no glow, no lift (a translation is not sung); everything else inherited from dofContent.
        TextEl SecondaryText(string text) => new(text)
        {
            Size = m.FontSize * Lyrics.Surface.SecondaryFontRatio, Weight = podcast ? m.Weight : WinUiLyricsRules.SecondaryWeight, Wrap = TextWrap.Wrap,
            LineHeight = m.LineHeight * Lyrics.Surface.SecondaryFontRatio,
            Color = ink.Secondary, MaxLines = 0, Trim = TextTrim.None,
            Margin = new Edges4(0f, Lyrics.Surface.SecondaryGapDip, 0f, 0f),
        };
    }

    static string? NonEmpty(string? s) => s is { Length: > 0 } ? s : null;

    /// <summary>Resolve a pure <see cref="Lyrics.InkRung"/> against the live <see cref="Lyrics.Ink"/> - the one place a rung becomes
    /// a theme colour.</summary>
    static ColorF RungInk(Lyrics.Ink ink, Lyrics.InkRung rung) => rung switch
    {
        Lyrics.InkRung.Accent => ink.Accent,
        Lyrics.InkRung.Primary => ink.Primary,
        _ => ink.Secondary,
    };
}
