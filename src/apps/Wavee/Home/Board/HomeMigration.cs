// ── Home/Board/HomeMigration.cs: v2 home-layout.json and the sidebar's pins into the v3 document (design 1.2.4) ─────
//
// Runs once per account, when no home.acct-<hash>.json exists:
// 1. start from the default;
// 2. read home-layout.json v2 with the existing reader; its ORDER is ignored (D12); a hidden daylist or recents removes
//    that widget; every other hidden zone name goes into LegacyHidden;
// 3. on the first answer ResolveLegacy hides the FAMILY of each section whose old role maps to a hidden zone, then
//    clears LegacyHidden;
// 4. up to 8 sidebar pins, folders left out, are copied into Pinned; SeededFromSidebar is set. The two lists are
//    independent afterwards.
// The v2 file is left in place and never read again.

using System.Collections.Immutable;
using Wavee.HomeUi;

namespace Wavee;

public static class HomeMigration
{
    public const int MaxSeedPins = 8;

    /// <summary>The first document of an account. <paramref name="folder"/> holds home-layout.json v2 (when any);
    /// <paramref name="sidebarPins"/> are the sidebar's pins in order, folders already left out.</summary>
    public static HomeDoc Run(string folder, IReadOnlyList<PinSource> sidebarPins)
    {
        var doc = HomeDocs.Default();
        var legacy = ReadV2Hidden(Path.Combine(folder, "home-layout.json"));
        var keep = ImmutableArray.CreateBuilder<string>();
        foreach (var zone in legacy)
        {
            switch (zone)
            {
                case LayoutZone.Daylist: doc = WithoutWidget(doc, "daylist"); break;
                case LayoutZone.Recents: doc = WithoutWidget(doc, "recents"); break;
                default: keep.Add(LayoutZones.KindName(zone)); break;
            }
        }
        doc = doc with { LegacyHidden = keep.ToImmutable() };

        if (sidebarPins.Count > 0)
        {
            int pg = doc.GroupIndex(HomeDocs.Pinned);
            if (pg >= 0)
            {
                var items = doc.Groups[pg].Items;
                int at = 0;
                for (int i = 0; i < items.Length; i++) if (items[i].Source is FillSource) { at = i; break; }
                var pins = ImmutableArray.CreateBuilder<HomeItem>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < sidebarPins.Count && pins.Count < MaxSeedPins; i++)
                    if (seen.Add(sidebarPins[i].PinId))
                        pins.Add(new HomeItem(HomeDocs.NewItemId(), sidebarPins[i], HomeSize.Auto, ImmutableSortedDictionary<string, string>.Empty));
                doc = doc with { Groups = doc.Groups.SetItem(pg, doc.Groups[pg] with { Items = items.InsertRange(at, pins.ToImmutable()) }) };
            }
            doc = doc with { SeededFromSidebar = true };
        }
        return doc;
    }

    /// <summary>The hidden zones of a v2 file (none when it is missing or unreadable).</summary>
    static List<LayoutZone> ReadV2Hidden(string path)
    {
        var hidden = new List<LayoutZone>();
        if (!File.Exists(path)) return hidden;
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return hidden; }
        var read = LayoutWire.Read(json);
        if (read.Fault != LayoutReadFault.None) return hidden;
        foreach (var z in LayoutZones.DefaultOrder) if (read.Doc.IsHidden(z)) hidden.Add(z);
        return hidden;
    }

    static HomeDoc WithoutWidget(HomeDoc doc, string widget)
    {
        for (int g = 0; g < doc.Groups.Length; g++)
        {
            var items = doc.Groups[g].Items;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Source is WidgetSource w && w.Widget == widget)
                    return doc with { Groups = doc.Groups.SetItem(g, doc.Groups[g] with { Items = items.RemoveAt(i) }) };
        }
        return doc;
    }

    /// <summary>The v2 zone a section's old role filled (null: a role no v2 zone could hide).</summary>
    public static string? LegacyZoneOf(SectionRole role) => role switch
    {
        SectionRole.Personal => "madeForYou",
        SectionRole.WideEditorial => "newMusic",
        SectionRole.Releases => "releases",
        SectionRole.Cluster => "becauseYouLike",
        SectionRole.Generic => "jumpBackIn",
        SectionRole.Radio => "radio",
        SectionRole.Browse => "browse",
        _ => null,
    };

    /// <summary>Step 3: hide the family of every section whose old role maps to a hidden v2 zone, then clear the list.</summary>
    public static HomeDoc ResolveLegacy(HomeDoc doc, IReadOnlyList<SectionInput> sections, long nowMs)
    {
        if (doc.LegacyHidden.IsDefaultOrEmpty) return doc;
        var hidden = doc.Hidden.ToBuilder();
        foreach (var s in sections)
        {
            var role = SectionRoles.Of(s, nowMs, allFacet: true);
            if (LegacyZoneOf(role) is not { } zone || !doc.LegacyHidden.Contains(zone)) continue;
            var family = HomeCommands.FamilyRef(HomeProjection.RefOf(s).Family);
            if (!hidden.Contains(family)) hidden.Add(family);
        }
        return doc with { Hidden = hidden.ToImmutable(), LegacyHidden = [] };
    }
}
