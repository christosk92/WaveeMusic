using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

public static partial class Shell
{
    // The podcast bar's picture (the ±10 s step buttons, the speed button and its panel) is the structure's since T6a
    // (`Themes\Classic\Structure\Views\Player\ClassicPlayerBarPodcast.cs`). What stays here is what the arithmetic and the
    // behaviour share with every theme: the speed button's width (it is part of the podcast transport's width budget:
    // `PlayerBarMetrics.EpisodeSpeedButtonW`), the rate as text, and the keyboard ladder's commit clock.

    public const float EpisodeSpeedButtonWidth = 48f;

    // The ±10 s step buttons call `IPlayerVerbs.SeekBy` (ONE seek straight from the reported position; no accumulator — the reducer
    // paints the drop point at the post, so a second press reads it), and the arrow keys are the scrub ladder
    // (`ScrubController.KeyStep`, the bar-wide `KeyboardScrub`; D4, V-PA22): the state, the grace and the commit all live in core.

    /// <summary>The ladder's commit clock: a zero-size component every seek rail mounts, so the interval runs where a rail does (bar, stage,
    /// on-media) and only while a ladder is live — the rail itself never re-renders for it. Several rails ticking is harmless: the first
    /// to see the quiet key commits, the rest find the ladder already committed.</summary>
    sealed class BarKeyLadderTick : Component
    {
        readonly Action _tick = KeyboardScrub.Shared.Tick;

        public override Element Render()
        {
            UseInterval(_tick, KeyboardScrub.TickMs, enabled: KeyboardScrub.Shared.Active.Value);
            // Nobody left to commit a live ladder (the only rail unmounted inside the grace): drop it, or the labels would hold its target forever.
            UseEffect(() => { KeyboardScrub.Shared.TickMounted(); return () => KeyboardScrub.Shared.TickUnmounted(); }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    static string EpisodeRateText(float rate) => rate.ToString("0.0#", CultureInfo.CurrentCulture) + "\u00D7";
}
