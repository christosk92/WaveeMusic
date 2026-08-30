using System;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Every page body, dispatched by <see cref="SetupPage"/>. Welcome/Terms/Appearance/Sidebar/Sound/
/// Notifications are real pages built in an earlier step; SignIn/LocalPlayback/Done are the three in-place phase
/// machines this step lands (<see cref="SetupSignInPage"/>/<see cref="SetupLocalPlaybackPage"/>/
/// <see cref="SetupDonePage"/>). This class name/shape (and the <c>For(SetupPage)</c> entry point
/// <see cref="SetupDialog"/> calls) stays exactly as step 3 left it.
///
/// <para>Every arm is wrapped in <see cref="SetupPageCapture"/>: <see cref="SetupSession.Primary"/>/
/// <see cref="SetupSession.Secondary"/>/<see cref="SetupSession.BuildCtx"/> run OUTSIDE any component render (a
/// footer button's onClick), so anything they need from the ambient tree — settings, the theme-transition request,
/// the live playback bridge, the LocalPlayback runtime model — has to already be attached to the session by the
/// time they run. Capturing it centrally, on every page rather than only the three phase pages, means the footer's
/// very first render (before the user has even reached SignIn/LocalPlayback) already sees the same Idle/Offer
/// default those types start from — nothing to desync on the first navigation into either.</para></summary>
static class SetupPagePlaceholders
{
    public static Element For(SetupPage page) =>
        Embed.Comp(() => new SetupPageCapture(page)) with { Key = "setup:capture:" + (int)page };

    static Element BodyFor(SetupPage page) => page switch
    {
        SetupPage.Welcome => Embed.Comp(() => new SetupWelcomePage()) with { Key = "setup:page:welcome" },
        SetupPage.Terms => Embed.Comp(() => new SetupTermsPage()) with { Key = "setup:page:terms" },
        SetupPage.SignIn => Embed.Comp(() => new SetupSignInPage()) with { Key = "setup:page:sign-in" },
        SetupPage.LocalPlayback => Embed.Comp(() => new SetupLocalPlaybackPage()) with { Key = "setup:page:local-playback" },
        SetupPage.Appearance => Embed.Comp(() => new SetupAppearancePage()) with { Key = "setup:page:appearance" },
        SetupPage.Sidebar => Embed.Comp(() => new SetupSidebarPage()) with { Key = "setup:page:sidebar" },
        SetupPage.Sound => Embed.Comp(() => new SetupSoundPage()) with { Key = "setup:page:sound" },
        SetupPage.Notifications => Embed.Comp(() => new SetupNotificationsPage()) with { Key = "setup:page:notifications" },
        SetupPage.Done => Embed.Comp(() => new SetupDonePage()) with { Key = "setup:page:done" },
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "Unknown SetupPage."),
    };

    /// <summary>The ambient-context capture wrapper (see the class doc-comment above). Renders unconditionally every
    /// time — the attach calls are idempotent field/property writes, never a signal write, so this never trips the
    /// "no signal writes during render" rule.</summary>
    sealed class SetupPageCapture : Component
    {
        readonly SetupPage _page;
        public SetupPageCapture(SetupPage page) => _page = page;

        public override Element Render()
        {
            var svc = UseContext(Services.Slot);
            var bridge = UseContext(PlaybackBridge.Slot);
            var requestTheme = UseContext(ThemeControl.Request);
            var post = UsePost();

            if (SetupSession.Current is { } session)
            {
                if (svc?.Settings is { } settings) session.AttachSettings(settings);
                session.AttachRequestTheme(requestTheme);
                if (bridge is not null)
                {
                    session.AttachBridge(bridge);
                    if (svc is not null && svc.Settings is { } s2) session.EnsureRuntime(svc, s2, bridge, post);
                }
            }

            return BodyFor(_page);
        }
    }
}
