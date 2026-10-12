// ── Theming/Contracts/ShellSearchProps.cs — the props of `Shell.Search` (themes plan 6.0, T4b) ────────────────────────────────
//
// THE OMNIBAR IS A CORE MODEL AND A THEME VIEW. `OmnibarModel` (the suggestion store over the request lifecycle, the keyboard
// cursor, the focus tickets, the keystroke and fetch edges, and every verb: submit, choose, play, the row menu, retry) is the
// core's; the field in the row, the magnifier and its flyout, and the popup's picture are the theme's. The field and the
// icon-mode flyout are two mounts of ONE omnibar over ONE model, so a search-mode flip keeps the rows, the pending generation
// and the cursor.
//
// The model is carried as the object itself: the members a view uses are the ones that name no `Shell` type (the stores, the
// edges and the verbs). The popup receives its answer and its rows as plain values (`SearchAnswer`, built by the core). The
// mount-specific part (the form and the live width of the centre column the field sits in) is set per mount with `with`;
// everything else is one shared, frozen record.
//
//   Field   the omnibar field in the centre column; its width is `FieldWidth(measured centre width)` (the allocator is
//           authoritative: a measurement below the field's minimum is evidence the measurement is wrong, not the ladder)
//   Icon    the magnifier, a flyout of the omnibar below it on press and on a focus ticket (Ctrl+F)

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellSearch"/>.</summary>
public sealed record ShellSearchProps
{
    /// <summary>Which shape this mount draws.</summary>
    public SearchForm Form { get; init; }

    /// <summary>The live measured width of the centre column the field sits in (<see cref="SearchForm.Field"/> only).</summary>
    public IReadSignal<float>? Avail { get; init; }

    /// <summary>The omnibar's model: the one store and its verbs.</summary>
    public required OmnibarModel Model { get; init; }

    /// <summary>The omnibar's text cell: the field's editor binds it (the text IS the search text, synced to the route on navigation
    /// only). A <c>Func</c> for the reason in <see cref="ShellTitleBarProps"/>.</summary>
    public required Func<Signal<string>> TextCell { get; init; }

    /// <summary>The focus ticket (<c>Ctrl+F</c>, the allocator handing the caret to a new form), read tracked.</summary>
    public required Func<int> FocusTicket { get; init; }

    /// <summary>The width a FIELD is laid out at, given the centre column's measured width (<c>Shell.Chrome.FieldWidthFor</c> over the
    /// current allocation): it never reads the search form, so a still-mounted field cannot be laid out at the icon's width.</summary>
    public required Func<float, float> FieldWidth { get; init; }

    /// <summary>What the popup body needs of the core.</summary>
    public required SearchSuggestionSource Suggestions { get; init; }
}
