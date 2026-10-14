// ── Theming/Contracts/ContentState.cs — what the content region shows (themes plan 6.0, T4a) ──────────────────────────────
//
// The shell decides WHICH state the content region is in; the theme DRAWS it (`Shell.ContentState`, one view key with four
// kinds). The decision is two pure rules (`Shell.FrameRules.ContentBodyFor`, `ShowsOfflineStrip`), so every edge of it is a
// table in a test:
//
//   Body          what stands where the page goes: the page itself, an EMPTY body (a real destination whose page is not
//                 registered yet: never the not-found page), or the not-found page (nothing claims the route).
//   OfflineLane   the page-scale strip floated over the kept content while a stored credential exists but the session is
//                 down. Independent of the body: it lies over a page, an empty body and the not-found page alike.
//
// The state machines that feed it (the route reducer, the auth fold, developer mode) and the page transition (the keep-alive
// boundary's recipe) stay core; only the four pictures are the theme's.

namespace Wavee.Theming.Contracts;

/// <summary>The four states the content region can draw. <see cref="Page"/>, <see cref="EmptyBody"/> and <see cref="NotFound"/>
/// are the BODY a route gets; <see cref="OfflineLane"/> floats over any of them.</summary>
public enum ContentStateKind : byte
{
    /// <summary>A page is registered for the route: it fills the body.</summary>
    Page,

    /// <summary>A real destination whose page has not been registered (a later wave's page in an earlier build): an empty body.</summary>
    EmptyBody,

    /// <summary>Nothing claims the route (a retired key, a stale restored tab, a developer route with developer mode off).</summary>
    NotFound,

    /// <summary>The offline strip over the kept content.</summary>
    OfflineLane,
}
