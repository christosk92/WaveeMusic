// ── Playback/LocalLibrary.Items.cs ──────────────────────────────────────────────────────────────────────────────────
// a playlist's spotify:local: items against the index: the match at every landing, the ids the commit hands back, the re-rule
// when the index changes (restate step 2), and the path map a matched row plays from
//
// Role: SHELL (MatchItem any thread; ItemsLanded, RestateItems and RuleItem on the UI thread; PathOfItem any thread)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-playlist-files-implementation.md §3.2, §3.4, §3.7 step 2 and step 3, D1(a)
//
// IDENTITY (D1 a). A matched row keeps its own `spotify:local:…` text-form id: the playlist member, the now-playing highlight, the
// queue, the playlist's edit verbs and Connect (which must be told the playlist's own uri, report 5.2) all stay on it. The file it
// plays from is the index's answer, kept here as EntityId -> LocalId.
//
// THE FLOW. A list landing stages each item's face (`LocalTrackUri.StageFace`, an api or store thread): one `Index.Match` (its own
// lock, one always-on `local.match` line), the row's Availability from the answer, and a note on the staging. The UI commit hands
// the notes here (`ItemsLanded`): each item's committed id joins the set of seen items and, when it plays, the path map. When the
// index changes (a scan, a folder added or removed, the inspector's fixture), the restate pass ends with `RestateItems`: every seen
// item is matched again and re-staged only where the answer moved, so a row lights up or greys while its list is open, in place.
//
// THREADS. The pump asks `PathOfItem` (one probe under `s_pathGate`, never id text). The seen set and the last answers are UI-thread
// only. Nothing here runs per frame.

using System.Text;

namespace Wavee;

public static partial class LocalLibrary
{
    /// <summary>What the index answers for a playlist item: the file (0: none), whether it plays now (present, a decodable format, its
    /// drive here), and its length.</summary>
    public readonly record struct ItemMatch(long LocalId, bool Plays, int DurationMs);

    // UI thread: every spotify:local: item committed this session (its uri, to match again) and the answer it is staged with.
    static readonly Dictionary<EntityId, string> s_items = new();
    static readonly Dictionary<EntityId, ItemMatch> s_itemAnswers = new();

    // any thread (under s_pathGate): the matched items that play, id -> LocalId.
    static Dictionary<EntityId, long> s_itemMap = new();

    /// <summary>The §3.4 walk for one item's decoded parts, and whether the file it names can play now. ANY THREAD (the index takes its
    /// own lock). One always-on <c>local</c> line: the match's own (<see cref="LocalIndex.Match"/>), or <c>local.match skip</c> when
    /// no music folder is listed (nothing to match against).</summary>
    public static ItemMatch MatchItem(string title, string artist, string album, int seconds)
    {
        if (Folders.Count == 0)
        {
            Log.Info("local", "local.match skip nofolders seconds=" + seconds);
            return default;
        }
        LocalMatchResult r = s_index.Match(title, artist, album, seconds);
        if (r.Entry is not { } e) return default;
        return new ItemMatch(e.LocalId, e.Playable && e.State != LocalState.Missing && !RootAway(e.RootId), Math.Max(0, e.Tags.DurationMs));
    }

    /// <summary>UI THREAD, from <see cref="Entities.Commit"/>: the items a batch staged, now committed. Each joins the seen set; one that
    /// plays joins the path map.</summary>
    internal static void ItemsLanded(Staging staging)
    {
        if (Entities.Current is not { } scope) return;
        bool mapMoved = false;
        for (int i = 0; i < staging.LocalItemCount; i++)
        {
            Staging.LocalItem item = staging.LocalItemAt(i);
            int slot = staging.Slot(scope.Tracks, new StagedId(item.Uri));
            if (slot == Table.None) continue;
            EntityId id = new Track(slot).Id;
            if (!s_items.ContainsKey(id)) s_items[id] = Encoding.UTF8.GetString(staging.Utf8(item.Uri));
            var answer = new ItemMatch(item.LocalId, item.Plays, 0);
            if (s_itemAnswers.TryGetValue(id, out ItemMatch old) && old.LocalId == answer.LocalId && old.Plays == answer.Plays) continue;
            s_itemAnswers[id] = answer;
            mapMoved = true;
        }
        if (mapMoved) RebuildItemMap();
    }

    /// <summary>UI THREAD. Restate step 2 (§3.7): match every seen item again and re-stage the ones whose answer moved — matched and
    /// playable lights the row, anything else greys it — in one commit. Runs after every index pass (<see cref="FinishPass"/>).</summary>
    static void RestateItems()
    {
        if (s_items.Count == 0 || Entities.Current is null) return;
        Staging? staging = null;
        int lit = 0, greyed = 0;
        try
        {
            foreach ((EntityId id, string uri) in s_items)
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(uri);
                if (!LocalTrackUri.TryParse(utf8, out string artist, out string album, out string title, out int seconds)) continue;
                ItemMatch m = MatchItem(title, artist, album, seconds);
                if (s_itemAnswers.TryGetValue(id, out ItemMatch old) && old.LocalId == m.LocalId && old.Plays == m.Plays) continue;
                if (staging is null)
                {
                    staging = Staging.Rent();
                    staging.Epoch = Entities.Current.Epoch;
                }
                // The same staging a landing does, so the commit hands the new answer back through ItemsLanded.
                LocalTrackUri.StageFace(staging, new StagedId(staging.AddText(utf8)), utf8, in m);
                if (m.Plays) lit++; else greyed++;
            }
            if (staging is null) return;
            Entities.Commit(staging);
        }
        finally { if (staging is not null) Staging.Return(staging); }
        Entities.Publish();
        Log.Info("local", "local.items restated lit=" + lit + " greyed=" + greyed + " seen=" + s_items.Count);
    }

    /// <summary>UI THREAD. Rule one <c>spotify:local:</c> row that has no verdict yet — a row a controller's queue or a Liked Songs change
    /// names before any list of ours landed it — the way a list landing would (one match, one commit). Anything else is left alone.
    /// Call it after <c>Entities.Ref</c>/<c>Slot</c> made the row.</summary>
    public static void RuleItem(EntityId id)
    {
        if (id.Provider != EntityProvider.Spotify || id.Form != EntityForm.Text || id.Kind != EntityKind.Track) return;
        if (Entities.Current is not { } scope || !scope.Tracks.TryGetSlot(id, out int slot)) return;
        if (s_items.ContainsKey(id) && new Track(slot).Knows(TrackFields.Availability)) return;
        string uri = id.Text;
        if (!LocalTrackUri.Is(uri)) return;
        byte[] utf8 = Encoding.UTF8.GetBytes(uri);
        Staging staging = Staging.Rent();
        try
        {
            staging.Epoch = scope.Epoch;
            LocalTrackUri.StageFace(staging, new StagedId(staging.AddText(utf8)), utf8);
            // Nobody will answer for its credits either (extended-metadata answers 404 for a local uri, report 3.3).
            if (staging.Tracks.Count > 0) staging.Tracks[staging.Tracks.Count - 1].Known |= (uint)TrackFields.Artists;
            Entities.Commit(staging);
        }
        finally { Staging.Return(staging); }
    }

    /// <summary>The file a matched playlist item plays from, or null. ANY THREAD (the audio pump): one probe under a lock, no id text.</summary>
    public static string? PathOfItem(EntityId id)
    {
        long localId;
        lock (s_pathGate) { if (!s_itemMap.TryGetValue(id, out localId)) return null; }
        return PathOf(localId);
    }

    /// <summary>Does a matched playlist item play from a file here? ANY THREAD (the pump's route: <c>Playback.Audio.RouteOf</c>).</summary>
    public static bool PlaysLocally(EntityId id)
    {
        lock (s_pathGate) return s_itemMap.ContainsKey(id);
    }

    static void RebuildItemMap()
    {
        var map = new Dictionary<EntityId, long>(s_itemAnswers.Count);
        foreach ((EntityId id, ItemMatch m) in s_itemAnswers) if (m.Plays && m.LocalId != 0) map[id] = m.LocalId;
        lock (s_pathGate) s_itemMap = map;
    }
}

public sealed partial class Staging
{
    /// <summary>One <c>spotify:local:</c> item a decode ruled (<see cref="LocalTrackUri.StageFace"/>): its uri in this arena, the file the
    /// index matched (0: none) and whether it plays.</summary>
    public readonly record struct LocalItem(TextRef Uri, long LocalId, bool Plays);

    List<LocalItem>? _localItems;

    /// <summary>Note an item for the commit (<see cref="LocalLibrary.ItemsLanded"/>). The decoder's thread.</summary>
    public void NoteLocalItem(TextRef uri, long localId, bool plays) => (_localItems ??= new()).Add(new LocalItem(uri, localId, plays));

    /// <summary>How many items this batch noted.</summary>
    public int LocalItemCount => _localItems?.Count ?? 0;

    /// <summary>The <paramref name="i"/>th noted item.</summary>
    public LocalItem LocalItemAt(int i) => _localItems![i];
}
