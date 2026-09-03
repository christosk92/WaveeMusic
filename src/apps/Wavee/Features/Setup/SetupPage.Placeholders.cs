using System;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Every page body, dispatched by <see cref="SetupPage"/>: Terms, SignIn, LocalPlayback — the three screens
/// the redesign leaves (Appearance/Sidebar/Sound/Notifications/Done are gone).
///
/// <para>Every arm is wrapped in <see cref="SetupPageCapture"/>: <see cref="SetupSession.Primary"/>/
/// <see cref="SetupSession.Secondary"/>/<see cref="SetupSession.BuildCtx"/> run OUTSIDE any component render (a
/// footer button's onClick), so anything they need from the ambient tree — settings, the live playback bridge, the
/// LocalPlayback runtime model — has to already be attached to the session by the time they run. Capturing it
/// centrally, on every page rather than only the two phase pages, means the footer's very first render (before the
/// user has even reached SignIn/LocalPlayback) already sees the same Idle/Offer default those types start from —
/// nothing to desync on the first navigation into either.</para></summary>
static class SetupPagePlaceholders
{
    // Same box-around-Embed.Comp recipe as SetupPageHost.Frame (ContentHost.PageFor, Features/Shell/ContentHost.cs:
    // 169-171): SetupPageCapture is a ComponentEl with no layout columns of its own, so without this it (and
    // SetupPageHost.Frame's own box underneath) never claims PagesHost's bounded height.
    public static Element For(SetupPage page) => new BoxEl
    {
        Key = "setup:capture:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [Embed.Comp(() => new SetupPageCapture(page))],
    };

    static Element BodyFor(SetupPage page) => page switch
    {
        SetupPage.Terms => Embed.Comp(() => new SetupTermsPage()) with { Key = "setup:page:terms" },
        SetupPage.SignIn => Embed.Comp(() => new SetupSignInPage()) with { Key = "setup:page:sign-in" },
        SetupPage.LocalPlayback => Embed.Comp(() => new SetupLocalPlaybackPage()) with { Key = "setup:page:local-playback" },
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
            var post = UsePost();

            if (SetupSession.Current is { } session)
            {
                if (svc?.Settings is { } settings) session.AttachSettings(settings);
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
