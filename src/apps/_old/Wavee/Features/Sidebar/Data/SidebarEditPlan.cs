using System;
using System.Collections.Generic;
using Wavee.Core.Sidebar;

namespace Wavee;

// PHASE 2 / DECISION B — THE EDIT SESSION AS A VALUE, AND THE PURE RULES OVER IT.
//
// "Customize" stopped being a page that redraws the sidebar and became a MODE OVER THE LIVE PANE. Everything the pane
// needs to know about that mode is this one POD record, handed to the renderer through the single new config delegate
// `SidebarPaneConfig.Edit` — a delegate, never a snapshot, because the config freezes at mount (the
// component-props contract) and a value member would pin frame 1's session forever.
//
// WHY THE RULES LIVE HERE AND NOT IN THE RENDERER. `Features/Sidebar/Data/*.cs` is source-included by `Wavee.Tests`
// (one level, engine-free by construction: System + Wavee.Core.Sidebar + the Data\ records). Every decision below —
// which sections reveal their rows, whether section drag is armed, what a card's count says, and the band-slot →
// `MoveSection` translation — is therefore drivable by a unit test instead of only by the eye. The engine-bound half
// (the card element, the popover host) stays in `Pane/`.
//
// The one thing this file must NOT gain is a branch on `SidebarDesign`: only `CuratedSidebar` supplies an `Edit`
// delegate, but the renderer and these rules only ever see "there is a session" / "there is not".

/// <summary>
/// One live edit session, as a value. Read fresh from <c>SidebarPaneConfig.Edit</c> on every pane render.
///
/// <para><paramref name="ExpandedSection"/> — the ONE section whose real rows are revealed under its card (null = every
/// section is a card). One at a time on purpose: a 60-row expanded sidebar turns section dragging into a scroll-fight,
/// and a card-only plan has the uniform pitch <c>Reorderable</c> wants (§Phase 2).</para>
///
/// <para><paramref name="ShowContents"/> — the companion page's "Show section contents" switch. True reveals every
/// (visible) section's body at once, for item-level work; it deliberately DISARMS section drag, because the card run is
/// then no longer contiguous (see <see cref="SidebarEditPlan.SectionsReorderable"/>).</para>
///
/// <para><paramref name="OptionsSection"/> — the section whose per-section options popover is open, i.e. the subject
/// the "…"-anchored popover host is editing. It is deliberately NOT part of <see cref="SidebarEditPlan.Fold"/>: opening
/// a popover changes nothing about the planned rows, and folding it in would re-plan the whole pane on every open.</para>
/// </summary>
public readonly record struct SidebarEditState(
    string? ExpandedSection = null,
    bool ShowContents = false,
    string? OptionsSection = null);

/// <summary>
/// PHASE 3 — what a palette chip carries while it is being dragged onto the canvas.
///
/// <para>It is the WHOLE <c>AddSection</c> argument list minus the index, so the drop site does not have to know what a
/// palette entry is: the companion page composes it once (at drag promotion, never per move) and the canvas turns it
/// into ONE undoable command. A record rather than a struct because the drag payload travels as <c>object?</c> anyway,
/// and it is allocated once per gesture — outside the per-frame region the dnd rules police.</para>
///
/// <para><paramref name="Label"/> is the already-localized name the drag chip shows. It is resolved at composition time
/// on purpose: the chip resolver runs inside the 0-alloc frame region while a drag is live, so it may look a string up
/// but must never build one.</para>
/// </summary>
public sealed record SidebarSectionDropPayload(
    SidebarSectionKind Kind,
    string Label,
    SidebarItemSpec? Item = null,
    SidebarExtensionRef? Extension = null);

/// <summary>The pure rules an edit session implies. Engine-free, so <c>Wavee.Tests</c> drives the real ones.</summary>
public static class SidebarEditPlan
{
    /// <summary>The drag KIND shared by the section-card reorder band and the companion palette's chips. ONE owner:
    /// <c>SidebarPane</c> reads this const rather than re-spelling the literal, because a drag kind that is typed twice
    /// is a drop that silently accepts nothing (the dnd skill's first rule for cross-list work). It lives HERE, in the
    /// engine-free half, so both the pane and the page can see it without either seeing the other.</summary>
    public const string SectionDragKind = "wavee.sidebar.section";

    /// <summary>Does this section reveal its real rows under its card?
    ///
    /// <para>A HIDDEN section never does, even while it is the expanded one: its body contributes nothing to the live
    /// sidebar, and drawing rows that the user's own sidebar does not have would be the editor lying about the artifact
    /// it edits (P1). The card itself stays — dimmed, with its eye-off badge — which is P2: nothing vanishes into an
    /// invisible elsewhere.</para></summary>
    public static bool ShowsBody(in SidebarEditState edit, SidebarSectionSpec section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Hidden || !HasBody(section.Kind)) return false;
        if (edit.ShowContents) return true;
        return edit.ExpandedSection is { Length: > 0 } id
               && string.Equals(id, section.Id, StringComparison.Ordinal);
    }

    /// <summary>Can this kind reveal anything at all under its card? A Divider and a Header are pure chrome — the
    /// planner has no body arm for either — so their cards carry no disclosure mark and are not expandable, rather than
    /// offering a chevron that opens onto nothing.</summary>
    public static bool HasBody(SidebarSectionKind kind)
        => kind is not (SidebarSectionKind.Divider or SidebarSectionKind.Header);

    /// <summary>Is the section-card drag band armed for this session?
    ///
    /// <para>Only while EVERY section is a card. A <c>Reorderable</c> band is one CONTIGUOUS run of plan rows at ONE
    /// uniform pitch (<c>SidebarPaneBand</c>); the moment a section expands, its body rows split the card run in two
    /// and the slot math would address body rows as if they were cards. This is the same guard the pane already applies
    /// to a Pinned band whose folder is expanded (<c>SidebarPane._pinnedSubtrees</c>: "the flat reorder controller is
    /// disabled until the folder collapses rather than moving the wrong store slot"). Explicit Move up / Move down stay
    /// available from every card's "…" menu, so a section can always be reordered — drag is one of several ways, never
    /// the only way (P6).</para></summary>
    public static bool SectionsReorderable(in SidebarEditState edit)
        => !edit.ShowContents && edit.ExpandedSection is not { Length: > 0 };

    /// <summary>Is this card the PINNED head — the materialised Shortcuts band (Phase 1's sentinel)?
    ///
    /// <para>The sentinel is not in <c>SidebarCustomLayout.Sections</c>, so <c>MoveSection</c>/<c>SetSectionHidden</c>/
    /// <c>DuplicateSection</c>/<c>RemoveSection</c> addressed at it are all <c>UnknownSection</c> rejections. Its card
    /// therefore carries no grip, no eye and no "…": an affordance that silently rejects is strictly worse than an
    /// affordance that is not offered. Its ITEMS are still fully editable — expanding the card reveals the real rows,
    /// whose reorder routes through <c>SidebarItemCommands</c> to <c>MoveTopBarItem</c>.</para></summary>
    public static bool IsPinnedCard(string? sectionId) => SidebarIds.IsTopBar(sectionId);

    /// <summary>The honest count a card may show beside its title, or -1 for "this section has no count worth
    /// claiming".
    ///
    /// <para>Deliberately NOT "how many rows would this section plan": that is only knowable by planning the body, and
    /// planning a 10 000-entry <c>EntityList</c> once per card per re-plan to print a number would be a real cost for a
    /// decoration. So a card counts what the DOCUMENT holds — a group's child sections, an authored item list's visible
    /// items — and a projected section (Pinned / PlaylistTree / EntityList / a feed / a contribution) shows nothing
    /// rather than a number it would have to guess.</para></summary>
    public static int CardCount(SidebarSectionSpec section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Kind == SidebarSectionKind.CustomGroup) return section.ChildList.Count;
        if (!SidebarSectionKinds.AcceptsItems(section.Kind)) return -1;
        // Pinned "items" are display OVERRIDES for pins made elsewhere (§C1.6), not the pin list — counting them would
        // print "0" over a band showing twelve pins.
        if (section.Kind == SidebarSectionKind.Pinned) return -1;

        var items = section.ItemList;
        int n = 0;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].Hidden) n++;
        return n;
    }

    /// <summary>The session folded into one int for the pane's plan <c>DepKey</c>. <c>OptionsSection</c> is excluded on
    /// purpose — see the record's remarks.</summary>
    public static int Fold(in SidebarEditState? edit)
    {
        if (edit is not { } e) return 0;
        unchecked
        {
            int h = e.ShowContents ? 0x5f5f_0001 : 0x5f5f_0002;   // never 0: "no session" must not collide with "session"
            if (e.ExpandedSection is { Length: > 0 } id) h = h * 31 + StringComparer.Ordinal.GetHashCode(id);
            return h;
        }
    }

    /// <summary>Translate one committed section-card drag into the undoable <c>MoveSection</c> command, or null when
    /// there is nothing honest to dispatch.
    ///
    /// <para>Two index spaces meet here, which is exactly why this is a pure, tested function rather than a lambda at
    /// the drag site (the <c>SidebarOutlineDrag.ToMove</c> precedent):</para>
    /// <list type="bullet">
    /// <item><b>Band slots</b> enumerate the SectionCard rows of the plan, which are the document's top-level sections
    /// in order MINUS any kind this build does not understand (an unknown kind plans no card, exactly as it renders no
    /// rows) and minus the pinned Shortcuts head, which the band never covers.</item>
    /// <item><b><c>MoveSection.NewIndex</c></b> is an index into <paramref name="document"/><c>.Sections</c>
    /// interpreted AFTER the removal (<c>SidebarLayoutReducer.DoMoveSection</c>: "Remove first — NewIndex is
    /// interpreted AFTER removal (the standard Reorderable.OnReorder contract)").</item>
    /// </list>
    /// <para>The two are therefore bridged through the NEIGHBOUR the drop landed above — the only translation that
    /// stays exact when a card is missing from the middle of the run.</para>
    /// </summary>
    /// <param name="document">The PERSISTED document (<c>SidebarPreferences.Layout</c>), never the render-path document
    /// the pane plans from: the latter carries the materialised Shortcuts section at index 0, so every index in it is
    /// one too high for a command the reducer will execute.</param>
    /// <param name="rows">The published plan rows.</param>
    /// <param name="bandStart">Plan index of band slot 0.</param>
    /// <param name="bandCount">Number of cards in the band.</param>
    /// <param name="from">The lifted card's band slot.</param>
    /// <param name="to">The committed band slot, post-removal (the <c>Reorderable.OnReorder</c> contract).</param>
    public static SidebarCommand? ToMoveSection(SidebarCustomLayout? document, IReadOnlyList<SidebarRow>? rows,
                                                int bandStart, int bandCount, int from, int to)
    {
        if (document is null || rows is null) return null;
        if (bandCount <= 1 || from == to) return null;
        if ((uint)from >= (uint)bandCount || (uint)to >= (uint)bandCount) return null;

        string movingId = SectionIdAt(rows, bandStart, bandCount, from);
        if (movingId.Length == 0 || IsPinnedCard(movingId)) return null;   // the sentinel is not in `Sections`

        var moving = document.Locate(movingId);
        if (moving.Index < 0 || moving.Parent is not null) return null;    // a card is always a TOP-LEVEL section

        // The post-removal band holds bandCount-1 cards, so slot bandCount-1 is "append". Any other slot names the card
        // the moved one lands ABOVE; its ORIGINAL slot is shifted by one wherever the removal was above it.
        int successorSlot = to >= bandCount - 1 ? -1 : (to < from ? to : to + 1);

        int newIndex;
        if (successorSlot < 0)
        {
            newIndex = document.Sections.Count - 1;                        // post-removal tail
        }
        else
        {
            string successorId = SectionIdAt(rows, bandStart, bandCount, successorSlot);
            var successor = document.Locate(successorId);
            if (successor.Index < 0 || successor.Parent is not null) return null;
            newIndex = successor.Index > moving.Index ? successor.Index - 1 : successor.Index;
        }

        if (newIndex < 0 || newIndex == moving.Index) return null;         // a no-op is silence, not a rejection
        return new MoveSection(movingId, null, newIndex);
    }

    /// <summary>PHASE 3 — translate one palette chip dropped ON a section card into the undoable <c>AddSection</c>, or
    /// null when there is nothing honest to dispatch.
    ///
    /// <para><b>The drop convention is "insert BEFORE the card you aimed at"</b>, which is the same neighbour-bridging
    /// discipline <see cref="ToMoveSection"/> uses and for the same reason: the canvas enumerates CARDS (top-level
    /// sections this build understands, plus the materialised Shortcuts head) while <c>AddSection.Index</c> is an index
    /// into <paramref name="document"/><c>.Sections</c>. Bridging through the neighbour keeps the two exact even when a
    /// card is missing from the middle of the run (an unknown kind plans no card, exactly as it renders no rows).</para>
    ///
    /// <para>The pinned Shortcuts head is not in <c>Sections</c>, so a drop on it resolves to index 0 — "above
    /// everything the reducer can address", which is where the cue pointed. A null/blank
    /// <paramref name="beforeSectionId"/> means "no card under the pointer" and APPENDS.</para></summary>
    /// <param name="document">The PERSISTED document (<c>SidebarPreferences.Layout</c>), never the render-path document:
    /// the latter carries the materialised Shortcuts section at index 0, so every index in it is one too high.</param>
    public static SidebarCommand? ToAddSection(SidebarCustomLayout? document, string? beforeSectionId,
                                               SidebarSectionDropPayload? payload)
    {
        if (document is null || payload is null) return null;
        if (!SidebarSectionKinds.IsKnown(payload.Kind)) return null;

        int index = document.Sections.Count;                       // no card under the pointer ⇒ append
        if (beforeSectionId is { Length: > 0 } id && !IsPinnedCard(id))
        {
            var at = document.Locate(id);
            // A child card is not a top-level slot; refusing is better than silently filing the new section somewhere
            // the cue never pointed. (Today's canvas only cards top-level sections, so this is a guard, not a path.)
            if (at.Index < 0 || at.Parent is not null) return null;
            index = at.Index;
        }
        else if (beforeSectionId is { Length: > 0 })
        {
            index = 0;                                             // the Shortcuts head: above every addressable section
        }

        return new AddSection(payload.Kind, index, ParentId: null, Item: payload.Item, Extension: payload.Extension);
    }

    /// <summary>The section id at a band slot, or "" when the slot is out of the plan.</summary>
    public static string SectionIdAt(IReadOnlyList<SidebarRow>? rows, int bandStart, int bandCount, int slot)
    {
        if (rows is null || (uint)slot >= (uint)bandCount) return "";
        int index = bandStart + slot;
        if ((uint)index >= (uint)rows.Count) return "";
        var row = rows[index];
        return row.Kind == SidebarRowKind.SectionCard ? row.SectionId : "";
    }
}
