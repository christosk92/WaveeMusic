// ── Shell/Hints.UI.cs — the slot a surface mounts for its hint, the Home banner's slot, and the watcher that feeds signal facts ─────
//
// Role: UI (settings rework phase 3, handoff/settings-2026-11-18/DESIGN.md 6.1-6.3). Decides nothing: `Hints.Pick` decides, the theme
// draws (`ViewKeys.Hint`, each theme's own template since phase 6).
//
//   Slot(surface)   the slot of a hint that sits IN PLACE. A `[CoreHost]`: at rest a zero box (no layout, no hit-testing); while a hint
//                   qualifies (or `hint show` forces one) it mounts the theme's hint view with `Hints.PropsFor`. It re-asks when
//                   `Hints.Epoch` moves, and once more when a timed gate it waits on opens (`UseTimeout`, armed only then). It reports
//                   the show (the one on-screen place, the session's hint, the day's show) and gives the place back when it unmounts.
//                   Its box eases its height through real layout, so a hint that arrives after its surface mounted moves nothing at
//                   once. The lyrics footer's slot is also where "lyrics on screen" is known: it feeds that fact while it is active.
//   Pointer(surface, target)  the slot of a hint that POINTS at a control the surface draws: a zero box (no room), the props carrying
//                   the target so the theme opens its TeachingTip or anchored line there (an overlay: nothing in the page moves).
//   HomeBanner()    the InfoBar slot over the top of Home (hints #8 and #9): floats in the overlay layer under the runtime banner and
//                   never reflows the page; mounted only while Home is the page.
//   Watcher()       one zero box in the overlay layer whose effects read the signals some facts follow (an audiobook playing) and ask,
//                   once per launch, whether it was by hand right after signing in.
// Mounted: the lyrics footer (`AiLyrics.Footer`, every lyrics surface but the mini player's face) and the Home banner here; the devices
// flyout, the bell panel's header, the queue panel, the player bar's play button and volume, the page head or back button and WinUI's title
// bar by each theme's own surface (phase 6).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Hints
{
    /// <summary>The hint slot of <paramref name="surface"/> where the hint sits IN PLACE (the lyrics footer, the devices flyout, the bell's
    /// header): a box whose height runs through real layout (<see cref="SizeMode.Reflow"/>), so a hint that comes after the surface mounted
    /// eases its neighbours aside instead of jumping them, while a surface that opens with its hint draws it at once (see the file header).
    /// <paramref name="close"/> closes the flyout the slot sits in (the devices picker's own close), so the hint's door to Settings never
    /// leaves the flyout open over the page.</summary>
    public static Element Slot(string surface, Action? close = null) => new BoxEl
    {
        Key = "hint-place:" + surface, Direction = 1, MinWidth = 0f, Shrink = 0f, ClipToBounds = true,
        Layout = Design.Reduced ? null : s_reveal,
        Children = [Core(surface, null, close)],
    };

    /// <summary>The hint slot of <paramref name="surface"/> where the hint POINTS at a control the surface draws (the play button, the volume
    /// slider, the queue's Crossfade switch, the back button, the title bar): a zero box that takes no room (place it where a gap does not count
    /// it: a ZStack layer, a gapless column, after the keyed children so it shifts no index), the theme opening its TeachingTip or anchored line
    /// at <paramref name="target"/>, a stable delegate the surface captures with <c>OnRealized</c>. (Not <c>Visible = false</c>: a hidden subtree
    /// runs no effects, so the tip would never open.)</summary>
    public static Element Pointer(string surface, Func<NodeHandle> target)
        => new BoxEl
        {
            Key = "hint-pointer:" + surface, Width = 0f, Height = 0f, Shrink = 0f, Grow = 0f, ClipToBounds = true, HitTestVisible = false,
            Children = [Core(surface, target, null)],
        };

    static Element Core(string surface, Func<NodeHandle>? target, Action? close)
        => Embed.Comp(new HintSlotProps(surface, target, close), static () => new HintSlot()) with { Key = "hint-slot:" + surface };

    /// <summary>An in-place slot's height eases through real layout (the lyrics footer's own clip box grows the same way).</summary>
    static readonly LayoutTransition s_reveal = new(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    /// <summary>The Home banner's slot, mounted only while Home is the page.</summary>
    public static Element HomeBanner() => Embed.Comp(static () => new HomeBannerHost());

    /// <summary>The fact watcher (one, in the overlay layer).</summary>
    public static Element Watcher() => Embed.Comp(static () => new HintWatcher());

    sealed record HintSlotProps(string Surface, Func<NodeHandle>? Target, Action? Close);

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

            // A confirmation (the inline action ran here) says its word and goes: set aside after its time on screen.
            var doneDef = UseRef<HintDef?>(null);
            doneDef.Value = def is not null && IsDone(def) ? def : null;
            UseTimeout(() => { if (doneDef.Value is { } d) SetAside(d); }, doneDef.Value is null ? 0 : HintRules.ConfirmationMs,
                DepKey.From(doneDef.Value is { } shown ? IndexOf(shown.Id) + 1 : 0));

            return def is null ? Nothing : ViewSlots.View(ViewKeys.Hint, PropsFor(def, p.Surface, p.Target, p.Close));
        }
    }

    [CoreHost]
    sealed class HomeBannerHost : Component
    {
        public override Element Render()
            => Shell.Current.Value.Kind == Shell.RouteKind.Home ? Core(HintSurfaces.HomeBanner, null, null) : Nothing;
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
