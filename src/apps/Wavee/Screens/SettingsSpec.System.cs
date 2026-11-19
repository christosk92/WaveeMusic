// ── Screens/SettingsSpec.System.cs — Startup & system: how Wavee starts, closes and runs on this PC (the dials and their verbs) ────
//
// Role: CORE (settings rework phase 1). Startup (open with Windows, start hidden) · Window and notification area (the tray icon, what
// closing and minimizing do) · Language · Links (the spotify: handler) · Graphics (the render GPU). Every writer is a `SettingsVerbs`
// member: no view persists, registers, enumerates or asks the OS itself.

using FluentGpu.Rhi.D3D12;
using K = Wavee.SettingsRowKind;

namespace Wavee;

public static partial class SettingsSpec
{
    static SettingsCategory SystemCategory() => new("system", Strings.Settings.Categories.System.Title,
        Strings.Settings.Categories.System.Strip, Strings.Settings.Categories.System.Subtitle, "ThisPc",
    [
        Group("startup", Strings.Settings.Groups.System.Startup, null,
            Make("system.startOnLogin", K.Toggle, Strings.Settings.General.StartOnLogin,
                SettingsWriter.OfToggle("tray.startOnLogin", SettingsVerbs.SetStartOnLogin), legacy: ["startOnLogin"], glyph: "Contact",
                captionKey: Strings.Settings.General.StartOnLoginSub, value: BoolOf(Platform.Keys.StartOnLogin), aliases: ["autostart"]),
            Make("system.startHidden", K.Toggle, Strings.Settings.General.Tray.StartHidden,
                SettingsWriter.OfToggle("tray.startHidden", on => SettingsVerbs.SetTrayFlag(Platform.Keys.TrayStartHidden, on)),
                legacy: ["startHidden"], glyph: "RevealPassword",
                captionKey: Strings.Settings.General.Tray.StartHiddenSub, value: BoolOf(Platform.Keys.TrayStartHidden),
                enabled: f => Tray.StartHiddenAvailable(f.StartOnLogin, f.TrayMode)
                    ? SettingsEnablement.On
                    : SettingsEnablement.Off(!Tray.HideModesAllowed(f.TrayMode)
                        ? Strings.Settings.General.Tray.NeedsIcon : Strings.Settings.General.Tray.NeedsStartOnLogin))),

        Group("window", Strings.Settings.Groups.System.Window, Strings.Settings.General.Tray.Subtitle,
            Make("system.tray.icon", K.Choice, Strings.Settings.General.Tray.IconMode,
                SettingsWriter.OfChoice("tray.iconMode", SettingsVerbs.SetTrayIconMode), legacy: ["trayIcon"], glyph: "Pin",
                caption: f => f.TrayMode == Tray.IconMode.Never ? Strings.Settings.General.Tray.IconNeverSub : Strings.Settings.General.Tray.IconModeSub,
                value: s => (int)Tray.ModeFrom(s.Get(Platform.Keys.TrayIconMode)), contexts: [SettingsSurfaces.TrayMenu], aliases: ["tray"],
                choices: [Strings.Settings.General.Tray.IconAlways, Strings.Settings.General.Tray.IconWhileHidden, Strings.Settings.General.Tray.IconNever]),
            Make("system.tray.closeToTray", K.Toggle, Strings.Settings.General.Tray.CloseToTray,
                SettingsWriter.OfToggle("tray.closeToTray", on => SettingsVerbs.SetTrayFlag(Platform.Keys.TrayCloseToTray, on)),
                legacy: ["closeToTray"], glyph: "ChromeClose",
                captionKey: Strings.Settings.General.Tray.CloseToTraySub, value: BoolOf(Platform.Keys.TrayCloseToTray),
                enabled: HideAllowed, aliases: ["tray"]),
            Make("system.tray.minimizeToTray", K.Toggle, Strings.Settings.General.Tray.MinimizeToTray,
                SettingsWriter.OfToggle("tray.minimizeToTray", on => SettingsVerbs.SetTrayFlag(Platform.Keys.TrayMinimizeToTray, on)),
                legacy: ["minimizeToTray"], glyph: "ChromeMinimize",
                captionKey: Strings.Settings.General.Tray.MinimizeToTraySub, value: BoolOf(Platform.Keys.TrayMinimizeToTray),
                enabled: HideAllowed, aliases: ["tray"])),

        Group("language", Strings.Settings.Groups.System.Language, Strings.Settings.Language.Subtitle,
            Make("system.language", K.Choice, Strings.Settings.Language.Label,
                SettingsWriter.OfChoice("language.set", SettingsVerbs.SetLanguage), legacy: ["language"], glyph: "LocaleLanguage",
                captionKey: Strings.Settings.Language.RestartSub,
                value: s => Settings.Language.IndexOf(s.Get(Platform.Keys.UiCulture)),
                choices: [Strings.Settings.Language.System, Strings.Settings.Language.EnglishUs, Strings.Settings.Language.Dutch, Strings.Settings.Language.Korean])),

        Group("links", Strings.Settings.Groups.System.Links, Strings.Settings.Links.Subtitle,
            // A packaged build's manifest owns its protocols, so the dial says Windows manages it.
            Make("system.spotifyLinks", K.Toggle, Strings.Settings.Links.Spotify,
                SettingsWriter.OfToggle("links.spotify", SettingsVerbs.SetHandleSpotifyLinks), legacy: ["spotifyLinks"], glyph: "OpenInNewWindow",
                captionKey: Strings.Settings.Links.SpotifySub, value: BoolOf(Platform.Keys.HandleSpotifyLinks),
                enabled: f => f.Packaged ? SettingsEnablement.Off(Strings.Settings.General.SpotifyLinksPackagedSub) : SettingsEnablement.On)),

        Group("graphics", Strings.Settings.Groups.System.Graphics, Strings.Settings.Gpu.Subtitle,
            // The index is into the adapters the card enumerated when it mounted (SettingsFacts.Gpus); 0 is Automatic.
            Make("system.gpu", K.Choice, Strings.Settings.Gpu.Label,
                SettingsWriter.OfChoice("gpu.pick", SettingsVerbs.PickGpu), legacy: ["preferredGpu"], glyph: "Device",
                captionKey: Strings.Settings.Gpu.RestartSub,
                value: s => SettingsFacts.GpuIndexOf(SettingsFacts.Gpus, s.Get(Platform.Keys.PreferredGpuLuid), s.Get(Platform.Keys.PreferredGpuName)),
                aliases: ["gpu"])),
    ]);

    /// <summary>The close/minimize-to-tray dials need an icon: with "Never" they grey and say so.</summary>
    static SettingsEnablement HideAllowed(ISettingsFacts f)
        => Tray.HideModesAllowed(f.TrayMode) ? SettingsEnablement.On : SettingsEnablement.Off(Strings.Settings.General.Tray.NeedsIcon);
}

public static partial class SettingsVerbs
{
    /// <summary>Pick the app language (the belt-and-braces behind the combo's own disabled items). Takes effect on restart.</summary>
    public static bool SetLanguage(int index)
    {
        if (!Settings.Language.CanPick(index)) return false;
        Platform.Settings.Set(Platform.Keys.UiCulture, Settings.Language.Codes[index]);
        return true;
    }

    /// <summary>The opt-in <c>spotify:</c> handler, applied AT THE TOGGLE (the next link opens here / the scheme goes straight back).</summary>
    public static bool SetHandleSpotifyLinks(bool on)
    {
        Platform.Settings.Set(Platform.Keys.HandleSpotifyLinks, on);
        Settings.SyncSpotifyScheme(on);
        return true;
    }

    /// <summary>Where the notification-area icon shows (0 always · 1 while hidden · 2 never). Every tray writer calls
    /// <c>Tray.Host.OnSettingsChanged()</c>: icon presence, the foreground hook and — through it — the startup registration follow.</summary>
    public static bool SetTrayIconMode(int index)
    {
        if (index is < 0 or > 2) return false;
        Platform.Settings.Set(Platform.Keys.TrayIconMode, index);
        Tray.Host.OnSettingsChanged();
        return true;
    }

    /// <summary>Close / minimize to tray, start hidden: persist and tell the tray host.</summary>
    public static bool SetTrayFlag(SettingKey<bool> key, bool on)
    {
        Platform.Settings.Set(key, on);
        Tray.Host.OnSettingsChanged();
        return true;
    }

    /// <summary>Start with Windows. <c>Tray.Host.OnSettingsChanged</c> calls <c>Shell.SyncStartupRegistration</c> first; it is
    /// called directly too so the Run value follows even before the tray host has booted.</summary>
    public static bool SetStartOnLogin(bool on)
    {
        Platform.Settings.Set(Platform.Keys.StartOnLogin, on);
        Shell.SyncStartupRegistration();
        Tray.Host.OnSettingsChanged();
        return true;
    }

    /// <summary>Pick the render GPU: index 0 (or out of range) is Automatic; otherwise the adapter the card enumerated at mount.
    /// Persists the LUID and the name and live-applies through the engine's device-reset path.</summary>
    public static bool PickGpu(int index)
    {
        var adapters = SettingsFacts.Gpus;
        if (index <= 0 || index > adapters.Count)
        {
            Platform.Settings.Set(Platform.Keys.PreferredGpuLuid, 0L);
            Platform.Settings.Set(Platform.Keys.PreferredGpuName, "");
            GpuAdapterInfo.RequestAdapterSwitch(0L);
            return false;
        }
        var a = adapters[index - 1];
        Platform.Settings.Set(Platform.Keys.PreferredGpuLuid, a.Luid);
        Platform.Settings.Set(Platform.Keys.PreferredGpuName, a.Name);
        GpuAdapterInfo.RequestAdapterSwitch(a.Luid);
        return false;
    }
}
