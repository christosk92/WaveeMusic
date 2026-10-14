// ── Shell/ImmersiveService.cs ──────────────────────────────────────────────────────────────────────────────────────
// the IMMERSIVE modes as one core service (themes plan C4, X12): real window fullscreen with the prior state restored, the
// focus scope at the root with focus returned to whoever opened it, and the chrome idle machine, keyed by the MODE and not by
// whatever view happens to be mounted
//
// Role: CORE
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4.2; Video.UI.cs FullscreenSurface
//
// Before this, the fullscreen stage (`Stage.UI.cs` SurfaceCore) and the fullscreen video surface (`Video.UI.cs`
// FullscreenSurface) each ran the same two mount effects: enter OS fullscreen remembering what was there, push a focus scope —
// and undid both in their cleanup, so a REMOUNT of the view (a root swap) exited fullscreen and re-entered it, and the
// stage's idle machine was a field of the component, reborn with it.
//
// Now: `Enter…` is idempotent per kind (the second call keeps the FIRST call's remembered prior state), and a view's teardown
// calls `Release…` with "is the mode still wanted?" — a remount while it is leaves the window exactly as it is; the exit
// happens when the mode itself ended. The window and the focus go through `IImmersiveHost`, so `ImmersiveServiceTests` drives
// the real service with a recording host.

using FluentGpu.Controls.Media;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Which immersive mode.</summary>
public enum ImmersiveKind : byte { Stage, VideoFullscreen }

/// <summary>The window and the focus, as the service reaches them.</summary>
public interface IImmersiveHost
{
    bool IsWindowFullscreen();
    void SetWindowFullscreen(bool on);
    NodeHandle GetFocus();
    void PushFocusScope(NodeHandle root);
    void PopFocusScope(NodeHandle root);
    void FocusNode(NodeHandle node, bool visible);
    void RestoreFocus(NodeHandle node);
}

/// <summary>The production host: the shell's <see cref="InputHooks"/>.</summary>
public sealed class HooksImmersiveHost(InputHooks hooks) : IImmersiveHost
{
    public bool IsWindowFullscreen() => hooks.IsWindowFullscreen?.Invoke() ?? false;
    public void SetWindowFullscreen(bool on) => hooks.WindowSetFullscreen?.Invoke(on);
    public NodeHandle GetFocus() => hooks.GetFocus?.Invoke() ?? default;
    public void PushFocusScope(NodeHandle root) => hooks.PushFocusScope?.Invoke(root);
    public void PopFocusScope(NodeHandle root) => hooks.PopFocusScope?.Invoke(root);
    public void FocusNode(NodeHandle node, bool visible) => hooks.FocusNode?.Invoke(node, visible);
    public void RestoreFocus(NodeHandle node) => hooks.RestoreFocus?.Invoke(node);
}

/// <summary>The chrome idle machine (controls hide after a dwell, the cursor after a trail) with the COUNTED menu hold: the
/// device flyout, the "…" menu, the options popover and the teaching tip each hold the chrome while open, and the machine is
/// told only on the 0 ↔ 1 edge. Every call takes the clock as an argument. The machine itself is created on <see cref="Start"/>
/// (a fresh surface shows its controls, then idles away); the counter works before it.</summary>
public sealed class ChromeIdle
{
    PlayerChromeVisibility? _machine;
    int _menus;

    /// <summary>The machine exists (<see cref="Start"/> ran).</summary>
    public bool Started => _machine is not null;

    /// <summary>How many menus hold the chrome right now.</summary>
    public int MenuHolds => _menus;

    public bool ChromeVisible => _machine?.ChromeVisible ?? true;
    public bool CursorHidden => _machine?.CursorHidden ?? false;
    public double NextWakeMs => _machine?.NextWakeMs ?? double.PositiveInfinity;

    /// <summary>Create the machine at <paramref name="nowMs"/> if it does not exist. Idempotent: a surface that adopts a
    /// surviving idle keeps its state.</summary>
    public void Start(double nowMs)
    {
        if (_machine is not null) return;
        _machine = new PlayerChromeVisibility(PlayerChromeTiming.Default, nowMs);
        if (_menus > 0) _machine.SetMenuOpen(true, nowMs);     // a menu opened before the machine existed still holds it
    }

    public bool Tick(double nowMs) => _machine?.Tick(nowMs) ?? false;

    public void Activity(ChromeActivity kind, double nowMs) => _machine?.Activity(kind, nowMs);

    public void PointerMoved(float x, float y, double nowMs) => _machine?.PointerMoved(x, y, nowMs);

    public void PointerLeft(double nowMs) => _machine?.PointerLeft(nowMs);

    public void SetPointerOverControls(bool over, double nowMs) => _machine?.SetPointerOverControls(over, nowMs);

    public void SetScrubbing(bool on, double nowMs) => _machine?.SetScrubbing(on, nowMs);

    public void SetPlayback(bool playing, double nowMs)
    {
        _machine?.SetPlayback(playing ? ChromePlayback.Playing : ChromePlayback.Paused, nowMs);
        _machine?.SetCursorMayHide(true, nowMs);
    }

    /// <summary>A menu opened (<paramref name="open"/>) or closed. The count never goes below zero; the machine hears the
    /// hold as one bool (some menu is open).</summary>
    public void MenuOpen(bool open, double nowMs)
    {
        _menus = Math.Max(0, _menus + (open ? 1 : -1));
        _machine?.SetMenuOpen(_menus > 0, nowMs);
    }
}

public sealed class ImmersiveService
{
    /// <summary>The shell's service. UI thread only.</summary>
    public static readonly ImmersiveService Shared = new();

    sealed class Entry
    {
        public bool WindowActive, PriorFullscreen;
        public bool FocusActive;
        public NodeHandle Root, PriorFocus;
        public ChromeIdle? Idle;
    }

    readonly Entry[] _entries = [new(), new()];

    Entry Of(ImmersiveKind kind) => _entries[(int)kind];

    /// <summary>Is the window entered for <paramref name="kind"/>?</summary>
    public bool IsEntered(ImmersiveKind kind) => Of(kind).WindowActive;

    /// <summary>Is a focus scope pushed for <paramref name="kind"/>?</summary>
    public bool HasFocusScope(ImmersiveKind kind) => Of(kind).FocusActive;

    /// <summary>The chrome idle machine of <paramref name="kind"/>: the SAME object across a remount of the view, until the
    /// mode exits. (The machine is started by the first surface that needs it.)</summary>
    public ChromeIdle IdleOf(ImmersiveKind kind) => Of(kind).Idle ??= new ChromeIdle();

    // ── the window ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Go borderless-fullscreen, remembering whether the window already was (restore puts back what it FOUND, never
    /// an unconditional windowed). Idempotent: false (and nothing happens) when the kind is already entered.</summary>
    public bool EnterWindow(ImmersiveKind kind, IImmersiveHost host)
    {
        var e = Of(kind);
        if (e.WindowActive) return false;
        e.WindowActive = true;
        e.PriorFullscreen = host.IsWindowFullscreen();
        if (!e.PriorFullscreen) host.SetWindowFullscreen(true);
        return true;
    }

    /// <summary>Leave: restore the remembered prior state and end the idle machine with the mode. Idempotent: false when the
    /// kind was not entered.</summary>
    public bool ExitWindow(ImmersiveKind kind, IImmersiveHost host)
    {
        var e = Of(kind);
        if (!e.WindowActive) return false;
        e.WindowActive = false;
        if (!e.PriorFullscreen) host.SetWindowFullscreen(false);
        e.PriorFullscreen = false;
        e.Idle = null;
        return true;
    }

    /// <summary>A view's teardown: exit only when the mode is no longer wanted. A REMOUNT while it is leaves the window as it is.</summary>
    public bool ReleaseWindow(ImmersiveKind kind, IImmersiveHost host, Func<bool> stillWanted)
        => !stillWanted() && ExitWindow(kind, host);

    // ── the focus scope ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Push a focus scope at <paramref name="root"/> (Tab cannot walk out of it) and remember who had focus, to give it
    /// back on exit; optionally park focus on the root. A remount while the scope is up moves the scope to the new root and keeps
    /// the ORIGINAL focus to return to. A null root does nothing.</summary>
    public void PushFocus(ImmersiveKind kind, IImmersiveHost host, NodeHandle root, bool focusRoot)
    {
        if (root.IsNull) return;
        var e = Of(kind);
        if (e.FocusActive)
        {
            if (!e.Root.IsNull) host.PopFocusScope(e.Root);       // the old root's node is gone: move the scope, keep who to return to
        }
        else
        {
            e.PriorFocus = host.GetFocus();
            e.FocusActive = true;
        }
        e.Root = root;
        host.PushFocusScope(root);
        if (focusRoot) host.FocusNode(root, false);
    }

    /// <summary>Pop the scope and give focus back to whoever opened the mode. Idempotent.</summary>
    public bool PopFocus(ImmersiveKind kind, IImmersiveHost host)
    {
        var e = Of(kind);
        if (!e.FocusActive) return false;
        e.FocusActive = false;
        host.PopFocusScope(e.Root);
        var back = e.PriorFocus;
        e.Root = default;
        e.PriorFocus = default;
        if (!back.IsNull) host.RestoreFocus(back);
        return true;
    }

    /// <summary>A view's teardown for the focus scope: pop only when the mode is no longer wanted.</summary>
    public bool ReleaseFocus(ImmersiveKind kind, IImmersiveHost host, Func<bool> stillWanted)
        => !stillWanted() && PopFocus(kind, host);

    /// <summary>Forget everything without touching the window (a test seam, and the shell's reset).</summary>
    public void Reset()
    {
        for (int i = 0; i < _entries.Length; i++) _entries[i] = new Entry();
    }
}
