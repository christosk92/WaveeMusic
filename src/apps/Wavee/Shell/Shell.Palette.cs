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
//   3. UI     — the lane (a ZStack sibling, top pad 64, hit-test transparent), the popup and the card.
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

namespace Wavee;

public static partial class Shell
{
    // ══ 1. PURE — the command table and its keystroke filter ═════════════════════════════════════════════════════════

    public enum PaletteKind : byte { Navigate, Playback, Settings, Registry, CatalogSearch, Library }
    public enum PalettePlaybackVerb : byte { PlayPause, Next, Previous, Shuffle, Repeat }
    public enum PaletteSettingsVerb : byte { ToggleTheme, ToggleCrossfade, ZoomIn, ZoomOut, ZoomReset, NpvTogglePresentation, NpvNextStyle }

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
        public PalettePlaybackVerb PlaybackVerb;
        public PaletteSettingsVerb SettingsVerb;
        public PaletteLibraryVerb LibraryVerb;
        /// <summary>The registry descriptor's fully-qualified key.</summary>
        public string? RegistryKey;
        public ActionTargetMode RegistryTarget;
        public string? CatalogQuery;
    }

    public static class CommandTable
    {
        /// <summary>The list never scrolls and never exceeds eight rows (ch 19 §0 item 3).</summary>
        public const int MaxResults = 8;

        /// <summary>Builtin rows; registry rows are appended after them.</summary>
        public const int BuiltinCount = 19;

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
            LibRow("library.newPlaylist", Loc.Get(Strings.Detail.NewPlaylist), Icons.Add, PaletteLibraryVerb.NewPlaylist),
            LibRow("library.newFolder", Loc.Get(Strings.Sidebar.CreateFolder), Icons.Folder, PaletteLibraryVerb.NewFolder),
        ];

        /// <summary>A fresh index for one open: the builtins, then every registry action that accepts a target the
        /// palette can supply (now playing, the active page, or none). <paramref name="glyphOf"/> resolves a
        /// descriptor's semantic icon key; null or an unknown key falls back to <c>Icons.More</c>.</summary>
        public static PaletteEntry[] BuildIndex(IReadOnlyList<ActionDescriptor>? registryActions, Func<string, string?>? glyphOf)
        {
            var builtins = Builtins();
            if (registryActions is null || registryActions.Count == 0) return builtins;
            int extra = 0;
            for (int i = 0; i < registryActions.Count; i++)
                if (TargetOf(registryActions[i].AcceptedTargets, out _)) extra++;
            if (extra == 0) return builtins;

            var all = new PaletteEntry[builtins.Length + extra];
            Array.Copy(builtins, all, builtins.Length);
            int w = builtins.Length;
            for (int i = 0; i < registryActions.Count; i++)
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

    /// <summary>Flip light ⇄ dark IN PLACE over the engine's theme transition and persist the explicit mode. The ONE
    /// theme-flip verb: the palette row, the profile menu's Theme row and any chord call it.</summary>
    public static void ToggleTheme(Action<float>? requestTransition)
    {
        var next = Tok.Theme == ThemeKind.Dark ? ThemeKind.Light : ThemeKind.Dark;
        if (SeedPalette is { } seed) seed(next); else Tok.Use(next);   // the installed Wavee palette, not the engine's
        Platform.Settings.Set(Platform.Keys.ThemeMode, next == ThemeKind.Dark ? 2 : 1);
        requestTransition?.Invoke(Design.Motion.Standard);
    }

    /// <summary>App zoom, one ladder rung per call (0 = reset to 100 %). The ONE zoom verb: the Ctrl±/0 chords, the
    /// Ctrl+wheel hook and the palette rows land here. Persistence is the host's debounced zoom save, never here, so a
    /// held chord does not write the settings store once per rung.</summary>
    public static void ZoomStep(int direction)
        => FluentApp.SetZoom(direction == 0 ? ZoomLadder.Default
            : direction > 0 ? ZoomLadder.In(FluentApp.Zoom) : ZoomLadder.Out(FluentApp.Zoom));

    static void InvokePaletteEntry(PaletteEntry e, Action<float>? requestTheme)
    {
        switch (e.Kind)
        {
            case PaletteKind.Navigate:
                GoTo(new Route(e.Destination));
                break;
            case PaletteKind.CatalogSearch:
                GoTo(Parse("search", e.CatalogQuery ?? ""));
                break;
            case PaletteKind.Playback:
                switch (e.PlaybackVerb)
                {
                    case PalettePlaybackVerb.PlayPause: TogglePlayPause(); break;
                    case PalettePlaybackVerb.Next: Playback.Next(); break;
                    case PalettePlaybackVerb.Previous: Playback.Previous(); break;
                    case PalettePlaybackVerb.Shuffle: ToggleShuffle(); break;
                    case PalettePlaybackVerb.Repeat: CycleRepeat(); break;
                }
                break;
            case PaletteKind.Settings:
                switch (e.SettingsVerb)
                {
                    case PaletteSettingsVerb.ToggleTheme: ToggleTheme(requestTheme); break;
                    case PaletteSettingsVerb.ToggleCrossfade:
                        bool on = !Platform.Settings.Get(Platform.Keys.CrossfadeEnabled);
                        Platform.Settings.Set(Platform.Keys.CrossfadeEnabled, on);
                        Playback.Audio.SetCrossfade(on, Platform.Settings.Get(Platform.Keys.CrossfadeMs));
                        break;
                    case PaletteSettingsVerb.ZoomIn: ZoomStep(+1); break;
                    case PaletteSettingsVerb.ZoomOut: ZoomStep(-1); break;
                    case PaletteSettingsVerb.ZoomReset: ZoomStep(0); break;
                    case PaletteSettingsVerb.NpvTogglePresentation:
                        Rail.PlayerPrefs.TogglePresentation(Rail.NpvDiagnostics.SourcePalette);
                        break;
                    case PaletteSettingsVerb.NpvNextStyle:
                        Rail.PlayerPrefs.NextStyle(Rail.NpvDiagnostics.SourcePalette);
                        break;
                }
                break;
            case PaletteKind.Library:
                (e.LibraryVerb == PaletteLibraryVerb.NewPlaylist ? OnNewPlaylist : OnNewFolder)?.Invoke();
                break;
            case PaletteKind.Registry:
                if (e.RegistryKey is not { Length: > 0 } key) return;
                var binding = new ActionBinding(Actions.Key.PublisherOf(key), key, e.RegistryTarget);
                Actions.Registry.Current?.Execute(Actions.Services, in binding);
                break;
        }
    }

    // ══ 3. UI — the lane, the popup, the card ═══════════════════════════════════════════════════════════════════════

    const float PaletteCardW = 560f, PaletteCardMaxH = 460f;

    /// <summary>The descriptor glyph resolver — the action platform's ONE semantic-key map (<c>Actions.UI.cs</c>).</summary>
    static readonly Func<string, string?> s_paletteGlyphOf = static key => ActionIcons.Resolve(key).Glyph;

    // MOUNT POINT (stage B contract)
    /// <summary>The command palette's lane: a hit-test-transparent ZStack sibling whose top pad puts the card's top edge
    /// exactly 64 DIP down, centred, a hard 560 wide with no clamp (W1). Open state is <see cref="PaletteOpen"/>, which
    /// Ctrl+K TOGGLES (the frame's chord). Because this is a sibling lane rather than an overlay entry, the frame's own
    /// Escape handler must bail while it is open.</summary>
    public static Element CommandPalette()
        => new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, HitTestVisible = false,
            Padding = new Edges4(0f, Spacing.XXXL * 2f, 0f, 0f),
            Children =
            [
                Popup.Create(
                    new BoxEl { Width = PaletteCardW, Height = 0f },
                    content: static () => Embed.Comp(static () => new PaletteCard()),
                    isOpen: PaletteOpen,
                    placement: FlyoutPlacement.BottomEdgeAlignedLeft,
                    options: new PopupOptions(FocusTrap: true, Chrome: PopupChrome.Popup)),
            ],
        };

    /// <summary>The card body. Popup mounts it FRESH per open, so the query and the selection reset for free; the index,
    /// the result array and the catalog row are allocated once per mount (ch 19 §9.1 item 2).</summary>
    sealed class PaletteCard : Component
    {
        public override Element Render()
        {
            var query = UseSignal("");
            var sel = UseSignal(0);            // UNCLAMPED; the per-render clamp below is the second tier (§9.1 item 1)
            var index = UseRef<PaletteEntry[]?>(null);
            var dest = UseRef<PaletteEntry[]?>(null);
            var scratch = UseRef<PaletteEntry?>(null);
            var announced = UseRef(false);
            var requestTheme = UseContext(ThemeControl.Request);

            index.Value ??= CommandTable.BuildIndex(Actions.Registry.Current?.Actions, s_paletteGlyphOf);
            dest.Value ??= new PaletteEntry[CommandTable.MaxResults];
            scratch.Value ??= CommandTable.NewCatalogScratch();

            var hits = dest.Value;
            string q = query.Value;
            int count = CommandTable.Filter(index.Value, q, hits, scratch.Value);
            int selected = count == 0 ? 0 : Math.Clamp(sel.Value, 0, count - 1);

            // "Command palette" once on open, then the throttled count on every edit (W3).
            UseEffect(() =>
            {
                if (!Announcer.IsAvailable) return;
                if (!announced.Value)
                {
                    announced.Value = true;
                    Announcer.Say(Loc.Get(Strings.Palette.Title));
                }
                Announcer.SayThrottled(count == 0 ? Loc.Get(Strings.Palette.NoMatches) : Strings.Palette.Matches(count));
            }, (DepKey)q);

            void Commit(int i)
            {
                if ((uint)i >= (uint)count) return;
                var entry = hits[i];
                PaletteOpen.Value = false;
                InvokePaletteEntry(entry, requestTheme);
            }

            void OnKey(KeyEventArgs e)
            {
                switch (e.KeyCode)
                {
                    case Keys.Down:
                        if (count > 0) sel.Value = (selected + 1) % count;
                        e.Handled = true;
                        break;
                    case Keys.Up:
                        if (count > 0) sel.Value = (selected - 1 + count) % count;
                        e.Handled = true;
                        break;
                    case Keys.Enter:
                        Commit(selected);
                        e.Handled = true;
                        break;
                    case Keys.Escape:
                        PaletteOpen.Value = false;
                        e.Handled = true;
                        break;
                }
            }

            Element[] rows;
            if (count == 0)
            {
                rows =
                [
                    new BoxEl
                    {
                        Padding = new Edges4(Spacing.L, Spacing.M, Spacing.L, Spacing.M),
                        Children = [global::Wavee.Design.Type.DenseMeta(Loc.Get(Strings.Palette.NoMatches)) with { Color = Tok.TextTertiary }],
                    },
                ];
            }
            else
            {
                rows = new Element[count];
                string enter = Loc.Get(Strings.Palette.Enter);
                for (int i = 0; i < count; i++)
                {
                    int at = i;
                    bool active = at == selected;
                    var entry = hits[at];
                    rows[i] = new BoxEl
                    {
                        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
                        MinHeight = Design.Size.ControlH, MinWidth = 0f,
                        Padding = new Edges4(Spacing.L, Spacing.XS, Spacing.L, Spacing.XS),
                        Corners = Radii.ControlAll,
                        // Selection and hover paint the SAME fill; the accent glyph + "Enter" disambiguate (W34).
                        Fill = active ? Tok.FillSubtleSecondary : ColorF.Transparent,
                        HoverFill = Tok.FillSubtleSecondary,
                        OnClick = () => Commit(at),
                        Children =
                        [
                            new TextEl(entry.Glyph)
                            {
                                Size = 14f, FontFamily = Theme.IconFont,
                                Color = active ? Tok.AccentDefault : Tok.TextTertiary,
                            },
                            new BoxEl
                            {
                                Grow = 1f, MinWidth = 0f,
                                Children =
                                [
                                    new TextEl(entry.Label)
                                    {
                                        Size = 14f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                                    },
                                ],
                            },
                            active ? global::Wavee.Design.Type.MicroMeta(enter) with { Color = Tok.TextTertiary } : new BoxEl(),
                        ],
                    };
                }
            }

            return new BoxEl
            {
                Direction = 1, Width = PaletteCardW, MaxHeight = PaletteCardMaxH, Gap = Spacing.XXS,
                Padding = Edges4.All(Spacing.S),
                OnKeyDown = OnKey,
                Children =
                [
                    TextBox.Create(query, options: new TextBox.TextBoxOptions
                    {
                        Placeholder = Loc.Get(Strings.Palette.Placeholder),
                        Width = PaletteCardW - Spacing.S * 2f,
                    }),
                    new BoxEl { Height = Spacing.XXS },
                    new BoxEl { Direction = 1, Gap = 1f, Children = rows },
                ],
            };
        }
    }
}
