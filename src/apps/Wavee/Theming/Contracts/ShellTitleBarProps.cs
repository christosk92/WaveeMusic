// ── Theming/Contracts/ShellTitleBarProps.cs — the props of `Shell.TitleBar` (themes plan 6.0, T4b) ─────────────────────────
//
// THE TITLE BAR IS A CORE MODEL AND A THEME VIEW. The core owns the allocation of the row (`Shell.Chrome`, the pressure
// allocator that takes the structure's `ChromePolicy`: it decides which affordances are in the row and how wide the search and
// the tabs are, with its promotion hysteresis), the tab workspace (the tabs, their pins, the reorder and the drag payloads
// are `TabViewItem` data and verbs), the auth fold, the history stacks and every verb. The theme DRAWS the row: the engine's
// `TitleBar` (drag regions and the three caption buttons are the control's, reported to Win32 as the non-client regions), the
// tabs island, the history buttons, the identity cluster and the search (Classic draws no theme toggle since tuned F13).
//
// WHAT THE RECORD CARRIES. One shared, frozen instance for the process (the frame props hold it); no member changes:
//   · `Func`s the title bar's render READS (the allocation, the version stamps, the tab items, the unread count, the pin):
//     they are called inside the engine bar's render, so reading a signal there subscribes the bar, exactly as the builders did
//     before the move. `Layout` subscribes, `PeekLayout` does not (the strip reads it at mount).
//   · Verbs (the tab and history actions, sign in, friends, the notification panel).
//   · Element builders over core hosts (the profile chip: `Shell.ProfileChip`, which mounts `Overlay.ProfileChip`).
//   · The search's own props (`Shell.Search`), which the row mounts in its centre column and, in the icon form, before the
//     caption buttons.
//
// Writable cells the engine's controls bind (the tab strip's selection) arrive as a `Func` returning the cell: WVTH006 keeps a
// writable `Signal<T>` out of a props member, and this is not state travelling in props but the control's own binding.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.ShellTitleBar"/>.</summary>
public sealed record ShellTitleBarProps
{
    // ── the allocation and the bar's version stamps ──────────────────────────────────────────────────────────────────

    /// <summary>The row's allocation (<see cref="ChromeRowState"/>), read tracked.</summary>
    public required Func<ChromeRowState> Layout { get; init; }

    /// <summary>The row's allocation, read WITHOUT subscribing (the tab strip reads it at mount).</summary>
    public required Func<ChromeRowState> PeekLayout { get; init; }

    /// <summary>The tabs island's revision: the workspace, the selection and the staged route. The bar re-renders and re-pushes its
    /// non-client regions when it moves.</summary>
    public required Func<int> TabsVersion { get; init; }

    /// <summary>The islands' revision: every term the centre, trailing and caption-leading islands read. The bar's memo key.</summary>
    public required Func<int> ContentVersion { get; init; }

    /// <summary>The tab strip's items revision (the workspace, the "+" slot and the staged route).</summary>
    public required Func<int> TabItemsVersion { get; init; }

    // ── the pane toggle (the engine bar's own button) ────────────────────────────────────────────────────────────────

    /// <summary>The pane toggle was pressed.</summary>
    public required Action OnPaneToggle { get; init; }

    /// <summary>The toggle's enabled state (it is disabled while the sidebar is being edited).</summary>
    public required IReadSignal<bool> PaneToggleEnabled { get; init; }

    /// <summary>The toggle's tooltip, read per render ("Collapse navigation" / "Expand navigation").</summary>
    public required Func<string> PaneToggleTip { get; init; }

    // ── the tabs ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip's items: label, glyph, pinned and closable facts, the context menu, the drag source and the drop target.</summary>
    public required Func<IReadOnlyList<TabViewItem>> TabItems { get; init; }

    /// <summary>The strip's selection cell (a projection the shell re-asserts after every workspace op); the strip writes it before it
    /// raises <see cref="OnActivateTab"/>.</summary>
    public required Func<Signal<int>> TabSelection { get; init; }

    /// <summary>A tab was activated (by index).</summary>
    public required Action<int> OnActivateTab { get; init; }

    /// <summary>A tab asked to close (by index).</summary>
    public required Action<int> OnCloseTab { get; init; }

    /// <summary>The "+" was pressed: a new tab on Home.</summary>
    public required Action OnNewTab { get; init; }

    /// <summary>The strip measured its content extent (DIP): the allocator's tab input.</summary>
    public required Action<float> OnTabExtent { get; init; }

    // ── history ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Back is possible.</summary>
    public required Func<bool> CanBack { get; init; }

    /// <summary>Forward is possible.</summary>
    public required Func<bool> CanForward { get; init; }

    /// <summary>Go back.</summary>
    public required Action GoBack { get; init; }

    /// <summary>Go forward.</summary>
    public required Action GoForward { get; init; }

    /// <summary>The history flyout's rows for the back stack (<c>false</c>) or the forward stack (<c>true</c>), read at OPEN (never at
    /// mount); empty when the stack is empty, and then nothing opens.</summary>
    public required Func<bool, IReadOnlyList<MenuFlyoutItem>> HistoryMenu { get; init; }

    // ── the identity cluster ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The signed-in profile chip: its core host, which mounts <see cref="ViewKeys.OverlayProfileChip"/>.</summary>
    public required Func<Element> ProfileChip { get; init; }

    /// <summary>"Sign in" / "Reconnect".</summary>
    public required Action SignIn { get; init; }

    /// <summary>The friends rail was toggled.</summary>
    public required Action ToggleFriends { get; init; }

    /// <summary>The unread notification count (zero while the store shot presents).</summary>
    public required Func<int> BellUnread { get; init; }

    /// <summary>Open the notification panel from the bell (a second press closes it).</summary>
    public required Action<IOverlayService, Func<NodeHandle>, Ref<OverlayHandle?>> ToggleNotifications { get; init; }

    /// <summary>The direct Pin / Unpin for the destination on screen, or null when it is not pinnable; read tracked.</summary>
    public required Func<TitleBarPin?> Pin { get; init; }

    // ── the search ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The search's props; the row mounts <see cref="ViewKeys.ShellSearch"/> with this record and sets its form.</summary>
    public required ShellSearchProps Search { get; init; }
}
