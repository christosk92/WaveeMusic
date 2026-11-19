// ── Shell/Shell.Connected.cs ───────────────────────────────────────────────────────────────────────────────────────
// the connected navigation's shell seam: a picture flies into the header of the page it opens (and back on Back), or the page grows
// out of the item it was opened from (and shrinks back toward it)
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
// thing that travels. The structure's `PageMotionPolicy.Connected` says how it flies; null (Classic, Zune today) flies nothing.
// Back flies the header home into the card it came from; mouse Forward after a Back flies from that card again (the engine remembers it).
//
// THE GROW. A kind the structure names in `PageMotionPolicy.GrowInto` (WinUI: the artist, whose header photograph is not the card's
// avatar) flies nothing: its page grows out of the item the user activated (`SharedTransition.NavOriginRect`: the engine's rect of that
// element, remembered under the page's key) and shrinks back toward it on Back (`GrowTransition`, which `PageTransition` applies).

using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace Wavee;

public static partial class Shell
{
    /// <summary>The host's flight trigger, captured once by <see cref="ContentHost"/> (the keep-alive boundary's component reads the
    /// ambient context). Null headless before the first render and in a host that publishes none: nothing flies.</summary>
    static Func<ConnectedNavRequest, bool>? s_beginNav;

    /// <summary>The host's grow origin (<c>SharedTransition.NavOriginRect</c>), captured with <see cref="s_beginNav"/>. Null: a growing kind
    /// grows from the page's centre.</summary>
    static Func<ConnectedOriginRequest, RectF>? s_navOrigin;

    /// <summary>The box the keep-alive pages are laid out in (<see cref="ContentHost"/>'s clipping box): a grow's pivot is measured against it.</summary>
    static NodeHandle s_pageBox;

    /// <summary>The route a flyer is up for (<see cref="Route.None"/>: none). The swap TO it plays quiet (<see cref="PageTransition"/>,
    /// <see cref="PageCascade"/>); every other swap keeps its recipe. Rewritten by every commit.</summary>
    static Route s_connectedFor = Route.None;

    /// <summary>The route the next swap grows into (forward) or shrinks out to (Back), <see cref="Route.None"/>: none; its recipe, whether it is
    /// Back, and the item's rect (window DIP; empty: the page's centre) with the page box's rect it was measured in. Rewritten by every commit.</summary>
    static Route s_growFor = Route.None;
    static Theming.GrowNav? s_grow;
    static bool s_growBack;
    /// <summary>The armed grow's page enters as one block: its units do not stagger in (<see cref="PageCascade"/>). A section's drill.</summary>
    static bool s_growQuietUnits;

    /// <summary>A section's "See all" page (a Home or Browse section's drill).</summary>
    static bool IsSectionDrill(in Route r) => r.Kind is RouteKind.HomeSection or RouteKind.BrowseSection;

    /// <summary>Is the swap to <paramref name="next"/> an armed grow whose page enters as one block?</summary>
    static bool GrowsAsOneBlock(in Route next) => s_growQuietUnits && s_grow is not null && !s_growFor.IsNone && SameSlot(next, s_growFor);
    static RectF s_growItem, s_growBox;

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

    /// <summary>A forward commit (<see cref="Commit"/>): fly the activated picture into <paramref name="to"/>'s header, or grow the page out of
    /// the activated item.</summary>
    static void ArmForward(in Route to)
        => Arm(in to, in to, back: false, s_fromActivated ? ConnectedNavSource.ActivatedAnyPicture : ConnectedNavSource.Activated);

    /// <summary>Back (<see cref="GoBack"/>): fly the header of <paramref name="leaving"/> back into the card it came from on
    /// <paramref name="arriving"/>, the page Keep-alive restores exactly, or shrink a grown page back toward its item. A flight still on its
    /// way turns around.</summary>
    static void ArmBack(in Route leaving, in Route arriving) => Arm(in arriving, in leaving, back: true, ConnectedNavSource.Activated);

    /// <summary>Forward (<see cref="GoForward"/>, mouse Forward after a Back): fly from the card the last flight into <paramref name="to"/> left
    /// from, when it is still on screen and still shows that picture, or grow from the item remembered for it; else only the swap plays.</summary>
    static void ArmForwardStep(in Route to) => Arm(in to, in to, back: false, ConnectedNavSource.Remembered);

    /// <summary>A commit that flies nothing (a tab switch, a page replaced in place, a restored history): abandons a running flight.</summary>
    static void ArmNone(in Route to) => Arm(in to, Route.None, back: false, ConnectedNavSource.Activated);

    /// <summary>One commit to <paramref name="to"/>; <paramref name="page"/> is the page whose key it moves under (the one opened, or the one
    /// left on Back; none for a commit that moves nothing).</summary>
    static void Arm(in Route to, in Route page, bool back, ConnectedNavSource source)
    {
        s_connectedFor = Route.None;
        s_growFor = Route.None;
        s_grow = null;
        s_growQuietUnits = false;
        s_fromActivated = false;
        var motion = Theming.ThemeHost.Facts.Peek().Policies.PageMotion;
        string? key = page.IsNone ? null : KeyOf(in page);
        // Settings ▸ Appearance ▸ Connected animations off: nothing flies or grows, but the engine is still told, so a running flight is abandoned.
        if (!Platform.Settings.Get(Platform.Keys.ConnectedAnimations)) key = null;
        // A kind that grows flies nothing: its page scales out of the item (and back toward it), the swap keeps its fades.
        if (key is not null && motion.GrowInto?.Invoke(page.Subject.Kind) is { } grow)
        {
            ArmGrow(in to, key, grow, back, remembered: back || source == ConnectedNavSource.Remembered);
            key = null;
        }
        // A section's "See all" page grows out of where it was opened (its link in the section's head), the same way.
        else if (IsSectionDrill(in page) && motion.GrowIntoSection is { } sectionGrow && Platform.Settings.Get(Platform.Keys.ConnectedAnimations))
        {
            ArmGrow(in to, "section:" + page.Subject.Text, sectionGrow, back, remembered: back || source == ConnectedNavSource.Remembered);
            s_growQuietUnits = true;
            key = null;
        }
        if (s_beginNav is not { } begin || motion.Connected is not { } spec) return;
        if (begin(new ConnectedNavRequest(key ?? "", back, spec, source))) s_connectedFor = to;
    }

    /// <summary>Arm the grow into (or, on Back, out to) <paramref name="to"/>: the item's rect from the engine (the activated element, else the
    /// one remembered under <paramref name="key"/>), or none (the page's centre). Under reduced motion nothing is armed: the structure's
    /// reduced fade plays (<see cref="PageTransition"/>).</summary>
    static void ArmGrow(in Route to, string key, Theming.GrowNav grow, bool back, bool remembered)
    {
        if (FluentGpu.Dsl.Motion.ReducedMotion) return;
        RectF box = PageBox();
        s_growItem = s_navOrigin is { } origin && !box.IsEmpty
            ? origin(new ConnectedOriginRequest(key, remembered, box, grow.MinVisible)) : default;
        s_growBox = box;
        s_growFor = to;
        s_grow = grow;
        s_growBack = back;
    }

    /// <summary>The keep-alive pages' box (window DIP), or empty before it is laid out.</summary>
    static RectF PageBox()
        => s_scene is { } scene && !s_pageBox.IsNull && scene.IsLive(s_pageBox) ? scene.AbsoluteRect(s_pageBox) : default;

    /// <summary>The swap to <paramref name="next"/> as a grow (<see cref="ArmGrow"/>), or null when none is armed for it. Forward: the incoming
    /// page scales from <see cref="GrowNav.EnterScale"/> about the item's centre and fades in on the spring, over the recipe's quiet exit leg.
    /// Back: the leaving page shrinks to <see cref="GrowNav.BackScale"/> toward the item and fades out, under the recipe's quiet enter leg.
    /// The pivot is an offset of (1 - scale) x (item centre - page centre) on the same spring as the scale, so the item's centre holds still
    /// on every frame while the page's own transform origin stays its centre. <paramref name="sameKind"/>: an artist from an artist, whose swap
    /// never shows two pages at once (<see cref="Design.Nav.SameKindSwap"/>): forward grows without the overlap, Back keeps that swap.</summary>
    static LayoutTransition? GrowTransition(in Route next, in LayoutTransition chosen, bool sameKind)
    {
        if (s_grow is not { } g || s_growFor.IsNone || !SameSlot(next, s_growFor)) return null;
        float pivotX = 0f, pivotY = 0f;   // the item's centre against the page's (none: the page's centre)
        if (!s_growItem.IsEmpty && !s_growBox.IsEmpty)
        {
            pivotX = s_growItem.X + s_growItem.W * 0.5f - (s_growBox.X + s_growBox.W * 0.5f);
            pivotY = s_growItem.Y + s_growItem.H * 0.5f - (s_growBox.Y + s_growBox.H * 0.5f);
        }
        LayoutTransition quiet = Design.Nav.Quiet(in chosen);
        if (!s_growBack)
        {
            float s = g.EnterScale;
            var grown = quiet with
            {
                Enter = new EnterExit(Dx: (1f - s) * pivotX, Dy: (1f - s) * pivotY, Sx: s, Sy: s, Opacity: g.EnterOpacity, Active: true),
                Dynamics = TransitionDynamics.Spring(g.Response, 1f),
                DelayMs = 0f,
                ExitDynamics = quiet.ExitDynamics ?? quiet.Dynamics,
                ExitDelayMs = quiet.ExitDelayMs ?? quiet.DelayMs,
            };
            return sameKind ? Design.Nav.NoOverlap(in grown) : grown;
        }
        if (sameKind) return null;
        float b = g.BackScale;
        return quiet with
        {
            Exit = new EnterExit(Dx: (1f - b) * pivotX, Dy: (1f - b) * pivotY, Sx: b, Sy: b, Opacity: 0f, Active: true),
            ExitDynamics = TransitionDynamics.Spring(g.Response * g.BackResponseScale, 1f),
            ExitDelayMs = 0f,
        };
    }

    /// <summary>The key of a route: the uri of the entity whose page it is (an album, prerelease, playlist, show, episode or user: the flight's
    /// key; an artist: the grow's, which no picture is tagged with), else null. Discography and ProfileList carry an entity subject but have no
    /// header picture: they never fly. One string per navigation.</summary>
    static string? KeyOf(in Route r) => r.Kind is RouteKind.Album or RouteKind.Prerelease or RouteKind.Playlist
        or RouteKind.Show or RouteKind.Episode or RouteKind.User ? Design.MorphKeys.ForUri(r.Subject.Text)
        : r.Kind == RouteKind.Artist ? r.Subject.Text is { Length: > 0 } uri ? uri : null
        : r.Kind == RouteKind.Liked ? EntityUri.LikedCollection : null;   // Liked Songs' generated cover flies whole under its collection uri

    /// <summary>Is the swap to <paramref name="next"/> the one a flyer is up for, under a structure that wants the page quiet?</summary>
    static bool SwapsUnderFlight(in Route next)
        => !s_connectedFor.IsNone && SameSlot(next, s_connectedFor)
           && Theming.ThemeHost.Facts.Peek().Policies.PageMotion.ConnectedQuietPage;
}
