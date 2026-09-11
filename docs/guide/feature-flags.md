# Feature flags and switches — the whole inventory

Four different things in this repo get called "a flag", and only the first is a feature flag. They have different
owners, different lifetimes and different rules, and conflating them is how a withheld surface leaks into a release
or a diagnostic quietly becomes load-bearing. This page is the map; each category names its **single source of
truth**, because a list duplicated here would drift within a week.

| # | Category | Where it lives | Count (2026-09-10) | Ships? |
|---|---|---|---|---|
| A | **Feature gates** — a finished-enough surface deliberately withheld from the UI | `src/apps/Wavee/App/WaveeFeatures.cs` | 1 | yes, as `false` |
| B | **User preferences** — a choice the user is meant to make | `WaveeSettings` (`Platform/AppSettings.cs`) + `SettingsCatalog` | 134 keys | yes |
| C | **Build / packaging switches** — decide what COMPILES or what is packed | `Wavee.csproj`, `ops/build/*.ps1` | 4 | build-time only |
| D | **Dev + diagnostic probes** — one-shot harnesses, screenshots, traces | `WAVEE_*` / `FG_*` environment variables | 78 app + 14 engine | present but inert |

---

## A. Feature gates — `WaveeFeatures`

The only category that is a feature flag in the usual sense: *the code is in the tree, the user must not see it
yet*. One `const bool` per gate, all in `WaveeFeatures`, each documented with what it hides, why, and **the
condition under which the flag must be deleted**.

| Flag | Hides | Why off | Delete when |
|---|---|---|---|
| `NpvPresentationSwitcher` | The Now Playing header's **Cover \| ‹Player›** `SelectorBar` (`NpvHeaderRow`) | Withheld 2026-09-10 by request while the player-style work (`docs/plans/wavee/npv-player-styles-implementation.md`) is in flight | The player styles ship — the switcher is the feature's primary affordance, so the flag must not outlive it |

**Scope note for `NpvPresentationSwitcher`:** it hides *only* the switcher. The gear next to it still opens
`PlayerStyleFlyout`, and Settings › Appearance, the command palette and the artwork context menu still reach
`npv.presentation` / `npv.player.style`. So nothing is stranded — a stored `npv.presentation = Player` keeps
rendering its deck — and turning the flag on restores the stored choice untouched. Hiding those other entry points
is a separate, larger decision.

### Rules

1. **`const bool`, not a setting.** One greppable owner, and the compiler drops the dead branch — a withheld
   surface cannot leak through a path someone forgot to check. Flipping it is a deliberate edit plus a rebuild,
   which is the right cost for "we are shipping this".
2. **Every flag names its removal condition.** A flag without one is a permanent branch, i.e. exactly the
   mode-specific fork this codebase keeps deleting.
3. **Default `false`.** A gate exists to withhold; if it would ship `true`, it is not a gate — delete it.
4. **Never gate measurement.** See category D and CLAUDE.md: diagnostics are always on.
5. **If the user is meant to turn it on, it is category B**, not a gate.

---

## B. User preferences — not flags

134 `SettingKey<T>` entries in `WaveeSettings`, surfaced through `SettingsCatalog` (which is what Settings, the
command palette and the diagnostics page all read). These are the user's choices — themes, densities, sidebar
design, `npv.presentation` — and they ship **on**. The distinction that matters:

> If the user is meant to discover and change it, it is a **setting**. If the user must not see it at all yet, it
> is a **gate**. Nothing is both.

`SettingsCatalog` is the source of truth; do not re-list keys here.

---

## C. Build and packaging switches

| Switch | Where | Effect |
|---|---|---|
| `WaveeSkipPrivateSources=true` (`-PublicOnly`) | `Wavee.csproj:186`, `ops/build/publish-wavee-aot.ps1`, `pack-wavee-msix.ps1` | Drops the PlayPlay sources and the `WAVEE_PLAYPLAY_LOCAL` define — the public-only variant. The release script asserts the junction unless this is passed. |
| `WAVEE_PLAYPLAY_LOCAL` (define) | derived from the above | Compiles the in-process key deriver. |
| `FluentGpuDiag=true` (`-Diag`) | `ops/build/publish-wavee-aot.ps1` | Defines `FLUENTGPU_DIAG` solution-wide — **a different binary**: `BindContract` and `BackwardsWriteGuard` become default-on, so its timings are not representative. `ops/diag/README.md` explains what to clear. |
| `NativeDebugSymbols` (`-Symbols`) | same script | PDB + IlcGenerateMapFile. No behaviour change. |

---

## D. Dev and diagnostic probes — 78 app, 14 engine

`WAVEE_*` in the app (`Features/Diagnostics/*`, the shot/probe harnesses) and `FG_*` in the engine. Enumerate them
rather than trusting a copied list:

```bash
# app
grep -rnoE '"WAVEE_[A-Z0-9_]+"' --include=*.cs src/apps/ | grep -v Tests | sed 's/.*"\(.*\)"/\1/' | sort -u
# engine — the maintained table
..\fluent-gpu\ops\diag\README.md
```

They are launch-time harnesses: screenshot probes (`WAVEE_*_SHOT`), scripted stress runs (`WAVEE_NAV_PROBE`,
`WAVEE_MEM_SOAK`, `WAVEE_PERF_BENCH`), traces (`WAVEE_SIDEBAR_BINDER_DIAG`, `WAVEE_LYRICS_DEBUG`). Two of them read
`FG_*` engine names. Beware the false friend: **`WAVEE_ANIM_CAP` is animation *capture*, not an animation cap** —
it screenshots a resize-band transition frame by frame and changes no pacing.

### The rule these must obey

CLAUDE.md: **no environment-variable switches for behaviour or verification.** Anything a release is judged by is
always on — the always-on log lines (`nav.frames`, `scroll.frames`, `session.frames`, `[wake]`, `[d3d12.present]`,
`[compositor-clock]`), the diagnostics page, and the gates. An env var may only *start a harness*; it may never
decide how the shipped app behaves, nor whether something is measured.

Two instruments were converted for exactly this reason and are worth knowing as precedent:

- **`[wake]`** (the wake-reason census) was `FG_WAKE_DIAG=1` to stderr once a second. "The loop is pinned at panel
  rate and nothing in the log says which term holds it" is unanswerable after the fact, so it is now always on at
  one line per 30 s. (Its reason table had also silently omitted two of the 28 wake bits.)
- **`[compositor-clock]`** logged its permanent-latch line at `Debug`, so the line that explained a session-long
  drop to a wall-clock timer was dropped from the shipped log
  (`docs/plans/wavee/scroll-feel-investigation-2026-09-10.md` §3.4). It is `Warning` now.

If you find yourself adding an env var so a behaviour can be checked, that is the signal to make the behaviour's
evidence always-on instead.

---

## Adding a gate

1. Add the `const bool` to `WaveeFeatures` with the four facts: what it hides, why it is off, what it does **not**
   hide, and when it must be deleted.
2. Add a row to the table in section A.
3. Branch at the narrowest place that actually hides the surface. Prefer omitting the child (a spread over an empty
   collection) to rendering a placeholder — a zero-size element still consumes its parent's `Gap`.
4. Keep the subscriptions outside the branch. A gated row still has to read the signals it depended on, or the
   surrounding UI stops updating when the flag is off.
