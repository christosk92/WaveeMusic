// ── Shell/Deck.Stage.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the now-playing STAGE: the art area of every theme's hero, which draws the chosen player style at rest and becomes the in-place style
// picker's carousel (2026-11-08 redesign, `canvas-design/project/Styles.dc.html`)
//
// Role: UI (core: the themes host it through `NowPlayingHeroProps.Stage` and draw only the chip, the header and the strip under it)
//
// AT REST it is the stored style, one cell the column's width: Cover (the theme's own cover image), Canvas (the track's looping video over the
// cover, 9:16, or the square cover when the track has none) or a deck (`Deck.Face`: Vinyl, CD, Cassette, ...).
//
// PICKING (`Shell.StylePicker.Open`) the same cell shrinks to 0.84 and its ring neighbours fade in at the sides at 0.66 and half opacity, so
// opening and closing the picker REMOUNTS NOTHING: the stored style's cell is the focused one. A neighbour takes a click (it focuses that
// style); a horizontal swipe drags the ring. The ring is circular (after Metro comes Cover), and only the cells within two places of the
// position are mounted, so a move mounts the next style as it enters, at opacity 0.
//
// MOTION. The ring is three scalars, each eased toward a target by one per-frame ticker that runs only while one of them is moving: the
// position, the focused scale (1 or 0.84) and the peek (0 at rest, 1 picking: the neighbours' presence). The cells bind their transform and
// opacity to them (paint only, never a layout write per frame). The stage's HEIGHT is the focused art's at the focused scale (the Canvas cell
// is 9:16 when it has a canvas to play), a plain value per render under a Reflow height transition: the engine eases it and the hero's meta
// under it rides every change instead of jumping (owner rule: no layout jumps). Reduced motion is a VALUE: the ring lands in one step and the
// height takes no transition.
//
// NOTHING DECODES unless the style is Canvas: the canvas layer claims the one canvas player only on the Canvas cell while it is the stage's
// centre (the stored style at rest, the focused one while picking).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Deck
{
    /// <summary>The focused cell's scale while picking, a neighbour's, and a neighbour's offset as a fraction of the column (the design's
    /// 214 of 328).</summary>
    public const float PickScale = 0.84f, PeekScale = 0.66f, PeekSpread = 214f / 328f;

    /// <summary>A canvas is 9:16: the Canvas cell's height over its width when the track has one.</summary>
    public const float CanvasAspect = 16f / 9f;

    /// <summary>The stage's node keys: its root, a style's cell ("npv-cell:vinyl") and a peeking neighbour's hit plate ("npv-peek:cd").</summary>
    public const string StageKey = "npv-stage", CellPrefix = "npv-cell:", PeekPrefix = "npv-peek:";

    /// <summary>The now-playing stage. <paramref name="chrome"/> is the hero's horizontal padding around the stage's column (both sides
    /// together): the carousel's neighbours peek into it, up to the tile's edge. <paramref name="cover"/> builds the theme's cover image (one
    /// cached delegate: the props compare it by reference); <paramref name="corners"/> rounds the canvas the way the cover is rounded.</summary>
    public static Element Stage(float chrome, Func<Element> cover, CornerRadius4 corners)
        => Embed.Comp(new StageProps(chrome, cover, corners), static () => new StageHost()) with { Key = StageKey };

    sealed record StageProps(float Chrome, Func<Element> Cover, CornerRadius4 Corners);

    sealed class StageHost : Component
    {
        /// <summary>The easing's time constant: 95 % of a move in ~270 ms, settled in ~420 ms (the design's 450 ms decelerate).</summary>
        const float TauMs = 90f;
        /// <summary>How close a scalar must come to its target before it lands on it.</summary>
        const float Settle = 0.0015f;
        /// <summary>A swipe this far (DIP) is a drag, not a click.</summary>
        const float DragSlop = 6f;

        readonly FloatSignal _pos = new(0f), _zoom = new(1f), _peek = new(0f), _width = new(float.NaN);
        readonly Signal<bool> _moving = new(false);
        float _posTo, _zoomTo = 1f, _peekTo;
        long _lastTickMs;
        bool _seeded, _wasOpen;

        StageProps _props = null!;
        int _center;
        bool _tall;

        // the swipe
        bool _dragging, _dragMoved;
        float _dragX0, _dragPos0;

        Action? _tick;
        Action<RectF>? _onBounds;
        Func<int, bool>? _escape, _savedPreview;
        bool _escapeInstalled;
        InputHooks? _hooks;

        public override Element Render()
        {
            _props = UseProps<StageProps>();
            _hooks = UseContext(InputHooks.Current);
            bool open = Shell.StylePicker.Open.Value;
            int stored = Rail.PlayerPrefs.Style();
            int center = open ? Shell.StylePicker.Focus.Value : stored;
            _center = center;
            bool canvasShows = CanvasVideo.Current.Value.Shows && CanvasVideo.Enabled();
            float fallbackSide = MathF.Max(1f, Shell.Ui.RailWidth.Value - _props.Chrome);

            _tick ??= Tick;
            // The window spans the tile (the column and the hero's padding either side): the column is its width less that padding.
            _onBounds ??= r =>
            {
                float column = r.W - _props.Chrome;
                if (!(MathF.Abs(_width.Peek() - column) <= 0.25f)) _width.Value = column;
            };

            // The ring's targets follow the picker; the ticker eases toward them. Deps-gated: never a write in render.
            bool wantsTall = center == Rail.PlayerCatalog.Canvas && canvasShows;
            _tall = wantsTall;
            UseEffect(() => Retarget(center, open), DepKey.From(center, open ? 1 : 0));
            UseEffect(() => InstallEscape(open), DepKey.From(open ? 1 : 0));
            // Unmounted while picking (the panel closed, the theme switched): the picker cancels, and the Escape preview goes back.
            UseEffect(() => () =>
            {
                UninstallEscape();
                Shell.StylePicker.Cancel();
            }, DepKey.Empty);

            // What is mounted re-renders only when the ring passes a whole place, settles on one, or the neighbours come or go: the
            // window is the floor of the position, doubled, plus one while it sits between two places.
            var window = UseComputed(() =>
            {
                float p = _pos.Value, f = MathF.Floor(p);
                return (int)f * 2 + (p - f > Settle ? 1 : 0);
            });
            var peeking = UseComputed(() => _peek.Value > Settle);
            int baseId = window.Value >> 1;
            bool between = (window.Value & 1) != 0;
            bool neighbours = peeking.Value || open;
            bool moving = _moving.Value;

            float w = _width.Value;
            float side = float.IsFinite(w) && w > 0f ? MathF.Floor(w) : MathF.Floor(fallbackSide);
            float deckSide = ClockRules.SideWithin(side);
            // The focused art's height at the focused scale: the engine eases a change (a style, the picker, a canvas that lands).
            float height = MathF.Round(side * (wantsTall ? CanvasAspect : 1f) * (open ? PickScale : 1f));

            // The cells in ring order from the position's left: the centre alone at rest; picking, every style less than two places away
            // (the focused one and its two neighbours on a place, four while the ring moves between two).
            int n = Rail.PlayerCatalog.Count;
            Span<bool> taken = stackalloc bool[n];
            var cells = new List<Element>(5);
            if (neighbours)
                for (int k = -1; k <= (between ? 2 : 1); k++)
                {
                    int id = Mod(baseId + k, n);
                    if (taken[id]) continue;
                    taken[id] = true;
                    cells.Add(Cell(id, side, deckSide, open));
                }
            if (!taken[center]) cells.Add(Cell(center, side, deckSide, open));

            // The carousel's window: the tile's full width (it reaches into the hero's padding, where the neighbours peek), clipped there
            // and at the stage's height. ONE box carries the height and its transition and the clip, so the clip a running size transition
            // applies is the window's own (an inner window narrower than the animating box would trim the peeks until it lands).
            float half = 0.5f * _props.Chrome;
            return new BoxEl
            {
                Shrink = 0f, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(-half, 0f, -half, 0f), Height = height, ZStack = true, ClipToBounds = true,
                OnBoundsChanged = _onBounds, Animate = Design.Reduced ? null : HeightMotion,
                OnPointerDown = open ? p => { _dragging = true; _dragMoved = false; _dragX0 = p.X; _dragPos0 = _pos.Peek(); } : null,
                OnDrag = open ? p => Drag(p.X, side) : null,
                OnPointerReleased = open ? _ => Release() : null,
                OnDragCanceled = open ? Release : null,
                Children = [.. cells, moving ? Embed.Comp(() => new StageFrames(_tick)) with { Key = "npv-stage-frames" } : new BoxEl { Width = 0f, Height = 0f }],
            };
        }

        /// <summary>The stage's per-frame ticker, named for the <c>[wake]</c> census.</summary>
        sealed class StageFrames(Action tick) : Controls.FrameTicker(tick);

        static int Mod(int v, int n) => ((v % n) + n) % n;

        /// <summary>The ring offset of <paramref name="id"/> from the position, the short way round.</summary>
        float Offset(int id)
        {
            int n = Rail.PlayerCatalog.Count;
            float d = (id - _pos.Value) % n;
            if (d > n * 0.5f) d -= n; else if (d <= -n * 0.5f) d += n;
            return d;
        }

        /// <summary>The stage's and the Canvas cell's height changes: Reflow, so the hero's blocks under the stage re-solve every frame.</summary>
        static LayoutTransition HeightMotion => MotionRecipes.CardResizeHeight;

        Element Cell(int id, float side, float deckSide, bool open)
        {
            var style = Rail.PlayerCatalog.ById(id);
            bool center = id == _center;
            Element art = id switch
            {
                Rail.PlayerCatalog.Cover => new BoxEl { Width = side, Shrink = 0f, Direction = 1, Children = [_props.Cover()] },
                Rail.PlayerCatalog.Canvas => new BoxEl
                {
                    Width = side, Height = MathF.Round(side * (_tall && center ? CanvasAspect : 1f)), Shrink = 0f, ZStack = true,
                    Animate = Design.Reduced ? null : HeightMotion,
                    Children =
                    [
                        new BoxEl { Width = side, Shrink = 0f, Direction = 1, AlignSelf = FlexAlign.Start, Children = [_props.Cover()] },
                        CanvasVideo.Layer(CanvasVideo.Surface.Deck, _props.Corners, default, on: center),
                    ],
                },
                _ => new BoxEl { Width = side, Shrink = 0f, Direction = 0, Justify = FlexJustify.Center, Children = [Face(id, deckSide)] },
            };
            var kids = open && !center
                ? new Element[]
                {
                    art,
                    new BoxEl
                    {
                        Key = PeekPrefix + style.Slug, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Fill = ColorF.Transparent,
                        Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = () => { if (!_dragMoved) Shell.StylePicker.FocusOn(id); },
                    },
                }
                : [art];
            // The keyed cell owns the entrance and the exit (a style that leaves cross-fades out; never a snap); the inner box owns the
            // bound ring pose (a bound opacity and an opacity track must not share a node).
            return new BoxEl
            {
                Key = CellPrefix + style.Slug, Width = side, Shrink = 0f, ZStack = true,
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Center,
                Enter = CellFade, Exit = CellFade, Transition = MotionTok.ControlFast,
                Children =
                [
                    new BoxEl
                    {
                        Width = side, Shrink = 0f, ZStack = true,
                        TransformOriginX = 0.5f, TransformOriginY = 0f,
                        Transform = Prop.Of(() => TransformOf(id, side)),
                        Opacity = Prop.Of(() => OpacityOf(id)),
                        Children = kids,
                    },
                ],
            };
        }

        static readonly EnterExit CellFade = new(Opacity: 0f, Active: true);

        Affine2D TransformOf(int id, float side)
        {
            float d = Offset(id), ad = MathF.Abs(d);
            float s = ad < 1f ? _zoom.Value + (PeekScale - _zoom.Value) * ad : PeekScale;
            return Affine2D.Translation(d * PeekSpread * side, 0f).Multiply(Affine2D.Scale(s, s));
        }

        float OpacityOf(int id)
        {
            float ad = MathF.Abs(Offset(id));
            float rest = ad <= 1f ? 1f - 0.5f * ad : MathF.Max(0f, 0.5f * (2f - ad));
            float presence = ad >= 1f ? _peek.Value : 1f + (_peek.Value - 1f) * ad;
            return rest * presence;
        }

        // ── the motion ──────────────────────────────────────────────────────────────────────────────────────────────────

        void Retarget(int center, bool open)
        {
            bool wasOpen = _wasOpen;
            _wasOpen = open;
            if (!_seeded)
            {
                // The first frame shows the stored style where it rests: no entrance.
                _seeded = true;
                _posTo = center; _zoomTo = open ? PickScale : 1f; _peekTo = open ? 1f : 0f;
                _pos.Value = _posTo; _zoom.Value = _zoomTo; _peek.Value = _peekTo;
                return;
            }
            int n = Rail.PlayerCatalog.Count;
            int from = Mod((int)MathF.Round(_posTo), n);
            _posTo += Rail.PlayerCatalog.RingDelta(from, center);
            // A style stored from elsewhere (Settings, the palette) while the picker is shut does not slide the ring: the old cell
            // cross-fades out and the new one in, in place.
            if (!open && !wasOpen && _pos.Peek() != _posTo)
            {
                _posTo = center;
                _pos.Value = center;
            }
            _zoomTo = open ? PickScale : 1f;
            _peekTo = open ? 1f : 0f;
            Kick();
        }

        void Kick()
        {
            _lastTickMs = 0L;
            if (!_moving.Peek()) _moving.Value = true;
            if (Design.Reduced) Tick();   // a value: land in one step
        }

        void Tick()
        {
            long now = FrameTime.NowMs;
            float dt = _lastTickMs == 0L ? 16f : MathF.Min(64f, now - _lastTickMs);
            _lastTickMs = now;
            float k = Design.Reduced ? 1f : 1f - MathF.Exp(-dt / TauMs);
            bool done = true;
            if (!_dragging) done &= Ease(_pos, _posTo, k);
            done &= Ease(_zoom, _zoomTo, k);
            done &= Ease(_peek, _peekTo, k);
            if (done && !_dragging)
            {
                // Settled: fold the unbounded ring position back into one turn so it never drifts far from the ids.
                int n = Rail.PlayerCatalog.Count;
                float wrapped = Mod((int)MathF.Round(_posTo), n);
                _posTo = wrapped;
                if (_pos.Peek() != wrapped) _pos.Value = wrapped;
                _moving.Value = false;
            }
        }

        static bool Ease(FloatSignal s, float to, float k)
        {
            float v = s.Peek();
            if (v == to) return true;
            float next = v + (to - v) * k;
            if (MathF.Abs(to - next) < Settle) next = to;
            s.Value = next;
            return next == to;
        }

        void Drag(float x, float side)
        {
            if (!_dragging) return;
            float dx = x - _dragX0;
            if (!_dragMoved && MathF.Abs(dx) < DragSlop) return;
            _dragMoved = true;
            _pos.Value = _dragPos0 - dx / MathF.Max(1f, PeekSpread * side);
            if (!_moving.Peek()) _moving.Value = true;   // the cells follow the drag through their bound thunks
        }

        void Release()
        {
            if (!_dragging) return;
            _dragging = false;
            if (!_dragMoved) return;
            float landed = MathF.Round(_pos.Peek());
            _posTo = landed;
            int id = Mod((int)landed, Rail.PlayerCatalog.Count);
            if (id != Shell.StylePicker.Focus.Peek()) Shell.StylePicker.FocusOn(id);   // the effect re-targets from here (a zero ring step)
            Kick();
        }

        // ── Escape is Cancel ────────────────────────────────────────────────────────────────────────────────────────────

        void InstallEscape(bool open)
        {
            if (_hooks is not { } hooks) return;
            _escape ??= key =>
            {
                if ((key == Keys.Escape || key == Keys.GamepadB) && Shell.StylePicker.Open.Peek())
                {
                    Shell.StylePicker.Cancel();
                    return true;
                }
                return _savedPreview?.Invoke(key) ?? false;
            };
            if (open && !_escapeInstalled)
            {
                _escapeInstalled = true;
                _savedPreview = hooks.KeyPreview;
                hooks.KeyPreview = _escape;
            }
            else if (!open) UninstallEscape();
        }

        void UninstallEscape()
        {
            if (!_escapeInstalled || _hooks is not { } hooks) return;
            _escapeInstalled = false;
            if (ReferenceEquals(hooks.KeyPreview, _escape)) hooks.KeyPreview = _savedPreview;
            _savedPreview = null;
        }
    }
}
