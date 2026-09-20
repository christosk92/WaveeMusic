# Localization completeness — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dutch and Korean selectable in Settings → Language, with compile-time loc gates and a packet workflow so a non-technical reviewer can approve AI drafts without touching JSON.

**Architecture:** Nested JSON in git stays the source of truth (`assets/loc/{en-US,nl,ko-KR}.json`, resolved by `Loc.Get` / generated `Wavee.Strings`). Completeness is a Roslyn diagnostic in `FluentGpu.SourceGen` (already referenced by Wavee). Human translation never edits those files: a small `ops/loc` packer flattens keys into **packets** (JSON + optional XLSX), an AI step fills `ai_draft`, a static review page (or Google Sheet) is the only human UI, and `import` writes approved rows back into nested satellite JSON.

**Tech Stack:** FluentGpu loc pillar (`JsonResourceReader`, `MessageFormatter` ICU plural/select subset, `PseudoLocalizer`); `FluentGpu.SourceGen` (`LocalizationKeysGenerator`, FGRP008); Wavee `assets/loc/*.json`; new net10 console `ops/loc/Wavee.LocPack` (engine-free); optional LLM for drafts only.

**Spec:** this document. Inventory snapshot 2026-09-19.

## Global Constraints

- Nested loc JSON in `src/apps/Wavee/assets/loc/` is the only runtime table. Packets are working copies.
- Keys beginning with `$` are metadata (`$culture`, `$comment`, `$unusedAllow`) and are never user-facing strings.
- ICU is the engine subset: named `{placeholder}`, `{count, plural, one {…} other {…}}`, `{name, select, … other {…}}`. `#` inside a plural body is the count. Translators must not edit braces or placeholder names.
- Dutch plurals: `one` + `other`. Korean plurals: `other` only (already the pattern in `ko-KR.json`).
- Missing `Loc.Get` key renders as `[key]` (loud). Satellite miss falls back to en-US (silent English).
- No environment-variable feature switches. No source-text tests (decision classes are unit-tested; they do not grep production `.cs`).
- Component props freeze at mount; loc re-resolves via `CultureEpoch` — do not invent a second loc system.
- `nl` / `ko-KR` stay **disabled** in the language combo until FLLOC004 is clean (or an explicit remaining-miss allow-list exists).
- Do not translate Spotify catalog text, the diagnostics page (optional English), or the 225 unused en-US leftovers — prune those instead.
- RTL / BiDi layout is out of scope (ch 29 decision).
- Git commits never include co-author trailers. Do not commit API keys. Packets under `ops/loc/work/` may be committed; screenshots of the real account must not.

---

## File map

| Path | Role |
|---|---|
| `src/apps/Wavee/assets/loc/en-US.json` | Base culture. Feeds `Wavee.Strings`. `$unusedAllow` lives here. |
| `src/apps/Wavee/assets/loc/nl.json` | Dutch overrides. Incomplete until packets import. |
| `src/apps/Wavee/assets/loc/ko-KR.json` | Korean overrides. Same. |
| `..\fluent-gpu\src\FluentGpu.Controls\assets\loc\en-US.json` | Kit neutral floor (45 keys). Wavee satellites must override them. |
| `..\fluent-gpu\src\FluentGpu.SourceGen\Analyzers\MissingLocKeyAnalyzer.cs` | **Create.** FLLOC003 (literal key missing from base) + FLLOC005 (unused base key). |
| `..\fluent-gpu\src\FluentGpu.SourceGen\Analyzers\HardcodedKitStringAnalyzer.cs` | **Modify.** Arm FGRP008 in opted-in assemblies; phase-2 sinks. |
| `..\fluent-gpu\src\FluentGpu.SourceGen\Localization\LocalizationKeysGenerator.cs` | **Modify.** FLLOC004 satellite missing, FLLOC006 satellite extra. |
| `src/apps/Wavee/Wavee.csproj` | AdditionalFiles for nl/ko; `FluentGpuLocHardcodedAssemblies=Wavee`. |
| `src/apps/Wavee/.editorconfig` | Severity knobs. |
| `src/apps/Wavee/Screens/Settings.cs` | `Language.Enabled` — flip last. |
| `ops/loc/Wavee.LocPack/` | Pack / draft / import / xlsx console. Engine-free. |
| `ops/loc/glossary.json` | Terms that must stay consistent. |
| `ops/loc/surfaces.json` | Key-prefix → surface, where-text, screenshot id. |
| `ops/loc/keep-english.json` | Product names / autonyms / cognates left as source. |
| `ops/loc/review/index.html` | Static reviewer. Opens a packet file. No server. |
| `ops/loc/work/{nl,ko-KR}/*.json` | Packets in flight. |
| `ops/loc/screens/*.png` | Optional `--fake` captures, one per surface id. |
| `src/apps/Wavee.Tests/LocPackTests.cs` | Tests flatten/merge/ICU/placeholder QA (no source greps). |

Two repos: analyzer work lands in `fluent-gpu` first, then Wavee pins it. LocPack and packets are Wavee-only.

---

## Frozen inventory (2026-09-19)

| Table | Keys | vs en-US |
|---|---:|---|
| en-US | 2598 | source of truth |
| nl | 866 | 33%, **1735 missing**, 3 extras |
| ko-KR | 866 | same key set as nl |
| FluentGpu.Controls | 45 | Wavee overrides 1; **44 stay English** under any culture |

- `Loc.Get("…")` literals missing from en-US: **0**.
- Unused in Wavee + tests (quoted key or `Strings.*`): **225 (9%)**. Keep tests-only: `modulePage.openOn`, `sidebar.template.curated`, `sidebar.template.curatedSub`.
- Satellite extras (delete on import): `player.play`, `player.pause`, `settings.tabs.diagnostics`.
- nl identical-to-English: 124 (mostly `library.*` copy-paste). ko: 52 (same `library.*` plus autonyms, which stay).
- Complete satellites: `podcast`, `videoOverride`, `recents`. Almost: `sidebar` (311/331).
- `Language.Enabled = [true, true, false, false]` on purpose until tables are complete.

Hardcoded user-facing English still in C# (extract before packets, so they get translated):

| Where | Copy |
|---|---|
| YouTube / Twitch / Radio `PageAction` titles | `"Play"`, `"Open on YouTube"`, `"Open on Twitch"` |
| `Modules.Host` demo pages | `"Play"`, `"Open on GitHub"`, `"Play latest"` |
| File-picker filter **names** | `"Spotify DLL"`, `"All files"`, `"JPEG"`, `"Log text"` |
| `Settings.Receipts.GpuLine` | `"Weak"` / `"Strong"` / `"Unknown"` / `"software"` |
| `Deck.Faces` | `"Now Playing"`, `"MENU"`, `"STEREO"` (`player.nowPlaying` exists). `"VU"` is `// loc-allow`. |

`ConcertCopy.English` is a test fallback. Production uses `Localized`. Leave it.

---

### Task 1: FLLOC003 — literal loc key not in the base JSON

**Files:**
- Create: `..\fluent-gpu\src\FluentGpu.SourceGen\Analyzers\MissingLocKeyAnalyzer.cs`
- Modify: `..\fluent-gpu\src\FluentGpu.SourceGen.Tests\AnalyzerTests.cs`
- Modify: `..\fluent-gpu\src\FluentGpu.SourceGen.Tests\Harness.cs` (AdditionalText options per file, if not already usable from `Analyze`)

**Interfaces:**
- Consumes: flattened base keys from AdditionalFiles (`FluentGpuLocBase=true` or path `**/assets/loc/en-US.json`), same flatten as `LocalizationKeysGenerator` / `TinyJsonReader`.
- Produces: diagnostic `FLLOC003`, Warning, category `FluentGpu.Localization`.

- [ ] **Write the failing test**

```csharp
[Fact]
public void FLLOC003_Fires_On_LocGet_Literal_Missing_From_Base()
{
    var diags = Harness.Analyze(new MissingLocKeyAnalyzer(), """
        namespace FluentGpu.Localization { public static class Loc { public static string Get(string key) => key; } }
        class C { string M() => FluentGpu.Localization.Loc.Get("detail.nope"); }
        """,
        additionalTexts: ("assets/loc/en-US.json", "{\"detail\":{\"play\":\"Play\"}}"));
    Assert.Equal(1, Harness.Count(diags, "FLLOC003"));
}

[Fact]
public void FLLOC003_Silent_On_Literal_Present_In_Base()
{
    var diags = Harness.Analyze(new MissingLocKeyAnalyzer(), """
        namespace FluentGpu.Localization { public static class Loc { public static string Get(string key) => key; } }
        class C { string M() => FluentGpu.Localization.Loc.Get("detail.play"); }
        """,
        additionalTexts: ("assets/loc/en-US.json", "{\"detail\":{\"play\":\"Play\"}}"));
    Assert.Equal(0, Harness.Count(diags, "FLLOC003"));
}
```

Extend `Harness.Analyze` the same way `Harness.Generate` already accepts additional texts. If `Analyze` cannot take AdditionalFiles today, add an overload; do not skip the test.

- [ ] **Implement**

Arm only when a base loc AdditionalFile exists. On `CompilationStart`, flatten it. On `InvocationExpression` whose target is `Loc.Get|Format|Bind|BindF` or `Localization.Get|Format`, if argument 0 is a string literal and the value is not in the table → `FLLOC003`. Silent on `// loc-allow`, empty keys, and non-literals (`Strings.*`, locals).

Message: `The loc key '{0}' is not in the base culture JSON. Loc.Get will render [{0}].`

- [ ] **Run `dotnet test src/FluentGpu.SourceGen.Tests/FluentGpu.SourceGen.Tests.csproj` (engine repo).** Expect the new tests green.
- [ ] **Commit in fluent-gpu** (`feat: FLLOC003 missing loc-key literal`).

---

### Task 2: FLLOC005 — unused keys in the base JSON (proposal)

**Files:**
- Modify: `MissingLocKeyAnalyzer.cs` (same compilation-end action)
- Modify: `AnalyzerTests.cs`
- Document `$unusedAllow` in `en-US.json` `$comment` and `docs/guide/localizing-the-control-kit.md`

**Interfaces:**
- Consumes: base key set + every string literal in the compilation + every `Strings` nested const/method (`IFieldSymbol` / `IMethodSymbol` containing namespace ending in `.Strings` or named `Strings`).
- Produces: `FLLOC005` **Info** per unused key, **location on the JSON property** (span in the AdditionalFile).

Used-set rules (lock these in tests):

1. String literal value equals a base key.
2. Member `Strings.Foo.Bar` / `Strings.Foo.BarKey` / `Strings.Foo.Bar(...)` maps to dotted key by lowercasing the first letter of each segment and stripping a trailing `Key` suffix on the last segment.
3. If `K` is used and `K + "Sub"` is a base key, count `K + "Sub"` used (`PaletteDescriptionLocKey`).
4. Top-level JSON array `$unusedAllow` (or `$unusedAllow` string list next to `$culture`) lists keys that must not fire.

```csharp
[Fact]
public void FLLOC005_Fires_On_Unreferenced_Base_Key()
{
    // en-US has detail.play + detail.shuffle; source only Loc.Get("detail.play")
    // Expect FLLOC005 on detail.shuffle only, Info severity, JSON location.
}

[Fact]
public void FLLOC005_Counts_Sub_Suffix_When_Stem_Is_Used()
{
    // literal "sidebar.section.pinned" plus key "sidebar.section.pinnedSub" in JSON → no FLLOC005 on *Sub
}
```

Default severity **Info**. Wavee will bump later. Never Error in the descriptor.

- [ ] Tests fail, implement, engine tests green, commit (`feat: FLLOC005 unused loc keys`).

---

### Task 3: FLLOC004 / FLLOC006 — satellite completeness

**Files:**
- Modify: `LocalizationKeysGenerator.cs` (it already parses the base file)
- Modify: `GeneratorTests.cs`

**Interfaces:**
- Consumes: all AdditionalFiles matching `**/assets/loc/*.json`. Base = `FluentGpuLocBase=true` or filename `en-US.json`. Others are satellites.
- Produces: `FLLOC004` Warning **per satellite file** (count + first 8 missing keys). `FLLOC006` Info **per extra satellite key** (or one Info per file with the extra names if extras > 20).

MSBuild opt-in later, not day one: `FluentGpuLocSatelliteMissingPerKey=true` fans FLLOC004 out per key.

```csharp
[Fact]
public void LocalizationKeysGenerator_FLLOC004_On_Satellite_Missing_Key()
{
    var (_, diags) = Harness.Generate(new LocalizationKeysGenerator(), "",
        ("assets/loc/en-US.json", "{\"dialog\":{\"ok\":\"OK\",\"cancel\":\"Cancel\"}}"),
        ("assets/loc/nl.json", "{\"dialog\":{\"ok\":\"OK\"}}"));
    Assert.Contains(diags, d => d.Id == "FLLOC004");
}

[Fact]
public void LocalizationKeysGenerator_FLLOC006_On_Satellite_Extra_Key()
{
    var (_, diags) = Harness.Generate(new LocalizationKeysGenerator(), "",
        ("assets/loc/en-US.json", "{\"dialog\":{\"ok\":\"OK\"}}"),
        ("assets/loc/nl.json", "{\"dialog\":{\"ok\":\"OK\",\"nope\":\"x\"}}"));
    Assert.Contains(diags, d => d.Id == "FLLOC006");
}
```

`$` keys never participate. Empty satellite (no file) is not FLLOC004 — missing AdditionalFiles means the generator cannot see it. Wavee **must** add nl/ko as AdditionalFiles in Task 5 or this diagnostic never arms in the app.

- [ ] Tests, implement, engine tests green, commit.

---

### Task 4: FGRP008 opt-in + phase-2 sinks

**Files:**
- Modify: `HardcodedKitStringAnalyzer.cs`
- Modify: `AnalyzerSemantics.cs` (optional helpers for Button/MenuFlyoutItem/Notify)
- Modify: `AnalyzerTests.cs`
- Modify: `docs/guide/localizing-the-control-kit.md`

**Keep:** `FGRP008_Silent_Out_Of_Scope` (default assembly still silent).

```csharp
[Fact]
public void FGRP008_Fires_When_Assembly_Opted_In()
{
    var diags = Harness.Analyze(new HardcodedKitStringAnalyzer(), Usings + """
        sealed class C : Component { public override Element Render() => new TextEl("Cut"); }
        """,
        assemblyName: "Wavee",
        globalOptions: ("build_property.FluentGpuLocHardcodedAssemblies", "Wavee"));
    Assert.Equal(1, Harness.Count(diags, "FGRP008"));
}
```

Arm if `AssemblyName == FluentGpu.Controls` **or** it appears in comma-separated `FluentGpuLocHardcodedAssemblies`.

Phase 1 (this task): existing sinks only (`TextEl` ctor arg 0, `Text =`, `AutomationName =`).

Phase 2 (same PR if tests stay small, else follow-up commit): first string literal arg of `Button.Create|Standard|Subtle`, `MenuFlyoutItem` / `.RadioItem` / `.SubMenu`, `Notify.Say`, `HyperlinkButton.Create`; assignments `Placeholder|PrimaryText|SecondaryText|CloseText|Title|Message`; tuple first item of file-picker filters `("JPEG", "*.jpg")`.

Allow: empty / no ASCII letter / `// loc-allow`. Skip types under namespace `Wavee.Screens.Diagnostics` (or file-level `// loc-allow-file`).

- [ ] Tests, implement, engine Debug+Release + SourceGen.Tests, commit.

---

### Task 5: Wire the analyzer in Wavee

**Files:**
- Modify: `src/apps/Wavee/Wavee.csproj`
- Create: `src/apps/Wavee/.editorconfig`

```xml
<AdditionalFiles Include="assets\loc\en-US.json" FluentGpuLocBase="true" />
<AdditionalFiles Include="assets\loc\nl.json" />
<AdditionalFiles Include="assets\loc\ko-KR.json" />
<FluentGpuLocHardcodedAssemblies>Wavee</FluentGpuLocHardcodedAssemblies>
<CompilerVisibleProperty Include="FluentGpuLocHardcodedAssemblies" />
```

```ini
# src/apps/Wavee/.editorconfig
root = false
[*.cs]
dotnet_diagnostic.FLLOC003.severity = warning
dotnet_diagnostic.FGRP008.severity = warning
dotnet_diagnostic.FLLOC005.severity = suggestion
dotnet_diagnostic.FLLOC006.severity = suggestion
dotnet_diagnostic.FLLOC004.severity = warning
```

Pin the engine commit that contains Tasks 1–4 (`$(EngineRoot)` sibling).

- [ ] `dotnet build Wavee.slnx` Debug **and** Release. Expect warnings (FLLOC004 ~1735 missing summarized as two file warnings; FGRP008 on Deck.Faces / Receipts; FLLOC005 Infos). **Zero errors.**
- [ ] Commit (`build: arm loc analyzers in Wavee`).

**Severity promotions (later, not this task):**

| Id | Flip to error when |
|---|---|
| FLLOC003 | After Task 6 (`Strings.*` sweep). Zero findings today. |
| FGRP008 | After Task 8 (hardcoded extract). |
| FLLOC004 | After packets have filled nl/ko. **This is the picker gate.** |
| FLLOC005 | Warning after Task 7 prune; never error unless `$unusedAllow` covers keepers. |
| FLLOC006 | Warning after extras deleted. |

---

### Task 6: Mechanical `Strings.*` sweep

**Files (highest density, disjoint):**
- `Shell/Sidebar.UI.Menus.cs`, `Shell/Sidebar.UI.Drop.cs`
- `Spotify/Spotify.Library.cs`
- `Entities/Concert.Rules.cs`, `Entities/Home.Customizer.cs`, `Entities/Playlist.Page.cs`
- Remaining `Loc.Get("…")` / `Loc.Format("…")` in `src/apps/Wavee`

Replace `Loc.Get("sidebar.layout.classic")` with `Loc.Get(Strings.Sidebar.Layout.Classic)`. Parameterized → generated method (`Strings.Drag.MovedTo(name)`), not `Loc.Format`.

No new keys. Const loc-key fields that already equal a dotted key (`Actions.LocKeyModeNotSupported`) may stay: FLLOC003 allows literals that exist; converting them is optional hygiene.

- [ ] Build Wavee; FLLOC003 count = 0 (or only `// loc-allow`).
- [ ] `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` (orchestrator).
- [ ] Commit (`refactor: compile-safe Strings.* loc lookups`).

---

### Task 7: Prune dead en-US keys

**Do this after FLLOC005 is live** so the Error List is the list, not a grep.

**Files:** `en-US.json`, `nl.json`, `ko-KR.json`

Delete whole unused namespaces first (confirm each key still FLLOC005):

- `shortcuts.*` (21) — overlay never ported
- `crash.*` (5) — only `_old/Program.cs`
- `profile.*` (7)
- leftover `auth.*` (`disclaimer`, `pairUrl`, `or`, `open`, `browserFailed`, `spotifySignInWeb`)
- `sidebar.chooser.*`
- `home.chartEye` / `dailyEye` / `podcastsEye` / `videoEye` / `weeklyEye`
- old `settings.about.checkForUpdates` / `upToDate` / `updateAvailable` / `updateCheckFailed` / `checking` / `sendFeedback` / `build` / `version` if still unused (replaced by `update.*`)
- `notifications.activity.*` if still unused
- `nav.apiConsole`, `nav.comingSoon`

Then walk remaining FLLOC005 Infos. Drop matching rows from nl/ko (else FLLOC006).

**Do not delete:** `modulePage.openOn`, `sidebar.template.curated`, `sidebar.template.curatedSub`. Put them in `$unusedAllow` **or** start using `modulePage.openOn` in Task 8.

```json
{
  "$culture": "en-US",
  "$unusedAllow": [ "modulePage.openOn", "sidebar.template.curated", "sidebar.template.curatedSub" ]
}
```

Parser already skips `$` keys; `$unusedAllow` is analyzer-only. Add a tiny read of that property in `MissingLocKeyAnalyzer` (Task 2) — if Task 2 shipped without it, this is the moment to parse it.

- [ ] Build: FLLOC005 silent except allow-list. FLLOC006 silent.
- [ ] Tests green (any key-existence tests).
- [ ] Commit (`chore: drop unused en-US loc keys`).

---

### Task 8: Extract leftover hardcoded copy

**Files:**
- `assets/loc/en-US.json` (new keys)
- `Platform/Modules.Host.cs` (map `PageAction` titles through loc; modules send ids, host labels)
- `src/apps/modules/Wavee.Module.{YouTube,Twitch,Radio}` — stop shipping English titles; send stable ids (`play`, `open`)
- `Screens/Setup.UI.Runtime.cs`, `Screens/Diagnostics.UI.cs`, `Entities/Playlist.UI.cs` — picker filter names
- `Screens/Settings.cs` `Receipts.GpuLine`
- `Shell/Deck.Faces.cs`

New keys (add English, packets will translate):

```json
"modulePage": { "play": "Play", "openOn": "Open on {name}", "openInBrowser": "Open in browser", "playLatest": "Play latest" },
"common": { "fileFilterAll": "All files", "fileFilterJpeg": "JPEG", "fileFilterLog": "Log text", "fileFilterSpotifyDll": "Spotify DLL" },
"settings": { "about": { "gpuWeak": "Weak", "gpuStrong": "Strong", "gpuUnknown": "Unknown", "gpuSoftware": "software" } }
```

`player.nowPlaying` already exists for Deck.Faces `"Now Playing"`. Add `player.deckMenu` / `player.deckStereo` if we want those chrome words localized; otherwise `// loc-allow` them as hardware face lettering.

Host-side mapping (preferred): modules stay language-agnostic.

```csharp
static string ModuleActionLabel(PageAction a) => a.Kind switch
{
    PageActionKind.Play => Loc.Get(Strings.ModulePage.Play),
    PageActionKind.OpenUrl => a.HostName is { Length: > 0 } n
        ? Strings.ModulePage.OpenOn(n)
        : Loc.Get(Strings.ModulePage.OpenInBrowser),
    _ => a.Title,
};
```

- [ ] FGRP008 silent in Wavee except loc-allow.
- [ ] Tests: module page still finds a Play action by id, not by English title (`ModulePageGateTests`).
- [ ] Commit (`fix: route leftover chrome through loc keys`).

---

### Task 9: LocPack catalog (engine-free)

This is the translation interchange. Humans never edit `nl.json` by hand after this.

**Files:**
- Create: `ops/loc/Wavee.LocPack/Wavee.LocPack.csproj` (net10, `OutputType=Exe`, no engine reference)
- Create: `ops/loc/Wavee.LocPack/Catalog.cs` — flatten, nest, ICU classify, placeholder extract, merge
- Create: `ops/loc/Wavee.LocPack/Packet.cs` — DTOs
- Create: `ops/loc/Wavee.LocPack/Program.cs` — `pack` / `xlsx` / `draft` / `import` / `qa`
- Create: `ops/loc/glossary.json`, `ops/loc/surfaces.json`, `ops/loc/keep-english.json`
- Create: `src/apps/Wavee.Tests/LocPackTests.cs` — ProjectReference to LocPack **or** compile Catalog.cs as a linked file. Prefer ProjectReference; add the project to `Wavee.slnx`.
- Create: `ops/loc/README.md` — operator steps (how to send a packet, how to import).

**Packet JSON schema** (one packet = one surface × one culture, 40–80 strings):

```json
{
  "culture": "ko-KR",
  "packet": "settings-general",
  "surface": "Settings · Language & region",
  "screenshot": "settings-language.png",
  "glossary": ["Wavee", "Liked Songs", "Your Library"],
  "rows": [
    {
      "key": "settings.language.label",
      "english": "App language",
      "current": "",
      "ai_draft": "",
      "yours": "",
      "status": "todo",
      "where": "Dropdown on Settings → General. Applies the next time Wavee starts.",
      "notes": "The language Wavee's own interface uses",
      "placeholders": [],
      "icu": "none",
      "example": "",
      "keep_english": false
    }
  ]
}
```

`status`: `todo` | `draft` | `approved` | `skip`. Import writes a row iff `status == approved` and `yours` is non-empty. `skip` means leave fallback English (cognate / product name).

`icu`: `none` | `plural` | `select`. For `plural`, LocPack also emits sibling fields the review UI uses:

```json
"icu_forms": { "one": "…", "other": "…" }
```

Korean packets omit `one` (CLDR `other` only). Import reconstructs `{count, plural, other {…}}`.

**Catalog.cs** (the decisions tests lock):

```csharp
public static class LocCatalog
{
    public static Dictionary<string, string> Flatten(JsonElement root);
    public static JsonObject Nest(IReadOnlyDictionary<string, string> flat); // rebuild nested objects; keep $culture
    public static IcuKind Classify(string template);
    public static string[] Placeholders(string template); // {name}, not ICU keywords
    public static bool PlaceholdersMatch(string source, string translation);
    public static string SurfaceOf(string key, IReadOnlyDictionary<string, Surface> map);
    public static Packet MissingOf(string culture, Dictionary<string,string> en, Dictionary<string,string> sat,
        string packetId, Func<string, bool> include);
}
```

Placeholder QA: every `{name}` in English appears in `yours`; no extra `{foo}`; ICU braces balanced. Fail import on a row that breaks this (do not write a broken satellite).

**surfaces.json** (prefix match, longest wins):

```json
{
  "settings.language": { "surface": "Settings · Language & region", "where": "General tab, first group.", "screenshot": "settings-language" },
  "settings.tabs": { "surface": "Settings · tabs", "where": "Tab strip at the top of Settings.", "screenshot": "settings-tabs" },
  "detail": { "surface": "Album / playlist / track page", "where": "Main detail chrome (play, shuffle, menus).", "screenshot": "detail-album" },
  "player": { "surface": "Player bar", "where": "Bottom playback bar.", "screenshot": "player-bar" },
  "drag": { "surface": "Drag and drop", "where": "Caption on the drag chip.", "screenshot": "drag-chip" }
}
```

Unknown prefix → `surface: "Other"`, `where: "Wavee UI (key {key})."`. Never put `.cs` paths in `where`.

**glossary.json:** Wavee, Spotify, Liked Songs, Your Library, playlist, folder, pin, queue, lossless, … plus “sentence case except eyebrows; informal-polite; desktop music app”.

**keep-english.json:** `Wavee`, `Spotify`, `DAILY MIX`, `LibraryV3`, `Nederlands`, `한국어`, `English (United States)`, plus keys listed as cognates we will `skip` (`home.radio`, `nav.album`, player style names like `iPod Classic` if we want the product name).

**CLI:**

```text
dotnet run --project ops/loc/Wavee.LocPack -- pack --culture ko-KR --packet settings-general --prefix settings.language --prefix settings.tabs --prefix settings.links --prefix settings.general
dotnet run --project ops/loc/Wavee.LocPack -- xlsx ops/loc/work/ko-KR/settings-general.json
dotnet run --project ops/loc/Wavee.LocPack -- draft ops/loc/work/ko-KR/settings-general.json
dotnet run --project ops/loc/Wavee.LocPack -- qa ops/loc/work/ko-KR/settings-general.json
dotnet run --project ops/loc/Wavee.LocPack -- import --culture ko-KR ops/loc/work/ko-KR/settings-general.json
```

`pack` writes only keys missing from the satellite **or** present but identical-to-English (the `library.*` dump). It never includes `$unusedAllow` / pruned keys / `diagnostics.*` unless `--include-diagnostics`.

`xlsx` is a convenience for Google Sheets: same columns as the packet rows. Round-trip: `pack` → `xlsx` → she edits `yours` + `status` → save xlsx → `import --xlsx`. UTF-8. Do not use XLSX as source of truth; re-export from JSON if in doubt.

`draft` fills empty `ai_draft` via stdin prompt or an HTTP model if `WAVEE_LOC_DRAFT_CMD` is a documented operator command in README (a wrapper you run, not an app env-var feature switch). Default `draft` can also be a **file merge**: read `ai_draft` from a sidecar the operator produced. Do not embed API keys.

- [ ] Tests: flatten round-trip nested JSON (comments `$comment` may drop on rewrite — preserve `$culture` and sibling `$comment` on objects when the source object still exists; new keys get no comment). Placeholder match / mismatch. Korean plural reconstructs `other` only. Import ignores `todo`. Import refuses broken `{name}`.
- [ ] `dotnet test` LocPackTests. Commit (`feat: loc packet packer and import`).

---

### Task 10: Static review UI

**Files:**
- Create: `ops/loc/review/index.html` (+ `app.js`, `app.css` if split)
- Modify: `ops/loc/README.md` — “send this zip to a reviewer”

No build step. Open the HTML, `File` picker loads a packet JSON (or drag-drop). Girlfriend never sees a key she must understand; keys stay in a collapsed `<details>`.

Wireframe:

```text
┌──────────────────────────────────────────────────────────────┐
│  Wavee Korean · settings-general     12 / 48 approved        │
│  [screenshot] Settings → General                             │
│                                                              │
│  App language                                                │
│  Dropdown on Settings → General. Applies next launch.        │
│                                                              │
│  Draft: 앱 언어                                              │
│  Yours: [앱 언어                    ]                        │
│  [ Approve draft ]  [ Save edit ]  [ Skip (keep English) ]   │
│                                                              │
│  Placeholders: (none)     ICU: none                          │
│  ── next ──                                                  │
└──────────────────────────────────────────────────────────────┘
```

Rules in JS (mirror Catalog.cs QA; keep it dumb):

- Highlight `{name}` / `{count}` / `#` as chips; if `yours` drops one, disable Approve and show “Keep {name} as written”.
- ICU `plural`: two boxes for nl (`one` / `other`), one box for ko (`other`). Rebuild the template on approve.
- `keep_english` or glossary hits: default Skip, show “Leave as English”.
- Screenshot: `ops/loc/screens/{id}.png` cannot be loaded from `file://` of another folder — README says zip `review/` + `screens/` + the packet, or embed `screenshot_data_url` at pack time (`pack --embed-screens`). Prefer `--embed-screens` so a single JSON is the whole packet.
- Download updated packet JSON (never writes git itself).

You (Dutch) can use the same UI for `nl`.

- [ ] Manual: pack settings-general, open review, approve one row, import, key appears in `ko-KR.json`.
- [ ] Commit the review UI (`feat: loc packet reviewer`).

---

### Task 11: AI draft step

**Files:**
- Create: `ops/loc/Wavee.LocPack/Draft.cs`
- Create: `ops/loc/draft-prompt.md`

Prompt (locked in the file, not improvised per run):

```text
You are drafting UI copy for Wavee, an unofficial desktop Spotify client for Windows.
Tone: sentence case, informal-polite, short. Buttons stay short.
Do not translate: {placeholders}, ICU braces, # inside plurals, glossary terms marked keep.
Target language: {culture}.
Surface: {surface}. Where: {where}.
Notes: {notes}.
English: {english}
Return only the translation (or ICU template with the same placeholders).
For Korean plurals use only `other`. For Dutch use `one` and `other`.
```

`draft` writes `ai_draft`, sets `status` from `todo` → `draft`, never overwrites a non-empty `yours`.

Operator produces drafts however they want (Claude, DeepL). LocPack may shell out to a command in README (`wavee-loc-draft.ps1`) — that script is untracked if it contains keys.

Do **not** auto-approve. Do **not** call a model from the Wavee app.

- [ ] Test: draft skips rows with `yours` set; placeholder-broken drafts fail `qa` and stay `draft`.
- [ ] Commit (`feat: loc packet AI draft merge`).

---

### Task 12: Screenshot pack (optional but expected for her packets)

**Files:** `ops/loc/screens/*.png` (git-lfs or compressed; no account identity)

Capture `--fake` at one DPI, one window size:

| id | What to show |
|---|---|
| `settings-language` | The Language & region row + open combo |
| `settings-tabs` | Settings tab strip |
| `player-bar` | Bottom bar playing a fake track |
| `detail-album` | Album page hero + first rows |
| `sidebar-classic` | Left sidebar |
| `drag-chip` | Any drag in progress if easy; else skip |

`pack --embed-screens` inlines as data URLs so the packet is self-contained.

- [ ] Add screens. Commit if they contain no personal data (`chore: loc reviewer screenshots`).

---

### Task 13: Fill satellites by packet (the bulk)

Each packet is its own import. Order is user-visible first. Do not enable the picker mid-way.

| # | Packet id | Prefixes | Missing (approx) | Reviewer |
|---|---|---|---:|---|
| 1 | `settings-general` | `settings.language`, `settings.tabs`, `settings.links`, `settings.general` | ~80 | both |
| 2 | `settings-rest` | `settings.*` minus packet 1; skip `settings.diagnostics` | ~250 | both |
| 3 | `player` | `player`, `taskbar`, `jumplist`, `tray` | ~90 | both |
| 4 | `sidebar` | `sidebar` (20 missing + untranslated extras) | ~40 | both |
| 5 | `detail` | `detail` | 286 | both |
| 6 | `setup-auth` | `setup`, `auth` | ~100 | both |
| 7 | `menus-drag-search` | `menu`, `drag`, `search`, `common` | ~90 | both |
| 8 | `concerts-artist` | `concerts`, `artist` | ~145 | both |
| 9 | `update-whatsnew-report` | `update`, `whatsNew`, `report`, `notifications` | ~210 | you first |
| 10 | `kit` | copy 44 FluentGpu.Controls keys into **both** satellites (and optionally en-US if we want `Wavee.Strings` for them — **not required**; override in nl/ko is enough because kit registers neutral) | 44 | both |
| 11 | `library-english-dump` | keys in satellite **equal** to en-US under `library.*` | ~30 | both |
| 12 | remaining | `playback`, `lyrics`, `home` holes, `localFile`, `shell`, `browse`, `toast`, `play`, `friends`, `palette`, `modulePage`, `runtime`, `crash` if not pruned | rest | you |
| — | skip | `diagnostics.*` | 149 | leave English |

After each import: `dotnet build` (FLLOC004 count drops), `qa` on the packet, smoke `--fake` in that language if the picker is still disabled — force culture by writing `localization.culture` in settings or a one-line test hook already used by `Platform.Host` (`TrySetCulture`). Do **not** flip `Enabled` yet.

Packet 10 kit keys (from `FluentGpu.Controls/assets/loc/en-US.json`): `dialog.ok`, `infoBar.close`, `nav.settings`, `tabStrip.newTab`, `autoSuggest.*`, `passwordBox.placeholder`, `calendarDatePicker.placeholder`, `datePicker.*`, `timePicker.*`, `media.*`.

Delete extras `player.play`, `player.pause`, `settings.tabs.diagnostics` on first import that rewrites those objects.

ICU: copy the English template structure; only translate the text inside braces.

- [ ] After packet 1: `ko-KR` / `nl` contain `settings.language.*`. Screenshot of Settings in each language (fake data).
- [ ] After all packets: FLLOC004 silent. FLLOC006 silent.

---

### Task 14: Enable the language picker

**Files:** `src/apps/Wavee/Screens/Settings.cs`

```csharp
public static readonly bool[] Enabled = [true, true, true, true];
```

Restart-to-apply copy is already `settings.language.restartSub`. Keep `SetLanguage` writing `Platform.Keys.UiCulture` and applying on next launch (`Platform.Host` `LoadFolder` + `TrySetCulture`).

- [ ] FLLOC004 severity may now be `error` in `.editorconfig`.
- [ ] Manual: pick Nederlands, restart, Settings + player bar + sidebar are Dutch. Repeat 한국어.
- [ ] `dotnet build` Debug+Release, `dotnet test` Wavee.Tests, engine gates if analyzer files changed.
- [ ] Commit (`feat: enable Dutch and Korean UI languages`). CHANGELOG + issue ref per house rule.

---

## Reviewer protocol (non-technical)

Zip for one packet:

- `review/index.html` (+ js/css)
- `settings-general.json` (with `ai_draft` filled, screens embedded)
- a 10-line `HOW-TO.txt` in **her language**: open the HTML, choose the JSON, for each line: does the draft sound like a music app? Fix it or Approve. Do not touch words in `{curly braces}`. Download when done and send the file back.

You import. You never ask her to install git, VS, or Python.

Dutch: same UI, you are the reviewer. Do not skip QA because you wrote the app.

---

## What “done” means

- User-facing Wavee chrome goes through `Strings.*` / `Loc.Get(Strings.…)`.
- FGRP008 silent in Wavee except loc-allow glyphs / diagnostics.
- FLLOC003 silent.
- FLLOC005 silent except `$unusedAllow`.
- FLLOC004 + FLLOC006 silent for `nl` and `ko-KR` (every live en-US key has a row; kit keys overridden; extras gone).
- `Language.Enabled` all true; restart applies nl / ko-KR.
- Packets + LocPack + reviewer exist so the next language is the same pipeline.
- Pseudo-locale still works for extraction QA.

---

## Execution order

Engine Tasks **1 → 4** (fluent-gpu), Wavee **5**, then **6–8** (hygiene), then **9–12** (pipeline), then **13** (packets), then **14** (picker).

Do not start Task 13 before Task 9 import works on a 3-key fixture. Do not start Task 14 before FLLOC004 is clean.

Two execution options after this plan is accepted:

1. **Subagent-driven** — one subagent per task, review between tasks.
2. **Inline** — this session, same order, checkpoint after Task 5 (Wavee still builds) and after Task 9 (first packet round-trips).
