// ── Shell/Stage.Caption.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────
// The stage's lyric caption host: the playhead clock, the edge wake, the render-thread wipe and the break dots' driver
//
// Role: UI (the core half: the picture is the structure's `Stage.Caption` view, `Themes\Classic\Structure\Views\Stage\ClassicStageCaption.cs`, T7a1)
// Owner: K
// Wave: 7
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.10

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Stage
{
    // ══ 4. THE LYRIC CAPTION (over the face · Compact Lyrics mode's lyric line) ═════════════════════════════════════

    /// <summary>The lyric over the visualizer, presented as three lines: the PREVIOUS line above (small, ≈35 %), the ACTIVE line
    /// at the hero-title scale (40–56 DIP by height) and the NEXT line below (small, ≈45 %), centred in the face's region at the
    /// allocator's caption geometry (<c>Layout.CaptionX/W/Bottom</c> — the lower third, above the transport and hairline) over a
    /// soft elliptical veil, never a plate.
    /// <list type="bullet">
    /// <item>The active line carries the lyrics view's karaoke WIPE (<c>Wipe.ComputeSplit</c> + <c>LeadFrac</c>, its DIP feather
    /// over the measured run, its lift — LineRow's recipe) and a blurred accent BLOOM layer that follows the same split; a
    /// line-synced line has no wipe and fades in whole. No other line is blurred.</item>
    /// <item>A real instrumental break (and a long intro) shows the lyrics view's three breathing dots in the active slot
    /// (<c>Interlude.Progress/Pulse/DotAlpha/BreathScale</c>), until the next line resolves (<c>Stage.Caption.View</c>).</item>
    /// <item>Every slot is KEYED by its role + line index (+ the document's generation), so a hand-off plays the Exit (up and
    /// out) on the outgoing line and the Enter (up from 16 DIP below) on the incoming one — StandardEnter, 300 ms; reduced
    /// motion keeps only the cross-fade.</item>
    /// <item>While playing, the RENDER THREAD poses the active line's wipe (<c>AnimChannel.GlyphWipeSplit</c> keyframes,
    /// <c>Lyrics.Wipe.SplitKeyframes</c> — the exact split function, seeded once per line and again when a position report
    /// moves the clock), the lyrics view's channel; the UI wakes once per view EDGE (<c>Caption.NextEdgeMs</c>) and per
    /// report, not per frame. A break's dots still tick per frame (scene columns only). Paused, NOTHING ticks: the media
    /// clock stands still, so the view and the split change only on a position report (a seek) or a play/pause edge, and
    /// the report effect ticks exactly then. A render happens only when the packed view (line + break edge) changes.</item>
    /// </list></summary>
    sealed class CaptionHost : Component
    {
        const int Unresolved = int.MinValue;
        /// <summary>The packed view (<c>Stage.Caption.Pack</c>) — the ONE signal the render subscribes to; the tick writes it on change.</summary>
        readonly Signal<int> _view = new(Unresolved);
        readonly FloatSignal[] _dotAlpha = new FloatSignal[Lyrics.Interlude.DotCount];
        readonly Action _tick;
        readonly Action<NodeHandle> _onMain, _onGlow, _onDots;
        readonly CaptionClock _clock = new();   // the playhead the caption extrapolates + its edge wake (core)
        Lyrics.Doc? _doc;
        int _docGen;                          // bumps per document, so a new track's line 5 is a NEW keyed slot (its wipe nodes re-report)
        // the active line's two wipe nodes (main + bloom), the line they were built for, its measured run and the wrap width
        NodeHandle _mainNode, _glowNode, _dotsNode;
        int _wipeLine = -1;
        float _runLen = float.NaN, _wrapW = float.NaN;
        // The render-thread wipe rows (CaptionWipeRows).
        readonly CaptionWipeRows _rows = new();
        // The one-shot wake at the next view edge (Caption.EdgeWake, published to CaptionWake), the timer's callback, and
        // whether the report effect has had its mount run.
        readonly Signal<Lyrics.MotionWake> _edgeWake = new(new Lyrics.MotionWake(0, -1f));
        int _edgeSeq;
        readonly Action _onEdge;
        bool _reportSubscribed;

        public CaptionHost()
        {
            for (int k = 0; k < _dotAlpha.Length; k++) _dotAlpha[k] = new FloatSignal(0f);
            _tick = () => Reactive.Untrack(Tick);
            _onEdge = () => { _clock.FireEdge(); _tick(); };
            _onMain = h => _mainNode = h;
            _onGlow = h => _glowNode = h;
            _onDots = h => _dotsNode = h;
        }

        public override Element Render()
        {
            var ctx = UseContext(StageContext)!;
            var L = ctx.Layout.Value;
            var k = ctx.Look.Value;
            bool galleryShown = ctx.GalleryShown.Value;
            var set = ctx.Accent.Value;
            _ = Lyrics.Store.Changed.Value;
            var track = ctx.RowValue();
            var doc = track.IsValid ? Lyrics.Store.Doc(track) : null;
            if (doc is not null && (!Lyrics.IsTimed(doc) || doc.Lines.Count == 0)) doc = null;
            if (!ReferenceEquals(doc, _doc)) { _doc = doc; _docGen++; _wipeLine = -1; _runLen = float.NaN; }
            bool playing = Playback.IsPlaying.Value;
            UseSignalEffect(() =>
            {
                int pos = Playback.PositionMs.Value;
                bool advancing = Playback.IsPlaying.Value && !Playback.Buffering.Value;
                // re-anchor on every report AND on a play/pause edge (Deck.PositionInterpolator.Anchor): a resume must not
                // extrapolate across the paused gap for a tick
                _clock.Report(pos, advancing, FrameTime.NowMs);
                // a report (or a play/pause edge) re-times the render-thread wipe and the edge wake; never on the mount run,
                // which runs inside this render
                if (!_reportSubscribed) { _reportSubscribed = true; return; }
                _tick();
            });
            // Paused, no timer at all: the clock is frozen, so the report effect above (a seek, a play/pause edge) and the
            // post-commit effect below are the only instants the view or the split can change. (A 4 Hz interval used to tick
            // here and re-present the stage ~3x a second for a picture that never moved.) Playing, the render thread poses
            // the wipe and the UI wakes at the next view edge (CaptionWake); only a break's breathing dots - or a host whose
            // render thread does not own the compositor - tick per frame (the ticker child below is mounted only then).
            bool renderOwned = Context.Anim is { RenderOwnsCompositor: true };

            // The render resolves the view itself (so a new document never shows the old one's indices for a tick) and
            // SUBSCRIBES to the tick's signal for the next change.
            int view = _view.Value;
            if (_doc is { } d0 && _clock.Anchored) view = ComputeView(d0, Now(), out _, out _);
            IReadOnlyList<Lyrics.Line> lines = _doc?.Lines ?? Array.Empty<Lyrics.Line>();
            int count = view == Unresolved ? 0 : lines.Count;
            int anchor = Caption.AnchorOf(view);
            bool dots = count > 0 && Caption.DotsOf(view);
            bool perFrame = _doc is not null && playing && (dots || !renderOwned);
            Element[] ticker = perFrame ? [Embed.Comp(() => new CaptionFrames(_tick))]
                : _doc is not null && playing ? [Embed.Comp(() => new CaptionWake(this))] : [];
            var (prevI, centreI, nextI) = count > 0 ? Caption.Slots(anchor, dots, L.CaptionContextFor(in k), count) : (-1, -1, -1);

            float capW = L.CaptionWFor(in k, galleryShown), capX = L.CaptionXFor(in k, galleryShown), capBottom = L.CaptionBottomFor(in k);
            var (aSize, aLine) = L.CaptionFont;
            var (cSize, cLine) = L.CaptionContextFont;
            // the wipe nodes belong to ONE line: a new centre re-keys the slot, whose fresh text nodes report through OnRealized
            if (centreI != _wipeLine) { _wipeLine = centreI; _mainNode = default; _glowNode = default; _runLen = float.NaN; }
            if (capW != _wrapW) { _wrapW = capW; _runLen = float.NaN; }
            if (!dots) _dotsNode = default;
            // after the commit that realized this view's nodes: seed (or hand back) the wipe and re-arm the edge wake
            UseEffect(_tick, DepKey.From(HashCode.Combine(view, centreI, _docGen, playing, capW)));

            string gen = _docGen.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string? prevText = prevI >= 0 && lines[prevI].Text.Length > 0 ? lines[prevI].Text : null;
            string? nextText = nextI >= 0 && lines[nextI].Text.Length > 0 ? lines[nextI].Text : null;
            string? activeText = null, secondary = null;
            StageCaptionWipe? wipe = null;
            if (!dots && centreI >= 0 && lines[centreI].Text.Length > 0)
            {
                var line = lines[centreI];
                activeText = line.Text;
                if (line.IsWordByWord && line.Syllables.Count > 0)
                {
                    float split = SplitAt(line, _clock.Anchored ? Now() : 0L);
                    float soft = Lyrics.Wipe.SoftnessOfLine(float.IsNaN(_runLen) ? MathF.Max(1f, capW) : _runLen, large: true);
                    float lift = Lyrics.Wipe.LiftFor(large: true, Design.Reduced);
                    float run = float.IsNaN(_runLen) ? 0f : _runLen;   // the run the render thread steps the split along
                    wipe = new StageCaptionWipe(split, soft, lift, run);
                }
                secondary = Prefs.Lyrics.SecondaryLine() switch { Prefs.Lyrics.Translation => line.Translation, Prefs.Lyrics.Romanization => line.Romanization, _ => null };
            }

            // THE PICTURE IS THE STRUCTURE'S (T7a1): the veil, the keyed previous / active / next slots, the dots. This host decided
            // everything above; the template lays it out and binds the node captures the timing below poses.
            return ViewSlots.View(ViewKeys.StageCaption, new StageCaptionProps
            {
                Width = capW, X = capX, Bottom = capBottom, BlockMaxHeight = L.CaptionBlockMaxHFor(in k),
                ActiveSize = aSize, ActiveLineHeight = aLine, ContextSize = cSize, ContextLineHeight = cLine,
                Generation = gen,
                PreviousIndex = prevI, PreviousText = prevText,
                NextIndex = nextI, NextText = nextText,
                ActiveIndex = centreI, ActiveText = activeText, Secondary = secondary, Wipe = wipe,
                Dots = dots, Anchor = anchor, Bloom = set.Text,
                OnMain = _onMain, OnGlow = _onGlow, OnDots = _onDots, DotAlpha = _dotAlpha, Ticker = ticker,
            });
        }

        /// <summary>The caption's per-frame tick (<see cref="Controls.FrameTicker"/>), named for the <c>[wake]</c> census.</summary>
        sealed class CaptionFrames(Action tick) : Controls.FrameTicker(tick);

        long Now() => _clock.Now(FrameTime.NowMs, Playback.DurationMs.Peek());

        /// <summary>The packed view at <paramref name="now"/> (<see cref="Caption.ViewAt"/>).</summary>
        static int ComputeView(Lyrics.Doc d, long now, out long gapStart, out long gapEnd) => Caption.ViewAt(d, now, out gapStart, out gapEnd);

        static float SplitAt(Lyrics.Line line, long now) => CaptionWipeRows.SplitAt(line, now);

        void Tick()
        {
            if (_doc is not { } d || !_clock.Anchored) { _rows.Hold(Context.Anim, Context.Scene); ArmEdge(long.MaxValue, 0L); return; }
            long now = Now();
            int view = ComputeView(d, now, out long gapStart, out long gapEnd);
            if (view != _view.Peek()) _view.Value = view;
            // playing: the next instant the view can change is the one wake owed (the dots tick per frame and need none)
            ArmEdge(_clock.Advancing && !Caption.DotsOf(view) ? Caption.NextEdgeMs(d, now) : long.MaxValue, now);
            if (Context.Scene is not { } scene) return;
            if (Caption.DotsOf(view)) { _rows.Hold(Context.Anim, scene); DriveDots(scene, now, gapStart, gapEnd); return; }
            int anchor = Caption.AnchorOf(view);
            // the nodes belong to the line the last render built; a line-synced line has no wipe (it faded in whole)
            if (anchor < 0 || anchor != _wipeLine || (uint)anchor >= (uint)d.Lines.Count) { _rows.Hold(Context.Anim, scene); return; }
            var line = d.Lines[anchor];
            if (!line.IsWordByWord || line.Syllables.Count == 0) { _rows.Hold(Context.Anim, scene); return; }
            var main = _mainNode;
            if (!main.IsNull && scene.IsLive(main) && float.IsNaN(_runLen)) _runLen = MeasureRun(scene, main, line.Text);
            if (_clock.Advancing && Context.Anim is { RenderOwnsCompositor: true } anim
                && _rows.Seed(anim, scene, line, anchor, main, _glowNode, now, FrameTime.NowMs, _runLen)) return;
            _rows.Cancel(Context.Anim);   // the UI owns the split (paused, buffering, a host whose render thread does not own the compositor)
            CaptionWipeRows.DriveUi(scene, main, _glowNode, line, now, _runLen);
        }

        /// <summary>Arm (or clear) the one-shot wake at media instant <paramref name="atMs"/> (<see cref="long.MaxValue"/> =
        /// none) through <see cref="Caption.EdgeWake"/>, in the frame clock's one domain.</summary>
        void ArmEdge(long atMs, long now)
        {
            if (_clock.ArmEdge(atMs, now, FrameTime.NowMs, out float delay))
                _edgeWake.Value = new Lyrics.MotionWake(++_edgeSeq, delay);
        }

        /// <summary>The caption's one-shot wake at the next view edge while the render thread poses the wipe: a re-arm restarts
        /// it, a cleared one cancels it. Named for the <c>[wake]</c> census.</summary>
        sealed class CaptionWake(CaptionHost owner) : Component
        {
            public override Element Render()
            {
                var wake = owner._edgeWake.Value;
                var timer = UseTimeout(owner._onEdge, MathF.Max(wake.DelayMs, 1f), DepKey.From(wake.Seq));
                if (wake.DelayMs < 0f) timer.Cancel();
                return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
            }
        }

        /// <summary>The active line's reading-order run length at the wrap width it was laid out at (one seam query per line,
        /// stack-allocated — the lyrics view's MeasureRunLength over the node's OWN text style).</summary>
        float MeasureRun(SceneStore scene, NodeHandle node, string text)
        {
            float wrap = _wrapW > 1f ? _wrapW : scene.Bounds(node).W;
            if (text.Length == 0 || !(wrap > 1f) || FluentGpu.Text.TextSeam.Default is not { } fonts) return MathF.Max(1f, wrap);
            var style = scene.Layout(node).TextStyle;
            Span<RectF> fragments = stackalloc RectF[8];
            int n = fonts.GetRangeRects(text, in style, wrap, 0, text.Length, fragments);
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += fragments[i].W;
            return sum > 1f ? sum : MathF.Max(1f, wrap);
        }

        /// <summary>The dots' fill + breath on the media clock (the lyrics view's DriveInterludeDots): three alpha signals bound
        /// to the dots, one transform on their row — both write-gated.</summary>
        void DriveDots(SceneStore scene, long now, long gapStart, long gapEnd)
        {
            float progress = Lyrics.Interlude.Progress(now, gapStart, gapEnd);
            float pulse = Lyrics.Interlude.Pulse(now, gapStart, Design.Reduced);
            var alpha = _dotAlpha;
            for (int k = 0; k < alpha.Length; k++)
            {
                float a = Lyrics.Interlude.DotAlpha(k, progress, pulse);
                if (MathF.Abs(alpha[k].Peek() - a) >= Lyrics.Wipe.DotAlphaEps) alpha[k].Value = a;
            }
            var h = _dotsNode;
            if (h.IsNull || !scene.IsLive(h)) return;
            ref NodePaint paint = ref scene.Paint(h);
            float scale = Lyrics.Interlude.BreathScale(pulse);
            if (MathF.Abs(paint.LocalTransform.M11 - scale) < Lyrics.Interlude.ScaleEps) return;
            paint.LocalTransform = scale >= 1f ? Affine2D.Identity : new Affine2D(scale, 0f, 0f, scale, 0f, 0f);
            scene.Mark(h, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }
}
