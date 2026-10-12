// ── Shell/NavModel.Selection.cs ────────────────────────────────────────────────────────────────────────────────────
// the model's two live facts about the app around the pane: which route is selected, and what is playing
//
// Role: CORE · Plan: technical-plan.md §5.1 row C9
//
// A pane draws a selection pill on the row whose route is selected, and a playing mark on the rows whose entity relates to
// the playing context. Both are decided here from the shell's current route and the playback seam; WHICH ROW of a plan that
// is (an index, a pill target) is the view's, because it is a fact about a plan.

namespace Wavee;

public sealed partial class NavModel
{
    Shell.Route _routeCache = Shell.Route.None;
    string _routeKeyCache = "";

    // ── selected ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The live selected route key. SUBSCRIBES the caller to the shell's current route.</summary>
    public string Selected => RouteKeyOf(Shell.Current.Value);

    /// <summary>The selected route key WITHOUT subscribing (rows re-render on their own epoch).</summary>
    public string SelectedPeek => RouteKeyOf(Shell.Current.Peek());

    string RouteKeyOf(in Shell.Route route)
    {
        if (!route.Equals(_routeCache))
        {
            _routeCache = route;
            _routeKeyCache = Shell.NameOf(route);
        }
        return _routeKeyCache;
    }

    // WHICH SECTION holds a route (the pill's header or folder anchor) is the planner's answer, not the model's: it depends on
    // which layouts exist and what each holds (X26). See `SidebarRowPlanner.OwningSection`, reached through `NavRowKit`.

    // ── playing ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Is a context loaded, and is it playing? SUBSCRIBES the caller: the coarse active-context gate first (an idle
    /// app never joins the identity fan-out), the play state only while one is active.</summary>
    public NavPlaying ReadPlaying()
    {
        var seam = Controls.NowPlaying;
        bool active = seam is not null && seam.HasActiveContext.Value;
        bool playing = active && seam!.IsPlaying.Value;
        return new NavPlaying(active, playing);
    }

    /// <summary>Does the playing context relate to <paramref name="uri"/> (the entity itself, its container, a track inside it)?
    /// False while nothing is loaded.</summary>
    public bool PlayingRelatesTo(string uri) => Controls.NowPlaying is { } seam && seam.RelatesTo(uri);
}
