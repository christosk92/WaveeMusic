// ── Shell/FrameModel.cs — the frame's decisions, facts and verbs ───────────────────────────────────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X47). `Shell.UI.cs`'s `FrameRoot` seeded the sidebar's band and mode on
// its first render, derived the band from the window width, and resolved the mode and presented width in one effect, all
// inline. The decisions are pure functions here (engine-free; the tests drive every branch); the frame component reads the
// signals, asks, and writes the answers back in the order it always wrote them.
//
// THE POLICY IS THE STRUCTURE'S. `NavPaneState`'s band comes from the window's width TIER (T3b: `WindowTiers` resolves it
// through the active structure's window ladder, with its hysteresis; Classic's 528 / 568 and 1008 / 1048 live in
// `Themes\Classic\Structure\Rules\Tiers.cs`); its mode and presented width come from `SidebarPaneModeRules` over a
// `NavPanePolicy` (T3). Every decision here takes a trailing policy; null is the active one. The frame's effects pass
// `ShellPolicy.Tracked.NavPane` so a policy change re-runs them.
//
// THE FACTS (`FrameFacts`) are the few reads a view must not make from settings or from a foreign model's signals: developer
// mode, "presenting" (the Store shot), the immersive stage and the derived "chrome mounted".
//
// THE VERBS (`IFrameVerbs`) are what the frame's chords and buttons do. The composition of the other verb sets (tabs, the
// notification panel, the omnibar) lives with them: `WorkspaceVerbs`, `NotificationVerbs`, `OmnibarModel`.

using FluentGpu.Signals;
using Wavee.Theming.Contracts;

namespace Wavee;

/// <summary>The window as the frame decides from it: the layout viewport in DIP (after app zoom).</summary>
public readonly record struct WindowFacts(float Width, float Height);

/// <summary>What the navigation pane presents: the window band, the resolved mode, the presented width and the fade of the
/// pane's content while a seam drag is collapsing it.</summary>
public readonly record struct NavPaneState(SidebarWindowBand Band, SidebarPaneMode Mode, float PresentedWidth, float Fade);

/// <summary>The frame's pure decisions.</summary>
public static class FrameModel
{
    /// <summary>The pane's first values, known only when the frame first renders. <paramref name="windowTier"/> is the tier a
    /// FRESH resolve gave the first frame (only the entering edges apply: Classic at 600 is Small, at 500 Compact), so a small
    /// launch lays out as a rail (or no pane) on frame one.</summary>
    public static NavPaneState Seed(in WindowFacts window, WidthTier windowTier, bool userCollapsed, bool editing, float expandedWidth,
        Theming.NavPanePolicy? policy = null)
    {
        policy = ShellPolicy.NavOr(policy);
        var band = SidebarPaneModeRules.BandOf(windowTier);
        var mode = SidebarPaneModeRules.Resolve(band, userCollapsed, editing, policy);
        return new NavPaneState(band, mode, SidebarPaneModeRules.PresentedWidth(mode, expandedWidth, window.Width, policy), 1f);
    }

    /// <summary>THE WINDOW BAND (design V.1): the window's width tier read as a sidebar band — the ONLY way the window width
    /// reaches the sidebar. The tier is hysteretic and deferred to the first frame that is not resized (<c>WindowTiers</c>).</summary>
    public static SidebarWindowBand BandOf(WidthTier windowTier) => SidebarPaneModeRules.BandOf(windowTier);

    /// <summary>Leaving into a band with no overlay pane closes the overlay.</summary>
    public static bool ClosesOverlay(SidebarWindowBand band, Theming.NavPanePolicy? policy = null)
        => !SidebarPaneModeRules.HasOverlay(band, policy);

    /// <summary>THE ONE PRESENTATION DECISION: ① the mode, ② the presented width. A live seam drag presents the tracked seam
    /// (<paramref name="dragging"/> and a band where the seam is visible); it never writes the preference
    /// (<c>Sidebar.Width</c> / <c>UserCollapsed</c> are persisted at commit).</summary>
    public static NavPaneState Present(in WindowFacts window, SidebarWindowBand band, bool userCollapsed, bool editing,
        float expandedWidth, bool dragging, float seam, Theming.NavPanePolicy? policy = null)
    {
        policy = ShellPolicy.NavOr(policy);
        if (dragging && SidebarPaneModeRules.SeamVisible(band, policy))
        {
            var state = new SidebarResizeRules.State(userCollapsed, expandedWidth);
            var live = SidebarResizeRules.Track(seam, in state, editing, policy);
            return new NavPaneState(band, live.Collapsed ? SidebarPaneMode.Compact : SidebarPaneMode.Expanded,
                live.PresentedWidth, live.Fade);
        }
        var mode = SidebarPaneModeRules.Resolve(band, userCollapsed, editing, policy);
        return new NavPaneState(band, mode, SidebarPaneModeRules.PresentedWidth(mode, expandedWidth, window.Width, policy), 1f);
    }

    /// <summary>Is a seam drag being presented (rather than the resolved mode)? The frame reads the seam signal only then.</summary>
    public static bool PresentsDrag(bool dragging, SidebarWindowBand band, Theming.NavPanePolicy? policy = null)
        => dragging && SidebarPaneModeRules.SeamVisible(band, policy);

    /// <summary>FULL-SCREEN VIDEO OR THE FULLSCREEN STAGE COLLAPSES THE CHROME ROW, THE CONTENT REGION AND THE PLAYER
    /// BAR — one derived predicate drives all three bound <c>Visible</c> channels, so there is exactly one transport on
    /// screen and the surface mounting and the chrome leaving cannot disagree by a frame (ch 18 §0.12). A collapse, not
    /// an unmount: the bands stay mounted (out of layout, paint, hit-test and focus, their subtree inactive), so leaving
    /// fullscreen does not rebuild the tab row and the whole player bar.</summary>
    public static bool ChromeMounted(Video.SurfacePlacement resolved, bool immersive)
        => resolved != Video.SurfacePlacement.Fullscreen && !immersive;

    // ── the unknown-route note ───────────────────────────────────────────────────────────────────────────────────────

    static readonly HashSet<string> s_unknownRoutes = new(StringComparer.Ordinal);

    /// <summary>The host's sink for "nothing claims this route" (set once by the composition root; it logs). The frame model
    /// decides WHEN to say it — the first sighting of a key — the host decides how.</summary>
    public static Action<string>? UnknownRouteSink { get; set; }

    /// <summary>A route nothing claims was drawn as the not-found page. Reports each distinct key once per process.</summary>
    public static bool NoteUnknownRoute(string key)
    {
        if (!s_unknownRoutes.Add(key)) return false;
        UnknownRouteSink?.Invoke(key);
        return true;
    }
}

/// <summary>The few facts a view reads about the frame.</summary>
public static class FrameFacts
{
    /// <summary>Developer mode, reactive (a flip re-renders every reader).</summary>
    public static IReadSignal<bool> Developer => Platform.Developer.Enabled;

    /// <summary>Developer mode as the persisted switch says right now, read without subscribing — for a decision made once
    /// per render of a page that does not follow the switch live (the not-found gate, the history page's developer rows).</summary>
    public static bool DeveloperNow => Platform.Settings.Get(Platform.Keys.DeveloperMode);

    /// <summary>Presentation mode (the Store shot): a generic picture and no account name.</summary>
    public static IReadSignal<bool> Presenting => Diagnostics.StoreShot.Presenting;

    /// <summary>The fullscreen (immersive) stage is up.</summary>
    public static IReadSignal<bool> ImmersiveOpen => Shell.Ui.ImmersiveLyrics;

    /// <summary>Is the chrome (title row, content region, player bar) mounted? False under fullscreen video or the immersive stage.</summary>
    public static bool ChromeMounted()
        => FrameModel.ChromeMounted(Video.PlacementCore.Resolve(Video.State.Surface.Value), ImmersiveOpen.Value);
}

/// <summary>What the frame's chords and buttons do.</summary>
public interface IFrameVerbs
{
    void GoBack();
    void GoForward();

    /// <summary>A new tab on Home.</summary>
    void NewTab();

    /// <summary>Open or close the command palette.</summary>
    void TogglePalette();

    /// <summary>Ctrl+F: ask the omnibar for focus.</summary>
    void FocusSearch();

    /// <summary>Ctrl+,: open Settings.</summary>
    void OpenSettings();

    /// <summary>F11: close the stage / toggle video fullscreen / open the stage, whichever applies.</summary>
    void ToggleFullscreen();

    /// <summary>App zoom, one ladder rung per call (0 = reset to 100 %).</summary>
    void Zoom(int direction);

    /// <summary>Sign out — after the user confirmed it (the confirm card is the view's).</summary>
    void LogOut();
}

/// <summary>The shell's <see cref="IFrameVerbs"/>.</summary>
public sealed class FrameVerbs : IFrameVerbs
{
    /// <summary>The process-wide verbs the frame's chords call.</summary>
    public static IFrameVerbs Shared { get; } = new FrameVerbs();

    public void GoBack() => Shell.GoBack();
    public void GoForward() => Shell.GoForward();
    public void NewTab() => Shell.OpenTab(new Shell.Route(Shell.RouteKind.Home));
    public void TogglePalette() => Shell.PaletteOpen.Value = !Shell.PaletteOpen.Peek();
    public void FocusSearch() => Shell.FocusSearch();
    public void OpenSettings() => Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings));
    public void ToggleFullscreen() => Shell.ToggleVideoFullscreen();
    public void Zoom(int direction) => Shell.ZoomStep(direction);

    public void LogOut()
    {
        Log.Info("ui", "profile.logout.confirmed");
        Spotify.Logout();
    }
}
