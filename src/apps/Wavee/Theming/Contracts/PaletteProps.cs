// ── Theming/Contracts/PaletteProps.cs — the props of `Shell.CommandPalette` (themes plan 6.0, T4c) ────────────────────────
//
// THE PALETTE'S MODEL IS CORE, ITS PICTURE IS THE THEME'S. The command table (the builtin rows, in declaration order = the empty-query
// order; the registry merge; the scoring ladder; the `>` commands-only prefix; the "Search for ..." row), the open flag (Ctrl+K
// toggles it) and what Enter does are `Shell.CommandTable`, `Shell.PaletteOpen` and `Shell.InvokePaletteEntry`. The theme draws
// the lane, the popup's anchor and the card, and runs the keyboard navigation over a SESSION the core hands it.
//
//   · `Popup` is the engine popup control, wired to the open flag by the host (a controlled signal, light-dismiss, a focus trap):
//     the theme gives it the lane's anchor and the card, and places what comes back. The popup mounts the card FRESH per open, so
//     the query and the selection reset for free.
//   · `NewSession` builds the card's session once per mount: the index, the result array and the catalog row are allocated there
//     and nowhere else (no per-keystroke allocation beyond the query's own lowercase copy).

using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>One open palette: the command index and the reusable result array. The card owns it for the length of one open.</summary>
public interface IPaletteSession
{
    /// <summary>Filter the index by <paramref name="query"/> into the session's results; returns how many rows match. The empty
    /// query answers the builtin table in declaration order.</summary>
    int Filter(string query);

    /// <summary>The icon glyph of result <paramref name="index"/> (valid below the last <see cref="Filter"/>'s count).</summary>
    string GlyphAt(int index);

    /// <summary>The label of result <paramref name="index"/>.</summary>
    string LabelAt(int index);

    /// <summary>Close the palette and run result <paramref name="index"/>; an out-of-range index does nothing.</summary>
    void Invoke(int index);
}

/// <summary>The props of <see cref="ViewKeys.ShellCommandPalette"/>. Cached statics of the core, so an equal record skips the template.</summary>
public sealed record PaletteProps
{
    /// <summary>The popup control wired to the palette's open flag: <c>(anchor, content) => popup</c>.</summary>
    public required Func<Element, Func<Element>, Element> Popup { get; init; }

    /// <summary>Builds one session (called once per card mount).</summary>
    public required Func<IPaletteSession> NewSession { get; init; }

    /// <summary>Close the palette (Escape).</summary>
    public required Action Close { get; init; }
}
