// ── Theming/Contracts/ChromeRowState.cs — the merged chrome row's allocation, as a theme reads it (themes plan 6.0, T4b) ─────
//
// The row is allocated by the core's pressure allocator (`Shell.Chrome.Resolve`, a state machine that takes the structure's
// `ChromePolicy`): which of the optional affordances are in the row, which identity chip it shows, which form the search takes
// and how wide, and how wide the tabs island is. The theme only DRAWS what the allocation says; this value is the allocation
// without the allocator's own bookkeeping (the hysteresis memory, the budget arithmetic).

namespace Wavee.Theming.Contracts;

/// <summary>Which identity chip the trailing island shows (the auth fold: a silent resume is <see cref="Connecting"/>, never an
/// actionable "Sign in").</summary>
public enum ChromeChipForm : byte
{
    /// <summary>The signed-in profile chip (avatar, optionally the name).</summary>
    Profile,

    /// <summary>A resume is in flight: a caption.</summary>
    Connecting,

    /// <summary>A stored credential exists but the session is down: the accent "Reconnect" button.</summary>
    Reconnect,

    /// <summary>Nobody is signed in: the accent "Sign in" button.</summary>
    SignIn,
}

/// <summary>Which shape the search takes: the field in the row's centre column, or the magnifier in the caption-leading island.</summary>
public enum SearchForm : byte
{
    /// <summary>The omnibar field.</summary>
    Field,

    /// <summary>The magnifier that opens the omnibar in a flyout.</summary>
    Icon,
}

/// <summary>The row's allocation (a plain value: reading it in a render subscribes that render to the allocator).</summary>
/// <param name="ShowName">The profile name is beside the avatar.</param>
/// <param name="ShowActions">Bell, friends and pin are in the trailing row (below the stage they fold into the profile menu).</param>
/// <param name="ShowForward">The forward button is in the tabs island.</param>
/// <param name="ShowBack">The back button is in the tabs island.</param>
/// <param name="ShowNewTab">The tab strip's "+" slot is mounted.</param>
/// <param name="ShowTrailing">The identity cluster is in the row.</param>
/// <param name="Chip">The identity chip's form.</param>
/// <param name="Search">The search's form.</param>
/// <param name="SearchWidth">The width the field was allocated (a width the row can seat; the icon form publishes its own).</param>
/// <param name="TabIslandWidth">The tabs island's reserved width: the lead cluster plus the history buttons and the add slot.</param>
/// <param name="TabCount">The tabs island is one tab-count button (a menu of the open tabs) instead of the strip (contract 1.12).</param>
/// <param name="TabMaxWidth">The widest a tab of the strip grows at this window tier; 0 where the allocation predates it (contract 1.12).</param>
/// <param name="TabsHidden">The tab-count button is out of the row: the window is too narrow to seat it whole (contract 1.15).</param>
public readonly record struct ChromeRowState(
    bool ShowName, bool ShowActions, bool ShowForward, bool ShowBack, bool ShowNewTab, bool ShowTrailing,
    ChromeChipForm Chip, SearchForm Search, float SearchWidth, float TabIslandWidth,
    bool TabCount = false, float TabMaxWidth = 0f, bool TabsHidden = false);

/// <summary>The direct Pin / Unpin the trailing island offers for the destination on screen.</summary>
/// <param name="Glyph">The button's glyph.</param>
/// <param name="Label">The tooltip.</param>
/// <param name="Invoke">The verb.</param>
public sealed record TitleBarPin(string Glyph, string Label, Action Invoke);
