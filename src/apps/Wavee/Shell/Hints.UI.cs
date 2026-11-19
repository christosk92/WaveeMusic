// ── Shell/Hints.UI.cs — the slot a surface mounts for its hint, the Home banner's slot, and the watcher that feeds signal facts ─────
//
// Role: UI (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 6.1-6.3). Decides nothing: `Hints.Pick` decides, the theme
// draws (`ViewKeys.Hint`, the Common stand-in until phase 6).
//
//   Slot(surface)   a `[CoreHost]` a surface mounts where its hint sits. At rest it is a zero box (no layout, no hit-testing); while a
//                   hint qualifies (or `hint show` forces one) it mounts the theme's hint view with `Hints.PropsFor`. It re-asks when
//                   `Hints.Epoch` moves, and once more when a timed gate it waits on opens (`UseTimeout`, armed only then). It reports
//                   the show (the one on-screen place, the session's hint, the day's show) and gives the place back when it unmounts.
//                   The lyrics footer's slot is also where "lyrics on screen" is known: it feeds that fact while it is active.
//   HomeBanner()    the InfoBar slot over the top of Home (hints #8 and #9): floats in the overlay layer under the runtime banner and
//                   never reflows the page; mounted only while Home is the page.
//   Watcher()       one zero box in the overlay layer whose effects read the signals some facts follow (an audiobook playing) and ask,
//                   once per launch, whether it was by hand right after signing in.
// Mounted today: the lyrics footer (`AiLyrics.Footer`, every lyrics surface but the mini player's face) and the Home banner. Every other
// surface mounts its slot with its placement (phase 6).

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Hints
{
    /// <summary>The hint slot of <paramref name="surface"/> (see the file header).</summary>
    public static Element Slot(string surface)
        => Embed.Comp(new HintSlotProps(surface), static () => new HintSlot()) with { Key = "hint-slot:" + surface };

    /// <summary>The Home banner's slot, mounted only while Home is the page.</summary>
    public static Element HomeBanner() => Embed.Comp(static () => new HomeBannerHost());

    /// <summary>The fact watcher (one, in the overlay layer).</summary>
    public static Element Watcher() => Embed.Comp(static () => new HintWatcher());

    sealed record HintSlotProps(string Surface);

    [CoreHost]
    sealed class HintSlot : Component
    {
        public override Element Render()
        {
            var p = UseProps<HintSlotProps>();
            var overlay = UseContext(Overlay.Service);
            var active = UseIsActive();
            var token = UseRef<object?>(null);
            token.Value ??= new object();
            object slot = token.Value;
            int epoch = Epoch.Value;
            bool lyrics = p.Surface == HintSurfaces.LyricsFooter;
            bool on = active.Value;

            long recheckMs = 0;
            HintDef? def = on ? Pick(p.Surface, slot, overlay.AnyOpen, out recheckMs) : null;
            var wait = UseRef(0L);
            wait.Value = def is null ? recheckMs : 0;

            // The lyrics surface is on screen while this slot is active: the "lyrics open" fact.
            UseEffect(() =>
            {
                if (!lyrics || !on) return null;
                HintFacts.LyricsOnScreen(true);
                return static () => HintFacts.LyricsOnScreen(false);
            }, DepKey.From(on));

            UseEffect(() =>
            {
                if (def is not null) Shown(def, slot);
                else Released(slot);
            }, (def?.Id, epoch));
            UseEffect(() => () => Released(slot), DepKey.Empty);

            // Ask again when the timed gate opens; a zero wait arms nothing that does anything.
            UseTimeout(() => { if (wait.Value > 0) Bump(); }, wait.Value, DepKey.From(epoch, (int)Math.Min(int.MaxValue, wait.Value)));

            return def is null ? Nothing : ViewSlots.View(ViewKeys.Hint, PropsFor(def, p.Surface));
        }
    }

    [CoreHost]
    sealed class HomeBannerHost : Component
    {
        public override Element Render()
            => Shell.Current.Value.Kind == Shell.RouteKind.Home ? Slot(HintSurfaces.HomeBanner) : Nothing;
    }

    sealed class HintWatcher : Component
    {
        public override Element Render()
        {
            var post = UsePost();
            UseEffect(() => HintFacts.AskSignInLaunch(post), DepKey.Empty);
            // An audiobook chapter playing (auto-tracked: re-runs when the playing flag or the playable changes, never per frame).
            UseEffect(() =>
            {
                bool playing = Playback.IsPlaying.Value;
                _ = Playback.CurrentId.Value;
                var current = Playback.Current.Peek();
                HintFacts.AudiobookPlaying(playing && current.Kind == EntityKind.Episode && new Episode(current.Slot).IsAudiobookChapter);
            });
            return Nothing;
        }
    }
}
