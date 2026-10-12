// ── Shell/Shell.Palette.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the command palette (Ctrl+K): table + filter + card. §2 previously said 2,400 for 492 lines of 0.2.9
//
// Role: UI (with a clearly separated PURE region — the table and its scoring — and a SHELL region, the dispatch)
// Owner: I (stage B, I2)
// Wave: 4
// Budget: 600 lines
// Spec: ch 19 §0 items 1-3, §2 W1-W3, §3, §5, §6.1, §8 (WaveeCommands), §9.1 items 1-2, §10 items 1-11d
//
// THREE REGIONS, in dependency order, so the pure half is testable without an engine (ShellPaletteTests):
//   1. PURE   — the builtin table (19 rows; declaration order IS the empty-query order), the registry merge, the
//               scoring ladder (prefix 0 · contains 1 · subsequence 2 · no match dropped), the bounded scored insert
//               into a CALLER-OWNED array, the `>` commands-only prefix and the "Search for …" row. No LINQ and no
//               per-keystroke allocation beyond the query's own lowercase copy.
//   2. SHELL  — invoking an entry, and the two shell verbs it lands on (theme, zoom) that had no 0.3 home.
//   3. UI     — the host of the structure's `Shell.CommandPalette` view (T4c): the props and the session. The lane (a ZStack
//               sibling, top pad 64, hit-test transparent), the popup's anchor and the card are `ClassicPalette.cs`.
//
// 0.2.9 names: `WaveeCommands` (the table) and `WaveeCommandPalette`/`WaveePaletteContent` (the card). Nothing here is
// named `Palette`: inside `Shell` a nested `Palette` would shadow `Wavee.Palette`, the cover-colour table.

using System;
using System.Collections.Generic;

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Input;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;

namespace Wavee;

public static partial class Shell
{
    // ══ 1. PURE — the command table and its keystroke filter ═════════════════════════════════════════════════════════

    public enum PaletteKind : byte { Navigate, Playback, Settings, Registry, CatalogSearch, Library, ThemeOption }
    public enum PalettePlaybackVerb : byte { PlayPause, Next, Previous, Shuffle, Repeat }
    public enum PaletteSettingsVerb : byte { ToggleTheme, ToggleCrossfade, ZoomIn, ZoomOut, ZoomReset, NpvTogglePresentation, NpvNextStyle, HomeFollowingOn, HomeFollowingOff, EditSidebar, SidebarClassic, SidebarLibrary }

    /// <summary>"New folder" lives here because the only other way to reach it is a right-click on a sidebar row — and
    /// a pane with no folders yet has no such row.</summary>
    public enum PaletteLibraryVerb : byte { NewPlaylist, NewFolder }

    /// <summary>One palette row. A class, not a struct: the filter writes REFERENCES into a reused array, so a keystroke
    /// copies nothing.</summary>
    public sealed class PaletteEntry
    {
        public string Id = "";
        public string Label = "";
        /// <summary>Lowercased ONCE at build time — never per keystroke.</summary>
        public string LabelLower = "";
        public string Glyph = "";
        public PaletteKind Kind;
        public RouteKind Destination;
        /// <summary>Home C1: the facet arg a <see cref="PaletteKind.Navigate"/> row to <see cref="RouteKind.Home"/>
        /// carries ("Home: Music/Podcasts/Audiobooks"). Empty for every other Navigate row (including "Home: All").</summary>
        public StringId NavArg;
        public PalettePlaybackVerb PlaybackVerb;
        public PaletteSettingsVerb SettingsVerb;
        public PaletteLibraryVerb LibraryVerb;
        /// <summary>The registry descriptor's fully-qualified key.</summary>
        public string? RegistryKey;
        public ActionTargetMode RegistryTarget;
        public string? CatalogQuery;
        /// <summary>What a <see cref="PaletteKind.ThemeOption"/> row does: the active theme's option applies itself (themes plan 8.2).</summary>
        public Action? Run;
    }

    public static class CommandTable
    {
        /// <summary>The list never scrolls and never exceeds eight rows (ch 19 §0 item 3).</summary>
        public const int MaxResults = 8;

        /// <summary>Builtin rows; registry rows are appended after them.</summary>
        public const int BuiltinCount = 29;

        public const string CatalogId = "search.query";

        /// <summary>The builtin table, labels localised at this edge (once per open). ORDER IS CONTRACT: nav first —
        /// the empty query shows the first eight exactly as declared (W1). `nav.library` lands on <b>liked</b>, and the
        /// three zoom rows borrow Add/Remove/Undo because the bundled set has no zoom glyph.</summary>
        public static PaletteEntry[] Builtins() =>
        [
            NavRow("nav.home", Loc.Get(Strings.Nav.Home), Icons.Home, RouteKind.Home),
            NavRow("nav.search", Loc.Get(Strings.Nav.Search), Icons.Search, RouteKind.Search),
            NavRow("nav.library", Loc.Get(Strings.Nav.YourLibrary), Icons.MusicNote, RouteKind.Liked),
            NavRow("nav.recents", Loc.Get(Strings.Nav.Recents), Icons.Headphones, RouteKind.Recents),
            NavRow("nav.settings", Loc.Get(Strings.Nav.Settings), Icons.Settings, RouteKind.Settings),
            PlayRow("playback.playPause", Loc.Get(Strings.Detail.Play), Icons.Play, PalettePlaybackVerb.PlayPause),
            PlayRow("playback.next", Loc.Get(Strings.Player.Next), Icons.Next, PalettePlaybackVerb.Next),
            PlayRow("playback.previous", Loc.Get(Strings.Player.Previous), Icons.Previous, PalettePlaybackVerb.Previous),
            PlayRow("playback.shuffle", Loc.Get(Strings.Player.Shuffle), Icons.Shuffle, PalettePlaybackVerb.Shuffle),
            PlayRow("playback.repeat", Loc.Get(Strings.Player.Repeat), Icons.RepeatAll, PalettePlaybackVerb.Repeat),
            SetRow("settings.theme", Loc.Get(Strings.Settings.Appearance.Theme), Icons.Brush, PaletteSettingsVerb.ToggleTheme),
            SetRow("settings.crossfade", Loc.Get(Strings.Settings.Sound.Crossfade), Icons.MusicNote, PaletteSettingsVerb.ToggleCrossfade),
            SetRow("settings.zoomIn", Loc.Get(Strings.Settings.Appearance.ZoomIn), Icons.Add, PaletteSettingsVerb.ZoomIn),
            SetRow("settings.zoomOut", Loc.Get(Strings.Settings.Appearance.ZoomOut), Icons.Remove, PaletteSettingsVerb.ZoomOut),
            SetRow("settings.zoomReset", Loc.Get(Strings.Settings.Appearance.ZoomReset), Icons.Undo, PaletteSettingsVerb.ZoomReset),
            SetRow("settings.npvPresentation", Loc.Get(Strings.Player.PresentationToggle), Icons.Picture, PaletteSettingsVerb.NpvTogglePresentation),
            SetRow("settings.npvNextStyle", Loc.Get(Strings.Player.PlayerStyleNext), Icons.Album, PaletteSettingsVerb.NpvNextStyle),
            SetRow("sidebar.edit", Loc.Get("sidebar.menu.edit"), Icons.Edit, PaletteSettingsVerb.EditSidebar),
            SetRow("sidebar.classic", Loc.Format("sidebar.palette.useLayout", ("layout", Loc.Get("sidebar.layoutName.classic"))), Icons.List, PaletteSettingsVerb.SidebarClassic),
            SetRow("sidebar.library", Loc.Format("sidebar.palette.useLayout", ("layout", Loc.Get("sidebar.layoutName.library"))), Icons.Library, PaletteSettingsVerb.SidebarLibrary),
            LibRow("library.newPlaylist", Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, PaletteLibraryVerb.NewPlaylist),
            LibRow("library.newFolder", Loc.Get(Strings.Sidebar.CreateFolder), Icons.Folder, PaletteLibraryVerb.NewFolder),
            // Home C1 (facet history + deep link, plan "Palette commands"): the four facet chip ids are the ones the
            // fake dataset and the live server both seed (Home/Facets.cs's FacetRoute is the same id space).
            HomeFacetRow("home.all", Loc.Get(Strings.Palette.HomeAll), Icons.Home, ""),
            HomeFacetRow("home.music", Loc.Get(Strings.Palette.HomeMusic), Icons.MusicNote, "music-chip"),
            HomeFacetRow("home.podcasts", Loc.Get(Strings.Palette.HomePodcasts), Icons.RadioTower, "podcasts-chip"),
            HomeFacetRow("home.audiobooks", Loc.Get(Strings.Palette.HomeAudiobooks), Icons.Microphone, "audiobooks-chip"),
            SetRow("home.followingOn", Loc.Get(Strings.Palette.HomeFollowingOn), Icons.Friends, PaletteSettingsVerb.HomeFollowingOn),
            SetRow("home.followingOff", Loc.Get(Strings.Palette.HomeFollowingOff), Icons.Friends, PaletteSettingsVerb.HomeFollowingOff),
            NavRow("home.customize", Loc.Get(Strings.Home.Customizer.Title), Icons.Edit, RouteKind.HomeCustomize),
        ];

        /// <summary>A fresh index for one open: the builtins, then the active theme's option commands
        /// (<paramref name="themeCommands"/>: the options that asked for a palette row), then every registry action that
        /// accepts a target the palette can supply (now playing, the active page, or none). <paramref name="glyphOf"/>
        /// resolves a descriptor's semantic icon key; null or an unknown key falls back to <c>Icons.More</c>.</summary>
        public static PaletteEntry[] BuildIndex(IReadOnlyList<ActionDescriptor>? registryActions, Func<string, string?>? glyphOf,
            IReadOnlyList<Theming.ThemeCommand>? themeCommands = null)
        {
            var builtins = Builtins();
            int themeCount = themeCommands?.Count ?? 0;
            int extra = 0;
            if (registryActions is not null)
                for (int i = 0; i < registryActions.Count; i++)
                    if (TargetOf(registryActions[i].AcceptedTargets, out _)) extra++;
            if (extra == 0 && themeCount == 0) return builtins;

            var all = new PaletteEntry[builtins.Length + themeCount + extra];
            Array.Copy(builtins, all, builtins.Length);
            int w = builtins.Length;
            for (int i = 0; i < themeCount; i++)
            {
                var command = themeCommands![i];
                all[w++] = new PaletteEntry
                {
                    Id = command.Id, Label = command.Label, LabelLower = command.Label.ToLowerInvariant(), Glyph = Icons.Brush,
                    Kind = PaletteKind.ThemeOption, Run = command.Run,
                };
            }
            for (int i = 0; registryActions is not null && i < registryActions.Count; i++)
            {
                var d = registryActions[i];
                if (!TargetOf(d.AcceptedTargets, out var target)) continue;
                string label = Loc.Get(d.LabelLocKey);
                all[w++] = new PaletteEntry
                {
                    Id = d.Key, Label = label, LabelLower = label.ToLowerInvariant(),
                    Glyph = glyphOf?.Invoke(d.IconKey) is { Length: > 0 } g ? g : Icons.More,
                    Kind = PaletteKind.Registry, RegistryKey = d.Key, RegistryTarget = target,
                };
            }
            return all;
        }

        /// <summary>Which target a palette invocation supplies, in preference order NowPlaying → ActiveRoute → None.
        /// False = the descriptor needs a picked entity and earns no palette row.</summary>
        public static bool TargetOf(ActionTargetModes accepted, out ActionTargetMode target)
        {
            if ((accepted & ActionTargetModes.NowPlaying) != 0) { target = ActionTargetMode.NowPlaying; return true; }
            if ((accepted & ActionTargetModes.ActiveRoute) != 0) { target = ActionTargetMode.ActiveRoute; return true; }
            if ((accepted & ActionTargetModes.None) != 0) { target = ActionTargetMode.None; return true; }
            target = ActionTargetMode.None;
            return false;
        }

        /// <summary>The reusable "Search for …" row, allocated once per open.</summary>
        public static PaletteEntry NewCatalogScratch()
            => new() { Id = CatalogId, Glyph = Icons.Search, Kind = PaletteKind.CatalogSearch };

        /// <summary>Scan <paramref name="index"/> into <paramref name="dest"/> (length ≥ <see cref="MaxResults"/>) and
        /// return the hit count. A query WITHOUT <c>&gt;</c> is trimmed (a pasted "  pl\n" still matches) and, when
        /// room remains, gets the catalog row LAST; a query WITH <c>&gt;</c> skips only the spaces right after it and
        /// never gets the catalog row.</summary>
        public static int Filter(PaletteEntry[] index, string query, PaletteEntry[] dest, PaletteEntry catalogScratch)
        {
            bool commandsOnly = false;
            string q = query ?? "";
            if (q.Length > 0 && q[0] == '>')
            {
                commandsOnly = true;
                int start = 1;
                while (start < q.Length && q[start] == ' ') start++;
                q = q.Substring(start);
            }
            else if (q.Length > 0)
            {
                q = q.Trim();
            }

            string qLower = q.Length == 0 ? "" : q.ToLowerInvariant();
            int written = 0;
            Span<int> scores = stackalloc int[MaxResults];
            for (int i = 0; i < index.Length; i++)
            {
                var e = index[i];
                int s = qLower.Length == 0 ? 0 : ScoreOf(e.LabelLower, qLower);
                if (s < 0) continue;
                InsertScored(dest, scores, ref written, e, s);
            }

            if (!commandsOnly && q.Length > 0 && written < MaxResults)
            {
                catalogScratch.CatalogQuery = q;
                catalogScratch.Label = Strings.Palette.SearchFor(q);
                catalogScratch.LabelLower = catalogScratch.Label.ToLowerInvariant();
                dest[written++] = catalogScratch;
            }
            return written;
        }

        /// <summary>Lower is better: prefix 0 · contains 1 · subsequence 2 · no match −1. Both sides pre-lowercased.</summary>
        public static int ScoreOf(string labelLower, string queryLower)
        {
            int at = labelLower.IndexOf(queryLower, StringComparison.Ordinal);
            if (at == 0) return 0;
            if (at > 0) return 1;
            return IsSubsequence(labelLower, queryLower) ? 2 : -1;
        }

        public static bool IsSubsequence(string labelLower, string queryLower)
        {
            int j = 0;
            for (int i = 0; i < labelLower.Length && j < queryLower.Length; i++)
                if (labelLower[i] == queryLower[j]) j++;
            return j == queryLower.Length;
        }

        /// <summary>The bounded linear insert. Ties keep DECLARATION order: an equal score inserts AFTER its peers (the
        /// scan steps back only past STRICTLY worse scores).</summary>
        static void InsertScored(PaletteEntry[] dest, Span<int> scores, ref int written, PaletteEntry e, int score)
        {
            if (written == MaxResults && score >= scores[written - 1]) return;
            int at = written < MaxResults ? written : MaxResults - 1;
            while (at > 0 && scores[at - 1] > score) at--;
            int last = written < MaxResults ? written : MaxResults - 1;
            for (int i = last; i > at; i--)
            {
                dest[i] = dest[i - 1];
                scores[i] = scores[i - 1];
            }
            dest[at] = e;
            scores[at] = score;
            if (written < MaxResults) written++;
        }

        static PaletteEntry NavRow(string id, string label, string glyph, RouteKind route) => new()
            { Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph, Kind = PaletteKind.Navigate, Destination = route };

        /// <summary>A Navigate row onto <see cref="RouteKind.Home"/> carrying a facet arg (Home C1) — same Kind as
        /// <see cref="NavRow"/>, just with <see cref="PaletteEntry.NavArg"/> set.</summary>
        static PaletteEntry HomeFacetRow(string id, string label, string glyph, string facetArg) => new()
            { Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph, Kind = PaletteKind.Navigate,
              Destination = RouteKind.Home, NavArg = facetArg.Length == 0 ? default : Entities.Strings.Intern(facetArg) };

        static PaletteEntry PlayRow(string id, string label, string glyph, PalettePlaybackVerb verb) => new()
            { Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph, Kind = PaletteKind.Playback, PlaybackVerb = verb };

        static PaletteEntry SetRow(string id, string label, string glyph, PaletteSettingsVerb verb) => new()
            { Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph, Kind = PaletteKind.Settings, SettingsVerb = verb };

        static PaletteEntry LibRow(string id, string label, string glyph, PaletteLibraryVerb verb) => new()
            { Id = id, Label = label, LabelLower = label.ToLowerInvariant(), Glyph = glyph, Kind = PaletteKind.Library, LibraryVerb = verb };
    }

    // ══ 2. SHELL — invoking an entry ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>"New playlist" — owner O's create flow installs it (Wave 5). Null = the row does nothing.</summary>
    public static Action? OnNewPlaylist;

    /// <summary>"New folder" — owner J's folder command installs it. Null = the row does nothing.</summary>
    public static Action? OnNewFolder;

    /// <summary>App zoom, one ladder rung per call (0 = reset to 100 %). The ONE zoom verb: the Ctrl±/0 chords, the
    /// Ctrl+wheel hook and the palette rows land here. Persistence is the host's debounced zoom save, never here, so a
    /// held chord does not write the settings store once per rung. A step that would take the layout under the Compact floor
    /// on this window is refused (<see cref="ZoomService.ManualStep"/>, plan 3.2).</summary>
    public static void ZoomStep(int direction)
        => ZoomService.Apply(ZoomService.ManualStep(FluentApp.Zoom, direction, ZoomService.WindowBaseWidth));

    static void InvokePaletteEntry(PaletteEntry e)
    {
        switch (e.Kind)
        {
            case PaletteKind.Navigate:
                GoTo(new Route(e.Destination, Arg: e.NavArg));
                break;
            case PaletteKind.CatalogSearch:
                GoTo(Parse("search", e.CatalogQuery ?? ""));
                break;
            case PaletteKind.Playback:
                switch (e.PlaybackVerb)
                {
                    case PalettePlaybackVerb.PlayPause: PlayerVerbs.Default.TogglePlay("palette"); break;
                    case PalettePlaybackVerb.Next: Playback.Next(); break;
                    case PalettePlaybackVerb.Previous: Playback.Previous(); break;
                    case PalettePlaybackVerb.Shuffle: PlayerVerbs.Default.ToggleShuffle(); break;
                    case PalettePlaybackVerb.Repeat: PlayerVerbs.Default.CycleRepeat(); break;
                }
                break;
            case PaletteKind.Settings:
                switch (e.SettingsVerb)
                {
                    case PaletteSettingsVerb.ToggleTheme: Appearance.ToggleMode(); break;
                    case PaletteSettingsVerb.ToggleCrossfade:
                        bool on = !Platform.Settings.Get(Platform.Keys.CrossfadeEnabled);
                        Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, on);
                        Playback.Audio.SetCrossfade(on, Platform.Settings.Get(Platform.Keys.CrossfadeMs));
                        break;
                    case PaletteSettingsVerb.ZoomIn: ZoomStep(+1); break;
                    case PaletteSettingsVerb.ZoomOut: ZoomStep(-1); break;
                    case PaletteSettingsVerb.ZoomReset: ZoomStep(0); break;
                    case PaletteSettingsVerb.NpvTogglePresentation:
                        DeckPrefs.TogglePresentation(DeckPrefSource.Palette);
                        break;
                    case PaletteSettingsVerb.NpvNextStyle:
                        DeckPrefs.NextStyle(DeckPrefSource.Palette);
                        break;
                    case PaletteSettingsVerb.HomeFollowingOn:
                    case PaletteSettingsVerb.HomeFollowingOff:
                        InvokeHomeFollowing(on: e.SettingsVerb == PaletteSettingsVerb.HomeFollowingOn);
                        break;
                    case PaletteSettingsVerb.EditSidebar:
                        Sidebar.EnterEdit();
                        break;
                    case PaletteSettingsVerb.SidebarClassic:
                    case PaletteSettingsVerb.SidebarLibrary:
                        // Q7: a layout switch is refused while editing; the palette says why instead of doing nothing.
                        if (Sidebar.Editing.Peek())
                        {
                            Notify.Say(Loc.Get("sidebar.pane.finishEditing"), InfoBarSeverity.Informational, dedupeKey: "sidebar.pane.finish-editing");
                            break;
                        }
                        Sidebar.SwitchLayout(e.SettingsVerb == PaletteSettingsVerb.SidebarLibrary ? SidebarLayoutId.Library : SidebarLayoutId.Classic);
                        break;
                }
                break;
            case PaletteKind.Library:
                (e.LibraryVerb == PaletteLibraryVerb.NewPlaylist ? OnNewPlaylist : OnNewFolder)?.Invoke();
                break;
            case PaletteKind.ThemeOption:
                e.Run?.Invoke();
                break;
            case PaletteKind.Registry:
                if (e.RegistryKey is not { Length: > 0 } key) return;
                var binding = new ActionBinding(Actions.Key.PublisherOf(key), key, e.RegistryTarget);
                Actions.Registry.Current?.Execute(Actions.Services, in binding);
                break;
        }
    }

    /// <summary>"Home: Following on/off" (Home C1). Reads the ACTIVE tab's own current route — the palette always
    /// acts on the tab it was opened over — and, only when it is currently on Home and that facet word carries a
    /// Following sub-chip (Music/Podcasts), navigates to the sub-chip id (on) or the plain word id (off). A no-op
    /// everywhere else (not on Home, or on All/Audiobooks, which have no sub-chip) — same "disabled word" shape
    /// <c>FacetPivot.Enabled</c> already gives the pivot itself.</summary>
    static void InvokeHomeFollowing(bool on)
    {
        var cur = Current.Peek();
        if (cur.Kind != RouteKind.Home) return;
        string arg = Entities.Strings.Resolve(cur.Arg);
        // The fake dataset and the live server both use this <word>-chip / <word>-following-chip pairing
        // (Home/Facets.cs's FacetPivot; Entities/Entities.Fake.Home.cs's SeedHomeFacets).
        string? target = arg switch
        {
            "music-chip" => on ? "music-following-chip" : "music-chip",
            "music-following-chip" => on ? "music-following-chip" : "music-chip",
            "podcasts-chip" => on ? "podcasts-following-chip" : "podcasts-chip",
            "podcasts-following-chip" => on ? "podcasts-following-chip" : "podcasts-chip",
            _ => null,   // All / Audiobooks / anything else: no Following sub-chip
        };
        if (target is not null) GoTo(new Route(RouteKind.Home, Arg: Entities.Strings.Intern(target)));
    }

    // ══ 3. UI — the host of the structure's view ═══════════════════════════════════════════════════════════════════

    /// <summary>The descriptor glyph resolver — the action platform's ONE semantic-key map (<c>Actions.UI.cs</c>).</summary>
    static readonly Func<string, string?> s_paletteGlyphOf = static key => ActionIcons.Resolve(key).Glyph;

    /// <summary>The palette's props (T4c): the popup control wired to <see cref="PaletteOpen"/> (a controlled signal; light-dismiss and
    /// a focus trap), the session factory and the close verb. Cached statics, so an equal record skips the template.</summary>
    static readonly PaletteProps s_paletteProps = new()
    {
        Popup = static (anchor, content) => Popup.Create(
            anchor, content,
            isOpen: PaletteOpen,
            placement: FlyoutPlacement.BottomEdgeAlignedLeft,
            options: new PopupOptions(FocusTrap: true, Chrome: PopupChrome.Popup)),
        NewSession = static () => new PaletteSession(),
        Close = static () => PaletteOpen.Value = false,
    };

    // MOUNT POINT (stage B contract)
    /// <summary>The command palette: the structure's <c>Shell.CommandPalette</c> view (T4c), which draws the lane (a hit-test-transparent
    /// ZStack sibling whose top pad puts the card's top edge exactly 64 DIP down, centred, a hard 560 wide with no clamp, W1) and the
    /// card. Open state is <see cref="PaletteOpen"/>, which Ctrl+K TOGGLES (the frame's chord). Because the lane is a sibling rather
    /// than an overlay entry, the frame's own Escape handler must bail while it is open.</summary>
    public static Element CommandPalette() => ViewSlots.View(ViewKeys.ShellCommandPalette, s_paletteProps);

    /// <summary>One open palette (the card's session): the command index, the result array and the catalog row, allocated once per
    /// mount (ch 19 §9.1 item 2). The card mounts FRESH per open, so the session does too.</summary>
    sealed class PaletteSession : IPaletteSession
    {
        readonly PaletteEntry[] _index = CommandTable.BuildIndex(Actions.Registry.Current?.Actions, s_paletteGlyphOf,
            Theming.ThemeOptionsSection.Commands(Platform.Developer.Enabled.Peek()));
        readonly PaletteEntry[] _hits = new PaletteEntry[CommandTable.MaxResults];
        readonly PaletteEntry _scratch = CommandTable.NewCatalogScratch();
        int _count;

        public int Filter(string query) => _count = CommandTable.Filter(_index, query, _hits, _scratch);

        public string GlyphAt(int index) => _hits[index].Glyph;

        public string LabelAt(int index) => _hits[index].Label;

        public void Invoke(int index)
        {
            if ((uint)index >= (uint)_count) return;
            var entry = _hits[index];
            PaletteOpen.Value = false;
            InvokePaletteEntry(entry);
        }
    }
}
