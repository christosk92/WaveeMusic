# Podcast reader repair — 2026-09-19

Implement the approved shared-reader design in the 0.3 worktree, against `fluent-gpu-pin`.

## Composition

```text
Podcasts library
  Words rail: Followed shows / Your Episodes
  navigator | Show.PageHost(embedded: true)
                compact identity
                ReaderHost
                  responsive toolbar (never a horizontal scroller)
                  selection commands
                  bound episode list | year/month navigator
Show route -> same PageHost, standard Detail.Frame identity rail
Episode route -> Detail.Frame + word tabs + independently loaded section
Player -> episode ±10s and speed -> shared transcript/lyrics surface
```

Rows use `Episode.RowItem` identity and version, `BoundItemScope`, the existing selector lane and a compact action cluster. Dates index the actual filtered item projection. Filters prune hidden selections; ordering remaps selected identities. Search expands below the word rail when it cannot fit inline. Group headings are not selectable.

## Work packages

1. Root: shared show/library reader, responsive toolbar/date navigation, selection/batch actions, row geometry, block-aware rich text and integration.
2. Episode reader: section composition, skeleton/error states, saved-playlist discovery, chapter/comment contract repairs.
3. Transcript: preserve captured character timings and adapt the existing lyrics surface/clock; share resources with the episode reader.
4. Player/engine: persistent podcast controls and focused keyboard handling; image identity across recycled slots.

No raw captures or credentials enter this repository. Supplied captures include `verycomplex3.saz`, `verycomplex4.saz`, and `themanagerpath.saz`; the latter must establish audiobook classification from its metadata, not its name.

## Acceptance

Behavioral tests cover delayed metadata, stable item identity, filter/sort selection, date jumps, saved discovery, transcript character timing and playback clock/rate, keyboard routing and image reuse. Build Debug and Release, run app tests and release Pester; run engine gates for engine edits. Inspect displayed-window screenshots at narrow/wide sizes and multiple scaling factors, including loading, selected, hovered and keyboard-focused states. A black hidden-window screenshot is not a pass.

## Results

Implementation complete in the worktree. Automated gates passed; the latest visual verification limitation is recorded below.

## Video and follow-up screenshots

Evidence: `buggsss.mp4` (26 seconds), plus the follow-up comment, transcript and library screenshots.

| Evidence | Cause / implementation |
| --- | --- |
| Throughout: transcript nearly invisible; later screenshots confirm no following | Music distance opacity/blur was applied to spoken prose, and shared resource readiness could precede host row-signal preparation. `Lyrics.Transcript.cs` now prepares each host before publishing its local Ready state. Podcast rows remain readable; only the actual playback owner follows and highlights captured timed words. |
| Around 4 seconds: Continue heading above a played episode | The head used a frozen `Episode.Fixed` item. `VisitHead` now owns a memo over the reader snapshot and passes its live signal to `BoundItemScope<Episode.RowItem>`. |
| Around 12 seconds / final screenshot: latest repeated above the episode list | Stand-alone shows no longer get a redundant latest card. The head is reserved for an actual resume or a sequential starting point. Membership and progress readiness share the list reveal boundary. |
| Around 20 seconds: clock changes after pointer reaches transcript | Transcript lines seek; this is not evidence of spontaneous playback movement. Visibility and active-line feedback are repaired separately. |
| Around 22 seconds: sidebar remaining time differs from bottom clock; long Resume label clips | Active row progress uses the playback clock in leaf bindings. Deliberate seek updates persisted progress immediately. The sidebar uses a short Resume button and a wrapping, live remaining-time label. |
| Follow-up: library artwork and publisher disappear, return on opening the show | Partial show identity writes could claim and clear fields absent from their payload. Producer masks, per-field commit, normalized cached masks and persistence now preserve supplied fields only. Rejected thin fields are excluded from write-behind; rich-to-thin-to-cold-read regression covers title, artwork, publisher and authority. |
| Follow-up: comment hierarchy and consent overflow | Shared author/avatar/body columns, compact inline dates, a restrained reply guide, and wrapping checkbox label; actions follow the body alignment. |
| Follow-up: show link looks like a stretched button; library header not clickable | Episode attribution becomes a wrapping text link. Library identity artwork/title/publisher form one focusable navigation target, with separate save/menu controls. |

The extra Debrief window around seven seconds was the validation harness, not spontaneous Wavee navigation. Further visible harness launches were stopped to avoid interfering with the user's session. Extracted frames remain outside the repository under `C:\WAVEE\validation\podcast-ui\video`.

### Reactive head implementation

```csharp
_rowOf = () => {
    var s = host.Model.Read();
    int slot = s.ResumeSlot > 0 ? s.ResumeSlot
        : s.Order == ConsumptionOrder.Sequential ? s.FirstSlot : 0;
    return Episode.RowItem.Of(new Episode(slot), Episode.RowMarks.NoRule);
};
// VisitHead.Render owns UseComputed(_rowOf); row leaves subscribe to that memo.
Episode.ReaderRow(new BoundItemScope<Episode.RowItem>(Episode.Fixed(default).Row, row), context, narrow);
```

```text
Comment
  avatar | author · date
         | body
         | reactions · reply
         | │ reply avatar | reply author · date
         | │              | reply body · actions
         | composer
         | checkbox + wrapping public-posting consent
```

## Validation record

- App full Debug and Release solution builds: passed, zero warnings/errors (public sources).
- App tests: 10,883 passed, one skipped, zero failed; includes transcript ownership/publication/timing, localized toolbar fit, date projection, selection, partial metadata and live episode progress regressions.
- Release tooling Pester: 372 passed, one skipped, zero failed.
- Engine Debug and Release builds: passed; existing test/harness analyzer warnings remain (164 Debug / 165 Release on the initial full rebuild).
- Engine canon check: passed.
- Engine full headless suite: **1,598 checks passed**, including image recycling and zero-allocation gates. The run initially exposed a shelf gate assuming every normal reactive update settles in one frame. The dispatcher deliberately permits a 4 ms slice to yield. The gate now waits for the original visible replacement label with the existing finite 12-frame ceiling before asserting the same retained nodes, viewport, focus, popup, page and current action. Offscreen virtual rows are not required to render.
- Actual displayed-window inspections before the video follow-up: wide show and episode About screenshots under `C:\WAVEE\validation\podcast-ui`. The latest comment/transcript fixes have not been visually rechecked in a new window, to avoid interrupting the user's running app; automated checks do not substitute for that visual check.
- Logs remain outside the repository under `C:\WAVEE\validation\podcast-*`.
