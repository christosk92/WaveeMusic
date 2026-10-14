// ── Shell/DeckPrefs.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the now-playing DECK's content-preference verbs (themes plan C3, X10): every write a view makes to the player style, an
// option of that style, the Cover/Player presentation and the style flyout's open state goes through here, attributed to the
// surface that made it. A view of any theme draws `Rail.PlayerPrefs`' reactive reads and calls these; it never names a
// settings key, an epoch or a log category.
//
// Role: CORE
// Spec: ch 21 §6.3, ch 00 §9.5; the store and the clamps are `Rail.PlayerPrefs` / `Prefs.NpvPlayer` (the store is the truth,
// the epoch is the update edge, every writer clamps, persists and bumps ONCE)
//
// Before this, five surfaces called `Rail.PlayerPrefs.Set*` with a string they picked (`NpvDiagnostics.SourceFlyout`, …) and
// the style flyout wrote the open flag and built its per-row mirror signals inline.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    /// <summary>Which surface wrote a deck preference. Always logged (`npv` category): without it "the style changed" is
    /// unattributable.</summary>
    public enum DeckPrefSource : byte { Header, Flyout, ArtMenu, Settings, Palette }

    public static class DeckPrefs
    {
        /// <summary>The attribution string the `npv` log carries for <paramref name="source"/>.</summary>
        public static string SourceName(DeckPrefSource source) => source switch
        {
            DeckPrefSource.Header => Rail.NpvDiagnostics.SourceHeader,
            DeckPrefSource.Flyout => Rail.NpvDiagnostics.SourceFlyout,
            DeckPrefSource.ArtMenu => Rail.NpvDiagnostics.SourceArtMenu,
            DeckPrefSource.Settings => Rail.NpvDiagnostics.SourceSettings,
            _ => Rail.NpvDiagnostics.SourcePalette,
        };

        /// <summary>Pick a player style. Picking a player ALSO shows it: a style chosen while the cover was up flips the
        /// presentation to Player in the same write.</summary>
        public static void SetStyle(int id, DeckPrefSource source) => Rail.PlayerPrefs.SetStyle(id, SourceName(source));

        /// <summary>The palette's "next style": wraps at the end of the table.</summary>
        public static void NextStyle(DeckPrefSource source) => Rail.PlayerPrefs.NextStyle(SourceName(source));

        /// <summary>Pick one option of a style (the index is clamped against the option's choices).</summary>
        public static void SetChoice(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option, int index,
            DeckPrefSource source)
            => Rail.PlayerPrefs.SetChoice(in preset, in option, index, SourceName(source));

        /// <summary>Show the cover (<see cref="Rail.PlayerPrefs.Cover"/>) or the player (<see cref="Rail.PlayerPrefs.Player"/>).</summary>
        public static void SetPresentation(int presentation, DeckPrefSource source)
            => Rail.PlayerPrefs.SetPresentation(presentation, SourceName(source));

        /// <summary>Flip cover ↔ player.</summary>
        public static void TogglePresentation(DeckPrefSource source)
            => Rail.PlayerPrefs.TogglePresentation(SourceName(source));

        /// <summary>Open the style flyout (the SAME popup the gear owns, from wherever it was asked).</summary>
        public static void OpenStyleFlyout() => Rail.PlayerPrefs.StyleFlyoutOpen.Value = true;

        /// <summary>The gear: open the flyout if it is closed, close it if open.</summary>
        public static void ToggleStyleFlyout()
            => Rail.PlayerPrefs.StyleFlyoutOpen.Value = !Rail.PlayerPrefs.StyleFlyoutOpen.Peek();

        /// <summary>A controlled mirror of one option's choice for a segmented row: the click writes it (the pill moves at
        /// once), then the row's change handler writes the store. A fresh one per render ON PURPOSE: the option SET changes
        /// with the style, so a hook per option would be a conditional hook. The store, not the mirror, is the truth.</summary>
        public static Signal<int> ChoiceMirror(in Rail.PlayerCatalog.Preset preset, in Rail.PlayerCatalog.OptionDef option)
            => new(Rail.PlayerPrefs.Choice(in preset, in option));
    }
}
