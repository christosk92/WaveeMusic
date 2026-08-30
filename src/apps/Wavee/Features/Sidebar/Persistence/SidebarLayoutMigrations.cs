namespace Wavee;

// ── The sidebar-layout document's version ladder (F.3.2.3 step 2) ─────────────────────────────────────────────────────
// v1 → v2 (LAYOUT V2, the extension-ready shape) is an IDENTITY step: v2 only ADDS optional members (a section's
// `extension` object, an item's `action` object, a query's `includeUris`/`excludeUris`) and one new kind/target string, so
// every v1 document is already a valid v2 document. The arm therefore rewrites nothing — it only stamps the version, and
// the file on disk keeps saying "version": 1 until the next ordinary commit. Existing layouts look and behave identically.
//
// Contract for every future arm:
//   • It runs IN MEMORY on a freshly deserialized document, BEFORE anything reads it. The file on disk is untouched until
//     the next ordinary commit (which then writes the upgraded shape) — so an upgrade never destroys a document the user
//     might still want to open with the older build.
//   • It must be TOTAL: never throw, never return null. An unmappable member degrades to the new shape's default.
//   • It must PRESERVE unknown members (the [JsonExtensionData] carry) — never rebuild a DTO from scratch when mutating
//     it in place will do.
//   • It steps ONE version at a time (the while loop below), so v0→v3 is v1→v2 then v2→v3, each arm tested in isolation.
public static class SidebarLayoutMigrations
{
    /// <summary>Bring a loaded document up to <see cref="SidebarLayoutStore.CurrentVersion"/>. Never throws; the returned
    /// instance is the same object (mutated in place) so the extension-data carry survives.</summary>
    public static SidebarLayoutDocDto Upgrade(SidebarLayoutDocDto doc)
    {
        if (doc is null) return new SidebarLayoutDocDto { Version = SidebarLayoutStore.CurrentVersion };

        int guard = 0;
        while (doc.Version < SidebarLayoutStore.CurrentVersion && guard++ < 32)
        {
            switch (doc.Version)
            {
                case 1: MigrateV1ToV2(doc); doc.Version = 2; break;
                // case 2: MigrateV2ToV3(doc); doc.Version = 3; break;
                default:
                    // No arm for this version: stamp the current version rather than spinning. Reaching here means the
                    // ladder has a hole, which is a coding error, not a user-data problem — the document is still usable.
                    doc.Version = SidebarLayoutStore.CurrentVersion;
                    break;
            }
        }

        if (doc.Version > SidebarLayoutStore.CurrentVersion) doc.Version = SidebarLayoutStore.CurrentVersion;
        return doc;
    }

    /// <summary>v1 → v2: IDENTITY. Every v2 addition is an OPTIONAL member (section <c>extension</c>, item
    /// <c>action</c>, query <c>includeUris</c>/<c>excludeUris</c>) plus two new enum STRINGS ("extension", "action"), so a
    /// v1 document is already well-formed v2 and its absent members read as the model defaults. Deliberately a named
    /// no-op rather than a missing arm: the ladder stays explicit, the caller keeps the SAME instance (so the
    /// <c>[JsonExtensionData]</c> carry lives), and a real v2 → v3 arm has an obvious shape to copy.</summary>
    static void MigrateV1ToV2(SidebarLayoutDocDto doc)
    {
        _ = doc;
    }
}
