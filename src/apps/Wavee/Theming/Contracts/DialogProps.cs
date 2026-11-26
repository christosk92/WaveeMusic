// ── Theming/Contracts/DialogProps.cs — the props of the dialog keys (themes plan 6.0, T11a) ─────────────────────────────────────
//
// THE DIALOGS THE APP DRAWS. A dialog is two things: the SHELL (the modal card, its command row, the default button, Enter and Escape, the
// focus trap and its return, the open and close motion: the engine's `ContentDialog`, or a `PopupChrome.Modal` popup) and the BODY the
// app puts in it. The core opens the shell and owns every decision (what the buttons do, busy and disabled states, light dismiss); these
// keys are the bodies and the two bare cards the app builds itself.
//
// WVTH006: sealed records, init-only members, no writable `Signal<T>` (an editable cell travels as a `Func<Signal<string>>`).

using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.DialogPlayLink"/>. Built per render by the core card, which holds the field, the lookup and the status.</summary>
public sealed record PlayLinkProps
{
    /// <summary>The field's placeholder (the module's own, or the generic one).</summary>
    public required string Placeholder { get; init; }

    /// <summary>The field's text cell (the core reads it for "can play"; Enter commits through <see cref="Submit"/>).</summary>
    public required Func<Signal<string>> Text { get; init; }

    /// <summary>The status line ("Looking up...", the settled match, a failure), reserved even when empty so the buttons never jump.</summary>
    public required string Status { get; init; }

    /// <summary>The start button is enabled (a non-empty link and no lookup running).</summary>
    public required bool CanPlay { get; init; }

    /// <summary>The failed input is a web url the user can open instead (the escape hatch).</summary>
    public required bool OffersBrowser { get; init; }

    /// <summary>Open the failed url in the browser.</summary>
    public required Action OpenInBrowser { get; init; }

    /// <summary>Cancel (closes the card).</summary>
    public required Action Cancel { get; init; }

    /// <summary>Ask the modules who can play the link.</summary>
    public required Action Submit { get; init; }
}

/// <summary>What the custom colours editor shows for its draft right now: every value already solved (#184). Read through binds, so a drag on
/// the picker repaints the wells and the preview without re-rendering the card.</summary>
/// <param name="Wells">The six wells' colours, in <see cref="ColourEditorProps.WellLabels"/>' order (a derived well shows the solver's value).</param>
/// <param name="Hex">The six wells' colours as <c>#RRGGBB</c>, or the auto label for a derived well.</param>
/// <param name="Ground">The preview's page.</param>
/// <param name="Well">The preview's lower plate (the player bar, the pane).</param>
/// <param name="Raised">The preview's cards.</param>
/// <param name="Ink">The preview's primary text.</param>
/// <param name="InkSecondary">The preview's secondary text.</param>
/// <param name="InkTertiary">The preview's tertiary text.</param>
/// <param name="AccentFill">The preview's accent fill (its button).</param>
/// <param name="AccentInk">The preview's accent as text (its link).</param>
/// <param name="OnAccent">The text on the accent fill.</param>
/// <param name="Hairline">The preview's divider and card ring.</param>
/// <param name="Note">What the floors changed ("Muted text was adjusted to stay readable."), or empty. Its line is reserved either way.</param>
public sealed record ColourEditorDraft(ColorF[] Wells, string[] Hex, ColorF Ground, ColorF Well, ColorF Raised, ColorF Ink, ColorF InkSecondary,
    ColorF InkTertiary, ColorF AccentFill, ColorF AccentInk, ColorF OnAccent, ColorF Hairline, string Note);

/// <summary>The props of <see cref="ViewKeys.DialogColourEditor"/>. Built by the core card host (open-time words and the verbs); the live values
/// travel as read signals.</summary>
public sealed record ColourEditorProps
{
    /// <summary>"Custom dark colours".</summary>
    public required string Title { get; init; }

    /// <summary>The six wells' labels (Background, Surface, Text, Muted text, Accent, Second surface), resolved.</summary>
    public required IReadOnlyList<string> WellLabels { get; init; }

    /// <summary>The well the picker edits.</summary>
    public required IReadSignal<int> Selected { get; init; }

    /// <summary>Make <c>i</c> the edited well.</summary>
    public required Action<int> Select { get; init; }

    /// <summary>The solved draft, for binds.</summary>
    public required IReadSignal<ColourEditorDraft> Draft { get; init; }

    /// <summary>The picker's colour cell (the edited well's colour; a pick writes the draft).</summary>
    public required Func<Signal<ColorF>> Picker { get; init; }

    /// <summary>Bumped when the picker must re-seed from <see cref="Picker"/> (another well selected, a start-from, a reset): the body keys the
    /// picker on it, since a colour picker seeds its hue and saturation once.</summary>
    public required IReadSignal<int> PickerEpoch { get; init; }

    /// <summary>The picker's own change (a drag, a typed hex).</summary>
    public required Action<ColorF> Pick { get; init; }

    /// <summary>"Start from" choices: the catalog's sets for this mode (proper nouns).</summary>
    public required IReadOnlyList<string> StartFromChoices { get; init; }

    /// <summary>Copy the seeds of start-from choice <c>i</c> into the draft.</summary>
    public required Action<int> StartFrom { get; init; }

    /// <summary>Put the draft back to what the editor opened with.</summary>
    public required Action Reset { get; init; }

    /// <summary>Close, discarding the draft.</summary>
    public required Action Cancel { get; init; }

    /// <summary>Store the draft as my own set for this mode, pick it and close.</summary>
    public required Action Apply { get; init; }
}

/// <summary>One label and value row of the signature sheet.</summary>
/// <param name="Label">The row's label (resolved).</param>
/// <param name="Value">The value, or null / blank for "-".</param>
public readonly record struct SignatureSheetRow(string Label, string? Value);

/// <summary>The props of <see cref="ViewKeys.DialogSignature"/>.</summary>
public sealed record SignatureSheetProps
{
    /// <summary>The verdict line under the badge.</summary>
    public required string Summary { get; init; }

    /// <summary>The signature is trusted (a success badge), else a caution badge.</summary>
    public required bool Trusted { get; init; }

    /// <summary>The label and value rows, in order.</summary>
    public required IReadOnlyList<SignatureSheetRow> Rows { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.DialogFolderPicker"/>. Built per render by the core picker, which keeps the filter's text and the match rule.</summary>
public sealed record DestinationPickerProps
{
    /// <summary>The one line of guidance naming what is moving, or null.</summary>
    public string? Body { get; init; }

    /// <summary>The filter field's placeholder.</summary>
    public required string Placeholder { get; init; }

    /// <summary>The filtered-empty line.</summary>
    public required string EmptyText { get; init; }

    /// <summary>The panel's inner padding (the playlist picker carried 8, the folder picker 0).</summary>
    public required float PanelPadding { get; init; }

    /// <summary>The filter field's text cell.</summary>
    public required Func<Signal<string>> Query { get; init; }

    /// <summary>The rows that match the filter, in order.</summary>
    public required IReadOnlyList<Actions.PickerItem> Rows { get; init; }

    /// <summary>Choose a row (the core runs the pick and closes the dialog).</summary>
    public required Action<Actions.PickerItem> Commit { get; init; }
}

/// <summary>One contributor of the credits sheet.</summary>
/// <param name="Heading">The group heading this row starts, in the server's casing, or null (not the first of its group).</param>
/// <param name="Name">The contributor.</param>
/// <param name="Role">The role, or "".</param>
/// <param name="ArtistSlot">The linked artist's slot (an accent link), or 0 (plain text).</param>
public readonly record struct CreditsEntry(string? Heading, string Name, string Role, int ArtistSlot);

/// <summary>The props of <see cref="ViewKeys.DialogSheet"/>.</summary>
public sealed record CreditsSheetProps
{
    /// <summary>Loading (bars), the list, or the empty line.</summary>
    public required Track.DrawerRules.CreditsView State { get; init; }

    /// <summary>The contributors, in display order (empty unless <see cref="State"/> is the list).</summary>
    public required IReadOnlyList<CreditsEntry> Entries { get; init; }

    /// <summary>Open an artist's page (a linked credit).</summary>
    public required Action<int> OpenArtist { get; init; }
}
