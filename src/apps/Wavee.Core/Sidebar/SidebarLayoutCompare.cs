namespace Wavee.Core.Sidebar;

// Structural comparison for the layout document. Needed because the payload records carry IReadOnlyList<T> members, and
// a positional record's synthesized Equals compares those by REFERENCE — so two documents that round-tripped through
// JSON, or two builds of the same template, are never `==` even when every field matches.
//
// Two questions, two answers:
//   * Equal            — "is this the same document?" (the JSON round-trip assertion, the dirty check)
//   * EqualIgnoringIds — "is this the same SHAPE?" (skip the template-apply confirmation when the user has no edits to
//                        lose; prove Build() is deterministic modulo freshly minted ids)
// FirstDifference is the diagnostic sibling: a human-readable path to the first mismatch, for test failure messages.

public static class SidebarLayoutCompare
{
    public static bool Equal(SidebarCustomLayout? a, SidebarCustomLayout? b) => Compare(a, b, ids: true) is null;

    public static bool EqualIgnoringIds(SidebarCustomLayout? a, SidebarCustomLayout? b)
        => Compare(a, b, ids: false) is null;

    /// <summary>Template-pristine comparison: template identity + section shape, modulo generated ids. The global top
    /// bar is intentionally excluded because templates preserve it and top-bar-only edits are not sections at risk.</summary>
    public static bool EqualTemplateSectionsIgnoringIds(SidebarCustomLayout? a, SidebarCustomLayout? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal)
               && CompareSections(a.Sections, b.Sections, ids: false, "sections") is null;
    }

    /// <summary>A dotted path to the first structural difference ("sections[2].display.maxItems"), or null when the two
    /// documents match. Intended for assertion messages and the devtools inspector — never for control flow.</summary>
    public static string? FirstDifference(SidebarCustomLayout? a, SidebarCustomLayout? b, bool ignoreIds = false)
        => Compare(a, b, ids: !ignoreIds);

    public static bool Equal(SidebarSectionSpec? a, SidebarSectionSpec? b)
        => CompareSection(a, b, ids: true, path: "section") is null;

    public static bool EqualIgnoringIds(SidebarSectionSpec? a, SidebarSectionSpec? b)
        => CompareSection(a, b, ids: false, path: "section") is null;

    static string? Compare(SidebarCustomLayout? a, SidebarCustomLayout? b, bool ids)
    {
        if (ReferenceEquals(a, b)) return null;
        if (a is null || b is null) return "layout";
        if (!string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal)) return "templateId";
        var band = CompareTopBar(a.TopBar, b.TopBar, ids);
        if (band is not null) return band;
        return CompareSections(a.Sections, b.Sections, ids, "sections");
    }

    /// <summary>The shell top-bar band. null (never customized) and [] (emptied on purpose) are DIFFERENT documents — they
    /// render differently (built-in Home vs nothing), so the diff must not collapse them.</summary>
    static string? CompareTopBar(IReadOnlyList<SidebarItemSpec>? a, IReadOnlyList<SidebarItemSpec>? b, bool ids)
    {
        if (a is null || b is null) return a is null && b is null ? null : "topBar";
        if (a.Count != b.Count) return "topBar.count";
        for (int i = 0; i < a.Count; i++)
        {
            var diff = CompareItem(a[i], b[i], ids, "topBar[" + i.ToString(Culture) + "]");
            if (diff is not null) return diff;
        }
        return null;
    }

    static string? CompareSections(IReadOnlyList<SidebarSectionSpec> a, IReadOnlyList<SidebarSectionSpec> b,
        bool ids, string path)
    {
        if (a.Count != b.Count) return path + ".count";
        for (int i = 0; i < a.Count; i++)
        {
            var diff = CompareSection(a[i], b[i], ids, path + "[" + i.ToString(Culture) + "]");
            if (diff is not null) return diff;
        }
        return null;
    }

    static string? CompareSection(SidebarSectionSpec? a, SidebarSectionSpec? b, bool ids, string path)
    {
        if (ReferenceEquals(a, b)) return null;
        if (a is null || b is null) return path;
        if (ids && !string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return path + ".id";
        if (a.Kind != b.Kind) return path + ".kind";
        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal)) return path + ".title";
        if (!string.Equals(a.TitleLocKey, b.TitleLocKey, StringComparison.Ordinal)) return path + ".titleLocKey";
        if (a.Hidden != b.Hidden) return path + ".hidden";
        if (a.Collapsed != b.Collapsed) return path + ".collapsed";
        if (a.Opts != b.Opts) return path + ".display";                       // record equality: all scalars
        // SidebarEntityQuery and SidebarExtensionRef declare their OWN Equals (uri sets element-wise, the config
        // JsonElement by raw text), so both of these are real CONTENT comparisons — not the reference check a
        // synthesized record equality would do for an IReadOnlyList/JsonElement member.
        if (SidebarSectionKinds.EffectiveQuery(a.Kind, a.Query) !=
            SidebarSectionKinds.EffectiveQuery(b.Kind, b.Query))
            return path + ".query";
        if (a.Extension != b.Extension) return path + ".extension";

        var ai = a.ItemList; var bi = b.ItemList;
        if (ai.Count != bi.Count) return path + ".items.count";
        for (int i = 0; i < ai.Count; i++)
        {
            var diff = CompareItem(ai[i], bi[i], ids, path + ".items[" + i.ToString(Culture) + "]");
            if (diff is not null) return diff;
        }

        return CompareSections(a.ChildList, b.ChildList, ids, path + ".children");
    }

    static string? CompareItem(SidebarItemSpec a, SidebarItemSpec b, bool ids, string path)
    {
        if (ids && !string.Equals(a.Id, b.Id, StringComparison.Ordinal)) return path + ".id";
        if (a.Target != b.Target) return path + ".target";
        if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)) return path + ".key";
        if (a.EntityKind != b.EntityKind) return path + ".entityKind";
        if (!string.Equals(a.LabelOverride, b.LabelOverride, StringComparison.Ordinal)) return path + ".label";
        if (!string.Equals(a.IconOverride, b.IconOverride, StringComparison.Ordinal)) return path + ".icon";
        if (!string.Equals(a.FallbackTitle, b.FallbackTitle, StringComparison.Ordinal)) return path + ".fallbackTitle";
        if (!string.Equals(a.FallbackImageUrl, b.FallbackImageUrl, StringComparison.Ordinal))
            return path + ".fallbackImage";
        if (a.Hidden != b.Hidden) return path + ".hidden";
        if (a.Action != b.Action) return path + ".action";   // SidebarActionBinding declares content equality too
        return null;
    }

    /// <summary>Every section and item id in the document, in document order — the "ids are unique" assertion's input
    /// and the customizer's "which section did that command touch?" trace.</summary>
    public static List<string> AllIds(SidebarCustomLayout layout)
    {
        var ids = new List<string>();
        for (int i = 0; i < layout.Sections.Count; i++) Collect(layout.Sections[i], ids);
        return ids;
    }

    static void Collect(SidebarSectionSpec s, List<string> ids)
    {
        ids.Add(s.Id);
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++) ids.Add(items[i].Id);
        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++) Collect(kids[i], ids);
    }

    static readonly System.Globalization.CultureInfo Culture = System.Globalization.CultureInfo.InvariantCulture;
}
