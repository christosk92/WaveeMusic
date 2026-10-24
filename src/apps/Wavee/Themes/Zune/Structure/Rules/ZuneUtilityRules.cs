// ── Themes/Zune/Structure/Rules/ZuneUtilityRules.cs — the numbers of Zune's History and Customize home screens (audit B5a, V20) ───────────────
//
// No board draws either screen (owner Q18): they are Zune's utility screens, in the voice the Settings boards give the family (Z-Settings-*): the
// panorama title over the page (`ZuneSettings.ScreenTitle`, the settings record's title zone and gutter), the meta words in the 13 tertiary voice,
// the verbs as lowercase words, flat rows that answer hover with the 5 % wash (no plates, no strokes, no pills), the 22 light group heads.
//
// WVTH003 allows literals only here; every width decision is the settings tier record's (`ZuneSettingsTiers`, by CONTENT tier).

namespace Wavee.Themes.Zune;

/// <summary>The numbers of Zune's History and Customize home screens.</summary>
public static class ZuneUtilityRules
{
    // ── the head (both screens) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The gap between the title and the meta row, and between the meta row and the tools under it (the find field, the word rails).</summary>
    public const float HeadGap = 12f;

    /// <summary>The air between the head and the list.</summary>
    public const float HeadBottom = 24f;

    /// <summary>The list's foot (the shell clips above the player bar; this is breathing room).</summary>
    public const float PageBottom = 32f;

    // ── History ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The find field's width beside the rails (it fills the row at Compact).</summary>
    public const float FindWidth = 280f;

    /// <summary>The chevron after a folded rail's word (Compact).</summary>
    public const float PickChevron = 10f;

    /// <summary>A visit row: its height (the 40 glyph lane plus 8 over and under), the glyph lane, the glyph, the gap between the lanes.</summary>
    public const float RowH = 56f, GlyphLane = 40f, Glyph = 16f, RowGap = 12f;

    /// <summary>The remove glyph and its box (Standard; the Compact target is the record's 40).</summary>
    public const float RemoveGlyph = 12f, RemoveBox = 32f;

    /// <summary>An unreachable visit (a page this build does not have) stays in the log at this opacity, inert.</summary>
    public const float InertRowOpacity = 0.6f;

    /// <summary>A day group: the gap between the head and its rows, and between groups.</summary>
    public const float GroupHeadGap = 4f, GroupGap = 24f;

    /// <summary>The gap between a group head's word and its count.</summary>
    public const float GroupCountGap = 12f;

    // ── Customize home ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A section row's height (a 40 target at every tier), the gap between its parts, the gripper glyph.</summary>
    public const float CustomizeRowH = 56f, CustomizeRowGap = 12f, CustomizeGrip = 16f;

    /// <summary>A hidden section's row ink.</summary>
    public const float CustomizeHiddenOpacity = 0.5f;
}
