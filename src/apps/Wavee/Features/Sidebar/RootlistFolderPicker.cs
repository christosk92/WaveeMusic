using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// "MOVE TO FOLDER…" — the third non-mouse route into the rootlist (D12), beside Move up / Move down and the Alt+arrows.
//
// WHY A PICKER AT ALL. Move up/down walk one sibling at a time and "Move out of {parent}" climbs exactly one level, so
// filing a playlist into a folder that is not adjacent to it was a DRAG and nothing else — across a scrolling 240-DIP
// pane, possibly into a folder that is currently collapsed. The picker names every legal destination at once and needs
// no pointer travel at all.
//
// WHAT IT REUSES. The flyout shell, the search field and the row rhythm are `PlaylistPickerPanel`'s; the legality rule
// is `RootlistOps.CheckMoves` over the store's marker stream, reached through `RootlistTreeNav` (the same authority the
// drop cue refuses with, so the picker cannot offer a destination a drag would refuse); and the commit is
// `FolderActions.Commit` — i.e. `WaveeResourceDrop.
// MoveRootlist`, the very call a DROP makes, with its awaited failure mapping, its announce, its "Moved to {name}"
// toast and its Undo.
//
// HOSTED IN A CONTENT DIALOG, not an anchored flyout: this opens from a CONTEXT MENU, and the menu that launched it is
// gone by invoke time, so there is no anchor node left to place against. Exactly the reason `Menus.OpenPicker` hosts
// the playlist picker the same way.

/// <summary>Opens the "Move to folder…" / "Move {n} to folder…" destination picker for a rootlist selection.</summary>
static class RootlistFolderPicker
{
    /// <summary>Open the picker for a whole selection of <c>SidebarLibraryEntry.Id</c>s. Silent no-op when none of the
    /// rows is in the published tree or when the selection has nowhere legal to go — an empty picker is a worse answer
    /// than a verb the menu did not offer, which is why <c>NavExtras</c> decides the same question before drawing the row.
    /// <para>The destination list is built from the BATCH: a folder is offered only if the WHOLE selection may be filed
    /// into it (<c>RootlistDropDecision.Check</c> over <c>RootlistBatchOrder.For</c>), so every selected subtree drops
    /// out for the same reason a drag refuses it — one authority, asked once.</para></summary>
    public static void Open(ActionServices s, IReadOnlyList<string> entryIds)
    {
        if (s.Overlay is not { } overlay || s.Library is not { } lib) return;
        var tree = s.Sidebar?.Binder?.CurrentInput.PlaylistTree;
        var markers = lib.RootlistMarkers;
        var selection = RootlistSelection.Normalize(tree, entryIds);
        if (selection.Count == 0) return;

        // A SNAPSHOT, taken at open. Component props freeze at mount, and the projection is not a signal this panel can
        // subscribe to — so the list the user sees is the tree as it stood when the menu verb fired. The COMMIT re-reads
        // the live tree (below), which is what keeps a mid-flight desktop rootlist change from landing the move against
        // stale sibling indices: the worst case is a destination that has since disappeared, and that commit resolves to
        // nothing rather than to the wrong folder.
        var ids = new string[selection.Count];
        for (int i = 0; i < selection.Count; i++) ids[i] = selection[i].Id;
        var destinations = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(tree, markers, ids, destinations);
        if (destinations.Count == 0) return;

        // The panel names WHAT is moving: one row by name, a selection by its count.
        string subject = selection.Count == 1 ? selection[0].Name : Strings.Sidebar.ItemCount(selection.Count);

        OverlayHandle? handle = null;
        handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Sidebar.MoveToFolderTitle);
            d.PrimaryText = "";                                   // rows act; the dialog only needs a dismiss
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Close;
            d.Content = Embed.Comp(() => new RootlistFolderPickerPanel
            {
                Destinations = destinations,
                Source = subject,
                Pick = choice => { Commit(s, ids, in choice); handle?.Close(); },
            });
        });
    }

    /// <summary>The N=1 sugar — a row menu's "Move to folder…" is a selection of one.</summary>
    public static void Open(ActionServices s, string entryId) => Open(s, [entryId]);

    /// <summary>Commit one picked destination against the LIVE tree, as ONE batch.</summary>
    static void Commit(ActionServices s, IReadOnlyList<string> entryIds, in RootlistFolderChoice choice)
    {
        var tree = s.Sidebar?.Binder?.CurrentInput.PlaylistTree;
        var markers = s.Library?.RootlistMarkers;
        var live = RootlistSelection.Normalize(tree, entryIds);
        if (live.Count == 0) return;
        if (choice.IsTopLevel)
        {
            // Top level = "after everything at depth 0". TryRange's exclusive end is what lands it after a TRAILING
            // FOLDER instead of inside it — the same anchor the tree-end drop slot uses.
            if (RootlistTreeNav.TryTopLevelAnchor(tree, markers, entryIds, out var anchor))
                FolderActions.Commit(s, live, anchor, RootlistDropPlacement.After, "");
            return;
        }
        FolderActions.Commit(s, live, new RootlistItemRef(choice.FolderId, IsFolder: true),
                             RootlistDropPlacement.Inside, choice.Name);
    }
}

/// <summary>The picker's content: a live search field over the frozen destination list, the pinned <b>Top level</b> row,
/// then the folders indented by their tree depth.</summary>
public sealed class RootlistFolderPickerPanel : Component
{
    /// <summary>The destinations, in render order (Top level first). Frozen at mount — see the snapshot note in
    /// <c>RootlistFolderPicker.Open</c>.</summary>
    public required IReadOnlyList<RootlistFolderChoice> Destinations;
    /// <summary>The row being moved, named in the panel's one line of guidance.</summary>
    public required string Source;
    public required Action<RootlistFolderChoice> Pick;

    /// <summary>The per-depth indent of a folder row, matching the sidebar tree's own step so the picker's shape reads
    /// as the shape the user is looking at.</summary>
    const float IndentStep = 12f;

    public override Element Render()
    {
        var query = UseSignal("");
        string q = query.Value;
        var destinations = Destinations;
        var pick = Pick;
        string topLevel = Loc.Get(Strings.Sidebar.TopLevel);

        var rows = new List<Element>(destinations.Count + 1);
        for (int i = 0; i < destinations.Count; i++)
        {
            var choice = destinations[i];
            string label = choice.IsTopLevel ? topLevel : choice.Name;
            // The Top level row is never filtered away: it is the picker's ANCHOR row, and a search that hid it would
            // leave a user who typed a folder name and changed their mind with no way back to the un-nest.
            if (!choice.IsTopLevel && q.Length > 0
                && label.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(Row(label, choice, () => pick(choice)));
        }

        Element list = rows.Count > 0
            ? new ScrollEl
            {
                ContentSized = true, MaxHeight = 360f,
                Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() },
            }
            : new BoxEl
            {
                Height = 44f, AlignItems = FlexAlign.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
                Children = [new TextEl(Loc.Get(Strings.Sidebar.NoFolders)) { Size = 13f, Color = Tok.TextSecondary }],
            };

        return new BoxEl
        {
            Direction = 1, Width = 320f, Gap = Spacing.XS,
            Children =
            [
                new TextEl(Strings.Sidebar.MoveToFolderBody(Source))
                {
                    Size = 13f, Color = Tok.TextSecondary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                },
                Embed.Comp(() => new EditableText
                {
                    Placeholder = Loc.Get(Strings.Sidebar.FindFolder),
                    Width = 300f, Height = 32f, Text = query,
                }),
                list,
            ],
        };
    }

    static Element Row(string label, in RootlistFolderChoice choice, Action onClick) => new BoxEl
    {
        Key = choice.IsTopLevel ? "top-level" : choice.FolderId,
        Direction = 0, Height = 40f, AlignItems = FlexAlign.Center, Gap = 10f,
        Padding = new Edges4(6f + (choice.IsTopLevel ? 0f : choice.Depth * IndentStep), 0f, 8f, 0f),
        Corners = CornerRadius4.All(4f),
        Role = AutomationRole.Button, OnClick = onClick,
        Children =
        [
            // A flat-list glyph for the top-level row, a folder for everything else: the two destinations are
            // different KINDS of place, and the icon is what says so before the label is read.
            Icon(choice.IsTopLevel ? Icons.List : Icons.Folder, 18f, Tok.TextSecondary),
            new TextEl(label)
            {
                Size = 14f, Color = Tok.TextPrimary, Grow = 1f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        ],
    }.Interactive(Interaction.Subtle);
}
