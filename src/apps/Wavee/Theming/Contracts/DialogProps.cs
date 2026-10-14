// ── Theming/Contracts/DialogProps.cs — the props of the dialog keys (themes plan 6.0, T11a) ─────────────────────────────────────
//
// THE DIALOGS THE APP DRAWS. A dialog is two things: the SHELL (the modal card, its command row, the default button, Enter and Escape, the
// focus trap and its return, the open and close motion: the engine's `ContentDialog`, or a `PopupChrome.Modal` popup) and the BODY the
// app puts in it. The core opens the shell and owns every decision (what the buttons do, busy and disabled states, light dismiss); these
// keys are the bodies and the two bare cards the app builds itself.
//
// WVTH006: sealed records, init-only members, no writable `Signal<T>` (an editable cell travels as a `Func<Signal<string>>`).

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
