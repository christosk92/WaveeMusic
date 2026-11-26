// ── Themes/Zune/Structure/Rules/ZuneActivityRules.cs — the numbers of the band's library activity word (#240 UX S1, the Zune voice) ──────
//
// Data, not decisions: `ZuneBandActivity` reads them (WVTH003 allows literals only here). Zune has no sidebar: the band is the shell, so the chip
// is a quiet word at the end of row 1, before the pins (boards240/ThemesLocal draws the Zune voice: lowercase words, no plates).

namespace Wavee.Themes.Zune;

/// <summary>The constants of the band's library activity word.</summary>
public static class ZuneActivityRules
{
    /// <summary>The word's slot (the click target) and its words.</summary>
    public const string SlotKey = "zune-band-activity-slot", Key = "zune-band-activity";

    /// <summary>The slot's reserved width at Standard and Huge: every state's words fit it, so a state change never resizes it.</summary>
    public const float Width = 220f;

    /// <summary>The slot's width at Compact and Small (the mark alone, the Compact target).</summary>
    public const float NarrowWidth = 40f;

    /// <summary>The gap between the mark and the words, and the marks' size (the ring, the tick; the dot is Common's).</summary>
    public const float Gap = 8f, Mark = 12f;
}
