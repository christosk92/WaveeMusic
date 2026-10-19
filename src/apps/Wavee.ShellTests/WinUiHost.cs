// ── Wavee.ShellTests/WinUiHost.cs — the app mounted under WinUI (P6 W0) ──────────────────────────────────────────────────────────────
//
// WinUI is Developer-only, so a fact that mounts the app under it turns Developer mode on and activates WinUI the way the `--inspect` server
// does (`InspectKit.ActivateTheme`: the shipped compositions installed, `theme.id = winui`, its definition seeded). Whatever the fact does,
// Classic is put back alone and Developer mode off.
//
// Until W9, WinUI's structure registers Classic's views as its stand-in (`WinUiStructure.cs`): the mounted app is Classic's views under
// WinUI's style, policies, tiers and capabilities. The harness is written against WinUI's facts and records, so it holds as WinUI's own
// views replace the stand-in family by family.

using Wavee.Themes.WinUi;

namespace Wavee.ShellTests;

/// <summary>Mounting the app under WinUI.</summary>
public static class WinUiHost
{
    /// <summary>Mount the app under WinUI at <paramref name="widthDip"/> x <paramref name="heightDip"/>, run <paramref name="body"/>, then put
    /// Classic back alone with Developer mode off.</summary>
    public static void Mounted(ShellHost host, Action<Shell.HeadlessShell> body, float widthDip, float heightDip)
    {
        host.Run(Activate);
        try { host.Mounted(body, widthDip, heightDip); }
        finally { host.Run(Restore); }
    }

    /// <summary>Developer mode on, the shipped themes installed, WinUI active with its definition installed. UI thread.</summary>
    public static void Activate() => Diagnostics.InspectKit.ActivateTheme(WinUiTheme.Id);

    /// <summary>Developer mode off, Classic alone and active. UI thread.</summary>
    public static void Restore()
    {
        Platform.Developer.Enabled.Value = false;
        ClassicAlt.RestoreClassic();
    }
}
