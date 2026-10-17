// ── Wavee.ShellTests/ZuneHost.cs — the app mounted under Zune (P5 Z0) ────────────────────────────────────────────────────────────
//
// Zune is Developer-only, so a fact that mounts the app under it turns Developer mode on, installs Classic and Zune (as App.cs does),
// stores `theme.id = zune` so the install activates Zune, and seeds the palette with Zune's definition (an install activates the
// composition; the token set is the appearance seed's). Whatever the fact does, Classic is put back alone and Developer mode off.
//
// Until Z9, Zune's structure registers Classic's views as its stand-in (`ZuneStructure.cs`): the mounted app is Classic's views under
// Zune's style, policies, tiers and capabilities. The harness is written against Zune's facts and records, so it holds as Zune's own
// views replace the stand-in family by family.

using FluentGpu.Dsl;
using FluentGpu.Theming;
using Wavee.Themes.Classic;
using Wavee.Themes.Zune;
using Wavee.Theming;

namespace Wavee.ShellTests;

/// <summary>Mounting the app under Zune.</summary>
public static class ZuneHost
{
    /// <summary>Mount the app under Zune at <paramref name="widthDip"/> x <paramref name="heightDip"/>, run <paramref name="body"/>, then put
    /// Classic back alone with Developer mode off.</summary>
    public static void Mounted(ShellHost host, Action<Shell.HeadlessShell> body, float widthDip, float heightDip)
    {
        host.Run(Activate);
        try { host.Mounted(body, widthDip, heightDip); }
        finally { host.Run(Restore); }
    }

    /// <summary>Developer mode on, Classic and Zune installed, Zune active with its definition installed. UI thread.</summary>
    public static void Activate()
    {
        Diagnostics.InspectKit.ActivateTheme(ZuneTheme.Id);   // the app's own activation (Screens/Diagnostics.Inspect.Kit.cs)
    }

    /// <summary>Developer mode off, Classic alone and active. UI thread.</summary>
    public static void Restore()
    {
        Platform.Developer.Enabled.Value = false;
        ClassicAlt.RestoreClassic();
    }
}
