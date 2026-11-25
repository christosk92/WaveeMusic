// ── Entities/Mix.Persist.cs — CORE (#48 Mix M2: a mixed list's Mix half on disk, full-support plan §2.1.5) ────────────────
//
// A mixed list read through the mix lens carries more than its rows: the stored recipes (with their raw bytes and transition
// uris), the signal templates every write echoes (item attribute 14, the list's contents.5 mix-state entry), the not-mixable
// reasons, the list's Mix attributes and the mix lens etag. Until M2 none of it survived a restart, so every cold start owed
// one lens read per mixed list before a seam or a save could be shown. It now rides WITH THE LIST it was read with: one BLOB
// in `list_head.mix` (Store.Lists.cs), written in the same transaction as the rows and the revision, read back through the
// very staging a lens read lands through (StagedMixRecipe + the playlist row's Mix bits and etag), so CommitMix holds the lens,
// the templates, the recipes and the etag exactly as after the read. The column is new DDL, so the file's fingerprint moves
// and the cache opens a new file (Store.cs's rule: a cache is never migrated).
//
//   MixBlob { 1 version (1), 2 list Mix flags (the read's half, LensApplied included), 3 lens etag, 4 contents.5 mix-state
//             template, 5[] item { 1 item id (16 hex), 2 attribute 14 template, 3 recipe base64, 4 transition uri,
//             5 not-mixable reason } }
//
// Encode runs on the UI thread (it reads MixData and the table); Stage on any thread (the store thread's read): it writes the
// staging and nothing else.

namespace Wavee;

public static class MixPersist
{
    const int Version = 1;

    /// <summary>UI THREAD. The playlist's Mix half as one blob, or null when there is nothing to keep: the list is not mixed, or
    /// its rows were not read through the lens.</summary>
    public static byte[]? Encode(Scope scope, int playlistSlot)
    {
        var playlists = scope.Playlists;
        if (playlistSlot <= Table.None || playlistSlot >= playlists.Count) return null;
        uint flags = playlists.MixFlags[playlistSlot] & PlaylistMixRules.ListMask;
        if (!PlaylistMixRules.On((PlaylistMixFlags)flags) || (flags & (uint)PlaylistMixFlags.LensApplied) == 0) return null;
        string etag = Entities.Strings.Resolve(playlists.MixLensEtag[playlistSlot]);
        var signals = MixData.Signals(playlistSlot);
        var items = new SortedSet<ulong>();
        if (signals is not null) foreach (ulong item in signals.Items()) items.Add(item);
        foreach (var t in MixData.All(playlistSlot)) items.Add(t.Recipe.FromItemId);

        int size = 64 + etag.Length + (signals?.MixState?.Length ?? 0) + (signals?.Bytes ?? 0);
        foreach (var t in MixData.All(playlistSlot)) size += t.Base64.Length + t.TransitionUri.Length * 3 + 16;
        size += items.Count * 64;
        if (signals is not null) foreach (ulong item in items) size += (signals.NotMixableReason(item)?.Length ?? 0) * 3;
        var buffer = new byte[size];
        var w = new Spotify.Decode.ProtoWriter(buffer);
        w.UAlways(1, Version);
        w.UAlways(2, flags);
        w.Str(3, etag);
        if (signals?.MixState is { } mixState) w.Utf8Always(4, mixState);
        foreach (ulong item in items)
        {
            int m = w.Open(5);
            w.StrAlways(1, item.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            if (signals is not null) w.Utf8(2, signals.TransitionTemplate(item));
            if (MixData.TryGet(playlistSlot, item, out var transition))
            {
                w.Str(3, transition.Base64);
                w.Str(4, transition.TransitionUri);
            }
            w.Str(5, signals?.NotMixableReason(item));
            w.Close(m);
        }
        return buffer.AsSpan(0, w.Length).ToArray();
    }

    /// <summary>ANY THREAD. Stage a blob for <paramref name="playlistUri"/> exactly as a lens read stages its Mix half
    /// (Spotify.Decode.MixList): the playlist row's list Mix bits and etag, and one StagedMixRecipe run (a run of one empty row
    /// when the list holds no item of the mix). False, staging nothing, for a blob of another version or one that does not parse.</summary>
    public static bool Stage(Staging s, string playlistUri, ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty || string.IsNullOrEmpty(playlistUri)) return false;
        long version = 0;
        uint flags = 0;
        ReadOnlySpan<byte> etag = default, mixState = default;
        for (var r = new Spotify.Decode.ProtoReader(blob); r.Next();)
        {
            if (r.Field == 1 && r.Wire == 0) version = (long)r.Varint();
            else if (r.Field == 2 && r.Wire == 0) flags = (uint)r.Varint();
            else if (r.Field == 3 && r.Wire == 2) etag = r.Bytes();
            else if (r.Field == 4 && r.Wire == 2) mixState = r.Bytes();
            else r.Skip();
        }
        if (version != Version || etag.IsEmpty) return false;
        StagedId id = EntityId.TryParseGid(playlistUri.AsSpan(), out EntityId gid) ? new StagedId(gid)
                    : new StagedId(s.AddText(System.Text.Encoding.UTF8.GetBytes(playlistUri)));
        ref var row = ref s.Playlists.RowFor(id, Authority.Thin, (uint)PlaylistFields.Mix);
        row.MixFlags = flags & PlaylistMixRules.ListMask;
        row.MixFlagsMask = PlaylistMixRules.ListMask;
        row.MixLensEtag = s.AddText(etag);

        var recipes = s.MixRecipes;
        int first = recipes.Count;
        for (var r = new Spotify.Decode.ProtoReader(blob); r.Next();)
        {
            if (r.Field != 5 || r.Wire != 2) { r.Skip(); continue; }
            ReadOnlySpan<byte> item = default, template = default, recipe = default, uri = default, reason = default;
            for (var f = r.Message(); f.Next();)
            {
                if (f.Wire != 2) { f.Skip(); continue; }
                switch (f.Field)
                {
                    case 1: item = f.Bytes(); break;
                    case 2: template = f.Bytes(); break;
                    case 3: recipe = f.Bytes(); break;
                    case 4: uri = f.Bytes(); break;
                    case 5: reason = f.Bytes(); break;
                    default: f.Skip(); break;
                }
            }
            if (item.IsEmpty) continue;
            ref var staged = ref recipes.Add();
            staged.Playlist = id;
            staged.ItemId = s.AddText(item);
            staged.Template = s.AddText(template);
            staged.Recipe = s.AddText(recipe);
            staged.TransitionUri = s.AddText(uri);
            staged.NotMixableReason = s.AddText(reason);
        }
        if (recipes.Count == first)
        {
            ref var none = ref recipes.Add();
            none.Playlist = id;
        }
        recipes.Span[first].ListTemplate = s.AddText(mixState);
        return true;
    }
}
