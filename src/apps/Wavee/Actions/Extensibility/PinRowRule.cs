using System;

namespace Wavee;

// The pin MENU-ROW decision (v1 spec F.5.2/F.5.3): which of the absolute-state pair a menu inserts, or none at all.
// Extracted from PinActions (which is engine-bound: AppAction / MenuFlyoutItem / Toast) so the rule — including the
// kill-switch arm — is unit-tested engine-free by src/apps/Wavee.Tests, exactly like ActionRules.cs.

/// <summary>Which pin row a menu shows. An ABSOLUTE-state pair, never a toggle: the deliberate precedent is
/// <c>Menus.AccessItem</c> ("a mis-checked toggle would invert the user's intent"), and it matches Spotify's own
/// menu, which shows exactly one of "Pin to sidebar" / "Unpin from sidebar".</summary>
public enum PinRowKind : byte
{
    /// <summary>No row at all — the target is not pinnable, or the pin store is absent (the feature's kill switch). The
    /// menu omits the row rather than showing a dead one.</summary>
    None = 0,
    Pin = 1,
    Unpin = 2,
}

public static class PinRowRule
{
    /// <summary>The one rule. <paramref name="hasStore"/> false ⇒ <see cref="PinRowKind.None"/> (a host without
    /// <c>ActionServices.Sidebar</c> has no pins at all, so a row would be a lie); an unpinnable target (a track, an
    /// episode, or an internal editor/tooling route — all decided upstream by <see cref="SidebarPinId"/>) ⇒
    /// <see cref="PinRowKind.None"/>; otherwise the row is whichever verb applies to the CURRENT pinned state.</summary>
    public static PinRowKind Decide(bool hasStore, string? pinId, bool isPinned)
    {
        if (!hasStore || string.IsNullOrEmpty(pinId)) return PinRowKind.None;
        return isPinned ? PinRowKind.Unpin : PinRowKind.Pin;
    }
}
