using System;
using System.IO;

namespace Wavee;

/// <summary>
/// The deployed Wavee app icon — <c>assets/AppIcon/appicon.ico</c> beside the exe. One resolver for the window class
/// (engine <c>LoadAppIcon</c>), the unpackaged AUMID / protocol DefaultIcon, and Jump List glyphs, so those surfaces
/// cannot drift onto a missing PE resource and paint the generic Windows placeholder.
/// </summary>
static class WaveeAppIcon
{
    /// <summary>Absolute path to the deployed multi-res <c>.ico</c>, or null when the file is absent (dev/test without
    /// content copy). Fail-soft: callers skip the icon rather than throw.</summary>
    public static string? Path()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "AppIcon", "appicon.ico");
            return File.Exists(ico) ? ico : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
