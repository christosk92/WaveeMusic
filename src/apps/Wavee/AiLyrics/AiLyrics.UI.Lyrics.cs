// ── AiLyrics/AiLyrics.UI.Lyrics.cs ───────────────────────────────────────────────────────────────────────────────────
// HeaderButton (the lyrics rail's sparkle), Footer (the end-of-lyrics AI disclosure) — plan §4.4, §4.5
//
// Role: UI (executes Rules.Header / Rules.ShowsFooter / Rules.FooterKey; decides nothing itself)
//
//   HeaderButton   mounted by Rail.LyricsHeader after the globe toggle. Its own component, so only IT subscribes the AI
//                  status, the track status and the playing entity: a progressive publish re-renders one 32-DIP glyph,
//                  never the rail header. Hidden (Visible = false, zero extent) while the feature is off or cannot run.
//                  While the current track is being timed the glyph breathes 1.0 → 0.55 → 1.0 on a looping opacity
//                  keyframe track (an idle-cadence engine loop sampled on frame time — no spinner, no re-render).
//                  Click: Settings › Appearance when Settings can fix what the tooltip says (Rules.OpensSettings);
//                  otherwise the tooltip is announced, so a click is never silent for a screen reader.
//   Footer         mounted by Lyrics.ViewCore BELOW the lyrics viewport in its root column — never a virtual-list item
//                  (the follow geometry assumes ItemCount == Lines.Count). One muted line, sparkle + text (never the
//                  icon alone). The clip box grows through real layout (SizeMode.Reflow), so the list above it shrinks
//                  smoothly instead of jumping; the line fades in under the stationary clip.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class AiLyrics
{
    // MOUNT POINT (Rail.LyricsHeader)
    /// <summary>The lyrics rail header's sparkle button, or a hidden zero-extent box while the feature is off.</summary>
    public static Element HeaderButton() => Embed.Comp(static () => new AiHeaderButton()) with { Key = "ai-lyrics-header" };

    // MOUNT POINT (Lyrics.ViewCore root column)
    /// <summary>The end-of-lyrics disclosure strip. <paramref name="ink"/> and <paramref name="sidePad"/> are the mounting
    /// surface's mount config (the rail's theme ink and gutter, or the stage's media ink and gutter) — frozen at mount,
    /// which is correct: each surface mounts one instance for its life. Everything that changes is a signal read inside.</summary>
    public static Element Footer(Lyrics.Ink ink, float sidePad)
        => Embed.Comp(() => new AiLyricsFooter(ink, sidePad)) with { Key = "ai-lyrics-footer" };

    /// <summary>The playing track's lyrics-store id ("" for an episode or nothing) — the id <see cref="TrackStatus"/>
    /// carries, so a surface can refuse a status that still describes the previous track. Subscribes the playable.</summary>
    static string PlayingLyricsId()
    {
        var current = Playback.Current.Value;
        _ = Entities.ScopeEpoch.Value;   // FIRST (G-179): a scope switch re-points the table read below
        return current.Kind == EntityKind.Track && !current.IsNone ? Lyrics.Store.IdOf(new global::Wavee.Track(current.Slot)) : "";
    }

    /// <summary>The sparkle. The root BoxEl is the component's host node: the pulse loop runs on ITS opacity channel, so
    /// the rail's shared <see cref="Rail.HeaderButton"/> stays untouched. The root's static Opacity is never declared, so
    /// a re-render never stomps the channel the loop (or the settle spring) owns.</summary>
    sealed class AiHeaderButton : Component
    {
        const float PulseMs = 1800f;
        /// <summary>1.0 → 0.55 → 1.0, eased both ways: a quiet breath, not a blink.</summary>
        static readonly Keyframe[] s_pulse =
            [new Keyframe(0f, 1f), new Keyframe(0.5f, 0.55f, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)];
        /// <summary>Back to full opacity from wherever the loop was, critically damped (never overshoots 1).</summary>
        static readonly SpringParams s_settle = SpringParams.FromResponse(0.35f, 1f);
        static readonly Action s_openSettings = static () => Settings.Open(Settings.Tab.Appearance);

        readonly Action _announce;
        readonly Action _drivePulse;
        string _tip = "";
        bool _pulse;    // this render's verdict (the layout effect reads it)
        bool _pulsed;   // a loop was seeded and has not been settled yet

        public AiHeaderButton()
        {
            _announce = () => FluentGpu.Input.Announcer.Say(_tip);
            _drivePulse = DrivePulse;
        }

        public override Element Render()
        {
            var track = Track.Value;
            var state = Rules.Header(Current.Value, track, Playback.CurrentId.Value.Kind);
            // Reduced motion is a VALUE folded into the verdict: the glyph simply stays at rest.
            _pulse = state.Visible && track.Phase == TrackPhase.Working && !Design.Reduced;
            // Keyed on the verdict only: a progressive publish (LinesReady moves) re-renders the tooltip, never re-seeds
            // the loop mid-breath.
            UseLayoutEffect(_drivePulse, DepKey.From(_pulse));

            if (!state.Visible) return new BoxEl { Visible = false };
            _tip = Loc.Get(state.TipKey);
            return new BoxEl
            {
                Shrink = 0f,
                Children = [Rail.HeaderButton(Icons.RefineSparkle, _tip, state.OpensSettings ? s_openSettings : _announce, active: state.Active)],
            };
        }

        void DrivePulse()
        {
            var ctx = Context;
            if (ctx.Anim is not { } anim || ctx.HostNode.IsNull) return;
            if (_pulse)
            {
                anim.Keyframes(ctx.HostNode, AnimChannel.Opacity, s_pulse, PulseMs, loop: true);
                _pulsed = true;
            }
            else if (_pulsed)
            {
                anim.Spring(ctx.HostNode, AnimChannel.Opacity, 1f, s_settle);
                _pulsed = false;
            }
        }
    }

    /// <summary>The disclosure strip. An always-present zero-extent root (a component root's Key is inert — the keyed
    /// clip box is its CHILD, the Artist pick-photo idiom); the clip box mounts only while
    /// <see cref="Rules.ShowsFooter"/> holds for the track on screen.</summary>
    sealed class AiLyricsFooter(Lyrics.Ink ink, float sidePad) : Component
    {
        const float GlyphSize = 12f, TextSize = 12f, TextLineHeight = 16f;

        // The CLIP box eases its height through real layout (siblings reflow — the lyrics viewport shrinks smoothly); the
        // line inside fades under the stationary window. SuppressDescendantTransitions: the line's own fade is the only
        // second track, never a second geometry wave.
        static readonly LayoutTransition s_grow = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height, SuppressDescendantTransitions: true);
        static readonly LayoutTransition s_fadeIn = new(TransitionChannels.Opacity, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Opacity: 0f, Active: true));

        readonly Lyrics.Ink _ink = ink;
        readonly float _sidePad = sidePad;

        public override Element Render()
        {
            var track = Track.Value;
            string playing = PlayingLyricsId();
            var root = new BoxEl { Direction = 1, Shrink = 0f, MinWidth = 0f };
            // A status that still names the previous track (the host re-evaluates on its own effect) never captions the
            // next track's lyrics.
            if (!Rules.ShowsFooter(track) || track.TrackId.Length == 0 || !string.Equals(track.TrackId, playing, StringComparison.Ordinal))
                return root;   // zero extent: nothing reserved

            var color = _ink.Secondary;
            return root with
            {
                Children =
                [
                    new BoxEl
                    {
                        Key = "ai-lyrics-footer-clip", Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
                        // Reduced motion snaps the height, keeps the fade.
                        Animate = Design.Reduced ? null : s_grow,
                        Children =
                        [
                            new BoxEl
                            {
                                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                                Padding = new Edges4(_sidePad, Spacing.S, _sidePad, Spacing.S),
                                Animate = s_fadeIn,
                                HitTestVisible = false,
                                Children =
                                [
                                    new TextEl(Icons.RefineSparkle)
                                    {
                                        Size = GlyphSize, FontFamily = Theme.IconFont, Color = color, Shrink = 0f,
                                    },
                                    new TextEl(Loc.Get(Rules.FooterKey(track)))
                                    {
                                        Size = TextSize, LineHeight = TextLineHeight, Color = color,
                                        Grow = 1f, Shrink = 1f, MinWidth = 0f,
                                        Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };
        }
    }
}
