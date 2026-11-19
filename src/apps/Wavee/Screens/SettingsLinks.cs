// ── Screens/SettingsLinks.cs — the one seam that links to a dial (or a category) of the Settings page ─────────────────────────────
//
// Role: CORE (settings rework phase 3). A hint's link, a confirmation's "Change", a glance tile and a Recently changed row name a dial
// and open it through this one door: `wavee://open?route=settings&arg=<dialId>` (or `arg=<category>`), the deep-link form of design 4.9
// that phase 2's parser resolves (category, parent expander, the row glided into view and highlighted). Until it lands, the page opens
// on its default area for an arg it does not know.

namespace Wavee;

/// <summary>Links into the Settings page (see the file header).</summary>
public static class SettingsLinks
{
    /// <summary>The deep link to <paramref name="dialId"/> (a registry id, or a category slug).</summary>
    public static string Dial(string dialId) => "wavee://open?route=settings&arg=" + Uri.EscapeDataString(dialId);

    /// <summary>Follow the deep link to <paramref name="dialId"/> (a registry id, or a category slug).</summary>
    public static void Open(string dialId) => Shell.OpenLink(Dial(dialId));
}
