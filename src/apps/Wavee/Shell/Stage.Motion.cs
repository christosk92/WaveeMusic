// ── Shell/Stage.Motion.cs ──────────────────────────────────────────────────────────────────────────────────────────
// Stage.Choreo: the full screen's entrance, its exit and Video mode's Cinema ⇄ Theatre switch as ONE calm motion; Stage.Traveller: the block
// that travels between Cinema and Theatre (the title) instead of cross-fading two copies of itself
//
// Role: CORE
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The owner (2026-11-11, the What's New footage): moving a video between the docked panel, Cinema and Theatre must read as one continuous
// motion, "smooth", "soothing", never "sudden", nothing that "stops" mid-way. Three things broke it:
//
//   ENTRY. The page under the stage collapsed the frame the stage mounted (FrameModel.ChromeMounted read the open flag), so the page vanished
//   in one frame and left the bare window ground under a stage still at opacity 0; and every part of the stage mounted in the open's first
//   frames, the chrome over the picture (top bar, control bar, shelf, title) a frame after the open, which made the frame right after the open
//   the heaviest one: the picture's glide aims on that frame, so it held at the docked spot for two long frames (~105 ms in a Debug build).
//   Now the page stays laid out and painted under the stage until the stage's ground is opaque and the picture has landed (`Covers`, CoverMs:
//   then it collapses, as before, so nothing under the stage costs GPU at rest), and the stage's CONTENT comes in BEATS after the picture
//   has mostly landed (`StageCtx.Entrance`: beat 1 at ArriveMs, one more every BeatMs; the structure gates each part on its beat, so each
//   mounts on its own frame and plays its own house Enter). The open's frames carry the ground and the hollow reservation only, and the glide
//   runs from the first frame after the open.
//
//   SWITCH. Cinema's title faded out in place while Theatre's faded in under the shrinking picture: two copies. Now one block travels
//   (`Traveller`: its box moves on the picture's own spring and its type scales from the size it had to the size it has, on the same
//   spring); what exists in one arrangement only leaves first (`StageCtx.Swapping`: the picture moves LeadMs later) and what the other
//   arrangement alone shows comes in once the move has mostly landed (SwapMs). The machine owns `StageCtx.Theatre` for it.
//
//   EXIT. The stage faded out over the page while the picture flew home: both full layouts at half opacity. Now Close leaves in two steps
//   when the picture is staged: the content fades out first (`Leaving`, LeaveMs), then the picture flies home while the ground fades to
//   reveal the page (laid out at its restored size: Close restores the window before the page comes back).
//
// THE VIDEO HOLE (Video.UI.cs rule 1) is untouched: the picture is never inside the stage's subtree (it follows a hollow reservation from
// the video layer), so every fade here is on the stage's ground and chrome, never on an ancestor of the picture.
//
// Reduced motion is a value: every delay is 0, the beats land at once.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The full screen's choreography (see the file header): the beats the content enters on, when the page under the stage may
    /// collapse, the two-step exit and the Cinema ⇄ Theatre switch's gate.</summary>
    public static class Choreo
    {
        /// <summary>The content's beats: 1 (the title, the scrims, the end card, the audio face), 2 (the control bar, the shelf, Theatre's
        /// panel and about block), 3 (the top bar, the hairline). The first lands when the picture's glide has covered most of its way (the
        /// stage's entrance spring, response 0.30 s: about 90 % at 180 ms), each next one BeatMs later: a soft stagger.</summary>
        public const int Beats = 3;
        public const float ArriveMs = 180f, BeatMs = 80f;

        /// <summary>The stage's ground is opaque (its entrance spring has landed) and the picture has landed: the page under it collapses.</summary>
        public const float CoverMs = 480f;

        /// <summary>The content's exit before the picture flies home (the house exits run 250 ms on FluentStandard: most of it is gone by then).</summary>
        public const float LeaveMs = 150f;

        /// <summary>A Cinema ⇄ Theatre switch: what exists in one arrangement only comes in once the picture's move (0.45 s, damping 0.90 in
        /// Classic; the themes' own springs are as calm) has covered most of its way.</summary>
        public const float SwapMs = 260f;

        /// <summary>A Cinema ⇄ Theatre switch's lead: what exists in one arrangement only (Cinema's shelf and veils, Theatre's panel and about block)
        /// starts its exit this long before the picture moves, so it is mostly gone and never lies over the moving picture.</summary>
        public const float LeadMs = 120f;

        static readonly Signal<bool> s_covers = new(false), s_leaving = new(false);

        /// <summary>The stage covers the page now (subscribes): mounted, its ground opaque and the picture landed. The frame collapses its
        /// chrome, content region and player bar on it (<c>FrameFacts.ChromeMounted</c>); before it the page stays under the fading
        /// ground, after Close it is back at once (laid out at the window's restored size).</summary>
        public static IReadSignal<bool> Covers => s_covers;

        /// <summary>The stage is leaving (subscribes): the content's exit, before the picture flies home and the ground fades.</summary>
        public static IReadSignal<bool> Leaving => s_leaving;

        static Machine? s_live;

        /// <summary>The first step of a close while the picture is staged (Video mode): the content leaves now, the rest after <see cref="LeaveMs"/>.
        /// False when there is no such step (no stage mounted, an audio mode, reduced motion, already leaving): the caller closes at once.</summary>
        internal static bool TryLeave(Action<string>? begin, string cause)
        {
            if (s_leaving.Peek()) return true;
            if (s_live is not { } m || !Picture.ShownPeek() || Design.Reduced) return false;
            s_leaving.Value = true;
            m.ArmLeave(begin, cause);
            return true;
        }

        /// <summary>The close itself (the second step): the page comes back under the stage before the stage unmounts.</summary>
        internal static void Closed() => s_covers.Value = false;

        /// <summary>The mounted stage's machine (SurfaceCore owns it; its three timers are SurfaceCore's hooks).</summary>
        internal sealed class Machine
        {
            readonly StageCtx _ctx;
            readonly Action<Action<string>?, string> _close;
            TimerHandle _beat, _swap, _lead, _leave;
            double _openedMs = double.NaN;
            bool? _theatre;
            Action<string>? _begin;
            string _cause = "";

            public readonly Action OnBeat, OnSwap, OnLead, OnLeave;

            public Machine(StageCtx ctx, Action<Action<string>?, string> close)
            {
                _ctx = ctx;
                _close = close;
                OnBeat = Beat;
                OnSwap = () => _ctx.Swapping.Value = false;
                OnLead = () => { if (_ctx.Swapping.Peek()) Lead(); };   // the hook's own first arming fires with no switch pending
                OnLeave = () => { if (s_leaving.Peek()) _close(_begin, _cause); };   // the hook's own first arming fires with nothing to close
            }

            /// <summary>The timers, re-bound every render (the handles are the hooks').</summary>
            public void Bind(TimerHandle beat, TimerHandle swap, TimerHandle lead, TimerHandle leave) { _beat = beat; _swap = swap; _lead = lead; _leave = leave; }

            static float Scale => Design.Reduced ? 0f : 1f;

            /// <summary>The stage mounted: the content waits for its beats, the page stays under the ground until it covers.</summary>
            public void Mount()
            {
                s_live = this;
                s_leaving.Value = false;
                _openedMs = _beat.NowMs;
                _ctx.Entrance.Value = 0;
                s_covers.Value = false;
                Beat();
            }

            public void Unmount()
            {
                if (!ReferenceEquals(s_live, this)) return;
                s_live = null;
                s_covers.Value = false;
                s_leaving.Value = false;
            }

            /// <summary>Advance to every beat that is due, cover the page when its time has come, and arm the timer for what is next.</summary>
            void Beat()
            {
                if (double.IsNaN(_openedMs)) return;
                float k = Scale;
                double t = _beat.NowMs - _openedMs;
                int beat = 0;
                for (int i = 1; i <= Beats; i++) if (t >= k * (ArriveMs + (i - 1) * BeatMs)) beat = i;
                _ctx.Entrance.SetIfChanged(beat);
                if (t >= k * CoverMs) s_covers.SetIfChanged(true);
                double next = beat < Beats ? k * (ArriveMs + beat * BeatMs) : k * CoverMs;
                if (beat < Beats || !s_covers.Peek()) _beat.RestartIn((float)Math.Max(1.0, next - t));
            }

            /// <summary>Theatre as wanted (Video mode, the stored or held choice, and room for the panel): what <see cref="StageCtx.Theatre"/> shows.
            /// Before the stage has settled (its entrance, or the first resolve) it is shown at once; after it a change (the toggle, a tab, Esc's
            /// step, a crossing) first lets what exists in one arrangement leave (<see cref="LeadMs"/>), then moves the picture, and brings what the
            /// other arrangement alone shows once the move has mostly landed (<see cref="SwapMs"/>).</summary>
            public void Theatre(bool want)
            {
                bool? was = _theatre;
                _theatre = want;
                if (was is null || _ctx.Entrance.Peek() < Beats || Design.Reduced)
                {
                    _lead.Cancel();
                    _ctx.Theatre.SetIfChanged(want);
                    return;
                }
                if (want == _ctx.Theatre.Peek())
                {
                    // toggled back before the move began: nothing moves, what left comes back
                    _lead.Cancel();
                    if (_ctx.Swapping.Peek()) _swap.RestartIn(1f);
                    return;
                }
                _ctx.Swapping.Value = true;
                _lead.RestartIn(LeadMs);
            }

            /// <summary>The lead is over: the picture moves now; what the other arrangement alone shows comes in after the move.</summary>
            void Lead()
            {
                if (_theatre is { } want) _ctx.Theatre.SetIfChanged(want);
                _swap.RestartIn(SwapMs);
            }

            public void ArmLeave(Action<string>? begin, string cause)
            {
                _begin = begin;
                _cause = cause;
                _leave.RestartIn(LeaveMs);
            }
        }
    }

    public sealed partial class StageCtx
    {
        /// <summary>The entrance's beat (<see cref="Choreo"/>): 0 when the stage opens, <see cref="Choreo.Beats"/> once all of its content is in.</summary>
        public readonly Signal<int> Entrance = new(0);

        /// <summary>A Cinema ⇄ Theatre switch is moving the picture (<see cref="Choreo.SwapMs"/>): what one arrangement alone shows waits.</summary>
        public readonly Signal<bool> Swapping = new(false);

        /// <summary>A part that enters on <paramref name="beat"/> is in (subscribes): its beat has come and the stage is not leaving. A structure
        /// gates each part of its content on it (the part's Show then plays its own Enter, and its Exit as the stage leaves).</summary>
        public bool In(int beat) => Entrance.Value >= beat && !Choreo.Leaving.Value;

        /// <summary><see cref="In"/> for a part that exists in ONE arrangement (Cinema's shelf and scrims, Theatre's panel and about block): it
        /// also waits out a Cinema ⇄ Theatre move, so it leaves before the move and comes in after it, never beside its counterpart.</summary>
        public bool Alone(int beat) => In(beat) && !Swapping.Value;
    }

    /// <summary>A component whose placed block TRAVELS between its Cinema and Theatre places (the title, its artist line and its "Playing from"
    /// line): one node, its position on the picture's own spring (a FLIP of its laid-out rect) and its type, a different size in each place,
    /// scaled from the size it had to the size it has on the same spring, from its top-left corner. So the block moves and resizes in step
    /// with the picture instead of two copies cross-fading.</summary>
    public abstract class Traveller : Component
    {
        NodeHandle _node;
        float _size = float.NaN;
        MotionTokenDef _motion;
        readonly Action<NodeHandle> _realized;

        protected Traveller() => _realized = h => _node = h;

        /// <summary>The placed block (a direct child of a full-bleed ZStack layer, placed by its own alignment and margin) at type size
        /// <paramref name="typeSize"/>, travelling on a spring of <paramref name="response"/> s and <paramref name="damping"/>.</summary>
        protected BoxEl Travel(BoxEl placed, float typeSize, float response, float damping)
        {
            _motion = MotionTokenDef.SpringOf(SpringParams.FromResponse(response, damping));
            float size = typeSize;
            UseLayoutEffect(() => { Seed(size); return null; }, DepKey.From(BitConverter.SingleToInt32Bits(size)));
            return placed with
            {
                OnRealized = _realized, TransformOriginX = 0f, TransformOriginY = 0f,
                Layout = new LayoutTransition(TransitionChannels.Position, TransitionDynamics.Spring(response, damping)),
            };
        }

        void Seed(float size)
        {
            float from = _size;
            _size = size;
            if (float.IsNaN(from) || from <= 0f || from == size || _node.IsNull) return;
            if (Context.Scene is not { } scene || !scene.IsLive(_node) || Context.Anim is not { } anim) return;
            float s = from / size;
            anim.SeedValue(_node, AnimChannel.ScaleX, 1f, in _motion, from: s);
            anim.SeedValue(_node, AnimChannel.ScaleY, 1f, in _motion, from: s);
        }
    }
}
