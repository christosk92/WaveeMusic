// ── Entities/Mix.cs — CORE (#48 Mix, plan §3.2, §3.3) ─────────────────────────────────────────────────────────────────
//
// What the wire says about a playlist's Mix, after it lands. Spotify's Mix is a server-side LENS over the playlist, not a
// playlist edit (capture report §3.1): the revision never moves, the lens has its own etag, and only EDITED transitions
// are stored, as a base64 recipe on the outgoing item. Four places carry it, and each lands here:
//
//   list format attributes (mix, mix-type, has-custom-transitions, …)  ─▶ PlaylistTable.MixFlags (list half)
//   SelectedListContent f22 (applied lenses, the mix lens etag)       ─▶ PlaylistTable.MixLensEtag + LensApplied
//   extended metadata 225 / 170 / 235 (mix state, auto lens, trait)   ─▶ PlaylistTable.MixFlags (XM half)
//   item attributes (f14 set-transition, not-mixable, the recipe)     ─▶ PlaylistTrackEdge.Flags + MixData.Transitions
//   item f14 / f11 not-mixable.reason, contents.5 mix-state (lens)    ─▶ MixData.Signals (the templates a write echoes)
//
// The per-track analysis the planner reads (kind 217 beats, kind 28 cue points) lands in Edges.TrackBeats and
// Edges.TrackCues (Edges.cs); kind 217's analysis id, which a recipe pins, in MixData.BeatsIds.
//
// UI THREAD, single writer (the entity tables' rule), EXCEPT the lens set: the fetch provider reads it on its worker
// thread to choose the `spotify-apply-lenses` header, so it is a concurrent set written at commit.

using System.Collections.Concurrent;

namespace Wavee;

/// <summary>A playlist's Mix facts, one <c>uint</c> column (<see cref="PlaylistTable.MixFlags"/>). The low byte is
/// what a list read says (its format attributes and applied lenses), bits 8.. what the extended-metadata kinds say; each
/// answer clears only its own half (<see cref="PlaylistMixRules.ListMask"/>, the XM kinds' own bits).</summary>
[Flags]
public enum PlaylistMixFlags : uint
{
    None = 0,
    /// <summary>List attribute <c>mix=true</c>: the lens is on for this list.</summary>
    ListMix = 1 << 0,
    /// <summary><c>mix-type=allow-custom</c>: custom transitions are allowed.</summary>
    AllowCustom = 1 << 1,
    /// <summary><c>has-custom-transitions=true</c>: at least one pair is edited.</summary>
    HasCustom = 1 << 2,
    /// <summary><c>can-view-transition=true</c>.</summary>
    CanViewTransition = 1 << 3,
    /// <summary><c>automix.ignore_setting=true</c>: Mix overrides the global crossfade setting [inferred].</summary>
    IgnoreSetting = 1 << 4,
    /// <summary><c>automix.queue=true</c>.</summary>
    AutomixQueue = 1 << 5,
    /// <summary><c>automix.autoplay=true</c>.</summary>
    AutomixAutoplay = 1 << 6,
    /// <summary>The answer was read WITH the mix lens applied (f22 named a <c>mix</c> lens): its item attributes carry the
    /// recipes and the per-item mixability. Without it a mixed list's rows say nothing about transitions.</summary>
    LensApplied = 1 << 7,

    /// <summary>Extended metadata 225 <c>{1:1}</c>: "this list is mixed".</summary>
    XmMixState = 1 << 8,
    /// <summary>Extended metadata 170 <c>{1:"mix"}</c>: apply the mix lens when reading or playing the list.</summary>
    XmAutoLens = 1 << 9,
    /// <summary>Extended metadata 235 <c>{1:1}</c>: the list may be mixed (gates the button) [inferred].</summary>
    XmMixable = 1 << 10,
}

/// <summary>The pure rules over <see cref="PlaylistMixFlags"/>.</summary>
public static class PlaylistMixRules
{
    /// <summary>The bits a list read speaks for (its format attributes and f22).</summary>
    public const uint ListMask = 0xFF;

    /// <summary>Mix is on: the list attribute, the mix-state kind, or the auto lens says so.</summary>
    public static bool On(PlaylistMixFlags f)
        => (f & (PlaylistMixFlags.ListMix | PlaylistMixFlags.XmMixState | PlaylistMixFlags.XmAutoLens)) != 0;

    /// <summary>Mix is on but the rows were not read through the lens yet: one full read with it is owed.</summary>
    public static bool OwesLensRead(PlaylistMixFlags f) => On(f) && (f & PlaylistMixFlags.LensApplied) == 0;
}

/// <summary>A mixed list's CACHE IDENTITY is (revision, mix lens etag) (full-support plan §2.1.5): a signal changes the items
/// without moving the revision and causes no push, only the mix lens etag moves (protocol §3.4), and a <c>/diff</c> answer names
/// the etag it holds now in f22 (protocol §1 step 4, §2). So a held mixed list stands only when the diff says the revision is
/// unchanged AND names the etag the list is held at; anything else (a moved etag, a moved revision, a diff that names no mix
/// lens, a failed diff) reads the list in full through the lens. PURE.</summary>
public static class MixIdentity
{
    public enum Verdict : byte { Stands, FullRead }

    public static Verdict Decide(bool revisionUnchanged, string? heldEtag, string? answerEtag)
        => revisionUnchanged && !string.IsNullOrEmpty(heldEtag) && string.Equals(heldEtag, answerEtag, StringComparison.Ordinal)
            ? Verdict.Stands : Verdict.FullRead;
}

/// <summary>The playlist membership edge's WIRE flags (<see cref="PlaylistTrackEdge.Flags"/>) for Mix.</summary>
public static class PlaylistItemFlags
{
    /// <summary>Item attribute 14 <c>{1 "set-transition", 2 {"mix", uri, item id}}</c> is present: this item's OUTGOING transition
    /// is editable. The attribute itself is the signal template a save echoes; a list read through the mix lens keeps its bytes
    /// (<see cref="MixSignals"/>).</summary>
    public const byte Mixable = 1 << 0;
    /// <summary>Item attribute 11 <c>not-mixable=true</c> (reason <c>entity-kind</c> for a local file).</summary>
    public const byte NotMixable = 1 << 1;
}

/// <summary>Kind 28: the chosen start and end cue, ms; 0 = none. The candidate lists are not kept. 12 bytes, unmanaged:
/// the payload of <see cref="Edges.TrackCues"/> (the <see cref="WaveSample"/> payload-only precedent).</summary>
public readonly record struct TrackCues(uint StartCueMs, uint EndCueMs, ushort TempoX10, ushort Reserved = 0);

/// <summary>One stored (edited) transition: the decoded recipe, its raw bytes for a verbatim carry on a later write,
/// and the server-minted <c>spotify:transition:…</c> uri.</summary>
public sealed record MixTransition(MixRecipe Recipe, byte[] Raw, string Base64, string TransitionUri);

/// <summary>One item's Mix half of a list read through the mix lens: its stored recipe (<c>automix.auto_transition_recipe</c>,
/// <c>automix.transition_uri</c>), its signal template (item attribute 14), its not-mixable reason (attribute 11
/// <c>not-mixable.reason</c>); any may be empty. A run per playlist; a row with an empty <see cref="ItemId"/> alone in its run =
/// "this lens read carries no item of the mix".</summary>
public struct StagedMixRecipe
{
    public StagedId Playlist;
    public TextRef ItemId, Recipe, TransitionUri, Template, NotMixableReason;
    /// <summary>On the run's first row: the list's <c>contents.5</c> mix-state signal template.</summary>
    public TextRef ListTemplate;
    /// <summary>Set on the first row of a run from a later page of a truncated list: ADD to the playlist's set.</summary>
    public bool Merge;
}

/// <summary>A mixed playlist's Spotify signal templates, as a list read through the mix lens gave them (plan §2.1.2), for the
/// writes to echo VERBATIM, never rebuild: per mixable item its attribute 14 <c>{1 "set-transition", 2 {1 "mix", 2 uri, 3 item
/// id}}</c> (a transition save sends its field 2 as the signal's args), the list's <c>contents.5</c> entry
/// <c>{1 "mix-state", 2 {1 "mix"}}</c> (the mix-state signal, and the sign that Mix may be switched), and each not-mixable
/// item's reason (<c>entity-kind</c> for a local file).
/// <para>Memory: the item templates sit back to back in one arena, no array per item: a catalogue track's template is 71 bytes
/// (an 8-byte item id, a 36-byte uri), plus its dictionary entry (24) and bucket (4), about 100 bytes per mixable item (a
/// 10,000-row mixed list ≈ 1 MB). Kept only for lists read through the lens. UI thread.</para></summary>
public sealed class MixSignals
{
    byte[] _arena = [];
    int _used;
    readonly Dictionary<ulong, (int Offset, int Length)> _items = new();
    Dictionary<ulong, string>? _reasons;

    /// <summary>The list's mix-state signal template (<c>contents.5</c>); null when the read carried none.</summary>
    public byte[]? MixState { get; internal set; }
    /// <summary>How many items hold a transition template.</summary>
    public int Count => _items.Count;
    /// <summary>The template bytes held (the arena's used part).</summary>
    public int Bytes => _used;
    public bool IsEmpty => _items.Count == 0 && MixState is null && _reasons is null;

    /// <summary>The item's attribute 14 bytes (the <c>set-transition</c> template); empty when it has none.</summary>
    public ReadOnlySpan<byte> TransitionTemplate(ulong itemId)
        => _items.TryGetValue(itemId, out var at) ? _arena.AsSpan(at.Offset, at.Length) : default;

    /// <summary>The item's <c>not-mixable.reason</c>; null when it has none.</summary>
    public string? NotMixableReason(ulong itemId) => _reasons?.GetValueOrDefault(itemId);

    /// <summary>Every item holding a template or a not-mixable reason (the list write's snapshot, Mix.Persist.cs). Allocates.</summary>
    public IEnumerable<ulong> Items()
    {
        foreach (ulong item in _items.Keys) yield return item;
        if (_reasons is null) yield break;
        foreach (ulong item in _reasons.Keys) if (!_items.ContainsKey(item)) yield return item;
    }

    /// <summary>Room for <paramref name="bytes"/> more template bytes: a new set is sized exactly, a later page doubles.</summary>
    internal void Reserve(int bytes)
    {
        if (_used + bytes <= _arena.Length) return;
        Array.Resize(ref _arena, Math.Max(_used + bytes, _arena.Length * 2));
    }

    internal void SetTransitionTemplate(ulong itemId, ReadOnlySpan<byte> template)
    {
        Reserve(template.Length);
        template.CopyTo(_arena.AsSpan(_used));
        _items[itemId] = (_used, template.Length);
        _used += template.Length;
    }

    internal void SetNotMixableReason(ulong itemId, string reason) => (_reasons ??= new())[itemId] = reason;
}

public sealed partial class Staging
{
    StagedList<StagedMixRecipe>? _mixRecipes;
    public StagedList<StagedMixRecipe> MixRecipes => _mixRecipes ??= Register(new StagedList<StagedMixRecipe>());
    internal StagedList<StagedMixRecipe>? StagedMixRecipes => _mixRecipes;
}

/// <summary>The Mix side tables: the stored transitions per playlist, kind 217's analysis id per track, and the set of
/// playlists the provider reads through the mix lens with the lens etag each one is held at. A playlist's transitions,
/// templates and etag are PERSISTED WITH ITS LIST (Mix.Persist.cs, `list_head.mix`), so a cold start owes no lens read; the
/// page's revalidation still asks (signals cause no push), and a moved etag reads the list again (MixIdentity).</summary>
public static class MixData
{
    static readonly Dictionary<int, Dictionary<ulong, MixTransition>> s_transitions = new();
    static readonly Dictionary<int, MixSignals> s_signals = new();
    static readonly Dictionary<int, string> s_beatsIds = new();
    static readonly ConcurrentDictionary<string, byte> s_lens = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string> s_heldEtags = new(StringComparer.Ordinal);

    /// <summary>WORKER THREAD SAFE: the mix lens etag a lens-read playlist (bare base62 id) is held at — with its revision, the
    /// list's cache identity (MixIdentity) — or null when none is held.</summary>
    public static string? HeldEtag(string playlistId) => s_heldEtags.GetValueOrDefault(playlistId);

    /// <summary>Hold (or forget, null) the etag a list read through the lens landed with. UI thread, at commit.</summary>
    internal static void SetHeldEtag(string playlistId, string? etag)
    {
        if (playlistId.Length == 0) return;
        if (string.IsNullOrEmpty(etag)) s_heldEtags.TryRemove(playlistId, out _);
        else s_heldEtags[playlistId] = etag;
    }

    /// <summary>Bumped on every change to the transitions or the lens set. UI-thread reads only.</summary>
    public static int Version { get; private set; }

    /// <summary>The stored transition whose OUTGOING item is <paramref name="fromItemId"/>.</summary>
    public static bool TryGet(int playlistSlot, ulong fromItemId, out MixTransition transition)
    {
        transition = null!;
        return s_transitions.TryGetValue(playlistSlot, out var byItem) && byItem.TryGetValue(fromItemId, out transition!);
    }

    /// <summary>How many edited transitions the playlist holds.</summary>
    public static int Count(int playlistSlot) => s_transitions.TryGetValue(playlistSlot, out var m) ? m.Count : 0;

    /// <summary>Every stored transition of a playlist (diagnostics). Allocates; never per frame.</summary>
    public static IReadOnlyCollection<MixTransition> All(int playlistSlot)
        => s_transitions.TryGetValue(playlistSlot, out var m) ? m.Values : Array.Empty<MixTransition>();

    internal static Dictionary<ulong, MixTransition>? Map(int playlistSlot) => s_transitions.GetValueOrDefault(playlistSlot);

    /// <summary>Replace a playlist's stored transitions (a lens read is the whole set).</summary>
    public static void Replace(int playlistSlot, Dictionary<ulong, MixTransition>? transitions)
    {
        if (transitions is null || transitions.Count == 0) s_transitions.Remove(playlistSlot);
        else s_transitions[playlistSlot] = transitions;
        Version++;
    }

    /// <summary>One pair's stored transition as a write from this PC moves it (Spotify.Mixes: the optimistic save or reset, and
    /// the put-back after a refusal); null removes it. The reply's fold replaces the whole set after.</summary>
    public static void SetTransition(int playlistSlot, ulong fromItemId, MixTransition? transition)
    {
        var map = s_transitions.GetValueOrDefault(playlistSlot);
        if (transition is null)
        {
            if (map is null || !map.Remove(fromItemId)) return;
            if (map.Count == 0) s_transitions.Remove(playlistSlot);
        }
        else
        {
            if (map is null) s_transitions[playlistSlot] = map = new Dictionary<ulong, MixTransition>();
            map[fromItemId] = transition;
        }
        Version++;
    }

    /// <summary>The playlist's signal templates and not-mixable reasons from its last lens read; null when it held none.</summary>
    public static MixSignals? Signals(int playlistSlot) => s_signals.GetValueOrDefault(playlistSlot);

    /// <summary>Replace a playlist's signal set (a first page), or keep the one a later page added to. Does not bump
    /// <see cref="Version"/>: nothing the planner reads changes.</summary>
    public static void ReplaceSignals(int playlistSlot, MixSignals? signals)
    {
        if (signals is null || signals.IsEmpty) s_signals.Remove(playlistSlot);
        else s_signals[playlistSlot] = signals;
    }

    /// <summary>Kind 217's analysis id (32 hex) for a track: the grid a recipe's times refer to.</summary>
    public static string? BeatsId(int trackSlot) => s_beatsIds.TryGetValue(trackSlot, out var id) ? id : null;

    internal static void SetBeatsId(int trackSlot, string id) => s_beatsIds[trackSlot] = id;

    /// <summary>WORKER THREAD SAFE: is this playlist (bare base62 id) read through the mix lens?</summary>
    public static bool ReadsWithLens(string playlistId) => s_lens.ContainsKey(playlistId);

    /// <summary>Add or remove a playlist from the lens set. True when that changed it.</summary>
    public static bool SetLens(string playlistId, bool on)
    {
        bool changed = on ? s_lens.TryAdd(playlistId, 0) : s_lens.TryRemove(playlistId, out _);
        if (changed) Version++;
        return changed;
    }
}

public static partial class Entities
{
    /// <summary>The bare base62 id of a playlist slot (the lens set's key, the provider's path id); "" for a text id.</summary>
    internal static string MixPlaylistId(int slot)
    {
        var gid = Current.Playlists.Id[slot].Gid;
        if (gid == UInt128.Zero) return "";
        Span<char> chars = stackalloc char[Base62.GidChars];
        return new string(chars[..Base62.Encode(gid, chars)]);
    }

    /// <summary>#48: land the staged recipe runs (each contiguous run naming one playlist is that lens read's WHOLE set),
    /// then keep the lens set in step with every playlist row this batch touched: a list Spotify has mixed is read
    /// through the mix lens, and the first time Mix is seen on a list whose rows came without it, its membership is
    /// asked again once (plan §3.2: "the first open of a mixed playlist may cost one extra read").</summary>
    static readonly HashSet<int> s_lensReread = new();

    /// <summary>How many times this session a mixed list owed a lens read (diagnostics: a list restored with its Mix half owes none).</summary>
    public static int MixLensRereads { get; private set; }

    static void CommitMix(Staging s)
    {
        var playlists = Current.Playlists;
        if (s.StagedMixRecipes is { Count: > 0 } staged)
        {
            var rows = staged.Span;
            for (int start = 0; start < rows.Length;)
            {
                int end = start + 1;
                while (end < rows.Length && rows[end].Playlist.Packed == rows[start].Playlist.Packed
                       && rows[end].Playlist.Text == rows[start].Playlist.Text) end++;
                int slot = s.Slot(playlists, in rows[start].Playlist);
                if (slot != Table.None)
                {
                    bool merge = rows[start].Merge;
                    Dictionary<ulong, MixTransition>? set = null;
                    if (merge && MixData.Count(slot) > 0) set = new Dictionary<ulong, MixTransition>(MixData.Map(slot)!);
                    // The signal templates: a first page starts a new set sized to its templates, a later page adds to the playlist's own.
                    var signals = (merge ? MixData.Signals(slot) : null) ?? new MixSignals();
                    int templateBytes = 0;
                    for (int i = start; i < end; i++) templateBytes += rows[i].Template.Length;
                    signals.Reserve(templateBytes);
                    var listTemplate = s.Utf8(rows[start].ListTemplate);
                    if (!listTemplate.IsEmpty) signals.MixState = listTemplate.ToArray();
                    for (int i = start; i < end; i++)
                    {
                        ulong from = MixRecipe.Hex64(s.Utf8(rows[i].ItemId));
                        if (from == 0) continue;
                        var template = s.Utf8(rows[i].Template);
                        if (!template.IsEmpty) signals.SetTransitionTemplate(from, template);
                        var reason = s.Utf8(rows[i].NotMixableReason);
                        if (!reason.IsEmpty) signals.SetNotMixableReason(from, System.Text.Encoding.UTF8.GetString(reason));
                        var b64 = s.Utf8(rows[i].Recipe);
                        if (b64.IsEmpty) continue;
                        if (!MixRecipe.TryDecodeBase64(b64, out var recipe, out var raw))
                        {
                            Log.Warn("mix", "recipe undecodable on item " + from.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
                            continue;
                        }
                        (set ??= new Dictionary<ulong, MixTransition>())[from] = new MixTransition(recipe, raw,
                            System.Text.Encoding.ASCII.GetString(b64), System.Text.Encoding.UTF8.GetString(s.Utf8(rows[i].TransitionUri)));
                    }
                    MixData.Replace(slot, set);
                    MixData.ReplaceSignals(slot, signals);
                    playlists.Bump(slot);
                }
                start = end;
            }
        }

        if (s.StagedPlaylists is not { Count: > 0 } heads) return;
        foreach (ref readonly var row in heads.Span)
        {
            if (row.MixFlagsMask == 0) continue;
            int slot = s.Slot(playlists, in row.Id);
            if (slot == Table.None) continue;
            string id = MixPlaylistId(slot);
            if (id.Length == 0) continue;
            var flags = (PlaylistMixFlags)playlists.MixFlags[slot];
            bool lens = PlaylistMixRules.On(flags) && !MixOptOut.Is(new global::Wavee.Playlist(slot));
            MixData.SetLens(id, lens);
            // The list's cache identity is (revision, mix lens etag): the etag a lens read (or the disk) landed is held beside the
            // revision for the provider's revalidation (Spotify.Api.ReadList, MixIdentity).
            MixData.SetHeldEtag(id, lens && (flags & PlaylistMixFlags.LensApplied) != 0 ? Strings.Resolve(playlists.MixLensEtag[slot]) : null);
            // Once per playlist per session: a lens read that still names no mix lens must not loop.
            if (lens && PlaylistMixRules.OwesLensRead(flags)
                && Current.Edges.PlaylistTracks.State(slot) != EdgeState.Unknown && s_lensReread.Add(slot))
            {
                MixLensRereads++;
                Log.Info("mix", "lens read owed for a mixed playlist; asking its rows again");
                RefreshEdge(FetchEdge.PlaylistTracks, slot);
            }
        }
    }
}

/// <summary>"Listen without Mix" for one playlist, SYNCED TO THE ACCOUNT (owner decision 2026-11-25): the account's
/// `mix-opt-out` / `mix-remove-opt-out` signals (Spotify.Mixes.SetListenWithoutMix), and this LOCAL MIRROR of them, which is
/// what Wavee reads: this account reads and plays an opted-out playlist without the lens.
/// <para>THE MIRROR IS THE SOURCE OF TRUTH. How an opt-out reads back on another device (which attribute or lens shows it) is open
/// (protocol §3.3, §13 #14): no lens read in M1's fixtures or the captures carries one, so nothing here is updated from a read.
/// The opt-out replies' shape is logged (the `mix` line after each one) so the owner's live check can name the field.</para>
/// <para>Each entry is (account, playlist id) → (opted out, pending): pending = Spotify has not taken it yet (offline, refused, or
/// migrated); <see cref="OnSessionReady"/> sends every pending entry when the account's session comes online. Persisted in the
/// settings registry as one line per entry, <c>&lt;account&gt;|&lt;playlist id&gt;|&lt;1 or 0&gt;[|p]</c>. UI thread.</para>
/// <para>THE MIGRATION: the per-PC list this replaces (`playback.mix.listenWithout`, a comma list of playlist ids) is moved into the
/// mirror of the first account that comes online, sent as opt-outs, and the key is removed; one log line says so.</para></summary>
public static class MixOptOut
{
    /// <summary>The per-PC "listen without Mix" list before M2. Read once by the migration, then removed.</summary>
    static readonly SettingKey<string> s_legacy = new("playback.mix.listenWithout", "");

    static Dictionary<(string Account, string Playlist), (bool Out, bool Pending)>? s_map;

    static Dictionary<(string Account, string Playlist), (bool Out, bool Pending)> Map => s_map ??= Load();

    static string Account => Entities.Current?.Key.Account ?? "";

    /// <summary>The key a playlist's choice is stored under: its bare base62 id (a fake text id's last part).</summary>
    public static string KeyOf(Playlist p)
    {
        string id = Entities.MixPlaylistId(p.Slot);
        return id.Length > 0 ? id : new string(EntityUri.IdOf(p.Uri.Text.AsSpan()));
    }

    /// <summary>Does this account listen to the playlist without Mix?</summary>
    public static bool Is(Playlist p) => p.IsValid && Is(KeyOf(p));

    /// <summary>Does this account listen to the playlist (bare id) without Mix? UI thread (the provider never calls it).</summary>
    public static bool Is(string playlistId)
        => playlistId.Length > 0 && Map.TryGetValue((Account, playlistId), out var e) && e.Out;

    /// <summary>Is the choice for this playlist still waiting for Spotify?</summary>
    public static bool IsPending(string playlistId) => Map.TryGetValue((Account, playlistId), out var e) && e.Pending;

    /// <summary>Record the user's choice (pending until Spotify takes it); persists. UI thread.</summary>
    internal static void Set(string playlistId, bool without, bool pending)
    {
        if (playlistId.Length == 0) return;
        var key = (Account, playlistId);
        // An opted-in playlist Spotify has taken needs no line at all: absent = listen with Mix.
        if (!without && !pending) { if (!Map.Remove(key)) return; }
        else if (Map.TryGetValue(key, out var e) && e == (without, pending)) return;
        else Map[key] = (without, pending);
        Save();
        if (without) MixData.SetLens(playlistId, false);
    }

    /// <summary>Spotify took <paramref name="without"/> for the playlist: the entry stops being pending (unless the user changed
    /// it again while the write was out).</summary>
    internal static void Landed(string playlistId, bool without)
    {
        if (Map.TryGetValue((Account, playlistId), out var e) && e.Out == without) Set(playlistId, without, pending: false);
    }

    /// <summary>The signed-in account's pending entries, in playlist-id order.</summary>
    static List<(string Playlist, bool Out)> PendingNow()
    {
        var list = new List<(string, bool)>();
        string account = Account;
        foreach (var (key, value) in Map) if (key.Account == account && value.Pending) list.Add((key.Playlist, value.Out));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    /// <summary>The per-PC list's raw value (empty once migrated; the inspector's proof).</summary>
    public static string LegacyValue => Platform.Settings.Get(s_legacy);

    /// <summary>Seed the per-PC list (the inspector's migration proof under --fake only).</summary>
    internal static void SeedLegacy(string commaList) { if (Platform.Args.Fake) Platform.Settings.Set(s_legacy, commaList); }

    /// <summary>The mirror's entries for this account (diagnostics).</summary>
    public static IEnumerable<(string Playlist, bool Out, bool Pending)> Entries()
    {
        string account = Account;
        foreach (var (key, value) in Map) if (key.Account == account) yield return (key.Playlist, value.Out, value.Pending);
    }

    /// <summary>The account's session is online (Spotify.Library.SyncNow; the inspector's `mix migrate` under --fake): migrate the
    /// per-PC list into this account's mirror as pending opt-outs, then send every pending entry. When the migration had entries,
    /// one log line once they all settled, and the per-PC key is removed (the mirror keeps any that failed pending, for the next
    /// start). <paramref name="reported"/> hears that line (or "" when there was nothing to migrate) after the last send settles.</summary>
    public static void OnSessionReady(Action<string>? reported = null)
    {
        if (Account.Length == 0) { reported?.Invoke(""); return; }
        string legacy = Platform.Settings.Get(s_legacy);
        var migrated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in legacy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // The per-PC key held a bare id, or a playlist uri for a text id.
            string id = part.Contains(':') ? new string(EntityUri.IdOf(part.AsSpan())) : part;
            if (id.Length == 0 || !migrated.Add(id)) continue;
            Set(id, without: true, pending: true);
        }
        var pending = PendingNow();
        if (pending.Count == 0)
        {
            if (legacy.Length > 0) Finish(0, 0);
            else reported?.Invoke("");
            return;
        }
        int left = pending.Count, sent = 0, failed = 0;
        foreach (var (id, without) in pending)
        {
            bool fromLegacy = migrated.Contains(id) && without;
            Spotify.Mixes.SendOptOut(id, without, outcome =>
            {
                if (fromLegacy) { if (outcome == Spotify.Mixes.Outcome.Landed) sent++; else failed++; }
                if (--left == 0) { if (legacy.Length > 0) Finish(sent, failed); else reported?.Invoke(""); }
            });
        }

        void Finish(int sentCount, int failedCount)
        {
            Platform.Settings.Remove(s_legacy.Name);
            string line = "listen-without migrated: per-PC entries=" + migrated.Count + " sent as account opt-outs=" + sentCount
                        + " failed=" + failedCount + (failedCount > 0 ? " (kept pending, sent at the next start)" : "") + "; per-PC key removed";
            Log.Info("mix", line);
            reported?.Invoke(line);
        }
    }

    static Dictionary<(string Account, string Playlist), (bool Out, bool Pending)> Load()
    {
        var map = new Dictionary<(string Account, string Playlist), (bool Out, bool Pending)>();
        foreach (var line in Platform.Settings.Get(Platform.Keys.MixOptOuts).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = line.Split('|');
            if (f.Length < 3 || f[0].Length == 0 || f[1].Length == 0) continue;
            map[(f[0], f[1])] = (f[2] == "1", f.Length > 3 && f[3] == "p");
        }
        return map;
    }

    static void Save()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (key, value) in Map)
            sb.Append(key.Item1).Append('|').Append(key.Item2).Append('|').Append(value.Item1 ? '1' : '0').Append(value.Item2 ? "|p" : "").Append('\n');
        Platform.Settings.Set(Platform.Keys.MixOptOuts, sb.ToString());
    }
}
