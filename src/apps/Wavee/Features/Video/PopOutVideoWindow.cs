using System;
using FluentGpu.Controls;
using FluentGpu.Controls.Media;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using Wavee.SpotifyLive;

namespace Wavee.Features.Video;

/// <summary>
/// Root content of the detached, always-on-top pop-out video window (its own composited AppHost + swapchain + video
/// presenter). Reads the resolved <see cref="PopOutVideoSource"/> for the CONTENT IDENTITY and mounts a keyed
/// <see cref="PopOutVideoStage"/>, which PRESENTS the player owned by <c>FluentVideoMediaHost</c>. The OS window frame
/// handles move/resize/close; the host sets always-on-top.
///
/// <see cref="Source"/>/<see cref="Player"/> are FROZEN signals on purpose: app <c>Ctx.Provide</c> chains do NOT cross
/// the AppHost boundary (a detached window builds its own reconciler + ambient map), so the bridge's signals are handed
/// in directly. Reading them inside <c>Render</c> still subscribes normally — this window has its own render loop.
/// <see cref="Bridge"/>/<see cref="Settings"/> are frozen for the same boundary reason, but are stable INSTANCES rather
/// than signals — correct to freeze outright (no subscription needed; only the values reachable FROM them are live).
/// </summary>
sealed class PopOutVideoWindow : Component
{
    /// <summary>The resolved source (null = nothing yet — shows the letterbox background).</summary>
    public required IReadSignal<PopOutVideoSource?> Source { get; init; }

    /// <summary>The live video player owned by the backend media host (see <see cref="PlaybackBridge.VideoPlayer"/>). This
    /// window never builds a player — it binds to this one, so a placement flip re-binds instead of restarting from 0.</summary>
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }

    /// <summary>OPTIONAL — frozen because a detached window builds its own AppHost/reconciler and does NOT inherit the
    /// shell's <c>Ctx.Provide</c> chain, so <c>UseContext(PlaybackBridge.Slot)</c> would resolve to null in here even
    /// though the instance is perfectly stable (same reasoning as freezing <see cref="Player"/>). When present, the
    /// video's own transport More (⋯) menu gains the shared placement rows (<see cref="VideoPlacementMenu"/>) ahead of
    /// its own — see <see cref="PopOutVideoStage.Bridge"/>. When null (the caller has not threaded it through yet),
    /// the element's own More menu still works; it simply carries no placement rows.</summary>
    public PlaybackBridge? Bridge { get; init; }

    /// <summary>OPTIONAL, same freezing rationale as <see cref="Bridge"/> — needed only for the Always-on-top row.</summary>
    public IAppSettings? Settings { get; init; }

    /// <summary>Wrap the content in an overlay host so the transport's flyouts (speed, more, quality, CC, the volume
    /// slider) actually open. A detached window builds its OWN AppHost — its own reconciler and ambient context map —
    /// so the shell's OverlayHost never reaches it and <c>UseContext(Overlay.Service)</c> would resolve to
    /// <c>NullOverlayService</c>, making every one of those buttons a silent no-op.
    ///
    /// <para>The child MUST be a COMPONENT, never an inline element tree. <see cref="OverlayHost.Child"/> is
    /// <c>[MountOnceContent]</c> and <see cref="OverlayHost.Create"/> hands it to <c>Embed.Comp</c>, so it is built
    /// ONCE and frozen (the props-freeze contract — see docs/design/subsystems/component-props-contract.md). Passing
    /// the element tree directly froze it at the first render, when no player existed yet: the window then rendered an
    /// empty root FOREVER, so no <c>MediaPlayerElement</c> was ever mounted, nothing pumped the protected session, and
    /// the managed side sat at <c>Opening</c> until the start watchdog gave up — while the native log showed the video
    /// licensed, playing and feeding samples. A component re-renders itself, so its signal reads stay live.</para></summary>
    public override Element Render() =>
        OverlayHost.Create(Embed.Comp(() => new PopOutVideoContent { Source = Source, Player = Player, Bridge = Bridge, Settings = Settings }));
}

/// <summary>The bundle a HOST hands the shared <see cref="PopOutVideoStage"/> so the stage never has to guess which
/// surface it is inside. Three surfaces present the same stage — the fullscreen surface, the in-window mini player, and
/// the detached pop-out window — and they differ in exactly two ways, both of which live here rather than in a branch
/// inside the stage:
/// <list type="bullet">
/// <item><b>Who draws the transport.</b> <see cref="Identity"/> is this host's <see cref="TransportOwner"/>; the stage
/// compares it against the ONE derived <see cref="Owner"/> signal and passes
/// <c>MediaPlayerElement.SuppressTransport</c> when it loses. Never a second per-surface visibility flag — two
/// independent flags is exactly how the fullscreen transport and the global player bar ended up stacked.</item>
/// <item><b>What the fullscreen affordance does.</b> <see cref="FullscreenRequested"/> is wired by EVERY host, which is
/// what makes <c>MediaPlayerElement</c>'s own overlay fullscreen unreachable from Wavee — that path opened a modal
/// light-dismiss popup over the app's own fullscreen surface, exited on Alt-Tab (OverlayHost closes light-dismiss
/// entries on window blur), and called <c>WindowSetFullscreen(false)</c> unconditionally on the way out.</item>
/// </list></summary>
/// <param name="Identity">This host's transport identity.</param>
/// <param name="Owner">The one derived owner signal (<see cref="PlaybackBridge.TransportOwnerNow"/>). A frozen SIGNAL
/// instance, so it crosses the detached window's AppHost boundary intact and still re-renders the stage on change.</param>
/// <param name="FullscreenRequested">What the fullscreen glyph / ⋯ row / F11 / F / double-click must do here: ENTER
/// fullscreen from an inline surface, EXIT it from the fullscreen surface itself.</param>
readonly record struct VideoStageHost(
    TransportOwner Identity,
    IReadSignal<TransportOwner> Owner,
    Action FullscreenRequested);

/// <summary>The pop-out's actual content, as a COMPONENT so it re-renders when the source/player signals change (see
/// the note on <see cref="PopOutVideoWindow.Render"/> for why this cannot be an inline element tree). Both props are
/// FROZEN signal instances — freezing a <c>Signal</c> is correct; freezing the values read out of one is not.</summary>
sealed class PopOutVideoContent : Component
{
    /// <inheritdoc cref="PopOutVideoWindow.Source"/>
    public required IReadSignal<PopOutVideoSource?> Source { get; init; }
    /// <inheritdoc cref="PopOutVideoWindow.Player"/>
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }
    /// <inheritdoc cref="PopOutVideoWindow.Bridge"/>
    public PlaybackBridge? Bridge { get; init; }
    /// <inheritdoc cref="PopOutVideoWindow.Settings"/>
    public IAppSettings? Settings { get; init; }

    /// <summary>This window's stage host bundle — see <see cref="VideoStageHost"/>. Built here (not by the caller)
    /// because it needs the bridge, which crosses the AppHost boundary as a frozen instance.
    ///
    /// <para>The fullscreen affordance is a TOGGLE of <see cref="PlaybackBridge.DetachedFullscreen"/> — deliberately
    /// NOT <c>ShowVideoAt(SurfacePlacement.Fullscreen)</c>. That call resolves the placement away from
    /// <see cref="SurfacePlacement.Detached"/>, so <see cref="VideoPlacementHost"/> CLOSES this window and the shell
    /// mounts <c>VideoFullscreenSurface</c> in the MAIN window, which fullscreens the main window — on the MAIN
    /// window's monitor. Since a pop-out is routinely dragged to a second display precisely so it can be watched there,
    /// the fullscreen glyph would move the picture to the wrong screen. Toggling the bit instead keeps the resolved
    /// placement at Detached and fullscreens THIS window in place, on the display it is already on (the owner forwards
    /// it to this window's own <see cref="IDetachedVideoWindow.SetFullscreen"/>).</para></summary>
    VideoStageHost? Host => Bridge is { } b
        ? new VideoStageHost(TransportOwner.PopOut, b.TransportOwnerNow,
            () => b.DetachedFullscreen.Value = !b.DetachedFullscreen.Peek())
        : null;

    public override Element Render()
    {
        // Size the root to THIS window's viewport (the AppHost does NOT auto-stretch a scene root — a bare Grow=1 hugs to
        // 0×0; WaveeShell fills the same way).
        var vp = UseContextSignal(Viewport.Size);
        var src = Source.Value;                 // subscribe → remount the stage on a source change
        var binding = Player.Value;             // subscribe → repaint the plate when the player arrives
        // Mount whenever a player exists — a brief source null must not unmount the only MF pump.
        bool live = VideoSurfaceMount.ShouldMountPlayerStage(binding.Player is not null);
        // THIS window's own fullscreen mode (never SurfacePlacement.Fullscreen — see PlaybackBridge.DetachedFullscreen).
        // Read as .Value so this content SUBSCRIBES: it is what makes the transport's glyph, the ⋯ row label
        // ("Exit full screen") and MediaPlayerElement's `case Keys.Escape when PresentingFullscreen` reflect the REAL
        // state instead of permanently reading "not fullscreen" and offering to enter a mode we are already in.
        bool hostFullscreen = Bridge is { } fsBridge && fsBridge.DetachedFullscreen.Value;
        string stageKey = src?.Key ?? ("gen:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new BoxEl
        {
            Direction = 1,
            Width = Prop.Of(() => vp.Value.Width),
            Height = Prop.Of(() => vp.Value.Height),
            // ALWAYS opaque — including while live. The video composites as a passive hole punched by a DESCENDANT
            // (MediaPlayerElement's VideoHole node), and that punch is a DestOut erase: it zeroes the UI back buffer
            // over the video rect, which removes this fill there just as it removes the element's own letterbox fill.
            // So an opaque root does NOT cause the black-video bug — only something painting AFTER the hole, i.e. a
            // later sibling or higher z, can do that.
            // It used to be transparent while live, on the assumption the element would cover everything around the
            // video. It does not: the element draws a ROUNDED, BORDERED frame, so its corners and any slack between
            // frame and window were never painted by anyone — and in a composited window "not painted" means the
            // DESKTOP shows through. That was the wallpaper-coloured strip under the titlebar.
            Fill = Tok.MediaLetterbox,
            Children = live
                // The stage's props FREEZE AT MOUNT (component-props-contract.md), and IsHostFullscreen is one of them,
                // so the fullscreen bit has to be part of the KEY — exactly the way PopOutVideoStage folds `suppress`
                // into its own element key as ":t0"/":t1". Without it the stage would keep whatever value it was born
                // with and the toggle would change nothing on screen.
                ? [new BoxEl { Grow = 1, Children = [Embed.Comp(() => new PopOutVideoStage { Source = src, Player = Player, Bridge = Bridge, Settings = Settings, Host = Host, IsHostFullscreen = hostFullscreen }) with { Key = "stage:" + stageKey + (hostFullscreen ? ":f1" : ":f0") }] }]
                : Array.Empty<Element>(),
        };
    }
}

/// <summary>One video SURFACE for a FROZEN source identity (props freeze at mount; the parent remounts this on a source
/// change). It does NOT own a player: the engine <c>MediaPlayer</c> — clear MF backend or clear+DRM (native PlayReady CDM) —
/// is built and owned by <c>FluentVideoMediaHost</c>, and this surface only binds a <see cref="MediaPlayerElement"/> to it.
/// That inversion is what fixes both M0 defects: the video's soundtrack is the ONE current media (so the song stops), and a
/// placement move re-binds a presenter instead of rebuilding a player (so playback does not restart from 0).
///
/// <c>MediaPlayerElement.Player</c> is a frozen-at-mount prop, so the element is KEYED on the binding generation: when the
/// host rebuilds its player the element remounts against the new instance. Exactly ONE mounted surface may pump a given
/// player (the MF session only advances while a mounted element pumps it); the single-placement state guarantees that.</summary>
sealed class PopOutVideoStage : Component
{
    /// <summary>Resolved source identity (may be briefly null while an override re-resolves — the stage stays mounted
    /// so MF keeps pumping; the parent overlays Loading when this is null).</summary>
    public PopOutVideoSource? Source { get; init; }
    public required IReadSignal<PlaybackBridge.VideoPlayerBinding> Player { get; init; }

    /// <summary>OPTIONAL — when present, wires <see cref="MediaPlayerElement.MoreMenuItems"/> with the shared
    /// <see cref="VideoPlacementMenu"/> rows (Fullscreen omitted: the element has its own Fullscreen row, and with
    /// <see cref="VideoStageHost.FullscreenRequested"/> wired that row delegates to the app — so including ours would
    /// duplicate it). All Wavee placement hosts thread this instance through; null remains a safe standalone fallback
    /// with only the element's playback rows.</summary>
    public PlaybackBridge? Bridge { get; init; }
    /// <summary>OPTIONAL, paired with <see cref="Bridge"/> — needed only for the Always-on-top row.</summary>
    public IAppSettings? Settings { get; init; }

    /// <summary>Which surface this stage is inside — see <see cref="VideoStageHost"/>. Decides whether this stage draws
    /// the transport at all and what its fullscreen affordance does. Null = the standalone fallback (the element keeps
    /// its own transport AND its own overlay fullscreen); every Wavee surface passes one.</summary>
    public VideoStageHost? Host { get; init; }

    /// <summary>Whether the SURFACE this stage sits in is already presenting fullscreen — the app's own fullscreen
    /// surface (always true there), or the detached pop-out window while it is borderless-fullscreen on its own monitor.
    /// Passed straight through to <see cref="MediaPlayerElement.IsHostFullscreen"/>, which ORs it into the element's
    /// notion of "presenting fullscreen": that one value drives the transport glyph, the ⋯ row label
    /// ("Exit full screen") and the Escape handler's <c>PresentingFullscreen</c> arm, so a host that forgets to set it
    /// renders an "enter fullscreen" affordance while already fullscreen and swallows Escape. FROZEN at mount like every
    /// prop here — a host whose value CHANGES must fold it into this stage's key (see
    /// <see cref="PopOutVideoContent.Render"/>).</summary>
    public bool IsHostFullscreen { get; init; }

    public override Element Render()
    {
        var binding = Player.Value;   // subscribe → re-bind when the host rebuilds/clears its player
        // The player vanished (host stopped) — render nothing; the owning surface unmounts this on the same pass.
        if (binding.Player is not { } player) return new BoxEl { Grow = 1f, MinHeight = 0f };
        var bridge = Bridge;
        var settings = Settings;
        // ONE transport, and the decision is the ONE derived owner value — never a per-surface bool. Reading .Value
        // subscribes, so an ownership change re-renders this stage; the key below then remounts the element, which is
        // required because MediaPlayerElement's props FREEZE AT MOUNT (component-props-contract.md). In practice a
        // surface's ownership is constant for its whole mount (the surfaces themselves mount/unmount with the
        // placement), so the remount arm is a safety net rather than a routine path.
        bool suppress = Host is { } h && h.Owner.Value != h.Identity;
        var fullscreen = Host?.FullscreenRequested;
        return Embed.Comp(() => new MediaPlayerElement
            {
                Player = player, Stretch = MediaStretch.Uniform,
                PlayRequested = bridge is null ? null : () => _ = bridge.Player.ResumeAsync(),
                PauseRequested = bridge is null ? null : () => _ = bridge.Player.PauseAsync(),
                // The seek MODE travels with the target: a scrub-in-flight asks for Keyframe (cheap, snappy) and the
                // commit asks for Accurate. Dropping it here forced every seek through the accurate path.
                SeekRequested = bridge is null ? null : (target, mode) => _ = bridge.Player.SeekAsync((long)target.TotalMilliseconds, mode),
                AspectMode = bridge?.VideoAspectPolicy,
                CustomAspectRatio = bridge?.VideoCustomAspectRatio,
                AspectModeChanged = bridge is null ? null : bridge.SetVideoAspect,
                MoreMenuItems = bridge is null ? null : () => VideoPlacementMenu.Items(bridge, settings, includeFullscreen: false),
                SuppressTransport = suppress,
                // Wired on EVERY Wavee surface: MediaPlayerElement.ToggleFullscreen prefers this over its own overlay
                // path, so the engine's modal light-dismiss fullscreen (and with it "Alt-Tab leaves fullscreen" and the
                // unconditional WindowSetFullscreen(false) on close) is unreachable from the app.
                FullscreenRequested = fullscreen,
                // "This surface is ALREADY fullscreen" — the element ORs it into PresentingFullscreen, which is what
                // makes the glyph, the ⋯ label and Escape agree with reality instead of always offering to enter.
                IsHostFullscreen = IsHostFullscreen,
            })
            with
            {
                // The key carries EVERY frozen prop of the element that can change under a live stage — the generation,
                // the transport-ownership bit, and the host-fullscreen bit — because that is the only mechanism that
                // remounts a component whose props froze at mount.
                Key = "player:" + binding.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + (suppress ? ":t0" : ":t1") + (IsHostFullscreen ? ":f1" : ":f0"),
            };
    }
}
