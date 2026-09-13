// ── Shell/Rail.cs ──────────────────────────────────────────────────────────────────────────────────────────────────
// RailVideoCoupling, NpvPlayerCatalog, NpvPlayerPrefs, NpvDiagnostics
//
// Role: CORE
// Owner: K
// Wave: 4
// Budget: 200 lines
// Spec: ch 21 §9.5
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The right rail's four engine-free decisions. `Rail.UI.cs` / `Rail.Styles.UI.cs` (stage 2) render what these decide.
//
//   • `Rail.VideoCoupling` — the two-way coupling between the rail's own open/closed + mode and the
//     video surface's placement (`Shell/Video.cs`). Four rules that are each an edge case, collected here so they are
//     asserted once over plain values instead of re-derived — and re-broken — at each of the four call sites.
//   • `Rail.PlayerCatalog` — the ONE table behind the style flyout, the Settings rows, the art context menu and the
//     tests. Ids and slugs are PERSISTED: never renumber, never rename (`NpvPlayerCatalog.cs:8-9`).
//   • `Rail.PlayerPrefs` — a catalog-aware ADAPTER over owner L's `Prefs.NpvPlayer` (the ONE epoch); it clamps on READ
//     as well as write, so a hand-edited value can never crash a render: it degrades to Record / Cover / choice 0.
//   • `Rail.NpvDiagnostics` — the always-on `npv` log category. A preference written from FIVE surfaces is only
//     attributable if each write says which one it came from (CLAUDE.md: always-on logs, no env switches).
//
// Rules: no allocation after warm-up (P8); no LINQ, no closures, no async, no boxing (P9); UI thread only (C1).

using FluentGpu.Signals;

namespace Wavee;

public static partial class Rail
{
    // ── 1. the video coupling ───────────────────────────────────────────────────────────────────────────────────────
    //
    // The rail's MODE vocabulary is owner I's `Shell.RailMode` (`Shell/Shell.cs` §6, beside `Shell.Ui.Mode`), because
    // the player bar's toggles write it and the session document persists it — `NowPlaying` is 0.2.9's `Details`.
    // `Video` is the video-first body (the docked card's takeover face, track meta, Up next), entered only when the
    // user docks a video into a CLOSED rail — never by a track change alone.

    /// <summary>The PURE rail ↔ docked-video coupling. Plain values in, a decision out — no <c>Signal&lt;T&gt;</c>, no
    /// FluentGpu type — so it is verifiable without a GPU or a window.</summary>
    public static class VideoCoupling
    {
        /// <summary>Docking was just requested (the user picked "Dock in rail", or a fresh profile's default resolved
        /// to Docked). Which rail mode should be showing? <c>null</c> means leave the rail exactly as it is — this
        /// only fires when the rail was CLOSED, so opening it for video does not clobber a mode the user was already
        /// looking at.</summary>
        /// <param name="host">Which docked host would own the surface (<see cref="Video.DockedHosting.HostFor"/>).
        /// Rail-only: when the watch page's in-page stage is what will host the video, opening the rail into the
        /// video-first body would open a rail whose card is mounted somewhere else entirely — an empty takeover body
        /// next to the real picture.</param>
        public static Shell.RailMode? ModeOnDock(bool railOpen, Shell.RailMode current, Video.DockedHost host)
            => host != Video.DockedHost.Rail || railOpen ? null : Shell.RailMode.Video;

        /// <summary>The user closed the rail. What should happen to a docked video? Returns the DEMOTE target, or
        /// <see cref="Video.SurfacePlacement.None"/> for "nothing to do". Only Docked is ever demoted here — the rail
        /// closing has no opinion about a floating or detached video.
        ///
        /// <para>INERT while the PAGE STAGE is hosting: the rail is not where that video lives, so closing the rail
        /// has no more to say about it than closing it says about a detached window. Without this term the rule would
        /// demote a watch page's in-page stage to the mini player and yank the video off the page being read.</para></summary>
        public static Video.SurfacePlacement OnRailClosed(in Video.PlacementState s, Video.DockedHost host)
            => host == Video.DockedHost.Rail && Video.PlacementCore.Resolve(s) == Video.SurfacePlacement.Docked
                ? Video.SurfacePlacement.Floating : Video.SurfacePlacement.None;

        /// <summary>The rail was opened again. Should video return to the dock? Keyed on
        /// <see cref="Video.PlacementState.Preferred"/> being Docked — exactly what distinguishes "the rail took my
        /// video away" (re-dock it) from "I deliberately chose the mini player from the menu" (leave it floating,
        /// since THAT write set <c>Preferred = Floating</c>). Excludes Fullscreen: entering fullscreen from the dock
        /// must not re-fire this the moment the rail happens to still be open underneath it.</summary>
        public static bool ReDockOnRailOpen(in Video.PlacementState s)
            => s.Preferred == Video.SurfacePlacement.Docked
            && s.Requested != Video.SurfacePlacement.None
            && s.Requested != Video.SurfacePlacement.Docked
            && s.Requested != Video.SurfacePlacement.Fullscreen;

        /// <summary>Video left the dock — turned off, its content lost availability, or it moved elsewhere — while the
        /// rail was showing the video-first body. Should the rail close? Yes: <see cref="Shell.RailMode.Video"/> has nothing
        /// left to show once the card it hosts is gone.
        ///
        /// <para><paramref name="hostBefore"/>, not the host NOW: once the video has left the dock the derivation
        /// always reads <see cref="Video.DockedHost.Rail"/> (nothing is docked, so the rail is the resting owner),
        /// which would make a current-host term unconditionally true and close the rail on behalf of a card the page
        /// stage was holding. The question is about the body that WAS on screen.</para></summary>
        public static bool CloseRailOnVideoLeft(Shell.RailMode mode, bool videoTurnedOff, Video.DockedHost hostBefore)
            => hostBefore == Video.DockedHost.Rail && mode == Shell.RailMode.Video && videoTurnedOff;

        /// <summary>What the rail's BODY renders, given the user's chosen mode and whether the page stage owns the one
        /// video surface. Video-first and NowPlaying (0.2.9 "Details") are the two bodies that HOST the docked card; when the stage owns
        /// the surface neither has anything left to host — a takeover body with no takeover, and a pinned hero square
        /// that would render an empty tile above the credits — so both fall back to Queue, the one body that is always
        /// meaningful while something is playing.
        ///
        /// <para>A substitution at RENDER time, never a write to the rail's mode. The user's chosen mode is untouched,
        /// so the moment the stage yields the rail is back exactly where it was — no displaced-mode memory to restore,
        /// and therefore no ordering between "the stage released it" and "the rail restored itself" to get wrong.</para></summary>
        public static Shell.RailMode BodyModeFor(Shell.RailMode mode, bool stageHostsVideo)
            => stageHostsVideo && mode is Shell.RailMode.Video or Shell.RailMode.NowPlaying ? Shell.RailMode.Queue : mode;
    }

    // ── 2. the player-style catalog (ids and slugs are PERSISTED) ───────────────────────────────────────────────────

    /// <summary>Which shelf of the style picker a preset sits on.</summary>
    public enum PlayerGroup : byte { Media, Devices, Software }

    /// <summary>How one option row is drawn: a segmented control, or a row of colour swatches.</summary>
    public enum OptionKind : byte { Segmented, Swatch }

    /// <summary>The ONE table behind the flyout, the Settings rows, the art context menu and the tests. Ids and slugs
    /// are PERSISTED — never renumber, never rename. Labels are Loc KEYS. The DEFAULT choice is always listed FIRST,
    /// so a stored default of 0 is always valid.</summary>
    public static class PlayerCatalog
    {
        /// <param name="Swatch">Packed ARGB, or <see cref="FromCover"/> to derive it from the cover grading.</param>
        public readonly record struct Choice(string Slug, string LabelKey, uint Swatch = 0);
        public readonly record struct OptionDef(string Slug, string LabelKey, OptionKind Kind, Choice[] Choices);
        public readonly record struct Preset(int Id, string Slug, string LabelKey, string ShortLabelKey,
                                             PlayerGroup Group, OptionDef[] Options);

        public const int Record = 0, Cassette = 1, Reel = 2, Cd = 3, Turntable = 4, Ipod = 5, Winamp = 6, Vu = 7,
                         Zune = 8, Wmp = 9, Canvas = 10, Picture = 11;
        public const int DefaultPresetId = Record;
        /// <summary>Presets per group — the picker's shelves are exactly this wide.</summary>
        public const int PerGroup = 4;
        /// <summary>Swatch 0 = derive from the cover (the album-colour vinyl).</summary>
        public const uint FromCover = 0;

        static readonly OptionDef Finish = new("finish", "player.opt.finish", OptionKind.Swatch,
        [
            new("black", "player.choice.black", 0xFF15171C), new("clear", "player.choice.clear", 0xFF8A93A6),
            new("album", "player.choice.albumColour", FromCover), new("splatter", "player.choice.splatter", 0xFFF5F1EA),
            new("marble", "player.choice.marble", 0xFF6B4FA8),
        ]);
        static readonly OptionDef Size = Seg("size", "player.opt.size", ("12", "player.choice.lp12"), ("7", "player.choice.single7"));
        static readonly OptionDef Rpm = Seg("rpm", "player.opt.speed", ("33", "player.choice.rpm33"), ("45", "player.choice.rpm45"));
        static readonly OptionDef Sleeve = Seg("sleeve", "player.opt.sleeve", ("on", "player.choice.shown"), ("off", "player.choice.hidden"));

        public static readonly Preset[] Presets =
        [
            new(Record, "record", "player.style.record", "player.style.record", PlayerGroup.Media, [Finish, Size, Rpm, Sleeve]),
            new(Cassette, "cassette", "player.style.cassette", "player.style.cassette", PlayerGroup.Media,
            [
                Seg("shell", "player.opt.shell", ("black", "player.choice.black"), ("clear", "player.choice.clear"), ("cream", "player.choice.cream"), ("smoke", "player.choice.smoke")),
                Seg("label", "player.opt.label", ("type1", "player.choice.typeI"), ("chrome", "player.choice.chrome"), ("hand", "player.choice.handwritten")),
                Seg("side", "player.opt.side", ("a", "player.choice.sideA"), ("b", "player.choice.sideB")),
            ]),
            new(Reel, "reel", "player.style.reel", "player.style.reelShort", PlayerGroup.Media,
                [Seg("reel", "player.opt.reels", ("alu", "player.choice.aluminium"), ("black", "player.choice.black"), ("clear", "player.choice.clear"))]),
            new(Cd, "cd", "player.style.cd", "player.style.cdShort", PlayerGroup.Media,
                [Seg("format", "player.opt.format", ("cd", "player.choice.cd"), ("md", "player.choice.minidisc"))]),
            new(Turntable, "turntable", "player.style.turntable", "player.style.turntable", PlayerGroup.Devices, [Finish, Size, Rpm, Sleeve]),
            new(Ipod, "ipod", "player.style.ipod", "player.style.ipodShort", PlayerGroup.Devices,
            [
                Seg("body", "player.opt.body", ("silver", "player.choice.silver"), ("black", "player.choice.black"), ("u2", "player.choice.u2")),
                Seg("lcd", "player.opt.lcd", ("white", "player.choice.white"), ("green", "player.choice.green")),
            ]),
            new(Winamp, "winamp", "player.style.winamp", "player.style.winamp", PlayerGroup.Devices,
            [
                Seg("skin", "player.opt.skin", ("base", "player.choice.base"), ("modern", "player.choice.modern"), ("dark", "player.choice.dark")),
                Seg("vis", "player.opt.analyser", ("spectrum", "player.choice.spectrum"), ("scope", "player.choice.oscilloscope")),
            ]),
            new(Vu, "vu", "player.style.vu", "player.style.vuShort", PlayerGroup.Devices,
            [
                Seg("face", "player.opt.face", ("ivory", "player.choice.ivory"), ("blue", "player.choice.blue"), ("black", "player.choice.black")),
                Seg("ballistics", "player.opt.needles", ("vu", "player.choice.vu"), ("ppm", "player.choice.ppm")),
            ]),
            new(Zune, "zune", "player.style.zune", "player.style.zune", PlayerGroup.Software,
            [
                new("accent", "player.opt.accent", OptionKind.Swatch,
                [
                    new("pink", "player.choice.pink", 0xFFF0568C), new("orange", "player.choice.orange", 0xFFFF7A1A),
                    new("green", "player.choice.green", 0xFF8CBF26), new("blue", "player.choice.blue", 0xFF1BA1E2),
                ]),
                Rpm,
            ]),
            new(Wmp, "wmp", "player.style.wmp", "player.style.wmpShort", PlayerGroup.Software,
            [
                Seg("preset", "player.opt.preset", ("bars", "player.choice.barsWaves"), ("alchemy", "player.choice.alchemy"), ("battery", "player.choice.battery")),
                Seg("colour", "player.opt.colour", ("accent", "player.choice.accent"), ("cover", "player.choice.fromCover")),
            ]),
            new(Canvas, "canvas", "player.style.canvas", "player.style.canvasShort", PlayerGroup.Software,
            [
                Seg("drift", "player.opt.drift", ("slow", "player.choice.slow"), ("fast", "player.choice.fast")),
                Seg("bleed", "player.opt.bleed", ("soft", "player.choice.soft"), ("strong", "player.choice.strong")),
            ]),
            new(Picture, "picture", "player.style.picture", "player.style.pictureShort", PlayerGroup.Software, [Size, Rpm, Sleeve]),
        ];

        public static bool IsPresetId(int id) => (uint)id < (uint)Presets.Length && Presets[id].Id == id;

        /// <summary>The preset for an id, falling back to <see cref="DefaultPresetId"/> — a stored id this build no
        /// longer defines degrades rather than throwing inside a render.</summary>
        public static Preset ById(int id) => Presets[IsPresetId(id) ? id : DefaultPresetId];

        /// <summary>Fill <paramref name="dest"/> with one group's presets, in table order; returns how many.</summary>
        public static int Group(PlayerGroup g, Span<Preset> dest)
        {
            int n = 0;
            for (int i = 0; i < Presets.Length; i++) if (Presets[i].Group == g) dest[n++] = Presets[i];
            return n;
        }

        /// <summary>The option row named <paramref name="slug"/>, or the preset's first (the faces' helper: every read
        /// must be reachable for that preset, or guarded by the variant flag — there is no "absent" answer).</summary>
        public static OptionDef Option(in Preset p, string slug)
        {
            for (int i = 0; i < p.Options.Length; i++) if (p.Options[i].Slug == slug) return p.Options[i];
            return p.Options[0];
        }

        static OptionDef Seg(string slug, string labelKey, params (string Slug, string LabelKey)[] choices)
        {
            var c = new Choice[choices.Length];
            for (int i = 0; i < c.Length; i++) c[i] = new(choices[i].Slug, choices[i].LabelKey);
            return new(slug, labelKey, OptionKind.Segmented, c);
        }
    }

    // ── 3. the player prefs — an ADAPTER over `Prefs.NpvPlayer` (owner L), never a second epoch ─────────────────────

    /// <summary>The hero's presentation + style preferences, as the RAIL speaks them: catalog-aware, and with the
    /// always-on <c>source</c> attribution every write carries.
    ///
    /// <para><b>No state lives here.</b> The epoch, the flyout's open state and the persisted choice are owner L's
    /// `Platform/Prefs.cs` (`Prefs.NpvPlayer`) — the one cross-surface epoch every writer bumps once. A second
    /// `Signal&lt;int&gt;` here would be a second authority: a Settings write that bumped one and not the other is exactly
    /// the "the flyout shows the old style" drift the epoch exists to prevent. So every member below forwards, and the
    /// only thing this class adds is what `Prefs.cs` deliberately does not know — the CATALOG (preset count, option
    /// slugs, choice counts) and the diagnostic line.</para>
    ///
    /// <para>It still clamps on READ as well as write (through `Prefs.NpvPlayer`'s clamps, fed the catalog's counts), so
    /// a hand-edited registry value or a preset a future build renamed can never crash a render: it degrades to Record
    /// / Cover / choice 0.</para></summary>
    public static class PlayerPrefs
    {
        /// <summary>The ONE epoch — `Prefs.NpvPlayer.Epoch`, the same instance.</summary>
        public static Signal<int> Epoch => Prefs.NpvPlayer.Epoch;

        /// <summary>The style flyout's controlled open state — shared so the art context menu opens the SAME popup the
        /// gear owns. The same instance as `Prefs.NpvPlayer.StyleFlyoutOpen`.</summary>
        public static Signal<bool> StyleFlyoutOpen => Prefs.NpvPlayer.StyleFlyoutOpen;

        public const int Cover = Prefs.NpvPlayer.Cover, Player = Prefs.NpvPlayer.Player;

        public static int ClampPresentation(int v) => Prefs.NpvPlayer.ClampPresentation(v);
        public static int ClampStyle(int id) => PlayerCatalog.IsPresetId(id) ? id : PlayerCatalog.DefaultPresetId;
        public static int ClampChoice(in PlayerCatalog.OptionDef o, int i) => (uint)i < (uint)o.Choices.Length ? i : 0;

        /// <summary>Reactive read (subscribes the epoch) of the hero presentation.</summary>
        public static int Presentation() => Prefs.NpvPlayer.Presentation();

        /// <summary>Reactive read of the chosen preset id, clamped against the catalog.</summary>
        public static int Style() => ClampStyle(Prefs.NpvPlayer.Style(PlayerCatalog.Presets.Length));

        /// <summary>Reactive read of the chosen preset's catalog row.</summary>
        public static PlayerCatalog.Preset CurrentPreset() => PlayerCatalog.ById(Style());

        public static int Choice(in PlayerCatalog.Preset p, in PlayerCatalog.OptionDef o)
            => Prefs.NpvPlayer.Choice(p.Slug, o.Slug, o.Choices.Length);

        public static int Choice(in PlayerCatalog.Preset p, string optionSlug)
            => Choice(p, PlayerCatalog.Option(p, optionSlug));

        /// <summary>The current choice's SLUG — what a face branches on (<c>ChoiceSlug(preset, "rpm") == "45"</c>).</summary>
        public static string ChoiceSlug(in PlayerCatalog.Preset p, string optionSlug)
        { var o = PlayerCatalog.Option(p, optionSlug); return o.Choices[Choice(p, o)].Slug; }

        public static void SetPresentation(int v, string source)
        {
            int from = ClampPresentation(Platform.Settings.Get(Platform.Keys.NpvPresentation)), to = ClampPresentation(v);
            Prefs.NpvPlayer.SetPresentation(to);                    // persists + bumps ONCE
            NpvDiagnostics.PresentationSet(from, to, source);
        }

        public static void TogglePresentation(string source)
            => SetPresentation(ClampPresentation(Platform.Settings.Get(Platform.Keys.NpvPresentation)) == Cover ? Player : Cover, source);

        /// <summary>Picking a player ALSO shows it: a style chosen while the cover was up flips to Player (in the same
        /// write — `Prefs.NpvPlayer.SetStyle` owns that rule).</summary>
        public static void SetStyle(int id, string source)
        {
            int from = ClampStyle(Platform.Settings.Get(Platform.Keys.NpvPlayerStyle)), to = ClampStyle(id);
            Prefs.NpvPlayer.SetStyle(to, PlayerCatalog.Presets.Length);
            NpvDiagnostics.StyleSet(PlayerCatalog.ById(from).Slug, PlayerCatalog.ById(to).Slug, source);
        }

        /// <summary>The art menu's "next style" — wraps at the end of the table.</summary>
        public static void NextStyle(string source)
            => SetStyle((ClampStyle(Platform.Settings.Get(Platform.Keys.NpvPlayerStyle)) + 1) % PlayerCatalog.Presets.Length, source);

        public static void SetChoice(in PlayerCatalog.Preset p, in PlayerCatalog.OptionDef o, int i, string source)
        {
            int to = ClampChoice(o, i);
            Prefs.NpvPlayer.SetChoice(p.Slug, o.Slug, to, o.Choices.Length);
            NpvDiagnostics.OptionSet(p.Slug, o.Slug, o.Choices[to].Slug, source);
        }
    }

    // ── 4. the always-on `npv` log category ─────────────────────────────────────────────────────────────────────────

    /// <summary>How a preference written from five surfaces is attributed. Always on: CLAUDE.md bans env-var switches
    /// for behaviour AND verification, and without the <c>source</c> field "the style changed" is unattributable.</summary>
    public static class NpvDiagnostics
    {
        public const string Category = "npv";
        public const string SourceHeader = "header", SourceFlyout = "flyout", SourceArtMenu = "artMenu",
                            SourceSettings = "settings", SourcePalette = "palette";

        public static void PresentationSet(int from, int to, string source) => Log.Event(
            WaveeLogLevel.Info, Category, "presentation.set",
            "now-playing hero " + Name(from) + " -> " + Name(to) + " via " + source,
            fields: [WaveeLogField.Of("from", Name(from)), WaveeLogField.Of("to", Name(to)), WaveeLogField.Of("source", source)]);

        public static void StyleSet(string from, string to, string source) => Log.Event(
            WaveeLogLevel.Info, Category, "style.set",
            "player style " + from + " -> " + to + " via " + source,
            fields: [WaveeLogField.Of("from", from), WaveeLogField.Of("to", to), WaveeLogField.Of("source", source)]);

        public static void OptionSet(string style, string option, string choice, string source) => Log.Event(
            WaveeLogLevel.Info, Category, "option.set",
            "player option " + style + "." + option + "=" + choice + " via " + source,
            fields: [WaveeLogField.Of("style", style), WaveeLogField.Of("option", option),
                     WaveeLogField.Of("choice", choice), WaveeLogField.Of("source", source)]);

        public static void FlyoutClosed() => Log.Debug(Category, "player style flyout dismissed");

        static string Name(int p) => p == PlayerPrefs.Player ? "player" : "cover";
    }
}
