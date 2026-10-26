// ── Theming/Appearance.cs — the ONE owner of the light / dark / system mode ─────────────────────────────────────────
//
// Role: CORE (themes plan §1.3 / §4.1 step 6, PR C1). Before this file four routines each re-implemented "mode -> kind ->
// swap the palette -> persist -> ask for the cross-fade": the boot seed (`Shell.Host.cs`), the OS-colours listener
// (`Shell.UI.cs`), the theme flip verb (`Shell.Palette.cs`) and the Settings > Appearance > Theme selector. They had
// already drifted (only some of them re-read the OS accent), so the rules live HERE and the four call `Appearance`.
//
// THE SPLIT. `AppearanceModel` is the decision, engine-free: it talks to the world only through `IAppearancePorts`, so
// the unit tests drive the REAL rules against a recorder (`Tok` is process-wide shared state; a test never touches it).
// `Appearance` is the process-wide facade over the live ports (`Tok`, `FluentApp`, `Platform.Settings`).
//
// WHAT EACH VERB DOES (exactly what the four routines did; C1 moves it, it does not change it):
//   Seed(mode)              before the window: kind from the mode (System reads the OS), palette, the OS accent ONCE
//                           (ramp, else the plain accent colour).
//   Seed(kind)              the shell test host: a fixed kind, NO OS read, the token-default accent.
//   SetMode(mode)           the Settings selector: palette; System re-reads the OS accent; persist; cross-fade.
//   ToggleMode()            the flip verb (palette row, profile menu): the other kind, persist the
//                           explicit mode (Light = 1, Dark = 2), cross-fade. The accent is not touched.
//   OnSystemColorsChanged() the OS colours changed: the accent ramp follows ALWAYS; the kind only while the mode is System.
//   OnWindowCreated()       the window exists: the engine installed the OS accent as a `Tok.SetAccent` override while creating it;
//                           that override is released (see THE OS ACCENT).
//
// THE OS ACCENT IS AN INPUT, NOT AN OVERRIDE. `Tok.SetAccent` overrides `ThemeDefinition.Accent`, so while one is set the engine's
// controls draw the OS accent whatever the active theme's accent is (a Zune swatch). Nothing here calls it any more: the OS ramp is
// fed to `ThemeHost.OsAccent`, the active style's `Definition(options, osAccent)` decides whether it uses it (Classic always;
// Zune only with "use the windows accent"), and the definition is installed again, so the engine's accent follows the resolved
// `ThemeDefinition.Accent`. The engine itself sets one override while it creates the window (after the boot seed): `OnWindowCreated`,
// called from the root factory (after the window, before the first frame), releases it.
//
// LIVE OR TRANSACTION (themes plan 4.2, A4.2). A kind change is a colour change, so it takes the live path (swap in place, the
// 250 ms cross-fade) — unless the active definition asks for something non-colour to change with the kind (today: a different
// window material per kind). Then the kind goes through the theme transaction (freeze, settle, dissolve, the DWM change ordered
// with the dissolve) and no cross-fade is requested.

using FluentGpu;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

/// <summary>The persisted light / dark / system preference (<c>theme.mode</c>). The numbers are the stored values.</summary>
public enum ThemeMode : byte { System = 0, Light = 1, Dark = 2 }

/// <summary>What <see cref="AppearanceModel"/> needs from the process. The live implementation is
/// <c>LiveAppearancePorts</c>; the tests pass a recorder.</summary>
public interface IAppearancePorts
{
    /// <summary>The theme the token set is showing now.</summary>
    ThemeKind CurrentKind { get; }

    /// <summary>The raw persisted <c>theme.mode</c>.</summary>
    int StoredMode { get; set; }

    /// <summary>Whether the OS "app" theme is light (an unreadable registry reads as dark).</summary>
    bool SystemUsesLightTheme();

    /// <summary>Swap the installed Wavee palette to <paramref name="kind"/> in place.</summary>
    void ApplyKind(ThemeKind kind);

    /// <summary>True when switching to <paramref name="kind"/> changes more than colours in the active definition (its window
    /// material differs per kind): the change must go through the theme transaction.</summary>
    bool KindChangeIsStructural(ThemeKind kind);

    /// <summary>Switch to <paramref name="kind"/> on the theme transaction (it owns its own dissolve).</summary>
    void ApplyKindInTransaction(ThemeKind kind);

    /// <summary>Hand the OS accent to the theme host (<c>ThemeHost.OsAccent</c>) and install the active definition again, so the
    /// style decides what the engine draws: the accent ramp when the OS has one; the plain accent colour only
    /// when <paramref name="allowPlainFallback"/> (the boot and the Settings selector fall back, the OS listener does not).</summary>
    void ApplySystemAccent(bool allowPlainFallback);

    /// <summary>Drop any <c>Tok.SetAccent</c> override: the active definition's accent (which carries the OS accent where the style
    /// follows it) decides the engine's accent.</summary>
    void ReleaseAccentOverride();
}

/// <summary>The mode -> kind decisions and the verbs that apply them. Engine-free: every effect goes through
/// <see cref="IAppearancePorts"/>.</summary>
public sealed class AppearanceModel
{
    /// <summary>The re-theme cross-fade the host runs after a LIVE change (ms).</summary>
    public const float TransitionMs = Design.Motion.Standard;

    readonly IAppearancePorts _ports;
    Action<float>? _transition;

    public AppearanceModel(IAppearancePorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        _ports = ports;
    }

    /// <summary>The theme the tokens show now.</summary>
    public ThemeKind Kind => _ports.CurrentKind;

    /// <summary>The persisted preference.</summary>
    public ThemeMode Mode => Appearance.ModeOf(_ports.StoredMode);

    /// <summary>The host's re-theme trigger (<c>ThemeControl.Request</c>); null = the swap simply does not animate.</summary>
    public void BindTransition(Action<float>? request) => _transition = request;

    /// <summary>Before the window: kind from <paramref name="mode"/> (System reads the OS), the OS accent once. The icon face
    /// travels in Wavee's definition (<c>Design.Definition</c>, installed by the kind's palette seed).</summary>
    public void Seed(ThemeMode mode)
    {
        _ports.ApplyKind(KindOf(mode));   // before the window: nothing to hold, always in place
        _ports.ApplySystemAccent(allowPlainFallback: true);
    }

    /// <summary>The window exists (the root factory runs after the engine created it): release the OS-accent override the engine set
    /// while creating it, so the active definition's accent decides from the first frame.</summary>
    public void OnWindowCreated() => _ports.ReleaseAccentOverride();

    /// <summary>A fixed kind with no OS read at all (the shell test host): the persisted mode is ignored and the accent is
    /// the token default.</summary>
    public void Seed(ThemeKind fixedKind) => _ports.ApplyKind(fixedKind);

    /// <summary>The Settings selector: swap in place (System re-reads the OS theme and accent now), persist, cross-fade.</summary>
    public void SetMode(ThemeMode mode)
    {
        bool live = Switch(KindOf(mode));
        if (mode == ThemeMode.System) _ports.ApplySystemAccent(allowPlainFallback: true);
        _ports.StoredMode = (int)mode;
        if (live) _transition?.Invoke(TransitionMs);
    }

    /// <summary>Flip light/dark in place and persist the EXPLICIT mode.</summary>
    public void ToggleMode()
    {
        var next = _ports.CurrentKind == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
        bool live = Switch(next);
        _ports.StoredMode = (int)(next == ThemeKind.Dark ? ThemeMode.Dark : ThemeMode.Light);
        if (live) _transition?.Invoke(TransitionMs);
    }

    /// <summary>The OS colours changed: the accent follows always; the theme follows only while the mode is System.</summary>
    public void OnSystemColorsChanged()
    {
        _ports.ApplySystemAccent(allowPlainFallback: false);
        if (Mode != ThemeMode.System) return;
        var kind = KindOf(ThemeMode.System);
        if (kind == _ports.CurrentKind) return;
        if (Switch(kind)) _transition?.Invoke(TransitionMs);
    }

    /// <summary>Apply <paramref name="kind"/>: in place (true: the caller asks for the live cross-fade), or on the theme
    /// transaction when the kind changes more than colours (false: the transaction dissolves on its own).</summary>
    bool Switch(ThemeKind kind)
    {
        if (kind != _ports.CurrentKind && _ports.KindChangeIsStructural(kind))
        {
            _ports.ApplyKindInTransaction(kind);
            return false;
        }
        _ports.ApplyKind(kind);
        return true;
    }

    /// <summary>The OS theme is read only for System: an explicit mode never touches the registry.</summary>
    ThemeKind KindOf(ThemeMode mode)
        => Appearance.KindFor(mode, mode == ThemeMode.System && _ports.SystemUsesLightTheme());
}

/// <summary>The process-wide appearance service. See the file header; the rules are <see cref="AppearanceModel"/>'s.</summary>
public static class Appearance
{
    static readonly AppearanceModel s_model = new(new LiveAppearancePorts());

    /// <summary>The theme the tokens show now.</summary>
    public static ThemeKind Kind => s_model.Kind;

    /// <summary>The persisted light / dark / system preference.</summary>
    public static ThemeMode Mode => s_model.Mode;

    /// <summary>The stored <c>theme.mode</c> int as a mode: 1 Light, 2 Dark, anything else follows the OS.</summary>
    public static ThemeMode ModeOf(int stored) => stored switch
    {
        1 => ThemeMode.Light,
        2 => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    /// <summary>The kind a mode shows: Light and Dark are explicit; System is the OS theme.</summary>
    public static ThemeKind KindFor(ThemeMode mode, bool systemUsesLight) => mode switch
    {
        ThemeMode.Light => ThemeKind.Light,
        ThemeMode.Dark => ThemeKind.Dark,
        _ => systemUsesLight ? ThemeKind.Light : ThemeKind.Dark,
    };

    /// <summary>Before the window comes up (no startup flash): the persisted preference, falling back to the live OS theme.</summary>
    public static void Seed(ThemeMode mode) => s_model.Seed(mode);

    /// <summary>The shell test host's seed: a fixed kind, no OS read.</summary>
    public static void Seed(ThemeKind fixedKind) => s_model.Seed(fixedKind);

    /// <summary>Settings > Appearance > Theme.</summary>
    public static void SetMode(ThemeMode mode) => s_model.SetMode(mode);

    /// <summary>The ONE theme-flip verb: the palette row, the profile menu's Theme row (and Settings › App mode sets the mode itself).</summary>
    public static void ToggleMode() => s_model.ToggleMode();

    /// <summary><c>FluentApp.SystemColorsChanged</c>: follow the OS accent always and the OS theme while the mode is System.</summary>
    public static void OnSystemColorsChanged() => s_model.OnSystemColorsChanged();

    /// <summary>The root factory: the engine's boot override of the accent is released (see the file header).</summary>
    public static void OnWindowCreated() => s_model.OnWindowCreated();

    /// <summary>Feeds the OS accent to the theme host and installs the active definition again when it changed (see the file
    /// header). Public so a fact can drive the live path with a chosen OS answer; <see cref="OnSystemColorsChanged"/> reads the OS.
    /// <paramref name="plain"/> is used only with <paramref name="allowPlainFallback"/>, when the OS has no <paramref name="ramp"/>.</summary>
    public static void FollowOsAccent(AccentRamp? ramp, ColorF? plain, bool allowPlainFallback)
    {
        if (Wavee.Theming.ThemeHost.FeedOsAccent(ramp, plain, allowPlainFallback)) Design.SeedPalette(Tok.Theme);
    }

    /// <summary>Hand over the host's <c>ThemeControl.Request</c> (read with <c>UseContext</c> by the frame and the Settings
    /// page); the live verbs ask it for the cross-fade.</summary>
    public static void BindTransition(Action<float>? request) => s_model.BindTransition(request);
}

/// <summary>The process's side of <see cref="IAppearancePorts"/>: the token set, the OS, the settings store.</summary>
sealed class LiveAppearancePorts : IAppearancePorts
{
    public ThemeKind CurrentKind => Tok.Theme;

    public int StoredMode
    {
        get => Platform.Settings.Get(Platform.Keys.ThemeMode);
        set => Platform.Settings.Set(Platform.Keys.ThemeMode, value);
    }

    public bool SystemUsesLightTheme() => FluentApp.SystemUsesLightTheme();

    // Wavee's own ramp (G-196), not the engine's default one.
    public void ApplyKind(ThemeKind kind) => Design.SeedPalette(kind);

    public bool KindChangeIsStructural(ThemeKind kind)
    {
        var definition = Wavee.Theming.ThemeHost.ActiveDefinition ?? Design.Definition;
        return definition.Materials.For(kind) != definition.Materials.For(Tok.Theme);
    }

    public void ApplyKindInTransaction(ThemeKind kind) => _ = Wavee.Theming.ThemeHost.SwitchKind(kind);

    public void ApplySystemAccent(bool allowPlainFallback)
    {
        ReleaseAccentOverride();
        Appearance.FollowOsAccent(FluentApp.SystemAccentRamp(), allowPlainFallback ? FluentApp.SystemAccent() : null, allowPlainFallback);
    }

    public void ReleaseAccentOverride() => Tok.SetAccent((ColorF?)null);
}
