// ── Shell/DeckPrefs.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// the now-playing PLAYER STYLE's verbs and the in-place picker's state (2026-11-08 redesign): every write a view makes to the style or a
// finish goes through here, attributed to the surface that made it. A view of any theme reads `Rail.PlayerPrefs` and `StylePicker` and calls
// these; it never names a settings key, an epoch or a log category.
//
// Role: CORE
// Spec: the approved design `canvas-design/project/Styles.dc.html` (the chip, the picker, the eleven styles, the two finishes); the store and
// the clamps are `Rail.PlayerPrefs` / `Prefs.NpvPlayer` (the store is the truth, the epoch is the update edge, every writer clamps, persists and
// bumps ONCE).
//
// THE PICKER. One chip next to "Now playing" names the style. A click opens the picker IN PLACE: the art becomes a carousel of the styles (the
// focused one live at 0.84, its neighbours peeking at 0.66), the header becomes "Player style" with Cancel and Done, and the name, the one-line
// description, the finish swatches (Vinyl, Pocket) and the dots sit under the art. Moving between styles is a DRAFT (`Focus`): nothing is
// stored until Done. A finish pick is stored at once (the live face shows it) and Cancel puts the finishes back as they were when the picker
// opened, with the style. Escape is Cancel. The state is the shell's one picker (one now-playing hero is mounted at a time; the Compact sheet's
// header and its art are two components that must agree).

using FluentGpu.Signals;

namespace Wavee;

public static partial class Shell
{
    /// <summary>Which surface wrote a player-style preference. Always logged (`npv` category): without it "the style changed" is
    /// unattributable.</summary>
    public enum DeckPrefSource : byte { Picker, Settings, Palette }

    public static class DeckPrefs
    {
        /// <summary>The attribution string the `npv` log carries for <paramref name="source"/>.</summary>
        public static string SourceName(DeckPrefSource source) => source switch
        {
            DeckPrefSource.Picker => Rail.NpvDiagnostics.SourcePicker,
            DeckPrefSource.Settings => Rail.NpvDiagnostics.SourceSettings,
            _ => Rail.NpvDiagnostics.SourcePalette,
        };

        /// <summary>Pick a player style (Settings; the picker commits through <see cref="StylePicker.Done"/>).</summary>
        public static void SetStyle(int id, DeckPrefSource source) => Rail.PlayerPrefs.SetStyle(id, SourceName(source));

        /// <summary>The palette's "next style": wraps at the end of the table.</summary>
        public static void NextStyle(DeckPrefSource source) => Rail.PlayerPrefs.NextStyle(SourceName(source));

        /// <summary>Pick one style's finish (the index is clamped against its finishes; a style without one ignores it).</summary>
        public static void SetFinish(int styleId, int index, DeckPrefSource source) => Rail.PlayerPrefs.SetFinish(styleId, index, SourceName(source));
    }

    /// <summary>The in-place style picker's state and verbs (see the file header). UI thread only.</summary>
    public static class StylePicker
    {
        /// <summary>Is the picker open (the art is the carousel, the header reads "Player style")?</summary>
        public static readonly Signal<bool> Open = new(false);

        /// <summary>The focused style while open: the draft Done commits.</summary>
        public static readonly Signal<int> Focus = new(Rail.PlayerCatalog.DefaultStyleId);

        static int s_vinylBefore, s_pocketBefore;

        /// <summary>Reactive: the style the art centres — the focused one while picking, the stored one otherwise. The chip names it.</summary>
        public static int Shown() => Open.Value ? Focus.Value : Rail.PlayerPrefs.Style();

        /// <summary>The chip: open the picker on the stored style, remembering the finishes for Cancel.</summary>
        public static void Begin()
        {
            if (Open.Peek()) return;
            s_vinylBefore = Rail.PlayerPrefs.FinishPeek(Rail.PlayerCatalog.Vinyl);
            s_pocketBefore = Rail.PlayerPrefs.FinishPeek(Rail.PlayerCatalog.Pocket);
            Focus.Value = Rail.PlayerPrefs.StylePeek();
            Open.Value = true;
        }

        /// <summary>The arrows: one style back (−1) or on (+1), round the ring.</summary>
        public static void Step(int delta)
        {
            if (!Open.Peek()) return;
            int n = Rail.PlayerCatalog.Count;
            Focus.Value = ((Focus.Peek() + delta) % n + n) % n;
        }

        /// <summary>A dot or a peeking neighbour: focus that style.</summary>
        public static void FocusOn(int id)
        {
            if (Open.Peek() && Rail.PlayerCatalog.IsStyleId(id)) Focus.Value = id;
        }

        /// <summary>A finish swatch of the focused style: stored at once (the live face shows it); Cancel restores it.</summary>
        public static void SetFinish(int index)
        {
            if (Open.Peek()) DeckPrefs.SetFinish(Focus.Peek(), index, DeckPrefSource.Picker);
        }

        /// <summary>Done: store the focused style and close.</summary>
        public static void Done()
        {
            if (!Open.Peek()) return;
            Rail.PlayerPrefs.SetStyle(Focus.Peek(), Rail.NpvDiagnostics.SourcePicker);
            Open.Value = false;
            Rail.NpvDiagnostics.PickerClosed(committed: true);
        }

        /// <summary>Cancel (and Escape, and the hero going away while picking): the stored style was never changed; the finishes go back to
        /// what they were when the picker opened.</summary>
        public static void Cancel()
        {
            if (!Open.Peek()) return;
            Rail.PlayerPrefs.SetFinish(Rail.PlayerCatalog.Vinyl, s_vinylBefore, Rail.NpvDiagnostics.SourcePicker);
            Rail.PlayerPrefs.SetFinish(Rail.PlayerCatalog.Pocket, s_pocketBefore, Rail.NpvDiagnostics.SourcePicker);
            Focus.Value = Rail.PlayerPrefs.StylePeek();
            Open.Value = false;
            Rail.NpvDiagnostics.PickerClosed(committed: false);
        }
    }
}
