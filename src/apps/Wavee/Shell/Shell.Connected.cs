// ── Shell/Shell.Connected.cs ───────────────────────────────────────────────────────────────────────────────────────
// the connected navigation flight's shell seam: a picture flies into the header of the page it opens, and back on Back
//
// Role: SHELL
// Owner: I
//
// THE SEAM. The engine owns the flight (`ConnectedAnimation.BeginNav`, published as `SharedTransition.BeginNav`): it finds the
// tagged picture inside the element the user activated, puts a flyer up at its rect, and lands it on the like-tagged header of the
// page that mounts (`Design.MorphKeys.ForUri`: the entity's uri on the card, the header and here). The shell tells it about EVERY
// route commit, before the route is written: the key of the page being opened (or left, for Back), an empty key when the route
// has none, so a running flight to another page is abandoned and navigation never waits for one. When a flyer is up, the swap
// under it is quiet (`Design.Nav.Quiet`: the recipe's opacity legs, no slide, no zoom, no stagger), so the picture is the only
// thing that travels (a kind may keep its sections' staggered rise, `PageMotionPolicy.ConnectedKeepsCascade`: the artist page). The
// structure's `PageMotionPolicy.Connected` says how it flies (`ConnectedInto` per kind of page); null (Classic, Zune today) flies nothing.
// Back flies the header home into the card it came from; mouse Forward after a Back flies from that card again (the engine remembers it).

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

    /// <summary>Set by <see cref="ConnectFromActivated"/>: the next forward commit flies the largest picture inside the activated element.</summary>
    static bool s_fromActivated;

    /// <summary>Run <paramref name="navigate"/> so that the forward navigation it commits flies the largest picture inside the element the
    /// user activated, tagged or not: a bound row's template cannot carry a per-item tag (the episode reader's rows). For an open action
    /// whose row's picture is the entity's own. Cleared after it, whether or not it navigated.</summary>
    internal static void ConnectFromActivated<T>(Action<T> navigate, T arg)
    {
        s_fromActivated = true;
        try { navigate(arg); }
        finally { s_fromActivated = false; }
    }

    /// <summary>A forward commit (<see cref="Commit"/>): fly the activated picture into <paramref name="to"/>'s header.</summary>
    static void ArmForward(in Route to)
        => Arm(in to, KeyOf(in to), back: false, s_fromActivated ? ConnectedNavSource.ActivatedAnyPicture : ConnectedNavSource.Activated);

    /// <summary>Back (<see cref="GoBack"/>): fly the header of <paramref name="leaving"/> back into the card it came from on
    /// <paramref name="arriving"/>, the page Keep-alive restores exactly. A flight still on its way turns around.</summary>
    static void ArmBack(in Route leaving, in Route arriving) => Arm(in arriving, KeyOf(in leaving), back: true, ConnectedNavSource.Activated);

    /// <summary>Forward (<see cref="GoForward"/>, mouse Forward after a Back): fly from the card the last flight into <paramref name="to"/> left
    /// from, when it is still on screen and still shows that picture; else nothing flies.</summary>
    static void ArmForwardStep(in Route to) => Arm(in to, KeyOf(in to), back: false, ConnectedNavSource.Remembered);

    /// <summary>A commit that flies nothing (a tab switch, a page replaced in place, a restored history): abandons a running flight.</summary>
    static void ArmNone(in Route to) => Arm(in to, null, back: false, ConnectedNavSource.Activated);

    static void Arm(in Route to, string? key, bool back, ConnectedNavSource source)
    {
        s_connectedFor = Route.None;
        s_fromActivated = false;
        var motion = Theming.ThemeHost.Facts.Peek().Policies.PageMotion;
        if (s_beginNav is not { } begin || motion.Connected is not { } spec) return;
        // Settings ▸ Appearance ▸ Connected animations off: fly nothing, but still tell the engine, so a running flight is abandoned.
        if (!Platform.Settings.Get(Platform.Keys.ConnectedAnimations)) key = null;
        // Forward into a page whose kind flies its own way (the artist header fades its flyer out); Back flies home as a card does.
        if (!back && motion.ConnectedInto?.Invoke(to.Subject.Kind) is { } into) spec = into;
        if (begin(new ConnectedNavRequest(key ?? "", back, spec, source))) s_connectedFor = to;
    }

    /// <summary>The flight key of a route: the uri of the entity whose page it is (an album, prerelease, playlist, artist, show,
    /// episode or user), else null. Discography and ProfileList carry an entity subject but have no header picture: they never
    /// fly. One string per navigation.</summary>
    static string? KeyOf(in Route r) => r.Kind is RouteKind.Album or RouteKind.Prerelease or RouteKind.Playlist or RouteKind.Artist
        or RouteKind.Show or RouteKind.Episode or RouteKind.User ? Design.MorphKeys.ForUri(r.Subject.Text)
        : r.Kind == RouteKind.Liked ? EntityUri.LikedCollection : null;   // Liked Songs' generated cover flies whole under its collection uri

    /// <summary>Is the swap to <paramref name="next"/> the one a flyer is up for, under a structure that wants the page quiet?</summary>
    static bool SwapsUnderFlight(in Route next)
        => !s_connectedFor.IsNone && SameSlot(next, s_connectedFor)
           && Theming.ThemeHost.Facts.Peek().Policies.PageMotion.ConnectedQuietPage;

    /// <summary>Does the quiet swap to <paramref name="next"/> drop its staggered entrance? Not into a kind that keeps it under a flight
    /// (<see cref="Theming.PageMotionPolicy.ConnectedKeepsCascade"/>: the artist page's sections rise as they always do).</summary>
    static bool DropsCascadeUnderFlight(in Route next)
        => SwapsUnderFlight(in next)
           && Theming.ThemeHost.Facts.Peek().Policies.PageMotion.ConnectedKeepsCascade?.Invoke(next.Subject.Kind) != true;
}
