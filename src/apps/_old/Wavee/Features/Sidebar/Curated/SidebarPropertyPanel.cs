using System;
using System.Collections.Generic;
using System.Text.Json;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// THE PROPERTY PANEL (§C4.6 + REVISION 2: "Property controls for extension sections are GENERATED from the source's
// config schema").
//
// Two rules this file exists to keep:
//
//  1. WHICH ROWS A KIND SHOWS IS NOT WRITTEN HERE. The display block walks SidebarDisplayValues.Order and asks
//     SidebarSectionKinds.AllowsDisplayField(kind, field) — the ONE per-kind option table (also the reducer's own gate,
//     so a row the panel shows is a row SetDisplayOption accepts, always). There is no per-kind hand-written form.
//  2. A CONTRIBUTED SECTION'S ROWS ARE GENERATED. An Extension section resolves its contribution through the registry
//     (never a switch on an extension id) and renders one control per ISidebarDataSource.ConfigSchema field, writing back
//     through SetExtensionConfig with the untouched members copied through (SidebarConfigJson).
//
// Every control here is CONTROLLED against the document (see the pattern note in SidebarCustomizerControls.cs): the row
// re-renders on LayoutVersion and mirrors the document into its signal from a layout effect, so a rejected edit snaps
// back instead of leaving a control that lies about the saved state.
// PHASE 2 / DECISION B — SAME PANEL, TWO HOSTS. It took `SidebarCustomizerPage`; it now takes `ISidebarEditHost`, which
// that page implements explicitly and the shared `SidebarEditSession` implements too. That is the whole of "only its
// host changes from a docked column to a popover" (P3): the live pane's section card anchors this panel to the section
// the user clicked, while the companion page keeps mounting it in its inspector column until Phase 3 removes that. Not
// one line of the per-kind option table, the schema-generated extension rows or the controlled-input contract is
// duplicated for the second host.
sealed class SidebarPropertyPanel : Component
{
    readonly ISidebarEditHost _page;

    /// <summary>The body's scroll-restoration key. Two mounts of this panel — the docked inspector column and the
    /// pane's options popover — must not fight over one saved offset, the same reason the pane suffixes its own key
    /// with <c>".drawer"</c> for its second mount.</summary>
    readonly string _scrollKey;

    public SidebarPropertyPanel(ISidebarEditHost page, string scrollKey = "customizer.props")
    {
        _page = page; _scrollKey = scrollKey;
    }

    /// <summary>The CONTENT group's trailing caption ("3 items"), set while the items block is built so the count can ride
    /// on the group label instead of occupying a body row (round-2 defect 4). A plain field: it is written and read inside
    /// one <see cref="Render"/> pass, so it carries no reactivity of its own.</summary>
    string? _contentCaption;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;          // THE dep: the panel is a projection of the selected section
        _ = _page.RejectEpoch.Value;                  // ...and of the reducer's rejections (round-2 defect 1a)
        string? id = _page.Selected.Value;
        var spec = id is { Length: > 0 } && prefs is not null ? prefs.Layout.Find(id) : null;
        if (spec is null || id is null) return NoSelection();

        var general = new List<Element>(3);
        var content = new List<Element>(16);
        var appearance = new List<Element>(12);
        var behavior = new List<Element>(8);
        _contentCaption = null;
        AppendGeneral(general, spec, id);
        AppendDisplay(appearance, behavior, spec, id);
        AppendBehavior(behavior, spec, id);

        if (SidebarSectionKinds.SupportsLibraryQuery(spec.Kind))
            content.Add(Embed.Comp(() => new CzQueryBlock(_page, id)) with { Key = "query:" + id });

        if (spec.IsExtension) AppendExtension(content, spec, id);
        if (SidebarSectionKinds.AcceptsItems(spec.Kind)) AppendItems(content, spec, id);

        var groups = new List<Element>(5)
        {
            CzRow.Group(CzLoc.GroupGeneral, general),
        };
        // The item count rides on the GROUP LABEL ("CONTENT · 3 items", round-2 defect 4): it used to be a bare "0 items"
        // caption row above a lone hint line, which read as a broken list rather than an empty one.
        if (content.Count > 0) groups.Add(CzRow.Group(CzLoc.GroupContent, content, _contentCaption));
        if (appearance.Count > 0) groups.Add(CzRow.Group(CzLoc.GroupAppearance, appearance));
        if (behavior.Count > 0) groups.Add(CzRow.Group(CzLoc.GroupBehavior, behavior));
        groups.Add(new BoxEl
        {
            Direction = 0, Shrink = 0f, Justify = FlexJustify.Start,
            Padding = new Edges4(Spacing.S, Spacing.M, Spacing.S, Spacing.XXL),
            Children = [CzRow.Danger(Loc.Get(CzLoc.RemoveSection), () => Remove(id), Icons.Delete)],
        });

        var body = ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XXL),
            Children = [.. groups],
        }) with
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, AutoEdgeFade = true, ScrollKey = _scrollKey,
        };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, ClipToBounds = true,
            Children = [SubjectHeader(spec, id), Divider(), body],
        };
    }

    Element NoSelection() => new BoxEl
    {
        Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.L, Spacing.XL, Spacing.L, Spacing.XL), Gap = Spacing.S,
        Children =
        [
            Icon(Icons.Settings, 20f, Tok.TextTertiary),
            new TextEl(Loc.Get(CzLoc.NoSelection))
            {
                Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
            },
        ],
    };

    // â”€â”€ header block (§C4.6) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    Element SubjectHeader(SidebarSectionSpec spec, string id) => new BoxEl
    {
        Direction = 0, Height = 52f, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, 0f, Spacing.S, 0f),
        Children =
        [
            Icon(CzGlyphs.ForKind(spec.Kind), 16f, Tok.TextSecondary),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(CzGlyphs.TitleOf(spec))
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(Loc.Get(SidebarSectionKinds.PaletteNameLocKey(spec.Kind)
                                       ?? "sidebar.section.extension"))
                    {
                        Size = 10f, Color = Tok.TextTertiary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
            Embed.Comp(() => new CzMenuButton(Icons.More, () => SectionMenu(spec, id)))
                with { Key = "subject-menu:" + id },
        ],
    };

    void AppendGeneral(List<Element> into, SidebarSectionSpec spec, string id)
    {
        // A Divider has no title and no options worth renaming — it is pure chrome.
        if (spec.Kind != SidebarSectionKind.Divider)
            into.Add(Embed.Comp(() => new CzTitleRow(_page, id)) with { Key = "title:" + id });

        into.Add(Embed.Comp(() => new CzHiddenRow(_page, id)) with { Key = "hidden:" + id });
    }

    IReadOnlyList<MenuFlyoutItem> SectionMenu(SidebarSectionSpec spec, string id)
        =>
        [
            new(Loc.Get(CzLoc.Duplicate), default, true, () => _page.Dispatch(new DuplicateSection(id,
                Loc.Format(CzLoc.DuplicateSuffix, ("name", CzGlyphs.TitleOf(spec)))))),
            MenuFlyoutItem.Separator,
            new(Loc.Get(CzLoc.RemoveSection), Icons.Delete, true, () => Remove(id)),
        ];

    void Remove(string id)
    {
        if (_page.Dispatch(new RemoveSection(id)) == SidebarRejectReason.None) _page.Select(null);
    }

    // â”€â”€ display block — GENERATED from the per-kind option table â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    void AppendDisplay(List<Element> appearance, List<Element> behavior, SidebarSectionSpec spec, string id)
    {
        var order = SidebarDisplayValues.Order;
        for (int i = 0; i < order.Length; i++)
        {
            var field = order[i];
            if (!SidebarSectionKinds.AllowsDisplayField(spec.Kind, field)) continue;
            // Grid columns only mean something while the section IS a grid (the reducer keeps the value either way).
            if (field == SidebarDisplayField.GridColumns && spec.Opts.Presentation != SidebarPresentation.Grid) continue;
            // NOT SHOWN — round-2 defect 1. `CollapsedByDefault` is an ADD-TIME SEED and nothing else: the reducer copies
            // it into the new section's live `Collapsed` at AddSection (SidebarLayoutReducer.cs:112) and then explicitly
            // refuses to let a later edit touch the live state (:424). Every renderer reads `spec.Collapsed`; NOTHING
            // anywhere reads `Opts.CollapsedByDefault`. So on an existing section this control provably cannot change
            // anything the user can see — exactly the "some things don't change" report. `AppendBehavior` puts the LIVE
            // collapse toggle here instead. The field stays in `Order` (it is still a real, persisted document field and
            // the ownership test asserts the table is complete); only the dead CONTROL is gone.
            if (field == SidebarDisplayField.CollapsedByDefault) continue;

            Element row = field switch
            {
                SidebarDisplayField.MaxItems =>
                    Embed.Comp(() => new CzSliderRow(_page, id, field, 0, SidebarLayoutReducer.MaxItemsPerSection)),
                SidebarDisplayField.GridColumns =>
                    Embed.Comp(() => new CzNumberRow(_page, id, field, 2, 4)),
                _ when SidebarDisplayValues.IsFlag(field) => Embed.Comp(() => new CzToggleRow(_page, id, field)),
                _ => Embed.Comp(() => new CzSelectorRow(_page, id, field)),
            };
            (IsBehavior(field) ? behavior : appearance)
                .Add(row with { Key = "opt:" + (int)field + ":" + id });
        }
    }

    static bool IsBehavior(SidebarDisplayField field)
        => field is SidebarDisplayField.MaxItems
            or SidebarDisplayField.CollapsedByDefault
            or SidebarDisplayField.ShowInRail
            or SidebarDisplayField.RecentsSource
            or SidebarDisplayField.EmptyBehavior;

    /// <summary>Behaviour rows that are NOT display options: the LIVE collapse state (round-2 defect 1). This one edits
    /// <c>spec.Collapsed</c> through <c>SetSectionCollapsed</c>, which the pane actually reads — flipping it collapses the
    /// section in the live sidebar in the same frame, which is what the dead "Start collapsed" row only pretended to do.</summary>
    void AppendBehavior(List<Element> into, SidebarSectionSpec spec, string id)
    {
        // A Divider and a Header have no body to collapse.
        if (spec.Kind is SidebarSectionKind.Divider or SidebarSectionKind.Header) return;
        if (!SidebarSectionKinds.AllowsDisplayField(spec.Kind, SidebarDisplayField.CollapsedByDefault)) return;
        into.Add(Embed.Comp(() => new CzCollapsedRow(_page, id)) with { Key = "collapsed:" + id });
    }

    // â”€â”€ extension block — GENERATED from the contribution's config schema â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    void AppendExtension(List<Element> into, SidebarSectionSpec spec, string id)
    {
        var xref = spec.Extension;
        if (xref is not { IsWellFormed: true })
        {
            into.Add(SidebarItemPickerBody.Note(Loc.Get(CzLoc.RejectExtensionRefMissing)));
            return;
        }

        string sourceId = SidebarContributions.SourceId(xref.ExtensionId, xref.ContributionId);
        // The contribution the section renders, read-only: an id is the only NAME a contribution has in M2 (a manifest
        // display name arrives with the external SDK), so showing it is honest rather than decorative.
        into.Add(CzRow.Prop(Loc.Get("sidebar.section.extension"), sourceId, null));

        ISidebarDataSource? source = null;
        if (_page.Registry is { } registry && registry.TryGetSource(sourceId, out var resolved)) source = resolved;
        if (source is null)
        {
            into.Add(SidebarItemPickerBody.Note(Loc.Get(CzLoc.ExtensionManage)));
            return;
        }
        if (xref.SchemaVersion > source.ConfigSchema.Version)
        {
            // A document authored against a NEWER schema than this build understands: keep the section, say so, change
            // nothing (the layout's never-rewrite policy).
            into.Add(SidebarItemPickerBody.Note(Loc.Get(CzLoc.ExtensionManage)));
            return;
        }

        var fields = source.ConfigSchema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            into.Add(Embed.Comp(() => new CzConfigRow(_page, id, field)) with { Key = "cfg:" + id + ":" + field.Key });
        }
        // A schema with no fields is legal (Queue takes only maxItems, Now Playing takes nothing): the host-owned display
        // rows above are then the whole story, and inventing a caption for that would be noise.
    }

    // â”€â”€ items block â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    void AppendItems(List<Element> into, SidebarSectionSpec spec, string id)
    {
        var items = spec.ItemList;
        // The count moves to the group label (round-2 defect 4) instead of taking a body row of its own.
        _contentCaption = Loc.Format(CzLoc.ItemCount, ("count", items.Count));

        for (int i = 0; i < items.Count; i++)
        {
            string itemId = items[i].Id;
            into.Add(Embed.Comp(() => new CzItemRow(_page, id, itemId)) with { Key = "item:" + id + ":" + itemId });
        }

        // A Pinned section's items are display OVERRIDES for pins the user made elsewhere (§C1.6) — the pin set itself is
        // the shared store, so there is nothing to "add" here. With no pins yet the group is EMPTY, so it gets a proper
        // empty-state row (icon + the hint) rather than a naked sentence under a "0 items" caption.
        if (spec.Kind == SidebarSectionKind.Pinned)
        {
            if (items.Count == 0) into.Add(EmptyRow(Icons.Pin, Loc.Get("sidebar.pin.emptyHint")));
            return;
        }

        // No empty-state row for an item-accepting section: the "Add" buttons immediately below ARE the affordance, and the
        // group label already says "0 items". Only Pinned (which has no add path of its own) needs one.
        bool full = items.Count >= SidebarSectionKinds.ItemCapacity(spec.Kind);
        bool embed = spec.Kind == SidebarSectionKind.EntityEmbed;
        var buttons = new List<Element>(2)
        {
            Button.Create(Loc.Get(CzLoc.ItemAdd), () => SidebarPickers.OpenItem(_page, item =>
                _page.Dispatch(new AddItem(id, item, items.Count)), entitiesOnly: embed),
                ButtonAppearance.Standard, ControlSize.Small,
                // EntityEmbed spotlights exactly one entity: a second pick RETARGETS it, so the button stays live.
                isEnabled: embed || !full) with { Grow = 1f },
        };
        if (!embed)
            buttons.Add(Button.Create(Loc.Get(CzLoc.ItemAction), () => SidebarActionPicker.Open(_page, null, binding =>
                _page.Dispatch(new AddItem(id, new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Action,
                    binding.ActionKey, Action: binding), items.Count))),
                ButtonAppearance.Subtle, ControlSize.Small, isEnabled: !full) with { Grow = 1f });

        into.Add(new BoxEl
        {
            Direction = 0, Gap = Spacing.S, Shrink = 0f,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
            Children = [.. buttons],
        });
    }

    /// <summary>An EMPTY-STATE row inside a group card (round-2 defect 4): a dimmed kind glyph beside the hint, on the same
    /// two-column geometry as every other row so the card keeps its rhythm while it has nothing to list.</summary>
    static Element EmptyRow(string glyph, string hint) => new BoxEl
    {
        Direction = 0, Shrink = 0f, MinHeight = 44f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
        Children =
        [
            new BoxEl
            {
                Width = 28f, Height = 28f, Shrink = 0f, Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleSecondary,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                Children = [Icon(glyph, 14f, Tok.TextTertiary)],
            },
            new TextEl(hint)
            {
                Size = 12f, Color = Tok.TextTertiary, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                MaxLines = 3, Wrap = TextWrap.Wrap,
            },
        ],
    };
}

/// <summary>The section title row: Enter commits <c>RenameSection</c>, Escape reverts. Deliberately NOT per keystroke
/// (§C3.3) — one rename is one undo step.
/// <para>The old honest deviation ("blurring leaves the typed text uncommitted") is CLOSED: the engine now exposes an
/// opt-in <c>TextBoxOptions.CommitOnLostFocus</c>, which this row takes. It matters far more in the pane's options
/// POPOVER than it ever did in a docked column — a light-dismiss surface is blurred by the very click that closes it,
/// so without the blur commit a rename typed in the popover would be dropped every single time. Escape still reverts
/// (it does not commit on the following blur), and an Enter that already committed is not re-published.</para></summary>
sealed class CzTitleRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly Signal<string> _text = new("");

    public CzTitleRow(ISidebarEditHost page, string sectionId)
    {
        _page = page; _sectionId = sectionId;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        string title = spec?.Title ?? "";
        UseLayoutEffect(() => _text.SetIfChanged(title),
            DepKey.From(StringComparer.Ordinal.GetHashCode(title), CzRow.Epoch(_page)));

        return CzRow.Wide(Loc.Get(CzLoc.Rename), Loc.Get(CzLoc.RenameHint),
            TextBox.Create(_text, null, new TextBox.TextBoxOptions
            {
                Width = CzRow.ComboWidth, Height = 32f, MaxLength = SidebarLayoutReducer.MaxTitleLength,
                Placeholder = spec is null ? "" : CzGlyphs.TitleOf(spec),
                OnCommit = text => _page.Dispatch(new RenameSection(_sectionId, text)),
                OnCancel = () => _text.SetIfChanged(title),
                CommitOnLostFocus = true,
            }));
    }
}

/// <summary>The authored-visibility row (<c>SetSectionHidden</c>): a hidden section keeps its place in the outline and
/// contributes no rows, no rail tiles and no projection work.</summary>
sealed class CzHiddenRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly Signal<bool> _on = new(false);

    public CzHiddenRow(ISidebarEditHost page, string sectionId)
    {
        _page = page; _sectionId = sectionId;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        bool hidden = spec?.Hidden ?? false;
        UseLayoutEffect(() => _on.SetIfChanged(hidden), DepKey.From(hidden ? 1 : 0, CzRow.Epoch(_page)));

        return CzRow.Prop(Loc.Get(CzLoc.Hidden), Loc.Get("sidebar.option.hiddenSub"),
            ToggleSwitch.Create(_on, v => _page.Dispatch(new SetSectionHidden(_sectionId, v))));
    }
}

/// <summary>The LIVE collapse row (round-2 defect 1). It edits <c>spec.Collapsed</c> — the field the pane renderer actually
/// reads (<c>SidebarPaneSlot</c>: <c>bool open = !section.Collapsed;</c>) — so flipping it collapses the section in the
/// docked sidebar and in the preview in the same frame. It REPLACES the panel's old <c>CollapsedByDefault</c> row, which
/// the reducer deliberately keeps decoupled from the live state and which therefore changed nothing visible.</summary>
sealed class CzCollapsedRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly Signal<bool> _on = new(false);

    public CzCollapsedRow(ISidebarEditHost page, string sectionId)
    {
        _page = page; _sectionId = sectionId;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        bool collapsed = spec?.Collapsed ?? false;
        UseLayoutEffect(() => _on.SetIfChanged(collapsed), DepKey.From(collapsed ? 1 : 0, CzRow.Epoch(_page)));

        // NO sublabel: the catalog's `collapsedSub` reads "Start this section collapsed", which describes the add-time
        // DEFAULT this row replaced, not the live state it edits. "Collapse section" says all of it (see the HANDOFF for
        // the string that would be worth adding).
        return CzRow.Prop(Loc.Get(CzLoc.Collapse), null,
            ToggleSwitch.Create(_on, v => _page.Dispatch(new SetSectionCollapsed(_sectionId, v))));
    }
}

/// <summary>The <c>EntityList</c> query block (§C4.6): kind checkboxes, sort, direction, and the qualifier rail — the
/// last shown ONLY when the live projection says the data supports qualifiers (locked decision 10). Every edit rebuilds
/// the whole <c>SidebarEntityQuery</c> from the stored one, so the include/exclude uri sets are never lost by editing a
/// scalar; the reducer then REPAIRS illegal combinations rather than rejecting them.</summary>
sealed class CzQueryBlock : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly Signal<bool> _playlists = new(false), _albums = new(false), _artists = new(false), _shows = new(false);
    readonly Signal<int> _sort = new(0);
    readonly Signal<bool> _desc = new(false);
    readonly Signal<int> _qualifier = new(0);

    static readonly string[] SortKeys =
    [
        "sidebar.option.sortRecents", "sidebar.option.sortRecentlyAdded", "sidebar.option.sortAlphabetical",
        "sidebar.option.sortCreator", "sidebar.option.sortCustom",
    ];

    static readonly string[] QualifierKeys =
    [
        "sidebar.option.qualifierAny", "sidebar.option.qualifierByYou", "sidebar.option.qualifierBySpotify",
        "sidebar.option.qualifierMixed",
    ];

    public CzQueryBlock(ISidebarEditHost page, string sectionId)
    {
        _page = page; _sectionId = sectionId;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        var kind = spec?.Kind ?? SidebarSectionKind.EntityList;
        var q = SidebarSectionKinds.EffectiveQuery(kind, spec?.Query);
        var prefs = _page.Prefs;
        // The qualifier rail's gate is the SAME capability flag Mode B's chips read — one authority, one answer.
        _ = prefs?.Entries.Version.Value ?? 0;
        bool qualifiers = prefs?.Entries.QualifiersAvailable ?? false;
        var shape = SidebarQueryPanelShape.For(kind, qualifiers);

        bool playlists = (q.Kinds & SidebarEntityKinds.Playlists) != 0;
        bool albums = (q.Kinds & SidebarEntityKinds.Albums) != 0;
        bool artists = (q.Kinds & SidebarEntityKinds.Artists) != 0;
        bool shows = (q.Kinds & SidebarEntityKinds.Shows) != 0;
        int sort = (int)q.Sort;
        bool desc = q.Descending;
        int qualifier = (int)q.Qualifier;

        UseLayoutEffect(() =>
        {
            _playlists.SetIfChanged(playlists);
            _albums.SetIfChanged(albums);
            _artists.SetIfChanged(artists);
            _shows.SetIfChanged(shows);
            _sort.SetIfChanged(sort);
            _desc.SetIfChanged(desc);
            _qualifier.SetIfChanged(qualifier);
        }, DepKey.Combine(DepKey.From((int)q.Kinds, sort),
                          DepKey.From(desc ? 1 : 0, qualifier, CzRow.Epoch(_page), 0)));

        var sortLabels = new string[SortKeys.Length];
        for (int i = 0; i < SortKeys.Length; i++) sortLabels[i] = Loc.Get(SortKeys[i]);

        // CustomOrder is only honoured for a playlists-only query (locked decision 10); the reducer would rewrite it to
        // Alphabetical, so the row DISABLES it rather than silently changing the user's pick.
        //
        // ROUND-2 DEFECT 1a: `ComboBox.Create` funnels through `Embed.Comp(props, factory)` whose FACTORY runs once —
        // `Items`, `ItemEnabled` and `Width` FREEZE AT MOUNT (only the `SelectedIndex` signal stays live). This block is
        // keyed on the section id, so ticking/unticking "Albums" left the frozen `itemEnabled` array behind and Custom
        // order stayed enabled (or stayed disabled) forever. The mount KEY now carries the gate, so the combo remounts
        // the instant the gate flips.
        bool customOrderOk = kind == SidebarSectionKind.PlaylistTree || q.Kinds == SidebarEntityKinds.Playlists;
        Element sortCombo = ComboBox.Create(sortLabels, _sort, width: CzRow.ComboWidth, onChange: Sort,
            itemEnabled: [true, true, true, true, customOrderOk]);
        sortCombo = sortCombo with { Key = "sort:" + (customOrderOk ? "custom" : "nocustom") };

        var kids = new List<Element>(6);
        if (shape.ShowKinds)
            kids.Add(new BoxEl
            {
                Direction = 1, Gap = 2f, Shrink = 0f,
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                Children =
                [
                    CheckBox.Create(Loc.Get("sidebar.v3.filter.playlists"), _playlists,
                        v => Kind(SidebarEntityKinds.Playlists, v)),
                    CheckBox.Create(Loc.Get("sidebar.v3.filter.albums"), _albums,
                        v => Kind(SidebarEntityKinds.Albums, v)),
                    CheckBox.Create(Loc.Get("sidebar.v3.filter.artists"), _artists,
                        v => Kind(SidebarEntityKinds.Artists, v)),
                    CheckBox.Create(Loc.Get("sidebar.v3.filter.podcasts"), _shows,
                        v => Kind(SidebarEntityKinds.Shows, v)),
                ],
            });
        kids.Add(CzRow.Wide(Loc.Get("sidebar.option.sort"), null, sortCombo));
        kids.Add(CzRow.Prop(Loc.Get("sidebar.option.descending"), null,
            ToggleSwitch.Create(_desc, Desc, isEnabled: q.Sort != SidebarSortMode.CustomOrder)));

        if (shape.ShowQualifier)
        {
            var qualifierLabels = new string[QualifierKeys.Length];
            for (int i = 0; i < QualifierKeys.Length; i++) qualifierLabels[i] = Loc.Get(QualifierKeys[i]);
            // CzRow.Choice, never SelectorBar (round-2 defect 2): "By Spotify" busts the Segmented label budget, so this
            // set resolves to the dropdown instead of a four-tab strip clipping mid-word in a 320-DIP column.
            kids.Add(CzRow.Wide(Loc.Get("sidebar.option.qualifier"), null,
                CzRow.Choice(qualifierLabels, _qualifier, Qualifier)));
        }

        return new BoxEl { Direction = 1, Gap = 0f, Shrink = 0f, Children = [.. kids] };
    }

    SidebarEntityQuery Current()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        return SidebarSectionKinds.EffectiveQuery(spec?.Kind ?? SidebarSectionKind.EntityList, spec?.Query);
    }

    void Kind(SidebarEntityKinds bit, bool on)
    {
        var q = Current();
        var kinds = on ? q.Kinds | bit : q.Kinds & ~bit;
        Push(q with { Kinds = kinds });
    }

    void Sort(int index) => Push(Current() with { Sort = (SidebarSortMode)Math.Clamp(index, 0, 4) });

    void Desc(bool on) => Push(Current() with { Descending = on });

    void Qualifier(int index) => Push(Current() with { Qualifier = (SidebarPlaylistQualifier)Math.Clamp(index, 0, 3) });

    void Push(SidebarEntityQuery next) => _page.Dispatch(new SetQuery(_sectionId, next));
}

/// <summary>ONE generated control for ONE <c>SidebarConfigField</c> (REVISION 2's schema-generated property controls).
/// The field record freezes at mount, which is correct: the row is keyed by section id + field key, so a different
/// section or schema remounts it.</summary>
sealed class CzConfigRow : Component
{
    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly SidebarConfigField _field;

    readonly Signal<string> _text = new("");
    readonly Signal<double> _number = new(0);
    readonly Signal<bool> _flag = new(false);
    readonly Signal<int> _choice = new(0);

    public CzConfigRow(ISidebarEditHost page, string sectionId, SidebarConfigField field)
    {
        _page = page; _sectionId = sectionId; _field = field;
    }

    public override Element Render()
    {
        var spec = CzRow.Subject(_page, _sectionId);
        var config = new SidebarSourceConfig(spec?.Extension?.Config ?? SidebarJson.EmptyObject);
        string label = Loc.Get(_field.LabelLocKey);

        string text = config.Str(_field.Key) ?? "";
        int number = config.Int(_field.Key, DefaultInt());
        bool flag = config.Bool(_field.Key, DefaultBool());
        int choice = ChoiceIndex(config.Str(_field.Key));

        // ONE mirror effect for every kind — hooks may not be conditional, so all four signals sync unconditionally and
        // only the one the field renders is read.
        UseLayoutEffect(() =>
        {
            _text.SetIfChanged(text);
            _number.SetIfChanged(number);
            _flag.SetIfChanged(flag);
            _choice.SetIfChanged(choice);
        }, DepKey.Combine(DepKey.From(StringComparer.Ordinal.GetHashCode(text)),
                          DepKey.From(number, flag ? 1 : 0, choice, CzRow.Epoch(_page))));

        switch (_field.Kind)
        {
            case SidebarConfigFieldKind.Bool:
                return CzRow.Prop(label, null, ToggleSwitch.Create(_flag,
                    v => Write(SidebarConfigJson.WithBool(Config(), _field.Key, v))));

            case SidebarConfigFieldKind.Int:
            {
                int min = _field.Min, max = _field.Max > _field.Min ? _field.Max : 500;
                return CzRow.Wide(label, null, NumberBox.CreateWithSpinners(_number,
                    v => Write(SidebarConfigJson.WithInt(Config(), _field.Key,
                        Math.Clamp((int)Math.Round(v), min, max))),
                    new NumberBox.NumberBoxOptions
                    {
                        Minimum = min, Maximum = max, SmallChange = 1, Width = CzRow.ComboWidth,
                    }));
            }

            case SidebarConfigFieldKind.Enum:
            {
                var values = _field.EnumValues;
                if (values is null || values.Count == 0) goto case SidebarConfigFieldKind.String;
                var labels = new string[values.Count];
                for (int i = 0; i < values.Count; i++) labels[i] = EnumLabel(values[i]);
                // CzRow.Choice, never SelectorBar (round-2 defect 2). An EXTENSION's vocabulary is unbounded — a schema can
                // declare six long enum values — so letting the label lengths pick the treatment is the only safe rule.
                return CzRow.Wide(label, null, CzRow.Choice(labels, _choice, i =>
                {
                    if ((uint)i >= (uint)values.Count) return;
                    Write(SidebarConfigJson.WithString(Config(), _field.Key, values[i]));
                }));
            }

            case SidebarConfigFieldKind.EntityUri:
            {
                string current = text;
                string shown = current.Length == 0
                    ? Loc.Get("sidebar.source.artistTopTracks.unset")
                    : NameOf(current) ?? current;
                return CzRow.Prop(label, shown,
                    Button.Create(Loc.Get(CzLoc.ItemAdd), () => SidebarPickers.OpenItem(_page,
                        item => Write(SidebarConfigJson.WithString(Config(), _field.Key, item.Key)),
                        entitiesOnly: true,
                        // A field whose key names an artist picks an ARTIST — the only kind hint a schema gives today.
                        kindFilter: _field.Key.IndexOf("artist", StringComparison.OrdinalIgnoreCase) >= 0
                            ? SidebarEntryKind.Artist : null),
                        ButtonAppearance.Standard, ControlSize.Small));
            }

            case SidebarConfigFieldKind.UriList:
            {
                var uris = new List<string>(4);
                int count = config.Strings(_field.Key, uris);
                return CzRow.Prop(label, count == 0 ? null : Loc.Format(CzLoc.ItemCount, ("count", count)),
                    Embed.Comp(() => new CzMenuButton(Icons.More, () => UriListMenu())));
            }

            case SidebarConfigFieldKind.String:
            default:
                return CzRow.Wide(label, null, TextBox.Create(_text, null, new TextBox.TextBoxOptions
                {
                    Width = CzRow.ComboWidth, Height = 32f,
                    OnCommit = value => Write(SidebarConfigJson.WithString(Config(), _field.Key, value)),
                }));
        }
    }

    IReadOnlyList<MenuFlyoutItem> UriListMenu()
    {
        var uris = new List<string>(8);
        new SidebarSourceConfig(CzRow.Subject(_page, _sectionId)?.Extension?.Config ?? SidebarJson.EmptyObject)
            .Strings(_field.Key, uris);

        var rows = new List<MenuFlyoutItem>(uris.Count + 2)
        {
            new(Loc.Get(CzLoc.ItemAdd), default, true, () => SidebarPickers.OpenItem(_page, item =>
            {
                var next = new List<string>(uris.Count + 1);
                next.AddRange(uris);
                if (!next.Contains(item.Key)) next.Add(item.Key);
                Write(SidebarConfigJson.WithStrings(Config(), _field.Key, next));
            }, entitiesOnly: true)),
        };
        if (uris.Count > 0)
        {
            rows.Add(MenuFlyoutItem.Separator);
            for (int i = 0; i < uris.Count && i < 20; i++)
            {
                string uri = uris[i];
                rows.Add(new MenuFlyoutItem(NameOf(uri) ?? uri, default, true, () =>
                {
                    var next = new List<string>(uris.Count);
                    for (int j = 0; j < uris.Count; j++)
                        if (!string.Equals(uris[j], uri, StringComparison.Ordinal)) next.Add(uris[j]);
                    Write(SidebarConfigJson.WithStrings(Config(), _field.Key, next));
                }));
            }
        }
        return rows;
    }

    JsonElement Config()
        => CzRow.Subject(_page, _sectionId)?.Extension?.Config ?? SidebarJson.EmptyObject;

    void Write(JsonElement config) => _page.Dispatch(new SetExtensionConfig(_sectionId, config));

    int DefaultInt()
    {
        if (_field.DefaultJson is not { Length: > 0 } raw) return _field.Min;
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : _field.Min;
    }

    bool DefaultBool() => string.Equals(_field.DefaultJson, "true", StringComparison.OrdinalIgnoreCase);

    int ChoiceIndex(string? value)
    {
        var values = _field.EnumValues;
        if (values is null || values.Count == 0) return 0;
        string? want = value ?? Trim(_field.DefaultJson);
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], want, StringComparison.Ordinal)) return i;
        return 0;

        static string? Trim(string? json)
            => json is { Length: > 1 } && json[0] == '"' && json[^1] == '"' ? json[1..^1] : json;
    }

    /// <summary>An entity uri's display name, from the live projection ("" when the library has not resolved it — the row
    /// then shows the uri, never a fabricated title).</summary>
    string? NameOf(string uri)
    {
        var entries = _page.Prefs?.Entries.Current;
        if (entries is null) return null;
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Uri, uri, StringComparison.Ordinal)) return entries[i].Name;
        return null;
    }

    /// <summary>A schema enum VALUE → a label. The catalog already localizes every first-party vocabulary word, so the
    /// known values reuse those keys and anything else falls back to the raw value (an extension's own vocabulary, which
    /// this build cannot localize).</summary>
    static string EnumLabel(string value) => value switch
    {
        "all" => Loc.Get("sidebar.option.maxItemsAll"),
        "playlists" => Loc.Get("sidebar.v3.filter.playlists"),
        "albums" => Loc.Get("sidebar.v3.filter.albums"),
        "artists" => Loc.Get("sidebar.v3.filter.artists"),
        "shows" => Loc.Get("sidebar.v3.filter.podcasts"),
        "recents" => Loc.Get("sidebar.option.sortRecents"),
        "added" => Loc.Get("sidebar.option.sortRecentlyAdded"),
        "alphabetical" => Loc.Get("sidebar.option.sortAlphabetical"),
        "creator" => Loc.Get("sidebar.option.sortCreator"),
        "any" => Loc.Get("sidebar.option.qualifierAny"),
        "byYou" => Loc.Get("sidebar.option.qualifierByYou"),
        "bySpotify" => Loc.Get("sidebar.option.qualifierBySpotify"),
        "mixed" => Loc.Get("sidebar.option.qualifierMixed"),
        _ => value,
    };
}

/// <summary>One authored item: its label override, its icon override, its action binding (when it is an action row), and
/// Remove. An UNRESOLVABLE row keeps its retained fallback title, dims, and offers Remove only (§C4.6) — an item is never
/// auto-removed.</summary>
sealed class CzItemRow : Component
{
    internal const float TopBarItemWidth = 320f;
    internal const float TopBarItemHeight = 52f;

    readonly ISidebarEditHost _page;
    readonly string _sectionId;
    readonly string _itemId;
    readonly bool _compact;
    readonly Signal<string> _label = new("");

    public CzItemRow(ISidebarEditHost page, string sectionId, string itemId, bool compact = false)
    {
        _page = page; _sectionId = sectionId; _itemId = itemId; _compact = compact;
    }

    public override Element Render()
    {
        _ = CzRow.Subject(_page, _sectionId);
        var item = Find();
        if (item is null) return new BoxEl { Height = 0f };

        string label = item.LabelOverride ?? "";
        UseLayoutEffect(() => _label.SetIfChanged(label),
            DepKey.From(StringComparer.Ordinal.GetHashCode(label), CzRow.Epoch(_page)));

        string title = TitleOf(item);
        string? reason = ActionReason(item);
        if (_compact) return Compact(item, title, label, reason);

        // A NORMAL row in the group card (round-2 defect 4): no plate of its own — the group card is the one surface, and a
        // card-per-item inside a card was the "inconsistent components" the reporter saw. Same 12/10 padding as every
        // other row so the items list shares the panel's rhythm.
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = 6f,
            Padding = new Edges4(Spacing.M, 10f, Spacing.M, 10f),
            Opacity = reason is null ? 1f : 0.7f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                            Fill = Tok.FillSubtleSecondary, HitTestVisible = false,
                            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Children = [Icon(GlyphOf(item), 13f, Tok.TextSecondary)],
                        },
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f, Gap = 1f,
                            Children = TitleLines(title, reason),
                        },
                        Embed.Comp(() => new CzMenuButton(Icons.More, ItemMenu)),
                        ToolTip.Wrap(IconButton.Create(Icons.Delete,
                            Remove, size: ControlSize.Small),
                            Loc.Get(CzLoc.ItemRemove)),
                    ],
                },
                TextBox.Create(_label, null, new TextBox.TextBoxOptions
                {
                    Width = CzRow.ComboWidth, Height = 30f, MaxLength = SidebarLayoutReducer.MaxTitleLength,
                    Placeholder = Loc.Get(CzLoc.ItemLabelPlaceholder),
                    OnCommit = text => Send(new SetItemLabel(_sectionId, _itemId, text)),
                    OnCancel = () => _label.SetIfChanged(label),
                }),
            ],
        };
    }

    Element Compact(SidebarItemSpec item, string title, string label, string? reason) => new BoxEl
    {
        Direction = 0, Width = TopBarItemWidth, Height = TopBarItemHeight, Shrink = 0f, MinWidth = 0f,
        Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = Edges4.All(Spacing.S), Corners = Radii.ControlAll,
        Fill = Tok.FillSubtleSecondary, HoverFill = Tok.FillSubtleTertiary,
        Opacity = reason is null ? 1f : 0.7f,
        Children =
        [
            Icon(Icons.GripperBar, 12f, Tok.TextTertiary),
            new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                Fill = Tok.FillSubtleSecondary, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(GlyphOf(item), 13f, Tok.TextSecondary)],
            },
            TextBox.Create(_label, null, new TextBox.TextBoxOptions
            {
                Width = 148f, Height = 32f, MaxLength = SidebarLayoutReducer.MaxTitleLength,
                Placeholder = title,
                OnCommit = text => Send(new SetItemLabel(_sectionId, _itemId, text)),
                OnCancel = () => _label.SetIfChanged(label),
            }),
            new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f },
            Embed.Comp(() => new CzMenuButton(Icons.More, ItemMenu)),
            ToolTip.Wrap(IconButton.Create(Icons.Delete, Remove, size: ControlSize.Small),
                Loc.Get(CzLoc.ItemRemove)),
        ],
    };

    /// <summary>The item row's text column: its title, plus the "why this row is inert" line when the registry has one.</summary>
    static Element[] TitleLines(string title, string? reason)
    {
        var head = new TextEl(title)
        {
            Size = 13f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        if (reason is null) return [head];
        return
        [
            head,
            new TextEl(reason)
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                Trim = TextTrim.CharacterEllipsis,
            },
        ];
    }

    /// <summary>The item this row edits. Which LIST a section id addresses (a section's items, or the global shortcut
    /// band behind the sentinel) is one decision with one owner — <see cref="SidebarItemCommands"/> — shared with the
    /// write side below, so the row can never read one list and write the other.</summary>
    SidebarItemSpec? Find() => SidebarItemCommands.FindItem(_page.Prefs?.Layout, _sectionId, _itemId);

    IReadOnlyList<MenuFlyoutItem> ItemMenu()
    {
        var item = Find();
        if (item is null) return Array.Empty<MenuFlyoutItem>();

        var rows = new List<MenuFlyoutItem>(SidebarIcons.Allowed.Length + 4);
        if (item.Target == SidebarItemTarget.Action)
        {
            var existing = item.Action;
            rows.Add(new MenuFlyoutItem(Loc.Get(CzLoc.ItemAction), default, true,
                () => SidebarActionPicker.Open(_page, existing,
                    binding => Send(new SetItemAction(_sectionId, _itemId, binding)))));
            rows.Add(MenuFlyoutItem.Separator);
        }

        rows.Add(new MenuFlyoutItem(Loc.Get(CzLoc.ItemIcon), default, false, null));
        var allowed = SidebarIcons.Allowed;
        for (int i = 0; i < allowed.Length; i++)
        {
            string name = allowed[i];
            bool on = string.Equals(item.IconOverride, name, StringComparison.Ordinal);
            rows.Add(MenuFlyoutItem.RadioItem(name, on,
                () => Send(new SetItemIcon(_sectionId, _itemId, on ? null : name)),
                SidebarIcons.Glyph(name, Icons.MusicNote)));
        }
        return rows;
    }

    string TitleOf(SidebarItemSpec item)
    {
        if (item.LabelOverride is { Length: > 0 } l) return l;
        switch (item.Target)
        {
            case SidebarItemTarget.Route:
                return ShellNav.Dest(item.Key, null).Title;
            case SidebarItemTarget.Action:
                if (item.Action is { } binding && _page.Registry is { } reg
                    && reg.TryGetAction(binding, out var descriptor)) return descriptor.Label();
                return item.Action?.ActionKey ?? item.Key;
            default:
                if (item.FallbackTitle is { Length: > 0 } f) return f;
                var entries = _page.Prefs?.Entries.Current;
                if (entries is not null)
                    for (int i = 0; i < entries.Count; i++)
                        if (string.Equals(entries[i].Uri, item.Key, StringComparison.Ordinal)) return entries[i].Name;
                return Loc.Get(CzLoc.MissingEntity);
        }
    }

    string GlyphOf(SidebarItemSpec item)
    {
        if (item.IconOverride is { Length: > 0 } name) return SidebarIcons.Glyph(name, Icons.MusicNote);
        return item.Target switch
        {
            SidebarItemTarget.Route => ShellNav.Dest(item.Key, null).Glyph,
            SidebarItemTarget.Action => Icons.RefineSparkle,
            _ => SidebarIcons.ForEntityKind(item.EntityKind),
        };
    }

    /// <summary>Why this row would be inert right now: an action whose binding cannot resolve (the registry's own
    /// reason), or an entity the projection cannot find. Null when the row is fine.</summary>
    string? ActionReason(SidebarItemSpec item)
    {
        if (item.Target != SidebarItemTarget.Action) return null;
        if (item.Action is not { } binding) return Loc.Get(CzLoc.RejectExtensionRefMissing);
        if (_page.Registry is not { } reg || _page.Acts is not { } acts) return null;
        var resolution = reg.Resolve(acts, binding);
        return resolution.Available ? null : Loc.Get(resolution.ReasonLocKey ?? CzLoc.MissingEntity);
    }

    /// <summary>Dispatch. The <c>IsTopBar</c> test here picks the REJECTION VOCABULARY (the band's own cap/duplicate
    /// messages instead of the generic ones), NOT the command — command routing is
    /// <see cref="SidebarItemCommands"/>'s single decision and is never re-made at a call site.</summary>
    SidebarRejectReason Send(SidebarCommand command)
        => SidebarIds.IsTopBar(_sectionId) ? _page.DispatchTopBar(command) : _page.Dispatch(command);

    void Remove() => Send(SidebarItemCommands.Remove(_sectionId, _itemId));
}
