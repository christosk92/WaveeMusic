// ── Shell/Sidebar.Modes.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the design vocabulary, the chooser gate, Classic's locked document, the nav band, and Library V3's synthesized one
//
// Role: CORE
// Owner: J
// Wave: 4
// Budget: named partial of Sidebar.cs (ch 26 §9.4's ~5,000 came in at ~7,500 once the pure rules were counted; this
//         is the split, declared by name rather than left as an overrun — see the report)
// Spec: ch 25 §8 (§ DESIGN, § DOCUMENTS, § V3) + ch 25 §0.13
//
// Three modes, three KINDS of document, one renderer. That is the whole architecture, and this file is where the two
// documents the user does not own are made:
//
//   Classic     `SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen)` — rebuilt from code, never
//               persisted, read-only. `ClassicDocumentCache` exists because a freshly-minted document per render
//               defeats the publish stage's reference check and turns every publish into a whole-window re-skin.
//   Library V3  `LibraryV3Document.Build(in LibraryV3DocState)` — ephemeral, synthesized from filter / qualifier /
//               sort / view / search / drill. Its chrome is FIXED (nav band 30 → header 44 → toolbar 36 → chip rail
//               40 → rule → breadcrumb 32) and sits above the scroll surface: a search, a filter or a drill level
//               never moves, hides or reorders Home.
//   Curated     the user's own document — `Sidebar.Doc.cs`, persisted, and the only one the customizer edits.
//
// `SidebarDesignInfo` is the single owner of the per-design width tiers (Classic 240/280/320 · Curated 280/320/360 ·
// V3 300/340/380); the breakpoints, the hysteresis and the [180,460] clamp are `SidebarPaneBounds`' and are identical
// for all three. A design switch is a genuine Key REMOUNT, never a re-render with a different flag.

using System.Globalization;
using FluentGpu.Foundation;

namespace Wavee;

// ── DESIGN, BUILT-IN DOCUMENTS, THE NAV BAND, PINS AND THE EDIT PLAN ─────────────────────────────────────────────────
//
// The sidebar's engine-free vocabulary, ported name-for-name from 0.2.9: the three-design enum and its per-design
// slug/mount-key/width-tier table plus pane snapshot/restore rules; the one-time chooser gate; Classic's entire
// locked information architecture as a built-in document; the nav band's row-shaping projection; the pin identity
// scheme (the pin id IS the nav route key), its Spotify ylpin sync mapping and its menu-row decision; and the
// customizer's edit session as a value plus the pure rules over it (drag translation, card counts, fold key).
// Every type below is engine-free by construction so Wavee.Tests can drive the real decision, not a copy of it.

// ── 1. the design vocabulary: enum, per-design table, pane snapshot/restore ─────────────────────────────────────────

/// <summary>The three user-selectable left-sidebar designs. Values are PERSISTED (<c>Platform.Keys.SidebarDesign</c>)
/// — append only, never reorder or reuse. Classic is 0 so an install that never wrote the key stays Classic.</summary>
public enum SidebarDesign : byte { Classic = 0, LibraryV3 = 1, Curated = 2 }

/// <summary>Which of Classic's three fixed sections a toggle refers to (its expansion is per-design state, so it
/// lives in settings rather than in the component).</summary>
public enum ClassicSection : byte { Pinned = 0, Library = 1, Playlists = 2 }

// The Library-V3 view state, persisted as ints (no enum arm in the settings store).
public enum SidebarV3Filter : byte { All = 0, Playlists = 1, Podcasts = 2, Albums = 3, Artists = 4 }
public enum SidebarV3Qualifier : byte { Any = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }
public enum SidebarV3Sort : byte { Recents = 0, RecentlyAdded = 1, Alphabetical = 2, Creator = 3, Custom = 4 }
public enum SidebarV3View : byte { CompactList = 0, List = 1, CompactGrid = 2, Grid = 3 }

/// <summary>The per-design static table: key slug, mount key, responsive width tiers, and the int coercion the
/// persisted setting round-trips through.</summary>
public static class SidebarDesignInfo
{
    public const int Count = 3;

    /// <summary>Render/settings order — the order the design picker and the layout menu list the designs.</summary>
    public static readonly SidebarDesign[] All =
        [SidebarDesign.Classic, SidebarDesign.LibraryV3, SidebarDesign.Curated];

    /// <summary>The per-design settings-key slug (<c>"classic"</c> | <c>"v3"</c> | <c>"curated"</c>). PERSISTED —
    /// never change these. Single source of truth for every <c>sidebar.{slug}.*</c> key in the app.</summary>
    public static string Slug(SidebarDesign d) => d switch
    {
        SidebarDesign.LibraryV3 => "v3",
        SidebarDesign.Curated => "curated",
        _ => "classic",
    };

    /// <summary>The mode component's mount <c>Key</c>. Stable across releases: it is what makes a design switch an
    /// unconditional REMOUNT rather than a reuse.</summary>
    public static string MountKey(SidebarDesign d) => d switch
    {
        SidebarDesign.LibraryV3 => "sidebar.v3",
        SidebarDesign.Curated => "sidebar.curated",
        _ => "sidebar.classic",
    };

    /// <summary>The responsive nav-pane width tiers per design. All three sets sit INSIDE the global
    /// <c>SidebarPaneBounds.NavPaneMinW/MaxW</c> clamp (180/460, issue #84) — the grip and the tier ladder still
    /// clamp through that one owner, and no per-design literal pair may be reintroduced anywhere else.</summary>
    public static (float Narrow, float Mid, float Wide) Tiers(SidebarDesign d) => d switch
    {
        SidebarDesign.LibraryV3 => (300f, 340f, 380f),
        SidebarDesign.Curated => (280f, 320f, 360f),
        _ => (SidebarPaneBounds.NavPaneNarrowW,
              SidebarPaneBounds.NavPaneMidW,
              SidebarPaneBounds.NavPaneWideW),   // 240 / 280 / 320 — Classic's existing ladder, unchanged
    };

    /// <summary>Persisted-int → design, tolerating a hand-edited or future value (falls back to Classic, never
    /// throws).</summary>
    public static SidebarDesign FromInt(int v) => (uint)v < Count ? (SidebarDesign)v : SidebarDesign.Classic;
}

/// <summary>One design's remembered pane triple. Immutable so a snapshot can be handed around without aliasing the
/// live state it came from.</summary>
public readonly record struct SidebarPaneSnapshot(float Width, bool Collapsed, bool WidthUserSet);

/// <summary>
/// The PURE per-design pane snapshot/restore rules behind a design switch. While a design's <c>WidthUserSet</c> is
/// false its width follows THAT design's tier ladder; the first committed seam drag in that design latches the flag
/// forever, for that design only. Switching designs never latches, and never clears, another design's flag.
/// Collapsing is not a width choice and never touches the flag.
/// </summary>
public static class SidebarPaneState
{
    /// <summary>The design's own width key, seeded with ITS narrow tier as the settings default (so an absent key
    /// answers with a sane per-design floor rather than a foreign design's number).</summary>
    static SettingKey<float> WidthKey(SidebarDesign design)
        => Platform.Keys.SidebarWidth(SidebarDesignInfo.Slug(design), SidebarDesignInfo.Tiers(design).Narrow);

    /// <summary>Write the outgoing design's live pane state into its own key bag (step 1 of a switch).</summary>
    public static void Snapshot(IAppSettings settings, SidebarDesign design, in SidebarPaneSnapshot state)
    {
        string slug = SidebarDesignInfo.Slug(design);
        settings.Set(WidthKey(design), state.Width);
        settings.Set(Platform.Keys.SidebarCollapsed(slug), state.Collapsed);
        settings.Set(Platform.Keys.SidebarWidthUserSet(slug), state.WidthUserSet);
    }

    /// <summary>Read the incoming design's remembered pane state (step 2 of a switch). A design whose width was
    /// never pinned by a drag gets its OWN tier default at the live viewport — which is what makes the tier ladder
    /// re-seed on a switch instead of carrying the outgoing design's width across.</summary>
    public static SidebarPaneSnapshot Restore(IAppSettings settings, SidebarDesign design, float viewportWidth)
    {
        string slug = SidebarDesignInfo.Slug(design);
        bool userSet = settings.Get(Platform.Keys.SidebarWidthUserSet(slug));
        float width = userSet
            ? Math.Clamp(settings.Get(WidthKey(design)), SidebarPaneBounds.NavPaneMinW, SidebarPaneBounds.NavPaneMaxW)
            : TierDefault(design, viewportWidth);
        return new SidebarPaneSnapshot(width, settings.Get(Platform.Keys.SidebarCollapsed(slug)), userSet);
    }

    /// <summary>The width a design gets when it has never been pinned: its own tier ladder evaluated at
    /// <paramref name="viewportWidth"/> (a zero/unknown viewport takes the narrow tier — the pre-measure seed).</summary>
    public static float TierDefault(SidebarDesign design, float viewportWidth)
        => SidebarPaneBounds.InitialNavPaneDefaultForViewport(viewportWidth, SidebarDesignInfo.Tiers(design));

    /// <summary>"Reset width" (the layout menu / customizer affordance): clear the design's user-set latch and hand
    /// back its tier default, so the responsive ladder owns the width again.</summary>
    public static SidebarPaneSnapshot ResetWidth(IAppSettings settings, SidebarDesign design, float viewportWidth)
    {
        string slug = SidebarDesignInfo.Slug(design);
        settings.Set(Platform.Keys.SidebarWidthUserSet(slug), false);
        float width = TierDefault(design, viewportWidth);
        settings.Set(WidthKey(design), width);
        return new SidebarPaneSnapshot(width, settings.Get(Platform.Keys.SidebarCollapsed(slug)), false);
    }

    /// <summary>The drag-commit edge: clamp, persist, and LATCH the width as this design's user choice.</summary>
    public static float CommitWidth(IAppSettings settings, SidebarDesign design, float width)
    {
        float clamped = Math.Clamp(width, SidebarPaneBounds.NavPaneMinW, SidebarPaneBounds.NavPaneMaxW);
        settings.Set(WidthKey(design), clamped);
        settings.Set(Platform.Keys.SidebarWidthUserSet(SidebarDesignInfo.Slug(design)), true);
        return clamped;
    }
}

// ── 2. the one-time chooser gate ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>The PURE decisions behind the design-chooser UX: when it may open, what marks it seen, which design it
/// starts on, and when "Customize sidebar" is live. Getting either the gate or the marker wrong is unrecoverable per
/// install — burned too early permanently denies the chooser to the fresh installs it exists for, and never written
/// shows a "one-time" dialog on every launch — so this is pinned by a unit test, not left to review.</summary>
public static class SidebarDesignGating
{
    /// <summary>The chooser gate: exactly one boolean read. An EXISTING install has the marker true (never sees the
    /// chooser, stays Classic); a FRESH install has it false. Nothing else may gate the chooser.</summary>
    public static bool ShouldShowChooser(IAppSettings? settings)
        => settings is not null && !settings.Get(Platform.Keys.SidebarOnboardingSeen);

    /// <summary>Burn the one-time marker. Called from EVERY chooser exit path (confirm / "Not now" / Escape / a
    /// shutdown-time close), so the dialog can never appear twice. Idempotent; returns true only on the transition.
    /// Deliberately does NOT touch <c>Platform.Keys.SidebarDesign</c>: whatever design is applied when the dialog
    /// closes is the user's answer.</summary>
    public static bool MarkChooserSeen(IAppSettings? settings)
    {
        if (settings is null || settings.Get(Platform.Keys.SidebarOnboardingSeen)) return false;
        settings.Set(Platform.Keys.SidebarOnboardingSeen, true);
        return true;
    }

    /// <summary>The design the chooser (and the Settings picker) starts on: the persisted selection, coerced.</summary>
    public static SidebarDesign ActiveDesign(IAppSettings? settings)
        => settings is null ? SidebarDesign.Classic
                            : SidebarDesignInfo.FromInt(settings.Get(Platform.Keys.SidebarDesign));

    /// <summary>Does confirming <paramref name="confirmed"/> offer the "Customize now" follow-up? Only Curated: it
    /// is the only design with a document to edit.</summary>
    public static bool OffersCustomize(SidebarDesign confirmed) => confirmed == SidebarDesign.Curated;

    /// <summary>Is the "Customize sidebar" affordance live for <paramref name="active"/>? Same rule as
    /// <see cref="OffersCustomize"/>, named separately because it answers a different question in a different place
    /// (the Settings link-row's presence) and the two could legitimately diverge later.</summary>
    public static bool CanCustomize(SidebarDesign active) => active == SidebarDesign.Curated;

    /// <summary>Design → the picker's card value. The values ARE the persisted ints of
    /// <c>Platform.Keys.SidebarDesign</c> — one numbering, so a card index, a settings value and an enum member can
    /// never drift.</summary>
    public static int IndexOf(SidebarDesign design) => (int)design;

    /// <summary>Card value → design, tolerating a hand-edited or future value — the one coercion, shared with the
    /// persisted-setting path.</summary>
    public static SidebarDesign FromIndex(int value) => SidebarDesignInfo.FromInt(value);

    /// <summary>The card's title loc KEY (resolution is the UI's job, so this stays culture-free and testable).</summary>
    public static string TitleKey(SidebarDesign design) => design switch
    {
        SidebarDesign.LibraryV3 => "sidebar.design.v3",
        SidebarDesign.Curated => "sidebar.design.custom",
        _ => "sidebar.design.classic",
    };

    /// <summary>The card's two-line subtitle loc key.</summary>
    public static string SubtitleKey(SidebarDesign design) => design switch
    {
        SidebarDesign.LibraryV3 => "sidebar.design.v3Sub",
        SidebarDesign.Curated => "sidebar.design.customSub",
        _ => "sidebar.design.classicSub",
    };
}

// ── 3. Classic as a locked built-in document ────────────────────────────────────────────────────────────────────────

/// <summary>
/// CLASSIC AS A LOCKED BUILT-IN DOCUMENT. Classic is a DOCUMENT rendered by the one sidebar pane, so its metrics ARE
/// the pane's metrics by construction — LOCKED, not editable: rebuilt from code on every read, never persisted, never
/// reachable by a customizer command. Its only mutable state is the three per-section collapse flags Classic has
/// always persisted (<c>Platform.Keys.ClassicPinnedOpen/LibraryOpen/PlaylistsOpen</c>).
///
/// Today's IA, transcribed verbatim: Pinned · a rule · Your Library (albums · artists · liked · podcasts · local,
/// with counts) · a rule · Playlists (artwork + song-count subtitle + the create row) · a rule · the DevTools entry
/// (developer-mode only, header-less). The display options are chosen so the shared height ladder reproduces
/// Classic's rows exactly: Pinned/Playlists → Cozy + Subtitles ⇒ 44 with 32-DIP artwork; Your Library/DevTools →
/// glyph rows on Shortcuts/Links, Cozy + Subtitles:false ⇒ 40 (neither section ever paints a subtitle line).
/// </summary>
public static class SidebarBuiltInDocuments
{
    /// <summary>The stable template id Classic's document reports. NOT one of the customizer's templates: Classic is
    /// never offered there, and a Curated document must never claim this id.</summary>
    public const string ClassicId = "classic.builtin";

    /// <summary>Classic's DevTools entry route. DEVELOPER SURFACE: the whole Tools section is emitted only when
    /// developer mode is on (the <paramref name="devTools"/> parameter of <see cref="Classic"/>).</summary>
    public const string DevToolsRoute = "api-console";

    /// <summary>Build Classic's locked document with the three section-collapse flags applied.
    ///
    /// <para>Section IDS ARE STABLE STRINGS (not minted), unlike the Curated templates: the pane keys its reorder
    /// bands, its collapse routing and its scroll/section identity off them, and a fresh id per rebuild would reset
    /// all three on every toggle. They are also how <see cref="ClassicSectionOf"/> maps a header click back to the
    /// right preference flag without a lookup table.</para></summary>
    /// <param name="topBar">The shell's shortcut band — ONE global list on the Curated document, reaching Classic as
    /// an argument rather than from Classic's own locked document. Non-empty ⇒ materialised as the FIRST section,
    /// ahead of Pinned. Null/empty ⇒ nothing added and Pinned stays first (keeps the quick layout menu on the
    /// Pinned header).</param>
    /// <param name="devTools">Developer mode. FALSE (the product default) omits the Tools section AND its leading
    /// divider entirely. A PARAMETER rather than an internal read on purpose: this file is engine-free.</param>
    public static SidebarCustomLayout Classic(bool pinnedOpen, bool libraryOpen, bool playlistsOpen,
        IReadOnlyList<SidebarItemSpec>? topBar = null, bool devTools = false)
        => SidebarShortcutsSection.Prepend(ClassicSections(pinnedOpen, libraryOpen, playlistsOpen, devTools), topBar);

    static SidebarCustomLayout ClassicSections(bool pinnedOpen, bool libraryOpen, bool playlistsOpen, bool devTools)
    {
        var sections = new List<SidebarSectionSpec>(7)
        {
            // The FIRST group, so it keeps Classic's `rule: false` (no leading divider). Its header hosts the quick
            // layout menu and never disappears — even with zero pins — so that entry point is always reachable.
            new SidebarSectionSpec(PinnedId, SidebarSectionKind.Pinned,
                Title: null, TitleLocKey: "sidebar.pinned",
                Hidden: false, Collapsed: !pinnedOpen,
                Display: SidebarDisplayOptions.Entities with { ShowInRail = true }),

            Divider(DividerLibraryId),

            new SidebarSectionSpec(LibraryId, SidebarSectionKind.CollectionShortcuts,
                Title: null, TitleLocKey: "sidebar.yourLibrary",
                Hidden: false, Collapsed: !libraryOpen,
                // The Shortcuts preset alone (Cozy + Subtitles:false) is the honest 40-DIP height for a row that
                // never paints a subtitle, with the same 32-DIP art column every other content row uses.
                Display: SidebarDisplayOptions.Shortcuts,
                Items:
                [
                    Route(LibraryId + ":albums", "albums", "Album"),
                    Route(LibraryId + ":artists", "artists", "Contact"),
                    Route(LibraryId + ":liked", "liked", "Heart"),
                    Route(LibraryId + ":podcasts", "podcasts", "RadioTower"),
                    // Audiobooks (A2 plan §3.6) — its own library entry beside Podcasts.
                    Route(LibraryId + ":audiobooks", "audiobooks", "Microphone"),
                ]),

            Divider(DividerPlaylistsId),

            // Artwork + the song-count subtitle ⇒ Cozy 44 with 32-DIP covers; the planner appends the create row.
            new SidebarSectionSpec(PlaylistsId, SidebarSectionKind.PlaylistTree,
                Title: null, TitleLocKey: "sidebar.playlists",
                Hidden: false, Collapsed: !playlistsOpen,
                Display: SidebarDisplayOptions.Entities),
        };

        // DEVELOPER SURFACE, not product surface: hidden — divider and all — unless developer mode is on.
        if (devTools)
        {
            sections.Add(Divider(DividerToolsId));
            // Classic's flat DevTools row — deliberately header-less (a StaticLinks section with no title plans no
            // SectionHeader row). ShowInRail off: a bare glyph with no label reads as a stray tile in the 56-DIP rail.
            sections.Add(new SidebarSectionSpec(ToolsId, SidebarSectionKind.StaticLinks,
                Title: null, TitleLocKey: null,
                Hidden: false, Collapsed: false,
                Display: SidebarDisplayOptions.Links with { ShowInRail = false },
                Items: [Route(ToolsId + ":devtools", DevToolsRoute, "Code")]));
        }

        return new SidebarCustomLayout(ClassicId, sections);
    }

    // ── stable section ids ──
    public const string PinnedId = "classic.pinned";
    public const string LibraryId = "classic.library";
    public const string PlaylistsId = "classic.playlists";
    public const string ToolsId = "classic.tools";
    const string DividerLibraryId = "classic.rule.library";
    const string DividerPlaylistsId = "classic.rule.playlists";
    const string DividerToolsId = "classic.rule.tools";

    /// <summary>Map one of Classic's collapsible section ids onto the preference flag that owns its state. Null for
    /// a section that is not collapsible, so a stray toggle is a no-op rather than a mis-write.</summary>
    public static ClassicSection? ClassicSectionOf(string sectionId) => sectionId switch
    {
        PinnedId => ClassicSection.Pinned,
        LibraryId => ClassicSection.Library,
        PlaylistsId => ClassicSection.Playlists,
        _ => null,
    };

    static SidebarSectionSpec Divider(string id) => new(id, SidebarSectionKind.Divider);

    static SidebarItemSpec Route(string id, string routeKey, string iconName)
        => new(id, SidebarItemTarget.Route, routeKey, IconOverride: iconName);
}

/// <summary>
/// One mode component's cache of <see cref="SidebarBuiltInDocuments.Classic"/>, keyed on the only state that document
/// is a function of — the three collapse flags (folded into the same bitmask the pane's mode epoch uses), the top
/// bar reference, and developer mode.
///
/// <para><b>Why this is load-bearing.</b> The pane's publish stage decides between a per-row diff and a whole-window
/// re-skin with a document reference-equality test — correct, but a freshly-minted document defeats it
/// unconditionally: rebuilding Classic's document on every render made every publish take the wholesale arm. Library
/// V3 and Curated already cache/stabilize their documents this way; this makes all three modes agree.</para>
///
/// <para>Per MODE-COMPONENT INSTANCE, never static: the docked pane and the narrow drawer are independent mounts and
/// must not share mutable state. The cached document is immutable, so handing the same instance to both is safe.</para>
///
/// <para><b>Port note.</b> 0.2.9's <c>Get</c> read <c>DeveloperMode.Enabled.Value</c> (an engine <c>Signal&lt;bool&gt;</c>)
/// internally, which is the pane's own render subscription to the switch. This file is engine-free CORE, so
/// <paramref name="devTools"/> here is an explicit parameter — the caller (the engine-bound render path, which reads
/// the live developer-mode signal) passes it in, exactly as <see cref="SidebarBuiltInDocuments.Classic"/> already
/// takes it as a parameter rather than reading anything itself.</para>
/// </summary>
public sealed class ClassicDocumentCache
{
    SidebarCustomLayout? _doc;
    int _flags = -1;
    bool _devTools;
    /// <summary>The band the cached document was built from, compared by REFERENCE: every band edit rebuilds the
    /// list and replaces the layout record, so a reference match proves the content matches.</summary>
    IReadOnlyList<SidebarItemSpec>? _topBar;

    /// <summary>Classic's collapse flags folded into one int — bit 0 pinned, bit 1 library, bit 2 playlists. This IS
    /// the mode epoch's bitmask; the two must not drift, or a toggle would re-plan without rebuilding the document.
    /// Developer mode is deliberately NOT a bit here: it mints a new document instance on its own, which the pane's
    /// reference-equality test already treats as a wholesale re-skin.</summary>
    public static int FlagsOf(bool pinnedOpen, bool libraryOpen, bool playlistsOpen)
        => (pinnedOpen ? 1 : 0) | (libraryOpen ? 2 : 0) | (playlistsOpen ? 4 : 0);

    /// <summary>The document for these flags, this shortcut band and this developer-mode answer: the SAME instance
    /// while all four are unchanged, a fresh one on any flip.</summary>
    public SidebarCustomLayout Get(bool pinnedOpen, bool libraryOpen, bool playlistsOpen, bool devTools,
        IReadOnlyList<SidebarItemSpec>? topBar = null)
    {
        int flags = FlagsOf(pinnedOpen, libraryOpen, playlistsOpen);
        if (_doc is { } cached && _flags == flags && _devTools == devTools && ReferenceEquals(_topBar, topBar))
            return cached;
        _flags = flags;
        _devTools = devTools;
        _topBar = topBar;
        _doc = SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen, topBar, devTools);
        return _doc;
    }
}

// ── 4. the nav band's pure half ──────────────────────────────────────────────────────────────────────────────────────

// The band's CONTENT is `SidebarCustomLayout.EffectiveTopBar` (one global list, mutated only through the existing
// AddTopBarItem/MoveTopBarItem/RemoveTopBarItem commands) — this section adds no schema, no command and no second
// source of truth; it is a projection of that list into the four render shapes the band draws.

/// <summary>Which of the four shapes a band tile draws. Mirrors <see cref="SidebarItemTarget"/> deliberately rather
/// than reusing it: an item target a future build adds must degrade to the route shape here instead of failing to
/// draw.</summary>
public enum SidebarNavBandTileKind : byte { Route = 0, Entity = 1, Track = 2, Action = 3 }

/// <summary>One shaped band tile. <paramref name="Index"/> is the tile's index in the EFFECTIVE band, so the
/// renderer resolves the full <see cref="SidebarItemSpec"/> (label/icon override, fallback title/art, binding) from
/// the same list it was shaped from — the shaping never copies display state, keeping this record POD.</summary>
public readonly record struct SidebarNavBandTile(int Index, string ItemId, SidebarNavBandTileKind Kind, string Key, string? RouteKey);

public static class SidebarNavBandModel
{
    /// <summary>The band's hard cap — the reducer's, not a second one; re-read here purely as the truncation bound
    /// so a hand-edited document that somehow carries more cannot make the band outgrow the pane's head.</summary>
    public const int MaxTiles = SidebarLayoutReducer.MaxTopBarItems;

    /// <summary>Does the band render at all? An EMPTY list means the user emptied it on purpose (Home is genuinely
    /// removable), and an emptied band draws NOTHING in both forms. Null never reaches here.</summary>
    public static bool Renders(IReadOnlyList<SidebarItemSpec>? band) => band is { Count: > 0 };

    /// <summary>The tile shape for an item. An unknown/future target degrades to
    /// <see cref="SidebarNavBandTileKind.Route"/> — the one shape that can always draw something instead of a hole.</summary>
    public static SidebarNavBandTileKind KindOf(SidebarItemSpec item) => item.Target switch
    {
        SidebarItemTarget.Entity => SidebarNavBandTileKind.Entity,
        SidebarItemTarget.Track => SidebarNavBandTileKind.Track,
        SidebarItemTarget.Action => SidebarNavBandTileKind.Action,
        _ => SidebarNavBandTileKind.Route,
    };

    /// <summary>The app route this tile navigates to — and therefore the route it draws SELECTED for. Null means no
    /// destination: a Track plays, an Action executes, and an entity uri the pin scheme refuses has nowhere to go and
    /// renders visible-but-inert. The uri → route map is <see cref="SidebarPinId.FromUri"/>'s, never a second one.</summary>
    public static string? RouteKeyOf(SidebarItemSpec item) => item.Target switch
    {
        SidebarItemTarget.Entity => SidebarPinId.FromUri(item.Key),
        SidebarItemTarget.Track or SidebarItemTarget.Action => null,
        _ => item.Key is { Length: > 0 } key ? key : null,
    };

    /// <summary>Does this tile draw its selection mark for <paramref name="route"/>? One owner, so the expanded
    /// row's mark and the rail tile's selected treatment can never disagree.</summary>
    public static bool SelectsRoute(SidebarItemSpec item, string? route)
        => route is { Length: > 0 }
           && RouteKeyOf(item) is { Length: > 0 } target
           && string.Equals(target, route, StringComparison.Ordinal);

    /// <summary>Shape the effective band into <paramref name="into"/> (cleared first), in DOCUMENT ORDER — the band
    /// is a flat authored list and its order is the user's, so nothing here sorts, groups or promotes.
    ///
    /// <para>Truncation is a tail drop at <paramref name="cap"/>: over-cap is already a reducer REJECTION, so a band
    /// that arrives over-cap is hand-edited or a newer-build document, and dropping the tail keeps the head's
    /// geometry predictable. Hidden items are deliberately NOT filtered — the band's three commands never set
    /// <c>Hidden</c>.</para></summary>
    /// <returns>The number of tiles written.</returns>
    public static int Shape(IReadOnlyList<SidebarItemSpec>? band, List<SidebarNavBandTile> into, int cap = MaxTiles)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (band is null || cap <= 0) return 0;

        for (int i = 0; i < band.Count && into.Count < cap; i++)
        {
            var item = band[i];
            if (item is null) continue;
            into.Add(new SidebarNavBandTile(i, item.Id, KindOf(item), item.Key, RouteKeyOf(item)));
        }
        return into.Count;
    }
}

// ── LIBRARY V3 — PURE RULES (METRICS, SEARCH, CHIPS, VIEW, DOCUMENT) ──────────────────────────────────────────────
//
// Library V3's engine-free decisions, ported verbatim in behaviour: the fixed chrome band heights and width
// thresholds (LibraryV3Metrics/Labels), the search host's escape/blur/width arithmetic (LibraryV3SearchRules), the
// filter rail's idle/filtered/fused chip model (LibraryV3ChipStrip), the tree re-grouping + drill-in view over the
// already-shaped projection (LibraryV3View/LibraryV3Window), and the synthesized read-only document that maps V3's
// view state onto section/display specs for the one SidebarPane (LibraryV3Document). Nothing here renders anything.

/// <summary>V3's pure chrome geometry: the fixed band heights (§3.2.2's vertical stack), the two width thresholds
/// the chrome branches on, the derived grid column rule, and the persisted-code coercion every reader shares.
/// <para>The row/grid CONTENT geometry is deliberately NOT here — content is the one SidebarPane's own
/// SidebarPaneMetrics/SidebarRowMetrics ladder; re-adding it here would recreate the second ladder that made the
/// same Cozy row 44 in one mode and 48 in another.</para></summary>
public static class LibraryV3Metrics
{
    // The expanded chrome stack, top to bottom: nav band (30) → header (44) → toolbar (36) → chip rail (40) →
    // (rule) → breadcrumb (32, narrow/drawer only).
    /// <summary>The library-destination word rail (#85 H4): five words between Home and the library rule. Shorter
    /// than a list row (<see cref="NavRowHeight"/>, 40) because it is a band of type, not a list.</summary>
    public const float DestinationRailH = 30f;
    public const float HeaderHeight = 44f;
    public const float ToolbarHeight = 36f;
    /// <summary>The ONE filter rail: a facet's sub-filter fuses INTO its pill instead of a second (qualifier) band,
    /// so 40 — vs the pills' own 28 — is deliberate slack for the fused pill's raised inner segment and its shadow.</summary>
    public const float ChipRailHeight = 40f;
    /// <summary>The drill-in breadcrumb band (narrow/drawer only — the folder amendment).</summary>
    public const float BreadcrumbHeight = 32f;

    /// <summary>The nav band's own row height — CHROME above the header, so it does not have to match the 44
    /// content row; Spotify's own "Your Library" nav rows are 40.</summary>
    public const float NavRowHeight = 40f;
    /// <summary>Destination-word type size and inter-word gap: 13.5 sits under the 15px header title and above the
    /// 12.5px filter chips, keeping all three legible as separate ranks.</summary>
    public const float DestinationWordSize = 13.5f, DestinationWordGap = 14f;
    /// <summary>The edge fade a clipped destination word peeks through — the affordance that says the rail
    /// scrolls. Labels never truncate to a glyph; the rail scrolls instead. Painted from the rail's LIVE scroll
    /// geometry per render, never a hardcoded side — a fade with nothing behind it is a lie.</summary>
    public const float DestinationRailFade = 20f;
    /// <summary>Diameter of the hover-revealed pager chevrons — smaller than the standard icon-button size so a
    /// pager large enough to brush the band's own edges never reads as enlarging it.</summary>
    public const float DestinationRailChevronSize = 20f;
    public const float DestinationRailChevronGlyph = 10f;
    /// <summary>Fraction of the rail's live viewport width one pager click scrolls. Less than 1 so the outgoing
    /// page's trailing word still peeks at the opposite edge — continuity, not a jump-cut.</summary>
    public const float DestinationRailPageStep = 0.8f;

    /// <summary>Grid cell gap, restated from the pane's own grid-strip spacing so the derived column count and the
    /// strip that renders it cannot disagree.</summary>
    public const float GridGap = 8f;

    /// <summary>Below this pane width the sort/view trigger renders icon-only so the search field gets the row.</summary>
    public const float SortIconOnlyWidth = 280f;

    /// <summary>At/above this pane width folders disclose INLINE (recursive, indented); below it — and always in
    /// the overlay drawer — folders NAVIGATE (drill-in, breadcrumb + back). A 240-320 pane cannot carry four indent
    /// levels and still show a playlist name.</summary>
    public const float DrillInWidth = 320f;

    public static bool IsGrid(int view) => view >= (int)SidebarV3View.CompactGrid;
    public static bool IsList(int view) => view <= (int)SidebarV3View.List;

    /// <summary>Minimum grid cell edge per view — the input to the derived column count.</summary>
    public static float MinCellWidth(int view) => view == (int)SidebarV3View.CompactGrid ? 84f : 116f;

    /// <summary>The derived column count: floor((cross + gap) / (min + gap)), never less than 1. The cell size is
    /// DERIVED from the pane width, never chosen — no S/M/L row, and the persisted V3GridSize stays unread.</summary>
    public static int Columns(int view, float cross)
    {
        if (!float.IsFinite(cross) || cross <= 0f) return 1;
        float min = MinCellWidth(view);
        int n = (int)MathF.Floor((cross + GridGap) / (min + GridGap));
        return n < 1 ? 1 : n;
    }

    /// <summary>Whether the library-only search box holds a real query, without allocating a trimmed copy.</summary>
    public static bool HasQuery(string? raw)
    {
        if (raw is null) return false;
        for (int i = 0; i < raw.Length; i++)
            if (!char.IsWhiteSpace(raw[i])) return true;
        return false;
    }

    // Persisted-code coercion — "auto-corrected on load".
    public static int NormalizeView(int v) => (uint)v <= 3 ? v : (int)SidebarV3View.List;
    public static int NormalizeFilter(int v) => (uint)v <= 4 ? v : (int)SidebarV3Filter.All;
    public static int NormalizeSort(int v) => (uint)v <= 4 ? v : (int)SidebarV3Sort.Recents;
    public static int NormalizeQualifier(int v) => (uint)v <= 3 ? v : (int)SidebarV3Qualifier.Any;
}

/// <summary>V3's label vocabulary, as LOC KEYS — CORE never calls into the localization engine; the UI layer
/// resolves these dotted keys. Separate from <see cref="LibraryV3Metrics"/> so strings and geometry review
/// independently, and so no caller hand-writes a key for a chip/sort/view that already has one.</summary>
public static class LibraryV3Labels
{
    public static string Filter(int filter) => filter switch
    {
        (int)SidebarV3Filter.Playlists => "sidebar.v3.filter.playlists",
        (int)SidebarV3Filter.Podcasts => "sidebar.v3.filter.podcasts",
        (int)SidebarV3Filter.Albums => "sidebar.v3.filter.albums",
        (int)SidebarV3Filter.Artists => "sidebar.v3.filter.artists",
        _ => "sidebar.v3.title",
    };

    public static string Qualifier(int qualifier) => qualifier switch
    {
        (int)SidebarV3Qualifier.ByYou => "sidebar.v3.qualifier.byYou",
        (int)SidebarV3Qualifier.BySpotify => "sidebar.v3.qualifier.bySpotify",
        (int)SidebarV3Qualifier.Mixed => "sidebar.v3.qualifier.mixed",
        _ => "",
    };

    /// <summary>Sort keys REUSE library.sort.* for codes 0-3 (index-aligned with the library sort view); only
    /// V3's own Custom order is a new key.</summary>
    public static string Sort(int sort) => sort switch
    {
        (int)SidebarV3Sort.RecentlyAdded => "library.sort.recentlyAdded",
        (int)SidebarV3Sort.Alphabetical => "library.sort.alphabetical",
        (int)SidebarV3Sort.Creator => "library.sort.creator",
        (int)SidebarV3Sort.Custom => "sidebar.v3.sort.custom",
        _ => "library.sort.recents",
    };

    /// <summary>The view toggle's glyph — an engine icon NAME string (CORE is engine-free: no Icons reference).</summary>
    public static string ViewGlyph(int view) => view >= (int)SidebarV3View.CompactGrid ? "ViewGrid" : "ViewList";
}

/// <summary>The library search host's pure decisions: the Escape ladder, the blur-close rule and the open-width
/// arithmetic — the morph's three fiddliest rules, reviewable and testable without a component, a signal or a
/// frame.</summary>
public static class LibraryV3SearchRules
{
    /// <summary>The closed host's width — the same box the magnifier button always was, so the morph's start/end
    /// frame never jumps on open/close.</summary>
    public const float ClosedWidth = 32f;

    /// <summary>The sort/view trigger's icon-only box — what <see cref="OpenWidth"/> must leave room for so the
    /// open field never overlaps the pill it shares the toolbar row with.</summary>
    public const float SortIconOnlyWidth = 28f;

    /// <summary>The toolbar's own gap between the search host and the sort/view trigger.</summary>
    public const float Gap = 4f;

    /// <summary>At/above this pane width the field is INLINE — always expanded, sharing the toolbar row with the
    /// full sort/view pill. Below it the field collapses to the 32-DIP magnifier and a click morphs it open while
    /// the sort pill drops to icon-only. Sits above <see cref="LibraryV3Metrics.SortIconOnlyWidth"/> (280) on
    /// purpose: an inline field never coexists with an icon-only pill, so the row has exactly two shapes.</summary>
    public const float InlineWidth = 300f;

    /// <summary>The toolbar row's shape for one (pane width, user opened it, has text) triple.</summary>
    /// <param name="Inline">The field is permanently expanded (wide pane) — no button, no tooltip, no morph.</param>
    /// <param name="Expanded">The field is showing (inline, or opened/holding text on a narrow pane).</param>
    /// <param name="SortIconOnly">The sort/view pill shows only its glyph.</param>
    public readonly record struct Layout(bool Inline, bool Expanded, bool SortIconOnly);

    /// <summary>Resolve the row's shape. Narrow + text keeps the field open even if the user never "opened" it (a
    /// query typed while wide must survive a seam drag past the threshold); narrow + empty + not opened is the
    /// button.</summary>
    public static Layout Resolve(float paneWidth, bool openedByUser, bool hasText)
    {
        bool inline = paneWidth >= InlineWidth;
        if (inline) return new Layout(true, true, SortIconOnly: false);
        bool expanded = openedByUser || hasText;
        return new Layout(false, expanded, SortIconOnly: expanded || paneWidth < 280f);
    }

    public enum EscapeAction : byte { None, Clear, Close }

    /// <summary>One Escape clears the query (mirroring the WinUI TextBox DeleteButton); a SECOND Escape, on an
    /// already-empty field, closes it. Never <see cref="EscapeAction.None"/> — the host is only reachable while
    /// open, and an open host always has something to do with Escape.</summary>
    public static EscapeAction OnEscape(string text) => text.Length > 0 ? EscapeAction.Clear : EscapeAction.Close;

    /// <summary>Focus left the editor: an EMPTY field closes; a field carrying a query stays open (Spotify keeps an
    /// active filter on screen even after the pointer moves to a row).</summary>
    public static bool ClosesOnBlur(string text) => text.Length == 0;

    /// <summary>The open host's width: the toolbar's own content lane minus the icon-only sort pill and the one gap
    /// between them, so the field's trailing edge lands exactly where the pill's leading edge would otherwise sit.
    /// Floored at <see cref="ClosedWidth"/> so a pane narrower than pill+gap still yields a host, not a negative
    /// width.</summary>
    public static float OpenWidth(float paneWidth, float toolbarPadH)
        => MathF.Max(ClosedWidth, paneWidth - toolbarPadH - SortIconOnlyWidth - Gap);
}

/// <summary>What one rendered position of the Library V3 filter rail IS — a pure function of (filter, qualifier,
/// whether the data evidences a qualifier). Generalises the Home facet strip for a rail with exactly ONE
/// selectable facet at a time: a leading Clear slot stands in for "All", because clearing here is a distinct
/// GESTURE (a ✕ that pops in), not another tab in the row.</summary>
public enum V3ChipKind : byte { Clear, Facet, Fused, Option }

/// <summary>One rendered position of the rail. <see cref="SelectFilter"/>/<see cref="SelectQualifier"/> is exactly
/// what a tap (or Space/Enter on the roved chip) writes back to the persisted filter/qualifier — one write path for
/// every kind instead of one per shape.
/// <para><see cref="Key"/> is the node key the renderer must use. A <see cref="V3ChipKind.Facet"/> slot and the
/// <see cref="V3ChipKind.Fused"/> slot of the SAME code share it ("v3f{code}") — that shared identity IS the
/// loose-pill ⇄ fused-pill morph; a distinct key per shape would unmount one and mount the other with nothing left
/// to reflow from.</para>
/// <para><see cref="Route"/> (issue #85, H4) — the destination page this chip's KIND has, or null when it has
/// none. A plain tap always writes the filter/qualifier; a non-null route is only a secondary affordance (a
/// double-click) the renderer may offer. Playlists has no "all playlists" page, so its Facet/Fused slots carry a
/// null route like Clear/Option always do.</para></summary>
public readonly record struct V3ChipSlot(
    V3ChipKind Kind, int Code, bool Selected, string Key, int SelectFilter, int SelectQualifier, string? Route = null);

/// <summary>The Library V3 filter rail's layout, as a pure function of (persisted filter, persisted qualifier,
/// whether the data evidences ≥2 qualifier flavors).
/// <para>THREE SHAPES, ONE MODEL. <b>Idle</b> (no filter): the four facets, unselected, no ✕ (nothing to clear).
/// <b>Filtered</b> (a facet picked, no qualifier fused): a leading Clear slot, then the selected facet — plus, ONLY
/// under Playlists with ≥2 provenance flavors evidenced, the three qualifier options spilled right after it.
/// <b>Fused</b> (a qualifier is ALSO picked — only possible under Playlists): Clear, then one Fused slot sharing
/// the facet's own key — the loose facet pill and the fused pill are the SAME node across that transition, which
/// is the entire point of the shared key.</para></summary>
public static class LibraryV3ChipStrip
{
    const int All = (int)SidebarV3Filter.All;
    const int Playlists = (int)SidebarV3Filter.Playlists;
    const int Any = (int)SidebarV3Qualifier.Any;

    public static readonly int[] Facets =
    [
        (int)SidebarV3Filter.Playlists, (int)SidebarV3Filter.Podcasts,
        (int)SidebarV3Filter.Albums, (int)SidebarV3Filter.Artists,
    ];

    public static readonly int[] Qualifiers =
    [
        (int)SidebarV3Qualifier.ByYou, (int)SidebarV3Qualifier.BySpotify, (int)SidebarV3Qualifier.Mixed,
    ];

    /// <summary>idle: F F F F. filtered: X [F*] (+ its options — Playlists only, and only when the data evidences
    /// them). fused: X [F*│Q] — the fused pill and the loose facet share key "v3f{code}" (the morph).</summary>
    public static List<V3ChipSlot> Slots(int filter, int qualifier, bool qualifiersAvailable)
    {
        var slots = new List<V3ChipSlot>(8);
        if (filter == All)
        {
            foreach (var f in Facets) slots.Add(Facet(f, false));
            return slots;
        }

        slots.Add(new V3ChipSlot(V3ChipKind.Clear, 0, false, "v3-clear", All, Any));

        bool owns = filter == Playlists && qualifiersAvailable;
        if (owns && qualifier != Any)
        {
            // Fused: tapping the compound pill is ONE step back (drop the qualifier), not all the way to All.
            // Route is always null here in practice (only Playlists fuses, and Playlists has no route), threaded
            // through anyway so a future fuseable facet is not silently dropped from the route contract.
            slots.Add(new V3ChipSlot(V3ChipKind.Fused, filter, true, "v3f" + filter, filter, Any, RouteFor(filter)));
        }
        else
        {
            slots.Add(Facet(filter, true));
            if (owns)
                foreach (var q in Qualifiers)
                    slots.Add(new V3ChipSlot(V3ChipKind.Option, q, false, "v3q" + q, filter, q));
        }
        return slots;
    }

    /// <summary>Roving focus survives relayout by CODE, not index: the slot whose key matches
    /// <paramref name="focusedKey"/>, or 0 when nothing matches (the focused chip left the rail entirely).</summary>
    public static int FocusIndex(List<V3ChipSlot> slots, string? focusedKey)
    {
        if (focusedKey is not null)
            for (int i = 0; i < slots.Count; i++)
                if (slots[i].Key == focusedKey) return i;
        return 0;
    }

    /// <summary>A facet slot. Unselected (idle): tapping SELECTS it. Selected (filtered, not fused): tapping
    /// CLEARS back to All — the pill itself is the "remove this filter" affordance while only one thing is active.</summary>
    static V3ChipSlot Facet(int f, bool selected)
        => new(V3ChipKind.Facet, f, selected, "v3f" + f, selected ? All : f, Any, RouteFor(f));

    /// <summary>Issue #85 (H4) — the chip's "open this as a page" destination. Only Albums/Artists/Podcasts have an
    /// actual library page; Playlists has none, so its chip stays filter-only like Clear/Option.</summary>
    public static string? RouteFor(int filter) => filter switch
    {
        (int)SidebarV3Filter.Albums => "albums",
        (int)SidebarV3Filter.Artists => "artists",
        (int)SidebarV3Filter.Podcasts => "podcasts",
        _ => null,
    };
}

/// <summary>The V3 content ORDER, as one pure pass over the published projection. The projection's own sort is
/// FLAT (a nested playlist can land above its containing folder); this re-groups folders among siblings by the
/// active sort with each folder's children ordered the same way WITHIN it, and also owns the DRILL LEVEL (a
/// drilled-in level is one folder's direct children, flattened to depth 0 — a BUILD INPUT, never a renderer
/// branch). It does not filter, sort, search or decide what is pinned — that already happened upstream.
/// <para>ALLOCATION: one output List plus pooled per-folder buckets, reused across rebuilds — once per plan
/// (a projection publish, a state change, a drill push/pop), never per frame or per row.</para></summary>
public sealed class LibraryV3View
{
    /// <summary>Hard recursion guard for the inline folder walk — the rootlist is unbounded and a cyclic or absurd
    /// tree must not be able to stack-overflow the UI thread. 32 is 0.2.9's number (its `SidebarTree.MaxDepth`); the
    /// deepest folder nesting Spotify itself allows is far below it, so this only ever fires on corrupt data.</summary>
    public const int MaxDepth = 32;

    readonly List<SidebarLibraryEntry> _rows = new(256);

    // folder id → parent folder id ("" at top level), walked off the binder's fully flattened tree slice. A
    // projected entry knows its CONTAINING folder for every kind except a folder row (whose FolderId is its own
    // id), so a folder's parent is only recoverable from the tree walk — memoised on the binder revision.
    readonly Dictionary<string, string> _parentOfFolder = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<int>> _buckets = new(StringComparer.Ordinal);
    readonly List<List<int>> _bucketPool = new();
    readonly List<int> _top = new();
    readonly HashSet<string> _folderRows = new(StringComparer.Ordinal);
    string[]? _folderStack;
    int _parentRevision = int.MinValue;

    /// <summary>The built order. Every entry carries a REWRITTEN Depth (display indent) and SourceOrder (its
    /// position here), which is what lets the planner's CustomOrder comparator reproduce this order verbatim.</summary>
    public IReadOnlyList<SidebarLibraryEntry> Rows => _rows;

    public int Count => _rows.Count;

    /// <summary>True when the drilled-into folder is no longer present (unfollowed, filtered away, reloaded) — the
    /// mode component pops the stack rather than showing a level whose breadcrumb points at nothing.
    /// "Missing" means the FOLDER ROW is gone, not that it has no children: an empty folder is a legitimate level.</summary>
    public bool DrillTargetMissing { get; private set; }

    /// <summary>Rebuild the order.</summary>
    /// <param name="published">The shaped projection.</param>
    /// <param name="skip">Leading entries to drop — the pin band, when rendered as its own section (0 otherwise).</param>
    /// <param name="tree">The binder's fully flattened rootlist tree slice, for the folder→parent map. Null
    /// degrades every folder to top level rather than hiding rows.</param>
    /// <param name="treeRevision">The binder revision <paramref name="tree"/> came from — the parent map's memo key.</param>
    /// <param name="drillFolderId">The folder whose direct children to list, or null/"" for the library root.</param>
    /// <param name="group">Whether to re-group into tree order. False passes the slice through flat at depth 0 —
    /// what a search (already flat) and the grid views (no disclosure) want.</param>
    public void Build(IReadOnlyList<SidebarLibraryEntry>? published, int skip,
                      IReadOnlyList<SidebarLibraryEntry>? tree, int treeRevision,
                      string? drillFolderId, bool group)
    {
        _rows.Clear();
        DrillTargetMissing = false;
        if (published is null || published.Count == 0)
        {
            // An empty projection at a drill level is not a MISSING target (cold library) — reporting "missing"
            // here would pop the stack on every cold start.
            ReleaseBuckets();
            _top.Clear();
            _folderRows.Clear();
            return;
        }

        int n = published.Count;
        if (skip < 0) skip = 0;
        if (skip > n) skip = n;

        bool drill = drillFolderId is { Length: > 0 };

        if (!drill && !group)
        {
            for (int i = skip; i < n; i++) Emit(published[i], 0);
            return;
        }

        EnsureParentMap(tree, treeRevision);
        // A drill level buckets EVERY row (a pinned playlist inside the folder must still appear inside it, and
        // the pin band does not render at a drilled-in level); the root level buckets only the post-pin remainder.
        BuildBuckets(published, drill ? 0 : skip);

        if (drill)
        {
            DrillTargetMissing = !_folderRows.Contains(drillFolderId!);
            if (!DrillTargetMissing && _buckets.TryGetValue(drillFolderId!, out var kids))
                for (int i = 0; i < kids.Count; i++) Emit(published[kids[i]], 0);
            return;
        }

        EmitLevel(published, _top, 0);
    }

    /// <summary>The parent-folder id of a built row — the sibling band a custom-order drag may move WITHIN. "" for
    /// a top-level row.</summary>
    public string ParentOf(int index)
        => (uint)index < (uint)_rows.Count ? ParentKey(_rows[index]) : "";

    /// <summary>Whether two built rows are siblings. A drop aimed across a folder boundary must not commit here:
    /// this overlay is V3's LOCAL custom order, and moving an item between folders is a rootlist write, made only
    /// through the resource-drop seam and folder actions.</summary>
    public bool SameParent(int a, int b)
        => string.Equals(ParentOf(a), ParentOf(b), StringComparison.Ordinal);

    /// <summary>The same boundary, applied DURING the gesture: the slot a drag from <paramref name="from"/> may
    /// actually reach when the pointer asks for <paramref name="to"/>. Snapping the REQUESTED slot to the nearest
    /// one inside the source's sibling run means the gap never opens across a boundary in the first place.
    /// <para>The run is the SET of same-parent rows, not a contiguous span (an expanded folder's children sit
    /// between two top-level siblings), so a top-level drag must be able to travel PAST them. Ties go to the lower
    /// slot — either is equally legal and the choice only needs to be deterministic.</para></summary>
    public int ClampToSiblingRun(int from, int to)
    {
        int n = _rows.Count;
        if (n == 0 || (uint)from >= (uint)n) return to;
        if (to < 0) to = 0;
        else if (to >= n) to = n - 1;
        if (SameParent(from, to)) return to;

        string parent = ParentKey(_rows[from]);
        int below = -1, above = -1;
        for (int i = to - 1; i >= 0; i--)
            if (string.Equals(ParentKey(_rows[i]), parent, StringComparison.Ordinal)) { below = i; break; }
        for (int i = to + 1; i < n; i++)
            if (string.Equals(ParentKey(_rows[i]), parent, StringComparison.Ordinal)) { above = i; break; }
        // `from` is itself in the run, so at least one side always resolves; the fallback is a no-move.
        if (below < 0) return above < 0 ? from : above;
        if (above < 0) return below;
        return to - below <= above - to ? below : above;
    }

    /// <summary>The stable key (entry id) at a built index — what the pane's reorder band reports per slot.</summary>
    public string KeyAt(int index) => (uint)index < (uint)_rows.Count ? _rows[index].Id : "";

    /// <summary>Materialize the ENTIRE visible order into <paramref name="into"/> as entry ids, with the row at
    /// <paramref name="from"/> moved to <paramref name="to"/> — "on any user move the whole current visible order
    /// is written", which is what keeps later appends stable without ever rewriting the overlay again.</summary>
    public void MaterializeOrder(List<string> into, int from, int to)
    {
        into.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            int slot = MovedIndex(i, from, to);                    // which VIEW slot supplies row i after the move
            if ((uint)slot >= (uint)_rows.Count) continue;
            var e = _rows[slot];
            // An authored route row (Liked Songs) and a track row have no place in a playlist order.
            if (e.Kind is SidebarEntryKind.AppRoute or SidebarEntryKind.Track) continue;
            if (e.Id.Length > 0) into.Add(e.Id);
        }
    }

    // The permutation a single remove-at-from/insert-at-to applies, read backwards (which VIEW slot supplies row i).
    static int MovedIndex(int i, int from, int to)
    {
        if (from == to) return i;
        if (from < to)
        {
            if (i < from || i > to) return i;
            return i == to ? from : i + 1;
        }
        if (i < to || i > from) return i;
        return i == to ? from : i - 1;
    }

    // ── the rebuild ──────────────────────────────────────────────────────────────────────────────────────────────

    // Depth-first emission of one sibling level: a folder row is followed by its children, present in the
    // projection only when the folder is expanded (the binder's own gate) — an expansion test here would be a
    // second, driftable copy of that rule.
    void EmitLevel(IReadOnlyList<SidebarLibraryEntry> src, List<int> level, int depth)
    {
        for (int i = 0; i < level.Count; i++)
        {
            int at = level[i];
            var e = src[at];
            Emit(e, depth);
            if (depth >= MaxDepth || !e.IsFolder) continue;
            if (_buckets.TryGetValue(e.FolderId, out var kids)) EmitLevel(src, kids, depth + 1);
        }
    }

    void BuildBuckets(IReadOnlyList<SidebarLibraryEntry> src, int from)
    {
        _top.Clear();
        ReleaseBuckets();
        _folderRows.Clear();

        for (int i = from; i < src.Count; i++)
            if (src[i].IsFolder && src[i].FolderId.Length > 0) _folderRows.Add(src[i].FolderId);

        for (int i = from; i < src.Count; i++)
        {
            string parent = ParentKey(src[i]);
            // A row whose parent folder is NOT itself a visible row (pinned into the band, dropped by the lens, or
            // a cold tree map) is promoted to top level. Nothing is ever hidden because its container is elsewhere.
            if (parent.Length == 0 || !_folderRows.Contains(parent)) _top.Add(i);
            else Bucket(parent).Add(i);
        }
    }

    string ParentKey(in SidebarLibraryEntry e)
    {
        if (!e.IsFolder) return e.FolderId;
        return _parentOfFolder.TryGetValue(e.FolderId, out var p) ? p : "";
    }

    void Emit(in SidebarLibraryEntry e, int depth)
    {
        int d = depth < 0 ? 0 : depth > MaxDepth ? MaxDepth : depth;
        // Depth is the DISPLAY indent the row planner stamps onto its rows; SourceOrder is this row's position,
        // which is what makes a re-sort by the planner's CustomOrder comparator (SourceOrder ascending) a no-op.
        _rows.Add(e with { Depth = d, SourceOrder = _rows.Count });
    }

    List<int> Bucket(string folderId)
    {
        if (_buckets.TryGetValue(folderId, out var list)) return list;
        if (_bucketPool.Count > 0)
        {
            list = _bucketPool[_bucketPool.Count - 1];
            _bucketPool.RemoveAt(_bucketPool.Count - 1);
            list.Clear();
        }
        else
        {
            list = new List<int>(8);
        }
        _buckets[folderId] = list;
        return list;
    }

    void ReleaseBuckets()
    {
        foreach (var kv in _buckets) _bucketPool.Add(kv.Value);
        _buckets.Clear();
    }

    // The folder→parent map, walked off the binder's tree slice (depth-first, pre-order, folders included, fully
    // flattened regardless of expansion — exactly why it can answer "who contains this folder" when the published
    // list cannot). Memoised on the binder revision: it only moves when the rootlist does.
    void EnsureParentMap(IReadOnlyList<SidebarLibraryEntry>? tree, int revision)
    {
        if (tree is null)
        {
            if (_parentRevision != int.MinValue) { _parentOfFolder.Clear(); _parentRevision = int.MinValue; }
            return;
        }
        if (revision == _parentRevision && _parentOfFolder.Count > 0) return;
        _parentRevision = revision;
        _parentOfFolder.Clear();

        var stack = _folderStack ??= NewStack();
        for (int i = 0; i < stack.Length; i++) stack[i] = "";
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            int d = e.Depth;
            if (d < 0 || d + 1 >= stack.Length) continue;
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            _parentOfFolder[e.FolderId] = stack[d];
            stack[d + 1] = e.FolderId;
        }
    }

    static string[] NewStack()
    {
        var s = new string[MaxDepth + 2];
        for (int i = 0; i < s.Length; i++) s[i] = "";
        return s;
    }
}

/// <summary>
/// A reusable WINDOW over a projection list — [start, start+count) without copying a single entry.
///
/// <para>It is how the mode component hands the pane its pin band: the shaped projection already leads with the
/// surviving pins, so the pin section's rows and the library section's rows are two windows over ONE list and can
/// never disagree about which pins survived the active filter. One instance is reused for the pane's life.</para>
/// </summary>
public sealed class LibraryV3Window : IReadOnlyList<SidebarLibraryEntry>
{
    /// <summary>The out-of-range fallback. NOT <c>default(SidebarLibraryEntry)</c> — a default instance's
    /// positional string members are null, and a row built from one would hand the renderer a null label.</summary>
    static readonly SidebarLibraryEntry Blank = SidebarLibraryEntry.ForRoute("", "");

    IReadOnlyList<SidebarLibraryEntry>? _source;
    int _start;
    int _count;

    public void Set(IReadOnlyList<SidebarLibraryEntry>? source, int start, int count)
    {
        _source = source;
        int n = source?.Count ?? 0;
        if (start < 0) start = 0;
        if (start > n) start = n;
        if (count < 0) count = 0;
        if (start + count > n) count = n - start;
        _start = start;
        _count = count;
    }

    public int Count => _count;

    /// <summary>Bounds-checked against BOTH the window and the LIVE source: the projection publishes into one
    /// reused List, so a window taken before a rebuild that shrank the list must degrade to a blank row, not throw.</summary>
    public SidebarLibraryEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count || _source is not { } src) return Blank;
            int at = _start + index;
            return (uint)at < (uint)src.Count ? src[at] : Blank;
        }
    }

    public IEnumerator<SidebarLibraryEntry> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

// ── LIBRARY V3 AS A SYNTHESIZED DOCUMENT ────────────────────────────────────────────────────────────────────────
//
// V3's chrome (nav band, header, toolbar, chips, breadcrumb) stays; its CONTENT becomes an ephemeral document plus
// a read-only SidebarPaneConfig over the one SidebarPane. EPHEMERAL, NEVER PERSISTED: unlike Curated's document
// (edited by the customizer, autosaved) and Classic's locked built-in, this one is rebuilt from V3's view state on
// every state change and thrown away — nothing dispatches a command against it, because the chrome, not the
// document, owns the state. PURE BY CONSTRUCTION: System plus the engine-free V3 enums and the core Sidebar model,
// so the mapping rules are unit-testable with no signal, no Loc, no Element, no width.
public static class LibraryV3Document
{
    /// <summary>The document's template id — deliberately NOT one of SidebarTemplates' ids: V3 is not a Curated
    /// template and must never be mistaken for one by "reset to template".</summary>
    public const string TemplateId = "v3.synth";

    // STABLE section ids. The pane keys its reorder bands, scroll identity and section lookup off them, so a fresh
    // id per rebuild — and this document IS rebuilt on every state change — would reset all three every keystroke.
    public const string PinsId = "v3.pins";
    public const string LikedId = "v3.liked";
    public const string LibraryId = "v3.library";

    /// <summary>The Liked Songs shortcut's route key and its item id inside the shortcut section.</summary>
    public const string LikedRouteKey = "liked";
    public const string LikedItemId = "v3.liked.item";

    /// <summary>Does V3's CHROME carry the fixed library destinations (Liked Songs / Albums / Artists / Podcasts /
    /// Local files, as one always-present nav-band strip)? It does — so this document stops emitting its own
    /// <c>v3.liked</c> row, which would be the same destination twice, two rows apart.
    /// <para>Kept as a named constant, not deleted code: the section/id/geometry below are still the right answer
    /// the moment the strip is turned off or moved.</para></summary>
    public static readonly bool ChromeCarriesDestinations = true;

    /// <summary>Build the ephemeral document for one V3 view state.</summary>
    /// <param name="topBar">The shell's shortcut band — the ONE global list Classic/Curated still materialise as a
    /// document section. V3 does NOT (its own band is fixed chrome, mounted above the header); this parameter
    /// survives for exactly one rule: a user who put Liked Songs in the band must not also get V3's own
    /// <c>v3.liked</c> row two places down (see <see cref="ChromeCarriesDestinations"/>).</param>
    public static SidebarCustomLayout Build(in LibraryV3DocState state,
        IReadOnlyList<SidebarItemSpec>? topBar = null)
    {
        var sections = new List<SidebarSectionSpec>(3);

        // 1 — the PIN BAND. The shaped projection already carries the surviving pins as its leading band (pin
        //     order, filter-aware); this section renders exactly that, which is also what gives V3 drop-to-pin and
        //     pin reordering it never had. Absent when nothing is pinned (V3's chrome does not budget for an empty
        //     drop-zone card) and at a drilled-in level (a folder level is that folder's contents, not the root).
        if (state.PinsBandVisible)
            sections.Add(new SidebarSectionSpec(PinsId, SidebarSectionKind.Pinned,
                Title: null, TitleLocKey: null,        // V3 renders NO section headers — no title ⇒ no header row
                Hidden: false, Collapsed: false,
                Display: ContentDisplay(in state)));

        // 2 — LIKED SONGS, the surface's own row: placed right after the pin band when not itself pinned, scoped
        //     to the lenses where a saved-songs shortcut is truthful. Dropped ALWAYS now that the chrome's
        //     destination strip carries Liked Songs unconditionally (#85 H4) — one destination, one row. Kept as a
        //     guarded branch rather than deleted so the section survives for the moment the strip is turned off.
        if (!ChromeCarriesDestinations &&
            state.LikedVisible && !SidebarShortcutsSection.ContainsRoute(topBar, LikedRouteKey))
            sections.Add(new SidebarSectionSpec(LikedId, SidebarSectionKind.StaticLinks,
                Title: null, TitleLocKey: null,
                Hidden: false, Collapsed: false,
                // A GLYPH row must be 44 tall with a 32-wide glyph column so its label lands at the content rows'
                // label x: Cozy + Subtitles=true selects 44/32 even though no subtitle text is ever drawn (a route
                // row never passes one). Fixed for every V3 view — Liked Songs is always exactly one row.
                Display: new SidebarDisplayOptions(
                    Density: SidebarDensity.Cozy,
                    Presentation: SidebarPresentation.List,
                    Artwork: false, Subtitles: true, CountBadges: false,
                    CollapsedByDefault: false, ShowInRail: true),
                Items: [new SidebarItemSpec(LikedItemId, SidebarItemTarget.Route, LikedRouteKey,
                                            IconOverride: "Heart")]));

        // 3 — THE ONE LIBRARY SECTION. Kind is the only thing that varies, for exactly one reason: only the
        //     PlaylistTree path stamps a row's nesting depth and preserves the given order verbatim; only the
        //     EntityList path can present a grid. See KindFor.
        var kind = KindFor(in state);
        sections.Add(new SidebarSectionSpec(LibraryId, kind,
            Title: null, TitleLocKey: null,
            Hidden: false, Collapsed: false,
            Display: ContentDisplay(in state) with
            {
                // The chrome owns V3's three actionable empty states (clear search / clear filter / create
                // playlist), so the pane must not also draw its own quiet one-line hint under them.
                EmptyBehavior = SidebarEmptyBehavior.HideBody,
            },
            // A PlaylistTree receives an already shaped, tree-regrouped sequence from LibraryV3View. Null is the
            // shared planner's explicit "preserve this rootlist order" contract; an honest query remains necessary
            // for the flat EntityList path (including the rail pass).
            Query: kind == SidebarSectionKind.PlaylistTree ? null : QueryFor(in state)));

        return new SidebarCustomLayout(TemplateId, sections);
    }

    /// <summary>Which section kind renders the library for this state.
    /// <para><b>PlaylistTree</b> when folders can appear inline — a LIST view, no search (search flattens), no
    /// drilled-in level (a level is one folder's direct children, already flat), a lens that contains playlists.
    /// The only path that stamps depth+entry.Depth (the indent) and emits rows in the given order unsorted.</para>
    /// <para><b>EntityList</b> otherwise — the only kind that honours a grid presentation, and for a flat lens
    /// (Albums/Artists/Podcasts), a search, or a drill level there is no nesting to express.</para></summary>
    public static SidebarSectionKind KindFor(in LibraryV3DocState state)
        => FoldersApply(in state) ? SidebarSectionKind.PlaylistTree : SidebarSectionKind.EntityList;

    /// <summary>The folder rule, made mechanical: folder rows exist only under the All/Playlists lenses, only in a
    /// list view, only with an empty search, and never at a drilled-in level.</summary>
    public static bool FoldersApply(in LibraryV3DocState state)
        => IsList(state.View) && !state.Searching && !state.Drilled
           && (state.Filter == (int)SidebarV3Filter.All || state.Filter == (int)SidebarV3Filter.Playlists);

    /// <summary>The display options every CONTENT band (pin band and library) shares — V3's view code, mapped once.
    /// CompactList ⇒ List+Compact (32 rows, 20 art, no subtitle) · List ⇒ List+Cozy (44/32/subtitle) · CompactGrid
    /// ⇒ Grid, no subtitle · Grid ⇒ Grid with a subtitle. Grid column count is derived from the pane width by the
    /// caller (the sidebar's cell size is derived, never chosen).</summary>
    public static SidebarDisplayOptions ContentDisplay(in LibraryV3DocState state) => new(
        Density: DensityFor(state.View),
        Presentation: PresentationFor(state.View),
        Artwork: true,
        Subtitles: SubtitlesFor(state.View),
        CountBadges: false,
        CollapsedByDefault: false,
        ShowInRail: true,
        MaxItems: 0,
        GridColumns: ClampColumns(state.GridColumns));

    public static SidebarPresentation PresentationFor(int view)
        => IsGrid(view) ? SidebarPresentation.Grid : SidebarPresentation.List;

    /// <summary>Compact only for the compact LIST; every other view is Cozy (a grid strip's cell height is
    /// measured, so its density only picks the art ladder the pane falls back to).</summary>
    public static SidebarDensity DensityFor(int view)
        => view == (int)SidebarV3View.CompactList ? SidebarDensity.Compact : SidebarDensity.Cozy;

    /// <summary>Only the two roomy views carry a second line. Compact density suppresses subtitles anyway; saying
    /// so here keeps the pane's height ladder honest (Cozy + subtitle = 44, Cozy alone = 40).</summary>
    public static bool SubtitlesFor(int view)
        => view == (int)SidebarV3View.List || view == (int)SidebarV3View.Grid;

    /// <summary>The section's query — the MIRROR of the shaping the projection already applied. Not a second
    /// filter (the rows handed to the planner are already the shaped V3 projection) — it still has to be right,
    /// because the RAIL plans from the same document (re-filtering/re-sorting an EntityList section for its tiles).
    /// <para>DIRECTION RECONCILIATION: V3's Descending flag means "reverse this sort's natural direction", while
    /// the query's Descending means "descending" literally, and the planner's comparator undoes that mapping again
    /// for the two recency modes. This inverts it for exactly those two, so the query round-trips to the same
    /// comparator the projection used.</para></summary>
    public static SidebarEntityQuery QueryFor(in LibraryV3DocState state)
    {
        var sort = SortFor(state.Sort, state.Filter);
        bool recency = sort is SidebarSortMode.Recents or SidebarSortMode.RecentlyAdded;
        return new SidebarEntityQuery(
            Kinds: KindsFor(state.Filter),
            Sort: sort,
            Descending: recency ? !state.Descending : state.Descending,
            Qualifier: QualifierFor(in state));
    }

    /// <summary>V3 chip → the query's kind set. Playlists includes folders (the app-side kind set maps Playlists
    /// onto the whole tree; the core mask has no folder bit).</summary>
    public static SidebarEntityKinds KindsFor(int filter) => filter switch
    {
        (int)SidebarV3Filter.Playlists => SidebarEntityKinds.Playlists,
        (int)SidebarV3Filter.Podcasts => SidebarEntityKinds.Shows,
        (int)SidebarV3Filter.Albums => SidebarEntityKinds.Albums,
        (int)SidebarV3Filter.Artists => SidebarEntityKinds.Artists,
        _ => SidebarEntityKinds.All,
    };

    /// <summary>V3 sort code → the query's sort mode: Custom order exists only under the Playlists lens and
    /// degrades to Alphabetical FOR DISPLAY everywhere else, exactly as the projection's own effective-sort rule
    /// does (the persisted preference is never rewritten here).</summary>
    public static SidebarSortMode SortFor(int sort, int filter)
    {
        if (sort == (int)SidebarV3Sort.Custom && filter != (int)SidebarV3Filter.Playlists)
            return SidebarSortMode.Alphabetical;
        return sort switch
        {
            (int)SidebarV3Sort.RecentlyAdded => SidebarSortMode.RecentlyAdded,
            (int)SidebarV3Sort.Alphabetical => SidebarSortMode.Alphabetical,
            (int)SidebarV3Sort.Creator => SidebarSortMode.Creator,
            (int)SidebarV3Sort.Custom => SidebarSortMode.CustomOrder,
            _ => SidebarSortMode.Recents,
        };
    }

    /// <summary>The qualifier the query carries. EFFECTIVE, not persisted: a qualifier the data cannot evidence and
    /// a qualifier outside the Playlists lens are both Any — the same two coercions the projection applied, so the
    /// mirror cannot filter MORE than the rows it describes.</summary>
    public static SidebarPlaylistQualifier QualifierFor(in LibraryV3DocState state)
    {
        if (!state.QualifiersAvailable || state.Filter != (int)SidebarV3Filter.Playlists)
            return SidebarPlaylistQualifier.Any;
        return state.Qualifier switch
        {
            (int)SidebarV3Qualifier.ByYou => SidebarPlaylistQualifier.ByYou,
            (int)SidebarV3Qualifier.BySpotify => SidebarPlaylistQualifier.BySpotify,
            (int)SidebarV3Qualifier.Mixed => SidebarPlaylistQualifier.Mixed,
            _ => SidebarPlaylistQualifier.Any,
        };
    }

    public static bool IsGrid(int view) => view >= (int)SidebarV3View.CompactGrid;
    public static bool IsList(int view) => view <= (int)SidebarV3View.List;

    /// <summary>The reducer clamps a persisted document's grid columns to [2,4]; a derived count must land in the
    /// same range, and a pane too narrow for two columns still gets two (the strip wraps rather than overflowing).</summary>
    public static int ClampColumns(int columns) => columns < 2 ? 2 : columns > 4 ? 4 : columns;
}

/// <summary>
/// The V3 view state the document is a function of — plain values only, so the synthesizer stays pure and
/// testable. Every member is what the mode component read from persisted preferences (or derived from the pane
/// width) on the render that built the document.
/// </summary>
/// <param name="Filter">A <see cref="SidebarV3Filter"/> code, already normalized.</param>
/// <param name="Qualifier">A <see cref="SidebarV3Qualifier"/> code, already normalized.</param>
/// <param name="Sort">A <see cref="SidebarV3Sort"/> code, already normalized.</param>
/// <param name="Descending">V3's direction flag — "reverse the sort's natural direction", NOT "descending".</param>
/// <param name="View">A <see cref="SidebarV3View"/> code, already normalized.</param>
/// <param name="GridColumns">The column count derived from the pane width (ignored by the list views).</param>
/// <param name="Searching">Whether the library-only search box holds a non-empty query (flattens the tree).</param>
/// <param name="DrillFolderId">The folder whose direct children are being listed, or null/"" at the library root.</param>
/// <param name="HasPins">Whether any pin SURVIVED the active lens (the projection's pin band is non-empty).</param>
/// <param name="LikedPinned">Whether Liked Songs is itself pinned — rendered as pin #n, never twice.</param>
/// <param name="QualifiersAvailable">Whether the data evidences ≥2 provenance classes.</param>
/// <param name="DragInFlight">Issue #85 (H1) — a Wavee resource drag is live ANYWHERE right now (read by the mode
/// root and folded in here rather than read from this pure struct — see <see cref="PinsBandVisible"/>).</param>
public readonly record struct LibraryV3DocState(
    int Filter = (int)SidebarV3Filter.All,
    int Qualifier = (int)SidebarV3Qualifier.Any,
    int Sort = (int)SidebarV3Sort.Recents,
    bool Descending = false,
    int View = (int)SidebarV3View.List,
    int GridColumns = 2,
    bool Searching = false,
    string? DrillFolderId = null,
    bool HasPins = false,
    bool LikedPinned = false,
    bool QualifiersAvailable = false,
    bool DragInFlight = false)
{
    /// <summary>At a drilled-in level the pane shows exactly one folder's direct children — no pin band, no shortcut.</summary>
    public bool Drilled => DrillFolderId is { Length: > 0 };

    /// <summary>Renders whenever pins survived the lens at the library root with no query — OR (issue #85, H1)
    /// while a drag is live anywhere, so pinning by drag is possible even on a fresh (zero-pin) install; the drop
    /// zone itself downgrades to its resting look for a drag that cannot be pinned. A search dissolves it
    /// regardless — search results are one flat relevance list, matching pins still lead it but not as a band.</summary>
    public bool PinsBandVisible => (HasPins || DragInFlight) && !Drilled && !Searching;

    /// <summary>The exact scope of the Liked Songs shortcut row: the unfiltered library and the Playlists lens,
    /// never while searching (a route row is not a search result), never when it is already a pin, never inside a
    /// folder.</summary>
    public bool LikedVisible
        => !LikedPinned && !Searching && !Drilled
           && (Filter == (int)SidebarV3Filter.All || Filter == (int)SidebarV3Filter.Playlists);
}

// ── the two built documents, named ───────────────────────────────────────────────────────────────────────────────────

public static partial class Sidebar
{
    /// <summary>Classic's document. Rebuilt from code on every read and never persisted — but CACHE it
    /// (<see cref="ClassicDocumentCache"/>): a freshly-minted document per render defeats the publish stage's
    /// reference check and turns every publish into a whole-window re-skin.</summary>
    public static SidebarCustomLayout ClassicDocument(bool pinnedOpen, bool libraryOpen, bool playlistsOpen,
                                                      IReadOnlyList<SidebarItemSpec>? topBar = null,
                                                      bool devTools = false)
        => SidebarBuiltInDocuments.Classic(pinnedOpen, libraryOpen, playlistsOpen, topBar, devTools);

    /// <summary>Library V3's document — ephemeral, read-only, synthesized from filter / qualifier / sort / view /
    /// search / drill. Its chrome is fixed and lives above the scroll surface, so none of that state ever moves,
    /// hides or reorders Home.</summary>
    public static SidebarCustomLayout V3Document(in LibraryV3DocState state,
                                                 IReadOnlyList<SidebarItemSpec>? topBar = null)
        => LibraryV3Document.Build(in state, topBar);
}
