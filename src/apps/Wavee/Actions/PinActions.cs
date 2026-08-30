using System;
using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>
/// The pin action pair (v1 spec F.5.2) — an ABSOLUTE-STATE pair, not a toggle row. This follows the deliberate
/// precedent set by <c>Menus.AccessItem</c> ("Explicit absolute-state rows (not a toggle): a mis-checked toggle
/// would invert the user's intent") and matches Spotify's own menu, which shows exactly one of "Pin to sidebar" /
/// "Unpin from sidebar". Which one a menu inserts is <see cref="Row"/>'s decision, delegated to the engine-free
/// <see cref="PinRowRule"/>.
///
/// KILL SWITCH: every path checks <c>ActionServices.Sidebar</c>. A host without a pin store gets NO row at all (not a
/// disabled one) and every Execute is a silent no-op — the documented contract on <c>ActionServices.Sidebar</c>.
///
/// UNDO (F.5.5) IS THE TOAST, NOT THE ACTIVITY LOG. <c>ActivityKind</c> records library MUTATIONS, every kind maps to a
/// server-reconciled write, and <c>ActivityUndoExecutor.ApplyInverseAsync</c> switches over exactly those kinds. A pin is
/// local presentation state with no server side; adding a kind would put a non-mutation into the notification centre's
/// mutation history and require a no-op inverse. Unpin's undo re-inserts at the pin's FORMER index (the index
/// <c>SidebarPinStore.Unpin</c> hands back), which a plain re-pin would not restore.
/// </summary>
public static class PinActions
{
    public static readonly AppAction PinToSidebar = new()
    {
        Id = ActionId.PinToSidebar, IconKey = ActionIcons.Pin,
        Label = static c => Loc.Get(Strings.Sidebar.Pin.PinTo),
        IsEnabled = static c => c.S.Sidebar is not null && SidebarPinId.FromTarget(in c.Target) is not null,
        Execute = static c =>
        {
            if (c.S.Sidebar is not { } prefs || SidebarPinId.FromTarget(in c.Target) is not { } id) return;
            Pin(prefs, id, SidebarPinId.KindOf(id), c.Target.Uri, c.Target.Name);
        },
    };

    public static readonly AppAction UnpinFromSidebar = new()
    {
        Id = ActionId.UnpinFromSidebar, IconKey = ActionIcons.Unpin,
        Label = static c => Loc.Get(Strings.Sidebar.Pin.Unpin),
        IsEnabled = static c => c.S.Sidebar is not null && SidebarPinId.FromTarget(in c.Target) is not null,
        Execute = static c =>
        {
            if (c.S.Sidebar is not { } prefs || SidebarPinId.FromTarget(in c.Target) is not { } id) return;
            Unpin(prefs, id, c.Target.Name);
        },
    };

    // ── the ONE row a menu inserts ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whichever of the pair applies to the current pinned state, or null when the target is not pinnable at
    /// all (tracks, episodes, or internal editor/tooling routes — refused in ONE place by <see cref="SidebarPinId"/>) or
    /// the pin store is absent. Callers add it guarded: <c>if (PinActions.Row(in ctx) is { } row) rows.Add(row);</c></summary>
    public static MenuFlyoutItem? Row(in ActionContext ctx)
    {
        string? id = SidebarPinId.FromTarget(in ctx.Target);
        switch (PinRowRule.Decide(ctx.S.Sidebar is not null, id, ctx.S.Sidebar?.IsPinned(id) ?? false))
        {
            case PinRowKind.Pin: return PinToSidebar.ToMenuItem(ctx);
            case PinRowKind.Unpin: return UnpinFromSidebar.ToMenuItem(ctx);
            default: return null;
        }
    }

    /// <summary>The sidebar's own rows know their pin id directly (a folder has no uri and an app-route row has no
    /// <see cref="ActionTarget"/> at all), so they pass it explicitly. Same rule, same toasts, same undo — just no
    /// <see cref="ActionTarget"/> in the middle.</summary>
    public static MenuFlyoutItem? RowForId(ActionServices s, string? pinId, SidebarEntryKind kind, string? uri, string? name)
    {
        if (s.Sidebar is not { } prefs) return null;
        if (!SidebarPinId.IsPinnable(kind)) return null;
        string id = pinId ?? "";
        switch (PinRowRule.Decide(true, pinId, prefs.IsPinned(pinId)))
        {
            case PinRowKind.Pin:
                return new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Pin.PinTo), ActionIcons.Resolve(ActionIcons.Pin),
                    true, () => Pin(prefs, id, kind, uri, name));
            case PinRowKind.Unpin:
                return new MenuFlyoutItem(Loc.Get(Strings.Sidebar.Pin.Unpin), ActionIcons.Resolve(ActionIcons.Unpin),
                    true, () => Unpin(prefs, id, name));
            default:
                return null;
        }
    }

    /// <summary>The projected-row overload (<c>Menus.SidebarEntry</c>): the entry's <c>Id</c> already IS the pin id
    /// (F.5.4), with the not-pinnable kinds screened out by <see cref="SidebarPinId.FromEntry"/>.</summary>
    public static MenuFlyoutItem? RowForEntry(ActionServices s, in SidebarLibraryEntry e)
        => RowForId(s, SidebarPinId.FromEntry(in e), e.Kind, e.Uri, e.Name);

    /// <summary>Tab/page overload: the destination already carries the canonical route identity and display cache.</summary>
    public static MenuFlyoutItem? RowForDestination(ActionServices s, in SidebarDestination destination)
        => RowForId(s, destination.PinId, destination.Kind, destination.Uri, destination.Name);

    // ── mutations (the ONLY two, shared by every projection above) ───────────────────────────────────────────────────

    /// <summary>Append the pin + raise the undo toast. Already-pinned is a SILENT no-op (no toast): the store is
    /// idempotent and a double invoke must never reorder the list or claim it did something.</summary>
    internal static void Pin(SidebarPreferences prefs, string pinId, SidebarEntryKind kind, string? uri, string? name)
    {
        if (string.IsNullOrEmpty(pinId) || !SidebarPinId.IsPinnable(kind)) return;
        var pin = new SidebarPin(pinId, kind, uri ?? "", name ?? "",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!prefs.Pin(pin)) return;
        Toast.Show(Message(Strings.Sidebar.PinnedToastKey, Strings.Sidebar.Pin.Pinned, name), new ToastOptions
        {
            Severity = InfoBarSeverity.Success,
            ActionLabel = Loc.Get(Strings.Sidebar.Pin.Undo),
            OnAction = () => prefs.Unpin(pinId),
        });
    }

    /// <summary>Remove the pin + raise the undo toast whose action restores it at its FORMER index. Snapshots the pin
    /// BEFORE the removal, because the store owns the record and the index is only knowable while it is still there.</summary>
    internal static void Unpin(SidebarPreferences prefs, string pinId, string? nameHint = null)
    {
        if (string.IsNullOrEmpty(pinId)) return;
        int at = prefs.Pins.IndexOf(pinId);
        if (at < 0) return;
        var removed = prefs.Pins[at];
        if (prefs.Unpin(pinId) < 0) return;
        string name = nameHint is { Length: > 0 } ? nameHint : removed.Name;
        Toast.Show(Message(Strings.Sidebar.UnpinnedToastKey, Strings.Sidebar.Pin.Unpinned, name), new ToastOptions
        {
            Severity = InfoBarSeverity.Informational,
            ActionLabel = Loc.Get(Strings.Sidebar.Pin.Undo),
            OnAction = () => prefs.InsertPin(removed, at),
        });
    }

    /// <summary>The named toast ("Pinned “Discover Weekly”") when a display name is known, else the plain one. A pinned
    /// app route or a cover-less row can legitimately have no name yet — the toast must still be a sentence.</summary>
    static string Message(string namedKey, string plainKey, string? name)
        => name is { Length: > 0 } n ? Loc.Format(namedKey, ("name", n)) : Loc.Get(plainKey);
}
