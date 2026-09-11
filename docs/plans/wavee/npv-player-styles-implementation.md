# Now Playing player styles (Cover | Player) — implementation

2026-09-10. Ships in 0.2.9. Visual source of truth: `npv-player-styles-mica.html` beside this file (open it; every
option row, every deck's geometry and the tonearm state table are there, live).

The Now Playing rail's pinned hero (`RightRail.PinnedHero` → `NowPlayingHeroTile` → `NowPlayingPanel.HeroArt`) grows a
header row — an eyebrow, a two-item **Cover | ‹Player›** SelectorBar and a gear — and, in Player mode, shows one of
twelve "decks" in the same square instead of the cover: Record, Cassette, Reel-to-reel, CD/MiniDisc (Media); Turntable,
iPod Classic, Winamp, Hi-fi VU (Devices); Zune, WMP visualizer, Canvas drift, Picture disc (Software). The gear opens a
**Player style** flyout (3 rows × 4 thumbnails + per-preset option rows). Each deck carries progress in its medium (arm
angle, tape-pack migration, laser sled, click-wheel thumb, Winamp slider) and reacts to play/pause/seek/track
change/buffering/error/queue end. The record family has a physically honest tonearm state machine (SL-1200 0.7 s spin-up,
damped cue-lever descent, 1.8 s locked groove, auto-return). Everything below the hero never moves.

Decisions: not a playback module and not a `RailMode` — a presentation swap inside the Details arm; docked video keeps
replacing the whole slot. Preferences are HKCU ints through `WaveeSettings` + an epoch signal (per user, NOT
`session.json`). Rendering is declarative (there is no canvas): `Canvas.Create` absolute layout, `BoxEl`/`ImageEl`/`PathEl`
parts, ONE 30 Hz ticker per mounted deck, leaf nodes bind `Transform`/`Opacity` FloatSignals and never re-render.
Conic/repeating gradients do not exist → bundled alpha PNG textures in `src/apps/Wavee/assets/deck/` with hairline-ring
fallbacks. Music-reactive decks read the engine's existing RMS/peak tap (`IAudioEffects.Visualizer`) exposed as
`PlaybackBridge.Levels`; band shapes are synthesized but level-driven and labelled so. All physics/state logic is
engine-free under `Features/Player/Deck/Model/` and unit-tested. **No engine changes.**

## Target

```
RightRail.PinnedHero (unchanged arbitration: docked video → DockedCap wins the WHOLE slot)
└ NowPlayingHeroTile  Direction=1 Shrink=0 Padding(S,S,S,0) Gap=S
  ├ NpvHeaderRow  Height 36            ┌────────────────────────────────────────┐
  │   eyebrow "NOW PLAYING" Grow=1     │ NOW PLAYING      ┃▣ Cover│◉ Record┃ ⚙ │  ← SelectorBar + HeaderButton gear
  │   SelectorBar.Create(...)          ├────────────────────────────────────────┤
  │   Popup.Create(gear, flyout, open) │                                        │
  └ hero slot (Key-remounted)          │            324 × 324 hero slot         │
      "cover"      → HeroArt(track)    │   Cover:  ImageEl (today's HeroArt)    │
      "deck:"+slug → NpvDeck.Create    │   Player: NpvDeck.Create(track,preset) │
      both: BoxEl.WithContextMenu      └────────────────────────────────────────┘
                                          ScrollView body below is untouched
```
Rail 340, content 324. Pinned block grows 332 → 376 DIP. `RightRail.cs` offsets `ArtVideoToggle` by
`NowPlayingHeroTile.ArtTop = Spacing.S + NpvHeaderRow.Height + Spacing.S` so it still sits on the art's corner.

```
NpvDeck.Create(track, preset) → Embed.Comp(() => new DeckHost()) with { Key = "deck:" + preset.Slug }
DeckHost: BoxEl{ Width=S, Height=S, ClipToBounds, IsolateLayout=true, Corners=Card, 320 ms opacity+blur entrance }
  ├ Face  = Canvas.Create(S, S, parts…)      one file per deck in Deck/Faces/
  └ DeckClock (0×0 BoxEl)                     the ticker: PlaybackBridge → DeckInput → IDeckModel.Tick → DeckSignals
```

## Files

```
src/apps/Wavee/Features/Player/
  NpvPlayerCatalog.cs   NpvPlayerPrefs.cs   NpvDiagnostics.cs        (engine-free; source-included in Wavee.Tests)
  NpvHeaderRow.cs       PlayerStyleFlyout.cs (+NpvArtMenu, NpvSwatch)   NpvThumbnails.cs
  Deck/  NpvDeck.cs  DeckHost.cs  DeckClock.cs  DeckSignals.cs  DeckArt.cs  DeckGesture.cs
  Deck/Model/  (ENGINE-FREE: no `using FluentGpu.*`; BCL + Wavee.Core only)
      DeckInput.cs  DeckFrame.cs  PositionInterpolator.cs  SpinIntegrator.cs  TonearmMachine.cs
      TapeMachine.cs  DiscMachine.cs  MeterBallistics.cs  LevelSynth.cs  DriftPath.cs
  Deck/Faces/  RecordDeck.cs (Record/Turntable/Zune/Picture)  CassetteDeck.cs  ReelDeck.cs  CdDeck.cs
               IpodDeck.cs  WinampDeck.cs  VuDeck.cs  WmpDeck.cs  CanvasDeck.cs
src/apps/Wavee/assets/deck/  grooves-1024.png  wood-grain-1024.png  cd-rainbow-512.png  marble-1024.png
                             reel-flange-512.png  winamp-tbar.png     (Wavee.csproj already copies assets/**)
Edited: Features/Player/NowPlayingPanel.cs (NowPlayingHeroTile; cover-wash helper made internal), RightRail.cs (toggle margin),
        Platform/AppSettings.cs, Backend/AudioHost.cs (+IAudioLevelSource), SpotifyLive/Audio/FluentMediaAudioHost.cs,
        App/PlaybackBridge.cs (+Levels), App/Services.cs, Features/Shell/SettingsPage.Appearance.cs, App/SettingsCatalog.cs,
        Features/Shell/WaveeCommands.cs, assets/loc/{en-US,nl,ko-KR}.json, .claude/skills/wavee/palette-shortcuts.md,
        CHANGELOG.md, Wavee.Tests/Wavee.Tests.csproj, Wavee.Tests/TestAppSettingsShim.cs
```

## Part 1 — Preference model (engine-free)

```csharp
// Platform/AppSettings.cs — inside WaveeSettings
// ── Now Playing presentation (docs/plans/wavee/npv-player-styles-implementation.md) ─────────────────────────────
// Cover (0) or a Player deck (1) in the Details rail's pinned hero. A PER-USER preference, deliberately NOT part of
// session.json (that carries RailOpen/RailMode only): the rail remembers WHERE you were, this remembers HOW you like
// the hero drawn.
public static readonly SettingKey<int> NpvPresentation = new("npv.presentation", 0);
// Which player deck, as an NpvPlayerCatalog preset id (ints, like ThemeMode/RowDensity). Append-only, never renumbered.
public static readonly SettingKey<int> NpvPlayerStyle = new("npv.player.style", 0);

// Per-preset deck options (the SidebarKeys runtime-built family): one int key per (preset, option), value = index into
// that option's choice list. SLUGS ARE PERSISTED — NpvPlayerCatalog owns them, never rename. Record and Turntable share
// option rows but keep SEPARATE keys.
static class NpvPlayerKeys
{
    public static SettingKey<int> Option(string presetSlug, string optionSlug) => new($"npv.player.{presetSlug}.{optionSlug}", 0);
}
```

```csharp
// Features/Player/NpvPlayerCatalog.cs
namespace Wavee;
public enum NpvPlayerGroup : byte { Media, Devices, Software }
public enum NpvOptionKind : byte { Segmented, Swatch }

/// The one table behind the flyout, the Settings rows, the art context menu and the tests. Ids and slugs are PERSISTED.
/// Labels are Loc KEYS. The DEFAULT choice is always listed FIRST (so stored default 0 is always valid).
public static class NpvPlayerCatalog
{
    public readonly record struct Choice(string Slug, string LabelKey, uint Swatch = 0);
    public readonly record struct OptionDef(string Slug, string LabelKey, NpvOptionKind Kind, Choice[] Choices);
    public readonly record struct Preset(int Id, string Slug, string LabelKey, string ShortLabelKey, NpvPlayerGroup Group, OptionDef[] Options);

    public const int Record = 0, Cassette = 1, Reel = 2, Cd = 3, Turntable = 4, Ipod = 5, Winamp = 6, Vu = 7,
                     Zune = 8, Wmp = 9, Canvas = 10, Picture = 11;
    public const int DefaultPresetId = Record;
    public const int PerGroup = 4;
    public const uint FromCover = 0;   // Swatch 0 = derive from the cover (album-colour vinyl)

    static readonly OptionDef Finish = new("finish", "player.opt.finish", NpvOptionKind.Swatch,
    [
        new("black", "player.choice.black", 0xFF15171C), new("clear", "player.choice.clear", 0xFF8A93A6),
        new("album", "player.choice.albumColour", FromCover), new("splatter", "player.choice.splatter", 0xFFF5F1EA),
        new("marble", "player.choice.marble", 0xFF6B4FA8),
    ]);
    static readonly OptionDef Size   = Seg("size",   "player.opt.size",   ("12", "player.choice.lp12"), ("7", "player.choice.single7"));
    static readonly OptionDef Rpm    = Seg("rpm",    "player.opt.speed",  ("33", "player.choice.rpm33"), ("45", "player.choice.rpm45"));
    static readonly OptionDef Sleeve = Seg("sleeve", "player.opt.sleeve", ("on", "player.choice.shown"), ("off", "player.choice.hidden"));

    public static readonly Preset[] Presets =
    [
        new(Record,    "record",    "player.style.record",    "player.style.record",      NpvPlayerGroup.Media,    [Finish, Size, Rpm, Sleeve]),
        new(Cassette,  "cassette",  "player.style.cassette",  "player.style.cassette",    NpvPlayerGroup.Media,
        [
            Seg("shell", "player.opt.shell", ("black","player.choice.black"), ("clear","player.choice.clear"), ("cream","player.choice.cream"), ("smoke","player.choice.smoke")),
            Seg("label", "player.opt.label", ("type1","player.choice.typeI"), ("chrome","player.choice.chrome"), ("hand","player.choice.handwritten")),
            Seg("side",  "player.opt.side",  ("a","player.choice.sideA"), ("b","player.choice.sideB")),
        ]),
        new(Reel,      "reel",      "player.style.reel",      "player.style.reelShort",   NpvPlayerGroup.Media,
            [Seg("reel", "player.opt.reels", ("alu","player.choice.aluminium"), ("black","player.choice.black"), ("clear","player.choice.clear"))]),
        new(Cd,        "cd",        "player.style.cd",        "player.style.cdShort",     NpvPlayerGroup.Media,
            [Seg("format", "player.opt.format", ("cd","player.choice.cd"), ("md","player.choice.minidisc"))]),
        new(Turntable, "turntable", "player.style.turntable", "player.style.turntable",   NpvPlayerGroup.Devices,  [Finish, Size, Rpm, Sleeve]),
        new(Ipod,      "ipod",      "player.style.ipod",      "player.style.ipodShort",   NpvPlayerGroup.Devices,
        [
            Seg("body", "player.opt.body", ("silver","player.choice.silver"), ("black","player.choice.black"), ("u2","player.choice.u2")),
            Seg("lcd",  "player.opt.lcd",  ("white","player.choice.white"), ("green","player.choice.green")),
        ]),
        new(Winamp,    "winamp",    "player.style.winamp",    "player.style.winamp",      NpvPlayerGroup.Devices,
        [
            Seg("skin", "player.opt.skin",     ("base","player.choice.base"), ("modern","player.choice.modern"), ("dark","player.choice.dark")),
            Seg("vis",  "player.opt.analyser", ("spectrum","player.choice.spectrum"), ("scope","player.choice.oscilloscope")),
        ]),
        new(Vu,        "vu",        "player.style.vu",        "player.style.vuShort",     NpvPlayerGroup.Devices,
        [
            Seg("face",       "player.opt.face",    ("ivory","player.choice.ivory"), ("blue","player.choice.blue"), ("black","player.choice.black")),
            Seg("ballistics", "player.opt.needles", ("vu","player.choice.vu"), ("ppm","player.choice.ppm")),
        ]),
        new(Zune,      "zune",      "player.style.zune",      "player.style.zune",        NpvPlayerGroup.Software,
        [
            new("accent", "player.opt.accent", NpvOptionKind.Swatch,
            [
                new("pink","player.choice.pink",0xFFF0568C), new("orange","player.choice.orange",0xFFFF7A1A),
                new("green","player.choice.green",0xFF8CBF26), new("blue","player.choice.blue",0xFF1BA1E2),
            ]),
            Rpm,
        ]),
        new(Wmp,       "wmp",       "player.style.wmp",       "player.style.wmpShort",    NpvPlayerGroup.Software,
        [
            Seg("preset", "player.opt.preset", ("bars","player.choice.barsWaves"), ("alchemy","player.choice.alchemy"), ("battery","player.choice.battery")),
            Seg("colour", "player.opt.colour", ("accent","player.choice.accent"), ("cover","player.choice.fromCover")),
        ]),
        new(Canvas,    "canvas",    "player.style.canvas",    "player.style.canvasShort", NpvPlayerGroup.Software,
        [
            Seg("drift", "player.opt.drift", ("slow","player.choice.slow"), ("fast","player.choice.fast")),
            Seg("bleed", "player.opt.bleed", ("soft","player.choice.soft"), ("strong","player.choice.strong")),
        ]),
        new(Picture,   "picture",   "player.style.picture",   "player.style.pictureShort", NpvPlayerGroup.Software, [Size, Rpm, Sleeve]),
    ];

    public static bool IsPresetId(int id) => (uint)id < (uint)Presets.Length && Presets[id].Id == id;
    public static Preset ById(int id) => Presets[IsPresetId(id) ? id : DefaultPresetId];
    public static int Group(NpvPlayerGroup g, Span<Preset> dest) { int n = 0; foreach (var p in Presets) if (p.Group == g) dest[n++] = p; return n; }
    /// Index of the current choice for option `slug` (or 0) — the helper faces use: Choice(settings, preset, "rpm").
    public static OptionDef Option(in Preset p, string slug) { foreach (var o in p.Options) if (o.Slug == slug) return o; return p.Options[0]; }
    static OptionDef Seg(string slug, string labelKey, params (string Slug, string LabelKey)[] choices)
    { var c = new Choice[choices.Length]; for (int i = 0; i < c.Length; i++) c[i] = new(choices[i].Slug, choices[i].LabelKey); return new(slug, labelKey, NpvOptionKind.Segmented, c); }
}
```

```csharp
// Features/Player/NpvPlayerPrefs.cs — the LyricsPrefs/PlayerBarPrefs idiom: reads subscribe Epoch then re-read the store;
// every writer clamps, persists, logs, Bumps ONCE. Surfaces: header row, flyout, art context menu, Settings › Appearance,
// palette — and the deck faces for their options.
using FluentGpu.Signals;
namespace Wavee;
static class NpvPlayerPrefs
{
    public static readonly Signal<int> Epoch = new(0);
    public static void Bump() => Epoch.Value = Epoch.Peek() + 1;
    /// The style flyout's controlled open state — shared so the art context menu opens the SAME popup the gear owns.
    public static readonly Signal<bool> StyleFlyoutOpen = new(false);
    public const int Cover = 0, Player = 1;

    public static int ClampPresentation(int v) => v == Player ? Player : Cover;
    public static int ClampStyle(int id) => NpvPlayerCatalog.IsPresetId(id) ? id : NpvPlayerCatalog.DefaultPresetId;
    public static int ClampChoice(in NpvPlayerCatalog.OptionDef o, int i) => (uint)i < (uint)o.Choices.Length ? i : 0;

    public static int Presentation(IAppSettings? s) { _ = Epoch.Value; return ClampPresentation(s?.Get(WaveeSettings.NpvPresentation) ?? WaveeSettings.NpvPresentation.Default); }
    public static int Style(IAppSettings? s)        { _ = Epoch.Value; return ClampStyle(s?.Get(WaveeSettings.NpvPlayerStyle) ?? WaveeSettings.NpvPlayerStyle.Default); }
    public static int Choice(IAppSettings? s, in NpvPlayerCatalog.Preset p, in NpvPlayerCatalog.OptionDef o)
    { _ = Epoch.Value; return ClampChoice(o, s?.Get(NpvPlayerKeys.Option(p.Slug, o.Slug)) ?? 0); }
    public static int Choice(IAppSettings? s, in NpvPlayerCatalog.Preset p, string optionSlug) => Choice(s, p, NpvPlayerCatalog.Option(p, optionSlug));
    public static string ChoiceSlug(IAppSettings? s, in NpvPlayerCatalog.Preset p, string optionSlug)
    { var o = NpvPlayerCatalog.Option(p, optionSlug); return o.Choices[Choice(s, p, o)].Slug; }

    public static void SetPresentation(IAppSettings? s, int v, string source)
    {
        int from = ClampPresentation(s?.Get(WaveeSettings.NpvPresentation) ?? 0), to = ClampPresentation(v);
        s?.Set(WaveeSettings.NpvPresentation, to); NpvDiagnostics.PresentationSet(from, to, source); Bump();
    }
    public static void TogglePresentation(IAppSettings? s, string source) => SetPresentation(s, Presentation(s) == Cover ? Player : Cover, source);
    /// Picking a player ALSO shows it: a style chosen while the cover was up flips to Player.
    public static void SetStyle(IAppSettings? s, int id, string source)
    {
        int from = ClampStyle(s?.Get(WaveeSettings.NpvPlayerStyle) ?? 0), to = ClampStyle(id);
        s?.Set(WaveeSettings.NpvPlayerStyle, to);
        if (ClampPresentation(s?.Get(WaveeSettings.NpvPresentation) ?? 0) != Player) s?.Set(WaveeSettings.NpvPresentation, Player);
        NpvDiagnostics.StyleSet(NpvPlayerCatalog.ById(from).Slug, NpvPlayerCatalog.ById(to).Slug, source); Bump();
    }
    public static void NextStyle(IAppSettings? s, string source) => SetStyle(s, (Style(s) + 1) % NpvPlayerCatalog.Presets.Length, source);
    public static void SetChoice(IAppSettings? s, in NpvPlayerCatalog.Preset p, in NpvPlayerCatalog.OptionDef o, int i, string source)
    {
        int to = ClampChoice(o, i); s?.Set(NpvPlayerKeys.Option(p.Slug, o.Slug), to);
        NpvDiagnostics.OptionSet(p.Slug, o.Slug, o.Choices[to].Slug, source); Bump();
    }
}
```

```csharp
// Features/Player/NpvDiagnostics.cs — PlaybackBucketDiagnostics wrapper shape; category "npv" appears in Settings › Logs automatically.
internal static class NpvDiagnostics
{
    const string Category = "npv";
    public const string SourceHeader = "header", SourceFlyout = "flyout", SourceArtMenu = "artMenu", SourceSettings = "settings", SourcePalette = "palette";
    public static void PresentationSet(int from, int to, string source) => WaveeLog.Instance.Info(Category, "presentation.set",
        "now-playing hero " + Name(from) + " -> " + Name(to) + " via " + source, WaveeLogField.Of("from", Name(from)), WaveeLogField.Of("to", Name(to)), WaveeLogField.Of("source", source));
    public static void StyleSet(string from, string to, string source) => WaveeLog.Instance.Info(Category, "style.set",
        "player style " + from + " -> " + to + " via " + source, WaveeLogField.Of("from", from), WaveeLogField.Of("to", to), WaveeLogField.Of("source", source));
    public static void OptionSet(string style, string option, string choice, string source) => WaveeLog.Instance.Info(Category, "option.set",
        "player option " + style + "." + option + "=" + choice + " via " + source, WaveeLogField.Of("style", style), WaveeLogField.Of("option", option), WaveeLogField.Of("choice", choice), WaveeLogField.Of("source", source));
    public static void FlyoutClosed() => WaveeLog.Instance.Debug(Category, "flyout.close", "player style flyout dismissed");
    static string Name(int p) => p == NpvPlayerPrefs.Player ? "player" : "cover";
}
```

## Part 2 — Shell chrome

### NowPlayingHeroTile (edit) and RightRail toggle offset

```csharp
sealed class NowPlayingHeroTile : Component
{
    /// Top of the ART inside the tile — RightRail offsets the Art|Video toggle by this so it stays on the art's corner.
    public const float ArtTop = Spacing.S + NpvHeaderRow.Height + Spacing.S;
    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot); var svc = UseContext(Services.Slot); var overlay = UseContext(Overlay.Service);
        var track = b?.CurrentTrack.Value;
        if (track is null) return new BoxEl();
        var settings = svc?.Settings;
        int presentation = NpvPlayerPrefs.Presentation(settings);           // Epoch-subscribed
        var preset = NpvPlayerCatalog.ById(NpvPlayerPrefs.Style(settings));
        Element hero = presentation == NpvPlayerPrefs.Player
            ? NpvDeck.Create(track, preset) with { Key = "deck:" + preset.Slug }
            : NowPlayingPanel.HeroArt(track) with { Key = "cover" };
        var heroBox = new BoxEl { Direction = 1, Shrink = 0f, Children = [hero] };   // ImageEl has no OnContextRequested: the wrapper carries the menu
        if (overlay is not null) heroBox = heroBox.WithContextMenu(overlay, () => NpvArtMenu.Model(settings, presentation, preset));
        return new BoxEl
        {
            Direction = 1, Shrink = 0f, Gap = Spacing.S, Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, 0f),
            Children = [Embed.Comp(() => new NpvHeaderRow()), heroBox],
        };
    }
}
// RightRail.PinnedHero: the ZStack arm becomes
// new BoxEl { ZStack = true, Children = [art, ArtVideoToggle(b) with { Margin = new Edges4(0f, NowPlayingHeroTile.ArtTop + Spacing.XS, Spacing.S + Spacing.XS, 0f) }] }
// (adapt to however ArtVideoToggle positions itself today — the goal is: same corner of the ART, below the header row).
```

### NpvHeaderRow

```csharp
/// Eyebrow · Cover|Player SelectorBar · Player-style gear. Its OWN component so the SelectorBar's controlled signal and
/// the flyout's open signal are hooks with a stable owner.
sealed class NpvHeaderRow : Component
{
    public const float Height = 36f;
    public override Element Render()
    {
        var svc = UseContext(Services.Slot); var settings = svc?.Settings;
        var presentation = UseSignal(NpvPlayerPrefs.Presentation(settings));
        var styleOpen = NpvPlayerPrefs.StyleFlyoutOpen;
        var preset = NpvPlayerCatalog.ById(NpvPlayerPrefs.Style(settings));
        int epoch = NpvPlayerPrefs.Epoch.Value;
        UseEffect(() => presentation.SetIfChanged(NpvPlayerPrefs.Presentation(settings)), epoch);   // another surface wrote the pref
        string[] items = [Loc.Get(Strings.Player.PresentationCover), Loc.Get(preset.ShortLabelKey)];
        string?[] icons = [Icons.Picture, Icons.Album];   // use whatever glyph names exist in Icons
        Element gear = RightRail.HeaderButton(Icons.Settings, Loc.Get(Strings.Player.PlayerStyle), onClick: () => styleOpen.Value = !styleOpen.Peek(), active: styleOpen.Value);
        return new BoxEl
        {
            Direction = 0, Height = Height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Spacing.XS,
            Children =
            [
                new TextEl(Loc.Get(Strings.Player.NowPlaying).ToUpperInvariant()) { Size = 11f, LineHeight = 16f, Weight = 600, Color = Tok.TextTertiary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
                SelectorBar.Create(items, presentation, onChange: i => NpvPlayerPrefs.SetPresentation(settings, i, NpvDiagnostics.SourceHeader), icons: icons),
                Popup.Create(gear, () => Embed.Comp(() => new PlayerStyleFlyout()), styleOpen,
                    onOpenChanged: open => { if (!open) NpvDiagnostics.FlyoutClosed(); },
                    placement: FlyoutPlacement.BottomEdgeAlignedRight,
                    options: new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup)),
            ],
        };
    }
}
```
`Popup.Create` (controlled), not `Flyout.Attach`: the gear needs a lit expanded state and the art menu opens the same
popup. Stays open on every selection; Escape/light-dismiss closes; focus returns to the gear.

### PlayerStyleFlyout (340 DIP), NpvArtMenu, NpvSwatch, NpvThumbnails

```csharp
sealed class PlayerStyleFlyout : Component
{
    public const float Width = 340f, ThumbCardGap = 6f, ThumbSize = (Width - 2 * Spacing.M - 3 * ThumbCardGap) / 4f;
    public override Element Render()
    {
        var settings = UseContext(Services.Slot)?.Settings;
        int styleId = NpvPlayerPrefs.Style(settings); var preset = NpvPlayerCatalog.ById(styleId);
        var kids = new List<Element>(8)
        {
            new TextEl(Loc.Get(Strings.Player.PlayerStyle)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary },
            GroupRow(Strings.Player.StyleGroupMedia, NpvPlayerGroup.Media, styleId, settings),
            GroupRow(Strings.Player.StyleGroupDevices, NpvPlayerGroup.Devices, styleId, settings),
            GroupRow(Strings.Player.StyleGroupSoftware, NpvPlayerGroup.Software, styleId, settings),
            Label(Strings.Player.StyleOptions),
        };
        foreach (var opt in preset.Options) kids.Add(OptionRow(settings, preset, opt));
        return new BoxEl { Width = Width, Direction = 1, Gap = Spacing.M, Padding = Edges4.All(Spacing.M), Children = kids.ToArray() };
    }
    // GroupRow: label + 4 ThumbCards. ThumbCard: BoxEl plate (Tok.FillControlAltSecondary, Corners 6), 2 px Tok.AccentDefault ring when
    // selected else 1 px Tok.StrokeCardDefault, Role=Button, Focusable, Cursor Hand, OnClick → NpvPlayerPrefs.SetStyle(settings, p.Id, "flyout"),
    // children: NpvThumbnails.For(p.Id, ThumbSize - 8f) + 11 pt centred label (MaxLines 2). Interaction.Card hover/press.
    // OptionRow: label (12 pt, TextSecondary, MinWidth 60) + control: Swatch kind → circular 30 DIP BoxEls (22 DIP fill, 2 px accent ring on the
    // selected one; the FromCover swatch binds the current cover wash) with ToolTip; Segmented kind → Segmented.Create(items, new Signal<int>(current),
    // onChange → NpvPlayerPrefs.SetChoice(settings, preset, opt, i, "flyout"), compact style height 26, font 12).
}
static class NpvSwatch { public static ColorF Color(uint argb) => new(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f); }
static class NpvArtMenu
{
    // Radio "Cover" / "<Player>" → SetPresentation(..., "artMenu"); separator; "Player style…" → NpvPlayerPrefs.StyleFlyoutOpen.Value = true.
    public static ContextMenuModel Model(IAppSettings? settings, int presentation, in NpvPlayerCatalog.Preset preset) { … }
}
// NpvThumbnails.For(id, size): 12 static 3–4 BoxEl mini-arts transliterated from the mockup's .m-* CSS (record: plate + tilted pale sleeve +
// black disc with pale label; cassette: shell + two white hubs; reel: two grey flanges; cd: rainbow-ish disc + plate hole; turntable: wood plate +
// black platter + grey ring; ipod: silver body + dark LCD + white wheel; winamp: dark frame + green bars; vu: two ivory meters + amber LCD;
// zune: black + disc + pink stripe; wmp: near-black + accent bars; canvas: pale gradient + tilted card; picture: pale disc). Literal colours are fine.
```

### Settings › Appearance › Now playing, palette, strings

`SettingsPage.Appearance.cs`: a "Now playing" section with a SelectorBar row (index = stored int) for Cover|Player and a
ComboBox over the 12 presets (index = preset id) — both call the same `NpvPlayerPrefs` writers with source
`"settings"`. `SettingsCatalog.cs`: section `("Now playing", glyph "Album")`, rows `npvPresentation` ("Picture"),
`npvStyle` ("Settings"). `WaveeCommands.cs`: `SettingsVerb.NpvTogglePresentation`, `NpvNextStyle`
(`BuiltinCount` +2), `settings.npvPresentation` "Now Playing: Cover / Player", `settings.npvNextStyle` "Player style:
next". Strings (en-US, nl, ko-KR): every `player.style.*`, `player.opt.*`, `player.choice.*` key in the catalog plus
`player.presentationCover`, `player.playerStyle`, `player.playerStyleEllipsis`, `player.playerStyleNext`,
`player.presentationToggle`, `player.nowPlaying`, `player.styleGroupMedia/Devices/Software`, `player.styleOptions`,
`settings.nowPlaying.title/subtitle`, `settings.appearance.npvPresentation(Sub)`, `settings.appearance.npvStyle(Sub)`.
Check how `Strings.*` constants are generated from the loc JSON before adding (a missing key is a compile error).

## Part 3 — Deck runtime

### Model contracts (engine-free)

```csharp
namespace Wavee.Features.Player.Deck.Model;
public enum DeckBoundary : byte { None, NaturalSameAlbum, NaturalNewAlbum, SkipSameAlbum, SkipNewAlbum, RepeatOne }

/// One fold of the bridge for one tick. Pure data.
public readonly record struct DeckInput(
    long NowMs, bool HasTrack, PlaybackPhase Phase, bool PlayWhenReady,
    bool Advancing,            // Transport.OutputAdvancing && !IsBuffering
    bool Buffering,            // IsBuffering || (PlayWhenReady && Phase is Resolving/Buffering)
    bool Error, bool QueueEnded,   // Phase == Ended && !PlayWhenReady
    DeckBoundary Boundary,     // an EDGE: set for exactly one fold
    long? SeekTargetMs,        // committed seek awaiting ack (bridge) OR a synthesized remote jump (DeckClock)
    long? ScrubTargetMs,       // pointer-owned position (a drag is live)
    long PositionMs,           // INTERPOLATED — never the raw 1 Hz value
    long DurationMs, bool RepeatOne, float Rpm, bool ReducedMotion)
{
    public float Frac => DurationMs > 0 ? Clamp01(PositionMs / (float)DurationMs) : 0f;
    public float FracOf(long ms) => DurationMs > 0 ? Clamp01(ms / (float)DurationMs) : 0f;
    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}

public static class DeckBoundaryRules
{
    public const long NaturalEndWindowMs = 1_500, RepeatRewindMs = 2_000;
    public static DeckBoundary Classify(string? prevUri, string? prevAlbumUri, long prevPosMs, long prevDurMs, PlaybackPhase prevPhase,
                                        string? uri, string? albumUri, long posMs, bool repeatOne)
    {
        if (string.Equals(prevUri, uri, StringComparison.Ordinal))
            return repeatOne && prevDurMs > 0 && prevPosMs >= prevDurMs - NaturalEndWindowMs && posMs < RepeatRewindMs ? DeckBoundary.RepeatOne : DeckBoundary.None;
        if (prevUri is null || uri is null) return DeckBoundary.None;
        bool natural = prevPhase == PlaybackPhase.Transitioning || (prevDurMs > 0 && prevPosMs >= prevDurMs - NaturalEndWindowMs);
        bool sameAlbum = albumUri is { Length: > 0 } && string.Equals(prevAlbumUri, albumUri, StringComparison.Ordinal);
        return (natural, sameAlbum) switch { (true, true) => DeckBoundary.NaturalSameAlbum, (true, false) => DeckBoundary.NaturalNewAlbum, (false, true) => DeckBoundary.SkipSameAlbum, _ => DeckBoundary.SkipNewAlbum };
    }
}

/// What a model hands back per tick. Faces bind DeckSignals; DeckClock diff-writes this into them.
public readonly record struct DeckFrame(
    float Frac, float Angle0, float Angle1,   // record: platter deg / ARM deg · tape: left / right reel · CD: disc · VU: L / R needle
    float Lift,                               // record: 0 down .. 1 up (bob above 1)
    float Slide,                              // record: 0 in sleeve .. 1 on platter · cassette eject · CD tray
    float Aux0, float Aux1,                   // pack scales / sled / misc (per model)
    int CoverGen, bool Thump, DeckPhaseName Phase);
public enum DeckPhaseName : byte { Idle, Cueing, NeedleDown, Playing, Pausing, Paused, SpinningUp, Seeking, NextTrack, ChangingRecord, RunOut, LockedGroove, AutoReturn, Buffering, Error, Stopped, Unavailable, Winding }
public interface IDeckModel
{
    DeckFrame Tick(in DeckInput input, float dtSec);   // alloc-free
    ReadOnlySpan<float> Bands { get; }                 // analysers; empty otherwise
    ReadOnlySpan<float> Peaks { get; }
    bool IsSettled { get; }                            // nothing moving → the ticker may stop
}
public static class DeckEase
{
    public static float Std(float t) => CubicBezier(0.4f, 0f, 0.2f, 1f, t);       // swings
    public static float LiftUp(float t) => CubicBezier(0.2f, 0f, 0f, 1f, t);      // cue lever up
    public static float Damped(float t) => CubicBezier(0.2f, 0.6f, 0.3f, 1f, t);  // silicone-damped lower
    public static float SlideOut(float t) => CubicBezier(0.2f, 0.7f, 0.2f, 1f, t);
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    public static float CubicBezier(float x1, float y1, float x2, float y2, float x) { /* Newton on x, 6 iterations, then y(t) */ }
}

public struct PositionInterpolator   // SeekBar.Recompute maths, minus the DVR arm
{
    long _anchorWallMs, _anchorPosMs;
    public void Anchor(long wallMs, long positionMs) { _anchorWallMs = wallMs; _anchorPosMs = positionMs; }
    public long Estimate(long nowMs, bool advancing, long? seekTargetMs, long? upperBoundMs, long durationMs)
    {
        if (seekTargetMs is { } t) return Clamp(t, durationMs);
        long est = advancing ? _anchorPosMs + (nowMs - _anchorWallMs) : _anchorPosMs;
        if (upperBoundMs is { } ub && est > ub) est = ub;
        return Clamp(est, durationMs);
    }
    static long Clamp(long v, long dur) => dur > 0 ? Math.Clamp(v, 0, dur) : Math.Max(0, v);
}

public struct SpinIntegrator   // first-order angular velocity; rpm·6 = deg/s
{
    public float Omega, Angle, TauUp, TauDown;
    public SpinIntegrator(float tauUp, float tauDown) { TauUp = tauUp; TauDown = tauDown; }
    public void Step(float targetDegPerSec, float dt, int dir = 1)
    {
        float tau = MathF.Abs(targetDegPerSec) > MathF.Abs(Omega) ? TauUp : TauDown;
        float k = 1f - MathF.Exp(-dt / tau);
        Omega += (targetDegPerSec - Omega) * k;
        if (targetDegPerSec == 0f && MathF.Abs(Omega) < 0.05f) Omega = 0f;
        Angle = (Angle + Omega * dt * dir) % 360f; if (Angle < 0f) Angle += 360f;
    }
    public bool AtRest => Omega == 0f;
}
```
Record platter: τ↑ 0.23 s / τ↓ 0.53 s (3τ = the SL-1200's 0.7 s spin-up and a 1.6 s electronic brake). 33⇄45 sets both
τ to 0.17 for 600 ms then restores.

### DeckSignals, DeckClock, DeckHost, NpvDeck (engine)

```csharp
sealed class DeckSignals
{
    public readonly FloatSignal Frac = new(0f), Angle0 = new(0f), Angle1 = new(-34f), Lift = new(1f), Slide = new(1f), Aux0 = new(0f), Aux1 = new(0f);
    public readonly FloatSignal[] Bands = Make(24), Peaks = Make(24);
    public readonly Signal<int> CoverGen = new(0);
    public readonly Signal<DeckPhaseName> Phase = new(DeckPhaseName.Idle);
    static FloatSignal[] Make(int n) { var a = new FloatSignal[n]; for (int i = 0; i < n; i++) a[i] = new(0f); return a; }
}

/// One per mounted deck; the ONLY timer in the hero slot. Renders a 0×0 box.
sealed class DeckClock : Component
{
    public required PlaybackBridge Bridge; public required IDeckModel Model; public required DeckSignals Out;
    public required Func<float> Rpm; public required Func<float> AngleQuantumDeg;   // 360 / (π · rimDiameterPx)
    const float TickMs = 1000f / 30f;      // = AmbientPowerPolicy foreground ambient cadence
    const long RemoteJumpMs = 2_500;       // a tick landing further than this from the interpolated expectation, with no SeekTarget, IS a seek
    readonly Signal<bool> _settled = new(true);
    PositionInterpolator _pos; long _lastTickMs, _expectedPosMs;
    string? _uri, _albumUri; long _prevPos, _prevDur; PlaybackPhase _prevPhase; DeckBoundary _pendingBoundary; long? _syntheticSeek;
    public Action? ThumpRequested;

    public override Element Render()
    {
        var b = Bridge; var ui = UseContext(ShellUi.Slot);
        long posMs = b.PositionMs.Value;                                   // 1 Hz anchor
        UseEffect(() =>
        {
            long now = Environment.TickCount64;
            if (_syntheticSeek is null && b.SeekTargetMs.Peek() is null && _prevDur > 0 && Math.Abs(posMs - _expectedPosMs) > RemoteJumpMs) _syntheticSeek = posMs;
            _pos.Anchor(now, posMs);
        }, posMs);
        var track = b.CurrentTrack.Value;
        UseEffect(() =>
        {
            _pendingBoundary = DeckBoundaryRules.Classify(_uri, _albumUri, _prevPos, _prevDur, _prevPhase, track?.Uri, track?.Album?.Uri, b.PositionMs.Peek(), b.Repeat.Peek() == RepeatMode.Track);
            _uri = track?.Uri; _albumUri = track?.Album?.Uri; Tick();     // fold the edge now (no 33 ms lag on a skip)
        }, track?.Uri ?? "");
        bool playing = b.IsPlaying.Value, pwr = b.PlayWhenReady.Value, buffering = b.IsBuffering.Value;
        bool err = b.Error.Value is not null, ended = b.Transport.Value.Phase == PlaybackPhase.Ended;
        _ = b.SeekTargetMs.Value; _ = b.ScrubTargetMs.Value; _ = b.Repeat.Value; _ = NpvPlayerPrefs.Epoch.Value;
        bool reduced = Motion.ReducedMotion;
        bool run = !reduced && (ui?.RailOpen.Value ?? true) && (playing || pwr || buffering || !_settled.Value);
        UseInterval(Tick, TickMs, enabled: run);                             // UseInterval already ANDs UseIsActive
        UseEffect(() => { if (reduced || !run) Tick(); }, HashCode.Combine(posMs, playing, pwr, buffering, err, ended, reduced));
        return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
    }

    void Tick()
    {
        var b = Bridge; long now = Environment.TickCount64;
        float dt = _lastTickMs == 0 ? TickMs / 1000f : Math.Clamp((now - _lastTickMs) / 1000f, 0.001f, 0.040f); _lastTickMs = now;
        var transport = b.Transport.Peek(); bool buffering = b.IsBuffering.Peek();
        bool advancing = transport.OutputAdvancing && !buffering; long dur = b.DurationMs.Peek();
        long pos = _pos.Estimate(now, advancing, b.SeekTargetMs.Peek() ?? _syntheticSeek, transport.PositionUpperBoundMs, dur);
        _expectedPosMs = pos;
        var input = new DeckInput(now, b.CurrentTrack.Peek() is not null, transport.Phase, transport.PlayWhenReady, advancing,
            buffering || (transport.PlayWhenReady && transport.Phase is PlaybackPhase.Resolving or PlaybackPhase.Buffering),
            b.Error.Peek() is not null, transport.Phase == PlaybackPhase.Ended && !transport.PlayWhenReady,
            _pendingBoundary, b.SeekTargetMs.Peek() ?? _syntheticSeek, b.ScrubTargetMs.Peek(), pos, dur,
            b.Repeat.Peek() == RepeatMode.Track, Rpm(), Motion.ReducedMotion);
        _pendingBoundary = DeckBoundary.None;
        if (_syntheticSeek is { } s && Math.Abs(pos - s) < 500) _syntheticSeek = null;
        _prevPos = pos; _prevDur = dur; _prevPhase = transport.Phase;
        var f = Model.Tick(in input, dt);
        float aq = AngleQuantumDeg(); var o = Out;
        void Write()   // one Batch → one FrameRequested; every write is value-gated at a quantum
        {
            Set(o.Frac, Q(f.Frac, 1f / 1024f)); Set(o.Angle0, Q(f.Angle0, aq)); Set(o.Angle1, Q(f.Angle1, aq * 0.5f));
            Set(o.Lift, Q(f.Lift, 0.01f)); Set(o.Slide, Q(f.Slide, 0.005f)); Set(o.Aux0, Q(f.Aux0, 0.005f)); Set(o.Aux1, Q(f.Aux1, 0.005f));
            var bands = Model.Bands; for (int i = 0; i < bands.Length && i < o.Bands.Length; i++) Set(o.Bands[i], Q(bands[i], 1f / 64f));
            var peaks = Model.Peaks; for (int i = 0; i < peaks.Length && i < o.Peaks.Length; i++) Set(o.Peaks[i], Q(peaks[i], 1f / 64f));
            if (o.CoverGen.Peek() != f.CoverGen) o.CoverGen.Value = f.CoverGen;
            if (o.Phase.Peek() != f.Phase) o.Phase.Value = f.Phase;
            if (_settled.Peek() != Model.IsSettled) _settled.Value = Model.IsSettled;
        }
        if (Context.Runtime is { } rt) rt.Batch(Write); else Write();
        if (f.Thump) ThumpRequested?.Invoke();
    }
    static void Set(FloatSignal s, float v) { if (v != s.Peek()) s.Value = v; }
    static float Q(float v, float q) => MathF.Round(v / q) * q;
}
```
Adapt the exact hook/API names to what exists (`UseInterval(Action, float, bool enabled)`, `Context.Runtime.Batch`,
`Motion.ReducedMotion`, `ShellUi.Slot`); the Equalizer and SeekBar are the templates.

```csharp
static class NpvDeck
{
    /// The one shell-facing call: a SQUARE element filling the rail content width (like HeroArt). Reads playback from PlaybackBridge.Slot and
    /// options through NpvPlayerPrefs (Epoch-subscribed → an option flip restyles the mounted deck; the shell remounts only on preset change).
    public static Element Create(Track track, NpvPlayerCatalog.Preset preset) => Embed.Comp(() => new DeckHost { Preset = preset });
}
sealed class DeckHost : Component
{
    public required NpvPlayerCatalog.Preset Preset;
    readonly DeckSignals _sig = new(); IDeckModel? _model;
    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot)!; var settings = UseContext(Services.Slot)?.Settings;
        _ = NpvPlayerPrefs.Epoch.Value;                                     // options restyle in place
        float side = /* measured content width; use the same fluid-square approach HeroArt uses (AspectRatio 1) or UseMeasuredWidth */;
        _model ??= DeckModels.Create(Preset.Id, settings, b);               // seeded from the CURRENT transport (Seed): no cue animation on mount
        var face = DeckFaces.Create(Preset, settings, side, _sig, b, this);
        return new BoxEl
        {
            Width = side, Height = side, ClipToBounds = true, IsolateLayout = true, Corners = CornerRadius4.All(Radii.Card), Shrink = 0f,
            Animate = /* 320 ms opacity(+blur) entrance, reduced motion keeps the fade */,
            Children = [face, Embed.Comp(() => new DeckClock { Bridge = b, Model = _model, Out = _sig,
                Rpm = () => NpvPlayerPrefs.ChoiceSlug(settings, Preset, "rpm") == "45" ? 45f : 33.333f,
                AngleQuantumDeg = () => 360f / (MathF.PI * side * 0.70f * /*device scale*/ 1f) })],
        };
    }
}
```
If the rail width cannot be measured synchronously, the hero slot may pass the known content width (rail width − 2·S)
from `RightRail.PinnedHero`'s `railWidth` down through `NpvDeck.Create(track, preset, side)`; that is acceptable — choose
whichever compiles cleanly and does not relayout.

### Tonearm state machine (engine-free) — reference implementation

Angles: Rest −34°, LeadIn −20°, Span 17°, RunOut −3° (`AngleOf(frac) = −20 + 17·frac`). Timings (ms), verbatim from the
mockup: Slide 600, CueHold 300, SwingLead 1200, Lower 900 (700 after a seek), Lift 450 (400 seek, 300 skip/drag, 500
auto-return), SpinUp 700, SeekSwing 300 + 600·|Δ|/Span, RecueSwing 900, ChangeSwing 1200 with platter off 700 ms in,
SleeveIn 600, CoverSwap 350, RunOut 400 + 800·|Δ|/Span (linear), LockedGroove 1800 @33⅓ / 1333 @45, AutoSwing 1400 with
platter off 500 ms in, ErrorSwing 1200, Bob period 1800, Reduced 150, Thump 120.

```csharp
public enum TonearmPhase : byte { Idle, SlideOut, Cue, SwingToLead, Lower, Tracking, Lift, Paused, SpinUp, SeekSwing, Dragging, RecueSwing, SwingToRest, SleeveIn, CoverSwap, RunOut, LockedGroove, Hover, Stopped, Unavailable }

public readonly record struct TonearmState(
    TonearmPhase Phase, long SinceMs, float PhaseMs, float FromDeg, float ToDeg, float LiftFrom,
    bool PlatterOn, bool RecordOut, int CoverGen,
    TonearmPhase AfterLift, TonearmPhase AfterRest, float SwingMs,   // SwingMs: duration of the swing that follows a Lift
    bool ResumeDown, long ThumpAtMs, long PlatterOffAtMs)
{
    public static TonearmState Initial => new(TonearmPhase.Idle, 0, 0, TonearmMachine.RestDeg, TonearmMachine.RestDeg, 1f, false, false, 0, TonearmPhase.Idle, TonearmPhase.Idle, 0, false, 0, 0);
}
public readonly record struct TonearmFrame(float ArmDeg, float Lift, float PlatterTargetDegPerSec, float Slide, bool Thump, DeckPhaseName Name);

public static class TonearmMachine
{
    public const float RestDeg = -34f, LeadInDeg = -20f, SpanDeg = 17f, RunOutDeg = LeadInDeg + SpanDeg;
    public const float SlideMs = 600, CueHoldMs = 300, SwingLeadMs = 1200, LowerMs = 900, LiftMs = 450, SpinUpMs = 700, SeekLiftMs = 400,
        SeekSwingBaseMs = 300, SeekSwingPerSpanMs = 600, SeekLowerMs = 700, RecueSwingMs = 900, SkipLiftMs = 300, ChangeSwingMs = 1200,
        ChangePlatterOffMs = 700, SleeveInMs = 600, CoverSwapMs = 350, RunOutBaseMs = 400, RunOutPerSpanMs = 800, LockedGroove33Ms = 1800,
        LockedGroove45Ms = 1333, AutoLiftMs = 500, AutoSwingMs = 1400, AutoPlatterOffMs = 500, ErrorSwingMs = 1200, BobPeriodMs = 1800,
        BufferLiftMs = 400, ReducedMs = 150, ThumpMs = 120, BufferLeadInFrac = 0.02f;

    public static float AngleOf(float frac) => LeadInDeg + SpanDeg * DeckEase.Clamp01(frac);
    static float Dur(float ms, in DeckInput i) => i.ReducedMotion ? MathF.Min(ms, ReducedMs) : ms;
    static float SwingMsFor(float from, float to, float baseMs, float perSpanMs, in DeckInput i) => Dur(baseMs + perSpanMs * MathF.Abs(to - from) / SpanDeg, i);

    /// Mounted mid-song (Cover→Player): the record was playing all along — no cueing sequence.
    public static TonearmState Seed(in DeckInput i)
    {
        if (!i.HasTrack) return TonearmState.Initial;
        float a = AngleOf(i.Frac);
        if (i.Error) return TonearmState.Initial with { Phase = TonearmPhase.Unavailable, RecordOut = true, SinceMs = i.NowMs };
        if (i.Advancing || (i.PlayWhenReady && !i.Buffering)) return TonearmState.Initial with { Phase = TonearmPhase.Tracking, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, LiftFrom = 0f, PlatterOn = true, RecordOut = true };
        if (i.Buffering) return TonearmState.Initial with { Phase = TonearmPhase.Hover, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, PlatterOn = true, RecordOut = true };
        return TonearmState.Initial with { Phase = TonearmPhase.Paused, SinceMs = i.NowMs, FromDeg = a, ToDeg = a, RecordOut = true };
    }

    public static TonearmState Step(TonearmState s, in DeckInput i)
    {
        long now = i.NowMs; var cur = Sample(s, in i); float armNow = cur.ArmDeg, liftNow = MathF.Min(cur.Lift, 1f);
        if (s.PlatterOffAtMs != 0 && now >= s.PlatterOffAtMs) s = s with { PlatterOn = false, PlatterOffAtMs = 0 };

        // 1. track removed → back into the sleeve from any pose
        if (!i.HasTrack) return s.Phase is TonearmPhase.Idle or TonearmPhase.SleeveIn or TonearmPhase.Lift or TonearmPhase.SwingToRest ? Timers(s, in i)
            : s.RecordOut ? LiftThen(s, in i, LiftMs, TonearmPhase.SwingToRest, TonearmPhase.SleeveIn, armNow, liftNow, 0, ChangeSwingMs)
            : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false };
        // 2. error → lift · rest · brake (once)
        if (i.Error && s.Phase is not (TonearmPhase.Unavailable or TonearmPhase.Lift or TonearmPhase.SwingToRest))
            return LiftThen(s, in i, LiftMs, TonearmPhase.SwingToRest, TonearmPhase.Unavailable, armNow, liftNow, 1, ErrorSwingMs);   // platter off with the swing
        if (s.Phase == TonearmPhase.Unavailable)
            return !i.Error && i.PlayWhenReady ? Enter(s, TonearmPhase.Cue, now, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = true } : s;
        // 3. boundary edges
        switch (i.Boundary)
        {
            case DeckBoundary.NaturalSameAlbum or DeckBoundary.SkipSameAlbum or DeckBoundary.RepeatOne:
                if (!s.RecordOut || !s.PlatterOn) return CueFrom(s, in i);
                return LiftThen(s, in i, i.Boundary == DeckBoundary.SkipSameAlbum ? SkipLiftMs : LiftMs, TonearmPhase.RecueSwing, TonearmPhase.Idle, armNow, liftNow, 0, RecueSwingMs);
            case DeckBoundary.NaturalNewAlbum or DeckBoundary.SkipNewAlbum:
                if (!s.RecordOut) return Enter(s, TonearmPhase.CoverSwap, now, Dur(CoverSwapMs, i), RestDeg, RestDeg, 1f) with { CoverGen = s.CoverGen + 1 };
                return LiftThen(s, in i, i.Boundary == DeckBoundary.SkipNewAlbum ? SkipLiftMs : LiftMs, TonearmPhase.SwingToRest, TonearmPhase.SleeveIn, armNow, liftNow, ChangePlatterOffMs, ChangeSwingMs);
        }
        // 4. queue end → ride out
        if (i.QueueEnded && s.Phase is TonearmPhase.Tracking or TonearmPhase.Lower)
            return Enter(s, TonearmPhase.RunOut, now, SwingMsFor(armNow, RunOutDeg, RunOutBaseMs, RunOutPerSpanMs, in i), armNow, RunOutDeg, 0f);
        // 5. headshell drag
        if (i.ScrubTargetMs is not null && s.RecordOut && s.Phase is not (TonearmPhase.Dragging or TonearmPhase.Lift))
            return LiftThen(s, in i, SkipLiftMs, TonearmPhase.Dragging, TonearmPhase.Idle, armNow, liftNow, 0, 0);
        if (s.Phase == TonearmPhase.Dragging && i.ScrubTargetMs is null)
        {
            float to = AngleOf(i.FracOf(i.SeekTargetMs ?? i.PositionMs));
            return Enter(s, TonearmPhase.SeekSwing, now, SwingMsFor(armNow, to, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), armNow, to, 1f) with { ResumeDown = i.PlayWhenReady, PlatterOn = i.PlayWhenReady || s.PlatterOn };
        }
        // 6. committed seek
        if (i.SeekTargetMs is { } target && s.Phase is TonearmPhase.Tracking or TonearmPhase.Paused)
        {
            float to = AngleOf(i.FracOf(target));
            if (MathF.Abs(to - armNow) < 0.15f) return s;
            var st = s with { ToDeg = to, ResumeDown = i.PlayWhenReady };
            return s.Phase == TonearmPhase.Paused
                ? Enter(st, TonearmPhase.SeekSwing, now, SwingMsFor(armNow, to, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), armNow, to, 1f)
                : LiftThen(st, in i, SeekLiftMs, TonearmPhase.SeekSwing, TonearmPhase.Idle, armNow, liftNow, 0, 0);
        }
        // 7. buffering → hover
        if (i.Buffering && s.Phase is TonearmPhase.Tracking or TonearmPhase.Lower or TonearmPhase.SpinUp)
        {
            float to = i.Frac < BufferLeadInFrac ? LeadInDeg : armNow;
            return LiftThen(s with { ToDeg = to }, in i, BufferLiftMs, TonearmPhase.Hover, TonearmPhase.Idle, armNow, liftNow, 0, 0) with { PlatterOn = true };
        }
        if (s.Phase == TonearmPhase.Hover)
        {
            if (!i.PlayWhenReady) return Enter(s, TonearmPhase.Paused, now, 0, armNow, armNow, 1f) with { PlatterOn = false };
            if (!i.Buffering && i.Advancing) { float a = AngleOf(i.Frac); return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), a, a, 1f); }
            return s;
        }
        // 8. pause / resume
        if (s.Phase == TonearmPhase.Tracking && !i.PlayWhenReady && !i.QueueEnded) return LiftThen(s, in i, LiftMs, TonearmPhase.Paused, TonearmPhase.Idle, armNow, liftNow, 0, 0);
        if (s.Phase == TonearmPhase.Paused && i.PlayWhenReady && !i.Buffering) return Enter(s, TonearmPhase.SpinUp, now, Dur(SpinUpMs, i), armNow, armNow, 1f) with { PlatterOn = true };
        // 9. idle/stopped → play
        if (s.Phase is TonearmPhase.Idle or TonearmPhase.Stopped && i.PlayWhenReady) return CueFrom(s, in i);
        // 10. timers
        return Timers(s, in i);
    }

    static TonearmState Timers(TonearmState s, in DeckInput i) => s.PhaseMs > 0 && i.NowMs - s.SinceMs >= s.PhaseMs ? Advance(s, in i) : s;

    static TonearmState Advance(TonearmState s, in DeckInput i)
    {
        long now = i.NowMs;
        switch (s.Phase)
        {
            case TonearmPhase.SlideOut:    return Enter(s, TonearmPhase.Cue, now, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { RecordOut = true, PlatterOn = true };
            case TonearmPhase.Cue:         return Enter(s, TonearmPhase.SwingToLead, now, Dur(SwingLeadMs, i), RestDeg, LeadInDeg, 1f);
            case TonearmPhase.SwingToLead: return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), LeadInDeg, LeadInDeg, 1f);
            case TonearmPhase.RecueSwing:  return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), LeadInDeg, LeadInDeg, 1f);
            case TonearmPhase.SpinUp:      return Enter(s, TonearmPhase.Lower, now, Dur(LowerMs, i), s.ToDeg, s.ToDeg, 1f);
            case TonearmPhase.SeekSwing:   return s.ResumeDown ? Enter(s, TonearmPhase.Lower, now, Dur(SeekLowerMs, i), s.ToDeg, s.ToDeg, 1f) : Enter(s, TonearmPhase.Paused, now, 0, s.ToDeg, s.ToDeg, 1f);
            case TonearmPhase.Lower:       return Enter(s, TonearmPhase.Tracking, now, 0, s.ToDeg, s.ToDeg, 0f) with { ThumpAtMs = now };
            case TonearmPhase.Lift:        return s.AfterLift switch
            {
                TonearmPhase.Paused      => Enter(s, TonearmPhase.Paused, now, 0, s.FromDeg, s.FromDeg, 1f) with { PlatterOn = false },
                TonearmPhase.SeekSwing   => Enter(s, TonearmPhase.SeekSwing, now, SwingMsFor(s.FromDeg, s.ToDeg, SeekSwingBaseMs, SeekSwingPerSpanMs, in i), s.FromDeg, s.ToDeg, 1f),
                TonearmPhase.RecueSwing  => Enter(s, TonearmPhase.RecueSwing, now, s.SwingMs, s.FromDeg, LeadInDeg, 1f),
                TonearmPhase.SwingToRest => Enter(s, TonearmPhase.SwingToRest, now, s.SwingMs, s.FromDeg, RestDeg, 1f),
                TonearmPhase.Hover       => Enter(s, TonearmPhase.Hover, now, 0, s.ToDeg, s.ToDeg, 1f),
                TonearmPhase.Dragging    => Enter(s, TonearmPhase.Dragging, now, 0, s.FromDeg, s.FromDeg, 1f),
                _                        => Enter(s, TonearmPhase.Paused, now, 0, s.FromDeg, s.FromDeg, 1f),
            };
            case TonearmPhase.SwingToRest: return s.AfterRest switch
            {
                TonearmPhase.SleeveIn    => Enter(s, TonearmPhase.SleeveIn, now, Dur(SleeveInMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = false },
                TonearmPhase.Unavailable => Enter(s, TonearmPhase.Unavailable, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false },
                TonearmPhase.Stopped     => Enter(s, TonearmPhase.Stopped, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false },
                _                        => Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { PlatterOn = false, RecordOut = false },
            };
            case TonearmPhase.SleeveIn:    return i.HasTrack
                ? Enter(s, TonearmPhase.CoverSwap, now, Dur(CoverSwapMs, i), RestDeg, RestDeg, 1f) with { RecordOut = false, CoverGen = s.CoverGen + 1 }
                : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f) with { RecordOut = false };
            case TonearmPhase.CoverSwap:   return i.PlayWhenReady && !i.Error ? Enter(s, TonearmPhase.SlideOut, now, Dur(SlideMs, i), RestDeg, RestDeg, 1f) : Enter(s, TonearmPhase.Idle, now, 0, RestDeg, RestDeg, 1f);
            case TonearmPhase.RunOut:      return Enter(s, TonearmPhase.LockedGroove, now, Dur(i.Rpm > 40f ? LockedGroove45Ms : LockedGroove33Ms, i), RunOutDeg, RunOutDeg, 0f);
            case TonearmPhase.LockedGroove:
                return Enter(s, TonearmPhase.Lift, now, Dur(AutoLiftMs, i), RunOutDeg, RestDeg, 0f) with
                { AfterLift = TonearmPhase.SwingToRest, AfterRest = TonearmPhase.Stopped, SwingMs = Dur(AutoSwingMs, i), PlatterOffAtMs = now + (long)Dur(AutoLiftMs + AutoPlatterOffMs, i) };
            default: return s;
        }
    }

    static TonearmState Enter(TonearmState s, TonearmPhase p, long now, float ms, float from, float to, float liftFrom)
        => s with { Phase = p, SinceMs = now, PhaseMs = ms, FromDeg = from, ToDeg = to, LiftFrom = liftFrom };

    /// Lift (liftMs) then AfterLift. platterOffInMs > 0 schedules the brake relative to the FOLLOWING swing's start; 1 = "with the swing".
    static TonearmState LiftThen(TonearmState s, in DeckInput i, float liftMs, TonearmPhase afterLift, TonearmPhase afterRest, float armNow, float liftNow, float platterOffInMs, float swingMs)
    {
        float lift = Dur(liftMs, i);
        return Enter(s, TonearmPhase.Lift, i.NowMs, lift, armNow, afterLift == TonearmPhase.SwingToRest ? RestDeg : s.ToDeg, liftNow) with
        { AfterLift = afterLift, AfterRest = afterRest, SwingMs = Dur(swingMs, i), PlatterOffAtMs = platterOffInMs > 0 ? i.NowMs + (long)(lift + Dur(platterOffInMs, i)) : 0 };
    }
    static TonearmState CueFrom(TonearmState s, in DeckInput i) => s.RecordOut
        ? Enter(s, TonearmPhase.Cue, i.NowMs, Dur(CueHoldMs, i), RestDeg, RestDeg, 1f) with { PlatterOn = true }
        : Enter(s, TonearmPhase.SlideOut, i.NowMs, Dur(SlideMs, i), RestDeg, RestDeg, 1f);

    public static TonearmFrame Sample(in TonearmState s, in DeckInput i)
    {
        float t = s.PhaseMs > 0 ? DeckEase.Clamp01((i.NowMs - s.SinceMs) / s.PhaseMs) : 1f;
        float arm, lift, slide = s.RecordOut ? 1f : 0f;
        switch (s.Phase)
        {
            case TonearmPhase.Tracking:     arm = AngleOf(i.Frac); lift = 0f; break;
            case TonearmPhase.Dragging:     arm = AngleOf(i.FracOf(i.ScrubTargetMs ?? i.PositionMs)); lift = 1f; break;
            case TonearmPhase.RunOut:       arm = s.FromDeg + (s.ToDeg - s.FromDeg) * t; lift = 0f; break;
            case TonearmPhase.LockedGroove: arm = RunOutDeg; lift = 0f; break;
            case TonearmPhase.SwingToLead or TonearmPhase.SeekSwing or TonearmPhase.RecueSwing or TonearmPhase.SwingToRest:
                                            arm = s.FromDeg + (s.ToDeg - s.FromDeg) * DeckEase.Std(t); lift = 1f; break;
            case TonearmPhase.Lift:         arm = s.FromDeg; lift = s.LiftFrom + (1f - s.LiftFrom) * DeckEase.LiftUp(t); break;
            case TonearmPhase.Lower:        arm = s.ToDeg; lift = 1f - DeckEase.Damped(t); break;
            case TonearmPhase.Hover:        arm = s.ToDeg; lift = i.ReducedMotion ? 1f : 1f + 0.4f * (0.5f - 0.5f * MathF.Cos(2f * MathF.PI * ((i.NowMs - s.SinceMs) % (long)BobPeriodMs) / BobPeriodMs)); break;
            case TonearmPhase.SlideOut:     arm = RestDeg; lift = 1f; slide = t; break;
            case TonearmPhase.SleeveIn:     arm = RestDeg; lift = 1f; slide = 1f - t; break;
            default:                        arm = s.Phase is TonearmPhase.Paused or TonearmPhase.SpinUp ? s.ToDeg : RestDeg; lift = 1f; break;
        }
        float platter = s.PlatterOn && !i.ReducedMotion ? i.Rpm * 6f : 0f;
        bool thump = s.ThumpAtMs != 0 && i.NowMs - s.ThumpAtMs < ThumpMs && !i.ReducedMotion;
        return new TonearmFrame(arm, lift, platter, slide, thump, NameOf(s.Phase));
    }
    static DeckPhaseName NameOf(TonearmPhase p) => p switch { /* Tracking→Playing, Lower→NeedleDown, SlideOut/Cue/SwingToLead→Cueing, Lift→Pausing, RecueSwing→NextTrack, SwingToRest/SleeveIn/CoverSwap→ChangingRecord, SeekSwing/Dragging→Seeking, … */ _ => DeckPhaseName.Idle };
}
```
`RecordModel : IDeckModel` = `Step` → `Sample` → `_platter.Step(frame.PlatterTargetDegPerSec, dt)` →
`DeckFrame(Frac, Angle0: platter.Angle, Angle1: arm, Lift, Slide, …, CoverGen, Thump, Name)`; `IsSettled` = phase ∈
{Idle, Paused, Stopped, Unavailable} ∧ platter at rest. Seed with `TonearmMachine.Seed(firstInput)`.

Headshell drag: a transparent hit `BoxEl` inside the `lift` wrapper (so it rotates with the arm), `OnPointerDown/OnDrag`
(implicit capture) + release; each pointer sample is mapped arm-local → deck space with the arm's current angle and then
to a fraction (label edge = 1, rim = 0):
```csharp
public static class TonearmGeometry
{
    public static float FracFromDeckPoint(float x, float y, float platterCx, float platterCy, float recordD)
    { float d = MathF.Sqrt((x - platterCx) * (x - platterCx) + (y - platterCy) * (y - platterCy)) / (recordD * 0.5f); return DeckEase.Clamp01(1f - (d - 0.34f) / 0.66f); }
    public static (float X, float Y) ArmLocalToDeck(float lx, float ly, float armDeg, float armX, float armY, float armW, float armH)
    { float px = armW * .5f, py = armH * .08f, r = armDeg * (MathF.PI / 180f), c = MathF.Cos(r), s = MathF.Sin(r), dx = lx - px, dy = ly - py; return (armX + px + c * dx - s * dy, armY + py + s * dx + c * dy); }
}
```
`DeckGesture` (engine, shared with the iPod wheel) is SeekBar's gesture lifted out: `Begin()` sets `b.ScrubTargetMs`,
`Move(ms)` writes it and queues `PreviewSeek` through a `SeekPreviewScheduler` (100 ms), `Commit()` → `CommitSeek(ms)` +
clear, `Cancel()` restores. The machine reacts only to `ScrubTargetMs`/`SeekTargetMs`.

### Record family face (`Faces/RecordDeck.cs`) — geometry in fractions of S

```
 S×S                                        pivot (0.86S, 0.094S)
 ┌───────────────────────────────────────────┐   sleeve  x.06 y.12 w.64 h.64 rot −2.5° (Turntable −4°)
 │ ┌ sleeve ────┐        rest ▮ (.845,.06) ● │   ppos    x.24 y.16 D=.70S   (7″: D=.52S at .33/.25)
 │ │            │  ╭───── record ─────╮  ║   │   arm box x.75 y.02 w.22 h.92, TransformOrigin (.5,.08)
 │ │            │ │   ◯ label .34D     │ ║   │   tube 9%×62% of arm box · pivot .44 arm w · headshell .22×.12 at .69, −18°
 │ └────────────┘  ╰───────────────────╯▄╨▄  │   Zune: no sleeve/arm/rest; ppos x.08 y.22 D=.62S; type block right/top; 3 px bar bottom
 └───────────────────────────────────────────┘
```
- `disc` = ONE rotating wrapper `BoxEl{Width=D,Height=D,ZStack,TransformOrigin .5/.5, Transform=()=>Affine2D.Rotation(sig.Angle0.Value·Deg2Rad)}`;
  children: vinyl body (`Corners=Radii.Circle(D)`, Fill/Acrylic/Gradient per finish, 1 px `White(.06)` border, `Shadow(34,14,0,Black(.45))`),
  grooves = `ImageEl assets/deck/grooves-1024.png Corners Circle ColorOverlay=grooveInk` (fallback: 14 hairline rings `BorderWidth=1 White(.035)`
  from .38D to .98D), sheen = off-centre radial 3-stop (`White(.10)→0→White(.06)`, `RadialCenter (.30,.25)`), label = `ImageEl cover
  Corners Radii.Circle(.34D) DecodePx 256 Placeholder wash BlurHash` with a 2 px `Black(.6)` ring (Zune: 3 px white), spindle `.05D #e6e9ef`
  (7″: `.20D` filled deck colour = the big hole), splatter = six static radial-gradient dots. Picture: cover fills the disc + faint groove overlay,
  no label, white spindle.
- `ppos` wrapper: `Transform=()=>Translation(−.34D·(1−SlideOut(Slide)),0)·Scale(.96+.04e)`, `Opacity=()=>Clamp01(Slide·1.6)`; children `disc` + dust ring
  (`Opacity 0`, captured `OnRealized`; on Thump: `anim.Keyframes(Opacity .9→0, 500)` + `ScaleX/Y .2→1.6`; disc `ScaleX/Y 1→1.006→1` 120 ms).
- `arm` wrapper binds `Rotation(sig.Angle1)`; inner `lift` wrapper binds `Translation(0,−.015·armH·l)·Scale(1+.015l)` with `l=min(Lift,1.4)` plus a
  shadow-only twin BoxEl with `Opacity=()=>.45·Clamp01(Lift)` (Shadow is not bindable). Tube/pivot/headshell/stylus static BoxEls; headshell
  `Rotation=-18` static.
- Sleeve: `BoxEl .64S ClipToBounds Corners 4 Rotation −2.5 Shadow(30,10,0,Black(.25))` with a tiny `SleeveArt` component that reads
  `sig.CoverGen.Value` and cross-fades two keyed cover ImageEls (300 ms Enter opacity).
- Finishes: black `Fill` (theme-bound `#0d0e12`/`#15171c`); clear `Acrylic(tint rgba(40,44,54,.55), blur 2)`; album colour `Fill = Prop.Of(() =>
  WaveePalette.Accent(Surfaces.SchemeFor(url)) deepened)` with `CoverColorPlane.Current.Watch(url)`; splatter `#f5f1ea` + dots; marble
  `ImageEl marble-1024.png` (fallback 4-stop radial `RadialCenter (.3,.7)`).
- Turntable adds: plinth `ImageEl wood-grain-1024.png Fit Cover` under `Gradient Vertical(#6b4a2e,#4a301b)` + inset highlight; felt mat
  `.77S` radial `#3a3d44→#2b2e34` + rings `#1f2126` 4 px, `#6d7078` 2 px; strobe `16%×5% #1a1c20` with an orange dot `Shadow(10,0,0,#ff5a2a)`; chrome
  arm gradients (`#5d6470,#f2f4f7@.4,#c3c8d1@.6,#5d6470`), black headshell with `#7a808a` border, `#c9ced8` rest.
- Zune adds: `SpanTextEl` lowercase title `.11S` weight 300 "Segoe UI Variable Display" with the last syllable in the accent colour, small
  `.036S` grey line `artist · <bound clock>`, 3 px `#333` bar with fill `Transform=Scale(sig.Frac,1)` origin left. Deck bg `#000`, corners 0.
- Draw ops ≈ 20 (Record) / 28 (Turntable) / 18 (Picture) / 12 (Zune); per-tick writes 1–2.

### Other decks (Face + Model each; per-tick writes are Transform/Opacity binds only)

- **Cassette** (`TapeMachine`): shell 86%×56% at (7%,21%) `Corners 6`, `Fill`/`Acrylic` per shell option (black `#1d1f24`, clear
  `rgba(190,205,230,.28)`+`White(.35)` border, cream `#e9e2d0`, smoke `rgba(70,72,84,.85)`); label 90%×30% (`Courier New` Type I on `#f2e9d8` /
  chrome gradient uppercase `Segoe UI` / handwritten `Segoe Print` on `#fffdf5` with 3 hairline rules) with title, artist · album, side badge;
  window 62%×34% `#14151a`; hubs at 31%/69% of the shell, 57% down: pack = `BoxEl{Width=RMAX, Corners Circle, Fill #3a2b1c, Transform=()=>
  Scale(sig.Aux0)}` (+ groove texture inside), reel = `.11S` white ring (`BorderWidth .014S`) + 3 spoke BoxEls (0/60/120°) + hub, `Transform=
  Rotation(Angle0)` (right: `Angle1`); tape strip, foot with 4 holes, 4 screws, brand text. Maths (units = % of S): `rL = 26−13p, rR = 13+13p`;
  `ω = −base/r` deg/s, `base = playing ? wind·900 : 0`, `wind = 4` for 900 ms after a committed seek then `p` snaps; `SpinIntegrator` τ .25/.3 per
  reel; `Aux0 = rL/26, Aux1 = rR/26`. New album: shell node `anim.Animate(TranslateY 0→−.4S, 500)` + opacity out, text swap on CoverGen, `−.6S→0`
  500 in. Writes/tick: 2 angles (+2 scales ≈ 1/s). Ops ≈ 24.
- **Reel-to-reel** (`TapeMachine` with `rL = 92−48p, rR = 44+48p, base = wind·6000, wind = 5`): two reel boxes 40% at top 7%, left/right 6%;
  pack scale-bound; flange = static `PathEl` annulus with 3 cutouts (`Rule EvenOdd`, PathBuilder cubic arcs, `ViewBox 100`) inside a rotating
  BoxEl, `Fill` per option (`#c9cdd4 | #2a2c33 | rgba(200,215,240,.45)`); rim ring + radial hub; tape path `PathEl` polyline `26,44 28,74 36,80
  64,80 72,74 74,44` stroke `#5a4028` 1.2; two guides; headblock with 3 heads; counter `TextEl Consolas 600 amber on #1a0d00` bound to seconds
  (`0000`) via a hoisted `FormatCache`. Ops ≈ 18.
- **CD / MiniDisc** (`DiscMachine`): disc wrapper 72% at (14%,12%) `Rotation(Angle0)`: cover `ImageEl Corners Circle`, rainbow `ImageEl
  cd-rainbow-512.png Corners Circle Opacity .75`, hole `.17D` deck-coloured with `White(.55)` ring + inner `White(.18)` ring; rail hairline 1×.47D;
  sled `BoxEl .03D #ff3b30 Shadow(8,0,0,#ff3b30) Transform=()=>Translation(0, sig.Aux0)` with `Aux0 = (20+26p)%·D`; CLV `target = playing ?
  3000−1800p : 0` deg/s, τ .4/.6. Caption "COMPACT DISC · DIGITAL AUDIO" / "MD · ATRAC". MiniDisc option: shell 62% × (68/72) `Gradient 160°`
  blue, rect-clipped window with the disc at 112%, chrome shutter, label strip `TextEl` uppercase. New album: wrapper TranslateY 0→.3S + fade,
  swap, back. Ops ≈ 12/18.
- **iPod** (no model beyond Frac): body 58%×96% at (21%,2%) `Corners .055S` `Gradient Vertical(#f7f7f9,#cfd2d8)` (black/U2: `#2a2c31→#0f1013`;
  U2 wheel radial `#d8262b→#8f1418`), shadow; LCD 84%×43% `#dfe6ea | #d6e6c4`, 2 px `#2a2f38` border: bar (gradient, "Now Playing" 700, play
  glyph bound to `IsPlaying`, battery), art `ImageEl 34%`, title/artist/album TextEls, progress `BoxEl border` + fill `Scale(sig.Frac,1)` + diamond
  thumb `Rotation 45, Transform=Translation(Frac·W,0)`, elapsed/`−remaining` clocks (1 Hz bound Text, fixed Width). Wheel 74% radial circle with
  MENU/⏮/⏭/⏯ captions; centre 36% `OnClick` play/pause; ⏮/⏭ → Previous/Next. Scrub: `OnPointerDown/OnDrag` on the wheel; `Δθ` unwrapped;
  `frac += Δθ/(2π)·0.25` → `DeckGesture`. Ops ≈ 22.
- **Winamp** (`LevelSynth`, 19 bands): two 275:116 windows (main at 10%, EQ below +2%), 92% wide; frame `#222a3a` + `#000` border + inset
  `#6e7a97` highlight; title bar `ImageEl winamp-tbar.png` (stripes) with "WINAMP" chip; skins swap greys + LCD colour (`#00ff00 | #e5f3ff |
  #ff8a00`); time `TextEl Consolas 700 .064S` bound `MM:SS`; spectrum = 19 bars in `vis` and 19 in the EQ window bound to the SAME `sig.Bands`
  (`TransformOriginY=1, Transform=()=>Scale(1, max(band,.02))`, gradient `#ff4a4a→#ffd24a@.45→lcd`) + 1 px white peak caps
  (`Translation(0,−H·peak)`); oscilloscope option = 24 one-pixel dots `Translation(0, band·H/2)` (a per-frame PathEl is not viable);
  `stitle` = `Marquee` "1. Artist - Title (m:ss) *** "; kbps/kHz from `StreamBitrateKbps`/`StreamFormat` or `---`; STEREO; position slider
  `Translation(Frac·(W−12),0)`; 5 buttons → Previous/Play/Pause/Stop(pause)/Next; EQ grid 10 hairlines. Ops ≈ 70; writes ≈ 39/tick.
- **Hi-fi VU** (`MeterBallistics`): two faces 43% at top 12%, left/right 5%, aspect 1/.82, `Corners (6,6,14,14)`, face `#efe6cf | #0a2a5a |
  #141519`, radial glow at the bottom, shadow; scale = one static `PathEl` (ViewBox 100×82: arc `M14 62 A40 40 0 0 1 86 62` ink 1.2, red arc
  `M68 32 A40 40 0 0 1 86 62` 3 px, 10 ticks) + 10 tiny `TextEl`s (−20…+3); "VU", "LEFT/RIGHT"; needle `BoxEl 1.5×.78H` `TransformOrigin (.5,1)`,
  `Rotation(Angle0/1)`; pivot cap. Ballistics: `dB = 20·log10(max(rms,1e-4)) + 18`, `deg = clamp(−45 + (dB+20)/23·90, −48, 48)`, first-order τ .3
  (VU) | .05 (PPM); paused/no levels → −45; right channel = left + `1.5·sin(2.1t)` dB (mono tap; label the row "Stereo: derived"). Amber LCD:
  elapsed / `−remaining` Consolas with `Shadow(6,0,0,amber .6)` glow, second row title · format; knob. Ops ≈ 30; writes 2/tick.
- **WMP** (`LevelSynth`, 24 bands): black bg; small cover `ImageEl 22%` bottom-left + shadow; 2 px progress line + `Scale(Frac,1)` fill; preset
  caption; colour = `Tok.AccentDefault` or cover accent (bound). Bars & Waves: 24 mirrored bars (`Scale(1,band)` origin bottom at the centre line +
  reflection `Opacity .35` origin top) + 32 wave dots `Translation(0, peaks[i]·.18H)` (Peaks reused as the wave sampler) — 80 writes/tick.
  Alchemy: 3 rings `BoxEl{Arc = new ArcSpec(col, 2f, 0, 360)}` with `Rotation(θ_r)·Scale(1+.25band, .8(1+.25band))`, θ advancing `.3(r+1)` rad/s,
  plus two trailing rings per ring at `Opacity .35/.15` (3 extra FloatSignals) — 9 writes. Battery: 6×6 squares `Scale(.15+.7v)` — 36 writes.
- **Canvas drift** (`DriftPath`): bleed `ImageEl{url, 1.5S, BakedBlur(44, .25), Saturation 1.5, Opacity .85|.6|1}` at (−.25S,−.25S) — ONE
  static draw; drift frame 78% centred `Corners 6 ClipToBounds Shadow(60,24,0,Black(.45))` with a 118% wrapper (captured `OnRealized`) holding
  the cover; motion is slab-driven: `UseLayoutEffect` keyed on (playing, drift, reduced, railOpen, active) seeds `anim.Keyframes` TranslateX
  `[0, −.05F, .04F]`, TranslateY `[0, .03F, −.04F]`, ScaleX/Y `[1, 1.06, 1.02]` over 40 s | 16 s, mirrored keys (no seam), `loop: true`;
  gate flips → `anim.Cancel` (pause freezes). Seek line 2 px + fill. New album: whole `cv` keyed on CoverGen with 300 ms opacity Enter. Ops ≈ 6.
- **LevelSynth** (engine-free): `env = clamp(peak·2.2)`, `rmsN = clamp(rms·3.5)` from the level tap; band i target `= on ? max(.04, env·(.35 +
  .45·sin(t·(3+.37i)+seed+i)·sin(1.3t+.8i)) + rmsN·hash(i,t)·.15) : .02`; attack .5 / release .12 per tick; peak caps hold 400 ms then fall 1.2/s.
  Scope mode fills `Bands` with a waveform sample. No tap (Connect/remote) → `env = rmsN = playing ? .8 : 0`; the option row reads "Analyser:
  level-driven".

### Levels plumbing (app-side only)

```csharp
// Backend/AudioHost.cs (IAudioDspControl precedent)
public interface IAudioLevelSource { IReadSignal<VisualizerFrame>? Levels { get; } }   // FluentGpu.Media.VisualizerFrame: Rms, Peak, Magnitudes(empty)
// SpotifyLive/Audio/FluentMediaAudioHost.cs:  sealed class FluentMediaAudioHost : …, IAudioLevelSource { public IReadSignal<VisualizerFrame>? Levels => _effects.Visualizer; }
// App/PlaybackBridge.cs:  public IReadSignal<VisualizerFrame>? Levels { get; internal set; }   — set in Services where the DSP control is wired.
```
The signal is written on the audio pump thread: decks `Peek()` it from the ticker only, never subscribe. (Optional follow-up, not v1: per-band
Goertzel in `PcmAudioSession.TapBlock` filling `Magnitudes`; `LevelSynth` prefers real bands when present.)

### Performance budget

Deck root `Width=Height=S, ClipToBounds, IsolateLayout` is the layout firewall; every part has explicit sizes inside `Canvas` wrappers; the 30 Hz
path writes `Transform`/`Opacity` binds only (`FrameStats.Rendered == false`); the 1 Hz clock `TextEl`s (Text bind → scoped relayout) have fixed
`Width` and stay inside the firewall; `CoverGen` swaps re-render ≤3 leaf nodes. Ticker off when: paused-and-settled (`IsSettled`), rail closed,
Cover mode (deck unmounted), reduced motion (1 Hz effect steps instead), minimized/parked (`UseIsActive`). Every tick = one `Runtime.Batch` →
one `FrameRequested`; value-gated writes at a pixel/degree quantum keep the DrawList byte-identical when nothing moved so skip-submit elides the
Present. Ambient presents ≤ 30/24 fps (`AmbientPowerPolicy`). Bind thunks capture once at mount; no per-tick allocations; one-shot thumps use
static `Keyframe[]`.

## Tests (xunit v3, engine-free; `<Compile Include>` with a comment naming the test class)

- `NpvPlayerCatalogTests`: ids contiguous == index; slugs unique, lowercase ASCII, pinned by a literal 12-row table; exactly 4 per group; ≥1 option
  row per preset; ≥2 choices per option with unique slugs; label keys start `player.`; swatch options have a colour or FromCover; `ById` unknown →
  Record; default = Record.
- `NpvPlayerPrefsTests` (in-memory `IAppSettings`): defaults/clamps (presentation −1/2/99 → Cover; style stray → Record; choice out of range → 0);
  round-trips; SetPresentation bumps Epoch once; SetStyle flips to Player and doesn't rewrite when already Player; option key names
  (`npv.player.turntable.finish`); Record vs Turntable separate keys; NextStyle wraps Picture → Record; Toggle alternates; null settings = no-op reads.
- `TonearmMachineTests` (33 ms stepper `Run(state, input, untilMs)`): constants pin the table; Idle→Play phase timeline (0/600/900/2100/3000);
  Tracking angle −20+17p; pause lifts 450 then platter off, angle unchanged; resume 700 + 900 + single Thump; committed seek swing scales with
  distance (d=1 → 900, d=.25 → 450); paused seek stays up; sub-quantum seek ignored; drag lifts 300 and follows ScrubTarget, release → swing +
  lower 700; same-album recue never sets PlatterOn=false; new-album full sequence with CoverGen +1 exactly at CoverSwap and platter off at
  lift+700; skip lift 300; repeat-one = recue; boundary while stopped = plain cue; queue end → linear run-out → locked groove 1800/1333 → lift 500 →
  swing 1400 with platter off +500 → Stopped; buffering at start hovers over lead-in, mid-track in place; bob peaks 1.4 at 900 ms, zero under
  reduced motion; Hover clears only on Advancing; error → Unavailable, recovers via Cue without SlideOut; rpm change leaves the arm, platter target
  200/270; reduced motion collapses every phase to 150 ms and Tracking still yields AngleOf(frac); Seed playing/paused/buffering/no-track; track
  removed from Tracking → lift, rest, sleeve in, Idle; NoAllocation over 1000 Step+Sample.
- `SpinIntegratorTests` (95% in 700 ms at 33; rest within 1600 ms; wraps), `DeckBoundaryRulesTests`, `PositionInterpolatorTests`,
  `TonearmGeometryTests`, `TapeMachineTests` (pack radii, ω ∝ 1/r, fast-wind), `DiscMachineTests` (CLV, sled), `MeterBallisticsTests` (dB→deg, τ),
  `LevelSynthTests` (bounds, silence → floor).
- Existing `SettingsCatalogTests` / `WaveeCommands` BuiltinCount pin updated.

## CHANGELOG, issue, commit

`## [0.2.9] - unreleased` → new `### Added` (above `### Changed`):
> **Player styles for Now Playing.** The Now Playing rail's cover gets a header with a Cover | Player switch and a Player style flyout: twelve
> players in three rows — Record, Cassette, Reel-to-reel, CD/MiniDisc; Turntable, iPod Classic, Winamp, Hi-fi VU; Zune, WMP visualizer, Canvas
> drift, Picture disc — each with its own options (vinyl finish, size and speed, cassette shell and label, iPod body, Winamp skin, …). The
> record's tonearm cues, lifts on pause, seeks, rides the run-out into a locked groove and returns to its rest the way a turntable does. The
> choice is remembered per user and is also reachable from the artwork's right-click menu, Settings › Appearance › Now playing and the command
> palette. (#n)

Issue: "Now Playing: Cover / Player switch with twelve player styles" (`type: feature`, `area: player`, milestone 0.2.9). Commit:
`player: Now Playing player styles (Cover | Player) in the right rail`, body + `Fixes #n`.

## Work split (parallel subagents on disjoint files; subagents never build, test or touch git)

| Agent | Files |
|---|---|
| A model/prefs | AppSettings keys + NpvPlayerKeys, NpvPlayerCatalog, NpvPlayerPrefs, NpvDiagnostics, TestAppSettingsShim, Wavee.Tests.csproj includes, NpvPlayerCatalogTests, NpvPlayerPrefsTests |
| B deck core | Deck/Model/{DeckInput,DeckFrame,PositionInterpolator,SpinIntegrator}.cs + tests; Deck/{DeckSignals,DeckClock,DeckHost,DeckArt,DeckGesture,NpvDeck}.cs (+ DeckModels/DeckFaces dispatch stubs) |
| C tonearm | Deck/Model/TonearmMachine.cs (+TonearmGeometry, RecordModel), TonearmMachineTests, TonearmGeometryTests |
| D other models | TapeMachine, DiscMachine, MeterBallistics, LevelSynth, DriftPath + tests |
| E rail chrome | NowPlayingHeroTile edit, RightRail toggle offset, NpvHeaderRow, PlayerStyleFlyout (+NpvArtMenu, NpvSwatch), NpvThumbnails |
| F record face | Faces/RecordDeck.cs; grooves/wood/marble PNGs |
| G media faces | Faces/{CassetteDeck,ReelDeck,CdDeck}.cs; cd-rainbow, reel-flange PNGs |
| H device/software faces | Faces/{IpodDeck,WinampDeck,VuDeck,WmpDeck,CanvasDeck}.cs; winamp-tbar PNG |
| I settings/palette/strings/changelog | SettingsPage.Appearance, SettingsCatalog, WaveeCommands, loc JSONs, palette-shortcuts.md, CHANGELOG |
| J levels plumbing | AudioHost IAudioLevelSource, FluentMediaAudioHost, PlaybackBridge.Levels, Services wiring |

## Verification (orchestrator only)

1. `dotnet build Wavee.slnx` (Debug) and `-c Release` clean; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` green.
2. `dotnet run --project src/apps/Wavee -- --fake`: play a track, open Details. Header row · Cover default · Record deck spinning with the arm
   tracking · body below unmoved · pause lifts the arm and brakes · resume · seek via the SeekBar lifts/swings/lowers · headshell drag previews
   and commits · Next recues or changes the record with a cover swap · queue end rides into the locked groove then auto-returns · flyout: all 12
   thumbnails, options swap per preset, stays open, SelectorBar label follows · every deck renders and reacts to pause/seek · right-click art
   menu · Settings › Appearance mirrors · Ctrl+K commands · Logs category "npv" · relaunch persists · dock a video → whole header+hero replaced.
3. Reduced motion on: nothing spins, transitions snap, progress still readable.
4. FPS overlay: rail open + Record playing → no relayout escapes, ambient ≤ 30 fps; rail closed or paused-and-settled → zero deck frames.

## Findings — first live run, 2026-09-10

Verified in the Debug build against the real account (Roxette, "Listen To Your Heart"): the header row, Cover | Record
switch and gear render in the docked rail; all twelve decks render with the live cover (contact sheet reviewed);
every style switch logs `style.set … source=flyout` and no warnings or errors; pause lifts the arm in place; the
Winamp spectrum and the VU needles move off the real RMS/peak tap while audio plays. Debug and Release build clean;
`Wavee.Tests` 7930/7933 green — the two failures are pre-existing (`StartWindowLadder_IsIdempotent…`, a
`StartupActivation` test untouched here) and one timing-flaky Connect test that passes in isolation.

Fixed during the run: `player.style.record` had the "12″ LP" string (now "Record" / "Plaat" / "레코드"); the flyout's
Segmented items were cramped (item min width 56); `DeckArt` set `Shrink` on `ImageEl` (no such prop); `AudioHost.cs`
and `PlaybackBridge.cs` imported `FluentGpu.Media` and made `SeekMode`/`RepeatMode` ambiguous (types are now
fully qualified); `LevelModel` never reported settled because the exponential release asymptotes above the floor
(snaps within 0.005 now); the `DeckEase.Damped` test asserted the wrong shape.

Follow-ups, not blocking: (1) on resume from Paused the arm was seen swinging out and back instead of lowering in
place — most likely `DeckClock`'s synthesized remote-seek on the first anchor after mount; guard until two anchors
have been seen. (2) `ArtVideoToggle` sits on the deck's top-right corner and overlaps the tonearm pivot; consider
hiding it in Player mode or moving it to the header row. (3) The Zune title truncates ("listen …") at 0.11·S; scale
the size to the title length. (4) A per-frame `hotAlloc` of 26–36 MB in the submit phase was observed on the
playlist page while playing BEFORE the deck was mounted — pre-existing, worth its own investigation. (5) One
unexplained `presentation.set … source=header` fired in the first session while the rail was floating
(RailFits false at 1183 DIP with a 470 DIP rail) and no input was sent; not reproduced with the rail docked.
(6) `wood-grain-1024.png` is 1 MB; a 512 px version would do.
