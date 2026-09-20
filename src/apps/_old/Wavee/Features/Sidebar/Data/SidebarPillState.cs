using System;

namespace Wavee;

/// <summary>
/// The live state of ONE item-owned NavigationView selection indicator (the left accent pill), and the ONE rule that
/// decides whether it is lit.
///
/// <para><b>Why the rule lives here and not in the component.</b> The pill used to render
/// <c>Opacity = selected ? 1f : 0f</c> as a MOUNT-TIME literal read out of the slot's snapshot, so its visibility was
/// whatever the last render happened to see; anything that wrote the node's opacity afterwards (the pane's moving-pill
/// transaction, a force-completed flight, a registry entry pointing at a recycled node) left a row lit that the row's
/// own state said must be dark — two pills at once (#22/#23). The pill's opacity is now a BOUND read of this state, the
/// same discipline the drop cue uses, and this type is the single place that maps a route to a lit/dark pill. It is
/// engine-free (System only) so <c>Wavee.Tests</c> drives the REAL rule instead of a copy of it.</para>
///
/// <para><b>The pill means "this is the open route", nothing else.</b> Playback is the <c>|||</c> glyph and never the
/// pill: <see cref="Route"/> is a NAV route key, and <see cref="Lit"/> compares it to the live route only. A row can
/// therefore be playing, hovered, dragged or expanded without ever touching this.</para>
/// </summary>
public readonly record struct SidebarPillState(
    string? Route,
    bool Selected,
    float Indent,
    float Top)
{
    /// <summary>The lit opacity of an accent pill.</summary>
    public const float LitOpacity = 1f;

    /// <summary>The dark opacity of an accent pill (mounted always, drawn only when selected).</summary>
    public const float DarkOpacity = 0f;

    /// <summary>The pill's opacity — DERIVED from <see cref="Selected"/>, never authored as a literal.</summary>
    public float Opacity => Selected ? LitOpacity : DarkOpacity;

    /// <summary>THE RULE: an indicator is lit exactly when the route it was drawn for IS the live nav route. A row with
    /// no route (a folder, a track, chrome) can never be lit, and neither can any row while the live route is empty.
    /// Ordinal by contract — route keys are ids, never display text.</summary>
    public static bool Lit(string? route, string liveRoute)
        => route is { Length: > 0 } r
           && !string.IsNullOrEmpty(liveRoute)
           && string.Equals(r, liveRoute, StringComparison.Ordinal);

    /// <summary>This state re-derived against the live route. The single call every probe makes, so a snapshot taken at
    /// render time can never carry a stale <see cref="Selected"/> into the next frame.</summary>
    public SidebarPillState For(string liveRoute) => this with { Selected = Lit(Route, liveRoute) };

    /// <summary>As <see cref="For(string)"/>, but with the pane's own row-level verdict folded in: the pill is lit only
    /// when the PANE says this row draws selected (<c>SidebarRowResolve.SelectsRoute</c>, the one owner the selection
    /// sweep also uses) AND the route this pill was drawn for is still the live one. The conjunction is what makes a
    /// recycled slot dark for the one frame in which its index has moved but its snapshot has not.</summary>
    public SidebarPillState For(string liveRoute, bool rowSelectsRoute)
        => this with { Selected = rowSelectsRoute && Lit(Route, liveRoute) };
}
