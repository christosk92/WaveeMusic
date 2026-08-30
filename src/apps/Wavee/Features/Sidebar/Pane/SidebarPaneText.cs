using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Core.Sidebar;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The pane renderer's pure display rules: section titles, per-kind subtitles, the item lookup, the icon fallbacks and the
// "never render a blank row" degradations. Split out of the row slot so the row builders stay about LAYOUT.
//
// R3.0.1 rename: this was `CuratedText`, which was only ever "Curated's" by accident of where the renderer was born. It is
// the ONE pane renderer's text layer now, and Classic/V3 read it too.
//
// SINGLE-OWNER NOTE (resolved): SubtitleOf used to duplicate WaveeSidebar's private SubtitleForEntry table. Classic is a
// document over this renderer now, so its copy is DELETED and this is the only per-kind subtitle rule in the app.

static class SidebarPaneText
{
    /// <summary>A section's rendered title: the user's rename wins, then the template's loc key, then the kind's default
    /// (which for JumpBackIn follows its recents source — a "played" section reads "Recently played").</summary>
    public static string TitleOf(SidebarSectionSpec section)
    {
        if (section.Title is { Length: > 0 } title) return title;
        if (section.TitleLocKey is { Length: > 0 } key) return Loc.Get(key);
        var fallback = SidebarSectionKinds.DefaultTitleLocKey(section.Kind, section.Opts.Recents);
        return fallback is null ? "" : Loc.Get(fallback);
    }

    /// <summary>The hand-placed item a plan row was projected from, by the planner's join rule (a hand-placed row carries
    /// <c>Key == item.Key</c>, unique within its section). Also finds a Pinned OVERRIDE row's side-table entry, which is
    /// what makes an alias/icon override apply to a pinned row.
    ///
    /// <para>The implementation lives in the engine-free <see cref="SidebarRowResolve"/>, which also owns the SELECTION
    /// rule built on top of it — the pane's per-row selection sweep and the row's own <c>Selected</c> flag must resolve a
    /// row identically, so both live in one place. This stays as the renderer-side name every call site already uses.</para></summary>
    public static SidebarItemSpec? ItemOf(SidebarSectionSpec section, string key)
        => SidebarRowResolve.ItemOf(section, key);

    /// <summary>The per-kind subtitle (§3.1.3's table): playlist → song count · album → "Album · first artist" ·
    /// artist → "Artist" · show → "Podcast · publisher" · folder → item count · track → its artist.</summary>
    public static string? SubtitleOf(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.Playlist => Strings.Sidebar.SongCount(e.TrackCount),
        // A LIBRARY album carries its billed artist in FirstArtistName; a FEED album (a new release) carries it in
        // Creator, because the notification names one creator and never a full billing list.
        SidebarEntryKind.Album => Artist(in e) is { Length: > 0 } artist
            ? Loc.Get(Strings.Sidebar.V3.Kind.Album) + " · " + artist
            : Loc.Get(Strings.Sidebar.V3.Kind.Album),
        SidebarEntryKind.Artist => Loc.Get(Strings.Sidebar.V3.Kind.Artist),
        SidebarEntryKind.Show => e.Publisher.Length > 0
            ? Loc.Get(Strings.Sidebar.V3.Kind.Show) + " · " + e.Publisher
            : Loc.Get(Strings.Sidebar.V3.Kind.Show),
        SidebarEntryKind.Folder => Strings.Sidebar.V3.ItemCount(e.ChildCount),
        // A feed track (queue / now playing / artist top tracks) carries its primary artist in Creator.
        SidebarEntryKind.Track => e.Creator.Length > 0 ? e.Creator : null,
        // An APP-ROUTE row normally has no subtitle — except a CONCERT, which is projected as a route whose Creator is the
        // VENUE (§C1.8.5's venue subtitle). One rule, so no per-section special case is needed at the row site.
        SidebarEntryKind.AppRoute => e.Creator.Length > 0 ? e.Creator : null,
        _ => null,
    };

    static string Artist(in SidebarLibraryEntry e)
        => e.FirstArtistName.Length > 0 ? e.FirstArtistName : e.Creator;

    /// <summary>§C1.8.4's age badge ("3d") — how long ago a release landed, in the most useful unit. DIGITS + a unit
    /// letter, deliberately: no loc key exists for the compact form and this wave may not add one (see the HANDOFF).</summary>
    public static string? AgeBadge(long epochMs)
    {
        if (epochMs <= 0) return null;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long days = (nowMs - epochMs) / 86_400_000L;
        if (days < 0) return null;                       // a future stamp is not an "age"
        if (days < 1) return "1d";
        if (days < 7) return days.ToString(System.Globalization.CultureInfo.InvariantCulture) + "d";
        if (days < 60) return (days / 7).ToString(System.Globalization.CultureInfo.InvariantCulture) + "w";
        return (days / 30).ToString(System.Globalization.CultureInfo.InvariantCulture) + "mo";
    }

    /// <summary>§C1.8.5's date block: the event's day over its month, in the art slot. The month name comes from the OS
    /// culture's abbreviated month list, so it localizes without a key of its own.</summary>
    public static Element DateBlock(long epochMs, float size)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime();
        string day = when.Day.ToString(System.Globalization.CultureInfo.CurrentCulture);
        string month = when.ToString("MMM", System.Globalization.CultureInfo.CurrentCulture);
        return new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(SidebarCover.Radius(size, circular: false)),
            Fill = Tok.FillSubtleSecondary,
            Children =
            [
                new TextEl(day) { Size = size >= 32f ? 14f : 11f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                new TextEl(month) { Size = size >= 32f ? 9f : 8f, Color = Tok.TextTertiary, MaxLines = 1 },
            ],
        };
    }

    /// <summary>A readable last resort when nothing else names a row: the uri's / route key's final segment. Never blank
    /// and never a crash — a hand-edited document must always render something the user can right-click and remove.</summary>
    public static string ShortUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return "—";
        int at = uri.LastIndexOf(':');
        return at >= 0 && at + 1 < uri.Length ? uri[(at + 1)..] : uri;
    }

    /// <summary>The item's retained art (<c>FallbackImageUrl</c>, stamped every time it resolved), or null so the art slot
    /// falls back to its seeded placeholder tile rather than a hole.</summary>
    public static Image? FallbackImage(SidebarItemSpec? item)
        => item?.FallbackImageUrl is { Length: > 0 } url ? new Image(url) : null;

    /// <summary>An item's glyph, preferring its authored override. Null-item tolerant (a projected row has no item).</summary>
    public static string Glyph(SidebarItemSpec? item, string fallback)
        => item is null ? fallback : SidebarIcons.For(item, fallback);

    /// <summary>The natural mark for a PROJECTED row's family — the app-side twin of <c>SidebarIcons.ForEntityKind</c>
    /// over the projection's own kind enum (the two enums are deliberately distinct: one is persisted, one is not).</summary>
    public static string EntryGlyph(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => Icons.MusicNote,
        SidebarEntryKind.Album => Icons.Album,
        SidebarEntryKind.Artist => Icons.Contact,
        SidebarEntryKind.Show => Icons.RadioTower,
        SidebarEntryKind.Folder => Icons.Folder,
        SidebarEntryKind.AppRoute => Icons.Home,
        _ => Icons.MusicNote,
    };

    /// <summary>R3.1.6 — the EMPTY copy for a section that resolved to zero rows, per kind. It used to borrow
    /// <c>nav.history.empty.nothingHere</c> for everything, which read as a raw debug string in a navigation pane; the
    /// per-kind keys already exist in the catalog and say something useful.</summary>
    public static string EmptyText(SidebarSectionKind kind) => kind switch
    {
        SidebarSectionKind.JumpBackIn => Loc.Get(SidebarPaneLoc.SectionEmptyRecents),
        SidebarSectionKind.NewReleases => Loc.Get(SidebarPaneLoc.NewReleasesEmpty),
        SidebarSectionKind.Concerts => Loc.Get(SidebarPaneLoc.ConcertsEmpty),
        _ => Loc.Get(SidebarPaneLoc.SectionEmpty),
    };
}

/// <summary>The pane renderer's loc KEYS as literals, in one place.
///
/// <para>Deliberate, with precedent (<c>WaveeActionTargets</c>, the landed <c>CuratedLoc</c> this replaces): the generated
/// <c>Strings</c> members are used wherever a landed call site already proves the generated name, and the remaining keys
/// are spelled once here. Every key below EXISTS in <c>assets/loc/*.json</c>; a typo would render loudly as <c>[key]</c>
/// rather than silently.</para></summary>
static class SidebarPaneLoc
{
    public const string ExtensionManage = "sidebar.extension.manage";
    public const string ExtensionMissing = "sidebar.action.unavailable.missing";
    public const string ExtensionNotNow = "sidebar.action.unavailable.notNow";
    public const string ConcertsPrompt = "sidebar.concerts.locationPrompt";
    public const string ConcertsEmpty = "sidebar.concerts.empty";
    public const string NewReleasesEmpty = "sidebar.newReleases.empty";
    public const string MissingEntity = "sidebar.customizer.missingEntity";
    public const string RemoveItem = "sidebar.customizer.undo.removeItem";
    /// <summary>The row menu's Remove — the same word the customizer's item list already uses, so a shortcut removed
    /// from the pane and one removed from the inspector cannot disagree in translation.</summary>
    public const string ItemRemove = "sidebar.customizer.itemRemove";
    public const string PaneEmpty = "sidebar.customizer.empty";
    public const string PaneEmptySub = "sidebar.customizer.emptySub";
    /// <summary>R3.1.6: the generic empty line ("Nothing here yet"), replacing the borrowed
    /// <c>nav.history.empty.nothingHere</c>.</summary>
    public const string SectionEmpty = "sidebar.section.empty";
    /// <summary>R3.1.6: JumpBackIn's own copy ("Play something and it'll show up here").</summary>
    public const string SectionEmptyRecents = "sidebar.section.emptyRecents";
    public const string LibraryEmpty = "sidebar.v3.empty.library";
    public const string SearchEmpty = "sidebar.v3.empty.search";
    public const string SearchPlaceholder = "sidebar.v3.searchPlaceholder";
    public const string SortLabel = "sidebar.option.sort";
    public const string SortRecents = "sidebar.option.sortRecents";
    public const string SortRecentlyAdded = "sidebar.option.sortRecentlyAdded";
    public const string SortAlphabetical = "sidebar.option.sortAlphabetical";
    public const string SortCreator = "sidebar.option.sortCreator";
    public const string SortCustom = "sidebar.v3.sort.custom";
    public const string SortReversed = "sidebar.v3.sort.reversed";
    public const string ViewList = "sidebar.option.presentationList";
    public const string ViewGrid = "sidebar.option.presentationGrid";
    public const string FilterPlaylists = "sidebar.v3.filter.playlists";
    public const string FilterPodcasts = "sidebar.v3.filter.podcasts";
    public const string FilterAlbums = "sidebar.v3.filter.albums";
    public const string FilterArtists = "sidebar.v3.filter.artists";

    // ── PHASE 2 / Decision B — the customize canvas ───────────────────────────────────────────────────────────────────
    /// <summary>The section-options popover's title — the SAME string the docked inspector's header uses, because it is
    /// the same surface re-hosted, not a second one (P3).</summary>
    public const string EditOptions = "sidebar.customizer.properties";
    public const string EditHide = "sidebar.customizer.undo.hideSection";
    public const string EditShow = "sidebar.customizer.undo.showSection";
    public const string EditHidden = "sidebar.customizer.hidden";
    public const string EditMoveUp = "sidebar.customizer.moveUp";
    public const string EditMoveDown = "sidebar.customizer.moveDown";
    public const string EditRemove = "sidebar.customizer.undo.removeSection";
    public const string EditDuplicate = "sidebar.customizer.undo.duplicateSection";
    public const string EditDuplicateSuffix = "sidebar.customizer.duplicateSuffix";

    // PHASE 3 — the palette→canvas drop cues. Both are CONSTANT for the whole gesture (the card the pointer is over IS
    // the insertion point, so there is no slot number to interpolate), which is what keeps the caption resolvers inside
    // the 0-alloc frame region the dnd rules police.
    public const string EditDropHere = "sidebar.customizer.dropHere";
    public const string EditDropFull = "sidebar.customizer.dropFull";

    /// <summary>REUSED, not re-spelled: "{index} of {count}" already exists as the pinned reorder's position caption,
    /// and a second key for one sentence is how translations drift apart.</summary>
    public const string ReorderPosition = "sidebar.pin.position";
    public const string ReorderGrabbed = "sidebar.customizer.reorderGrabbed";
    public const string ReorderMoved = "sidebar.customizer.reorderMoved";
    public const string ReorderDropped = "sidebar.customizer.reorderDropped";
    public const string ReorderCancelled = "sidebar.customizer.reorderCancelled";
}

/// <summary>Renders an action descriptor's <see cref="IconRef"/> as a leading element. Mirrors the engine's own single
/// IconRef render path (a registered layered-vector name wins over the glyph, and a glyph keeps its own font override) —
/// which is internal to the control library, so this is the app-side equivalent rather than a duplicate of its rules.</summary>
static class SidebarPaneIcon
{
    public static Element? Leading(string? iconOverride, IconRef icon, bool enabled)
    {
        var color = enabled ? Tok.TextSecondary : Tok.TextTertiary;
        // An authored icon override is the user's explicit choice and beats the descriptor's own mark.
        if (iconOverride is { Length: > 0 } name && SidebarIcons.IsAllowed(name))
            return Icon(SidebarIcons.Glyph(name, Icons.MusicNote), 16f, color);
        if (icon.ThemedName is { Length: > 0 } themed && ThemedIconRegistry.Has(themed))
            return ThemedIcon.Create(themed, 16f);
        if (icon.Glyph is { Length: > 0 } glyph) return Icon(glyph, 16f, color, icon.Font);
        return Icon(Icons.MusicNote, 16f, color);
    }
}
