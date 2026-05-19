namespace Wavee.UI.WinUI.Controls.InPageFilter;

/// <summary>
/// Opt-in contract implemented by any page (or surface inside a page) that
/// wants to participate in the shared Ctrl+F in-page filter overlay. When
/// the user presses Ctrl+F, <see cref="InPageFilterController"/> looks at
/// the currently active page (<c>PageHost.ActivePage</c>) and, if it
/// implements this interface with <see cref="CanFilter"/> true, shows the
/// floating filter bar and routes typed text into <see cref="FilterQuery"/>.
///
/// Implementers typically forward <see cref="FilterQuery"/> straight to
/// their view-model's existing <c>SearchQuery</c> property; the VM already
/// owns debouncing and the <c>ApplyFilterAndSort</c> pipeline.
/// </summary>
public interface IInPageFilterable
{
    /// <summary>Two-way: the overlay writes here on every keystroke.</summary>
    string FilterQuery { get; set; }

    /// <summary>Placeholder shown in the overlay's input. Page-specific
    /// (<c>"Filter tracks…"</c>, <c>"Filter episodes…"</c>, etc.).</summary>
    string FilterPlaceholder { get; }

    /// <summary>When false, Ctrl+F is a no-op for this page (the bar stays
    /// hidden). Use to gate by current sub-surface state (e.g. a library
    /// view that hasn't loaded yet, or a wizard step that isn't a list).</summary>
    bool CanFilter { get; }

    /// <summary>Invoked when the bar is dismissed (Esc, X, or page nav).
    /// Default clears the query.</summary>
    void OnFilterClosed() => FilterQuery = string.Empty;
}
