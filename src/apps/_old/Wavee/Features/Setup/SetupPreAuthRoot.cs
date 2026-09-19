using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>The setup wizard's PRE-AUTH mount — <c>WaveeApp</c>'s login gate mounts this for every unauthenticated
/// launch. It is the whole window until sign-in completes: the wizard is Wavee's only sign-in surface, so there is no
/// second takeover to fall back to and nothing here may be dismissed into an empty window
/// (<see cref="SetupGating.CanDismiss"/>).
///
/// <para><b>Why this exists (do not "simplify" it away).</b> <see cref="OverlayHost.Create"/> is called in exactly
/// ONE other place in this app, <c>WaveeShell.cs</c> — and <c>WaveeShell</c> only mounts once authed, so in the
/// logged-out branch <c>UseContext(Overlay.Service)</c> resolves to <c>NullOverlayService</c>: there is no overlay
/// service at all below the gate while logged out. Hoisting the host UP into <c>WaveeApp</c> itself is rejected:
/// <see cref="OverlayHost.Child"/> is <c>[MountOnceContent]</c> (see the warning on
/// <c>Features/Video/PopOutVideoWindow.cs</c>'s <c>PopOutVideoWindow.Render</c>), so handing it the whole
/// auth-gated tree would freeze that tree at first render — before there is anything authenticated to show — and
/// the auth flip would never be seen.</para>
///
/// <para>So this root is its own tiny app shell: a minimal <see cref="TitleBar"/> (<c>Program.cs</c> asks for
/// <c>CustomFrame = true</c> globally, so nothing else will ever draw the OS min/max/close affordance a logged-out
/// window needs), a transparent Mica body (paint nothing, so the live DWM Mica reads straight through), and its OWN
/// <see cref="OverlayHost.Create"/> wrapping a component (never a raw element — the same <c>[MountOnceContent]</c>
/// contract <c>PopOutVideoWindow</c> already has to honor) that opens the wizard once, bare.</para></summary>
sealed class SetupPreAuthRoot : Component
{
    readonly SetupSession _session;
    readonly IAppSettings _settings;
    public SetupPreAuthRoot(SetupSession session, IAppSettings settings) { _session = session; _settings = settings; }

    public override Element Render()
    {
        // Fire-and-forget: warms all three Lottie heroes off the UI thread before the wizard's first page ever
        // mounts one, so WaveeLottie.For's first real call is never the one paying the parse/compile cost.
        UseEffect(() => { _ = WaveeLottie.Warm(); }, DepKey.Empty);

        var titleBar = TitleBar.Create(new TitleBarOptions { ShowCaptionButtons = true });

        // Every entry point — FirstRun, Reauth, TermsRearm — opens the wizard immediately through the one-post
        // auto-open below. There is no pre-dialog "Welcome to Wavee · Start setup" splash any more: the dialog IS
        // the first thing a cold install shows (its Terms page carries the welcome copy), so nothing sits under the
        // plate to leak through it.
        Element mica = Embed.Comp(() => new SetupPreAuthOpener(_session, _settings));

        var body = new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children =
            [
                titleBar,
                // The transparent Mica body: paints nothing itself, so the window's live DWM Mica material (Program.cs)
                // reads straight through underneath the wizard dialog.
                new BoxEl { Grow = 1f, Fill = ColorF.Transparent, Children = [mica] },
            ],
        };
        return OverlayHost.Create(body);
    }
}

/// <summary>Zero-size opener, mounted ONCE inside <see cref="SetupPreAuthRoot"/>'s own overlay host — a component,
/// per the <c>[MountOnceContent]</c> contract <see cref="OverlayHost.Create"/> demands of its child (a raw element
/// there would freeze at first render, exactly the bug <c>PopOutVideoWindow.Render</c>'s remarks describe). Opens the
/// wizard BARE (the engine's own popup scrim paints instead — there is no live shell behind it to cover) exactly
/// once per mount, after this root's first painted frame. <see cref="SetupDialog.Open"/> owns every close-path
/// cleanup (the marker, resetting <c>Covering</c> to <c>SetupCover.None</c>, <c>SetupSession.Current</c>) — this
/// opener only guards against opening a second dialog concurrently.</summary>
sealed class SetupPreAuthOpener : Component
{
    readonly SetupSession _session;
    readonly IAppSettings _settings;
    public SetupPreAuthOpener(SetupSession session, IAppSettings settings) { _session = session; _settings = settings; }

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var post = UsePost();
        var handle = UseRef<OverlayHandle?>(null);

        UseEffect(() =>
        {
            if (handle.Value is { IsOpen: true }) return;
            // ONE post, not the SidebarOnboardingChrome double-post. That discipline exists so a one-time dialog
            // rises over an already-painted SHELL — but this is the PRE-AUTH root: there is no shell behind it, only
            // a titlebar over bare Mica. Deferring an extra frame bought nothing and cost two real things: a visible
            // empty-window flash on launch, and an un-screenshottable first frame (the `--screenshot` harness
            // captured the window before the dialog existed, so every pre-auth capture came out black).
            post(() =>
            {
                if (handle.Value is { IsOpen: true }) return;
                handle.Value = SetupDialog.Open(overlay, post, _settings, _session, bare: true);
            });
        }, DepKey.Empty);

        return new BoxEl { HitTestVisible = false, Shrink = 0f };
    }
}
