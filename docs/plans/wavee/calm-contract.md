# Wavee calm contract

Wavee is a place the user owns. Nothing is injected. Muscle memory is sacred.

This is the product contract behind Home layout preferences (`home-layout.json`,
[home-layout skill](../../../.claude/skills/wavee/home-layout.md)) and the later
Notifications settings consumer. It is not a feature checklist.

## The user owns Home

Home is the user's room. The landing modules — hero, weekly pair, jump-back-in,
recents, mixes, chips, radio, queue, audiobooks, podcasts, editorial, discover —
exist because the **user** kept them, in the **order they chose**.

- Nothing is injected onto Home. No promo tile, no partner shelf, no "because
  Wavee thinks you should" module the user did not already have from their
  library and their provider feed.
- Hiding a module removes it from the landing. It does not leave a hole, and it
  does not delete the source section from the ledger (drill-in and accounting
  stay intact).
- Reordering is visible. The customizer writes one document; the projection
  applies hide + reorder **before** row synthesis (`HomeLandingProjection.Project`).
- Reset returns the designed prototype rhythm. It is an ordinary undoable
  command, not a silent rewrite of `home-layout.json` on a corrupt load.

The customizer is `HomeCustomizerPage`, route key `home-customize`. Entry is the
Home header overflow ("Customize Home") — not a floating FAB, not a first-run
wizard.

## Nothing injected

Calm means the product does not surprise the user with content they did not ask
for:

- Home modules come from the feed the account already receives, filtered by the
  user's layout document.
- Dynamic section-deck ids (`deckOrder` on the v1 schema) are reserved so a
  later customizer can order server sections. They are not a hook for injected
  cards.
- The sidebar customizer and the Home customizer are siblings, not funnels:
  customizing one never rewrites the other.

## Notifications are opt-in and quiet

Notifications are a **later consumer** of this contract. Do not build a
Notifications settings page from this doc.

When that page lands it must:

- Default quiet. Categories start off unless the user turns them on.
- Never use Home as a notification surface. A toast, a badge, or a "what's new"
  injection on the landing violates this contract.
- Respect the same fail-soft persistence stance as `home-layout.json` and
  `sidebar-layout.json`: a corrupt preferences file is preserved, never
  overwritten, until the user chooses to start fresh.

## Muscle memory is sacred

Controls stay where the user last put them.

- Home module order is a preference, not a per-session shuffle.
- The greeting, chips, artists podium, timeline, section deck, and tail
  destinations are chrome, not customizer v1 targets. They keep their designed
  anchors so the page still feels like Home after a reorder.
- Navigation keys do not churn. `home-customize` is the customizer; `home` is
  Home. A later Notifications settings route must be a new key, not a reuse.

See also: [sidebar extension platform](../../guide/sidebar-extension-platform.md)
(the layout-document lineage this Home customizer copies) and
`.claude/skills/wavee/home-layout.md`.
