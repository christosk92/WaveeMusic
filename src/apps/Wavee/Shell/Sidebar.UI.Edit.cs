// ── Shell/Sidebar.UI.Edit.cs ───────────────────────────────────────────────────────────────────────────────────────
// Edit mode's pane: the edit bar and the Outline (design C.3) — the Show checkboxes, the section band (Classic), the
// pins band (both layouts, Q3), the Collections item band, the section chip, and the keys (Alt+↑/↓, Enter, Space
// lift, Esc, Ctrl+Z / Ctrl+Y)
//
// Role: UI
// Spec: sidebar-rework-implementation.md §P4.5, §P5.7 · design C.3, P.2a, Q3, Q5, Q7, Q8, Q16, Q17, V.11
// NAMED PARTIAL of Sidebar.UI.cs: EditPane

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Sidebar
{
    /// <summary>EDIT MODE'S PANE (design C.3): the edit bar over the Outline. Modal for the sidebar only — the page behind
    /// stays fully interactive.</summary>
    internal sealed class EditPane(PaneView owner) : Component
    {
        /// <summary>The mounted pane: the section chip's titles read its band through this static resolver.</summary>
        static EditPane? s_active;
        static readonly Func<DragState, DragChipSpec?> s_sectionChip = SectionChip;
        static readonly DragVisualStyle s_stationary = new() { Lift = DragLift.Stationary, Opacity = FluentGpu.Controls.Drag.SourceDimOpacity };

        readonly Signal<bool> _showAllPins = new(false);
        readonly List<SidebarOutlineRow> _rows = new(32);
        readonly List<string> _band = new(8);
        readonly List<string> _collections = new(8);
        readonly Dictionary<string, Signal<bool>> _checks = new(StringComparer.Ordinal);
        Reorderable? _sections, _pins, _items;
        NodeHandle _firstStop;

        public override Element Render()
        {
            var layout = Sidebar.Layout.Value;
            _ = Sidebar.LayoutVersion.Value;
            _ = Sidebar.PinsVersion.Value;
            _ = Sidebar.RingVersion.Value;
            var state = Sidebar.State;
            int playlists = Sidebar.Binder?.CurrentInput.PlaylistTree?.Count ?? 0;
            SidebarEditRules.Outline(state, layout, Sidebar.Pins.Items, Sidebar.PinnedLocked(), _showAllPins.Value, playlists, _rows);
            SidebarEditRules.Band(state.Of(layout), _band);

            var hooks = UseContext(InputHooks.Current);
            var post = UsePost();
            UseEffect(() =>
            {
                // The section chip (the customizer's freed slot): "Playlists" + its kind glyph while a section travels.
                var previous = Drag.ExtraChip;
                Drag.ExtraChip = s_sectionChip;
                s_active = this;
                return () =>
                {
                    if (ReferenceEquals(Drag.ExtraChip, s_sectionChip)) Drag.ExtraChip = previous;
                    if (ReferenceEquals(s_active, this)) s_active = null;
                };
            }, DepKey.Empty);
            UseLayoutEffect(() =>
            {
                // Once per Edit session (this component mounts on enter): keyboard and palette users land IN the Outline.
                post(() =>
                {
                    if (_firstStop.IsNull) return;
                    var target = hooks.FirstFocusableIn?.Invoke(_firstStop) ?? _firstStop;
                    hooks.FocusNode?.Invoke(target, true);
                });
            }, DepKey.Empty);

            return new BoxEl
            {
                Key = "edit-pane", Direction = 1, Grow = 1f,
                OnKeyDown = OnPaneKey,
                Children = [EditBar(), Outline(layout, state)],
            };
        }

        void OnPaneKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            if (e.KeyCode == Keys.Escape && e.Mods == KeyModifiers.None)
            {
                Sidebar.ExitEdit();
                e.Handled = true;
                return;
            }
            if (e.Mods == KeyModifiers.Ctrl && e.KeyCode is Keys.Z or Keys.Y)
            {
                if (e.KeyCode == Keys.Z) Sidebar.Undo(); else Sidebar.Redo();
                e.Handled = true;
            }
        }

        // ── the edit bar ───────────────────────────────────────────────────────────────────────────────────────────────

        Element EditBar()
        {
            bool canUndo = Sidebar.Ring.CanUndo, canRedo = Sidebar.Ring.CanRedo;
            return new BoxEl
            {
                Key = "edit-bar", Direction = 0, Height = 36f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 4f,
                Margin = new Edges4(SidebarRowGeometry.PaneEdge, SidebarRowGeometry.PaneTopInset, SidebarRowGeometry.PaneEdge, 2f),
                Padding = new Edges4(8f, 0f, 4f, 0f), Corners = Radii.ControlAll,
                Fill = Tok.FillLayerDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    Icon(Icons.Edit, 14f, Tok.TextSecondary),
                    Ui.Caption(Loc.Get("sidebar.edit.title")) with
                    {
                        Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Shrink = 1f, MinWidth = 0f,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new BoxEl { Grow = 1f },
                    ToolTip.Wrap(IconButton.Create(Icons.Undo, Sidebar.Undo, isEnabled: canUndo), Loc.Get("sidebar.undo.undoAction")),
                    ToolTip.Wrap(IconButton.Create(Icons.Redo, Sidebar.Redo, isEnabled: canRedo), Loc.Get("sidebar.undo.redo")),
                    Button.Create(Loc.Get("sidebar.edit.reset"), static () => { }, ButtonAppearance.Subtle, ControlSize.Small)
                        .WithContextMenu(owner.MenuOverlay, ResetMenu) with { ClickRequestsContext = true, Key = "edit-reset" },
                    Button.Accent(Loc.Get("sidebar.edit.done"), Sidebar.ExitEdit) with { Key = "edit-done" },
                ],
            };
        }

        /// <summary>The Reset ▾ menu (§P4.5): this layout (recorded) and everything (confirmed, §P4.6).</summary>
        ContextMenuModel? ResetMenu()
        {
            SidebarMenus.Overlay = owner.MenuOverlay;
            var layout = Sidebar.Layout.Peek();
            var rows = new[]
            {
                new SidebarMenuRow(SidebarMenuAction.ResetLayout, "sidebar.menu.resetLayout",
                    Enabled: SidebarLayoutRules.IsModified(Sidebar.State.Of(layout))),
                new SidebarMenuRow(SidebarMenuAction.ResetEverything, "sidebar.menu.resetEverything"),
            };
            return new ContextMenuModel(SidebarMenus.Map(rows, null, owner));
        }

        // ── the Outline ────────────────────────────────────────────────────────────────────────────────────────────────

        Element Outline(SidebarLayoutId layout, SidebarLayoutState state)
        {
            var overlay = state.Of(layout);
            _sections ??= new Reorderable("sidebar-section")
            {
                LiveProject = true, ShowInsertionLine = false, RequireDropOnList = true,
                DragStyle = s_stationary, AnnounceAssertive = true,
            };
            _pins ??= new Reorderable("sidebar-item:pins")
            {
                ItemExtent = SidebarEditRules.RowHeight, RequireDropOnList = true,
                DragStyle = s_stationary, AnnounceAssertive = true,
            };
            _items ??= new Reorderable("sidebar-item:collections")
            {
                ItemExtent = SidebarEditRules.RowHeight, RequireDropOnList = true,
                DragStyle = s_stationary, AnnounceAssertive = true,
            };
            _sections.ItemCount = _band.Count;
            _sections.ExtentOf = i => SidebarEditRules.ExtentOf(layout, overlay.Find(_band[i])!, Sidebar.Pins.Count, _showAllPins.Peek());
            _sections.OnReorder = (f, t) => Sidebar.Dispatch(new MoveSection(Sidebar.Layout.Peek(), _band[f], t));
            _sections.AnnounceText = a => Announce(a, SectionName(NameAt(_band, a.Index)));
            _pins.ItemCount = 0;
            _pins.OnReorder = (f, t) => Sidebar.MovePinRecorded(f, t);
            _pins.AnnounceText = a => Announce(a, Sidebar.Pins.Count == 0 ? "" : SidebarMenus.PinName(Sidebar.Pins[Math.Clamp(a.Index, 0, Sidebar.Pins.Count - 1)].Id));
            _items.ItemCount = 0;
            _items.OnReorder = (f, t) => Sidebar.Dispatch(new MoveItem(Sidebar.Layout.Peek(), "collections", _collections[f], t));
            _items.AnnounceText = a => Announce(a, ItemName(NameAt(_collections, a.Index)));

            var kids = new List<Element>(_rows.Count + 8);
            var band = new List<Element>(_band.Count);
            _collections.Clear();
            int slot = 0;
            bool first = true;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Kind == SidebarOutlineRowKind.Home)
                {
                    kids.Add(LockedHead(Icons.Home, SectionName("home")));
                    continue;
                }
                if (row.Kind != SidebarOutlineRowKind.Section) continue;   // the body rows are consumed by their section
                int end = i + 1;
                while (end < _rows.Count && _rows[end].Kind is not (SidebarOutlineRowKind.Section or SidebarOutlineRowKind.Settings)) end++;
                SidebarCatalogue.TryKindOf(row.SectionId, out var kind);
                if (row.Movable)
                {
                    // A band item: the Reorderable's slot is its place in the band; the wrapper takes the section keys.
                    string id = row.SectionId;
                    int at = slot++;
                    // The wrapper is the focus target (design C.3): slot 0's wrapper is the first stop, so Space lifts the section.
                    Element item = _sections.Item(at, SectionBlock(layout, row, kind, i + 1, end, first: false), key: id, transition: LayoutTransition.Slide);
                    band.Add(new BoxEl
                    {
                        Key = "block:" + id, Direction = 1, OnRealized = at == 0 ? SetFirstStop : null,
                        OnKeyDown = e => OnSectionKey(id, at, e), Children = [item],
                    });
                }
                else
                {
                    FlushBand(kids, band);
                    kids.Add(SectionBlock(layout, row, kind, i + 1, end, first));
                }
                first = false;
                i = end - 1;
            }
            FlushBand(kids, band);

            if (layout == SidebarLayoutId.Library)
            {
                kids.Add(LockedHead(Icons.Library, Loc.Get("sidebar.section.title.library")));
                kids.Add(HintRow(Loc.Get("sidebar.edit.libraryHint")));
            }
            kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Margin = new Edges4(8f, 6f, 8f, 6f), Fill = Tok.StrokeCardDefault });
            foreach (var row in _rows)
            {
                if (row.Kind != SidebarOutlineRowKind.Settings) continue;
                kids.Add(SettingsRow(layout, row));
                break;
            }
            kids.Add(new BoxEl
            {
                Padding = new Edges4(8f, 8f, 8f, 4f),
                Children =
                [
                    Ui.Caption(Loc.Get(layout == SidebarLayoutId.Library ? "sidebar.edit.footLibrary" : "sidebar.edit.footClassic")) with
                    {
                        Color = Tok.TextTertiary, MaxLines = 2,
                    },
                ],
            });

            return new ScrollEl
            {
                Grow = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = "sidebar.outline",
                Content = new BoxEl { Direction = 1, Padding = new Edges4(4f, 3f, 4f, 8f), Children = [.. kids] },
            };
        }

        /// <summary>Closes the band's run of wrappers into the section Reorderable's list (one list: the band is contiguous).</summary>
        void FlushBand(List<Element> kids, List<Element> band)
        {
            if (band.Count == 0) return;
            kids.Add(_sections!.List(new BoxEl { Direction = 1, Children = [.. band] }));
            band.Clear();
        }

        /// <summary>One section's block: its header, then its body rows (the Pinned and Collections inner bands, the
        /// Filters rows, the hint rows). <paramref name="from"/>..<paramref name="to"/> are its body rows in the Outline.</summary>
        Element SectionBlock(SidebarLayoutId layout, SidebarOutlineRow row, SidebarSectionKind kind, int from, int to, bool first)
        {
            var body = new List<Element>(8);
            Body(layout, kind, from, to, body);
            var kids = new List<Element>(body.Count + 1) { SectionHeaderRow(layout, row, kind, first) };
            kids.AddRange(body);
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Opacity = row.Shown ? 1f : 0.5f,
                Children = [.. kids],
            };
        }

        /// <summary>A section's body rows. Pins and Collections items are inner bands (Reorderable); the rest are plain rows.</summary>
        void Body(SidebarLayoutId layout, SidebarSectionKind kind, int from, int to, List<Element> into)
        {
            var pinKids = new List<Element>(8);
            var itemKids = new List<Element>(8);
            Element? showAll = null;
            for (int r = from; r < to; r++)
            {
                var row = _rows[r];
                switch (row.Kind)
                {
                    case SidebarOutlineRowKind.Pin:
                    {
                        // The Reorderable's wrapper is the focus target and keys bubble UP from it: this box takes the pin keys.
                        int at = pinKids.Count;
                        Element item = _pins!.Item(at, PinRow(Sidebar.Pins[at]), key: row.ItemId);
                        pinKids.Add(new BoxEl { Key = "pinwrap:" + row.ItemId, Direction = 1, OnKeyDown = e => OnPinKey(at, e), Children = [item] });
                        break;
                    }
                    case SidebarOutlineRowKind.ShowAllPins:
                        showAll = ShowAllRow(row.Count);
                        break;
                    case SidebarOutlineRowKind.Item:
                    {
                        if (kind == SidebarSectionKind.Collections)
                        {
                            int k = itemKids.Count;
                            _collections.Add(row.ItemId);
                            Element it = _items!.Item(k, ItemRow(layout, kind, row), key: row.ItemId);
                            string itemId = row.ItemId;
                            bool shown = row.Shown;
                            itemKids.Add(new BoxEl { Key = "itemwrap:" + itemId, Direction = 1, OnKeyDown = e => OnItemKey(k, itemId, shown, e), Children = [it] });
                        }
                        else into.Add(ItemRow(layout, kind, row));
                        break;
                    }
                    case SidebarOutlineRowKind.Hint:
                        // Every Hint row renders (a blank one where no text exists), so the block's height is ExtentOf's.
                        into.Add(HintRow(HintKey(kind) is { } hint ? Loc.Get(hint) : ""));
                        break;
                }
            }
            if (kind == SidebarSectionKind.Pinned)
            {
                _pins!.ItemCount = pinKids.Count;
                if (pinKids.Count > 0) into.Add(_pins!.List(new BoxEl { Direction = 1, Children = [.. pinKids] }));
                if (showAll is not null) into.Add(showAll);
            }
            if (kind == SidebarSectionKind.Collections && itemKids.Count > 0)
            {
                _items!.ItemCount = itemKids.Count;
                into.Add(_items!.List(new BoxEl { Direction = 1, Children = [.. itemKids] }));
            }
        }

        static string? HintKey(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.Pinned => "sidebar.pin.emptyHint",
            SidebarSectionKind.Recent => "sidebar.edit.recentHint",
            SidebarSectionKind.NewReleases => "sidebar.edit.newReleasesHint",
            _ => null,
        };

        /// <summary>A section header (40 tall): [checkbox or lock] [title] [summary] spacer [Unpin all shortcuts] [⋯] [grip].
        /// The header text is not a click target (Q8); the wrapper takes focus through the first stop.</summary>
        Element SectionHeaderRow(SidebarLayoutId layout, SidebarOutlineRow row, SidebarSectionKind kind, bool first)
        {
            string id = row.SectionId;
            string title = kind == SidebarSectionKind.Library ? Loc.Get("sidebar.edit.filters") : SectionName(id);
            var kids = new List<Element>(8)
            {
                SectionCheck(layout, row, kind, title),
                new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
            };
            if (SidebarEditRules.SummaryKeyOf(kind, row.Count) is { } key)
                kids.Add(Ui.Caption(Loc.Format(key, ("count", row.Count), ("total", SidebarCatalogue.ItemsOf(layout, kind).Count))) with
                {
                    Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis,
                });
            kids.Add(new BoxEl { Grow = 1f });
            if (kind == SidebarSectionKind.Pinned && SidebarMenus.LockingNames().Count > 0)
                kids.Add(Button.Create(Loc.Get("sidebar.menu.unpinShortcuts"), Sidebar.UnpinAllShortcutsRecorded, ButtonAppearance.Subtle, ControlSize.Small)
                    with { Key = "pinned-unpin-shortcuts", BlocksDragArm = true });
            if (owner.MenuOverlay is { } svc && owner.HeaderMenu(id) is { } menu)
                kids.Add(ToolTip.Wrap(SectionHeader.InlineButton(Icons.More, null, reveal: false, requestsContext: true).WithContextMenu(svc, menu),
                    Loc.Get(PaneLoc.SectionOptions)));
            if (row.Movable) kids.Add(Icon(Icons.GripperBar, 16f, Tok.TextTertiary));
            return new BoxEl
            {
                Key = "head:" + id, Direction = 0, Height = SidebarEditRules.SectionHeaderHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Gap = 4f, Padding = new Edges4(4f, 0f, 4f, 0f),
                OnRealized = first ? SetFirstStop : null,
                Children = [.. kids],
            };
        }

        /// <summary>The header's leading box: the Show checkbox; the Filters icon for Library's kind; a lock for a section
        /// that cannot be hidden. A locked Pinned shows the box disabled and says why.</summary>
        Element SectionCheck(SidebarLayoutId layout, SidebarOutlineRow row, SidebarSectionKind kind, string title)
        {
            if (kind == SidebarSectionKind.Library)
                return new BoxEl { Width = 24f, Height = 24f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.Filter, 14f, Tok.TextSecondary)] };
            if (!SidebarCatalogue.Hideable(kind))
                return new BoxEl { Width = 24f, Height = 24f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.Lock, 12f, Tok.TextTertiary)] };
            string id = row.SectionId;
            string? reason = row.Locked ? Loc.Format("sidebar.menu.unpinFirst", ("names", string.Join(", ", SidebarMenus.LockingNames()))) : null;
            return Check("section:" + id, row.Shown, !row.Locked, title, v =>
            {
                if (Sidebar.Dispatch(new SetSectionShown(layout, id, v)) != SidebarOpReject.None) return;
                // Showing an empty section is allowed; Done says so once (design C.3).
                if (v && HasNothingToShow(kind)) Sidebar.s_enabledEmptySection = title;
                else if (!v && Sidebar.s_enabledEmptySection == title) Sidebar.s_enabledEmptySection = null;
            }, reason);
        }

        /// <summary>True when a section has no rows to show right now: the same sources the section renders from.</summary>
        static bool HasNothingToShow(SidebarSectionKind kind) => kind switch
        {
            SidebarSectionKind.Pinned => Sidebar.Pins.Count == 0,
            SidebarSectionKind.Playlists => Sidebar.Binder?.CurrentInput.PlaylistTree is not { Count: > 0 },
            SidebarSectionKind.Recent => Sidebar.Binder?.CurrentInput.Played is not { Count: > 0 },
            SidebarSectionKind.NewReleases => Sidebar.Binder?.CurrentInput.NewReleases is not { Count: > 0 },
            _ => false,
        };

        /// <summary>A pin row (40): [glyph] [title] spacer [Unpin] [grip]. Its band wrapper takes the keys (<see cref="OnPinKey"/>).</summary>
        Element PinRow(SidebarPin pin)
        {
            string name = SidebarMenus.PinName(pin.Id);
            return new BoxEl
            {
                Key = "pin:" + pin.Id, Direction = 0, Height = SidebarEditRules.RowHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Gap = 4f, Padding = new Edges4(4f, 0f, 4f, 0f),
                Children =
                [
                    PinArt(pin),
                    new TextEl(name) { Size = 14f, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
                    new BoxEl { Grow = 1f },
                    ToolTip.Wrap(SectionHeader.InlineButton(Icons.UnPin, () => Sidebar.UnpinRecorded(pin.Id, name), reveal: false), Loc.Get("sidebar.pin.unpin")),
                    Icon(Icons.GripperBar, 16f, Tok.TextTertiary),
                ],
            };
        }

        /// <summary>A pin's 20-px leading art: a route pin wears its destination's glyph; an entity pin its cover, once the
        /// projection has resolved it (the kind glyph until then).</summary>
        static Element PinArt(SidebarPin pin)
        {
            Element art = pin.Kind == SidebarEntryKind.AppRoute
                ? Icon(Shell.Dest(Shell.Parse(pin.Id)).Glyph, 14f, Tok.TextSecondary)
                : ResolvedPin(pin.Id) is { } entry ? Cover.ForEntry(in entry, 20f) : Icon(Icons.Library, 14f, Tok.TextSecondary);
            return new BoxEl { Width = 20f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [art] };
        }

        /// <summary>The projection's resolved row for a pin (its cover, its kind), or null until the projection has it.</summary>
        static SidebarLibraryEntry? ResolvedPin(string id)
        {
            var pins = Sidebar.Binder?.CurrentInput.Pins;
            if (pins is null) return null;
            for (int i = 0; i < pins.Count; i++) if (pins[i].Id == id) return pins[i];
            return null;
        }

        /// <summary>An item row (40) of an item section: [checkbox] [glyph] [title] spacer [grip when movable]. Hidden items
        /// render at 0.5 opacity. A Collections row's band wrapper takes the keys (<see cref="OnItemKey"/>).</summary>
        Element ItemRow(SidebarLayoutId layout, SidebarSectionKind kind, SidebarOutlineRow row)
        {
            string section = row.SectionId, item = row.ItemId;
            var kids = new List<Element>(6)
            {
                Check("item:" + section + ":" + item, row.Shown, true, ItemName(item), v =>
                {
                    if (Sidebar.Dispatch(new SetItemShown(layout, section, item, v)) != SidebarOpReject.None) return;
                    // A hidden kind's chip is gone: step the active filter off it, as the menu's Filters rows do.
                    if (!v && kind == SidebarSectionKind.Library)
                        Sidebar.SetLibraryFilter(SidebarLibraryFilters.Effective((int)Sidebar.LibraryFilter.Peek(), Sidebar.Doc.Library.HiddenKinds));
                }),
                Icon(Shell.Dest(Shell.Parse(item)).Glyph, 16f, Tok.TextSecondary),
                new TextEl(ItemName(item)) { Size = 14f, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
                new BoxEl { Grow = 1f },
            };
            if (row.Movable) kids.Add(Icon(Icons.GripperBar, 16f, Tok.TextTertiary));
            return new BoxEl
            {
                Key = "item:" + section + ":" + item, Direction = 0, Height = SidebarEditRules.RowHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Gap = 4f, Padding = new Edges4(4f, 0f, 4f, 0f),
                Opacity = row.Shown ? 1f : 0.5f,
                Children = [.. kids],
            };
        }

        /// <summary>The footer's Settings row in the Outline (Q7: the Outline replaces the footer): its Show checkbox, and
        /// "Still in the profile menu" while hidden.</summary>
        Element SettingsRow(SidebarLayoutId layout, SidebarOutlineRow row)
        {
            var dest = Shell.Dest(new Shell.Route(Shell.RouteKind.Settings));
            var kids = new List<Element>(6)
            {
                Check("settings", row.Shown, true, dest.Title, v => Sidebar.Dispatch(new SetSectionShown(layout, "settings", v))),
                Icon(Icons.Settings, 16f, Tok.TextSecondary),
                new TextEl(dest.Title) { Size = 14f, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
                new BoxEl { Grow = 1f },
            };
            if (!row.Shown)
                kids.Add(Ui.Caption(Loc.Get("sidebar.edit.stillInProfile")) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f });
            return new BoxEl
            {
                Key = "settings-row", Direction = 0, Height = SidebarEditRules.SectionHeaderHeight, Shrink = 0f,
                AlignItems = FlexAlign.Center, Gap = 4f, Padding = new Edges4(4f, 0f, 4f, 0f),
                Opacity = row.Shown ? 1f : 0.5f,
                Children = [.. kids],
            };
        }

        /// <summary>A locked header with no checkbox ("Home", "Your Library"): a lock, the icon and the title.</summary>
        static Element LockedHead(string glyph, string title) => new BoxEl
        {
            Direction = 0, Height = SidebarEditRules.SectionHeaderHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 4f,
            Padding = new Edges4(4f, 0f, 4f, 0f),
            Children =
            [
                new BoxEl { Width = 24f, Height = 24f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.Lock, 12f, Tok.TextTertiary)] },
                Icon(glyph, 16f, Tok.TextSecondary),
                new TextEl(title) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
            ],
        };

        /// <summary>A 44-tall hint row whose caption sits at x 40 (under the header text).</summary>
        static Element HintRow(string text) => new BoxEl
        {
            Direction = 0, Height = SidebarEditRules.HintHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(40f, 0f, 8f, 0f),
            Children = [Ui.Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis }],
        };

        /// <summary>"Show all N pins" — the fold above <see cref="SidebarEditRules.PinFoldAt"/> (Q3).</summary>
        Element ShowAllRow(int count) => new BoxEl
        {
            Direction = 0, Height = SidebarEditRules.RowHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(40f, 0f, 4f, 0f),
            Children = [Button.Create(Loc.Format("sidebar.edit.showAllPins", ("count", count)), () => _showAllPins.Value = true, ButtonAppearance.Subtle, ControlSize.Small)],
        };

        /// <summary>The checkbox for row <paramref name="key"/> ("section:pinned", "item:collections:albums", "settings"): its
        /// signal is reused across renders and set to the overlay's truth before the element is built. A disabled box says
        /// <paramref name="reason"/> instead of "Show {name}".</summary>
        Element Check(string key, bool shown, bool enabled, string title, Action<bool> onChange, string? reason = null)
        {
            if (!_checks.TryGetValue(key, out var sig)) _checks[key] = sig = new Signal<bool>(shown);
            sig.SetIfChanged(shown);
            string tip = reason is { } why && !enabled ? why : Loc.Format("sidebar.edit.showInSidebar", ("name", title));
            // The drag-arm barrier sits on a wrapper box: CheckBox.Create returns an Element, and only BoxEl carries BlocksDragArm.
            return ToolTip.Wrap(new BoxEl
            {
                Key = "check:" + key, Shrink = 0f, BlocksDragArm = true,
                Children = [CheckBox.Create("", sig, onChange, isEnabled: enabled)],
            }, tip);
        }

        // ── keys ───────────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Keys the lifted Reorderable does not take, on a section wrapper (design Q8): Enter toggles Show; Alt+↑/↓
        /// moves one step, Alt+Shift+↑/↓ to the edge.</summary>
        void OnSectionKey(string id, int slot, KeyEventArgs e)
        {
            if (e.Handled) return;
            var layout = Sidebar.Layout.Peek();
            if (e.KeyCode == Keys.Enter && e.Mods == KeyModifiers.None)
            {
                var s = Sidebar.State.Of(layout).Find(id);
                if (s is not null && SidebarCatalogue.TryKindOf(id, out var kind) && SidebarCatalogue.Hideable(kind))
                    Sidebar.Dispatch(new SetSectionShown(layout, id, s.Hidden));
                e.Handled = true;
                return;
            }
            if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && (e.Mods & KeyModifiers.Alt) != 0)
            {
                int dir = e.KeyCode == Keys.Up ? -1 : 1;
                int to = SidebarEditRules.MoveTarget(slot, _band.Count, dir, toEdge: (e.Mods & KeyModifiers.Shift) != 0);
                if (to >= 0 && to != slot) Sidebar.Dispatch(new MoveSection(layout, id, to));
                e.Handled = true;
            }
        }

        /// <summary>Alt+↑/↓ on a pin row's wrapper (Alt+Shift: to the edge). The same in both layouts (Q3). Enter stops here:
        /// a pin has no Show, and the key must not reach the section wrapper and toggle Pinned.</summary>
        void OnPinKey(int index, KeyEventArgs e)
        {
            if (e.Handled) return;
            if (e.KeyCode == Keys.Enter) { e.Handled = true; return; }
            if ((e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) || (e.Mods & KeyModifiers.Alt) == 0) return;
            int to = SidebarEditRules.MoveTarget(index, Sidebar.Pins.Count, e.KeyCode == Keys.Up ? -1 : 1,
                                                 toEdge: (e.Mods & KeyModifiers.Shift) != 0);
            if (to >= 0 && to != index) Sidebar.MovePinRecorded(index, to);
            e.Handled = true;
        }

        /// <summary>Keys on a Collections item row's wrapper (design Q8): Enter toggles the item's Show (never the section's);
        /// Alt+↑/↓ moves one step, Alt+Shift+↑/↓ to the edge.</summary>
        void OnItemKey(int index, string itemId, bool shown, KeyEventArgs e)
        {
            if (e.Handled) return;
            if (e.KeyCode == Keys.Enter && e.Mods == KeyModifiers.None)
            {
                Sidebar.Dispatch(new SetItemShown(Sidebar.Layout.Peek(), "collections", itemId, !shown));
                e.Handled = true;
                return;
            }
            if ((e.KeyCode != Keys.Up && e.KeyCode != Keys.Down) || (e.Mods & KeyModifiers.Alt) == 0) return;
            int to = SidebarEditRules.MoveTarget(index, _collections.Count, e.KeyCode == Keys.Up ? -1 : 1,
                                                 toEdge: (e.Mods & KeyModifiers.Shift) != 0);
            if (to >= 0 && to != index) Sidebar.Dispatch(new MoveItem(Sidebar.Layout.Peek(), "collections", _collections[index], to));
            e.Handled = true;
        }

        void SetFirstStop(NodeHandle h) => _firstStop = h;

        // ── the section chip and the spoken sentences ──────────────────────────────────────────────────────────────────

        /// <summary>The drag chip while a band item travels: a section's title and kind glyph, or a pin's or item's name.</summary>
        static DragChipSpec? SectionChip(DragState state)
        {
            if (s_active is not { } pane || state.Payload is not ReorderPayload p || p.Index < 0) return null;
            switch (p.Owner.Kind)
            {
                case "sidebar-section":
                    if (p.Index >= pane._band.Count) return null;
                    string id = pane._band[p.Index];
                    SidebarCatalogue.TryKindOf(id, out var kind);
                    return new DragChipSpec(Title: SectionName(id), Glyph: PaneIcon.SectionGlyph(kind));
                case "sidebar-item:pins":
                    return p.Index < Sidebar.Pins.Count ? new DragChipSpec(Title: SidebarMenus.PinName(Sidebar.Pins[p.Index].Id), Glyph: Icons.GripperBar) : null;
                case "sidebar-item:collections":
                    return p.Index < pane._collections.Count ? new DragChipSpec(Title: ItemName(pane._collections[p.Index]), Glyph: Icons.GripperBar) : null;
                default:
                    return null;
            }
        }

        static string NameAt(IReadOnlyList<string> list, int index)
            => list.Count == 0 ? "" : list[Math.Clamp(index, 0, list.Count - 1)];

        /// <summary>The spoken sentence of one reorder milestone (§P4.1 keys: name, position, count).</summary>
        static string Announce(ReorderAnnounce a, string name)
            => Loc.Format(SidebarEditRules.AnnounceKey(a.Kind),
                ("name", name),
                ("pos", (a.Slot + 1).ToString(CultureInfo.InvariantCulture)),
                ("count", a.Count.ToString(CultureInfo.InvariantCulture)));
    }
}
