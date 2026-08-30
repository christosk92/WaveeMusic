namespace Wavee.Core.Sidebar;

// The five seed layouts. A template is a FUNCTION, not a stored document: Build mints fresh ids per call, so applying
// the same template twice yields two documents that differ only by id (SidebarLayoutCompare.EqualIgnoringIds proves it).
//
// Titles are authored as TitleLocKey (never a literal), so a template-seeded sidebar follows the UI culture until the
// user renames a section — at which point RenameSection sets Title and clears TitleLocKey.

public static class SidebarTemplates
{
    public const string Curated         = "curated";
    public const string ClassicInspired = "classic";
    public const string V3Inspired      = "library";
    public const string Minimal         = "minimal";
    public const string Blank           = "blank";

    /// <summary>Palette order — this IS the order the customizer's template list renders.</summary>
    public static readonly string[] All = [Curated, ClassicInspired, V3Inspired, Minimal, Blank];

    public static bool IsKnown(string? templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return false;
        for (int i = 0; i < All.Length; i++)
            if (string.Equals(All[i], templateId, StringComparison.Ordinal)) return true;
        return false;
    }

    public static string NameLocKey(string? templateId) => "sidebar.template." + Normalize(templateId);
    public static string DescriptionLocKey(string? templateId) => "sidebar.template." + Normalize(templateId) + "Sub";

    static string Normalize(string? templateId) => IsKnown(templateId) ? templateId! : Curated;

    /// <summary>Builds a fresh layout. Ids are newly generated per call (never shared between two builds).
    /// An unknown id yields Wavee Curated (and stamps <c>TemplateId = "curated"</c>), which is also what
    /// <c>ResetLayout</c> leans on to heal a hand-edited document.</summary>
    public static SidebarCustomLayout Build(string? templateId) => Normalize(templateId) switch
    {
        ClassicInspired => BuildClassicInspired(),
        V3Inspired => BuildV3Inspired(),
        Minimal => BuildMinimal(),
        Blank => new SidebarCustomLayout(Blank, Array.Empty<SidebarSectionSpec>()),
        _ => BuildCurated(),
    };

    // ── C2.1 Wavee Curated — the fresh-install default ───────────────────────────────────────────────────────────────
    // Note the ordering divergence from today's sidebar: Liked Songs FIRST, because Curated leads with the destination
    // people open most. The shortcut order is user-reorderable, so this is a default, not a constraint.
    static SidebarCustomLayout BuildCurated() => new(Curated,
    [
        Section(SidebarSectionKind.Pinned, "sidebar.pinned", SidebarDisplayOptions.Entities),
        Divider(),
        // The extended catalog's headline ask: Jump back in ships as RECENTLY PLAYED (the local play log), top 4.
        Section(SidebarSectionKind.JumpBackIn, "sidebar.section.recentlyPlayed",
            SidebarDisplayOptions.Entities with
            {
                Presentation = SidebarPresentation.Grid,
                GridColumns = 2,
                Artwork = true,
                Subtitles = false,
                MaxItems = 4,
                ShowInRail = false,
                Recents = SidebarRecentsSource.Played,
            }),
        Divider(),
        // Comfortable + Subtitles:false = the 44-DIP glyph row — the SAME number Classic's locked document uses, so the
        // Curated default template is pixel-identical to Classic's shortcuts (R3 residual close-out).
        Section(SidebarSectionKind.CollectionShortcuts, "sidebar.yourLibrary",
            SidebarDisplayOptions.Shortcuts with { Density = SidebarDensity.Comfortable },
        [
            Route("liked", "Heart"),
            Route("albums", "Album"),
            Route("artists", "Contact"),
            Route("podcasts", "RadioTower"),
            Route("local", "Folder"),
        ]),
        Divider(),
        Section(SidebarSectionKind.PlaylistTree, "sidebar.playlists", SidebarDisplayOptions.Entities),
    ]);

    // ── C2.2 Classic-inspired — today's WaveeSidebar IA inside the Custom renderer ────────────────────────────────────
    static SidebarCustomLayout BuildClassicInspired() => new(ClassicInspired,
    [
        // Classic's rail has no pin tiles.
        Section(SidebarSectionKind.Pinned, "sidebar.pinned",
            SidebarDisplayOptions.Entities with { ShowInRail = false }),
        Section(SidebarSectionKind.CollectionShortcuts, "sidebar.yourLibrary",
            SidebarDisplayOptions.Shortcuts with { Density = SidebarDensity.Comfortable },
        [
            Route("albums", "Album"),
            Route("artists", "Contact"),
            Route("liked", "Heart"),
            Route("podcasts", "RadioTower"),
            Route("local", "Folder"),
        ]),
        Divider(),
        // Subtitles = the song-count caption.
        Section(SidebarSectionKind.PlaylistTree, "sidebar.playlists", SidebarDisplayOptions.Entities),
        Divider(),
        // Mirrors today's flat DevToolsRow — deliberately header-less. `Links`, not `Shortcuts`: a StaticLinks section
        // forbids CountBadges (defect 8), so seeding it from the CollectionShortcuts preset carried a dead flag.
        Section(SidebarSectionKind.StaticLinks, null, SidebarDisplayOptions.Links,
        [
            Route("api-console", "Code"),
        ]),
    ]);

    // ── C2.3 Library-inspired — the V3 unified list embedded as a section ─────────────────────────────────────────────
    static SidebarCustomLayout BuildV3Inspired() => new(V3Inspired,
    [
        Section(SidebarSectionKind.Pinned, "sidebar.pinned", SidebarDisplayOptions.Entities),
        Divider(),
        // InlineControls = the self-contained, fully customizable "Your Library" component (chips + sort/view flyout
        // pinned under the header, editing THIS section's persisted spec).
        Section(SidebarSectionKind.EntityList, "sidebar.yourLibrary",
            SidebarDisplayOptions.Entities with { Subtitles = true, InlineControls = true },
            query: SidebarEntityQuery.Default),
        Divider(),
        // `Links`, not `Shortcuts` — see the ClassicInspired arm above (defect 8).
        Section(SidebarSectionKind.StaticLinks, null, SidebarDisplayOptions.Links,
        [
            Route("home", "Home"),
            Route("search", "Search"),
        ]),
    ]);

    // ── C2.4 Minimal ────────────────────────────────────────────────────────────────────────────────────────────────
    static SidebarCustomLayout BuildMinimal() => new(Minimal,
    [
        Section(SidebarSectionKind.CollectionShortcuts, "sidebar.yourLibrary",
            SidebarDisplayOptions.Shortcuts with { CountBadges = false, Density = SidebarDensity.Compact },
        [
            Route("liked", "Heart"),
            Route("albums", "Album"),
            Route("artists", "Contact"),
        ]),
        Divider(),
        Section(SidebarSectionKind.PlaylistTree, "sidebar.playlists",
            SidebarDisplayOptions.Entities with
            {
                Artwork = false, Subtitles = false, Density = SidebarDensity.Compact,
            }),
    ]);

    // ── Builders ────────────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarSectionSpec Section(SidebarSectionKind kind, string? titleLocKey, SidebarDisplayOptions display,
        IReadOnlyList<SidebarItemSpec>? items = null, SidebarEntityQuery? query = null)
        => new(SidebarIds.NewSection(), kind,
            Title: null,
            TitleLocKey: titleLocKey,
            Hidden: false,
            Collapsed: display.CollapsedByDefault,
            Display: display,
            Items: items,
            Query: query,
            Children: null);

    static SidebarSectionSpec Divider()
        => new(SidebarIds.NewSection(), SidebarSectionKind.Divider);

    static SidebarItemSpec Route(string routeKey, string iconName)
        => new(SidebarIds.NewItem(), SidebarItemTarget.Route, routeKey, IconOverride: iconName);
}
