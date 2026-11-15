// ── Shell/Shell.Connected.cs ───────────────────────────────────────────────────────────────────────────────────────
// the connected navigation flight's shell seam: an album card's cover flies into the album page's hero, and back on Back
//
// Role: SHELL
// Owner: I
//
// THE SEAM. The engine owns the flight (`ConnectedAnimation.BeginNav`, published as `SharedTransition.BeginNav`): it finds the
// tagged cover inside the element the user activated, puts a flyer up at its rect, and lands it on the like-tagged cover of the
// page that mounts (`Design.MorphKeys.ForUri`: the album uri on the card, the hero and here). The shell tells it about EVERY
// route commit, before the route is written: the key of the page being opened (or left, for Back), an empty key when the route
// has none, so a running flight to another page is abandoned and navigation never waits for one. When a flyer is up, the swap
// under it is quiet (`Design.Nav.Quiet`: the recipe's opacity legs, no slide, no zoom, no stagger), so the cover is the only
// thing that travels. The structure's `PageMotionPolicy.Connected` says how it flies; null (Classic, Zune today) flies nothing.

using FluentGpu.Animation;

namespace Wavee;

public static partial class Shell
{
    /// <summary>The host's flight trigger, captured once by <see cref="ContentHost"/> (the keep-alive boundary's component reads the
    /// ambient context). Null headless before the first render and in a host that publishes none: nothing flies.</summary>
    static Func<ConnectedNavRequest, bool>? s_beginNav;

    /// <summary>The route a flyer is up for (<see cref="Route.None"/>: none). The swap TO it plays quiet (<see cref="PageTransition"/>,
    /// <see cref="PageCascade"/>); every other swap keeps its recipe. Rewritten by every commit.</summary>
    static Route s_connectedFor = Route.None;

    /// <summary>A forward commit (<see cref="Commit"/>): fly the activated card's cover into <paramref name="to"/>'s hero.</summary>
    static void ArmForward(in Route to) => Arm(in to, KeyOf(in to), back: false);

    /// <summary>Back (<see cref="GoBack"/>): fly the hero of <paramref name="leaving"/> back into the card it came from on
    /// <paramref name="arriving"/>, the page Keep-alive restores exactly. A flight still on its way turns around.</summary>
    static void ArmBack(in Route leaving, in Route arriving) => Arm(in arriving, KeyOf(in leaving), back: true);

    /// <summary>A commit that flies nothing in this slice (Forward, a tab switch, a page replaced in place): abandons a running flight.</summary>
    static void ArmNone(in Route to) => Arm(in to, null, back: false);

    static void Arm(in Route to, string? key, bool back)
    {
        s_connectedFor = Route.None;
        if (s_beginNav is not { } begin || Theming.ThemeHost.Facts.Peek().Policies.PageMotion.Connected is not { } spec) return;
        if (begin(new ConnectedNavRequest(key ?? "", back, spec))) s_connectedFor = to;
    }

    /// <summary>The flight key of a route: its album's uri (albums only in this slice), else null. One string per navigation.</summary>
    static string? KeyOf(in Route r) => r.Subject.Kind == EntityKind.Album ? Design.MorphKeys.ForUri(r.Subject.Text) : null;

    /// <summary>Is the swap to <paramref name="next"/> the one a flyer is up for, under a structure that wants the page quiet?</summary>
    static bool SwapsUnderFlight(in Route next)
        => !s_connectedFor.IsNone && SameSlot(next, s_connectedFor)
           && Theming.ThemeHost.Facts.Peek().Policies.PageMotion.ConnectedQuietPage;
}
