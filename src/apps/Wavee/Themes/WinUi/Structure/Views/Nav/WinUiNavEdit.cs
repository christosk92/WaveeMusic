// ── Themes/Classic/Structure/Views/Nav/WinUiNavEdit.cs — Classic's `Shell.NavEdit` (themes plan 6.1, T5d) ─────────────────────
//
// From `Shell/Sidebar.UI.Edit.cs` (`EditPane`): edit mode's pane, the edit bar and the Outline (design C.3) — the Show checkboxes,
// the section band (Classic), the pins band (both layouts, Q3), the Collections item band, the section chip, and the keys (Alt+Up/Down,
// Enter, Space lift, Esc, Ctrl+Z / Ctrl+Y). The core pane keeps what DECIDES (when it edits, the edit model and its ops, undo,
// persistence, validation, the Reset and header menus' rows: `INavEditHost`); this file draws the Outline and wires the engine's
// keys to the model's table. The component keeps its hook order, its Reorderables and its first-stop focus landing, so entering
// and leaving edit mode behave exactly as the nested class did.
//
// Spec: sidebar-rework-implementation.md §P4.5, §P5.7 · design C.3, P.2a, Q3, Q5, Q7, Q8, Q16, Q17, V.11

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

/// <summary>The registered template of <see cref="ViewKeys.ShellNavEdit"/>.</summary>
internal static class WinUiNavEdit
{
    public static Element Template(NavEditProps props, in FluentGpu.Theming.ViewContext ctx)
        => Embed.Comp(() => new WinUiEditPane(props.Host));
}

/// <summary>EDIT MODE'S PANE (design C.3): the edit bar over the Outline. Modal for the sidebar only — the page behind
/// stays fully interactive.</summary>
internal sealed class WinUiEditPane : Component
{
    readonly INavEditHost owner;

    public WinUiEditPane(INavEditHost owner) => this.owner = owner;

    /// <summary>The mounted pane: the section chip's titles read its band through this static resolver.</summary>
    static readonly RootState<WinUiEditPane?> s_activeState = new("WinUi.Sidebar.EditPane");
    static WinUiEditPane? s_active { get => s_activeState.Value; set => s_activeState.Value = value; }
    static readonly Func<DragState, DragChipSpec?> s_sectionChip = SectionChip;
    static readonly DragVisualStyle s_stationary = new() { Lift = DragLift.Stationary, Opacity = FluentGpu.Controls.Drag.SourceDimOpacity };

    readonly Signal<bool> _showAllPins = new(false);
    readonly List<SidebarOutlineRow> _rows = new(32);
    readonly List<string> _band = new(8);
    readonly List<string> _collections = new(8);
    readonly Dictionary<string, Signal<bool>> _checks = new(StringComparer.Ordinal);
    Reorderable? _sections, _pins, _items;
    NodeHandle _firstStop;

    NavEditModel Edit => owner.Edit;

    public override Element Render()
    {
        // The edit model reads the Outline (and subscribes this pane to the layout, the pin store and the undo ring).
        var layout = Edit.ReadOutline(_showAllPins.Value, _rows, _band);
        var state = Edit.State;

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
        if (Edit.OnPaneKey(EditKeyOf(e.KeyCode), EditModsOf(e.Mods))) e.Handled = true;
    }

    /// <summary>The engine key as the edit model's table knows it.</summary>
    static NavEditKey EditKeyOf(int key) => key switch
    {
        Keys.Enter => NavEditKey.Enter,
        Keys.Up => NavEditKey.Up,
        Keys.Down => NavEditKey.Down,
        Keys.Escape => NavEditKey.Escape,
        Keys.Z => NavEditKey.Z,
        Keys.Y => NavEditKey.Y,
        _ => NavEditKey.Other,
    };

    /// <summary>The held modifiers as the edit model's table knows them; any modifier it does not name is <c>Other</c>.</summary>
    static NavEditMods EditModsOf(KeyModifiers mods)
    {
        var result = NavEditMods.None;
        if ((mods & KeyModifiers.Alt) != 0) result |= NavEditMods.Alt;
        if ((mods & KeyModifiers.Shift) != 0) result |= NavEditMods.Shift;
        if ((mods & KeyModifiers.Ctrl) != 0) result |= NavEditMods.Ctrl;
        if ((mods & ~(KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Ctrl)) != 0) result |= NavEditMods.Other;
        return result;
    }

    // ── the edit bar ───────────────────────────────────────────────────────────────────────────────────────────────

    Element EditBar()
    {
        bool canUndo = Edit.CanUndo, canRedo = Edit.CanRedo;
        return new BoxEl
        {
            Key = "edit-bar", Direction = 0, Height = WinUiNavEditLayout.BarHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = WinUiNavEditLayout.Gap,
            Margin = WinUiNavEditLayout.BarMargin,
            Padding = WinUiNavEditLayout.BarPadding, Corners = Radii.ControlAll,
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
                ToolTip.Wrap(IconButton.Create(Icons.Undo, Edit.Undo, isEnabled: canUndo), Loc.Get("sidebar.undo.undoAction")),
                ToolTip.Wrap(IconButton.Create(Icons.Redo, Edit.Redo, isEnabled: canRedo), Loc.Get("sidebar.undo.redo")),
                Button.Create(Loc.Get("sidebar.edit.reset"), static () => { }, ButtonAppearance.Subtle, ControlSize.Small)
                    .WithContextMenu(owner.MenuOverlay, owner.ResetMenu) with { OnClick = null, ClickRequestsContext = true, Key = "edit-reset" },
                Button.Accent(Loc.Get("sidebar.edit.done"), Edit.Exit) with { Key = "edit-done" },
            ],
        };
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
        _sections.ExtentOf = i => SidebarEditRules.ExtentOf(layout, overlay.Find(_band[i])!, owner.Pins.Count, _showAllPins.Peek());
        _sections.OnReorder = (f, t) => Edit.MoveSection(_band[f], t);
        _sections.AnnounceText = a => Announce(a, owner.SectionName(NameAt(_band, a.Index)));
        _pins.ItemCount = 0;
        _pins.OnReorder = (f, t) => owner.MovePin(f, t);
        _pins.AnnounceText = a => Announce(a, owner.Pins.Count == 0 ? "" : owner.PinName(owner.Pins[Math.Clamp(a.Index, 0, owner.Pins.Count - 1)].Id));
        _items.ItemCount = 0;
        _items.OnReorder = (f, t) => Edit.MoveItem("collections", _collections[f], t);
        _items.AnnounceText = a => Announce(a, owner.ItemName(NameAt(_collections, a.Index)));

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
                kids.Add(LockedHead(Icons.Home, owner.SectionName("home")));
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
        kids.Add(new BoxEl { Height = 1f, Shrink = 0f, Margin = WinUiNavEditLayout.SeparatorMargin, Fill = Tok.StrokeCardDefault });
        foreach (var row in _rows)
        {
            if (row.Kind != SidebarOutlineRowKind.Settings) continue;
            kids.Add(SettingsRow(layout, row));
            break;
        }
        kids.Add(new BoxEl
        {
            Padding = WinUiNavEditLayout.FootPadding,
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
            Content = new BoxEl { Direction = 1, Padding = WinUiNavEditLayout.ContentPadding, Children = [.. kids] },
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
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Opacity = row.Shown ? 1f : WinUiNavEditLayout.HiddenOpacity,
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
                    Element item = _pins!.Item(at, PinRow(owner.Pins[at]), key: row.ItemId);
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
        string title = kind == SidebarSectionKind.Library ? Loc.Get("sidebar.edit.filters") : owner.SectionName(id);
        var kids = new List<Element>(8)
        {
            SectionCheck(layout, row, kind, title),
            new TextEl(title) { Size = WinUiNavEditLayout.TitleSize, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
        };
        if (SidebarEditRules.SummaryKeyOf(kind, row.Count) is { } key)
            kids.Add(Ui.Caption(Loc.Format(key, ("count", row.Count), ("total", SidebarCatalogue.ItemsOf(layout, kind).Count))) with
            {
                Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis,
            });
        kids.Add(new BoxEl { Grow = 1f });
        if (kind == SidebarSectionKind.Pinned && NavMenuModel.LockingNames().Count > 0)
            kids.Add(Button.Create(Loc.Get("sidebar.menu.unpinShortcuts"), Edit.UnpinAllShortcuts, ButtonAppearance.Subtle, ControlSize.Small)
                with { Key = "pinned-unpin-shortcuts", BlocksDragArm = true });
        if (owner.MenuOverlay is { } svc && owner.HeaderMenu(id) is { } menu)
            kids.Add(ToolTip.Wrap(WinUiNavHead.InlineButton(Icons.More, null, reveal: false, requestsContext: true).WithContextMenu(svc, menu),
                Loc.Get(Sidebar.PaneLoc.SectionOptions)));
        if (row.Movable) kids.Add(Icon(Icons.GripperBar, 16f, Tok.TextTertiary));
        return new BoxEl
        {
            Key = "head:" + id, Direction = 0, Height = SidebarEditRules.SectionHeaderHeight, Shrink = 0f,
            AlignItems = FlexAlign.Center, Gap = WinUiNavEditLayout.Gap, Padding = WinUiNavEditLayout.RowPadding,
            OnRealized = first ? SetFirstStop : null,
            Children = [.. kids],
        };
    }

    /// <summary>The header's leading box: the Show checkbox; the Filters icon for Library's kind; a lock for a section
    /// that cannot be hidden. A locked Pinned shows the box disabled and says why.</summary>
    Element SectionCheck(SidebarLayoutId layout, SidebarOutlineRow row, SidebarSectionKind kind, string title)
    {
        if (kind == SidebarSectionKind.Library)
            return GlyphBox(Icon(Icons.Filter, 14f, Tok.TextSecondary));
        if (!SidebarCatalogue.Hideable(kind))
            return GlyphBox(Icon(Icons.Lock, 12f, Tok.TextTertiary));
        string id = row.SectionId;
        string? reason = row.Locked ? Loc.Format("sidebar.menu.unpinFirst", ("names", string.Join(", ", NavMenuModel.LockingNames()))) : null;
        // Showing an empty section is allowed; Done says so once (design C.3): the edit model keeps the note.
        return Check("section:" + id, row.Shown, !row.Locked, title, v => Edit.SetSectionShown(layout, id, kind, title, v), reason);
    }

    /// <summary>The 24-DIP column a checkbox, a lock or the Filters glyph sits in.</summary>
    static Element GlyphBox(Element glyph) => new BoxEl
    {
        Width = WinUiNavEditLayout.GlyphBox, Height = WinUiNavEditLayout.GlyphBox, Shrink = 0f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [glyph],
    };

    /// <summary>A pin row (40): [glyph] [title] spacer [Unpin] [grip]. Its band wrapper takes the keys (<see cref="OnPinKey"/>).</summary>
    Element PinRow(SidebarPin pin)
    {
        string name = owner.PinName(pin.Id);
        return new BoxEl
        {
            Key = "pin:" + pin.Id, Direction = 0, Height = SidebarEditRules.RowHeight, Shrink = 0f,
            AlignItems = FlexAlign.Center, Gap = WinUiNavEditLayout.Gap, Padding = WinUiNavEditLayout.RowPadding,
            Children =
            [
                PinArt(pin),
                new TextEl(name) { Size = WinUiNavEditLayout.TitleSize, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
                new BoxEl { Grow = 1f },
                ToolTip.Wrap(WinUiNavHead.InlineButton(Icons.UnPin, () => owner.Unpin(pin.Id, name), reveal: false), Loc.Get("sidebar.pin.unpin")),
                Icon(Icons.GripperBar, 16f, Tok.TextTertiary),
            ],
        };
    }

    /// <summary>A pin's 20-px leading art: a route pin wears its destination's glyph; an entity pin its cover, once the
    /// projection has resolved it (the kind glyph until then).</summary>
    Element PinArt(SidebarPin pin)
    {
        Element art = pin.Kind == SidebarEntryKind.AppRoute
            ? Icon(owner.DestOf(pin.Id).Glyph, 14f, Tok.TextSecondary)
            : owner.ResolvedPin(pin.Id) is { } entry ? Sidebar.Cover.ForEntry(in entry, WinUiNavEditLayout.PinCover) : Icon(Icons.Library, 14f, Tok.TextSecondary);
        return new BoxEl
        {
            Width = WinUiNavEditLayout.PinArtBox, Height = WinUiNavEditLayout.PinArtBox, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [art],
        };
    }

    /// <summary>An item row (40) of an item section: [checkbox] [glyph] [title] spacer [grip when movable]. Hidden items
    /// render at 0.5 opacity. A Collections row's band wrapper takes the keys (<see cref="OnItemKey"/>).</summary>
    Element ItemRow(SidebarLayoutId layout, SidebarSectionKind kind, SidebarOutlineRow row)
    {
        string section = row.SectionId, item = row.ItemId;
        var kids = new List<Element>(6)
        {
            // A hidden Library kind's chip is gone: the edit model steps the active filter off it, as the menu's Filters rows do.
            Check("item:" + section + ":" + item, row.Shown, true, owner.ItemName(item), v => Edit.SetItemShown(layout, section, item, kind, v)),
            Icon(owner.DestOf(item).Glyph, 16f, Tok.TextSecondary),
            new TextEl(owner.ItemName(item)) { Size = WinUiNavEditLayout.TitleSize, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
            new BoxEl { Grow = 1f },
        };
        if (row.Movable) kids.Add(Icon(Icons.GripperBar, 16f, Tok.TextTertiary));
        return new BoxEl
        {
            Key = "item:" + section + ":" + item, Direction = 0, Height = SidebarEditRules.RowHeight, Shrink = 0f,
            AlignItems = FlexAlign.Center, Gap = WinUiNavEditLayout.Gap, Padding = WinUiNavEditLayout.RowPadding,
            Opacity = row.Shown ? 1f : WinUiNavEditLayout.HiddenOpacity,
            Children = [.. kids],
        };
    }

    /// <summary>The footer's Settings row in the Outline (Q7: the Outline replaces the footer): its Show checkbox, and
    /// "Still in the profile menu" while hidden.</summary>
    Element SettingsRow(SidebarLayoutId layout, SidebarOutlineRow row)
    {
        string title = owner.SettingsTitle;
        var kids = new List<Element>(6)
        {
            Check("settings", row.Shown, true, title, v => Edit.SetSettingsShown(layout, v)),
            Icon(Icons.Settings, 16f, Tok.TextSecondary),
            new TextEl(title) { Size = WinUiNavEditLayout.TitleSize, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis, Color = Tok.TextPrimary },
            new BoxEl { Grow = 1f },
        };
        if (!row.Shown)
            kids.Add(Ui.Caption(Loc.Get("sidebar.edit.stillInProfile")) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 0f });
        return new BoxEl
        {
            Key = "settings-row", Direction = 0, Height = SidebarEditRules.SectionHeaderHeight, Shrink = 0f,
            AlignItems = FlexAlign.Center, Gap = WinUiNavEditLayout.Gap, Padding = WinUiNavEditLayout.RowPadding,
            Opacity = row.Shown ? 1f : WinUiNavEditLayout.HiddenOpacity,
            Children = [.. kids],
        };
    }

    /// <summary>A locked header with no checkbox ("Home", "Your Library"): a lock, the icon and the title.</summary>
    static Element LockedHead(string glyph, string title) => new BoxEl
    {
        Direction = 0, Height = SidebarEditRules.SectionHeaderHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = WinUiNavEditLayout.Gap,
        Padding = WinUiNavEditLayout.RowPadding,
        Children =
        [
            GlyphBox(Icon(Icons.Lock, 12f, Tok.TextTertiary)),
            Icon(glyph, 16f, Tok.TextSecondary),
            new TextEl(title) { Size = WinUiNavEditLayout.TitleSize, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    /// <summary>A 44-tall hint row whose caption sits at x 40 (under the header text).</summary>
    static Element HintRow(string text) => new BoxEl
    {
        Direction = 0, Height = SidebarEditRules.HintHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = WinUiNavEditLayout.HintPadding,
        Children = [Ui.Caption(text) with { Color = Tok.TextTertiary, MaxLines = 1, Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>"Show all N pins" — the fold above <see cref="SidebarEditRules.PinFoldAt"/> (Q3).</summary>
    Element ShowAllRow(int count) => new BoxEl
    {
        Direction = 0, Height = SidebarEditRules.RowHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = WinUiNavEditLayout.ShowAllPadding,
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

    /// <summary>Keys the lifted Reorderable does not take, on a section wrapper (design Q8): the edit model's table decides
    /// (Enter toggles Show; Alt+↑/↓ moves one step, Alt+Shift+↑/↓ to the edge).</summary>
    void OnSectionKey(string id, int slot, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Edit.OnSectionKey(id, slot, _band.Count, EditKeyOf(e.KeyCode), EditModsOf(e.Mods))) e.Handled = true;
    }

    /// <summary>Alt+↑/↓ on a pin row's wrapper (Alt+Shift: to the edge). The same in both layouts (Q3). Enter stops here:
    /// a pin has no Show, and the key must not reach the section wrapper and toggle Pinned.</summary>
    void OnPinKey(int index, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Edit.OnPinKey(index, EditKeyOf(e.KeyCode), EditModsOf(e.Mods))) e.Handled = true;
    }

    /// <summary>Keys on a Collections item row's wrapper (design Q8): Enter toggles the item's Show (never the section's);
    /// Alt+↑/↓ moves one step, Alt+Shift+↑/↓ to the edge.</summary>
    void OnItemKey(int index, string itemId, bool shown, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (Edit.OnItemKey(index, _collections, itemId, shown, EditKeyOf(e.KeyCode), EditModsOf(e.Mods))) e.Handled = true;
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
                return new DragChipSpec(Title: pane.owner.SectionName(id), Glyph: Sidebar.PaneIcon.SectionGlyph(kind));
            case "sidebar-item:pins":
                return p.Index < pane.owner.Pins.Count ? new DragChipSpec(Title: pane.owner.PinName(pane.owner.Pins[p.Index].Id), Glyph: Icons.GripperBar) : null;
            case "sidebar-item:collections":
                return p.Index < pane._collections.Count ? new DragChipSpec(Title: pane.owner.ItemName(pane._collections[p.Index]), Glyph: Icons.GripperBar) : null;
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
