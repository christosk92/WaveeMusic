using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Zero-size, always-mounted chrome (the <c>PlaybackRuntimeChrome</c> precedent — Features/Shell/
/// PlaybackRuntimeBanner.cs) that owns BOTH ways the POST-AUTH setup wizard can come up:
/// <list type="bullet">
/// <item>a manual re-run — <see cref="SetupSession.OpenRequest"/>, bumped by Settings' "Run setup again" row
/// (<c>SettingsPage.General.cs</c>) — always <see cref="SetupSession.EntryPoint.Rerun"/>, built HERE (not at the
/// settings row) since this component owns the session's lifetime;</item>
/// <item>continuing a first-run wizard that was still pending when auth completed — <see cref="SetupGating.IsPending"/>,
/// checked ONCE at mount. <c>WaveeShell</c> (and this chrome with it) only (re)mounts on an auth flip, so a
/// mount-time check exactly answers "did the wizard start pre-auth, in <c>SetupPreAuthRoot</c>, and not finish
/// yet" — mirroring <c>SidebarOnboardingChrome</c>'s own one-shot <c>opened</c>/<c>DepKey.Empty</c> shape.</item>
/// </list>
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
        int req = SetupSession.OpenRequest.Value;   // subscribe: a Bump() must re-run this render
        var lastReq = UseRef(req);
        var handle = UseRef<OverlayHandle?>(null);
        var checkedPending = UseRef(false);

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
            if (req == lastReq.Value) return;
            lastReq.Value = req;
            if (handle.Value is { IsOpen: true }) return;
            var session = new SetupSession(SetupSession.EntryPoint.Rerun, alreadyAuthenticated: true);
            SetupSession.Current = session;
            OpenBare(session);
        }, req);

        UseEffect(() =>
        {
            if (checkedPending.Value) return;
            checkedPending.Value = true;
            if (!SetupGating.IsPending(_settings)) return;
            if (handle.Value is { IsOpen: true }) return;
            // Reuse the SAME session the pre-auth mount was carrying (page/direction/apply state survives the
            // pre-auth → post-auth remount) when there is one; otherwise (the shell mounted already authenticated —
            // e.g. a fast silent-resume that never showed SetupPreAuthRoot at all) start a fresh one. Either way
            // `alreadyAuthenticated: true` here only matters for the FRESH case — a carried-over session already
            // froze its own SkipSignIn at construction.
            var session = SetupSession.Current ??= new SetupSession(SetupSession.EntryPoint.FirstRun, alreadyAuthenticated: true);
            // Resume the carried session exactly where it was. Auth swaps the pre-auth overlay host out the moment a
            // token lands, so the SignIn page's Done phase — the "Is this you?" confirmation — is only ever SEEN here,
            // post-auth. The user's "Yes, continue" advances; the auth flip itself must not (an earlier revision jumped
            // straight to LocalPlayback here, which is exactly the silent wrong-account sign-in the confirmation
            // exists to catch).
            OpenBare(session);
        }, DepKey.Empty);

        return new BoxEl { HitTestVisible = false, Shrink = 0f };
    }
}
