// ── Shell/Verse.UI.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// Verse.Face (the entry the Visualizer.Face switch calls), the gallery preview, Host (media clock, per-song tables, the
// per-line remount: the live line as per-word nodes, the ghost stack, the read-ahead, the translation, the hairline, the
// instrumental cloud and count-in, the honest states), its Driver (the paced frame ticker / one-shot wake), CloudWord
//
// Role: UI
// Owner: K
// Wave: 7
// Budget: 1150 lines
// Spec: verse-plan.md §3.1, §4, §5.3-§5.5, §6; viz-app-plan.md §2.1, §3.8
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE LYRICS ARE THE PICTURE, and nothing here decides: every number and rule is `Verse.cs`'s (words, sections, emphasis,
// flow, envelopes, lanes, geometry) or `Lyrics.cs`'s (the clock, the resolve, the break, the wipe, the bloom envelope).
//
//   1. ZERO RE-RENDER PER FRAME. The host renders per LINE (a hand-off, a break edge, a document, a layout change). The
//      per-frame driver writes value-gated FloatSignals the word nodes BIND (opacity, transform — compositor-only) and
//      three scene columns directly (a landing word's σ, a word's glyph-wipe split), allocation-free.
//   2. MOTION SAMPLES THE FRAME CLOCK. "Now" is `Lyrics.MediaClock` fed the host's (position, stamp) sample and queried at
//      `Design.FrameTime.NowQpc` — the lyrics view's clock, verbatim in shape.
//   3. THE TICKER MOUNTS ONLY WHILE A LANE MOVES (`Verse.Lanes`): a sung line (its hairline), a landing word, a held word,
//      a chorus echo. Between lines and through a break the face is asleep and a one-shot timeout wakes it at the next
//      media instant (a hand-off, a count-in dot). Audio coupling (kick, bass, level, the cloud's bands) is NOT in the
//      tick — those are bound thunks over the slab, written by the visualizer clock's own batch.
//   4. DEPTH WITHOUT BLUR LAYERS. Ghosts recede by size, alpha, weight and the cover's tint; at most two blur layers
//      animate (the landing word(s), capped at two in flight, and a held word's bloom), none on a weak GPU or under
//      reduced motion, none in the gallery preview.
//   5. LIGHT AND DARK. All ink is `StageInk` read at render; the accent is the slab's cross-faded `A`; on the light arm the
//      held word's bloom is a soft darker halo of ink, never a light one.
//
// Props freeze at mount: the host receives (slab, W, H) as RE-PUSHED props (a resize re-flows the line); everything live
// is a signal (the stage context's, the slab's, the playback signals).

using System.Diagnostics;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;

using Ink = Wavee.Design.StageInk;
using FrameTime = Wavee.Design.FrameTime;

namespace Wavee;

public static partial class Verse
{
    const string DisplayFace = "Segoe UI Variable Display";

    /// <summary>Bumped on every RETURNING chorus entry (a chorus occurrence ≥ 2 reaching its first line). The shared
    /// moments rule (Visualizer.Moments, viz-app-plan §3.5) reads it to force a palette rotation on the hook.</summary>
    public static readonly Signal<int> ChorusEntries = new(0);

    // the inks and sizes the UI alone uses (rungs over StageInk; the rules' numbers are Verse.cs's)
    const float LabelSize = 14f, HairTrackA = 0.10f, CountLitA = 0.70f, CountUnlitA = 0.15f, LightHaloA = 0.5f;
    const float CloudAlphaFloor = 0.30f, CloudAlphaBand = 0.70f, CloudBreath = 0.12f, CloudTint = 0.55f, CloudPeriodMs = 26000f;
    const float TitleBreath = 0.04f, TitleGlowFloor = 0.20f, TitleGlowLevel = 0.45f, GoldenAngle = 2.39996f;
    const int CloudMax = 12, PoemLines = 5;

    // ══ 1. THE ENTRY ═════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The Verse face. A gallery tile is a still word with a wipe (no lyrics read, no clock, no blur); the stage
    /// face is the host component, re-pushed (slab, size) props.</summary>
    public static Element Face(Visualizer.Slab slab, in Visualizer.Palette pal, in Visualizer.FaceSpec spec)
        => spec.Preview ? Preview(in pal, in spec) : Embed.Comp(new HostProps(slab, spec.W, spec.H), static () => new Host());

    sealed record HostProps(Visualizer.Slab Slab, float W, float H);

    /// <summary>The gallery tile: the face's name set as a sung word — the wipe two-thirds through it in the cover's
    /// accent, the rest faint ink — over a hairline at the same point. Three nodes, nothing bound.</summary>
    static Element Preview(in Visualizer.Palette pal, in Visualizer.FaceSpec spec)
    {
        float size = MathF.Round(MathF.Max(14f, spec.H * 0.24f));
        float hair = MathF.Round(size * 2.6f);
        const float split = 0.62f;
        ColorF ink = Ink.Ink;
        return new BoxEl
        {
            Width = spec.W, Height = spec.H, ClipToBounds = true, HitTestVisible = false,
            Direction = 1, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center, Gap = MathF.Round(size * 0.2f),
            Children =
            [
                new TextEl(Loc.Get(Strings.Stage.Viz.Verse))
                {
                    Size = size, Weight = 700, FontFamily = DisplayFace, Color = ink, Wrap = TextWrap.NoWrap, MaxLines = 1,
                    Wipe = new GlyphWipe(pal.A, ink with { A = 0.35f }, split, 0.08f),
                },
                new BoxEl
                {
                    Width = hair, Height = 2f, Shrink = 0f, Corners = CornerRadius4.All(1f), Fill = ink with { A = HairTrackA }, ClipToBounds = true, HitTestVisible = false,
                    Children = [new BoxEl { Width = hair, Height = 2f, Corners = CornerRadius4.All(1f), Fill = pal.A, Transform = Affine2D.Translation((split - 1f) * hair, 0f) }],
                },
            ],
        };
    }

    static EnterExit SlotEnter => new(Dy: Design.Reduced ? 0f : Stage.Tone.CaptionRiseDip, Opacity: 0f, Active: true);
    static EnterExit SlotExit => new(Dy: Design.Reduced ? 0f : -Stage.Tone.CaptionRiseDip, Opacity: 0f, Active: true);
    static EnterExit FadeOut => new(Opacity: 0f, Active: true);
    static readonly LayoutTransition s_glide = new(TransitionChannels.Position, TransitionDynamics.Tween(480f, Easing.FluentDecelerate));

    // ══ 2. THE PER-LINE RIG ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Everything the per-frame driver writes for the line the last render built: the per-word signals the nodes
    /// bind, the node handles reported at realization, and the line's frozen layout decisions. ONE rig per (document
    /// generation, view, type size, column) — a re-render of the same line reuses it, so its handles stay valid.</summary>
    sealed class Rig(string key, int line)
    {
        public readonly string Key = key;
        /// <summary>The live line (−1: a break, the intro, before the first line — no live block).</summary>
        public readonly int Line = line;
        public Word[] Words = [];
        public Role[] Roles = [];
        public float[] Widths = [], Sizes = [], RowH = [];
        public int[] RowOf = [];
        public int Rows;
        public float Gap, BlockH, HairW = 1f;
        public TimingMode Mode;
        public bool Whole, Dense, Rtl, Blur, WholeWipe;
        public float LandMs = Land.Ms;
        public long StartMs, SungOutMs, NextStartMs = Lanes.None;
        public FloatSignal[] Alpha = [], Dy = [];
        public FloatSignal?[] Swell = [], Bloom = [];
        public Signal<int>[] State = [];
        public NodeHandle[] Nodes = [], Text = [], BloomText = [];
        public readonly FloatSignal BlockAlpha = new(1f), BlockDy = new(0f), Progress = new(0f), Echo = new(0f);
        public NodeHandle BlockNode, WholeText;
        public Signal<int>? Lit;
        public bool Wipes(int i) => Mode == TimingMode.Word && !Whole && !Rtl && Words[i].SylCount > 1;
    }

    // ══ 3. THE HOST ══════════════════════════════════════════════════════════════════════════════════════════════════

    sealed class Host : Component
    {
        const int Unresolved = int.MinValue;

        // ── the media clock (Lyrics.UI.cs's feed, verbatim in shape) ─────────────────────────────────────────────────
        readonly Lyrics.MediaClock _clock = new();
        long _lastSampleStamp = long.MinValue;
        int _lastSamplePos = int.MinValue;

        // ── the document ─────────────────────────────────────────────────────────────────────────────────────────────
        Lyrics.Doc? _doc, _pending;
        Song? _song;
        string _trackId = "";
        int _gen;

        // ── the view the last render built, and the tick's request for the next ──────────────────────────────────────
        readonly Signal<int> _epoch = new(0);
        int _builtView = Unresolved, _requestedView = Unresolved;
        Rig? _rig;
        bool _video, _reduced, _calm;
        Visualizer.Slab? _slab;
        StringId _family;

        // ── motion demand ────────────────────────────────────────────────────────────────────────────────────────────
        readonly Signal<bool> _live = new(true), _timed = new(false);
        readonly Signal<Lyrics.MotionWake> _wake = new(new Lyrics.MotionWake(0, -1f));
        int _wakeSeq;
        float _beatMs;
        int _lastBeat = int.MinValue;
        long _lastBeatWallMs;
        readonly Action _tick, _tickCore, _onBeat, _rearm;

        public Host()
        {
            _tickCore = Tick;
            _tick = () => Reactive.Untrack(_tickCore);
            _onBeat = OnBeat;
            _rearm = () => _live.Value = true;
        }

        public override Element Render()
        {
            var p = UseProps<HostProps>();
            _slab = p.Slab;
            var ctx = UseContext(Stage.StageContext);
            var L = ctx?.Layout.Value ?? Stage.Layout.Seed(p.W, p.H);
            bool galleryOpen = ctx?.GalleryOpen.Value ?? false;
            bool chrome = ctx?.Chrome.Value ?? true;
            _calm = ctx?.Calm.Value ?? false;
            _ = ctx?.Palette.Value;                       // the cover / theme epoch: a flip re-renders with the arm's inks
            var track = ctx?.RowValue() ?? default;
            _ = Lyrics.Store.Changed.Value;
            bool playing = Playback.IsPlaying.Value;
            _video = Lyrics.SyncGate.SyncSuppressed(Playback.VideoActive.Value);
            _reduced = Design.Reduced;
            int epoch = _epoch.Value;                     // the tick's "the view moved" — the ONE per-line render trigger
            bool dark = Ink.IsDark;
            if (_family.IsEmpty && Context.Scene?.Strings is { } strings) _family = strings.Intern(DisplayFace);
            UseEffect(() => { if (track.IsValid) Lyrics.Store.Ensure(track); }, DepKey.From(track.Slot));

            // ── the document: a new track adopts at once; a swap mid-line is HELD to the next hand-off (Authority) ──────
            string trackId = track.IsValid ? Lyrics.Store.IdOf(track) : "";
            var doc = trackId.Length > 0 ? Lyrics.Store.Doc(trackId) : null;
            bool answered = trackId.Length > 0 && Lyrics.Store.Answered(trackId);
            if (!string.Equals(trackId, _trackId, StringComparison.Ordinal)) { _trackId = trackId; Adopt(doc); ResetClock(); }
            else if (!ReferenceEquals(doc, _doc) && !ReferenceEquals(doc, _pending))
            {
                int activeLine = _builtView == Unresolved ? -1 : Stage.Caption.AnchorOf(_builtView);
                if (doc is null || _doc is null || Lyrics.RowShape.SameRows(_doc, doc) || Lyrics.Authority.ApplyImmediately(playing, _doc, activeLine))
                    Adopt(doc);
                else _pending = doc;
            }

            var song = _song;
            bool timed = song is { Mode: not TimingMode.Static } && !_video;
            long now = song is not null ? ClockNow() : 0L;
            int view = Unresolved;
            long gapStart = 0L;
            if (timed)
            {
                view = ComputeView(song!.Doc, now, out gapStart, out _);
                if (_pending is { } held && view != _builtView)
                {
                    // the hand-off frame: the held richer document lands HERE, never mid-line
                    Adopt(held);
                    song = _song;
                    timed = song is { Mode: not TimingMode.Static };
                    view = timed ? ComputeView(song!.Doc, now, out gapStart, out _) : Unresolved;
                }
            }
            _builtView = view;
            _requestedView = view;
            bool timedNow = timed;
            UseEffect(() => _timed.SetIfChanged(timedNow), DepKey.From(timedNow ? 1 : 0));
            UseEffect(_rearm, DepKey.From(epoch, _gen, playing ? 1 : 0, view));   // every built view re-decides the lanes

            var region = Geometry.For(in L, p.W, p.H, galleryOpen, chrome);
            bool blurOn = region.LandBlur && !GpuProfile.IsWeak && Lyrics.Prefs.BlurStrength(GpuProfile.IsWeak, onStage: false) >= Land.BlurMinStrength;
            Element[] body;
            if (song is null)
            {
                _rig = null;
                bool miss = answered && (doc is null || doc.Lines.Count == 0);
                body = [NoLyrics(in region, track, miss, dark)];
            }
            else if (!timed) { _rig = null; body = [Poem(song.Doc, in region)]; }
            else body = Timed(song!, in region, view, now, gapStart, playing, p.W, p.H, blurOn, dark);

            return new BoxEl
            {
                Width = p.W, Height = p.H, ZStack = true, ClipToBounds = true, HitTestVisible = false, RepaintBoundary = true,
                Children = [.. body, Embed.Comp(() => new Driver(this)) with { Key = "verse:driver" }],
            };
        }

        void Adopt(Lyrics.Doc? doc)
        {
            _doc = doc;
            _pending = null;
            _song = doc is { Lines.Count: > 0 } ? Song.Build(doc) : null;
            _gen++;
            _rig = null;
            _builtView = Unresolved;
        }

        /// <summary>A document clear resets the clock too: a clock kept across a track change re-treats the next document's
        /// first sample as a &gt;250 ms disagreement (Lyrics.MediaClock.Reset).</summary>
        void ResetClock()
        {
            _clock.Reset(0L, FrameTime.NowQpc, false);
            _lastSampleStamp = long.MinValue;
            _lastSamplePos = int.MinValue;
        }

        /// <summary>The host's authoritative (position, stamp) sample, fed only when it changes and mapped onto QPC through its
        /// age; then queried at THIS frame's present time. Paused: the authoritative position.</summary>
        long ClockNow()
        {
            var snap = Playback.Snap();
            bool playing = Playback.IsPlaying.Peek();
            if (snap.PosQpc != _lastSampleStamp || snap.PosMs != _lastSamplePos || snap.ContentRate != _clock.ContentRate)
            {
                _lastSampleStamp = snap.PosQpc;
                _lastSamplePos = snap.PosMs;
                long sampleQpc = Lyrics.SampleClock.SampleQpc(snap.PosQpc, Playback.FrameNowMs(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
                _ = _clock.OnSample(snap.PosMs, sampleQpc, playing, snap.ContentRate);
            }
            else if (playing != _clock.Playing)
                _ = _clock.OnSample(Playback.PositionMs.Peek(), Stopwatch.GetTimestamp(), playing, snap.ContentRate);
            return playing ? _clock.At(FrameTime.NowQpc) : Playback.PositionMs.Peek();
        }

        /// <summary>The packed view (line + break edge) from the lyrics view's OWN resolves — the stage caption's rule
        /// (<c>Stage.Caption.View</c>): the lead-shifted line, and the dots/break standing in from the sung-out point until the
        /// next line resolves. The intro's "break" is [0, first line).</summary>
        static int ComputeView(Lyrics.Doc d, long now, out long gapStart, out long gapEnd)
        {
            var lines = d.Lines;
            int lead = Lyrics.ResolveLine(lines, now + Lyrics.LeadMs);
            _ = Lyrics.AdvancePastInterlude(d, lead, now, out gapStart, out gapEnd);
            long first = lines.Count > 0 ? lines[0].StartMs : long.MaxValue;
            if (lead < 0) { gapStart = 0L; gapEnd = first; }
            var (anchor, dots) = Stage.Caption.View(lead, gapStart, gapEnd, first, now, Lyrics.LeadMs, Lyrics.InterludeGapMs);
            return Stage.Caption.Pack(anchor, dots);
        }

        // ── 3.1 the timed composition ───────────────────────────────────────────────────────────────────────────────

        Element[] Timed(Song song, in Region r, int view, long now, long gapStart, bool playing, float W, float H, bool blurOn, bool dark)
        {
            var lines = song.Doc.Lines;
            int count = lines.Count;
            int anchor = Stage.Caption.AnchorOf(view);
            bool dots = Stage.Caption.DotsOf(view);
            int live = !dots && (uint)anchor < (uint)count && lines[anchor].Text.Length > 0 ? anchor : -1;
            int newest = dots ? anchor : anchor - 1;
            int next = anchor + 1 < count ? Math.Max(0, anchor + 1) : -1;

            string key = _gen + ":" + view + ":" + (int)r.Base + ":" + (int)r.ColumnW + ":" + (int)r.Aspect;
            var rig = _rig is { } kept && kept.Key == key ? kept : (_rig = BuildRig(song, key, live, next, dots, in r, now, blurOn));

            float lineH = MathF.Round(r.Base * Geometry.LineK);
            float blockH = live >= 0 ? rig.BlockH : lineH;
            float liveTop = MathF.Max(r.GhostTop, r.AnchorY - blockH * 0.5f);
            int depth = Geometry.GhostsThatFit(in r, liveTop);
            if (live < 0) depth = Math.Min(depth, 2);          // a break gives the stage to the song's words
            int echoD = live >= 0 ? Sections.EchoGhost(song.Sections, live, depth) : 0;

            var kids = new List<Element>(5) { Ghosts(lines, rig, in r, newest, depth, echoD, liveTop - Geometry.BlockAir, H, dark) };
            float lowerTop = liveTop + blockH + Geometry.BlockAir;
            if (live >= 0) kids.Add(LiveBlock(song, rig, in r, liveTop, now, playing, dark));
            else if (dots && r.Cloud)
            {
                float ry = MathF.Max(lineH, MathF.Min(0.16f * H, (r.Bottom - r.AnchorY) * 0.6f));
                float cy = r.AnchorY + ry * 0.3f;
                kids.Add(Cloud(song, in r, gapStart, W, H, cy, ry));
                lowerTop = MathF.Min(r.Bottom - lineH, cy + ry + Geometry.BlockAir);
            }
            kids.Add(Lower(lines, rig, in r, live, next, lowerTop));
            return kids.ToArray();
        }

        /// <summary>Build the rig for one view: the live line's words, roles, measured widths, rows and its signals seeded from
        /// <paramref name="now"/> (so the first frame lands on the same values the tick would write).</summary>
        Rig BuildRig(Song song, string key, int live, int next, bool dots, in Region r, long now, bool blurOn)
        {
            var lines = song.Doc.Lines;
            var rig = new Rig(key, live);
            if (next >= 0) rig.NextStartMs = lines[next].StartMs;
            if (dots && next >= 0) rig.Lit = new Signal<int>(CountIn.Lit(now, rig.NextStartMs, _beatMs));
            if (live < 0) return rig;

            var line = lines[live];
            var words = song.Words[live];
            int n = words.Length;
            rig.Words = words;
            rig.StartMs = line.StartMs;
            rig.SungOutMs = Lyrics.SungOutMs(song.Doc, live);
            rig.Mode = Timing.LineMode(song.Mode, line, words);
            rig.Dense = rig.Mode == TimingMode.Word && Timing.IsDense(words);
            rig.Rtl = Words.IsRtl(line.Text);
            rig.Blur = blurOn && !rig.Dense;
            rig.LandMs = rig.Mode == TimingMode.Word ? Timing.LandMsFor(words, rig.Dense) : Land.Ms;
            rig.Roles = new Role[n];
            Emphasis.Line(words, rig.Dense, r.HeldScale, rig.Roles);

            // measured widths at scale 1, the measured space, then the flow
            rig.Widths = new float[n];
            for (int i = 0; i < n; i++) rig.Widths[i] = Measure(words[i].Text, r.Base * rig.Roles[i].Scale, rig.Roles[i].Weight);
            float gap = MathF.Max(r.Base * 0.2f, Measure("a a", r.Base, Emphasis.Weight) - 2f * Measure("a", r.Base, Emphasis.Weight));
            rig.RowOf = new int[n];
            var flowed = Flow.Layout(rig.Widths, gap, r.ColumnW, Flow.MaxRows, rig.RowOf);
            rig.Whole = n == 0 || !flowed.Fits || n > Timing.MaxWords;
            if (rig.Whole)
            {
                // ONE wrapped run that lands whole (MinSize auto-fit); a word-timed line keeps the standard whole-line wipe
                rig.WholeWipe = rig.Mode == TimingMode.Word || (line.IsWordByWord && line.Syllables.Count > 0);
                rig.Mode = TimingMode.Line;
                float total = 0f;
                for (int i = 0; i < n; i++) total += rig.Widths[i] + gap;
                rig.Rows = Math.Clamp((int)MathF.Ceiling(total / MathF.Max(1f, r.ColumnW)), 1, Flow.MaxRows);
                rig.BlockH = rig.Rows * MathF.Round(r.Base * Geometry.LineK);
                rig.HairW = MathF.Min(r.ColumnW, MathF.Max(total, r.ColumnW * 0.3f));
                rig.Widths = [MathF.Max(1f, total)];
            }
            else
            {
                rig.Rows = flowed.Rows;
                rig.Gap = MathF.Round(gap * flowed.Scale);
                rig.Sizes = new float[n];
                rig.RowH = new float[flowed.Rows];
                for (int i = 0; i < n; i++)
                {
                    rig.Sizes[i] = MathF.Round(r.Base * rig.Roles[i].Scale * flowed.Scale);
                    rig.Widths[i] *= flowed.Scale;
                    int row = rig.RowOf[i];
                    rig.RowH[row] = MathF.Max(rig.RowH[row], MathF.Round(rig.Sizes[i] * Geometry.LineK));
                }
                float h = 0f;
                for (int k = 0; k < rig.RowH.Length; k++) h += rig.RowH[k];
                rig.BlockH = h;
                rig.HairW = MathF.Min(r.ColumnW, MathF.Max(flowed.WidestRow, r.ColumnW * 0.3f));
            }

            // the signals, seeded at now
            bool playing = Playback.IsPlaying.Peek();
            rig.Alpha = new FloatSignal[n]; rig.Dy = new FloatSignal[n]; rig.State = new Signal<int>[n];
            rig.Swell = new FloatSignal?[n]; rig.Bloom = new FloatSignal?[n];
            rig.Nodes = new NodeHandle[n]; rig.Text = new NodeHandle[n]; rig.BloomText = new NodeHandle[n];
            bool still = _reduced || rig.Dense;
            for (int i = 0; i < n; i++)
            {
                float lp = rig.Mode == TimingMode.Word ? LandAt(rig, i, now, playing) : 1f;
                rig.Alpha[i] = new FloatSignal(rig.Mode == TimingMode.Word ? Land.Alpha(lp) : 1f);
                rig.Dy[i] = new FloatSignal(rig.Mode == TimingMode.Word ? Land.Drop(lp, still) : 0f);
                rig.State[i] = new Signal<int>(StateAt(in words[i], now, rig.Mode));
                if (rig.Mode == TimingMode.Word && rig.Roles[i].Held)
                {
                    float g = Hold.Glow(in words[i], now);
                    rig.Swell[i] = new FloatSignal(Hold.Swell(g, 0f, _reduced, _calm));
                    rig.Bloom[i] = new FloatSignal(Hold.Bloom(g, 0f, playing));
                }
            }
            if (rig.Mode == TimingMode.Line)
            {
                float bp = BlockLand(rig, now, playing);
                rig.BlockAlpha.Value = Land.Alpha(bp);
                rig.BlockDy.Value = Land.Drop(bp, _reduced);
            }
            rig.Progress.Value = HairProgress(rig, now);
            return rig;
        }

        float Measure(string text, float size, ushort weight)
        {
            if (text.Length > 0 && TextSeam.Default is { } fonts)
            {
                var style = new TextStyle(_family, size, weight, TextWrap.NoWrap, TextTrim.None, 0);
                Span<RectF> rects = stackalloc RectF[4];
                try
                {
                    int n = fonts.GetRangeRects(text, in style, float.PositiveInfinity, 0, text.Length, rects);
                    float w = 0f;
                    for (int i = 0; i < n; i++) w += rects[i].W;
                    if (w > 0f) return w;
                }
                catch (NotSupportedException) { }   // a backend without range geometry: the estimate below
            }
            return text.Length * size * 0.52f;
        }

        static int StateAt(in Word w, long now, TimingMode mode)
            => mode != TimingMode.Word ? 2 : now >= w.EndMs ? 2 : now >= w.StartMs ? 1 : 0;

        /// <summary>A word's landing progress at <paramref name="now"/>: the envelope, completed on pause (a paused face is a
        /// still, lit picture — never a half-blurred word).</summary>
        static float LandAt(Rig rig, int i, long now, bool playing)
        {
            float p = Land.Progress(now, rig.Words[i].StartMs, rig.LandMs);
            return !playing && p > 0f ? 1f : p;
        }

        static float BlockLand(Rig rig, long now, bool playing)
        {
            float p = Land.Progress(now, rig.StartMs, Land.Ms);
            return !playing && p > 0f ? 1f : p;
        }

        static float HairProgress(Rig rig, long now)
        {
            long span = rig.SungOutMs - rig.StartMs;
            float p = span > 0L && rig.SungOutMs < Lanes.None ? Math.Clamp((now - rig.StartMs) / (float)span, 0f, 1f) : now >= rig.StartMs ? 1f : 0f;
            return rig.HairW > 1f ? MathF.Round(p * rig.HairW) / rig.HairW : p;
        }

        static float WipeSplit(in Word w, long now, float width)
        {
            float split = Words.Split(in w, now);
            if (split > 0f && split < 1f) split = Math.Clamp(split + Lyrics.Wipe.LeadFrac, 0f, 1f);
            if (width > 1f && split > 0f && split < 1f) split = MathF.Round(split * width) / width;
            return split;
        }

        // ── 3.2 the live block: rows of word nodes (or one wrapped run) ─────────────────────────────────────────────

        Element LiveBlock(Song song, Rig rig, in Region r, float top, long now, bool playing, bool dark)
        {
            var slab = _slab!;
            bool calm = _calm, reduced = _reduced;
            Element[] rows;
            if (rig.Whole)
            {
                var line = song.Doc.Lines[rig.Line];
                ColorF ink = Ink.Ink;
                var run = new TextEl(line.Text)
                {
                    Size = r.Base, MinSize = MathF.Round(r.Base * Flow.MinScale), Weight = Emphasis.Weight, FontFamily = DisplayFace, Color = ink,
                    Wrap = TextWrap.Wrap, MaxLines = Flow.MaxRows, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                };
                if (rig.WholeWipe)
                {
                    float split = Lyrics.Wipe.ComputeSplit(line, now);
                    if (split > 0f && split < 1f) split = Math.Clamp(split + Lyrics.Wipe.LeadFrac, 0f, 1f);
                    run = run with
                    {
                        Wipe = new GlyphWipe(ink, ink with { A = Lyrics.Wipe.UnsungAlpha }, split, Lyrics.Wipe.SoftnessOfLine(rig.Widths[0], large: true)),
                        OnRealized = h => rig.WholeText = h,
                    };
                }
                rows = [run];
            }
            else
            {
                rows = new Element[rig.Rows];
                var row = new List<Element>(rig.Words.Length);
                for (int k = 0; k < rig.Rows; k++)
                {
                    row.Clear();
                    for (int i = 0; i < rig.Words.Length; i++)
                        if (rig.RowOf[i] == k) row.Add(WordEl(rig, i, now, playing, dark));
                    if (rig.Rtl) row.Reverse();
                    rows[k] = new BoxEl
                    {
                        Direction = 0, Height = rig.RowH[k], Gap = rig.Gap, AlignItems = FlexAlign.End, HitTestVisible = false,
                        Justify = rig.Rtl ? FlexJustify.End : FlexJustify.Start, Children = row.ToArray(),
                    };
                }
            }
            float blur0 = rig.Mode == TimingMode.Line ? Land.Blur(BlockLand(rig, now, playing), reduced, rig.Blur) : 0f;
            var inner = new BoxEl
            {
                Direction = 1, Width = r.ColumnW, HitTestVisible = false, OnRealized = h => rig.BlockNode = h,
                Opacity = (Prop<float>)rig.BlockAlpha, Blur = blur0,
                // the kick nudges the whole live line ≤ 2 DIP (Coupling); a line-timed line's landing rides the same matrix
                Transform = Prop.Of(() => Affine2D.Translation(0f, rig.BlockDy.Value - Coupling.Kick(slab.Kick.Value, calm, reduced))),
                Children = rows,
            };
            return new BoxEl
            {
                Key = "v:l:" + rig.Key, Width = r.ColumnW, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(r.ColumnX, top, 0f, 0f), HitTestVisible = false,
                Enter = SlotEnter, Exit = SlotExit, Transition = MotionTok.StandardEnter,
                Children = [inner],
            };
        }

        /// <summary>One word: a wrapper the landing drives (bound opacity + transform, its σ written per frame), around the
        /// text — and, for a held word, a ZStack of [the bloom in a blur box at a bound alpha, the text]. A multi-syllable
        /// word carries its own glyph wipe over its syllables.</summary>
        Element WordEl(Rig rig, int i, long now, bool playing, bool dark)
        {
            var slab = _slab!;
            var w = rig.Words[i];
            float size = rig.Sizes[i];
            ushort weight = rig.Roles[i].Weight;
            float lineH = MathF.Round(size * Geometry.LineK);
            ColorF ink = Ink.Ink;
            var st = rig.State[i];
            var main = new TextEl(w.Text)
            {
                Size = size, LineHeight = lineH, LineStacking = LineStacking.BlockLineHeight, Weight = weight, FontFamily = DisplayFace,
                Color = Prop.Of(() => st.Value == 1 ? slab.A.Value : ink), Wrap = TextWrap.NoWrap, MaxLines = 1,
            };
            bool wipe = rig.Wipes(i);
            float split = 0f, soft = 0f;
            if (wipe)
            {
                split = WipeSplit(in w, now, rig.Widths[i]);
                soft = Lyrics.Wipe.SoftnessOfLine(MathF.Max(1f, rig.Widths[i]), large: true);
                ColorF before = st.Peek() == 2 ? ink : slab.A.Peek();
                main = main with { Wipe = new GlyphWipe(before, ink with { A = Lyrics.Wipe.UnsungAlpha }, split, soft), OnRealized = h => rig.Text[i] = h };
            }
            Element content = main;
            if (rig.Bloom[i] is { } bloom)
            {
                ColorF glow = dark ? slab.A.Peek() : ink with { A = LightHaloA };
                Action<NodeHandle>? onHalo = null;
                GlyphWipe? haloWipe = null;
                if (wipe) { onHalo = h => rig.BloomText[i] = h; haloWipe = new GlyphWipe(glow, glow with { A = 0f }, split, soft); }
                var halo = main with { Color = dark ? Prop.Bind(slab.A) : (Prop<ColorF>)glow, Wipe = haloWipe, OnRealized = onHalo };
                content = new BoxEl
                {
                    ZStack = true, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl { Direction = 1, Blur = Stage.Tone.CaptionBloomSigma, Opacity = (Prop<float>)bloom, HitTestVisible = false, Children = [halo] },
                        main,
                    ],
                };
            }
            var dy = rig.Dy[i];
            Prop<Affine2D> transform;
            if (rig.Swell[i] is { } swell) transform = Prop.Of(() => { float s = swell.Value; return new Affine2D(s, 0f, 0f, s, 0f, dy.Value); });
            else transform = Prop.Of(() => Affine2D.Translation(0f, dy.Value));
            float blur0 = rig.Mode == TimingMode.Word ? Land.Blur(LandAt(rig, i, now, playing), _reduced, rig.Blur) : 0f;
            return new BoxEl
            {
                Shrink = 0f, HitTestVisible = false, OnRealized = h => rig.Nodes[i] = h,
                Opacity = (Prop<float>)rig.Alpha[i], Blur = blur0, Transform = transform,
                Children = [content],
            };
        }

        // ── 3.3 the ghost stack: the song remembers ─────────────────────────────────────────────────────────────────

        /// <summary>The last sung lines above the live one, deepest first, BOTTOM-anchored at the live block's top: smaller,
        /// fainter, lighter and tinted toward the cover's B / C / Deep with depth — never blurred. Keyed by line, so a hand-off
        /// glides the stack up; the bass sinks it ≤ 6 DIP. The ghost the live line echoes lights in the accent.</summary>
        Element Ghosts(IReadOnlyList<Lyrics.Line> lines, Rig rig, in Region r, int newest, int depth, int echoD, float bottomY, float H, bool dark)
        {
            var slab = _slab!;
            bool calm = _calm, reduced = _reduced;
            var kids = new List<Element>(depth);
            for (int d = depth; d >= 1; d--)
            {
                int j = newest - (d - 1);
                if (j < 0 || j >= lines.Count || lines[j].Text.Length == 0) continue;
                kids.Add(Ghost(lines[j].Text, j, d, echoD == d ? rig.Echo : null, r.Base, r.ColumnW, dark));
            }
            return new BoxEl
            {
                Key = "v:ghosts", Direction = 1, Width = r.ColumnW, Gap = Geometry.GhostGap, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(r.ColumnX, 0f, 0f, MathF.Max(0f, H - bottomY)), HitTestVisible = false,
                Transform = Prop.Of(() => Affine2D.Translation(0f, Coupling.BassSink(slab.Low.Value, calm, reduced))),
                Children = kids.ToArray(),
            };
        }

        Element Ghost(string text, int line, int d, FloatSignal? echo, float baseSize, float maxW, bool dark)
        {
            var slab = _slab!;
            float size = MathF.Round(baseSize * Geometry.GhostScale(d));
            float a = Geometry.GhostAlpha(d, dark);
            var tint = d == 1 ? slab.B : d == 2 ? slab.C : slab.Deep;
            ColorF ink = Ink.Ink;
            Prop<ColorF> color;
            if (echo is { } lit)
                color = Prop.Of(() =>
                {
                    var c = ColorF.Lerp(ink, tint.Value, Geometry.GhostTint) with { A = a };
                    float e = lit.Value;
                    return e <= 0f ? c : ColorF.Lerp(c, slab.A.Value with { A = MathF.Max(a, Echo.Peak) }, e);
                });
            else color = Prop.Of(() => ColorF.Lerp(ink, tint.Value, Geometry.GhostTint) with { A = a });
            return new BoxEl
            {
                Key = "v:g:" + _gen + ":" + line, Direction = 1, MaxWidth = maxW, HitTestVisible = false,
                AlignSelf = Words.IsRtl(text) ? FlexAlign.End : FlexAlign.Start,
                Layout = _reduced ? (LayoutTransition?)null : s_glide, Enter = SlotEnter, Exit = FadeOut, Transition = MotionTok.StandardEnter,
                Children =
                [
                    new TextEl(text)
                    {
                        Size = size, LineHeight = MathF.Round(size * Geometry.LineK), LineStacking = LineStacking.BlockLineHeight,
                        Weight = Geometry.GhostWeight(d), FontFamily = DisplayFace, Color = color,
                        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }

        // ── 3.4 below the line: hairline · translation · count-in · read-ahead ──────────────────────────────────────

        Element Lower(IReadOnlyList<Lyrics.Line> lines, Rig rig, in Region r, int live, int next, float top)
        {
            var kids = new List<Element>(5);
            float small = MathF.Round(r.Base * Geometry.ReadAheadK);
            if (live >= 0) kids.Add(Hairline(rig));
            if (live >= 0 && r.Translation)
            {
                var line = lines[live];
                string? secondary = Prefs.Lyrics.SecondaryLine() switch
                {
                    Prefs.Lyrics.Translation => line.Translation,
                    Prefs.Lyrics.Romanization => line.Romanization,
                    _ => null,
                };
                if (secondary is { Length: > 0 })
                    kids.Add(new TextEl(secondary)
                    {
                        Key = "v:t:" + _gen + ":" + live, Size = small, LineHeight = MathF.Round(small * 1.3f), Weight = 500, FontFamily = DisplayFace,
                        Color = Ink.InkSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        Enter = SlotEnter, Exit = FadeOut, Transition = MotionTok.StandardEnter,
                    });
            }
            if (rig.Lit is { } lit)
            {
                // a break: name it, then the count-in toward the next line
                kids.Add(new TextEl(Loc.Get(Strings.Stage.Verse.Instrumental))
                {
                    Key = "v:il:" + _gen, Size = LabelSize, Weight = 600, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, MinWidth = 0f,
                });
                kids.Add(CountInRow(lit, rig.Key));
            }
            if (next >= 0 && r.ReadAhead && lines[next].Text.Length > 0)
            {
                if (live < 0)
                    kids.Add(new TextEl(Loc.Get(Strings.Stage.Verse.Next))
                    {
                        Key = "v:nl:" + _gen, Size = LabelSize, Weight = 600, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, MinWidth = 0f,
                    });
                kids.Add(new TextEl(lines[next].Text)
                {
                    Key = "v:n:" + _gen + ":" + next, Size = small, LineHeight = MathF.Round(small * 1.3f), Weight = 600, FontFamily = DisplayFace,
                    Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Enter = SlotEnter, Exit = FadeOut, Transition = MotionTok.StandardEnter,
                });
            }
            return new BoxEl
            {
                Key = "v:lower", Direction = 1, Width = r.ColumnW, Gap = 8f, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                AlignItems = rig.Rtl ? FlexAlign.End : FlexAlign.Start,
                Margin = new Edges4(r.ColumnX, top, 0f, 0f), MaxHeight = MathF.Max(0f, r.Bottom - top), ClipToBounds = true, HitTestVisible = false,
                Children = kids.ToArray(),
            };
        }

        /// <summary>The line's progress: a clipped track with an accent capsule TRANSLATED in (capsules never scale), true
        /// timing in word and line mode alike; right to left on an RTL line.</summary>
        Element Hairline(Rig rig)
        {
            var slab = _slab!;
            var prog = rig.Progress;
            float w = rig.HairW, dir = rig.Rtl ? -1f : 1f;
            return new BoxEl
            {
                Key = "v:h:" + rig.Key, Width = w, Height = Geometry.HairlineH, Shrink = 0f, Corners = CornerRadius4.All(Geometry.HairlineH * 0.5f),
                Fill = Ink.Ink with { A = HairTrackA }, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Width = w, Height = Geometry.HairlineH, Corners = CornerRadius4.All(Geometry.HairlineH * 0.5f), Fill = Prop.Bind(slab.Accent), HitTestVisible = false,
                        Transform = Prop.Of(() => Affine2D.Translation((prog.Value - 1f) * w * dir, 0f)),
                    },
                ],
            };
        }

        /// <summary>The count-in: the lyrics view's three dots (size, gap, ink), lit on the beat grid by the driver.</summary>
        static Element CountInRow(Signal<int> lit, string key)
        {
            float d = Lyrics.Interlude.DotSize(large: true);
            var dots = new Element[CountIn.Dots];
            for (int k = 0; k < dots.Length; k++)
            {
                int kk = k;
                dots[k] = new BoxEl
                {
                    Width = d, Height = d, Shrink = 0f, Corners = CornerRadius4.All(d * 0.5f), Fill = Ink.Ink, HitTestVisible = false,
                    Opacity = Prop.Of(() => lit.Value > kk ? CountLitA : CountUnlitA),
                };
            }
            return new BoxEl
            {
                Key = "v:c:" + key, Direction = 0, Gap = Lyrics.Interlude.DotGap(large: true), AlignItems = FlexAlign.Center, HitTestVisible = false,
                Enter = SlotEnter, Exit = FadeOut, Transition = MotionTok.StandardEnter, Children = dots,
            };
        }

        // ── 3.5 the instrumental cloud: the song's own words take the stage ─────────────────────────────────────────

        /// <summary>The most-sung words so far (Memory.TopWordsAt at the break's start), the biggest in the middle and the rest
        /// on a golden-angle spiral, each sized by its count, drifting on slow keyframes (zero ticks) and breathing with its
        /// own band (a bound thunk over the slab).</summary>
        Element Cloud(Song song, in Region r, long gapStart, float W, float H, float cy, float ry)
        {
            var slab = _slab!;
            Span<int> tokens = stackalloc int[CloudMax];
            Span<int> counts = stackalloc int[CloudMax];
            int n = Memory.TopWordsAt(song, gapStart, tokens, counts);
            var kids = new List<CanvasChild>(n);
            float cx = r.ColumnX + r.ColumnW * 0.5f;
            float rx = MathF.Min(W * 0.42f, MathF.Max(r.ColumnW * 0.62f, W * 0.3f));
            for (int i = 0; i < n; i++)
            {
                string word = song.Tokens[tokens[i]];
                float size = MathF.Round(r.Base * MathF.Min(1.3f, 0.5f + 0.12f * counts[i]));
                ushort weight = counts[i] > 3 ? (ushort)700 : (ushort)500;
                float w = Measure(word, size, weight);
                float frac = i == 0 ? 0f : 0.25f + 0.75f * MathF.Sqrt(i / (float)n);
                float ang = i * GoldenAngle + 0.6f;
                float x = Math.Clamp(cx + MathF.Cos(ang) * rx * frac - w * 0.5f, W * 0.04f, MathF.Max(W * 0.04f, W * 0.96f - w));
                float y = Math.Clamp(cy + MathF.Sin(ang) * ry * frac - size * 0.6f, r.GhostTop, MathF.Max(r.GhostTop, r.Bottom - size * 1.3f));
                float drift = MathF.Round(size * 0.35f);
                var props = new CloudWord.Props(word, size, weight, i, slab, (i & 1) == 0 ? drift : -drift, ((i >> 1) & 1) == 0 ? drift * 0.6f : -drift * 0.6f);
                kids.Add(new CanvasChild(x, y, Embed.Comp(props, static () => new CloudWord()) with { Key = "v:w:" + _gen + ":" + gapStart + ":" + i }));
            }
            return Canvas.Create(W, H, kids) with
            {
                Key = "v:cloud:" + _gen + ":" + gapStart, HitTestVisible = false,
                Enter = new EnterExit(Opacity: 0f, Active: true), Exit = FadeOut, Transition = MotionTok.StandardEnter,
            };
        }

        // ── 3.6 the honest states ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>No lyrics (H4): the title as one held word breathing with the level over its bloom, the hairline as the
        /// track's progress, and — only once the store has really answered — "No lyrics for this track". Never a blank stage.</summary>
        Element NoLyrics(in Region r, Track track, bool miss, bool dark)
        {
            var slab = _slab!;
            bool reduced = _reduced;
            string title = track.IsValid && Stage.Transport.UsesTitle(track.Title, track.Uri.Text) ? track.Title : "";
            float size = MathF.Round(r.Base * r.HeldScale);
            float hair = MathF.Round(r.ColumnW * 0.5f), minSize = r.Base;
            ColorF ink = Ink.Ink;
            var kids = new List<Element>(3);
            if (miss)
                kids.Add(new TextEl(Loc.Get(Strings.Stage.Verse.NoLyrics))
                {
                    Size = LabelSize, Weight = 600, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                });
            if (title.Length > 0)
            {
                var glow = new BoxEl
                {
                    Direction = 1, Blur = Stage.Tone.CaptionBloomSigma, HitTestVisible = false,
                    Opacity = Prop.Of(() => TitleGlowFloor + TitleGlowLevel * Math.Clamp(slab.Level.Value, 0f, 1f)),
                    Children = [Title(dark ? Prop.Bind(slab.A) : (Prop<ColorF>)(ink with { A = LightHaloA }))],
                };
                kids.Add(new BoxEl
                {
                    ZStack = true, HitTestVisible = false, TransformOriginX = 0f, TransformOriginY = 0.5f,
                    Transform = Prop.Of(() => { float s = 1f + (reduced ? 0f : TitleBreath * Math.Clamp(slab.Level.Value, 0f, 1f)); return Affine2D.Scale(s, s); }),
                    Children = [glow, Title(ink)],
                });
            }
            kids.Add(new BoxEl
            {
                Width = hair, Height = Geometry.HairlineH, Shrink = 0f, Corners = CornerRadius4.All(Geometry.HairlineH * 0.5f),
                Fill = ink with { A = HairTrackA }, ClipToBounds = true, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Width = hair, Height = Geometry.HairlineH, Corners = CornerRadius4.All(Geometry.HairlineH * 0.5f), Fill = Prop.Bind(slab.Accent), HitTestVisible = false,
                        Transform = Prop.Of(() => Affine2D.Translation((Math.Clamp(slab.Progress.Value, 0f, 1f) - 1f) * hair, 0f)),
                    },
                ],
            });
            return new BoxEl
            {
                Key = "v:none:" + _gen, Direction = 1, Gap = 14f, Width = r.ColumnW, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(r.ColumnX, MathF.Max(r.GhostTop, r.AnchorY - size), 0f, 0f), HitTestVisible = false,
                Enter = SlotEnter, Exit = FadeOut, Transition = MotionTok.StandardEnter,
                Children = kids.ToArray(),
            };

            TextEl Title(Prop<ColorF> color) => new(title)
            {
                Size = size, MinSize = minSize, Weight = Emphasis.HeldWeight, FontFamily = DisplayFace, Color = color,
                Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            };
        }

        /// <summary>Unsynced lyrics, or a video suppressing timed sync: a calm poem block — the first lines at a ghost size,
        /// no landing, no hairline — under a one-line note that names the state.</summary>
        Element Poem(Lyrics.Doc doc, in Region r)
        {
            float size = MathF.Round(r.Base * Geometry.GhostScale(2));
            float lineH = MathF.Round(size * 1.3f);
            var kids = new List<Element>(PoemLines + 1)
            {
                new TextEl(_video ? Loc.Get(Strings.Player.LyricsSyncUnavailableDuringVideo) : Loc.Get(Strings.Stage.Verse.NotTimed))
                {
                    Size = LabelSize, Weight = 600, Color = Ink.InkTertiary, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            };
            int shown = 0;
            for (int i = 0; i < doc.Lines.Count && shown < PoemLines; i++)
            {
                string t = doc.Lines[i].Text;
                if (t.Length == 0) continue;
                kids.Add(new TextEl(t)
                {
                    Size = size, LineHeight = lineH, Weight = 500, FontFamily = DisplayFace, Color = Ink.InkSecondary,
                    Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                });
                shown++;
            }
            float blockH = shown * (lineH + 6f) + LabelSize * 2f;
            return new BoxEl
            {
                Key = "v:poem:" + _gen, Direction = 1, Gap = 6f, Width = r.ColumnW, AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start,
                Margin = new Edges4(r.ColumnX, MathF.Max(r.GhostTop, r.AnchorY - blockH * 0.5f), 0f, 0f), HitTestVisible = false,
                Enter = SlotEnter, Exit = FadeOut, Transition = MotionTok.StandardEnter,
                Children = kids.ToArray(),
            };
        }

        // ── 3.7 the per-frame driver (allocation-free) ──────────────────────────────────────────────────────────────

        void Tick()
        {
            var song = _song;
            if (song is null || song.Mode == TimingMode.Static || _video) { Apply(new Lyrics.MotionDecision(false, Lanes.None), 0L); return; }
            long now = ClockNow();
            bool playing = _clock.Playing;
            int view = ComputeView(song.Doc, now, out _, out _);
            if (view != _builtView)
            {
                // a hand-off or a break edge: the ONE re-render; the next tick drives the new line's nodes
                if (view != _requestedView)
                {
                    _requestedView = view;
                    _epoch.Value = _epoch.Peek() + 1;
                    int at = Stage.Caption.AnchorOf(view);
                    if (playing && !Stage.Caption.DotsOf(view) && song.Sections.IsChorusEntry(at) && song.Sections.Instance[at] >= 2)
                    {
                        ChorusEntries.Value = ChorusEntries.Peek() + 1;
                        Visualizer.Moments.Force();   // a returning chorus forces a palette moment (verse-plan B3 / app plan §3.5)
                    }
                }
                Apply(new Lyrics.MotionDecision(true, Lanes.None), now);
                return;
            }
            bool voice = false, landing = false, held = false, echo = false;
            long countEdge = Lanes.None;
            if (_rig is { } rig && Context.Scene is { } scene)
            {
                if (rig.Line >= 0) Drive(scene, song, rig, now, playing, ref voice, ref landing, ref held, ref echo);
                if (rig.Lit is { } lit)
                {
                    lit.SetIfChanged(CountIn.Lit(now, rig.NextStartMs, _beatMs));
                    countEdge = CountIn.NextEdgeMs(now, rig.NextStartMs, _beatMs);
                }
            }
            int from = Math.Max(0, Stage.Caption.AnchorOf(view));
            var lanes = new LaneState(playing, voice, landing, held, echo, now, Lanes.NextEventMs(song.Doc.Lines, from, now, countEdge));
            Apply(Lanes.Decide(in lanes), now);
        }

        void Drive(SceneStore scene, Song song, Rig rig, long now, bool playing, ref bool voice, ref bool landing, ref bool held, ref bool echo)
        {
            var slab = _slab!;
            bool still = _reduced || rig.Dense;
            if (rig.Mode == TimingMode.Word)
            {
                float low = slab.Low.Peek(), level = slab.Level.Peek();
                ColorF ink = Ink.Ink, accent = slab.A.Peek();
                int newer = 0;
                for (int i = rig.Words.Length - 1; i >= 0; i--)
                {
                    ref readonly var w = ref rig.Words[i];
                    float p = Land.Capped(LandAt(rig, i, now, playing), newer);
                    if (p > 0f && p < 1f) { newer++; landing = true; }
                    WriteFloat(rig.Alpha[i], Land.Alpha(p), AlphaEps);
                    WriteFloat(rig.Dy[i], Land.Drop(p, still), DyEps);
                    WriteBlur(scene, rig.Nodes[i], Land.Blur(p, _reduced, rig.Blur));
                    int st = StateAt(in w, now, TimingMode.Word);
                    rig.State[i].SetIfChanged(st);
                    if (rig.Wipes(i) && (st == 1 || p > 0f))
                    {
                        float split = WipeSplit(in w, now, rig.Widths[i]);
                        WriteWipe(scene, rig.Text[i], split, st == 2 ? ink : accent, recolour: true);
                        WriteWipe(scene, rig.BloomText[i], split, default, recolour: false);
                    }
                    if (rig.Swell[i] is { } swell && rig.Bloom[i] is { } bloom)
                    {
                        float g = Hold.Glow(in w, now);
                        held |= g > 0f;
                        WriteFloat(swell, Hold.Swell(g, low, _reduced, _calm), SwellEps);
                        WriteFloat(bloom, Hold.Bloom(g, level, playing), AlphaEps);
                    }
                }
            }
            else
            {
                float p = BlockLand(rig, now, playing);
                landing |= p > 0f && p < 1f;
                WriteFloat(rig.BlockAlpha, Land.Alpha(p), AlphaEps);
                WriteFloat(rig.BlockDy, Land.Drop(p, _reduced), DyEps);
                WriteBlur(scene, rig.BlockNode, Land.Blur(p, _reduced, rig.Blur));
                if (rig.WholeWipe)
                {
                    var line = song.Doc.Lines[rig.Line];
                    float split = Lyrics.Wipe.ComputeSplit(line, now);
                    if (split > 0f && split < 1f) split = Math.Clamp(split + Lyrics.Wipe.LeadFrac, 0f, 1f);
                    WriteWipe(scene, rig.WholeText, split, default, recolour: false);
                }
            }
            WriteFloat(rig.Progress, HairProgress(rig, now), 0f);
            voice = rig.SungOutMs < Lanes.None && now >= rig.StartMs && now < rig.SungOutMs;
            float e = Echo.Alpha(now, rig.StartMs, _beatMs, _reduced);
            WriteFloat(rig.Echo, e, AlphaEps);
            echo = e > 0f;
        }

        const float AlphaEps = 1f / 256f, DyEps = 0.05f, SwellEps = 1f / 512f, BlurEps = 0.25f;

        /// <summary>Value-gated: an endpoint (0, 1, an exact rest) is written exactly; a move under the gate writes nothing.</summary>
        static void WriteFloat(FloatSignal s, float v, float eps)
        {
            float cur = s.Peek();
            if (cur == v) return;
            if (MathF.Abs(cur - v) >= eps || v == 0f || v == 1f || v == Land.WaitAlpha) s.Value = v;
        }

        /// <summary>A landing σ straight onto the node's paint column (no bound channel for blur), PaintDirty on that node only;
        /// the landed 0 is exact so the recorder drops the layer.</summary>
        static void WriteBlur(SceneStore scene, NodeHandle h, float sigma)
        {
            if (h.IsNull || !scene.IsLive(h)) return;
            ref NodePaint paint = ref scene.Paint(h);
            float cur = paint.BlurSigma;
            if (cur == sigma || (sigma != 0f && MathF.Abs(cur - sigma) < BlurEps)) return;
            paint.BlurSigma = sigma;
            scene.Mark(h, NodeFlags.PaintDirty);
        }

        /// <summary>The glyph wipe on one text node — the caption's write path in shape: the settled/eps write-stops, and the
        /// sung colour turned to ink once the word is done.</summary>
        static void WriteWipe(SceneStore scene, NodeHandle h, float split, ColorF before, bool recolour)
        {
            if (h.IsNull || !scene.IsLive(h) || !scene.TryGetGlyphWipe(h, out var w)) return;
            bool moved = !Lyrics.Wipe.SplitSettled(split, w.Split) && MathF.Abs(split - w.Split) > Lyrics.Wipe.SplitEps;
            bool tinted = recolour && w.Before != before;
            if (!moved && !tinted) return;
            scene.SetGlyphWipe(h, recolour ? w with { Split = split, Before = before } : w with { Split = split });
            scene.Mark(h, NodeFlags.PaintDirty);
        }

        void Apply(in Lyrics.MotionDecision d, long now)
        {
            _live.SetIfChanged(d.NeedsTicks);
            float delay = d.NeedsTicks || d.WakeAtMs >= Lanes.None
                ? -1f
                : MathF.Max(1f, (float)((d.WakeAtMs - now) / Math.Max(0.25, _clock.ContentRate)));
            if (delay < 0f && _wake.Peek().DelayMs < 0f) return;
            _wake.Value = new Lyrics.MotionWake(++_wakeSeq, delay);
        }

        /// <summary>The beat period from the grid's own index edges (one effect run per beat, while the visualizer clock runs):
        /// what the count-in and the echo count in. Without a grid it stays 0 and both fall back to 500 ms.</summary>
        void OnBeat()
        {
            if (_slab is not { } slab) return;
            int b = slab.BeatIndex.Value;
            long t = FrameTime.NowMs;
            if (_lastBeat != int.MinValue && b == _lastBeat + 1)
            {
                float d = t - _lastBeatWallMs;
                if (d is > 200f and < 2000f) _beatMs = _beatMs <= 0f ? d : _beatMs + (d - _beatMs) * 0.25f;
            }
            _lastBeat = b;
            _lastBeatWallMs = t;
        }

        /// <summary>The motion-demand child: the paced per-frame ticker while a lane moves, a one-shot wake at the next media
        /// instant otherwise, a 4 Hz re-check while paused (a seek still lands). Its own component, so a lane flip re-renders
        /// only it.</summary>
        sealed class Driver(Host host) : Component
        {
            public override Element Render()
            {
                bool timed = host._timed.Value, live = host._live.Value;
                bool playing = Playback.IsPlaying.Value;
                var wake = host._wake.Value;
                UseSignalEffect(host._onBeat);
                UseInterval(host._tick, 250f, enabled: timed && !playing);
                Element? child = !timed || !playing ? null
                    : live ? Embed.Comp(() => new Frames(host._tick)) with { Key = "verse:frames" }
                    : wake.DelayMs >= 0f ? Embed.Comp(() => new Waker(host)) with { Key = "verse:wake" }
                    : null;
                return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false, Children = child is null ? [] : [child] };
            }
        }

        /// <summary>The one-shot wake (re-armed by a new sequence number, so the same delay twice still restarts).</summary>
        sealed class Waker(Host host) : Component
        {
            public override Element Render()
            {
                var wake = host._wake.Value;
                UseTimeout(host._tick, MathF.Max(wake.DelayMs, 1f), DepKey.From(wake.Seq));
                return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
            }
        }

        /// <summary>Verse's per-frame tick, named for the <c>[wake]</c> census; paceable — every value it writes is a pure
        /// function of the media clock, so a governor-paced frame lands on the same picture.</summary>
        sealed class Frames(Action tick) : Controls.FrameTicker(tick, paceable: true);
    }

    // ══ 4. THE CLOUD WORD ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>One cloud word: its root drifts on looping ping-pong keyframes (zero ticks; the keyframes target this
    /// component's root, which is why it is a component — the Field blob's shape), its inner box breathes with its band.
    /// Reduced motion swaps the loop for rest keys and zeroes the breath; the word and its brightness stay.</summary>
    sealed class CloudWord : Component
    {
        public sealed record Props(string Text, float Size, ushort Weight, int Index, Visualizer.Slab Slab, float DriftX, float DriftY);
        static readonly Keyframe[] s_rest = [new Keyframe(0f, 0f), new Keyframe(1f, 0f)];

        public override Element Render()
        {
            var p = UseProps<Props>();
            bool reduced = Design.Reduced;
            float ms = CloudPeriodMs + (p.Index % 5) * 3000f;
            var key = DepKey.From(p.Index, (int)p.DriftX, (int)p.DriftY, reduced ? 1 : 0);
            UseKeyframes(AnimChannel.TranslateX, reduced ? s_rest : [new Keyframe(0f, 0f), new Keyframe(0.5f, p.DriftX, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms, loop: !reduced, key);
            UseKeyframes(AnimChannel.TranslateY, reduced ? s_rest : [new Keyframe(0f, 0f), new Keyframe(0.5f, p.DriftY, Easing.EaseInOut), new Keyframe(1f, 0f, Easing.EaseInOut)], ms * 1.3f, loop: !reduced, key);
            var slab = p.Slab;
            var band = slab.Bands[(p.Index * 7 + 3) % slab.Bands.Length];
            var tint = (p.Index % 3) switch { 0 => slab.A, 1 => slab.B, _ => slab.C };
            ColorF ink = Ink.Ink;
            float amp = reduced ? 0f : CloudBreath;
            return new BoxEl
            {
                HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        HitTestVisible = false,
                        Opacity = Prop.Of(() => MathF.Min(1f, CloudAlphaFloor + CloudAlphaBand * band.Value)),
                        Transform = Prop.Of(() => { float s = 1f + amp * band.Value; return Affine2D.Scale(s, s); }),
                        Children =
                        [
                            new TextEl(p.Text)
                            {
                                Size = p.Size, Weight = p.Weight, FontFamily = DisplayFace, Color = Prop.Of(() => ColorF.Lerp(ink, tint.Value, CloudTint)),
                                Wrap = TextWrap.NoWrap, MaxLines = 1,
                            },
                        ],
                    },
                ],
            };
        }
    }
}
