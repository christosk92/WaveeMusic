// ── AiLyrics/AiLyrics.UI.Lyrics.cs ───────────────────────────────────────────────────────────────────────────────────
// HeaderFacts / HeaderClick (the lyrics rail's sparkle), Footer (the end-of-lyrics AI disclosure) — plan §4.4, §4.5
//
// Role: UI (executes Rules.Header / Rules.ShowsFooter / Rules.FooterKey; decides nothing itself)
//
//   HeaderFacts    the lyrics header's sparkle VERDICT (`LyricsSparkleFacts`), read by the `Panel.LyricsHeader` view inside the
//                  sparkle's own component (Classic's `ClassicLyricsSparkle`), so only IT subscribes the AI status, the track status
//                  and the playing entity: a progressive publish re-renders one 32-DIP glyph, never the rail header. The picture (the
//                  glyph, hidden while the feature is off or cannot run, and its breath while the track is being timed) is the view's.
//   HeaderClick    what a sparkle click does with the verdict it was drawn from: on a song with people-made word timing, switch
//                  it to AI timing and back (Rules.HeaderState.TogglesPreference, AiLyrics.SetPreferAi); else Settings › Appearance
//                  when Settings can fix what the tooltip says (Rules.OpensSettings); otherwise announce the tooltip, so a click is
//                  never silent for a screen reader.
//   Footer         mounted by Lyrics.ViewCore BELOW the lyrics viewport in its root column — never a virtual-list item
//                  (the follow geometry assumes ItemCount == Lines.Count). One muted line, sparkle + text (never the
//                  icon alone). While a live job runs (Rules.FooterShowsProgress) the line reads "Timing words · x of y
//                  lines" over a thin buffer bar: the timed share of the song (accent, reduced) and a playhead tick, so
//                  the user sees the timing running ahead of where they are. The bar binds: the timed width and the
//                  tick's translate are Prop thunks over the job, the position and the bar's measured width, so a
//                  position tick never re-renders the footer (the timed width eases each chunk, a reflow like the
//                  Settings card's progress fill). Done or cached: the disclosure line alone. The clip box grows through
//                  real layout (SizeMode.Reflow), so the list above it shrinks smoothly instead of jumping; the line
//                  fades in under the stationary clip.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class AiLyrics
{
    // THE SPARKLE (the `Panel.LyricsHeader` view draws it)
    static readonly Action s_openSettings = static () => Settings.Open(Settings.Area.Lyrics);

    /// <summary>The lyrics header sparkle's verdict for this render: the status, the track's status and the playing entity through
    /// <see cref="Rules.Header"/>, with the track's preference. Subscribes all three (and the preference epoch).</summary>
    internal static LyricsSparkleFacts HeaderFacts()
    {
        var track = Track.Value;
        bool prefersAi = PrefersAi(track.TrackId);                                 // subscribes to PreferEpoch
        var state = Rules.Header(Current.Value, track, Playback.CurrentId.Value.Kind, prefersAi);
        return new LyricsSparkleFacts(state.Visible, state.Active, track.Phase == TrackPhase.Working,
            state.Visible ? Loc.Get(state.TipKey) : "", track.TrackId, prefersAi, state.TogglesPreference, state.OpensSettings);
    }

    /// <summary>A sparkle click, acting on the verdict the glyph was drawn from: switch the song between AI timing and the provider's word
    /// timing, open Settings when Settings can fix what the tooltip says, else read the tooltip aloud.</summary>
    internal static void HeaderClick(LyricsSparkleFacts facts)
    {
        if (facts.TogglesPreference) SetPreferAi(facts.TrackId, !facts.PrefersAi);
        else if (facts.OpensSettings) s_openSettings();
        else FluentGpu.Input.Announcer.Say(facts.Tip);
    }

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

    /// <summary>The disclosure strip. An always-present zero-extent root (a component root's Key is inert — the keyed
    /// clip box is its CHILD, the Artist pick-photo idiom); the clip box mounts only while
    /// <see cref="Rules.ShowsFooter"/> holds for the track on screen. While <see cref="Rules.FooterShowsProgress"/> the line
    /// counts the timed lines and a buffer bar sits under it.</summary>
    sealed class AiLyricsFooter : Component
    {
        const float GlyphSize = 12f, TextSize = 12f, TextLineHeight = 16f;
        /// <summary>The bar: a 3-DIP rounded strip centred in an 8-DIP band the 2-DIP playhead tick spans.</summary>
        const float BarHeight = 3f, BarRadius = 1.5f, BandHeight = 8f, TickWidth = 2f, TickRadius = 1f;
        /// <summary>The timed region's share of the accent: a buffer, not the played part.</summary>
        const float TimedAlpha = 0.5f;

        // The CLIP box eases its height through real layout (siblings reflow — the lyrics viewport shrinks smoothly); the
        // line inside fades under the stationary window. SuppressDescendantTransitions: the line's own fade is the only
        // second track, never a second geometry wave.
        static readonly LayoutTransition s_grow = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Active: true), Exit: new EnterExit(Active: true),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height, SuppressDescendantTransitions: true);
        static readonly LayoutTransition s_fadeIn = new(TransitionChannels.Opacity, MotionTok.ControlNormal.ToDynamics(),
            Enter: new EnterExit(Opacity: 0f, Active: true));
        /// <summary>The timed region grows in ~10 s steps (one per chunk): ease each step instead of jumping (the Settings
        /// card's progress fill, <c>s_aiFillEase</c>). Reduced motion snaps it.</summary>
        static readonly LayoutTransition s_timedEase = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
            Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

        readonly Lyrics.Ink _ink;
        readonly float _sidePad;
        /// <summary>The bar's laid-out width, written by its bounds callback; both bindings scale by it.</summary>
        readonly FloatSignal _barWidth = new(0f);
        // Stable delegates (built once): a re-render hands the same bindings back, a position report re-runs one thunk.
        readonly Action<RectF> _onBarBounds;
        readonly Func<float> _timedWidth;
        readonly Func<Affine2D> _playhead;

        public AiLyricsFooter(Lyrics.Ink ink, float sidePad)
        {
            _ink = ink;
            _sidePad = sidePad;
            _onBarBounds = r => _barWidth.SetIfChanged(r.W);
            _timedWidth = TimedWidth;
            _playhead = Playhead;
        }

        /// <summary>The song's length: the job's (the audio it times) for the track on screen, else the player's.</summary>
        static double DurationSeconds()
        {
            var track = Track.Value;
            if (Job.Value is { } job && job.TrackId == track.TrackId && job.DurationSeconds > 0) return job.DurationSeconds;
            return Playback.DurationMs.Value / 1000.0;
        }

        float TimedWidth() => Rules.Fraction(Track.Value.ProcessedSeconds, DurationSeconds()) * _barWidth.Value;

        Affine2D Playhead()
        {
            float travel = MathF.Max(0f, _barWidth.Value - TickWidth);
            return Affine2D.Translation(Rules.Fraction(Playback.PositionMs.Value / 1000.0, DurationSeconds()) * travel, 0f);
        }

        public override Element Render()
        {
            var track = Track.Value;
            string playing = PlayingLyricsId();
            var root = new BoxEl { Direction = 1, Shrink = 0f, MinWidth = 0f };
            // A status that still names the previous track (the host re-evaluates on its own effect) never captions the
            // next track's lyrics.
            if (!Rules.ShowsFooter(track) || track.TrackId.Length == 0 || !string.Equals(track.TrackId, playing, StringComparison.Ordinal))
                return root;   // zero extent: nothing reserved

            bool progress = Rules.FooterShowsProgress(track);
            var color = _ink.Secondary;
            var line = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinWidth = 0f,
                Children =
                [
                    new TextEl(Icons.RefineSparkle)
                    {
                        Size = GlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = color, Shrink = 0f,
                    },
                    new TextEl(progress ? Strings.Lyrics.Ai.Footer.Progress(track.LinesReady, track.LineCount) : Loc.Get(Rules.FooterKey(track)))
                    {
                        Size = TextSize, LineHeight = TextLineHeight, Color = color,
                        Grow = 1f, Shrink = 1f, MinWidth = 0f,
                        Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };

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
                                Direction = 1, Gap = Spacing.XS, MinWidth = 0f,
                                Padding = new Edges4(_sidePad, Spacing.S, _sidePad, Spacing.S),
                                Animate = s_fadeIn,
                                HitTestVisible = false,
                                Children = progress ? [line, BufferBar()] : [line],
                            },
                        ],
                    },
                ],
            };
        }

        /// <summary>Track, timed region, playhead tick (the video player's buffer bar). The timed width and the tick's
        /// translate are bound thunks: a chunk publish or a position report updates them without a render (a scoped
        /// relayout of one box / compositor only).</summary>
        Element BufferBar()
        {
            var accent = _ink.Accent;
            return new BoxEl
            {
                Key = "ai-lyrics-footer-bar", ZStack = true, Height = BandHeight, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
                OnBoundsChanged = _onBarBounds,
                Animate = s_fadeIn,
                Children =
                [
                    // the 3-DIP strip, centred in the band
                    new BoxEl
                    {
                        Direction = 1, Justify = FlexJustify.Center, MinWidth = 0f,
                        Children =
                        [
                            new BoxEl
                            {
                                ZStack = true, Height = BarHeight, MinWidth = 0f, Corners = CornerRadius4.All(BarRadius),
                                Fill = _ink.RingTrack,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Width = Prop.Of(_timedWidth), Height = BarHeight, Corners = CornerRadius4.All(BarRadius),
                                        Fill = accent with { A = accent.A * TimedAlpha },
                                        Layout = Design.Reduced ? null : s_timedEase,
                                    },
                                ],
                            },
                        ],
                    },
                    // the playhead: where the user is, behind the timed edge while the job keeps ahead
                    new BoxEl
                    {
                        Width = TickWidth, Height = BandHeight, Corners = CornerRadius4.All(TickRadius), Fill = _ink.Primary,
                        Transform = Prop.Of(_playhead),
                    },
                ],
            };
        }
    }
}
