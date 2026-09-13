using System;
using FluentGpu.Controls;   // Route
using FluentGpu.Dsl;
using FluentGpu.Animation;  // MotionTok (the design-switch motion)
using FluentGpu.Foundation; // EnterExit
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The ONE sidebar mount seam. Replaces the direct <c>WaveeSidebar</c> mount in <c>WaveeShell</c> (docked pane +
/// narrow drawer). Re-renders when <c>SidebarPreferences.Design</c> changes and mounts the selected mode component under a
/// design-derived <c>Key</c> so a switch REMOUNTS (fresh hooks / section state / Enter motion) instead of reusing an
/// instance. Owns nothing itself: every signal it forwards is either the shell's or the preference service's.</summary>
sealed class SidebarHost : Component
{
    readonly Signal<Route> _route;
    readonly Action<string, string?> _go;
    readonly Signal<bool> _compact;          // PRESENTED compact (shell narrow-band OR user collapse) — never the raw pref
    readonly Signal<float> _expandedWidth;   // the live pane width signal (SidebarPreferences.Width)
    readonly bool _inDrawer;                 // frozen per mount site: the docked host and the drawer host are two mounts

    public SidebarHost(Signal<Route> route, Action<string, string?> go, Signal<bool> compact,
                       Signal<float> expandedWidth, bool inDrawer = false)
    {
        _route = route; _go = go; _compact = compact; _expandedWidth = expandedWidth; _inDrawer = inDrawer;
    }

    public override Element Render()
    {
        // Read prefs from CONTEXT, never a ctor field: the service instance is reference-stable for the process lifetime,
        // so the provide never churns consumers (the ActionServices precedent).
        var prefs = UseContext(SidebarPreferences.Slot);
        // The ONLY signal read in this body. It must not read width/collapse/filters — those are read inside the mode
        // components and inside bound props, so a filter change never re-renders the host and therefore can never risk a
        // remount of the whole pane.
        var design = prefs?.Design.Value ?? SidebarDesign.Classic;

        Element mode = design switch
        {
            SidebarDesign.LibraryV3 => Embed.Comp(() => new LibraryV3Sidebar(_route, _go, _compact, _expandedWidth, _inDrawer)),
            SidebarDesign.Curated => Embed.Comp(() => new CuratedSidebar(_route, _go, _compact, _expandedWidth, _inDrawer)),
            _ => Embed.Comp(() => new WaveeSidebar(_route, _go, _compact, _expandedWidth)),
        };

        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            // Key is MANDATORY. Without it the reconciler reuses the previous ComponentEl slot whenever ComponentType
            // matches, so two mode components that later shared a base type would silently reuse hooks across a switch.
            // The Key makes the remount unconditional and explicit: "sidebar.classic" | ".v3" | ".curated".
            // Top-level navigation changes use a quick FADE only. Local row/card interactions own spatial motion; moving
            // the entire navigation pane on every design switch competes with the shell's width transition.
            Children = [mode with
            {
                Key = SidebarDesignInfo.MountKey(design),
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Transition = MotionTok.ControlFast,
            }],
        };
    }
}
