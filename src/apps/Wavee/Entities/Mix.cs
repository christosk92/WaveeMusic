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

/// <summary>The playlist membership edge's WIRE flags (<see cref="PlaylistTrackEdge.Flags"/>) for Mix.</summary>
public static class PlaylistItemFlags
{
    /// <summary>Item attribute 14 <c>{1 "set-transition", 2 {"mix", uri, item id}}</c>: this item's OUTGOING transition
    /// is editable. Its args are all things Wavee already holds, so only the presence is kept.</summary>
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

/// <summary>One staged recipe from a list read (<c>automix.auto_transition_recipe</c> on the item
/// <see cref="ItemId"/>). A run per playlist; a row with an empty <see cref="ItemId"/> alone in its run = "this lens read
/// carries no recipes".</summary>
public struct StagedMixRecipe
{
    public StagedId Playlist;
    public TextRef ItemId, Recipe, TransitionUri;
    /// <summary>Set on the first row of a run from a later page of a truncated list: ADD to the playlist's set.</summary>
    public bool Merge;
}

public sealed partial class Staging
{
    StagedList<StagedMixRecipe>? _mixRecipes;
    public StagedList<StagedMixRecipe> MixRecipes => _mixRecipes ??= Register(new StagedList<StagedMixRecipe>());
    internal StagedList<StagedMixRecipe>? StagedMixRecipes => _mixRecipes;
}

/// <summary>The Mix side tables: the stored transitions per playlist, kind 217's analysis id per track, and the set of
/// playlists the provider reads through the mix lens. In memory only: a mixed list is re-read through its lens when its
/// page opens (O8: no dealer push for a save is known), so nothing here has to survive a restart.</summary>
public static class MixData
{
    static readonly Dictionary<int, Dictionary<ulong, MixTransition>> s_transitions = new();
    static readonly Dictionary<int, string> s_beatsIds = new();
    static readonly ConcurrentDictionary<string, byte> s_lens = new(StringComparer.Ordinal);

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
                    Dictionary<ulong, MixTransition>? set = null;
                    if (rows[start].Merge && MixData.Count(slot) > 0) set = new Dictionary<ulong, MixTransition>(MixData.Map(slot)!);
                    for (int i = start; i < end; i++)
                    {
                        ulong from = MixRecipe.Hex64(s.Utf8(rows[i].ItemId));
                        if (from == 0) continue;
                        var b64 = s.Utf8(rows[i].Recipe);
                        if (!MixRecipe.TryDecodeBase64(b64, out var recipe, out var raw))
                        {
                            Log.Warn("mix", "recipe undecodable on item " + from.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
                            continue;
                        }
                        (set ??= new Dictionary<ulong, MixTransition>())[from] = new MixTransition(recipe, raw,
                            System.Text.Encoding.ASCII.GetString(b64), System.Text.Encoding.UTF8.GetString(s.Utf8(rows[i].TransitionUri)));
                    }
                    MixData.Replace(slot, set);
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
            bool lens = PlaylistMixRules.On(flags) && !MixListenWithout.Is(id);
            MixData.SetLens(id, lens);
            // Once per playlist per session: a lens read that still names no mix lens must not loop.
            if (lens && PlaylistMixRules.OwesLensRead(flags)
                && Current.Edges.PlaylistTracks.State(slot) != EdgeState.Unknown && s_lensReread.Add(slot))
            {
                Log.Info("mix", "lens read owed for a mixed playlist; asking its rows again");
                RefreshEdge(FetchEdge.PlaylistTracks, slot);
            }
        }
    }
}

/// <summary>The local "listen without Mix" choice for one playlist (decision D3): this PC reads and plays it without the
/// lens. Nothing is sent. Persisted in the settings registry as one comma list of bare playlist ids (a handful of rows
/// at most: it is a per-playlist opt-out the user makes by hand).</summary>
public static class MixListenWithout
{
    static HashSet<string>? s_ids;

    static HashSet<string> Ids => s_ids ??= Load();

    static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        string raw = Platform.Settings.Get(Platform.Keys.MixListenWithout);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) set.Add(part);
        return set;
    }

    /// <summary>Is this playlist (bare id) set to play without Mix on this PC? Thread safe for reads after the first load
    /// on the UI thread (the provider never calls it).</summary>
    public static bool Is(string playlistId) => playlistId.Length > 0 && Ids.Contains(playlistId);

    /// <summary>Set or clear the choice; persists. UI thread.</summary>
    public static void Set(string playlistId, bool without)
    {
        if (playlistId.Length == 0) return;
        bool changed = without ? Ids.Add(playlistId) : Ids.Remove(playlistId);
        if (!changed) return;
        Platform.Settings.Set(Platform.Keys.MixListenWithout, string.Join(',', Ids));
        if (without) MixData.SetLens(playlistId, false);
    }
}
