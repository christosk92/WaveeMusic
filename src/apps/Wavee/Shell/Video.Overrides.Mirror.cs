// ── Shell/Video.Overrides.Mirror.cs ────────────────────────────────────────────────────────────────────────────────
// G-220: THE one production subscriber of `Video.Overrides.Changed` — the roster's mutations mirrored onto the track
// row (`TrackFlags.VideoOverride`, `TrackTable.LocalVideo`), plus the allocation-free id index the track COMMIT reads
// so a row that lands after the attach lights too. NAMED PARTIAL of Shell/Video.Host.cs, sibling of
// Shell/Video.Overrides.cs — which is the roster itself and decides nothing.
//
// Role: HOST
// Owner: K
// Wave: 4 (gap fix)
// Budget: 200 lines
// Spec: G-220 (gap register); ch 24 §7 DATA GAP 3
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// THE DEFECT. Every surface that asks "does this row have a video?" — the player bar's video slot, the immersive
// rail, the availability fold, the album page's indicator — reads `Track.HasVideo`, which is
// `(Flags & (HasVideo | VideoOverride)) != 0` (`Entities/Track.cs`). `Overrides.Changed` had NO subscriber, and the
// commit's Video arm only PRESERVES a `VideoOverride` bit that is already on the row — so nothing in the app ever
// ORIGINATED it. An attached file PLAYED (the resolver's tier 1 asks the roster directly,
// `Playback/Playback.Video.Source.cs`) while no button, badge or row indicator ever appeared.
//
// WHY A DIRECT COLUMN WRITE AND NOT A STAGING COMMIT (which is what the register's remedy column suggested). Two
// reasons, and the second one is fatal to the staging shape:
//   · a staged row resolves its identity through `Staging.Slot`, which ALLOCATES a row for an unseen uri — attaching
//     a file to a module playable, or to a track no page has mentioned, would mint a phantom `TrackTable` row;
//   · a staged row cannot REMOVE the bit. The Video arm ORs `row.Flags & VideoMask` over the preserved override bit,
//     and there is no shape of staged row that says "the user detached this". Detach is half of this feature.
// So the write is `Table.Bump` + `Entities.Publish()` — the sanctioned "a write that is not a provider answer" path
// (`Entities.cs`) — and the string column goes through `SetText`/`ClearText`, so the interner's refcount stays
// balanced exactly as `TrackTable.ReleaseText` expects (defect 1).
//
// WHY `Bump(slot)` AND NOT `Bump(slot, TrackFields.Video)`. The roster is not an ANSWER for the Video group. Sealing
// that group's Known bit would tell `Fetch.NeedOf` the kind-99 association is settled — so a catalogue music video
// would never be asked for again for that row — and would make `Album`'s `Knows(TrackFields.Video)` readiness gate
// read true for a row nobody answered for. This writes the user's CURATION; the group's knowledge is untouched.
//
// WHY NOTHING AUTO-OPENS. `Video.OverrideMutation.Plan` also plans a reveal (`RevealSurfaceIfCurrent`) and a forced
// reload; this mirror deliberately applies NEITHER. The decision (2026-09-18) is that attaching a file lights the
// BUTTON and plays nothing, so the only surface effect is `State.FoldForTrack(hasVideo, boundary: false)`, whose
// upgrade is DEFERRED (`Video.DeferUpgrade`): the badge lights now, the surface waits for the user's click.
//
// AND THAT FOLD IS A BELT, NOT THE BRACES. `HostObserver`'s availability effect already re-folds on every tracks
// publication (`Video.Host.Wiring.cs`: `CurrentHasVideo(subscribe: true)`), so the `Bump` + `Publish` below lights
// the badge by itself wherever the observer is mounted. The direct call covers the two cases it cannot: no observer
// yet, and an attachment on a playable with no track ROW at all. It is idempotent either way — `BoundaryFold.Commits`
// returns false when the fold changes nothing. What neither can cover is the MODULE plane (ch 01 GAP 8): the
// observer's `CurrentHasVideo` answers false for a non-track playable, so it will fold that one back.
//
// THREAD. UI thread, and no marshalling — every producer of `Changed` is already on it: the five verbs in
// `Entities/Track.Menu.Video.cs` (inside a menu invoke, around a modal picker that must run on the window's thread),
// the two drop paths (`Entities/Playlist.Page.cs`, `Entities/Track.Table.cs`, inside an input handler), the Settings
// roster (`Screens/Settings.UI.Video.cs`) and the undo callbacks on the notification queue. The roster raises
// `Changed` OUTSIDE its own lock, so a handler that writes columns and publishes cannot deadlock against it.
// `EntityId.Parse` interns, and the column writes and `Publish` are C1 — a background producer would have to
// marshal (`Playback.ToUi`) BEFORE it calls the roster, not here, because the roster's own `Persist` writes settings
// on the caller's thread too.

using FluentGpu.Signals;

namespace Wavee;

public static partial class Video
{
    /// <summary>THE composition call for the mirror (`App.cs`, immediately after <see cref="Install"/>). Idempotent:
    /// the subscription is unsubscribe-then-subscribe, and the index is rebuilt from the roster as loaded.</summary>
    public static void InstallMirror() => OverrideMirror.Install();

    /// <summary>The roster → entity-columns mirror, and the identity index the track commit probes.
    /// <para>A SIBLING of <see cref="Overrides"/> rather than a part of it: <c>Overrides</c> is not a partial class,
    /// and this index is a DERIVED view of it — the roster stays "a dictionary, a file and an epoch".</para>
    /// <para>Public, not internal: this assembly has no <c>InternalsVisibleTo</c> (see <c>Platform/Controls.cs</c>),
    /// so <c>Wavee.Tests</c> can only pin the mirror through a public surface.</para></summary>
    public static class OverrideMirror
    {
        /// <summary>identity → the attached path. Keyed by the PARSED id because that is what the reader on the hot
        /// side holds: the commit arm has a slot, and a slot's <see cref="EntityId"/> is a field read
        /// (<c>Table.Id</c>), while its uri text is a format or a resolve. Rebuilt whenever the roster's epoch moves,
        /// which is what makes `Attach(settings)` (a fresh load) and `Clear()` — neither of which raises
        /// <see cref="Overrides.Changed"/> — heal themselves on the next read instead of drifting.</summary>
        static readonly Dictionary<EntityId, string> Index = new();

        /// <summary>The roster's own discipline, kept for the same reason: the index is a DERIVED view of a
        /// process-wide static that is itself locked because the PLAYBACK path reads it off the UI thread. Uncontended
        /// it costs a few nanoseconds, which is what a per-row probe can afford; the COLUMN writes below are C1
        /// regardless of any lock, and stay on the UI thread.</summary>
        static readonly Lock Gate = new();

        /// <summary>The roster epoch <see cref="Index"/> was built from; -1 = never, so the first read builds it.</summary>
        static int s_indexed = -1;

        /// <summary>Subscribe and index. Called by the composition root; safe to call twice.</summary>
        public static void Install()
        {
            Overrides.Changed -= OnChanged;
            Overrides.Changed += OnChanged;
            Reindex();
        }

        /// <summary>Drop the subscription (the pair to <see cref="Install"/>; a test's teardown, exactly as
        /// <c>Overrides.Attach(null)</c> is the roster's). The index heals itself from the epoch either way.</summary>
        public static void Uninstall() => Overrides.Changed -= OnChanged;

        // ── the reads the COMMIT takes (one int compare, then one dictionary probe: no allocation) ──────────────────

        /// <summary>Did the user attach a file to THIS row? The question `CommitTracks` asks for every staged row, so
        /// it must cost nothing: one int compare for the epoch, and an empty index — the state of every session in
        /// which the user has curated nothing — returns before it hashes anything. Quarantine is deliberately not
        /// folded in: a quarantined attachment still means "the user wants video here"
        /// (<see cref="Overrides.Has"/>).</summary>
        public static bool Has(EntityId id) => !id.IsEmpty && TryPath(id, out _);

        /// <summary>…and the path to mirror into <c>TrackTable.LocalVideo</c> with it. The string is the roster
        /// record's own, so interning it is a probe of an entry that already exists.</summary>
        public static bool TryPath(EntityId id, out string path)
        {
            path = "";
            if (id.IsEmpty) return false;
            lock (Gate)
            {
                Fresh();
                if (!Index.TryGetValue(id, out string? found)) return false;
                path = found;
                return true;
            }
        }

        /// <summary>How many attachments the index carries (the log's count, and a fact for the tests).</summary>
        public static int Count { get { lock (Gate) { Fresh(); return Index.Count; } } }

        /// <summary>Under <see cref="Gate"/>.</summary>
        static void Fresh()
        {
            if (Overrides.Epoch.Peek() != s_indexed) Rebuild();
        }

        static void Reindex() { lock (Gate) Rebuild(); }

        /// <summary>Rebuild from the roster, under <see cref="Gate"/>. ALLOCATES (`Overrides.All`), once per roster
        /// mutation — the roster changes at HUMAN rate, which is the same reason it is an epoch and not a per-row
        /// subscription. Safe under the lock: the roster raises <see cref="Overrides.Changed"/> OUTSIDE its own gate,
        /// so nothing ever takes the roster's lock and then this one.</summary>
        static void Rebuild()
        {
            s_indexed = Overrides.Epoch.Peek();
            Index.Clear();
            var all = Overrides.All();
            for (int i = 0; i < all.Count; i++)
            {
                // A catalogue uri parses to the GID form with no intern at all; a module playable's takes the text
                // form, which interns the uri the roster already holds (bounded by the roster, which is human-sized).
                var id = EntityId.Parse(all[i].Uri.AsSpan());
                if (!id.IsEmpty) Index[id] = all[i].Path;
            }
        }

        // ── the mutation mirror ─────────────────────────────────────────────────────────────────────────────────────

        static void OnChanged(string uri, OverrideMutationKind kind)
        {
            Reindex();                                        // the mutation bumped the epoch before it raised this
            var id = EntityId.Parse(uri.AsSpan());
            if (id.IsEmpty) return;                           // a uri nothing can identify is not a row and not a fold
            bool attached = kind != OverrideMutationKind.Remove;

            // The row, WITHOUT allocating one: an attachment for a playable no page has ever mentioned has no slot to
            // write, and the index is what lights it when its row finally lands (`CommitTracks`).
            if (Entities.Current is { } scope && scope.Tracks.TryGetSlot(id, out int slot) && slot > Table.None)
            {
                var t = scope.Tracks;
                if (attached)
                {
                    t.Flags[slot] |= (uint)TrackFlags.VideoOverride;
                    if (TryPath(id, out string path)) t.SetText(ref t.LocalVideo, slot, Entities.Strings.Intern(path));
                }
                else
                {
                    t.Flags[slot] &= ~(uint)TrackFlags.VideoOverride;
                    t.ClearText(ref t.LocalVideo, slot);
                }
                t.Bump(slot);                                 // NOT the Video group's Known bit (file header)
                Entities.Publish();                           // this is no drain: publish the row now
                Log.Info(State.LogCategory,
                    $"local video {(attached ? "mirrored onto" : "cleared from")} track slot {slot} (roster: {Count})");
            }

            // THE BADGE, in this frame. `FoldForTrack` reads the row this handler just wrote, so the has-video answer
            // is the catalogue plane OR the attachment — a REMOVE on a track that also has a kind-99 video keeps its
            // badge, which is exactly what the row can plainly see.
            if (Playback.CurrentId.Peek().Equals(id)) State.FoldForTrack(HasVideoNow(id), boundary: false);
        }

        /// <summary>The has-video answer for one identity: the row's own two planes when there is a row, else the
        /// roster alone (a module playable, whose form probe is `Platform/Modules.cs`'s third plane).</summary>
        static bool HasVideoNow(EntityId id)
        {
            if (Entities.Current is { } scope && scope.Tracks.TryGetSlot(id, out int slot) && slot > Table.None
                && new Track(slot) is { IsValid: true } row)
                return row.HasVideo;
            return Has(id);
        }
    }
}
