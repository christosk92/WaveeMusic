// ── Home/Board/Hosts/HomeBoardView.cs — the pinned Home board's view state (pinned-home-tech.md 1.2.3) ──────────────────────────
//
// Role: CORE (never saved). What the board shows that is not the document: the one open tile, Zune's selected tile, the edit mode
// (Classic's Edit Home, Zune's customize; WinUI never sets it), the tile just added (ringed for a moment), the open add surface and its
// query, Classic's reset question. One instance per Home tab, held by `HomeBoardHost` for its lifetime; `Reset` on a theme switch, an
// account switch or leaving Home.

using FluentGpu.Controls;
using FluentGpu.Signals;
using Wavee.Theming;

namespace Wavee;

/// <summary>Which add surface is open.</summary>
public enum HomeAddSurfaceKind : byte
{
    /// <summary>None.</summary>
    Closed,

    /// <summary>WinUI's add band under the header (<see cref="HomeAddSurface.Arg"/>: the selector kind).</summary>
    Band,

    /// <summary>Classic's add row at a group's end (<see cref="HomeAddSurface.Arg"/>: the group id).</summary>
    Row,

    /// <summary>Zune's all items (<see cref="HomeAddSurface.Arg"/>: the pivot).</summary>
    AllItems,

    /// <summary>Classic's find strip under the edit bar.</summary>
    Find,
}

/// <summary>The open add surface: its kind and its argument (a selector kind, a group id, a pivot).</summary>
public readonly record struct HomeAddSurface(HomeAddSurfaceKind Kind, string Arg = "")
{
    /// <summary>No add surface.</summary>
    public static HomeAddSurface Closed => default;

    /// <summary>Whether a surface is open.</summary>
    public bool IsOpen => Kind != HomeAddSurfaceKind.Closed;
}

/// <summary>The board's view state.</summary>
public sealed class HomeBoardView
{
    /// <summary>The one tile open for its options.</summary>
    public Signal<string?> OpenId { get; } = new(null);

    /// <summary>Zune's selected tile.</summary>
    public Signal<string?> SelectedId { get; } = new(null);

    /// <summary>Classic's Edit Home, Zune's customize. WinUI never sets it.</summary>
    public Signal<bool> Editing { get; } = new(false);

    /// <summary>The tile just added (ringed for a moment).</summary>
    public Signal<string?> RingId { get; } = new(null);

    /// <summary>The open add surface.</summary>
    public Signal<HomeAddSurface> Add { get; } = new(HomeAddSurface.Closed);

    /// <summary>The open add surface's search text.</summary>
    public Signal<string> Query { get; } = new("");

    /// <summary>Set by a theme whose board has no edit mode (WinUI): the sentence the inspector's `home do=edit` answers with.</summary>
    public string? NoEditMode { get; set; }

    /// <summary>The board grid's controller (rects, neighbours, bring-into-view); set by the host.</summary>
    public PackGridController? Grid { get; internal set; }

    /// <summary>The shortcuts grid's face (the concept's "Your shortcuts" switch): 0 the shortcuts (the user's pins, then Spotify's fill), 1 the
    /// Recently played cells in the same places. Every theme's Pinned group reads it; the grid keeps its size, its cells cross-fade.</summary>
    public Signal<int> ShortcutsFace { get; } = new(0);

    /// <summary>The shortcuts grid's pager (owner 2026-11-16, "one grid, edit inside it"): the grid's paged strip binds it, the Pinned group's head
    /// draws the theme's pager over it. In memory only: a remount starts at the first page.</summary>
    public GridPager ShortcutsPager { get; } = new();

    /// <summary>Classic's reset question in the edit bar.</summary>
    public Signal<bool> ConfirmingReset { get; } = new(false);

    static readonly StateKey<bool> s_tipSeen = new("homeTipSeen", false, false, true);
    Signal<bool>? _tipSeen;

    /// <summary>The first-run tip was closed once (the active theme's <c>theme.&lt;id&gt;.state.homeTipSeen</c>; WinUI's TeachingTip reads it).</summary>
    public IReadSignal<bool> TipSeen => _tipSeen ??= new Signal<bool>(ThemeState.Get(s_tipSeen));

    /// <summary>Remember that the first-run tip was closed.</summary>
    public void MarkTipSeen()
    {
        ThemeState.Set(s_tipSeen, true);
        _ = TipSeen;
        _tipSeen!.Value = true;
    }

    /// <summary>Back to rest: nothing open, nothing selected, no mode, no surface.</summary>
    public void Reset()
    {
        OpenId.Value = null;
        SelectedId.Value = null;
        Editing.Value = false;
        RingId.Value = null;
        Add.Value = HomeAddSurface.Closed;
        Query.Value = "";
        ConfirmingReset.Value = false;
    }
}
