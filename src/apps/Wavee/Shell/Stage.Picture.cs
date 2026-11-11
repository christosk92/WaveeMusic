// ── Shell/Stage.Picture.cs ─────────────────────────────────────────────────────────────────────────────────────────
// Stage.Picture: the stage's Video mode (Cinema). Whether the structure draws it, whether the stage offers it, whether it is showing
// now, the hollow reservation the ONE video presenter follows, and the host of the stage's chrome OVER the picture.
//
// Role: UI
// Spec: the plan "video full screen", Part 3 (3.0, 3.1); the boards' rules note ("Classic · Cinema")
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// ONE FULL SCREEN. While a music video is on, the app's full screen (the stage) opens on the VIDEO: `Stage.Mode.Video` is its first tab
// and the picture fills the stage. The picture is NOT drawn inside the stage's subtree: the stage wrapper carries a scale and opacity
// entrance (`Stage.EnterTerminal`) and the video hole forbids any opacity, layout transition or scale on its ancestors (Video.UI.cs's
// rule 1). So the stage draws a hollow RESERVATION (the theme's box, `StageRootView.PictureRealized`) and the main window's one
// stay-mounted presenter (`Video.PipSurface`) FOLLOWS it with the engine's `FollowRect`, exactly as it follows the docked card's.
//
// THE CHROME OVER THE PICTURE. The hole erases whatever was painted under it, and the presenter lives in the video layer, painted after
// the stage. The stage's top bar, title block and transport must sit OVER the picture, so in Video mode the stage's own chrome stands
// down and this file's host mounts the structure's `Stage.PictureChrome` view INSIDE the video layer, right after the presenter
// (`Video.PipLayer`): painted after the hole, under the app's overlays (menus, toasts). It is the stage's chrome, on the stage's
// signals and verbs (the same `StageCtx`, idle machine and key map): there is one transport, the stage's; the presenter's own
// on-media strip stands down while the picture is staged.
//
// THE AUDIO FACE. Video mode outlives an audio-only track: while video stays ON for the main window (`VideoOn`: the placement is sticky across
// tracks) and the stage was in Video mode, a track without a video keeps the frame (the top bar with the Video tab disabled, Cinema's shelf or
// Theatre's panel, the transport) and the picture's place shows the cover and the lyrics (`StageCtx.AudioFace`). No presenter is mounted for such a
// track, so no hole is up: the face is an ordinary stage layer over the reservation, and the presenter that the next video track mounts covers
// it (it follows the same reservation, which never left).

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

using Wavee.Theming.Contracts;

using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Stage
{
    /// <summary>The stage's Video mode: the capability, the offer, the showing state, the reservation and the chrome's host.</summary>
    public static class Picture
    {
        /// <summary>The mounted stage is in <see cref="Mode.Video"/> (SurfaceCore writes it; false while no stage is mounted).</summary>
        static readonly Signal<bool> s_videoMode = new(false);

        /// <summary>The mounted stage's view (SurfaceCore publishes it at mount and clears it at unmount): what the chrome host draws with.</summary>
        static readonly Signal<StageRootView?> s_view = new(null);

        /// <summary>The live reservation node (UI thread; written at its realize, cleared when it unmounts). ROOT-LIFETIME state: the node
        /// belongs to the mounted root's tree.</summary>
        static readonly RootState<NodeHandle> s_reservationState = new("Stage.PictureReservation");

        /// <summary>The structure draws the stage's Video mode (<see cref="Theming.StagePolicy.VideoStage"/>). Peeked: for verbs.</summary>
        public static bool Supported => ShellPolicy.Now.Stage.VideoStage;

        /// <summary>The stage offers Video mode now (subscribes): the structure draws it and the music video is on in the MAIN window
        /// (docked or in the mini player: <see cref="Video.MainWindowHole.Stageable"/>). A video in the pop-out stays in its window.</summary>
        public static bool Offered()
            => ShellPolicy.Tracked.Stage.VideoStage && Video.MainWindowHole.Stageable(Video.PlacementCore.Resolve(Video.State.Surface.Value));

        /// <summary>Video is ON for the main window (subscribes): the structure draws Video mode and the user's placement is the docked card or the
        /// mini player, whether or not the playing track has a video (the placement is sticky across tracks: <c>PlacementCore.TurnOff</c>). What
        /// keeps Video mode up over an audio-only track (the audio face), so the next track with a video brings the picture back by itself.</summary>
        public static bool VideoOn()
            => ShellPolicy.Tracked.Stage.VideoStage && Video.State.Surface.Value.Requested is Video.SurfacePlacement.Docked or Video.SurfacePlacement.Floating;

        /// <summary>The mounted stage holds Video mode over an audio-only track (the audio face); peeked (the inspector's <c>status</c>).</summary>
        public static bool AudioFacePeek() => s_view.Peek()?.Stage.AudioFace.Peek() ?? false;

        /// <summary>The mounted stage's controls are shown: the idle chrome the stage itself publishes (<c>StageCtx.Chrome</c>, what its views read);
        /// peeked (the frame bench's note). True while no stage is mounted.</summary>
        public static bool ChromePeek() => s_view.Peek()?.Stage.Chrome.Peek() ?? true;

        /// <summary>The mounted stage shows the end card (a music video ended, the countdown runs); peeked (the inspector's <c>status</c>).</summary>
        public static bool EndCardPeek() => s_view.Peek()?.Stage.EndCard.Peek() ?? false;

        /// <summary><see cref="Offered"/> without subscribing: where a stage that opens now starts.</summary>
        public static bool OfferedNow()
            => Supported && Video.MainWindowHole.Stageable(Video.PlacementCore.Resolve(Video.State.Surface.Peek()));

        /// <summary>The picture is staged now (subscribes): the stage is up and in Video mode. What the presenter's visibility, its
        /// follow target and its strip read, and what mounts the chrome over the picture.</summary>
        public static bool Shown() => Shell.Ui.ImmersiveLyrics.Value && s_videoMode.Value;

        /// <summary>The stage's pane region is on screen (subscribes): always outside Video mode; in Video mode only Theatre's panel shows one (Cinema
        /// is the picture alone, the audio face reads its own column). What the parked lyrics pane's gate reads, so a Lyrics pane kept alive under the
        /// picture neither ticks nor follows.</summary>
        public static bool PaneOnScreen() => !s_videoMode.Value || s_view.Value is { } v && v.Stage.Theatre.Value && v.Stage.TheatrePane.Value == PaneKind.Lyrics;

        /// <summary><see cref="Shown"/> without subscribing (the engine's follow pass runs outside any reactive scope).</summary>
        public static bool ShownPeek() => Shell.Ui.ImmersiveLyrics.Peek() && s_videoMode.Peek();

        /// <summary>The node the presenter follows while the picture is staged (default: none realized).</summary>
        public static NodeHandle Reservation => s_reservationState.Value;

        /// <summary>The reservation's realize report (the theme puts it on the box it draws for the picture).</summary>
        internal static readonly Action<NodeHandle> Realized = static h => s_reservationState.Value = h;

        internal static void NoteMode(bool video) => s_videoMode.SetIfChanged(video);

        /// <summary>The mounted stage shows the picture in Theatre (a panel beside it), and which panel; peeked (the inspector's <c>status</c>).</summary>
        public static bool TheatrePeek() => s_view.Peek()?.Stage.Theatre.Peek() ?? false;

        public static PaneKind TheatrePanePeek() => s_view.Peek()?.Stage.TheatrePane.Peek() ?? PaneKind.None;

        internal static void Publish(StageRootView? view) => s_view.Value = view;

        /// <summary>Unpublish <paramref name="view"/> (only if it is still the published one: a successor may have published its own).</summary>
        internal static void Withdraw(StageRootView view)
        {
            if (!ReferenceEquals(s_view.Peek(), view)) return;
            s_view.Value = null;
            s_videoMode.Value = false;
            s_reservationState.Value = default;
        }

        // MOUNT POINT: `Video.PipLayer`, right after the presenter.
        /// <summary>The stage's chrome OVER the picture: mounted while the picture is staged, painted after the video hole.</summary>
        public static Element ChromeLayer() => Embed.Comp(static () => new ChromeHost());

        /// <summary>The host: a Show on <see cref="Shown"/> (and a published view), so nothing of the stage's chrome exists in the video
        /// layer outside Video mode.</summary>
        [CoreHost]
        sealed class ChromeHost : Component
        {
            // it comes and goes WITH the stage (the stage wrapper's own entrance and exit, `Stage.EnterTerminal` / `ExitTerminal`): it is not
            // inside the stage's subtree, and without its own it popped in and vanished at once while the stage under it faded
            public override Element Render()
                => Flow.Show(static () => Shown() && s_view.Value is not null,
                    Embed.Comp(static () => new ChromeView()) with { Enter = EnterTerminal, Exit = ExitTerminal });
        }

        /// <summary>The structure's <see cref="ViewKeys.StagePictureChrome"/> under the stage's context (its views read the same
        /// <see cref="StageContext"/> as the stage's own).</summary>
        [CoreHost]
        sealed class ChromeView : Component
        {
            StagePictureChromeProps? _props;

            public override Element Render()
            {
                // the host's Show mounts this only with a view published; while it plays its exit the view may already be gone: keep the last
                var view = s_view.Value;
                if (view is not null && (_props is null || !ReferenceEquals(_props.Root, view))) _props = new StagePictureChromeProps { Root = view };
                var props = _props!;
                return Ctx.Provide(StageContext, props.Root.Stage, ViewSlots.View(ViewKeys.StagePictureChrome, props));
            }
        }
    }
}
