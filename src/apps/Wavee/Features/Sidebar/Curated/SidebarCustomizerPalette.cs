using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// PHASE 3 — THE PERSISTENT PALETTE. Always visible, no popover and no accordion: it is a block in the companion page's
// one scrolling column, and the only mode it has is the contribution picker (which is a LIST swap, not a second
// surface).
//
// The TABLE and the FILTER are pure and unit-tested (`SidebarPalette` in SidebarCustomizerLayout.cs); this file is only
// the surface: it resolves the loc keys (and, for a destination, the route's own name through `ShellNav.Dest`), maps the
// entry's glyph NAME to a real glyph, and turns a click — or a drop on the canvas — into the ONE dispatch the page owns.
//
// WHAT THE OLD PALETTE GOT WRONG, and where each fix lives:
//   • Typing "home" returned one row: "Links — Shortcuts to pages like Home or Search", and adding it made an EMPTY
//     section. → the DESTINATIONS group (pure, in the table) renders first, and the bare Links row now opens the
//     destination picker (defect 7, `SidebarCustomizerPage.AddLinksSection`).
//   • The query was never cleared, so the palette reopened still filtered (defect 4). → the policy is stated once, on
//     `SidebarCustomizerPage.PaletteQuery`, and applied by `AfterAdd` + the page's enter/leave effect.
//   • Contribution-pick mode dropped the search box and printed the raw source id twice (defect 5). → the search box is
//     unconditional, the query filters BOTH lists, and a contribution the palette already names shows that name.
//   • The empty-search line borrowed V3's loc key as a hard-coded string (defect 6). → `CzLoc.PaletteEmpty`.
sealed class SidebarCustomizerPalette : Component
{
    readonly SidebarCustomizerPage _page;

    /// <summary>True ⇒ the palette is showing the "pick a contribution" list instead of the groups. A MODE rather than a
    /// nested flyout: a popup-on-popup for a one-shot pick reads as a bug, and now that the palette is inline there is
    /// no popup to nest inside anyway.</summary>
    readonly Signal<bool> _pickContribution = new(false);
    bool _forward = true;

    /// <summary>Reused across renders — the filter appends into it (no per-keystroke list allocation).</summary>
    readonly List<SidebarPaletteEntry> _matches = new();

    public SidebarCustomizerPalette(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        string query = _page.PaletteQuery.Value;
        bool picking = _pickContribution.Value;
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;

        // The APPEND subject. Read here (not inside a click handler) so the palette re-renders — and its Destinations
        // header re-labels itself — the moment the canvas's expanded card changes.
        var appendTo = _page.SelectedStaticLinks();

        int used = prefs?.Layout.SectionCount ?? 0;
        bool full = used >= SidebarLayoutReducer.MaxSections;

        var rows = new List<Element>(24);
        if (picking) AppendContributions(rows, query);
        else AppendGroups(rows, query, appendTo);

        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Spacing.XS,
            Children =
            [
                Head(used, full, picking),
                SearchBox(),
                new BoxEl
                {
                    // ONE card, like every other block in the column (CzRow.Group's card), but built by hand because the
                    // palette needs GROUP headers inside it and CzRow.Group takes a flat row list.
                    Key = picking ? "palette-card:contributions" : "palette-card:sections",
                    Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                    Direction = 1, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = Tok.FillCardSecondary,
                    BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Padding = Edges4.All(Spacing.XS),
                    ClipToBounds = true,
                    Children = [.. rows],
                },
            ],
        };
    }

    /// <summary>The block head: the title (or the pick-mode back row) plus the section budget.</summary>
    Element Head(int used, bool full, bool picking) => new BoxEl
    {
        Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Margin = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f),
        Children =
        [
            picking
                ? BackRow()
                : CzRow.GroupLabel(Loc.Get(CzLoc.AddSection)) with { Grow = 1f, Shrink = 1f, MinWidth = 0f },
            new TextEl(Loc.Format(CzLoc.SectionCount, ("used", used), ("max", SidebarLayoutReducer.MaxSections)))
            {
                Size = 11f, Weight = 600, Shrink = 0f, MaxLines = 1,
                Color = full ? Tok.SystemFillCritical : Tok.TextTertiary,
            },
        ],
    };

    // ── rows ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    void AppendGroups(List<Element> into, string query, SidebarSectionSpec? appendTo)
    {
        SidebarPalette.Filter(query, LabelOf, DescriptionOf, _matches);

        var groups = SidebarPalette.Groups;
        for (int g = 0; g < groups.Length; g++)
        {
            var group = groups[g];
            int before = into.Count;
            for (int i = 0; i < _matches.Count; i++)
            {
                var e = _matches[i];
                if (e.Group != group) continue;
                if (into.Count == before) into.Add(GroupHeader(group, appendTo));
                into.Add(EntryRow(e, appendTo));
            }
        }

        // Only a SEARCH can empty the palette (the table is static), so the empty state names the query.
        // DEFECT 6 — its OWN key. It used to hard-code "sidebar.v3.empty.search", which is the LIBRARY list's line: two
        // surfaces on one key means neither can be reworded, and the sentence was about the wrong thing anyway.
        if (into.Count == 0 && query.Length > 0) into.Add(EmptyLine(query));
    }

    /// <summary>An entry's searchable/label text. A DESTINATION has no name key — its label is the route's own name from
    /// <c>ShellNav.Dest</c>, the single owner of "what this page is called", so the palette follows the UI culture and
    /// can never disagree with the tab strip or the breadcrumb.</summary>
    static string LabelOf(SidebarPaletteEntry entry)
        => entry.RouteKey is { Length: > 0 } route ? ShellNav.Dest(route).Title : Loc.Get(entry.NameLocKey);

    static string? DescriptionOf(SidebarPaletteEntry entry)
        => entry.DescriptionLocKey is { Length: > 0 } key ? Loc.Get(key) : null;

    static string GlyphOf(SidebarPaletteEntry entry)
        => entry.RouteKey is { Length: > 0 } route ? ShellNav.Dest(route).Glyph : CzGlyphs.ForName(entry.IconName);

    Element GroupHeader(SidebarPaletteGroup group, SidebarSectionSpec? appendTo)
    {
        string label = Loc.Get(SidebarPalette.GroupLocKey(group));
        // The Destinations header says out loud where the next click will land. Without it the append rule is invisible
        // state — the failure mode P2 names — and the user would see a click behave two different ways for no stated
        // reason.
        if (group != SidebarPaletteGroup.Destinations || appendTo is null) return Header(label);
        return new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 2f),
            Children =
            [
                CzRow.GroupLabel(label) with { Shrink = 0f },
                new TextEl(Loc.Format(CzLoc.AppendsTo, ("name", CzGlyphs.TitleOf(appendTo))))
                {
                    Size = 11f, Color = WaveeAccent.Decor, Grow = 1f, Shrink = 1f, MinWidth = 0f, MaxLines = 1,
                    Trim = TextTrim.CharacterEllipsis,
                },
            ],
        };
    }

    Element EntryRow(SidebarPaletteEntry entry, SidebarSectionSpec? appendTo)
    {
        string label = LabelOf(entry);
        string? sub = DescriptionOf(entry);
        var page = _page;
        var add = entry.Add;
        var kind = entry.Kind;
        string? contribution = entry.ContributionId;
        string? route = entry.RouteKey;
        var picking = _pickContribution;

        void Click()
        {
            switch (add)
            {
                // Declaration patterns (`… is { Length: > 0 } r`), not bare tests: the case BODY then has a
                // provably non-null local to pass on, which keeps `<Nullable>enable</Nullable>` + warnings-as-errors
                // from turning a correct guard into a build break.
                case SidebarPaletteAdd.Destination when route is { Length: > 0 } r:
                    page.AddDestination(r);
                    break;
                case SidebarPaletteAdd.LinksWithPicker:
                    page.AddLinksSection();
                    break;
                case SidebarPaletteAdd.Contribution when contribution is { Length: > 0 } c:
                    page.AddContribution(c);
                    break;
                case SidebarPaletteAdd.RecentlyPlayed:
                    page.AddRecentlyPlayed();
                    break;
                case SidebarPaletteAdd.ActionShortcut:
                    page.AddActionShortcut();
                    break;
                case SidebarPaletteAdd.LikedSongsShortcut:
                    page.AddLikedSongsShortcut();
                    break;
                case SidebarPaletteAdd.AnyContribution:
                    _forward = true;
                    picking.Value = true;
                    break;
                default:
                    page.AddSectionOfKind(kind);
                    break;
            }
        }

        // P6 — DRAG IS ONE OF SEVERAL WAYS, NEVER THE ONLY ONE. The click above always adds (at the end, or into the
        // selected StaticLinks section); the drag below only ADDITIONALLY lets the user choose WHERE. See
        // `SidebarPalette.CanDrag` for the rows that stay click-only and why.
        DragSource? drag = null;
        if (SidebarPalette.CanDrag(add) && !SidebarPalette.AppendsToSelection(entry, appendTo))
        {
            var payload = BuildDropPayload(entry, label);
            if (payload is not null)
                drag = Drag.Source(SidebarEditPlan.SectionDragKind, () => payload,
                    // A palette row is CLICK-PRIMARY (adding at the end is the common intent, placing it the
                    // exception), so it takes WinUI's own list-item drag box multiplier or a click landed while the
                    // mouse is still travelling gets eaten by a drag promotion (dnd pitfall 9).
                    thresholdMultiplier: Drag.ClickPrimaryThresholdMultiplier);
        }

        return Row(GlyphOf(entry), label, sub, Click, drag);
    }

    /// <summary>The payload a dragged chip carries: the whole <c>AddSection</c> argument list minus the index, composed
    /// ONCE here (at render time, captured by the promotion factory) rather than in the per-move drag path.</summary>
    SidebarSectionDropPayload? BuildDropPayload(SidebarPaletteEntry entry, string label) => entry.Add switch
    {
        SidebarPaletteAdd.Destination when entry.RouteKey is { Length: > 0 } route =>
            new SidebarSectionDropPayload(SidebarSectionKind.StaticLinks, label,
                Item: new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, route)),

        SidebarPaletteAdd.LikedSongsShortcut =>
            new SidebarSectionDropPayload(SidebarSectionKind.StaticLinks, label,
                Item: new SidebarItemSpec(SidebarIds.NewItem(), SidebarItemTarget.Route, "liked",
                    IconOverride: "Heart")),

        SidebarPaletteAdd.Contribution when entry.ContributionId is { Length: > 0 } id =>
            new SidebarSectionDropPayload(SidebarSectionKind.Extension, label, Extension: ContributionRef(id)),

        SidebarPaletteAdd.Section => new SidebarSectionDropPayload(entry.Kind, label),
        _ => null,
    };

    /// <summary>A contributed section's ref, seeded with its schema defaults — the same shape
    /// <c>SidebarCustomizerPage.AddContribution</c> builds, so a dropped contribution and a clicked one are identical.</summary>
    SidebarExtensionRef ContributionRef(string contributionId)
    {
        var config = SidebarJson.EmptyObject;
        int schemaVersion = 1;
        if (_page.Registry is { } reg && reg.TryGetSource(contributionId, out var source))
        {
            config = SidebarConfigJson.Defaults(source.ConfigSchema);
            schemaVersion = source.ConfigSchema.Version;
        }
        return new SidebarExtensionRef(SidebarContributions.WaveeExtensionId, contributionId, schemaVersion, config);
    }

    /// <summary>DEFECT 5 — the contribution picker. It keeps the search box (the query filters this list too) and names
    /// each source through the palette's own table where one exists, so the raw id appears at most ONCE, as the
    /// subtitle of a source this build has no name for.</summary>
    void AppendContributions(List<Element> into, string query)
    {
        var sources = _page.Registry?.Sources;
        if (sources is null || sources.Count == 0)
        {
            into.Add(Note(Loc.Get(CzLoc.ExtensionManage)));
            return;
        }

        string q = SidebarPalette.NormalizeQuery(query);
        var page = _page;
        var picking = _pickContribution;
        int shown = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            string id = sources[i].Id;
            var named = SidebarPalette.EntryForContribution(id);
            string label = named is not null ? Loc.Get(named.NameLocKey) : SidebarContributions.ContributionOf(id);
            string sub = named is not null
                ? Loc.Get(named.DescriptionLocKey)
                : Loc.Format(CzLoc.ContributionUnnamed, ("id", id));
            if (!SidebarPalette.Matches(q, label, sub)) continue;
            shown++;
            string glyph = named is not null ? CzGlyphs.ForName(named.IconName) : Icons.Code;
            into.Add(Row(glyph, label, sub, () =>
            {
                picking.Value = false;
                page.AddContribution(id);
            }, drag: null));
        }
        if (shown == 0 && q.Length > 0) into.Add(EmptyLine(query));
    }

    Element BackRow() => new BoxEl
    {
        Direction = 0, Height = 28f, Grow = 1f, Shrink = 1f, MinWidth = 0f,
        AlignItems = FlexAlign.Center, Gap = Spacing.XS,
        Corners = Radii.ControlAll, Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
        OnClick = () => { _forward = false; _pickContribution.Value = false; },
        Children =
        [
            Icon(Icons.ChevronLeft, 12f, Tok.TextSecondary),
            new TextEl(Loc.Get(CzLoc.AddSection)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1 },
        ],
    }.Interactive(Interaction.Subtle);

    static Element EmptyLine(string query) => Note(Loc.Format(CzLoc.PaletteEmpty, ("query", query)));

    static Element Note(string text) => new TextEl(text)
    {
        Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3,
        Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, Spacing.S),
    };

    static Element Row(string glyph, string label, string? sub, Action onClick, DragSource? drag)
    {
        var lines = new List<Element>(2)
        {
            new TextEl(label)
            {
                Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            },
        };
        if (sub is { Length: > 0 })
            lines.Add(new TextEl(sub)
            {
                Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
            });

        return new BoxEl
        {
            Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XS, 6f, Spacing.XS, 6f), Corners = Radii.ControlAll,
            Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = onClick,
            // The drag source sits on the CLICK-OWNING node itself (the sidebar row pattern the dnd skill names): one
            // node owns hit-testing, the pressed visual, the drag arm and the click, so `DragController.TryArm`'s
            // walk-up has nothing to disambiguate.
            Draggable = drag,
            Children =
            [
                new BoxEl
                {
                    Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.ControlAll,
                    Fill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
                    Children = [Icon(glyph, 13f, Tok.TextSecondary)],
                },
                new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = [.. lines] },
                AddChip(),
            ],
        }.Interactive(Interaction.ListRow);
    }

    /// <summary>The trailing "+". It is an AFFORDANCE, not a second button: the whole row already adds, so the chip is
    /// hit-test-transparent and merely says out loud what a click will do. It brightens on ROW hover — the engine
    /// cascades a container's hover to a descendant only for an opacity/scale REVEAL (<c>AnimScheduler.Hover</c>), which
    /// is exactly (and only) what this uses; a HoverFill here would never be reached.</summary>
    static Element AddChip() => new BoxEl
    {
        Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.FullAll,
        Fill = Tok.FillSubtleSecondary,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HitTestVisible = false,
        Opacity = 0.45f, HoverOpacity = 1f, HoverDurationMs = Motion.ControlFaster,
        Children = [Icon(Icons.Add, 12f, Tok.TextSecondary)],
    };

    static Element Header(string text) => CzRow.GroupLabel(text) with
    {
        Margin = new Edges4(Spacing.XS, Spacing.S, Spacing.XS, 2f),
    };

    /// <summary>The search box is UNCONDITIONAL — including in contribution-pick mode, which used to drop it (defect 5).
    /// It is a controlled <c>TextBox</c> over the page's own query signal, so clearing that signal (defect 4's policy)
    /// visibly empties the box.</summary>
    Element SearchBox() => TextBox.Create(_page.PaletteQuery, null, new TextBox.TextBoxOptions
    {
        Placeholder = Loc.Get(CzLoc.PaletteSearch),
        Height = 30f,
    });
}

/// <summary>The customizer's glyph resolution, in ONE place: palette entry NAMES (the pure table is engine-free, so it
/// carries names) and section KINDS (the section card, the property panel and the hidden list must agree on a kind's
/// mark). An unknown value degrades to the neutral section mark, never a blank box.</summary>
static class CzGlyphs
{
    public static string ForName(string? name) => name switch
    {
        "Pin" => Icons.Pin,
        "Heart" => Icons.Heart,
        "Link" => Icons.Link,
        "Folder" => Icons.Folder,
        "Filter" => Icons.Filter,
        "FavoriteStar" => Icons.FavoriteStar,
        "Headphones" => Icons.Headphones,
        "Queue" => Icons.Queue,
        "Play" => Icons.Play,
        "Clock" => Icons.Clock,
        "Contact" => Icons.Contact,
        "Album" => Icons.Album,
        "Calendar" => Icons.Calendar,
        "Grid" => Icons.Grid,
        "Font" => Icons.Font,
        "Remove" => Icons.Remove,
        "RefineSparkle" => Icons.RefineSparkle,
        "Code" => Icons.Code,
        _ => Icons.MusicNote,
    };

    public static string ForKind(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.Pinned => Icons.Pin,
        SidebarSectionKind.JumpBackIn => Icons.Clock,
        SidebarSectionKind.CollectionShortcuts => Icons.Heart,
        SidebarSectionKind.PlaylistTree => Icons.Folder,
        SidebarSectionKind.EntityList => Icons.Filter,
        SidebarSectionKind.StaticLinks => Icons.Link,
        SidebarSectionKind.CustomGroup => Icons.Grid,
        SidebarSectionKind.Header => Icons.Font,
        SidebarSectionKind.Divider => Icons.Remove,
        SidebarSectionKind.EntityEmbed => Icons.FavoriteStar,
        SidebarSectionKind.NewReleases => Icons.Album,
        SidebarSectionKind.Concerts => Icons.Calendar,
        SidebarSectionKind.Extension => Icons.Code,
        _ => Icons.MusicNote,
    };

    /// <summary>A section's display TITLE: the user rename wins, else the localized kind/template key, else the kind's
    /// palette name — so a listed section is never blank (a Divider legitimately has no title of its own, and so does a
    /// kind this build does not understand; the caller supplies the fallback word for those).</summary>
    public static string TitleOf(SidebarSectionSpec section)
    {
        if (section.Title is { Length: > 0 } t) return t;
        if (section.TitleLocKey is { Length: > 0 } k) return Loc.Get(k);
        if (SidebarSectionKinds.PaletteNameLocKey(section.Kind) is { Length: > 0 } p) return Loc.Get(p);
        return "";
    }
}

/// <summary>The five template cards (P7's second half). Radio semantics: the document's own <c>TemplateId</c> is the
/// checked one; picking another routes through the page's confirmation.</summary>
sealed class SidebarTemplateList : Component
{
    readonly SidebarCustomizerPage _page;

    public SidebarTemplateList(SidebarCustomizerPage page) => _page = page;

    public override Element Render()
    {
        var prefs = _page.Prefs;
        _ = prefs?.LayoutVersion.Value ?? 0;      // the active template changes with the document
        string active = prefs?.Layout.TemplateId ?? SidebarTemplates.Curated;

        var ids = SidebarTemplates.All;
        var kids = new List<Element>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            string id = ids[i];
            bool on = string.Equals(id, active, StringComparison.Ordinal);
            kids.Add(Card(id, on, () => _page.ApplyTemplate(id)));
        }
        return CzRow.Group(CzLoc.Templates, kids);
    }

    static Element Card(string templateId, bool active, Action onClick) => new BoxEl
    {
        Direction = 0, Shrink = 0f, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
        // Selection-aware fills set EXPLICITLY: `.Interactive(...)` overwrites all three from its recipe, which would
        // erase the active card's plate — so this row styles its own ramp.
        Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
        HoverFill = active ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
        PressedFill = active ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.RadioButton, OnClick = onClick,
        Children =
        [
            Icon(active ? Icons.RadioBullet : Icons.Grid, 14f, active ? Tok.AccentDefault : Tok.TextSecondary),
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f,
                Children =
                [
                    new TextEl(Loc.Get(SidebarTemplates.NameLocKey(templateId)))
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    new TextEl(Loc.Get(SidebarTemplates.DescriptionLocKey(templateId)))
                    {
                        Size = 11f, Color = Tok.TextTertiary, MaxLines = 2, Wrap = TextWrap.Wrap,
                    },
                ],
            },
            // The ACTIVE checkmark. The card already carries an accent plate, but a plate reads as "hovered" to a user
            // who has not moved the pointer — the tick is the unambiguous "this one is applied".
            active
                ? (Element)(Icon(Icons.Accept, 14f, Tok.AccentTextPrimary) with { Margin = new Edges4(0f, 0f, 2f, 0f) })
                : new BoxEl { Width = 16f, Shrink = 0f },
        ],
    };
}
