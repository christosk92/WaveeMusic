using System.Text.Json;

namespace Wavee.Core.Sidebar;

// The custom-sidebar COMMAND SET — the only way a SidebarCustomLayout changes. In-memory only (never serialized), so
// unlike the payload model these ARE a record hierarchy. SidebarPreferences.Dispatch is the single mutation entry point:
// reduce -> if Changed, push the pre-image onto SidebarUndo, clear redo, write the document signal, persist.
//
// Undo is a PRE-IMAGE SNAPSHOT, not an inverse command (SidebarUndo.cs): the document and everything under it are
// immutable records, so an edit rebuilds only the spine (O(depth)) and structurally shares the rest. That is why
// ApplyTemplate and ResetLayout are ordinary single-step undoable commands with no special machinery.

/// <summary>The undo/redo label loc keys, one per command shape. Kept as consts (Wavee.Core cannot see the app's
/// generated <c>Strings</c> table) so a key is spelled exactly once.</summary>
public static class SidebarUndoLabels
{
    public const string AddSection = "sidebar.customizer.undo.addSection";
    public const string RemoveSection = "sidebar.customizer.undo.removeSection";
    public const string DuplicateSection = "sidebar.customizer.undo.duplicateSection";
    public const string RenameSection = "sidebar.customizer.undo.renameSection";
    public const string HideSection = "sidebar.customizer.undo.hideSection";
    public const string ShowSection = "sidebar.customizer.undo.showSection";
    public const string CollapseSection = "sidebar.customizer.undo.collapseSection";
    public const string ExpandSection = "sidebar.customizer.undo.expandSection";
    public const string MoveSection = "sidebar.customizer.undo.moveSection";
    public const string AddItem = "sidebar.customizer.undo.addItem";
    public const string MoveItem = "sidebar.customizer.undo.moveItem";
    public const string RemoveItem = "sidebar.customizer.undo.removeItem";
    public const string RelabelItem = "sidebar.customizer.undo.relabelItem";
    public const string ReiconItem = "sidebar.customizer.undo.reiconItem";
    public const string SetOption = "sidebar.customizer.undo.setOption";
    public const string SetQuery = "sidebar.customizer.undo.setQuery";
    // LAYOUT V2.
    public const string SetExtensionConfig = "sidebar.customizer.undo.setExtensionConfig";
    public const string SetItemAction = "sidebar.customizer.undo.setItemAction";
    // The shell TOP BAR band (one global list on the same document, so it rides the same undo ring).
    public const string AddTopBarItem = "sidebar.customizer.undo.addTopBarItem";
    public const string MoveTopBarItem = "sidebar.customizer.undo.moveTopBarItem";
    public const string RemoveTopBarItem = "sidebar.customizer.undo.removeTopBarItem";
    public const string ApplyTemplate = "sidebar.customizer.undo.applyTemplate";
    public const string Reset = "sidebar.customizer.undo.reset";
}

public abstract record SidebarCommand
{
    /// <summary>Drives the undo/redo tooltip ("Undo: Add section") and the a11y announcement.</summary>
    public abstract string LabelLocKey { get; }
}

/// <summary>Inserts a fresh section of <paramref name="Kind"/> at <paramref name="Index"/> under
/// <paramref name="ParentId"/> (null = top level).
/// <para><paramref name="Item"/> is an OPTIONAL seed member so a picker-driven add (an EntityEmbed's spotlight target,
/// a first link) is ONE undoable step instead of AddSection+AddItem. Omitting it is legal for every kind — an
/// item-less EntityEmbed plans as an Empty "pick something to spotlight" row rather than being rejected, so a UI that
/// dispatches AddSection then AddItem also works.</para>
/// <para><paramref name="Extension"/> is REQUIRED for <see cref="SidebarSectionKind.Extension"/> (LAYOUT V2) — the palette
/// always knows which contribution the user picked, and a ref-less contributed section would be dead weight. It is
/// ignored for every other kind.</para></summary>
public sealed record AddSection(SidebarSectionKind Kind, int Index, string? ParentId = null,
    SidebarItemSpec? Item = null, SidebarExtensionRef? Extension = null) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.AddSection;
}

public sealed record RemoveSection(string SectionId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RemoveSection;
}

/// <summary>Deep-clones the section (and children) with fresh ids for every section and item, inserted immediately
/// after the original.
/// <para><paramref name="TitleOverride"/> carries the localized "{name} (copy)" title the customizer formats — Wavee.Core
/// has no <c>Loc</c>, so the copy's literal title is supplied by the caller. When null the clone keeps the original's
/// Title/TitleLocKey verbatim.</para></summary>
public sealed record DuplicateSection(string SectionId, string? TitleOverride = null) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.DuplicateSection;
}

public sealed record RenameSection(string SectionId, string? Title) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RenameSection;
}

public sealed record SetSectionHidden(string SectionId, bool Hidden) : SidebarCommand
{
    public override string LabelLocKey => Hidden ? SidebarUndoLabels.HideSection : SidebarUndoLabels.ShowSection;
}

public sealed record SetSectionCollapsed(string SectionId, bool Collapsed) : SidebarCommand
{
    public override string LabelLocKey =>
        Collapsed ? SidebarUndoLabels.CollapseSection : SidebarUndoLabels.ExpandSection;
}

public sealed record MoveSection(string SectionId, string? NewParentId, int NewIndex) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.MoveSection;
}

public sealed record AddItem(string SectionId, SidebarItemSpec Item, int Index) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.AddItem;
}

public sealed record MoveItem(string FromSectionId, int FromIndex, string ToSectionId, int ToIndex) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.MoveItem;
}

public sealed record RemoveItem(string SectionId, string ItemId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RemoveItem;
}

public sealed record SetItemLabel(string SectionId, string ItemId, string? Label) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RelabelItem;
}

public sealed record SetItemIcon(string SectionId, string ItemId, string? IconName) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.ReiconItem;
}

public sealed record SetDisplayOption(string SectionId, SidebarDisplayField Field, int Value) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetOption;
}

public sealed record SetQuery(string SectionId, SidebarEntityQuery Query) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetQuery;
}

/// <summary>LAYOUT V2 — replace an <see cref="SidebarSectionKind.Extension"/> section's opaque contribution config (what
/// the inspector's schema-generated property controls edit). The config is never interpreted by the reducer: it is only
/// size-checked (&gt; <see cref="SidebarExtensionRef.MaxConfigBytes"/> ⇒
/// <see cref="SidebarRejectReason.ConfigTooLarge"/>) and cloned so the document owns it.
/// <para>Ordinary undoable single step. On a non-Extension section it is a <c>NoChange</c>, exactly like SetQuery on a
/// non-EntityList; on an Extension section with no ref it is <c>ExtensionRefMissing</c>.</para></summary>
public sealed record SetExtensionConfig(string SectionId, JsonElement Config) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetExtensionConfig;
}

/// <summary>LAYOUT V2 — bind (or, with <paramref name="Binding"/> null, unbind) an item's action. Orthogonal to
/// <see cref="SidebarItemSpec.Target"/> on purpose: the picker sets Target = Action when it CREATES the item, and this
/// command only ever rewrites the binding, so re-binding can never silently turn a playlist row into an action row.
/// <para>A malformed binding (blank provider/action id) is a NoChange, not a silent clear.</para></summary>
public sealed record SetItemAction(string SectionId, string ItemId, SidebarActionBinding? Binding) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.SetItemAction;
}

// ── the shell TOP BAR band ────────────────────────────────────────────────────────────────────────────────────────────
// Three dedicated commands rather than AddItem/MoveItem/RemoveItem against a sentinel section: the band is a flat global
// list with its own cap (SidebarLayoutReducer.MaxTopBarItems), and the item commands' section arms carry per-KIND rules
// (AcceptsItems, EntityEmbed's retarget, the lazy Pinned-override prune) that mean nothing here. The three item-PROPERTY
// commands do route to the band, addressed by SidebarIds.TopBarSection — see that constant for the reasoning.

/// <summary>Insert a shortcut into the shell's customizable top-bar band at <paramref name="Index"/> (clamped).
/// <para>Reduced against <see cref="SidebarCustomLayout.EffectiveTopBar"/>, so the FIRST add to a never-customized band
/// materializes the built-in default alongside the new item rather than silently discarding Home.</para></summary>
public sealed record AddTopBarItem(SidebarItemSpec Item, int Index) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.AddTopBarItem;
}

/// <summary>Reorder the band. Indices are into <see cref="SidebarCustomLayout.EffectiveTopBar"/>; <paramref name="ToIndex"/>
/// is interpreted AFTER the removal (the standard <c>Reorderable.OnReorder</c> contract, same as <c>MoveItem</c>).</summary>
public sealed record MoveTopBarItem(int FromIndex, int ToIndex) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.MoveTopBarItem;
}

/// <summary>Drop a shortcut from the band. Removing the last one leaves an EMPTY list, never null — "the user emptied the
/// band" and "the user never customized it" are different states (null re-renders the built-in Home).</summary>
public sealed record RemoveTopBarItem(string ItemId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.RemoveTopBarItem;
}

public sealed record ApplyTemplate(string TemplateId) : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.ApplyTemplate;
}

public sealed record ResetLayout() : SidebarCommand
{
    public override string LabelLocKey => SidebarUndoLabels.Reset;
}

/// <summary>The reducer's verdict. <c>Changed == false</c> ⇒ the caller pushes NOTHING to undo and does NOT autosave.</summary>
public readonly record struct SidebarCommandResult(
    SidebarCustomLayout Layout,   // the new document (== input when !Changed)
    bool Changed,                 // false ⇒ no undo push, no save, no signal write
    SidebarRejectReason Reason)   // why nothing changed (diagnostics + a customizer inline message)
{
    public static SidebarCommandResult Ok(SidebarCustomLayout layout)
        => new(layout, true, SidebarRejectReason.None);

    public static SidebarCommandResult Reject(SidebarCustomLayout layout, SidebarRejectReason reason)
        => new(layout, false, reason);
}

/// <summary>Why a command changed nothing. Append only — the customizer maps these to inline messages.</summary>
public enum SidebarRejectReason : byte
{
    None = 0, UnknownSection, UnknownItem, NestingTooDeep, KindNotNestable,
    KindDoesNotAcceptItems, DuplicateItem, NoChange, UnknownTemplate, InvalidIcon, SectionCapReached,
    // LAYOUT V2.
    ConfigTooLarge,        // an extension config (or action arguments) over SidebarExtensionRef.MaxConfigBytes
    ExtensionRefMissing,   // an Extension section without an addressable SidebarExtensionRef
    // PHASE 1 / defect 9. APPENDED, never inserted — the enum is persisted-adjacent (the customizer maps it to inline
    // messages and the tests pin the values).
    KindNotDuplicable,     // DuplicateSection on a store-backed kind (SidebarSectionKinds.IsStoreBacked)
}
