using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Zero-size, always-mounted chrome (the <c>PlaybackRuntimeChrome</c> precedent — Features/Shell/
/// PlaybackRuntimeBanner.cs) that continues a POST-AUTH pending wizard: a first-run/reauth session that was still
/// pending when auth completed, OR a fresh <see cref="SetupEntryPoint.TermsRearm"/> wizard for a completed,
/// already-signed-in install whose terms acceptance just fell behind this build's version
/// (<c>SetupBootstrap.RearmForTerms</c> / <see cref="SetupGating.NeedsTermsRearm"/>) and so never showed
/// <c>SetupPreAuthRoot</c> at all.
///
/// <para>Checked ONCE at mount, via <see cref="SetupGating.IsPending"/>: <c>WaveeShell</c> (and this chrome with it)
/// only (re)mounts on an auth flip, so a mount-time check exactly answers "is there a pending wizard to pick up
/// right now" — mirroring <c>SidebarOnboardingChrome</c>'s own one-shot <c>opened</c>/<c>DepKey.Empty</c> shape.</para>
///
/// Mounted inside <c>WaveeShell</c>'s <c>shellWithOverlays</c> ZStack, next to <c>SidebarOnboardingChrome</c>, so
/// <c>UseContext(Overlay.Service)</c> resolves the real overlay host.
///
/// <para>Deliberately does NOT set <c>handle.ClosedAction</c> itself: <see cref="SetupDialog.Open"/> owns that field
/// EXCLUSIVELY for the marker/<see cref="SetupSession.Covering"/>/<see cref="SetupSession.Current"/> structural
/// cleanup (see its own doc comment) — <c>OverlayHandle.ClosedAction</c> is a single delegate field, so assigning it
/// again here would silently drop that cleanup. The "never open twice concurrently" guard therefore reads the held
/// handle's <c>IsOpen</c> rather than nulling the ref back on close.</para></summary>
sealed class SetupChrome : Component
{
    readonly IAppSettings _settings;
    public SetupChrome(IAppSettings settings) => _settings = settings;

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var post = UsePost();
        var handle = UseRef<OverlayHandle?>(null);
        var checkedPending = UseRef(false);

        // Fire-and-forget: this chrome is what opens the post-auth wizard, so warm the same three Lottie heroes
        // SetupPreAuthRoot warms for the pre-auth path — a session that never showed that root (a silent-resume
        // FirstRun, a TermsRearm on an already-signed-in install) still gets its heroes preloaded.
        UseEffect(() => { _ = WaveeLottie.Warm(); }, DepKey.Empty);

        void OpenBare(SetupSession session)
        {
            // TWO nested posts — the SidebarOnboardingChrome discipline: the first lands after this mount's commit,
            // the second after the frame that PAINTED the shell, so the user sees the app, THEN the wizard rises
            // over it (never a dialog over a still-blank frame).
            post(() => post(() =>
            {
                if (handle.Value is { IsOpen: true }) return;
                handle.Value = SetupDialog.Open(overlay, post, _settings, session, bare: false);
            }));
        }

        UseEffect(() =>
        {
            if (checkedPending.Value) return;
            checkedPending.Value = true;
            if (!SetupGating.IsPending(_settings)) return;
            if (handle.Value is { IsOpen: true }) return;

            // Reuse the SAME session the pre-auth mount was carrying (page/direction state survives the pre-auth →
            // post-auth remount, including whichever page the user had already clicked forward to) when there is
            // one — the remount then simply lands on whatever page (SignIn's "Is this you?", LocalPlayback, or
            // already closed) the carried session is on. No carried session means this shell mounted already
            // authenticated: a completed, signed-in install re-armed for new terms (TermsRearm) — the ONLY reason a
            // completed install is ever pending again — or a fast silent-resume that never showed SetupPreAuthRoot
            // at all (FirstRun).
            var session = SetupSession.Current;
            if (session is null)
            {
                bool completed = SetupGating.IsCompleted(_settings);
                session = new SetupSession(
                    completed ? SetupEntryPoint.TermsRearm : SetupEntryPoint.FirstRun,
                    alreadyAuthenticated: true);
                SetupSession.Current = session;
            }
            OpenBare(session);
        }, DepKey.Empty);

        return new BoxEl { HitTestVisible = false, Shrink = 0f };
    }
}
