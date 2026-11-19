// ── Home/Board/HomeCustomize.cs — the one switch for Home's customization (owner, 2026-11-18) ─────────────────────────────────────────────
//
// Role: CORE. Owner, 2026-11-18: "can we for now disable home customization, we have the building blocks but dont actually allow it yet. i wanna
// work on it a bit more but dont wanna hold the release"; asked what exactly: "drag, resize and tile options are fine, so does pin and unpin. its
// mostly about edit and widgets". So two parts of Home's customization are switched OFF for the release, in every theme, and nothing is deleted:
//
//   EditMode    Classic's Edit Home, Zune's customize (the "edit" word, its app bar, a held press, a right-click or Enter on a tile), every
//               "Customize Home" (the facets' link, the palette command, `HomeVerbs.OpenCustomize`, a tile menu's way in) and the inspector's
//               `home do=edit`. `HomeBoardView.Editing` never turns on.
//   AddSurface  WinUI's "Add widgets" (its button, its band, its first-run tip, "/"), Classic's add rows, its find strip and Home's ⋯ "Show on
//               Home", Zune's all items (the round arrow, the app bar's add, "/"), and every add from the catalogue (`HomeBoardVerbs.Add`, the
//               inspector's `home do=add|surface|search`). With it off nothing a user can do may need it to come back: a tile menu offers no
//               "Remove" / "Hide" of a widget or a section (a pin's "Unpin" stays: Pin to Home brings it back; `HomeBoardVerbs.CanRemove`).
//
// What stays on everywhere it works without the edit mode: drag reorder, resize, tile options, pin and unpin, the default board's own widgets
// (`HomeDoc.Default`) and any saved or migrated layout, shown as saved.
//
// TO TURN A PART BACK ON, set its member below to true: the core verbs, the inspector and every theme read these two and nothing else. They are
// properties, not constants (a constant false makes the guarded code unreachable, and warnings are errors).

namespace Wavee;

/// <summary>Which parts of Home's customization this build offers (owner, 2026-11-18: both off for the release; the building blocks stay).</summary>
public static class HomeCustomize
{
    /// <summary>The edit mode (Classic's Edit Home, Zune's customize) and every way into it ("Customize Home"). Off: the mode never turns on.</summary>
    public static bool EditMode { get; } = false;

    /// <summary>The add surface (WinUI's add band, Classic's add rows and find, Zune's all items) and every add from the catalogue. Off: no way into
    /// it is drawn, and nothing that only it could undo is offered (removing or hiding a widget or a section).</summary>
    public static bool AddSurface { get; } = false;
}
