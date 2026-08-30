using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// PHASE 2 / DECISION B — THE SECTION CARD, and the per-section options POPOVER anchored to its "…".
//
// The card is what a whole section looks like while the pane is the customize canvas: ONE uniform-height row
// (SidebarPaneMetrics.EditCardHeight) carrying grip · kind glyph · title · count · eye · "…", with a chevron marking
// whether its real rows are revealed underneath. Two reasons it exists at all, both from the rework's research pass:
// a 60-row expanded sidebar makes section dragging a scroll-fight, and a uniform card pitch is what lets the section
// band be an ordinary `Reorderable` run inside the ONE virtualized plan list rather than a second geometry.
//
// EVERY AFFORDANCE HERE MUTATES THROUGH `SidebarPreferences.Dispatch` (via the pane's command methods), so every edit
// is reduced, undoable, autosaved and visible in the same frame — there is no staged apply and nothing to reconcile.
//
// P3 — OPTIONS LIVE ON THE OBJECT. The "…" opens the customizer's own `SidebarPropertyPanel` as a popover anchored to
// THIS card. That panel is not forked and not reimplemented: only its HOST changed, from a docked column whose subject
// silently changed under you to a popover whose subject is the thing you clicked.
static class SidebarPaneEditCard
{
    /// <summary>The card's inner plate height. The SLOT is <c>SidebarPaneMetrics.EditCardHeight</c> tall exactly — the
    /// air is PADDING on the slot root, never a Margin, so the measured extent the VariableList seeds from and the
    /// uniform pitch <c>Reorderable</c> assumes are the same number (the <c>SidebarPaneSlot.Banded</c> rule).</summary>
    const float PlateInsetY = 2f;

    /// <summary>Dimming for a section the user has hidden. It stays IN the canvas (P2) — never removed from view — so
    /// the dimming plus the eye-off tint is the whole difference between "hidden" and "gone".</summary>
    const float HiddenOpacity = 0.55f;

    /// <summary>One card. <paramref name="chevron"/> is built by the SLOT (a chevron is a hook-owning component whose
    /// ctor args freeze at mount, so its live-state probe must capture the recycling slot and never a section id).</summary>
    public static Element Build(SidebarPane owner, SidebarSectionSpec section, int planIndex, Element chevron)
    {
        string id = section.Id;
        bool pinned = SidebarEditPlan.IsPinnedCard(id);
        bool hidden = section.Hidden;
        bool open = owner.EditShowsBody(section);
        // A Divider / Header card has nothing to reveal, so it gets no disclosure mark and no click — an affordance that
        // opens onto nothing is the failure this rework is named after. The chevron's SLOT is still occupied (by a
        // spacer of the same width), because every card shares one recycle pool and a uniform child shape is what keeps
        // a recycle from restructuring the row.
        bool expandable = SidebarEditPlan.HasBody(section.Kind);
        // A card in the armed drag band gets its focus stop (and the Space/arrow lift keys) from `Reorderable.Item`'s
        // wrapper; adding a second one here would give every card two tab stops and bury the keyboard reorder.
        bool inBand = owner.TryEditSectionBand(planIndex, out _);

        var kids = new List<Element>(7);

        // The GRIP is a pure affordance mark: the drag source is the whole card (installed by `Reorderable.Item`), so a
        // grip that were its own hit target would only shrink the area the gesture works from. The pinned Shortcuts
        // card has none, because it genuinely cannot be dragged.
        kids.Add(new BoxEl
        {
            Width = 12f, Height = SidebarPaneMetrics.EditCardHeight - PlateInsetY * 2f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children = [pinned ? Blank(12f) : Icon(Icons.GripperBar, 12f, Tok.TextTertiary)],
        });

        // The KIND mark, from the customizer's single owner of "a kind's glyph" (CzGlyphs.ForKind — the outline, the
        // inspector header and the property panel all read it, so a renamed section is still identifiable at a glance).
        kids.Add(new BoxEl
        {
            Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
            Fill = open ? Tok.AccentSubtle : Tok.FillSubtleSecondary,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
            Children = [Icon(CzGlyphs.ForKind(section.Kind), 13f,
                             open ? Tok.AccentTextPrimary : Tok.TextSecondary)],
        });

        kids.Add(new TextEl(SidebarPaneText.TitleOf(section))
        {
            Size = 13f, Weight = 600,
            Color = hidden ? Tok.TextTertiary : Tok.TextPrimary,
            Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        });

        // HIDDEN wins the trailing slot over the count (P2's badge). A hidden section stays in the canvas, dimmed, and
        // this word is what says which of "dimmed" and "gone" it is — the more useful fact by far, and swapping rather
        // than adding keeps the 44-DIP card's trailing cluster one fixed width in a ~280-DIP pane.
        //
        // Otherwise the COUNT, through the pane's one quiet badge — and only where the document can answer honestly: a
        // projected section shows nothing rather than a number the planner would have to guess (SidebarEditPlan.CardCount).
        if (hidden) kids.Add(HiddenTag());
        else
        {
            int count = SidebarEditPlan.CardCount(section);
            if (count >= 0) kids.Add(SidebarCounts.Number(count));
        }

        // The EYE. One glyph for both states (the whitelisted icon font carries no struck-through eye), so the STATE is
        // carried by the tint plus the card's own dimming — exactly what the customizer outline already does. The
        // pinned Shortcuts card has none: `SetSectionHidden("topbar")` is an `UnknownSection` rejection, and an
        // affordance that silently rejects is worse than one that is not offered.
        if (!pinned)
            kids.Add(Affordance(Icons.RevealPassword, Loc.Get(hidden ? SidebarPaneLoc.EditShow : SidebarPaneLoc.EditHide),
                () => owner.SetSectionHidden(id, !hidden),
                hidden ? Tok.AccentTextPrimary : Tok.TextSecondary, focusable: false));

        // The "…" — the section's options, anchored to the section (P3). Also absent on the pinned card: the sentinel
        // is not in `Sections`, so the property panel would resolve no subject and render its "select a section" state.
        // The band's ITEMS are still fully editable — expand the card and its real rows appear, whose reorder routes
        // through `SidebarItemCommands` to `MoveTopBarItem`.
        if (!pinned)
            kids.Add(Embed.Comp(() => new SidebarSectionOptionsButton(owner, id)) with { Key = "sec-opt:" + id });

        kids.Add(expandable ? chevron : Blank(10f));

        // An explicit local, never a ternary against null inside the initializer: a lambda has no natural type there
        // (the note `SidebarSectionHeader` and `SidebarPaneSlot.Header` both carry).
        Action? activate = null;
        if (expandable) activate = () => owner.ToggleEditExpanded(id);

        var plate = new BoxEl
        {
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f,
            Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Padding = new Edges4(4f, 0f, 4f, 0f),
            Corners = Radii.ControlAll,
            // The same selection-aware ramp the outline's cards use, so the two surfaces read as one system while both
            // exist. The EXPANDED card wears the selected plate: it is the one whose rows are on screen.
            Fill = open ? WaveeColors.SelectedRest : Tok.FillCardDefault,
            HoverFill = open ? WaveeColors.SelectedHover : Tok.FillCardSecondary,
            PressedFill = open ? WaveeColors.SelectedPressed : Tok.FillSubtleTertiary,
            BorderWidth = 1f,
            BorderColor = open ? Tok.AccentSubtle : Tok.StrokeCardDefault,
            BrushTransitionMs = Motion.ControlFaster,
            Opacity = hidden ? HiddenOpacity : 1f,
            Role = expandable ? AutomationRole.Button : AutomationRole.None,
            Cursor = expandable ? CursorId.Hand : CursorId.Arrow,
            Focusable = !inBand,
            OnClick = activate,
            // PHASE 3 — the palette→canvas DROP. See PaletteDrop's remarks for why the target is the CARD and not the
            // band's `Reorderable`.
            DropTarget = PaletteDrop(owner, id),
            Children = [.. kids],
        };

        if (owner.MenuOverlay is { } svc && !pinned)
            plate = plate.WithContextMenu(svc, () => CardMenu(owner, id));

        return new BoxEl
        {
            Key = id,
            Direction = 1, Height = SidebarPaneMetrics.EditCardHeight, Shrink = 0f,
            // THE ONE INSET: the card is a plan row inside the padded list, so it takes the ROW inset like every other
            // band — never a second horizontal literal (SidebarPaneMetrics.RowInset).
            Padding = new Edges4(SidebarRowGeometry.RowInsetLeft, PlateInsetY,
                                 SidebarRowGeometry.RowInsetRight, PlateInsetY),
            Children = [plate],
        };
    }

    /// <summary>
    /// PHASE 3 — the companion palette's chips land HERE: each card is its own drop target, and a drop inserts the new
    /// section immediately ABOVE the card it landed on (<c>SidebarEditPlan.ToAddSection</c>, pure and unit-tested).
    ///
    /// <para><b>Why the card and not the band's <c>Reorderable</c>.</b> The plan's first choice was the band's foreign
    /// seams (<c>CanAcceptForeign</c>/<c>ForeignCaption</c>/<c>OnCrossCommit</c>), and they are genuinely unreachable
    /// here: every one of them is published through the <c>DropTargetSpec</c> that <c>Reorderable.List(body)</c> installs
    /// on its wrapper, and the pane mounts NO wrapper — each band is a run inside the ONE virtualized plan list, which is
    /// also why <c>ShowInsertionLine</c> is false. With no wrapper there is no drop target, no <c>_listNode</c>, and
    /// <c>SlotFromPosition</c> would measure slot 0 from the wrong origin (the cards start several rows into the list) —
    /// i.e. the cue and the outcome disagreeing by a row, the exact class of bug this rework is named after. Wrapping the
    /// pane's whole scroll body in a <c>List(...)</c> would register the target but keep the bad geometry; fixing the
    /// geometry needs an origin/offset seam on <c>Reorderable</c>, which is an engine change and out of scope.
    /// A per-card target needs neither: the card the pointer is over IS the insertion point, so there is no pointer math
    /// in app code at all (the dnd skill's "declare intent, never coordinates").</para>
    ///
    /// <para>The band's own reorder payload is a <c>ReorderPayload</c> whose <c>Item</c> is null (the section band sets
    /// <c>ItemOf = null</c>), so <c>Drop.Target&lt;T&gt;</c> fails to unwrap it and every gate here answers false. A
    /// card-to-card drag therefore never becomes this target's business: it is not accepted, <c>RefusalCaption</c>
    /// returns null for a payload that did not unwrap (so no reason is published and — with no chip resolved for a
    /// <c>ReorderPayload</c> — no not-allowed cue is drawn either), and the reorder still commits through
    /// <c>Reorderable</c>'s own L1 gesture completion, which does not consult the drop effect.</para>
    ///
    /// <para>Every delegate below runs once per frame per card while a drag is live (edge auto-scroll re-projects under a
    /// still pointer and the spotlight refresh re-tests acceptance), so none of them allocates: the accept test is a
    /// count comparison and both captions are interned table lookups of constant keys.</para>
    /// </summary>
    static DropTargetSpec PaletteDrop(SidebarPane owner, string sectionId) =>
        Drop.Target<SidebarSectionDropPayload>(
            SidebarEditPlan.SectionDragKind,
            accepts: _ => owner.CanAcceptPaletteDrop,
            onDrop: (payload, _) => owner.AddSectionFromPalette(sectionId, payload),
            caption: _ => Loc.Get(SidebarPaneLoc.EditDropHere),
            // A refusal that publishes nothing is indistinguishable from empty space (dnd pitfall 10), and the ONE way
            // this target refuses — the 40-section cap — is a reason the user can act on.
            refusalCaption: _ => Loc.Get(SidebarPaneLoc.EditDropFull));

    /// <summary>The card's context menu — where the NON-DRAG ways to move a section live (P6: drag is one of several
    /// ways, never the only one). Right-click reaches it whatever the drag band's state is, which matters because the
    /// band disarms itself while a card is expanded; the keyboard lift (Space · arrows · Space · Esc) covers the armed
    /// case and is built into <c>Reorderable</c>.</summary>
    static ContextMenuModel? CardMenu(SidebarPane owner, string sectionId)
    {
        if (owner.Prefs is not { } prefs) return null;
        var spec = prefs.Layout.Find(sectionId);
        if (spec is null) return null;

        var at = prefs.Layout.Locate(sectionId);
        int siblings = at.Parent is null ? prefs.Layout.Sections.Count : at.Parent.ChildList.Count;
        string title = SidebarPaneText.TitleOf(spec);

        var rows = new List<MenuFlyoutItem>(7)
        {
            new(Loc.Get(SidebarPaneLoc.EditMoveUp), default, at.Index > 0, () => owner.MoveSectionBy(sectionId, -1)),
            new(Loc.Get(SidebarPaneLoc.EditMoveDown), default, at.Index >= 0 && at.Index < siblings - 1,
                () => owner.MoveSectionBy(sectionId, 1)),
            MenuFlyoutItem.Separator,
            new(Loc.Get(spec.Hidden ? SidebarPaneLoc.EditShow : SidebarPaneLoc.EditHide), default, true,
                () => owner.SetSectionHidden(sectionId, !spec.Hidden)),
            new(Loc.Get(SidebarPaneLoc.EditDuplicate), default, true,
                () => owner.DuplicateEditSection(sectionId,
                    Loc.Format(SidebarPaneLoc.EditDuplicateSuffix, ("name", title)))),
            MenuFlyoutItem.Separator,
            new(Loc.Get(SidebarPaneLoc.EditRemove), Icons.Delete, true, () => owner.RemoveEditSection(sectionId)),
        };
        return new ContextMenuModel(rows);
    }

    /// <summary>One 24-DIP card affordance. Non-focusable by default: the card's own focus stop (or the reorder
    /// wrapper's) is the row's, and every command here is also in the context menu, so nothing is unreachable.</summary>
    static Element Affordance(string glyph, string tip, Action onClick, ColorF tint, bool focusable)
    {
        var box = new BoxEl
        {
            Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Cursor = CursorId.Hand, Focusable = focusable,
            Role = AutomationRole.Button,
            OnClick = onClick,
            Children = [Icon(glyph, 12f, tint)],
        }.Interactive(Interaction.Subtle);
        return ToolTip.Wrap(box, tip);
    }

    static Element Blank(float size) => new BoxEl { Width = size, Height = 0f, Shrink = 0f };

    /// <summary>The eye-off badge: the ONE word that distinguishes a section the user hid from one they removed. It
    /// reuses the customizer's existing "Hidden" string rather than minting a second spelling of it.</summary>
    static Element HiddenTag() => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(6f, 1f, 6f, 2f), Corners = CornerRadius4.All(Radii.Full),
        Fill = Tok.FillSubtleSecondary, HitTestVisible = false,
        Children =
        [
            new TextEl(Loc.Get(SidebarPaneLoc.EditHidden))
            {
                Size = 10f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1,
            },
        ],
    };
}

/// <summary>
/// The card's "…" — and the whole of P3's "options live on the object".
///
/// <para>It opens the customizer's <c>SidebarPropertyPanel</c> in a popover anchored to THIS card. That panel, the
/// <c>Cz*</c> control rows behind it and the item/action pickers under those are re-hosted VERBATIM: the only change on
/// their side is that the reference-stable holder they always took is now the <c>ISidebarEditHost</c> interface, which
/// the shared <c>SidebarEditSession</c> implements as well as the companion page. Forking that control set would have
/// duplicated the per-kind option table, the schema-generated extension rows and the controlled-input/rejection
/// contract — the exact "same artifact defined twice" this architecture exists to prevent.</para>
///
/// <para>Its own component because it needs an anchor node, an overlay handle and their refs — hooks a static row
/// builder cannot own and hooks a RECYCLING slot must not grow. Keyed by section id at the call site, so a recycle onto
/// another section remounts it rather than leaving a frozen subject behind.</para>
/// </summary>
sealed class SidebarSectionOptionsButton : Component
{
    /// <summary>The popover's box. 320 wide is the docked inspector's own width, which is what
    /// <c>CzRow.ComboWidth</c> (264) and the panel's two-column row contract were measured against — re-hosting at any
    /// other width would start clipping the controls the panel spent a round fixing.</summary>
    const float PanelWidth = 320f;
    const float PanelHeight = 520f;

    readonly SidebarPane _owner;
    readonly string _sectionId;

    public SidebarSectionOptionsButton(SidebarPane owner, string sectionId)
    {
        _owner = owner; _sectionId = sectionId;
    }

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (_owner.MenuOverlay is not { } svc) return;
            if (_owner.EditHost is not { } host) return;
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }

            // The subject is what was CLICKED — the whole point of P3. The panel reads it back through
            // `host.Selected`, which for the session IS `OptionsSection`, so there is one subject and not two that
            // could drift.
            host.Select(_sectionId);
            handle.Value = svc.Open(
                () => anchor.Value,
                Body,
                // To the RIGHT of the sidebar, top-aligned with the card: a bottom-aligned flyout over a 44-DIP card in
                // a narrow pane would cover the very rows the options are about.
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss,
                                 Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true });
            handle.Value.ClosedAction = () =>
            {
                handle.Value = null;
                // Clear the subject on close so a stale one cannot make the NEXT popover open on the wrong section for
                // a frame. The session reads it with Peek, so this never re-plans the canvas.
                if (string.Equals(host.Selected.Peek(), _sectionId, StringComparison.Ordinal)) host.Select(null);
            };
        }

        Element Body()
        {
            // Keyed by the subject: the panel's rows key themselves by section id too (props freeze at mount), so this
            // remounts the whole surface when the popover is reopened on another card.
            Element[] kids = _owner.EditHost is { } host
                ? [Embed.Comp(() => new SidebarPropertyPanel(host, PanelScrollKey))
                    with { Key = "sec-props:" + _sectionId }]
                : Array.Empty<Element>();
            return new BoxEl
            {
                Direction = 1, Width = PanelWidth, Height = PanelHeight, MinHeight = 0f, ClipToBounds = true,
                Children = kids,
            };
        }

        return ToolTip.Wrap(new BoxEl
        {
            Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [Icon(Icons.More, 14f, Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle), Loc.Get(SidebarPaneLoc.EditOptions));
    }

    /// <summary>Its OWN scroll key: the docked inspector still mounts the same panel under
    /// <c>"customizer.props"</c> until Phase 3 removes it, and two surfaces sharing one saved offset is the same bug the
    /// pane's <c>".drawer"</c> suffix exists to prevent.</summary>
    const string PanelScrollKey = "sidebar.section.props";
}
