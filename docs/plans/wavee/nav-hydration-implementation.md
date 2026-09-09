# Navigation, hydration and independent rendering implementation

Status: app fixes and independent snapshot rendering implemented; final verification in progress.
Full transactional mount/update remains unfinished. Source investigation: `nav-hydration-findings-2026-09-07.md`.

## Accepted contract

Implement the app publication/demand/startup/diagnostic fixes and the sibling FluentGpu full immutable-scene
snapshot renderer, independent compositor animation, budgeted reactive flush and transactional reconciliation.
Preserve existing worktree edits. No database reset, environment switches, source-text tests or external writes.

**User correction: readiness belongs to the developer.** Do not implement the proposed primary-resource checklist,
relation-resolution readiness predicates or automatic `HasPrimaryData` page gating. A page drives its loadable:

```csharp
page.SetPending(seed);
page.SetReady(data); // Chosen by the page author; no framework completeness policy.
page.SetFailed(error);
```

The existing `Skel.Region` displays that state and derives its skeleton from the actual content tree. Optional-field
pending states and hydration diagnostics do not decide whether a page is ready.

```text
page-owned Loadable -> Skel.Region -> real retained page
query value         -> background projection -> coherent bound rows
resource status     -> visible field presentation
render requirements -> viewport demand and diagnostics
```

## App batches

- Separate immutable knowledge/value facts from request activity; preserve fact identity/revision on activity-only
  changes. Both query binding variants expose independent equality-gated fact/status signals.
- Serialize query recomputation off UI callers; cheap pending results, atomic current snapshots, short lease locks,
  generation-fenced latest invalidations. Preserve passive cold reads, finite reads and refresh semantics.
- Extract detail sorting/filtering into a background latest-input projection that publishes its source rows and
  index map together. No status-triggered sort, full-list equivalence or hydration scan on the UI callback.
- Preserve bound item source, occurrence identity, selection/focus/popup/viewport and current-item actions.
- Couple optional-facet demand to presentation configuration. Artist popular rows demand PlayCount; present zero
  renders `0 plays`; pending/absent/unsupported/error/offline have explicit stable field presentation.
- Separate local replica preparation from remote protocol execution. Retain current demand while initializing;
  start once per active node at matching provider/session readiness. Offline/fake/local behavior is explicit.
- Evaluate hydration against actual visible demand and required facts, distinguish resolved from successful,
  stamp navigation/publication revisions and bound diagnostic lifetime. Never gate the page on these diagnostics.

## Engine batches

1. Snapshot all recorder inputs, including visual side tables, resources, overlays, popup/detached trees, styles,
   damage and epochs. Renderer never aliases live mutable UI state. Recorder scratch and outputs become per-target.
2. Claimed generation-stamped slots protect header and arena reads; retain active scene across motion turns;
   retirement respects CPU snapshots and all GPU submissions. Distinct publish/consume/present/render sequences.
3. Move record/batch/submit/present onto the device-owning renderer; same implementation runs headlessly inline.
   Preserve recursive recording stack headroom and zero steady hot-phase allocation.
4. Render-owned compositor generators compose over immutable base paint on a display-paced independent clock.
   Desired animation snapshots preserve identity across dropped publications; UI-owned layout, disclosure,
   pointer-driven work and lifecycle remain UI-owned. Generation-stamped reverse feedback retains completion.
5. Cover resize/device recovery/minimize/occlusion/popups/detached hosts/shutdown; no stale target epoch rendering.
6. Budget all hosted flushes across repeated invocations in one turn, preserve structural ordering and queued work,
   report indivisible callback overruns. Reconcile yields only with staged scratch, committed interaction topology,
   effect generations and external-input version validation. No half-built scene publication.

```text
UI: input -> reactive/reconcile -> layout -> immutable SceneFrame
                                               |
RT: acquire -> own animation clock -> record -> submit -> present
              ^                                  |
              +--- retained scene while UI busy --+
RT feedback -> UI before next input: presented pose, completion, record results
```

## Acceptance

- Behavioral catalog tests: immutable snapshots, activity-only stability, cold play counts, duplicate occurrences,
  bounded worker lifetime, scope fencing, local startup hydration and zero premature Spotify remote waves.
- Mounted engine gates: retained row state, snapshot parity, ownership races, resource lifetime, deadline/yield
  ordering, transaction discard/effect consistency, optional-field geometry, all window paths.
- A 200 ms blocked UI callback must not stop an already-running compositor animation, subject to GPU scheduling
  and managed stop-the-world pauses. Input/new layout remains UI-bound; no stronger guarantee is claimed.
- Replay 12/31-track albums, 50/1494-track playlists, 327 liked tracks and 10k scaling fixture. Activity-only UI
  work must scale with realized rows, not total membership; no hydration projection-caused >=100 ms stall.
- Only orchestrator builds/tests: Wavee Debug+Release, Wavee.Tests, release Pester; engine Debug+Release,
  VerticalSlice in both configurations, guarded race/alloc tests, canon gate and deterministic screenshots.
- Never call the work complete from app tests alone: independent render progress and transactional gates matter.

## Verification record

### Implemented code

| Contract | Implementation |
|---|---|
| Developer-owned page readiness | `App/Queries/QueryHooks.cs`: mandatory `Action<Loadable<TView>, TView>` publication callback; page call sites explicitly call `SetReady`. `HomePage`, `HomeSectionPage` and `DetailPage` no longer gate readiness on `HasPrimaryData`. Acquisition revision zero is only a seed, not a data publication. |
| Separate facts/status | `Wavee.Core/Catalog/QueryFacts.cs`, `QuerySnapshot.Facts/FactsRevision`, and both `QuerySignalBinding` variants. Activity changes update `Resources` without invalidating `Facts`. |
| Background query computation | `Backend/Queries/QueryService.cs`: typed pending result, short lease lock, serialized pool projection, atomic current snapshot, demand-generation fencing, finite read/refresh completion and passive local observation. |
| Coherent detail projection | `App/Queries/LatestProjection.cs`, `Features/Detail/DetailTrackProjection.cs`, `DetailTracks.cs`: serialized latest-input work; exact source rows and display indices publish together; obsolete route/delivery generations are discarded. |
| Presentation-driven facets | `TrackPresentationFacts`, `VisibleTrackHydration`, `TrackRow`, `ArtistPopular`, `ArtistPage`, `HomeModules.Artists`: visible play counts demand PlayCount; zero, unknown/pending, failure and offline are distinct. |
| Startup admission | `Backend/Catalog/ProviderExecutionGate.cs`, `ResourceCoordinator`, `CatalogRuntime`, `LibraryQueryDemand`, `Services`, `WaveeApp`: local warm reads proceed; Spotify network work waits for the matching protocol epoch or resolves offline/cancelled. This is transport admission, not page readiness. |
| Truthful bounded diagnostics | `App/HydrationGaps.cs`, `NavigationFrameWatch.cs`: actual visible windows and submitted demand, field-specific missing facts, navigation/publication stamps; no whole-list scan in UI publication. |
| Render-owned recording | Sibling engine `SceneRecordingSnapshot`, `SceneFramePublisher`, `SceneRecordingContext`, `RenderThread`, `AppHost`: full detached recording inputs, claimed generation slots, per-target recorder and output, retained active scenes and independent compositor ticks. |
| Resource/lifecycle safety | Snapshot string pins, scene image readers, submitted/completed D3D fence retirement, generation/epoch fencing, parked resize/popup operations, detached target routing, stop/join and present feedback. |
| Budgeted reactive work | `ReactiveRuntime.Flush(deadlineTicks)` and shared hosted turn deadline: queued debt survives yields, structural work retains priority, indivisible callback overruns remain observable. |
| Isolated child matching | `ChildReconcilePlan` and `ChildReconcileCommit`: resumable side-effect-free identity planning, validation and one commit adapter. Production currently drains the plan and commits atomically. **This is not full transactional reconciliation.** |

The page-facing implementation is deliberately direct:

```csharp
QueryHooks.UseMapped(Context,
    static (page, value) => page.SetReady(value),
    queries, specification, project, seed, demand);
```

### Remaining transaction work

The existing mount/update implementation immediately runs component hooks, mutates shared props/context signals,
registers bindings/controllers/animation state, invokes realization callbacks and can start resources or image loads.
Yielding after any subset would expose speculative effects or partial interaction topology. The shipped commit
therefore remains indivisible. A complete staged implementation still needs:

1. Logical ancestry separated from committed topology for new subtrees.
2. Transaction-owned hook cells, dependency subscriptions and provider state.
3. Deferred intents for effects, resources, timers, image requests and realization/lifecycle callbacks.
4. External-input and generation validation before one topology/props/effect commit; disposal of discarded intents.
5. Hosted deadline scheduling plus mounted interaction, discard, cleanup and effect-order gates.

The independent renderer protects already-running compositor motion during a long UI callback; it does not make
input, new layout or arbitrary developer callbacks preemptible. No claim of full time-sliced mounting is made.

### Test evidence

Final results are recorded below after all current-source gates finish. Live GPU resize/occlusion/device-loss and
real-account cold-navigation timing are separate from the deterministic headless coverage; fixture correctness alone
does not establish the original investigation's latency targets.
