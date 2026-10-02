using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    public const float EpisodeSpeedButtonWidth = 48f;

    static bool SeekEnabledNow() => SeekRail.Enabled(!Playback.CurrentId.Peek().IsEmpty, Playback.Error.Peek(),
        Playback.PhaseSignal.Peek(), Playback.CanSeek.Peek());

    /// <summary>The ±10 s step buttons (the podcast transport): ONE seek straight from the reported position (<see cref="SeekRail.StepTargetMs"/>).
    /// There is no accumulator any more — the reducer paints the drop point at the post, so a second press reads it; and no keyboard
    /// path through here either: the arrow keys are the scrub ladder (<see cref="BarKeyStep"/>).</summary>
    internal static void BarSeekBy(int deltaMs)
    {
        if (!SeekEnabledNow()) return;
        var live = Playback.Live.Peek();
        long position = live.HasWindow ? live.PositionMs : Playback.Snap().Position(Playback.FrameNowMs());
        long target = SeekRail.StepTargetMs(position, deltaMs, Playback.DurationMs.Peek(), in live);
        Playback.SeekTo((int)Math.Clamp(target, 0L, int.MaxValue));
    }

    // ── the keyboard scrub ladder (D4, V-PA22) ────────────────────────────────────────────────────────────────────────
    // Left / Right step a VISUAL target — 5 s, then 15 s, then 30 s while the key is held, Shift = 1 s — and one seek commits once the
    // key has been quiet for `KeyboardScrubLadder.GraceMs` (there is no key-up). The ladder is bar-wide state (the bar's key handler and the
    // fullscreen stage's call the same `BarKeyStep`), the commit tick is mounted by every seek rail (`BarKeyLadderTick`, so it runs wherever a
    // rail does — bar, stage, on-media), and every rail and time label paints the target through `s_keyPreviewMs` until the seek lands.

    static KeyboardScrubLadder s_ladder;                  // UI thread only
    static long s_keyBaseMs;                              // the ladder works in [0, span]; a commit seeks to BaseMs + target (a DVR window's start)
    static EntityId s_keyTrack;                           // the row the ladder began on: a load that replaced it drops the ladder
    static long s_keyCommitAtMs;                          // the commit's frame stamp; 0 = still stepping
    static uint s_keyCommitGen;                           // the SeekGen sampled BEFORE the commit's post
    static int s_keyTickMounts;                           // how many `BarKeyLadderTick`s are mounted: the last one leaving drops a live ladder

    /// <summary>The target the keyboard ladder paints, in ms (absolute, the rail's own coordinates); −1 = no ladder live. Read by the seek rails
    /// (the thumb) and the time labels. It stays set after the commit until the seek LANDS (or <see cref="SeekRail.CommitHoldMs"/> passes), so
    /// the thumb and the label never step back to the pre-seek position in the frame between the post and its drain.</summary>
    internal static readonly Signal<long> s_keyPreviewMs = new(-1L);
    /// <summary>The ladder has something to tick for: stepping, or committed and waiting for its seek to land. Gates the 50 ms interval.</summary>
    static readonly Signal<bool> s_ladderActive = new(false);

    /// <summary>One Left / Right press (or OS repeat): step the ladder. Internal so the stage's key map shares it (X5).</summary>
    internal static void BarKeyStep(int direction, bool fine)
    {
        if (!SeekEnabledNow()) { KeyLadderReset(); return; }
        var live = Playback.Live.Peek();
        long now = Playback.FrameNowMs();
        long position = live.HasWindow ? live.PositionMs : Playback.Snap().Position(now);
        var (baseMs, spanMs, fromMs) = SeekRail.KeySpace(in live, Playback.DurationMs.Peek(), position);
        if (spanMs <= 0) { KeyLadderReset(); return; }    // no seekable span (live without a window): a step must never commit a seek to 0
        s_keyCommitAtMs = 0;                              // a new press supersedes a commit still waiting to land
        if (!s_ladder.Active) s_keyTrack = Playback.CurrentId.Peek();
        int target = s_ladder.Step(direction, fine, fromMs, spanMs, now);
        s_keyBaseMs = baseMs;
        s_keyPreviewMs.Value = baseMs + target;
        s_ladderActive.Value = true;
    }

    static void KeyLadderReset()
    {
        s_ladder = default;
        s_keyCommitAtMs = 0L;
        if (s_keyPreviewMs.Peek() >= 0L) s_keyPreviewMs.Value = -1L;
        if (s_ladderActive.Peek()) s_ladderActive.Value = false;
    }

    /// <summary>The 50 ms tick (mounted by <see cref="BarKeyLadderTick"/> while a ladder is live): commit ONCE when the key went quiet, then
    /// hold the painted target until the seek lands. The seek generation is sampled BEFORE the post (V-PA12): the drain that bumps it runs
    /// later — or inline under a synchronous marshaller.</summary>
    static void KeyLadderTick()
    {
        long now = Playback.FrameNowMs();
        if (!s_keyTrack.Equals(Playback.CurrentId.Peek()) || !SeekEnabledNow()) { KeyLadderReset(); return; }   // the row changed under it
        if (s_keyCommitAtMs == 0L)
        {
            int target = s_ladder.Tick(now);
            if (target < 0) return;                       // still stepping
            s_keyCommitGen = Playback.SeekGen.Peek();     // BEFORE the post
            s_keyCommitAtMs = Math.Max(1L, now);
            Playback.SeekTo((int)Math.Clamp(s_keyBaseMs + target, 0L, int.MaxValue));
            return;
        }
        if (SeekRail.HoldsDrop(s_keyCommitAtMs, now) && !Playback.SeekLandedAfter(s_keyCommitGen, Playback.LastSeekLandedGen.Peek())) return;
        KeyLadderReset();                                 // landed (or the hold timed out): the model paints the position again
    }

    /// <summary>The ladder's commit clock: a zero-size component every seek rail mounts, so the interval runs where a rail does (bar, stage,
    /// on-media) and only while a ladder is live — the rail itself never re-renders for it. Several rails ticking is harmless: the first
    /// to see the quiet key commits, the rest find the ladder already committed.</summary>
    sealed class BarKeyLadderTick : Component
    {
        readonly Action _tick = KeyLadderTick;

        public override Element Render()
        {
            UseInterval(_tick, 50f, enabled: s_ladderActive.Value);
            // Nobody left to commit a live ladder (the only rail unmounted inside the grace): drop it, or the labels would hold its target forever.
            UseEffect(() => { s_keyTickMounts++; return () => { if (--s_keyTickMounts == 0) KeyLadderReset(); }; }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    static Element BarSeekStepButton(int deltaMs, float box)
    {
        bool enabled = SeekRail.Enabled(!Playback.CurrentId.Value.IsEmpty, Playback.Error.Value,
            Playback.PhaseSignal.Value, Playback.CanSeek.Value);
        return ToolTip.Wrap(new BoxEl
        {
            Width = box, Height = box, Shrink = 0f,
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
            IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
            OnClick = () => BarSeekBy(deltaMs),
            Children = [new TextEl(deltaMs < 0 ? "\u221210" : "+10")
                { Size = 12f, Weight = 600, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled, HoverColor = Tok.TextPrimary }],
        }, Loc.Get(deltaMs < 0 ? "player.seekBack10" : "player.seekForward10"));
    }

    static string EpisodeRateText(float rate) => rate.ToString("0.0#", CultureInfo.CurrentCulture) + "\u00D7";

    sealed class BarEpisodeSpeed : Component
    {
        public override Element Render()
        {
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var overlay = UseContext(Overlay.Service);
            UseEffect(() => () => handle.Value?.Close(), DepKey.Empty);
            void Toggle()
            {
                if (handle.Value is { IsOpen: true } open) { open.Close(); return; }
                handle.Value = overlay.Open(() => anchor.Value,
                    static () => Embed.Comp(static () => new BarEpisodeSpeedPanel()),
                    FlyoutPlacement.TopEdgeAlignedLeft,
                    new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss));
                handle.Value.ClosedAction = () => handle.Value = null;
            }
            return ToolTip.Wrap(new BoxEl
            {
                Width = EpisodeSpeedButtonWidth, Height = PlayerBarLayout.MinButtonBox, Shrink = 0f,
                Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.ControlAll, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand,
                OnRealized = node => anchor.Value = node, OnClick = Toggle,
                Children = [new TextEl(EpisodeRateText(Playback.EpisodeSpeed.Value)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary }],
            }, Loc.Get("podcast.reader.speed"));
        }
    }

    sealed class BarEpisodeSpeedPanel : Component
    {
        static readonly Slider.SliderOptions Options = new()
        {
            Min = .5f, Max = 3f, Step = .05f, SmallChange = .05f, LargeChange = .25f,
            ThumbToolTipValueConverter = EpisodeRateText,
        };
        public override Element Render()
        {
            var value = UseFloatSignal(Playback.EpisodeSpeed.Peek());
            UseSignalEffect(() => value.SetIfChanged(Playback.EpisodeSpeed.Value));
            Element Preset(float rate) => FluentGpu.Controls.Button.Create(EpisodeRateText(rate),
                () => Playback.SetEpisodeSpeed(rate), ButtonAppearance.Subtle, ControlSize.Small);
            return new BoxEl
            {
                Width = 256f, Direction = 1, Gap = Spacing.S, Padding = Edges4.All(Spacing.M),
                Children =
                [
                    FluentGpu.Dsl.Ui.Text(Loc.Get("podcast.reader.speed")),
                    Slider.Create(value, static rate => Playback.SetEpisodeSpeed(rate), Options, length: 224f),
                    new BoxEl { Direction = 0, Gap = Spacing.XXS, Justify = FlexJustify.SpaceBetween,
                        Children = [Preset(1f), Preset(1.5f), Preset(2f), Preset(3f)] },
                ],
            };
        }
    }
}
