using System;
using FluentGpu.Controls;   // IOverlayService
using FluentGpu.Signals;
using Wavee.Core.Sidebar;

namespace Wavee;

// PHASE 2 / DECISION B — WHERE THE EDIT SESSION LIVES, AND THE ONE SHAPE ITS TWO SURFACES SHARE.
//
// The canvas (the docked `SidebarPane`, in the shell) and the companion page (`SidebarCustomizerPage`, in the content
// host) are SIBLINGS in the tree: neither can read a context the other provides, and — per the sidebar skill's iron
// rule 6 — the customizer never talks to the renderer anyway. So the session cannot be a page field the pane reaches
// into. It lives on `SidebarPreferences` (`prefs.Edit`), which is the one owner of sidebar state, is provided at the
// APP ROOT and is reference-stable for the process lifetime. Both surfaces reach it through
// `UseContext(SidebarPreferences.Slot)`, and the shared state seam is the ONLY thing they share.
//
// It holds SIGNALS, not decisions: every rule over the session is the pure, unit-tested `SidebarEditPlan`
// (Features/Sidebar/Data/, source-included by Wavee.Tests). This file is the reactive shell around it, exactly as
// `SidebarPreferences` is the reactive shell around `SidebarPaneState`/`SidebarPinStore`.
//
// SESSION-ONLY, NEVER PERSISTED. Which section is expanded and whether contents are shown are editor ergonomics, not
// document state — persisting them would put a second, invisible copy of "collapsed" beside the real one.

/// <summary>
/// What the customizer's generated control set needs from whoever is hosting it. It exists so
/// <c>SidebarPropertyPanel</c> and the <c>Cz*</c> rows can be RE-HOSTED — in the pane's per-section options popover
/// (P3: options live on the object) as well as in the page's docked column — without being forked or rewritten. The
/// controls' bodies are untouched; only the type of the reference-stable holder they were already taking changed.
///
/// <para><c>SidebarCustomizerPage</c> implements it explicitly (one additive block, no member signatures changed) and
/// <c>SidebarPaneEditHost</c> implements it over the pane's own services plus the shared
/// <see cref="SidebarEditSession"/>.</para>
/// </summary>
public interface ISidebarEditHost
{
    SidebarPreferences? Prefs { get; }
    ActionServices? Acts { get; }
    WaveeExtensionRegistry? Registry { get; }
    IOverlayService? OverlaySvc { get; }

    /// <summary>Bumped whenever the reducer's answer changes, INCLUDING "no". A rejected command does not bump
    /// <c>LayoutVersion</c>, so without this a controlled row would never re-render after a rejection and would keep
    /// showing the value the user picked while the document still held the old one (`CzRow.Subject`/`CzRow.Epoch`).</summary>
    Signal<int> RejectEpoch { get; }

    /// <summary>The section the property surface is editing.</summary>
    Signal<string?> Selected { get; }

    /// <summary>The item inside it the item block is editing.</summary>
    Signal<string?> SelectedItem { get; }

    /// <summary>The ONE mutation path: <c>SidebarPreferences.Dispatch</c> → reducer → undo pre-image → LayoutVersion →
    /// autosave, with the rejection surfaced through <see cref="RejectEpoch"/>.</summary>
    SidebarRejectReason Dispatch(SidebarCommand command);

    /// <summary>The same path with the shortcut band's rejection vocabulary. Command ROUTING is never re-decided at a
    /// call site — that is <c>SidebarItemCommands</c>' single decision; this only picks the message.</summary>
    SidebarRejectReason DispatchTopBar(SidebarCommand command);

    void Select(string? sectionId);
}

/// <summary>
/// The live "customize" session shared by the canvas and the companion page. Owned by <see cref="SidebarPreferences"/>.
///
/// <para><b>Who writes what.</b> The COMPANION PAGE owns <see cref="ShowContents"/> (its "Show section contents"
/// switch) and resets the ergonomics on the way in and out. The CANVAS owns <see cref="Expanded"/> (tapping a card) and
/// <see cref="OptionsSection"/> (opening a card's "…" popover). Nobody reads the other's fields directly — both read
/// <see cref="Read"/>, and the pane reads it through the <c>SidebarPaneConfig.Edit</c> delegate so the signals it
/// touches subscribe the PANE.</para>
///
/// <para><b>What ARMS the canvas is not here.</b> There is deliberately no <c>Active</c> flag on this object, because a
/// flag needs someone to clear it and there is no reliable clearer: the customizer page is a <c>Flow.KeepAlive</c>
/// destination (`ContentHost` — MaxEntries 8), so navigating away PARKS it rather than unmounting it and a mount-effect
/// cleanup would not run until the page fell out of an 8-entry ring — leaving structural drag armed on the live sidebar
/// for the rest of the session, which is precisely the Discord failure this whole mode exists to avoid. Arming is
/// therefore DERIVED, in <c>CuratedSidebar.ReadEditSession</c>, from the one fact that cannot go stale: whether the
/// customize route is the active destination. One gate, no lifecycle to get wrong.</para>
/// </summary>
public sealed class SidebarEditSession : ISidebarEditHost
{
    /// <summary>The ONE section whose real rows are revealed under its card (null = every section is a card).</summary>
    public readonly Signal<string?> Expanded = new(null);

    /// <summary>The companion's "Show section contents" switch: reveal every visible section's body at once.</summary>
    public readonly Signal<bool> ShowContents = new(false);

    /// <summary>The section whose options popover is open — also the property surface's subject, which is why
    /// <see cref="ISidebarEditHost.Selected"/> IS this signal rather than a second one that could drift from it.</summary>
    public readonly Signal<string?> OptionsSection = new(null);

    readonly Signal<string?> _selectedItem = new(null);
    readonly Signal<int> _rejectEpoch = new(0);
    bool _rejected;

    /// <summary>The app services the re-hosted control set needs. Attached by <c>CuratedSidebar.Render</c> — the one
    /// place that has both this session (through the preferences service) and the contexts, and the composition root of
    /// the only mode that supplies an <c>Edit</c> delegate. Plain fields on a reference-stable object, refreshed every
    /// render exactly like the mode component's own <c>_prefs</c>/<c>_lib</c>; the session itself is constructed by
    /// <see cref="SidebarPreferences"/>, which has no context to read them from.</summary>
    public SidebarPreferences? Prefs { get; set; }
    public ActionServices? Acts { get; set; }
    public WaveeExtensionRegistry? Registry { get; set; }
    public IOverlayService? OverlaySvc { get; set; }

    Signal<int> ISidebarEditHost.RejectEpoch => _rejectEpoch;
    Signal<string?> ISidebarEditHost.Selected => OptionsSection;
    Signal<string?> ISidebarEditHost.SelectedItem => _selectedItem;

    /// <summary>The session as a VALUE for the renderer. Reading it touches <see cref="Expanded"/> and
    /// <see cref="ShowContents"/>, which is exactly the subscription the pane wants: it is invoked inside the pane's
    /// render through <c>SidebarPaneConfig.Edit</c>. The CALLER decides whether the canvas is armed at all — see the
    /// class remarks.
    ///
    /// <para><see cref="OptionsSection"/> is read with <c>Peek</c> — opening a popover changes no planned row, and
    /// subscribing the pane to it would re-plan the whole canvas on every open (see the record's remarks).</para></summary>
    public SidebarEditState Read()
        => new(Expanded.Value, ShowContents.Value, OptionsSection.Peek());

    /// <summary>Reset the ergonomics — never the document. Called by the companion page on the way in and out so a
    /// visit opens on a predictable state. It is NOT what arms or disarms the canvas (see the class remarks), which is
    /// why a KeepAlive-parked page that never runs the cleanup can leave nothing dangerous behind: at worst the next
    /// visit resumes with the same card expanded, which is if anything the friendlier outcome.</summary>
    public void ResetErgonomics()
    {
        Expanded.SetIfChanged(null);
        OptionsSection.SetIfChanged(null);
        _selectedItem.SetIfChanged(null);
    }

    /// <summary>Expand this section, or collapse it when it is already the expanded one. ONE at a time by construction —
    /// the signal holds a single id, so there is no set to get out of sync.</summary>
    public void ToggleExpanded(string sectionId)
    {
        if (sectionId.Length == 0) return;
        bool open = string.Equals(Expanded.Peek(), sectionId, StringComparison.Ordinal);
        Expanded.Value = open ? null : sectionId;
    }

    // ── ISidebarEditHost ─────────────────────────────────────────────────────────────────────────────────────────────

    SidebarRejectReason ISidebarEditHost.Dispatch(SidebarCommand command) => Apply(command);

    SidebarRejectReason ISidebarEditHost.DispatchTopBar(SidebarCommand command) => Apply(command);

    void ISidebarEditHost.Select(string? sectionId)
    {
        OptionsSection.SetIfChanged(sectionId);
        _selectedItem.SetIfChanged(null);
    }

    /// <summary>Dispatch and publish the reducer's answer. The epoch moves on a rejection AND on the first success
    /// after one, so a control that snapped to the user's pick snaps back to the document exactly once — the same rule
    /// <c>SidebarCustomizerPage.Dispatch</c> follows, not a second policy.</summary>
    public SidebarRejectReason Apply(SidebarCommand command)
    {
        if (Prefs is not { } prefs) return SidebarRejectReason.None;
        var reason = prefs.Dispatch(command);
        bool rejected = reason != SidebarRejectReason.None;
        if (rejected || _rejected) _rejectEpoch.Value = _rejectEpoch.Peek() + 1;
        _rejected = rejected;
        return reason;
    }
}
