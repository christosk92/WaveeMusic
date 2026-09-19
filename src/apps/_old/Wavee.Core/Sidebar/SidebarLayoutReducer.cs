using System.Text.Json;

namespace Wavee.Core.Sidebar;

// The PURE custom-sidebar reducer. One entry point, one verdict, zero side effects: Apply never mutates its input (every
// edit rebuilds only the spine and structurally shares the rest), never touches disk, never localizes, and never reaches
// for a service. That is what makes undo a pre-image snapshot and autosave a one-liner in SidebarPreferences.Dispatch.
//
// Rejections are DATA, not exceptions: !Changed carries a SidebarRejectReason the customizer can surface inline and the
// caller uses to skip the undo push + the save entirely.

public static class SidebarLayoutReducer
{
    public const int MaxSections = 40;        // top level + children combined
    public const int MaxItemsPerSection = 500;
    public const int MaxTitleLength = 60;

    /// <summary>How many shortcuts the shell's TOP BAR band may hold. Small on purpose and enforced HERE (never in the UI):
    /// the band shares one 48-DIP row with the fixed chrome and the centred omnibar, so past this the omnibar starts losing
    /// its usable width. Over-cap is a <see cref="SidebarRejectReason.SectionCapReached"/> rejection, never a truncation.</summary>
    public const int MaxTopBarItems = 6;

    /// <summary>How many uris an include/exclude set may carry (LAYOUT V2). Same order as the item cap: the sets exist so
    /// "only these artists" is a query instead of a hand-list — past this it IS a hand-list, and the document budget
    /// (2 MiB) is the next wall. Over-long sets are TRUNCATED by normalization, never rejected.</summary>
    public const int MaxUrisPerSet = 500;

    /// <summary>Reduce without a pin list — stale <c>Pinned</c> overrides are left alone (they are only pruned when the
    /// caller can prove a key is no longer pinned).</summary>
    public static SidebarCommandResult Apply(SidebarCustomLayout layout, SidebarCommand command)
        => Apply(layout, command, null);

    /// <summary>Reduce with the live pin key set (<see cref="SidebarItemSpec.Key"/> values, i.e. pinned uris).
    /// A <c>Pinned</c> section's override entries whose key is no longer pinned are pruned when — and only when — a
    /// command touches that section (§C1.6: never eagerly, so an accidental unpin+repin keeps the alias).</summary>
    public static SidebarCommandResult Apply(SidebarCustomLayout layout, SidebarCommand command,
        IReadOnlySet<string>? pinnedKeys)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            AddSection c => DoAddSection(layout, c, pinnedKeys),
            RemoveSection c => DoRemoveSection(layout, c),
            DuplicateSection c => DoDuplicateSection(layout, c),
            RenameSection c => DoRenameSection(layout, c, pinnedKeys),
            SetSectionHidden c => DoSetHidden(layout, c, pinnedKeys),
            SetSectionCollapsed c => DoSetCollapsed(layout, c, pinnedKeys),
            MoveSection c => DoMoveSection(layout, c, pinnedKeys),
            AddItem c => DoAddItem(layout, c, pinnedKeys),
            MoveItem c => DoMoveItem(layout, c, pinnedKeys),
            RemoveItem c => DoRemoveItem(layout, c, pinnedKeys),
            SetItemLabel c => DoSetItemLabel(layout, c, pinnedKeys),
            SetItemIcon c => DoSetItemIcon(layout, c, pinnedKeys),
            SetDisplayOption c => DoSetDisplayOption(layout, c, pinnedKeys),
            SetQuery c => DoSetQuery(layout, c, pinnedKeys),
            SetExtensionConfig c => DoSetExtensionConfig(layout, c, pinnedKeys),
            SetItemAction c => DoSetItemAction(layout, c, pinnedKeys),
            AddTopBarItem c => DoAddTopBarItem(layout, c),
            MoveTopBarItem c => DoMoveTopBarItem(layout, c),
            RemoveTopBarItem c => DoRemoveTopBarItem(layout, c),
            ApplyTemplate c => DoApplyTemplate(layout, c),
            // A template / reset REPLACES the sidebar sections and PRESERVES the top bar: a template is a sidebar-section
            // preset, and silently reinstating Home in the shell chrome (or dropping the user's own shortcuts) because they
            // tried a different sidebar layout would be a data-shaped surprise. Carrying `null` through keeps "never
            // customized" meaning the built-in default.
            ResetLayout => SidebarCommandResult.Ok(
                SidebarTemplates.Build(layout.TemplateId) with { TopBar = layout.TopBar }),
            _ => SidebarCommandResult.Reject(layout, SidebarRejectReason.NoChange),
        };
    }

    // ── AddSection ───────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoAddSection(SidebarCustomLayout l, AddSection c, IReadOnlySet<string>? pins)
    {
        // A kind this build does not understand can only arrive through the document (where it round-trips untouched);
        // it is never something the palette can add.
        if (!SidebarSectionKinds.IsKnown(c.Kind)) return Rej(l, SidebarRejectReason.NoChange);

        int parentTop = -1;
        if (c.ParentId is { Length: > 0 } pid)
        {
            if (!TryLocate(l, pid, out int pt, out int pc)) return Rej(l, SidebarRejectReason.UnknownSection);
            if (!SidebarSectionKinds.IsNestable(c.Kind)) return Rej(l, SidebarRejectReason.NestingTooDeep);
            if (pc >= 0) return Rej(l, SidebarRejectReason.NestingTooDeep);   // the parent is itself a child
            if (l.Sections[pt].Kind != SidebarSectionKind.CustomGroup)
                return Rej(l, SidebarRejectReason.KindNotNestable);
            parentTop = pt;
        }

        if (l.SectionCount >= MaxSections) return Rej(l, SidebarRejectReason.SectionCapReached);

        // LAYOUT V2: a contributed section is defined by its ref. The palette always has one (the user picked a
        // contribution); a ref-less Extension section could only render the "Manage extension" placeholder forever.
        SidebarExtensionRef? extension = null;
        if (SidebarSectionKinds.RequiresExtensionRef(c.Kind))
        {
            if (c.Extension is not { IsWellFormed: true } incoming)
                return Rej(l, SidebarRejectReason.ExtensionRefMissing);
            if (incoming.ConfigByteCount > SidebarExtensionRef.MaxConfigBytes)
                return Rej(l, SidebarRejectReason.ConfigTooLarge);
            extension = NormalizeRef(incoming);
        }

        IReadOnlyList<SidebarItemSpec>? items = null;
        if (c.Item is { } seed)
        {
            if (!SidebarSectionKinds.AcceptsItems(c.Kind)) return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
            if (c.Kind == SidebarSectionKind.EntityEmbed && seed.Target != SidebarItemTarget.Entity)
                return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
            if (seed.IconOverride is not null && !SidebarIconNames.IsAllowed(seed.IconOverride))
                return Rej(l, SidebarRejectReason.InvalidIcon);
            items = new[] { NormalizeItem(seed with { Id = SidebarIds.NewItem() }) };
        }

        var display = SidebarSectionKinds.DefaultDisplay(c.Kind);
        var spec = new SidebarSectionSpec(
            Id: FreshSectionId(l),
            Kind: c.Kind,
            Title: null,
            TitleLocKey: SidebarSectionKinds.DefaultTitleLocKey(c.Kind, display.Recents),
            Hidden: false,
            Collapsed: display.CollapsedByDefault,
            Display: display,
            Items: items,
            Query: c.Kind == SidebarSectionKind.EntityList ? SidebarEntityQuery.Default : null,
            Children: null,
            Extension: extension);

        var tops = new List<SidebarSectionSpec>(l.Sections);
        if (parentTop >= 0)
        {
            var parent = tops[parentTop];
            var kids = new List<SidebarSectionSpec>(parent.ChildList);
            kids.Insert(Math.Clamp(c.Index, 0, kids.Count), spec);
            tops[parentTop] = parent with { Children = kids };
        }
        else
        {
            tops.Insert(Math.Clamp(c.Index, 0, tops.Count), spec);
        }
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    // ── RemoveSection / DuplicateSection ─────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoRemoveSection(SidebarCustomLayout l, RemoveSection c)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);

        var tops = new List<SidebarSectionSpec>(l.Sections);
        if (child < 0)
        {
            tops.RemoveAt(top);                    // removing a group removes its children with it
        }
        else
        {
            var p = tops[top];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.RemoveAt(child);
            tops[top] = p with { Children = kids.Count == 0 ? null : kids };
        }
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    static SidebarCommandResult DoDuplicateSection(SidebarCustomLayout l, DuplicateSection c)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var src = Get(l.Sections, top, child);

        // DEFECT 9 — a STORE-BACKED section cannot be duplicated. Its rows and their order live in a shared store
        // (the pin store, the rootlist), not in the spec, so a clone is a second WRITER onto one list: two Pinned
        // sections render the same pins and BOTH commit their reorders into the same store, so a drag in the copy
        // silently reshuffles the original. Fresh ids cannot separate them, because the id was never what bound the
        // section to the store — the KIND was. Refused, not repaired: see SidebarSectionKinds.IsStoreBacked.
        // A GROUP is refused when any child is store-backed, for the same reason one level down.
        if (SidebarSectionKinds.IsStoreBacked(src.Kind)) return Rej(l, SidebarRejectReason.KindNotDuplicable);
        var srcKids = src.ChildList;
        for (int i = 0; i < srcKids.Count; i++)
            if (SidebarSectionKinds.IsStoreBacked(srcKids[i].Kind))
                return Rej(l, SidebarRejectReason.KindNotDuplicable);

        if (l.SectionCount + 1 + src.ChildList.Count > MaxSections)
            return Rej(l, SidebarRejectReason.SectionCapReached);

        var used = CollectIds(l);
        var clone = CloneWithFreshIds(src, used);
        // DEFECT 10 — AN AUTHORED TitleLocKey SURVIVES THE COPY.
        //
        // This used to take the caller's "{name} (copy)" literal unconditionally and CLEAR TitleLocKey, which froze a
        // culture-following title into whatever language happened to be active at the moment of the duplicate: a copy
        // of "Playlists" made under nl stayed "Afspeellijsten (kopie)" forever, in every language.
        //
        // THE DECIDING QUESTION IS RECOVERABILITY, because `RenameSection(null)` reverts to the KIND DEFAULT — not to
        // whatever key the section carried:
        //   * TitleLocKey != null (a template/kind-authored, culture-following name) — a literal here would be
        //     UNRECOVERABLE: clearing the rename lands on the kind default, and the authored key is gone for good. The
        //     copy therefore KEEPS the key and takes no literal, so it keeps following the culture. The cost is stated,
        //     not hidden: the copy reads the same as the original until the user renames it — one click, and reversible.
        //     Carrying BOTH would not help either; Title wins in TitleOf, so the key would be unreachable data.
        //   * TitleLocKey == null (a user rename, or a section with no authored title at all) — nothing localized is
        //     lost, and the literal is fully recoverable: clearing it returns to exactly what the original shows. The
        //     caller's "{name} (copy)" lands verbatim, exactly as before.
        if (c.TitleOverride is { } t && clone.TitleLocKey is null)
        {
            var title = Shorten(t);
            clone = clone with { Title = title, TitleLocKey = null };
        }

        var tops = new List<SidebarSectionSpec>(l.Sections);
        if (child < 0)
        {
            tops.Insert(top + 1, clone);
        }
        else
        {
            var p = tops[top];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.Insert(child + 1, clone);
            tops[top] = p with { Children = kids };
        }
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    // ── Section scalars ─────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoRenameSection(SidebarCustomLayout l, RenameSection c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);

        string? title = Shorten(c.Title);
        // Clearing a rename reverts to the localized kind default.
        string? locKey = title is null
            ? SidebarSectionKinds.DefaultTitleLocKey(s.Kind, s.Opts.Recents)
            : null;

        if (string.Equals(title, s.Title, StringComparison.Ordinal) &&
            string.Equals(locKey, s.TitleLocKey, StringComparison.Ordinal))
            return Rej(l, SidebarRejectReason.NoChange);

        return SidebarCommandResult.Ok(
            Replace(l, top, child, s with { Title = title, TitleLocKey = locKey }, pins));
    }

    static SidebarCommandResult DoSetHidden(SidebarCustomLayout l, SetSectionHidden c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        if (s.Hidden == c.Hidden) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Hidden = c.Hidden }, pins));
    }

    static SidebarCommandResult DoSetCollapsed(SidebarCustomLayout l, SetSectionCollapsed c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        if (s.Collapsed == c.Collapsed) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Collapsed = c.Collapsed }, pins));
    }

    static SidebarCommandResult DoMoveSection(SidebarCustomLayout l, MoveSection c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var moving = Get(l.Sections, top, child);

        int destTop = -1;
        if (c.NewParentId is { Length: > 0 } pid)
        {
            if (string.Equals(pid, c.SectionId, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.NestingTooDeep);                 // into itself
            if (!TryLocate(l, pid, out int pt, out int pc)) return Rej(l, SidebarRejectReason.UnknownSection);
            if (pc >= 0) return Rej(l, SidebarRejectReason.NestingTooDeep);         // the target is itself a child
            if (!SidebarSectionKinds.IsNestable(moving.Kind))
                return Rej(l, SidebarRejectReason.NestingTooDeep);                  // a group may never nest…
            if (IsAncestorOf(l, c.SectionId, pid))
                return Rej(l, SidebarRejectReason.NestingTooDeep);                  // …nor into its own child
            if (l.Sections[pt].Kind != SidebarSectionKind.CustomGroup)
                return Rej(l, SidebarRejectReason.KindNotNestable);
            destTop = pt;
        }

        var tops = new List<SidebarSectionSpec>(l.Sections);

        // Remove first — NewIndex is interpreted AFTER removal (the standard Reorderable.OnReorder contract).
        if (child < 0)
        {
            tops.RemoveAt(top);
        }
        else
        {
            var p = tops[top];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.RemoveAt(child);
            tops[top] = p with { Children = kids.Count == 0 ? null : kids };
        }

        int dt = destTop;
        if (child < 0 && dt > top) dt--;   // the removal shifted the destination group left

        var inserted = NormalizeSection(moving, pins);
        if (dt < 0)
        {
            tops.Insert(Math.Clamp(c.NewIndex, 0, tops.Count), inserted);
        }
        else
        {
            var p = tops[dt];
            var kids = new List<SidebarSectionSpec>(p.ChildList);
            kids.Insert(Math.Clamp(c.NewIndex, 0, kids.Count), inserted);
            tops[dt] = p with { Children = kids };
        }

        if (SameArrangement(l.Sections, tops)) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    // ── Items ───────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoAddItem(SidebarCustomLayout l, AddItem c, IReadOnlySet<string>? pins)
    {
        if (c.Item is null) return Rej(l, SidebarRejectReason.NoChange);
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);

        if (!SidebarSectionKinds.AcceptsItems(s.Kind)) return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        if (s.Kind == SidebarSectionKind.EntityEmbed && c.Item.Target != SidebarItemTarget.Entity)
            return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        if (c.Item.IconOverride is not null && !SidebarIconNames.IsAllowed(c.Item.IconOverride))
            return Rej(l, SidebarRejectReason.InvalidIcon);

        var items = new List<SidebarItemSpec>(s.ItemList);

        // EntityEmbed is the single-item kind: a second add RETARGETS the spotlight rather than stacking.
        if (s.Kind == SidebarSectionKind.EntityEmbed)
        {
            var one = NormalizeItem(c.Item with { Id = FreshItemId(l, c.Item.Id) });
            if (items.Count == 1 && ItemsEquivalent(items[0], one)) return Rej(l, SidebarRejectReason.NoChange);
            return SidebarCommandResult.Ok(
                Replace(l, top, child, s with { Items = new[] { one } }, pins));
        }

        for (int i = 0; i < items.Count; i++)
            if (items[i].Target == c.Item.Target && string.Equals(items[i].Key, c.Item.Key, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.DuplicateItem);

        if (items.Count >= MaxItemsPerSection) return Rej(l, SidebarRejectReason.SectionCapReached);

        var item = NormalizeItem(c.Item with { Id = FreshItemId(l, c.Item.Id) });
        items.Insert(Math.Clamp(c.Index, 0, items.Count), item);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    static SidebarCommandResult DoMoveItem(SidebarCustomLayout l, MoveItem c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.FromSectionId, out int ft, out int fc)) return Rej(l, SidebarRejectReason.UnknownSection);
        if (!TryLocate(l, c.ToSectionId, out int tt, out int tc)) return Rej(l, SidebarRejectReason.UnknownSection);

        var src = Get(l.Sections, ft, fc);   // ("src", not "from" — `from … with` trips the query-expression parser)
        var to = Get(l.Sections, tt, tc);
        if (!SidebarSectionKinds.AcceptsItems(src.Kind) || !SidebarSectionKinds.AcceptsItems(to.Kind))
            return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        if (c.FromIndex < 0 || c.FromIndex >= src.ItemList.Count) return Rej(l, SidebarRejectReason.UnknownItem);

        var moving = src.ItemList[c.FromIndex];
        bool same = string.Equals(c.FromSectionId, c.ToSectionId, StringComparison.Ordinal);

        if (same)
        {
            var items = new List<SidebarItemSpec>(src.ItemList);
            items.RemoveAt(c.FromIndex);
            int at = Math.Clamp(c.ToIndex, 0, items.Count);
            if (at == c.FromIndex) return Rej(l, SidebarRejectReason.NoChange);
            items.Insert(at, moving);
            return SidebarCommandResult.Ok(Replace(l, ft, fc, src with { Items = items }, pins));
        }

        if (to.Kind == SidebarSectionKind.EntityEmbed && moving.Target != SidebarItemTarget.Entity)
            return Rej(l, SidebarRejectReason.KindDoesNotAcceptItems);
        for (int i = 0; i < to.ItemList.Count; i++)
            if (to.ItemList[i].Target == moving.Target &&
                string.Equals(to.ItemList[i].Key, moving.Key, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.DuplicateItem);
        if (to.ItemList.Count >= SidebarSectionKinds.ItemCapacity(to.Kind))
            return Rej(l, SidebarRejectReason.SectionCapReached);

        var srcItems = new List<SidebarItemSpec>(src.ItemList);
        srcItems.RemoveAt(c.FromIndex);
        var dstItems = new List<SidebarItemSpec>(to.ItemList);
        dstItems.Insert(Math.Clamp(c.ToIndex, 0, dstItems.Count), moving);

        var tops = new List<SidebarSectionSpec>(l.Sections);
        Set(tops, ft, fc, NormalizeSection(src with { Items = srcItems.Count == 0 ? null : srcItems }, pins));
        // Re-locate the destination: Set rebuilt the parent record, not the indices, so (tt, tc) still address it.
        Set(tops, tt, tc, NormalizeSection(Get(tops, tt, tc) with { Items = dstItems }, pins));
        return SidebarCommandResult.Ok(l with { Sections = tops });
    }

    static SidebarCommandResult DoRemoveItem(SidebarCustomLayout l, RemoveItem c, IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items.RemoveAt(at);
        return SidebarCommandResult.Ok(
            Replace(l, top, child, s with { Items = items.Count == 0 ? null : items }, pins));
    }

    static SidebarCommandResult DoSetItemLabel(SidebarCustomLayout l, SetItemLabel c, IReadOnlySet<string>? pins)
    {
        if (SidebarIds.IsTopBar(c.SectionId))
        {
            int bandAt = TopBarIndexOf(l, c.ItemId);
            if (bandAt < 0) return Rej(l, SidebarRejectReason.UnknownItem);
            var tile = l.EffectiveTopBar[bandAt];
            string? alias = Shorten(c.Label);
            if (string.Equals(alias, tile.LabelOverride, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.NoChange);
            return TopBarReplace(l, bandAt, tile with { LabelOverride = alias });
        }
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        string? label = Shorten(c.Label);
        if (string.Equals(label, s.ItemList[at].LabelOverride, StringComparison.Ordinal))
            return Rej(l, SidebarRejectReason.NoChange);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items[at] = items[at] with { LabelOverride = label };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    static SidebarCommandResult DoSetItemIcon(SidebarCustomLayout l, SetItemIcon c, IReadOnlySet<string>? pins)
    {
        if (SidebarIds.IsTopBar(c.SectionId))
        {
            int bandAt = TopBarIndexOf(l, c.ItemId);
            if (bandAt < 0) return Rej(l, SidebarRejectReason.UnknownItem);
            var tile = l.EffectiveTopBar[bandAt];
            string? mark = c.IconName is { Length: > 0 } ? c.IconName : null;
            if (mark is not null && !SidebarIconNames.IsAllowed(mark)) return Rej(l, SidebarRejectReason.InvalidIcon);
            if (string.Equals(mark, tile.IconOverride, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.NoChange);
            return TopBarReplace(l, bandAt, tile with { IconOverride = mark });
        }
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        string? icon = c.IconName is { Length: > 0 } ? c.IconName : null;
        if (icon is not null && !SidebarIconNames.IsAllowed(icon)) return Rej(l, SidebarRejectReason.InvalidIcon);
        if (string.Equals(icon, s.ItemList[at].IconOverride, StringComparison.Ordinal))
            return Rej(l, SidebarRejectReason.NoChange);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items[at] = items[at] with { IconOverride = icon };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    // ── Display + query ─────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult DoSetDisplayOption(SidebarCustomLayout l, SetDisplayOption c,
        IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        if (!SidebarSectionKinds.AllowsDisplayField(s.Kind, c.Field)) return Rej(l, SidebarRejectReason.NoChange);

        var opts = WithField(s.Opts, c.Field, c.Value);
        if (opts == s.Opts) return Rej(l, SidebarRejectReason.NoChange);

        var next = s with { Display = opts };
        // A JumpBackIn section that still wears its localized kind default retargets it when the recents source flips
        // ("Jump back in" <-> "Recently played"); an explicit rename (Title != null) is never touched.
        if (c.Field == SidebarDisplayField.RecentsSource && next.Title is null && next.TitleLocKey is not null)
            next = next with { TitleLocKey = SidebarSectionKinds.DefaultTitleLocKey(next.Kind, opts.Recents) };

        // Setting CollapsedByDefault must NOT change the live Collapsed state.
        return SidebarCommandResult.Ok(Replace(l, top, child, next, pins));
    }

    static SidebarCommandResult DoSetQuery(SidebarCustomLayout l, SetQuery c, IReadOnlySet<string>? pins)
    {
        if (c.Query is null) return Rej(l, SidebarRejectReason.NoChange);
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        // Only library-query kinds have a query; the closed reject-reason enum has no dedicated code, so every other
        // section reads as NoChange.
        if (!SidebarSectionKinds.SupportsLibraryQuery(s.Kind)) return Rej(l, SidebarRejectReason.NoChange);

        var q = RepairQuery(c.Query, s.Kind);
        if (q == SidebarSectionKinds.EffectiveQuery(s.Kind, s.Query)) return Rej(l, SidebarRejectReason.NoChange);
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Query = q }, pins));
    }

    // ── extension config / action bindings (LAYOUT V2) ───────────────────────────────────────────────────────────────

    static SidebarCommandResult DoSetExtensionConfig(SidebarCustomLayout l, SetExtensionConfig c,
        IReadOnlySet<string>? pins)
    {
        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        // Only an Extension section has a config; the closed reject-reason enum reads this as NoChange, like SetQuery on
        // a non-EntityList section.
        if (!s.IsExtension) return Rej(l, SidebarRejectReason.NoChange);
        if (s.Extension is not { } current) return Rej(l, SidebarRejectReason.ExtensionRefMissing);

        if (SidebarJson.ByteCount(c.Config) > SidebarExtensionRef.MaxConfigBytes)
            return Rej(l, SidebarRejectReason.ConfigTooLarge);
        if (SidebarJson.Same(current.Config, c.Config)) return Rej(l, SidebarRejectReason.NoChange);

        // Own the element: the caller's JsonDocument may be pooled/disposed, and the document must stay serializable.
        var next = current with { Config = SidebarJson.Own(c.Config) };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Extension = next }, pins));
    }

    static SidebarCommandResult DoSetItemAction(SidebarCustomLayout l, SetItemAction c, IReadOnlySet<string>? pins)
    {
        // The binding validation is identical for a section item and a top-bar tile, so it runs BEFORE the addressing split.
        SidebarActionBinding? binding = null;
        if (c.Binding is { } incoming)
        {
            // A blank provider/action id is not a "clear" — the caller asked for a binding it cannot address.
            if (NormalizeBinding(incoming) is not { } normalized) return Rej(l, SidebarRejectReason.NoChange);
            if (normalized.ArgumentsByteCount > SidebarExtensionRef.MaxConfigBytes)
                return Rej(l, SidebarRejectReason.ConfigTooLarge);
            binding = normalized;
        }

        if (SidebarIds.IsTopBar(c.SectionId))
        {
            int bandAt = TopBarIndexOf(l, c.ItemId);
            if (bandAt < 0) return Rej(l, SidebarRejectReason.UnknownItem);
            var tile = l.EffectiveTopBar[bandAt];
            if (binding == tile.Action) return Rej(l, SidebarRejectReason.NoChange);
            return TopBarReplace(l, bandAt, tile with { Action = binding });
        }

        if (!TryLocate(l, c.SectionId, out int top, out int child)) return Rej(l, SidebarRejectReason.UnknownSection);
        var s = Get(l.Sections, top, child);
        int at = IndexOfItem(s, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        if (binding == s.ItemList[at].Action) return Rej(l, SidebarRejectReason.NoChange);

        var items = new List<SidebarItemSpec>(s.ItemList);
        items[at] = items[at] with { Action = binding };
        return SidebarCommandResult.Ok(Replace(l, top, child, s with { Items = items }, pins));
    }

    static SidebarCommandResult DoApplyTemplate(SidebarCustomLayout l, ApplyTemplate c)
    {
        if (!SidebarTemplates.IsKnown(c.TemplateId)) return Rej(l, SidebarRejectReason.UnknownTemplate);
        // PRESERVE the top bar — see the ResetLayout arm in Apply for why a template must not touch shell chrome.
        return SidebarCommandResult.Ok(SidebarTemplates.Build(c.TemplateId) with { TopBar = l.TopBar });
    }

    // ── the shell TOP BAR band ───────────────────────────────────────────────────────────────────────────────────────
    // Every arm reduces against EffectiveTopBar, so the first edit to a never-customized band materializes the built-in
    // default (Home) instead of starting from nothing — what the user sees IS what they are editing.

    static SidebarCommandResult DoAddTopBarItem(SidebarCustomLayout l, AddTopBarItem c)
    {
        if (c.Item is null) return Rej(l, SidebarRejectReason.NoChange);
        if (c.Item.IconOverride is not null && !SidebarIconNames.IsAllowed(c.Item.IconOverride))
            return Rej(l, SidebarRejectReason.InvalidIcon);

        var band = l.EffectiveTopBar;
        for (int i = 0; i < band.Count; i++)
            if (band[i].Target == c.Item.Target && string.Equals(band[i].Key, c.Item.Key, StringComparison.Ordinal))
                return Rej(l, SidebarRejectReason.DuplicateItem);
        if (band.Count >= MaxTopBarItems) return Rej(l, SidebarRejectReason.SectionCapReached);

        var items = new List<SidebarItemSpec>(band);
        items.Insert(Math.Clamp(c.Index, 0, items.Count), NormalizeItem(c.Item with { Id = FreshItemId(l, c.Item.Id) }));
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    static SidebarCommandResult DoMoveTopBarItem(SidebarCustomLayout l, MoveTopBarItem c)
    {
        var band = l.EffectiveTopBar;
        if (c.FromIndex < 0 || c.FromIndex >= band.Count) return Rej(l, SidebarRejectReason.UnknownItem);

        var items = new List<SidebarItemSpec>(band);
        var moving = items[c.FromIndex];
        items.RemoveAt(c.FromIndex);
        int at = Math.Clamp(c.ToIndex, 0, items.Count);
        if (at == c.FromIndex) return Rej(l, SidebarRejectReason.NoChange);
        items.Insert(at, moving);
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    static SidebarCommandResult DoRemoveTopBarItem(SidebarCustomLayout l, RemoveTopBarItem c)
    {
        var band = l.EffectiveTopBar;
        int at = IndexOfItem(band, c.ItemId);
        if (at < 0) return Rej(l, SidebarRejectReason.UnknownItem);

        var items = new List<SidebarItemSpec>(band);
        items.RemoveAt(at);
        // An EMPTY list, never null: null would re-render the built-in Home the user just removed.
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    /// <summary>Locate a tile in the effective band, or -1 — the shared head of the three item-PROPERTY commands when they
    /// are addressed at <see cref="SidebarIds.TopBarSection"/>.</summary>
    static int TopBarIndexOf(SidebarCustomLayout l, string? itemId)
        => itemId is { Length: > 0 } id ? IndexOfItem(l.EffectiveTopBar, id) : -1;

    /// <summary>Rewrite one tile in place. The band is copied here (never mutated) exactly like a section's item list.</summary>
    static SidebarCommandResult TopBarReplace(SidebarCustomLayout l, int at, SidebarItemSpec item)
    {
        var items = new List<SidebarItemSpec>(l.EffectiveTopBar);
        items[at] = item;
        return SidebarCommandResult.Ok(l with { TopBar = items });
    }

    /// <summary>Legality repair for a library-query kind, never a rejection (§C3.3 SetQuery).</summary>
    public static SidebarEntityQuery RepairQuery(SidebarEntityQuery q)
        => RepairQuery(q, SidebarSectionKind.EntityList);

    /// <summary>Legality repair against the OWNING kind (LAYOUT V2). The scalar repairs are unconditional; the
    /// include/exclude uri sets are normalized (trim, drop blanks, dedupe ordinal, truncate to
    /// <see cref="MaxUrisPerSet"/>, empty ⇒ null) and survive ONLY on a library-query-capable kind — a query that ended
    /// up on some other kind (a hand-edited document, a kind changed by a newer build) loses its uri sets rather than
    /// carrying a filter nothing applies.</summary>
    public static SidebarEntityQuery RepairQuery(SidebarEntityQuery q, SidebarSectionKind kind)
    {
        var kinds = kind == SidebarSectionKind.PlaylistTree
            ? SidebarEntityKinds.Playlists
            : q.Kinds == SidebarEntityKinds.None ? SidebarEntityKinds.All : q.Kinds;
        var sort = q.Sort;
        if (sort == SidebarSortMode.CustomOrder && kinds != SidebarEntityKinds.Playlists)
            sort = SidebarSortMode.Alphabetical;
        var qual = q.Qualifier;
        if (qual != SidebarPlaylistQualifier.Any && (kinds & SidebarEntityKinds.Playlists) == 0)
            qual = SidebarPlaylistQualifier.Any;

        bool queryable = SidebarSectionKinds.SupportsLibraryQuery(kind);
        var include = queryable ? NormalizeUris(q.IncludeUris) : null;
        var exclude = queryable ? NormalizeUris(q.ExcludeUris) : null;

        return q with
        {
            Kinds = kinds, Sort = sort, Qualifier = qual, IncludeUris = include, ExcludeUris = exclude,
        };
    }

    /// <summary>Trim, drop blanks, dedupe (ordinal, first wins), truncate to <see cref="MaxUrisPerSet"/>; an empty
    /// result is null so the wire never carries <c>[]</c>. Returns the SAME instance when nothing needed changing.</summary>
    public static IReadOnlyList<string>? NormalizeUris(IReadOnlyList<string>? uris)
    {
        if (uris is null || uris.Count == 0) return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string>? kept = null;                     // stays null while every entry so far survived UNCHANGED
        for (int i = 0; i < uris.Count; i++)
        {
            string raw = uris[i] ?? "";
            string one = raw.Trim();
            bool keep = one.Length > 0 && seen.Count < MaxUrisPerSet && seen.Add(one);
            if (keep && kept is null && string.Equals(one, raw, StringComparison.Ordinal)) continue;
            kept ??= Prefix(uris, i);
            if (keep) kept.Add(one);
        }

        if (kept is null) return uris;                 // already canonical — keep the caller's instance
        return kept.Count == 0 ? null : kept;

        static List<string> Prefix(IReadOnlyList<string> src, int count)
        {
            var list = new List<string>(src.Count);
            for (int j = 0; j < count; j++) list.Add(src[j]);
            return list;
        }
    }

    /// <summary>Per-field clamp (§C3.3 SetDisplayOption). Bools encode 0/1; every numeric field has a hard range.</summary>
    public static SidebarDisplayOptions WithField(SidebarDisplayOptions o, SidebarDisplayField f, int v) => f switch
    {
        SidebarDisplayField.Density => o with { Density = (SidebarDensity)Math.Clamp(v, 0, 2) },
        SidebarDisplayField.Presentation => o with { Presentation = (SidebarPresentation)Math.Clamp(v, 0, 1) },
        SidebarDisplayField.Artwork => o with { Artwork = v != 0 },
        SidebarDisplayField.Subtitles => o with { Subtitles = v != 0 },
        SidebarDisplayField.CountBadges => o with { CountBadges = v != 0 },
        SidebarDisplayField.CollapsedByDefault => o with { CollapsedByDefault = v != 0 },
        SidebarDisplayField.ShowInRail => o with { ShowInRail = v != 0 },
        SidebarDisplayField.MaxItems => o with { MaxItems = Math.Clamp(v, 0, MaxItemsPerSection) },
        SidebarDisplayField.GridColumns => o with { GridColumns = Math.Clamp(v, 2, 4) },
        SidebarDisplayField.InlineControls => o with { InlineControls = v != 0 },
        SidebarDisplayField.PlayButton => o with { PlayButton = v != 0 },
        SidebarDisplayField.RecentsSource => o with { Recents = (SidebarRecentsSource)Math.Clamp(v, 0, 1) },
        SidebarDisplayField.EmptyBehavior => o with { EmptyBehavior = (SidebarEmptyBehavior)Math.Clamp(v, 0, 3) },
        _ => o,
    };

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarCommandResult Rej(SidebarCustomLayout l, SidebarRejectReason r)
        => SidebarCommandResult.Reject(l, r);

    static bool TryLocate(SidebarCustomLayout l, string? id, out int top, out int child)
    {
        top = -1; child = -1;
        if (id is null or { Length: 0 }) return false;
        for (int i = 0; i < l.Sections.Count; i++)
        {
            var s = l.Sections[i];
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) { top = i; child = -1; return true; }
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
                if (string.Equals(kids[j].Id, id, StringComparison.Ordinal)) { top = i; child = j; return true; }
        }
        return false;
    }

    static SidebarSectionSpec Get(IReadOnlyList<SidebarSectionSpec> tops, int top, int child)
        => child < 0 ? tops[top] : tops[top].ChildList[child];

    static void Set(List<SidebarSectionSpec> tops, int top, int child, SidebarSectionSpec spec)
    {
        if (child < 0) { tops[top] = spec; return; }
        var p = tops[top];
        var kids = new List<SidebarSectionSpec>(p.ChildList);
        kids[child] = spec;
        tops[top] = p with { Children = kids };
    }

    static SidebarCustomLayout Replace(SidebarCustomLayout l, int top, int child, SidebarSectionSpec spec,
        IReadOnlySet<string>? pins)
    {
        var tops = new List<SidebarSectionSpec>(l.Sections);
        Set(tops, top, child, NormalizeSection(spec, pins));
        return l with { Sections = tops };
    }

    /// <summary>Invariants re-established every time a command TOUCHES a section: EntityEmbed keeps exactly one item
    /// (a hand-edited document may carry more), and a Pinned section's override entries whose key is no longer pinned
    /// are pruned — lazily, only here, so an accidental unpin+repin keeps the alias.</summary>
    static SidebarSectionSpec NormalizeSection(SidebarSectionSpec s, IReadOnlySet<string>? pins)
    {
        if (s.Kind == SidebarSectionKind.EntityEmbed && s.ItemList.Count > 1)
            s = s with { Items = new[] { s.ItemList[0] } };

        // LAYOUT V2: the include/exclude uri sets only mean something on a library-query kind. A query that ended up
        // somewhere else (hand-edited document, a kind a newer build changed) keeps its scalars but loses the uri sets.
        if (s.Query is { } q && (q.HasIncludeSet || q.HasExcludeSet) &&
            !SidebarSectionKinds.SupportsLibraryQuery(s.Kind))
            s = s with { Query = q with { IncludeUris = null, ExcludeUris = null } };

        if (s.Kind == SidebarSectionKind.Pinned && pins is not null && s.Items is { Count: > 0 } items)
        {
            List<SidebarItemSpec>? keep = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (pins.Contains(items[i].Key)) { keep?.Add(items[i]); continue; }
                if (keep is null)
                {
                    keep = new List<SidebarItemSpec>(items.Count);
                    for (int j = 0; j < i; j++) keep.Add(items[j]);
                }
            }
            if (keep is not null) s = s with { Items = keep.Count == 0 ? null : keep };
        }
        return s;
    }

    static SidebarItemSpec NormalizeItem(SidebarItemSpec i)
    {
        var label = Shorten(i.LabelOverride);
        var icon = i.IconOverride is { Length: > 0 } ? i.IconOverride : null;
        // LAYOUT V2: an action binding that arrived with the item goes through the same normalization SetItemAction uses
        // (trimmed ids, mode-consistent target key, an owned arguments element). A malformed one is dropped, not kept:
        // the item itself is still legal, and SetItemAction is how a real binding lands.
        var action = i.Action is { } b ? NormalizeBinding(b) : null;
        return string.Equals(label, i.LabelOverride, StringComparison.Ordinal) &&
               string.Equals(icon, i.IconOverride, StringComparison.Ordinal) && action == i.Action
            ? i
            : i with { LabelOverride = label, IconOverride = icon, Action = action };
    }

    /// <summary>LAYOUT V2 — trim the two ids and take ownership of the config element. Nothing about the config's SHAPE is
    /// validated here: only the contributing source knows its schema, and an unknown member must round-trip.</summary>
    static SidebarExtensionRef NormalizeRef(SidebarExtensionRef r)
        => new(r.ExtensionId.Trim(), r.ContributionId.Trim(), r.SchemaVersion, SidebarJson.Own(r.Config));

    /// <summary>LAYOUT V2 — trim the ids, clear a target key the mode cannot use, own the arguments element. Null for a
    /// binding that cannot address an action (blank provider or action id), which the caller turns into a NoChange.</summary>
    static SidebarActionBinding? NormalizeBinding(SidebarActionBinding b)
    {
        string provider = (b.ProviderId ?? "").Trim();
        string action = (b.ActionId ?? "").Trim();
        if (provider.Length == 0 || action.Length == 0) return null;

        string? key = b.TargetKey is { } k && k.Trim() is { Length: > 0 } trimmed ? trimmed : null;
        if (!b.RequiresTargetKey) key = null;      // a leftover key from a previous mode is noise, never data

        return new SidebarActionBinding(provider, action, b.TargetMode, key, SidebarJson.Own(b.Arguments));
    }

    static bool ItemsEquivalent(SidebarItemSpec a, SidebarItemSpec b)
        => a.Target == b.Target && string.Equals(a.Key, b.Key, StringComparison.Ordinal) &&
           a.EntityKind == b.EntityKind &&
           string.Equals(a.LabelOverride, b.LabelOverride, StringComparison.Ordinal) &&
           string.Equals(a.IconOverride, b.IconOverride, StringComparison.Ordinal) &&
           a.Hidden == b.Hidden && a.Action == b.Action;

    static int IndexOfItem(SidebarSectionSpec s, string itemId) => IndexOfItem(s.ItemList, itemId);

    static int IndexOfItem(IReadOnlyList<SidebarItemSpec> items, string itemId)
    {
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].Id, itemId, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Trim, normalize "" to null, truncate to 60 chars (truncated, never rejected).</summary>
    static string? Shorten(string? s)
    {
        if (s is null) return null;
        var t = s.Trim();
        if (t.Length == 0) return null;
        return t.Length <= MaxTitleLength ? t : t[..MaxTitleLength];
    }

    static bool IsAncestorOf(SidebarCustomLayout l, string sectionId, string candidateChildId)
    {
        var s = l.Find(sectionId);
        if (s is null) return false;
        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++)
            if (string.Equals(kids[i].Id, candidateChildId, StringComparison.Ordinal)) return true;
        return false;
    }

    static bool SameArrangement(IReadOnlyList<SidebarSectionSpec> a, IReadOnlyList<SidebarSectionSpec> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)) return false;
            var ka = a[i].ChildList; var kb = b[i].ChildList;
            if (ka.Count != kb.Count) return false;
            for (int j = 0; j < ka.Count; j++)
                if (!string.Equals(ka[j].Id, kb[j].Id, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // ── Ids ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    static HashSet<string> CollectIds(SidebarCustomLayout l)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < l.Sections.Count; i++) CollectIds(l.Sections[i], set);
        // The top-bar band shares the document's item-id space, and the built-in default's id is RESERVED even when the
        // band was emptied — otherwise a freshly minted id could collide with the Home tile a later add re-materializes.
        var band = l.EffectiveTopBar;
        for (int i = 0; i < band.Count; i++) set.Add(band[i].Id);
        set.Add(SidebarIds.TopBarHomeItem);
        return set;
    }

    static void CollectIds(SidebarSectionSpec s, HashSet<string> set)
    {
        set.Add(s.Id);
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++) set.Add(items[i].Id);
        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++) CollectIds(kids[i], set);
    }

    static string FreshSectionId(SidebarCustomLayout l)
    {
        var used = CollectIds(l);
        string id;
        do { id = SidebarIds.NewSection(); } while (used.Contains(id));
        return id;
    }

    /// <summary>Keeps <paramref name="incoming"/> when it is well-formed and unique; regenerates on a collision
    /// (or when the caller left it blank).</summary>
    static string FreshItemId(SidebarCustomLayout l, string? incoming = null)
    {
        var used = CollectIds(l);
        if (incoming is { Length: > 0 } && !used.Contains(incoming)) return incoming;
        string id;
        do { id = SidebarIds.NewItem(); } while (used.Contains(id));
        return id;
    }

    static SidebarSectionSpec CloneWithFreshIds(SidebarSectionSpec s, HashSet<string> used)
    {
        IReadOnlyList<SidebarItemSpec>? items = null;
        if (s.Items is { Count: > 0 })
        {
            var copy = new SidebarItemSpec[s.Items.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = s.Items[i] with { Id = Mint(used, section: false) };
            items = copy;
        }

        IReadOnlyList<SidebarSectionSpec>? kids = null;
        if (s.Children is { Count: > 0 })
        {
            var copy = new SidebarSectionSpec[s.Children.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = CloneWithFreshIds(s.Children[i], used);
            kids = copy;
        }

        return s with { Id = Mint(used, section: true), Items = items, Children = kids };
    }

    static string Mint(HashSet<string> used, bool section)
    {
        string id;
        do { id = section ? SidebarIds.NewSection() : SidebarIds.NewItem(); } while (!used.Add(id));
        return id;
    }
}
