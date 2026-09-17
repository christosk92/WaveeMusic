// ── Spotify/Spotify.Encode.cs ─────────────────────────────────────────────────────────────────────────────────────────
// the library writes' pure half: the playlist4 /changes envelope (playlist, rootlist, create), the client-minted ids, the
// folder markers, the rootlist op math over the marker stream, and the library host's decisions (sync, pushes, pins)
//
// Role: CORE
// Owner: E
// Wave: gap batch B2
// Budget: 1150 lines
// Spec: gap register G-043, G-042 (sync rules), G-048 (owned caps), G-049 (pin bridge), G-062; decisions D10, D11 —
//       a named partial of Spotify
//
// BYTE-EXACT, OR NOT AT ALL. The playlist service keys conflict detection, echo suppression and dedup on the exact
// envelope desktop sends, so every builder here reproduces 0.2.9's `PlaylistWireMapper` field for field and is pinned by
// `EncodeTests` against the desktop captures (`Fixtures/playlist-wire`, 2026-08-15): ListChanges{ base_revision,
// deltas[ Delta{ ops, info{ user, timestamp } } ], want_resulting_revisions, want_sync_result, nonces[1] }. Two things
// that look like omissions are the desktop shape: a Delta carries NO base_version of its own, and ChangeInfo carries
// NOTHING but user + timestamp. The COLLECTION write body is not here: `Spotify.Api.CollectionWriteBody` (batch B1b)
// already is 0.2.9's `CollectionWriteMapper`, and one encoder per wire shape is the rule.
//
// THE ROOTLIST IS INDEX OPS OVER A MARKER STREAM. A folder is a balanced pair (`spotify:start-group:<id>:<name>` …
// `spotify:end-group:<id>`) inside the flat item stream, so every organisation gesture — a move, a folder create,
// rename or delete, a playlist follow — is a positional ADD/REM/MOV against the list the server holds at the base
// revision. `RootlistOps.CheckMove` (Shell/Sidebar.cs) is the LEGALITY authority a cue asks; `TryBuildMove` here is the
// WRITER, over the same index math, and `RootlistSlotToOpTests` pins that the two cannot drift (the op a legal cue
// builds lands the row where the cue said). Every reply to a rootlist `/changes` is revision bookkeeping only (golden
// a164-folder-create-response), so the new tree is computed locally (`ApplyLocally`), never read out of the answer.
//
// THE HOST'S DECISIONS LIVE HERE TOO, because they are decisions: when a login syncs (`LibrarySyncRules`), which
// relations a dealer push names (`LibraryPushRules`), which of the account's playlists it owns without a capability
// block yet (`LibraryCaps`), and how Spotify's ylpin set and the sidebar's pin store converge (`LibraryPinSync`, the port
// of 0.2.9's `SidebarPinSync`). `Spotify.Library.cs` is the SHELL that feeds them values and sends what they build.
//
// Rules: pure functions over values and the entity tables (UI thread where a table is read or written, C1); the
// randomness of a client-minted id is the only non-determinism, and every builder takes its nonce and clock as
// arguments so a golden can pass the capture's own. Writes allocate (a request body is an allocation by nature); no
// render or per-frame path calls anything here.

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using FluentGpu.Foundation;
using Google.Protobuf;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee;

// ── 1. the op vocabulary ─────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What one playlist4 op does.</summary>
public enum PlaylistOpKind : byte { Add, Remove, Move, UpdateList, UpdateItem }

/// <summary>Where an ITEM-KEYED move lands (the playlist reorder shape); <see cref="None"/> for a positional op.</summary>
public enum PlaylistMoveAnchor : byte { None = 0, First, Last, AfterItem }

/// <summary>One item an op names: its uri, the client-minted 16-hex <paramref name="ItemId"/> ("" for a rootlist row,
/// which has none), and the add instant in unix MILLISECONDS (0 = not stated).</summary>
public readonly record struct PlaylistMember(string Uri, string ItemId = "", long AddedAtMs = 0);

/// <summary>The list-attribute half of an UPDATE_LIST op. A null member says nothing; a Clear flag travels as
/// <c>no_value</c>.</summary>
public sealed record PlaylistListPatch(string? Name = null, string? Description = null, bool? Collaborative = null,
    bool? DeletedByOwner = null, bool ClearName = false, bool ClearDescription = false, bool ClearPicture = false);

/// <summary>ONE playlist4 op, as a value. Positional fields (<see cref="FromIndex"/>/<see cref="Length"/>/
/// <see cref="ToIndex"/>) for the rootlist shapes; <see cref="ItemsAsKey"/> + <see cref="Anchor"/> for the keyed playlist
/// shapes; <see cref="Patch"/> for UPDATE_LIST; <see cref="ItemPublic"/> for UPDATE_ITEM.</summary>
public readonly record struct PlaylistOp(
    PlaylistOpKind Kind,
    int FromIndex = 0, int Length = 0, int ToIndex = 0,
    bool AddFirst = false, bool AddLast = false,
    IReadOnlyList<PlaylistMember>? Items = null,
    bool ItemsAsKey = false,
    PlaylistMoveAnchor Anchor = PlaylistMoveAnchor.None,
    string AnchorItemId = "", string AnchorUri = "",
    PlaylistListPatch? Patch = null,
    bool? ItemPublic = null);

public static partial class Spotify
{
    /// <summary>Request bodies for every library write, and the rootlist op math. CORE (file header).</summary>
    public static class Encode
    {
        public const string StartGroupPrefix = "spotify:start-group:";
        public const string EndGroupPrefix = "spotify:end-group:";

        /// <summary>A <see cref="RootlistEntry.Kind"/> for a wire item that is neither a playlist nor a marker (the
        /// decoder skipped it but it still occupies an index the server addresses). No rule matches it; every index
        /// counts it.</summary>
        public const int OtherKind = 3;

        /// <summary>The wire uri Liked Songs pins as (<c>PinSyncRules.LikedWireUri</c>).</summary>
        public const string LikedPinUri = PinSyncRules.LikedWireUri;

        // ── 2. client-minted ids ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A rootlist folder id — 8 random bytes as 16 lowercase hex characters, the shape desktop mints for the
        /// <c>spotify:start-group:{id}:{name}</c> / <c>spotify:end-group:{id}</c> pair (0.2.9 <c>SpotifyIds.NewGroupId</c>).
        /// The server keeps whatever a client wrote, and it is the folder's identity for ever after (D10).</summary>
        public static string NewGroupId()
        {
            Span<byte> bytes = stackalloc byte[8];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexStringLower(bytes);
        }

        /// <summary>The per-request dedup nonce: the service will not apply one nonce twice. Positive, as desktop's are.</summary>
        public static long NewNonce() => Random.Shared.NextInt64(1, int.MaxValue);

        // ── 3. the /changes envelope ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>A playlist edit — POST <c>/playlist/v2/playlist/{id}/changes</c>. Byte-exact against a046, a143,
        /// a148, a154, a498, b063.</summary>
        public static byte[] PlaylistChanges(ReadOnlySpan<byte> baseRevision, IReadOnlyList<PlaylistOp> ops, string username,
                                             long nowMs, long nonce)
            => Changes(baseRevision, ops, username, nowMs, nonce, rootlist: false);

        /// <summary>The rootlist flavour — POST <c>/playlist/v2/user/{u}/rootlist/changes</c>: the identical envelope plus
        /// <c>public=true</c> on every PLAYLIST row an ADD carries (never on a folder marker). Byte-exact against a042,
        /// a164, a281, b037, b049, b128.</summary>
        public static byte[] RootlistChanges(ReadOnlySpan<byte> baseRevision, IReadOnlyList<PlaylistOp> ops, string username,
                                             long nowMs, long nonce)
            => Changes(baseRevision, ops, username, nowMs, nonce, rootlist: true);

        /// <summary>The CREATE body (golden a031): a <c>/changes</c> envelope for the CLIENT-MINTED playlist id, based on the
        /// 8-byte <see cref="CreateBase"/> and carrying exactly one op — UPDATE_LIST with the name. The reply carries
        /// revision bookkeeping only, which is why the row is seeded optimistically.</summary>
        public static byte[] CreateChanges(string name, string username, long nowMs, long nonce)
            => Changes(CreateBase, [new PlaylistOp(PlaylistOpKind.UpdateList, Patch: new PlaylistListPatch(Name: name))],
                       username, nowMs, nonce, rootlist: false);

        /// <summary>The 8 bytes a create sends as its base revision: four zeroes and ASCII "root". Never stored.</summary>
        public static ReadOnlySpan<byte> CreateBase => [0, 0, 0, 0, (byte)'r', (byte)'o', (byte)'o', (byte)'t'];

        static byte[] Changes(ReadOnlySpan<byte> baseRevision, IReadOnlyList<PlaylistOp> ops, string username, long nowMs,
                              long nonce, bool rootlist)
        {
            // Both result flags plus ONE nonce: want_sync_result makes the accepted edit authoritative on the response
            // path; the nonce stops a replayed request being applied twice.
            var changes = new Pl.ListChanges { WantResultingRevisions = true, WantSyncResult = true };
            var delta = new Pl.Delta();
            // user + timestamp and NOTHING else (no admin/undo/merge), and never the base inside the delta.
            if (!string.IsNullOrEmpty(username) || nowMs > 0)
                delta.Info = new Pl.ChangeInfo { User = username ?? "", Timestamp = nowMs };
            if (!baseRevision.IsEmpty) changes.BaseRevision = ByteString.CopyFrom(baseRevision);
            for (int i = 0; i < ops.Count; i++) delta.Ops.Add(WireOp(ops[i], rootlist));
            changes.Deltas.Add(delta);
            changes.Nonces.Add(nonce);
            return changes.ToByteArray();
        }

        static Pl.Op WireOp(in PlaylistOp op, bool rootlist) => op.Kind switch
        {
            PlaylistOpKind.Add => new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = Add(in op, rootlist) },
            PlaylistOpKind.Remove => new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = Rem(in op) },
            PlaylistOpKind.Move => new Pl.Op { Kind = Pl.Op.Types.Kind.Mov, Mov = Mov(in op) },
            PlaylistOpKind.UpdateList => new Pl.Op
            {
                Kind = Pl.Op.Types.Kind.UpdateListAttributes,
                UpdateListAttributes = op.Patch is { } patch ? new Pl.UpdateListAttributes { NewAttributes = Partial(patch) } : null,
            },
            PlaylistOpKind.UpdateItem => new Pl.Op
            {
                Kind = Pl.Op.Types.Kind.UpdateItemAttributes,
                UpdateItemAttributes = new Pl.UpdateItemAttributes
                {
                    Index = op.FromIndex,
                    NewAttributes = new Pl.ItemAttributesPartialState { Values = new Pl.ItemAttributes { Public = op.ItemPublic ?? false } },
                },
            },
            _ => new Pl.Op { Kind = Pl.Op.Types.Kind.Unknown },
        };

        // An ADD carries the client-minted item_id (which the server KEEPS) and the add timestamp; public=true only on a
        // rootlist PLAYLIST row. added_by is never sent (the server derives it). from_index only when the op is not an
        // end-insert, add_first/add_last only when true.
        static Pl.Add Add(in PlaylistOp op, bool rootlist)
        {
            var add = new Pl.Add();
            if (op.AddFirst) add.AddFirst = true;
            else if (op.AddLast) add.AddLast = true;
            else add.FromIndex = op.FromIndex;
            if (op.Items is { } items)
                for (int i = 0; i < items.Count; i++)
                {
                    var m = items[i];
                    var item = new Pl.Item { Uri = m.Uri };
                    Pl.ItemAttributes? attributes = null;
                    if (m.AddedAtMs > 0) (attributes ??= new Pl.ItemAttributes()).Timestamp = m.AddedAtMs;
                    if (rootlist && !IsGroupMarker(m.Uri)) (attributes ??= new Pl.ItemAttributes()).Public = true;
                    if (!string.IsNullOrEmpty(m.ItemId))
                        (attributes ??= new Pl.ItemAttributes()).ItemId = ByteString.CopyFrom(Convert.FromHexString(m.ItemId));
                    if (attributes is not null) item.Attributes = attributes;
                    add.Items.Add(item);
                }
            return add;
        }

        // Keyed REM (items_as_key): one Item{uri, attrs{item_id}} per row, no index. Index REM (the rootlist): from +
        // length, and a bare Item{uri} per named row when the op names any (a281 names its playlist; a rename names none).
        static Pl.Rem Rem(in PlaylistOp op)
        {
            var rem = op.ItemsAsKey ? new Pl.Rem { ItemsAsKey = true } : new Pl.Rem { FromIndex = op.FromIndex, Length = op.Length };
            if (op.Items is { Count: > 0 } items)
                for (int i = 0; i < items.Count; i++) rem.Items.Add(KeyItem(items[i].Uri, items[i].ItemId));
            return rem;
        }

        // Keyed MOV (the playlist reorder): the rows by (uri, item_id) plus ONE anchor, no index fields. Positional MOV is
        // the rootlist shape.
        static Pl.Mov Mov(in PlaylistOp op)
        {
            if (!op.ItemsAsKey) return new Pl.Mov { FromIndex = op.FromIndex, Length = op.Length, ToIndex = op.ToIndex };
            var mov = new Pl.Mov();
            if (op.Items is { } items)
                for (int i = 0; i < items.Count; i++) mov.Items.Add(KeyItem(items[i].Uri, items[i].ItemId));
            switch (op.Anchor)
            {
                case PlaylistMoveAnchor.First: mov.AddFirst = true; break;
                case PlaylistMoveAnchor.Last: mov.AddLast = true; break;
                case PlaylistMoveAnchor.AfterItem: mov.AddAfterItem = KeyItem(op.AnchorUri, op.AnchorItemId); break;
                default: throw new InvalidOperationException("a keyed MOV needs an anchor");
            }
            return mov;
        }

        static Pl.Item KeyItem(string uri, string itemIdHex) => new()
        {
            Uri = uri,
            Attributes = string.IsNullOrEmpty(itemIdHex) ? null
                : new Pl.ItemAttributes { ItemId = ByteString.CopyFrom(Convert.FromHexString(itemIdHex)) },
        };

        static Pl.ListAttributesPartialState Partial(PlaylistListPatch patch)
        {
            var partial = new Pl.ListAttributesPartialState();
            if (patch.Name is not null || patch.Description is not null || patch.Collaborative is not null
                || patch.DeletedByOwner is not null)
            {
                var values = new Pl.ListAttributes();
                if (patch.Name is not null) values.Name = patch.Name;
                if (patch.Description is not null) values.Description = patch.Description;
                if (patch.Collaborative is { } collaborative) values.Collaborative = collaborative;
                if (patch.DeletedByOwner is { } deleted) values.DeletedByOwner = deleted;
                partial.Values = values;
            }
            if (patch.ClearPicture) partial.NoValue.Add(Pl.ListAttributeKind.ListPicture);
            if (patch.ClearName) partial.NoValue.Add(Pl.ListAttributeKind.ListName);
            if (patch.ClearDescription) partial.NoValue.Add(Pl.ListAttributeKind.ListDescription);
            return partial;
        }

        static bool IsGroupMarker(string uri)
            => uri.StartsWith(StartGroupPrefix, StringComparison.Ordinal) || uri.StartsWith(EndGroupPrefix, StringComparison.Ordinal);

        // ── 4. revisions ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Longest revision this file handles (a head is 24 bytes).</summary>
        public const int MaxRevisionBytes = 64;

        /// <summary>The wire spelling <c>{counter},{hex}</c> (what <c>Edges.RootlistRevision</c> holds) back to its raw
        /// bytes — the 4-byte big-endian counter and the hash. Returns the byte count, or 0 for anything malformed. PURE.</summary>
        public static int RevisionBytes(ReadOnlySpan<char> text, Span<byte> into)
        {
            int comma = text.IndexOf(',');
            if (comma <= 0 || into.Length < 4) return 0;
            if (!uint.TryParse(text[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out uint counter)) return 0;
            var hex = text[(comma + 1)..];
            if (hex.Length == 0 || (hex.Length & 1) != 0 || 4 + hex.Length / 2 > into.Length) return 0;
            BinaryPrimitives.WriteUInt32BigEndian(into, counter);
            for (int i = 0; i < hex.Length / 2; i++)
            {
                int hi = Nibble(hex[2 * i]), lo = Nibble(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return 0;
                into[4 + i] = (byte)((hi << 4) | lo);
            }
            return 4 + hex.Length / 2;

            static int Nibble(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10
                                       : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
        }

        /// <summary>The head a <c>/changes</c> reply (or a list read) leaves us on: the top-level <c>revision</c> (1) when
        /// present, else the FIRST <c>resulting_revisions</c> entry (8). Raw bytes into <paramref name="into"/>; 0 when the
        /// body carries neither. PURE, no message allocation.</summary>
        public static int ResultingRevision(ReadOnlySpan<byte> selectedListContent, Span<byte> into)
        {
            ReadOnlySpan<byte> top = default, resulting = default;
            var r = new Decode.ProtoReader(selectedListContent);
            while (r.Next())
            {
                if (r.Field == 1 && r.Wire == 2) top = r.Bytes();
                else if (r.Field == 8 && r.Wire == 2 && resulting.IsEmpty) resulting = r.Bytes();
                else r.Skip();
            }
            var pick = !top.IsEmpty ? top : resulting;
            if (pick.IsEmpty || pick.Length > into.Length) return 0;
            pick.CopyTo(into);
            return pick.Length;
        }

        // ── 5. folder markers ────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A folder's start marker. The name is url-encoded with SPACE AS <c>+</c> — desktop's exact encoding
        /// (a164 "New+Folder", b037 "named+folder+update").</summary>
        public static string StartGroupUri(string groupId, string name)
            => StartGroupPrefix + groupId + ":" + Uri.EscapeDataString(name).Replace("%20", "+", StringComparison.Ordinal);

        public static string EndGroupUri(string groupId) => EndGroupPrefix + groupId;

        /// <summary>The group id a marker row addresses: the segment after either prefix (up to the name), or the uri
        /// itself — the projection-derived stream (<c>RootlistMarkerStream</c>) stores a folder's BARE id there.</summary>
        public static string GroupIdOf(string uri)
        {
            string prefix = uri.StartsWith(StartGroupPrefix, StringComparison.Ordinal) ? StartGroupPrefix
                          : uri.StartsWith(EndGroupPrefix, StringComparison.Ordinal) ? EndGroupPrefix : "";
            if (prefix.Length == 0) return uri;
            int colon = uri.IndexOf(':', prefix.Length);
            return colon < 0 ? uri[prefix.Length..] : uri[prefix.Length..colon];
        }

        /// <summary>A start marker's folder name: <c>+</c> is a space FIRST, then percent-unescaped — the decoder's own
        /// rule (<c>Decode.FolderName</c>). Empty for anything that is not a start marker with a name.</summary>
        public static string FolderNameOf(string startGroupUri)
        {
            if (!startGroupUri.StartsWith(StartGroupPrefix, StringComparison.Ordinal)) return "";
            int colon = startGroupUri.IndexOf(':', StartGroupPrefix.Length);
            if (colon < 0 || colon + 1 >= startGroupUri.Length) return "";
            string raw = startGroupUri[(colon + 1)..].Replace('+', ' ');
            try { return Uri.UnescapeDataString(raw); }
            catch (UriFormatException) { return raw; }
        }

        // ── 6. the marker stream ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The stream a list of wire uris stands for, with depths and folder names derived (0.2.9
        /// <c>RootlistTreeBuilder.EntriesFromUris</c>). <paramref name="stampsMs"/> is parallel, or null.</summary>
        public static List<RootlistEntry> EntriesFromUris(IReadOnlyList<string> uris, IReadOnlyList<long>? stampsMs = null)
        {
            var list = new List<RootlistEntry>(uris.Count);
            for (int i = 0; i < uris.Count; i++)
                list.Add(EntryOf(uris[i], stampsMs is not null && i < stampsMs.Count ? stampsMs[i] : 0));
            Renumber(list);
            return list;
        }

        static RootlistEntry EntryOf(string uri, long stampMs)
        {
            if (uri.StartsWith(StartGroupPrefix, StringComparison.Ordinal))
                return new RootlistEntry(0, 1, uri, FolderNameOf(uri), 0, stampMs);
            if (uri.StartsWith(EndGroupPrefix, StringComparison.Ordinal))
                return new RootlistEntry(0, 2, uri, null, 0, stampMs);
            return new RootlistEntry(0, EntityUri.KindOf(uri.AsSpan()) == EntityKind.Playlist ? 0 : OtherKind, uri, null, 0, stampMs);
        }

        /// <summary>Positions are the index; a start marker sits at its folder's own depth and raises it, an end marker
        /// lowers it first — the decoder's rule, so a locally applied stream reads like a decoded one.</summary>
        static void Renumber(List<RootlistEntry> list)
        {
            int depth = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                int at;
                if (e.Kind == 1) { at = depth; depth++; }
                else if (e.Kind == 2) { depth = Math.Max(0, depth - 1); at = depth; }
                else at = depth;
                list[i] = e with { Position = i, Depth = at };
            }
        }

        public static int FindPlaylistIndex(IReadOnlyList<RootlistEntry> entries, string uri)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Kind == 0 && string.Equals(entries[i].Uri, uri, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>Index of a folder's start marker, or -1.</summary>
        public static int FindFolderStart(IReadOnlyList<RootlistEntry> entries, string groupId)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Kind == 1 && string.Equals(GroupIdOf(entries[i].Uri), groupId, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>Where a new playlist or folder goes: index 0 at the top level, or right AFTER the parent folder's start
        /// marker (its first child). -1 when the parent folder is gone.</summary>
        public static int PlacementIndex(IReadOnlyList<RootlistEntry> entries, string? parentFolderId)
        {
            if (string.IsNullOrEmpty(parentFolderId)) return 0;
            int start = FindFolderStart(entries, parentFolderId);
            return start < 0 ? -1 : start + 1;
        }

        // ── 7. the rootlist op builders ──────────────────────────────────────────────────────────────────────────────

        /// <summary>One move as ONE positional MOV, with the reason when it cannot be built. The index math is
        /// <c>RootlistOps.CheckMove</c>'s (Shell/Sidebar.cs) exactly — resolve both ranges, then the destination for the
        /// placement, in the same guard order — and the destination is expressed against the PRE-removal stream, which is
        /// how the server applies a MOV.</summary>
        public static bool TryBuildMove(IReadOnlyList<RootlistEntry> entries, RootlistItemRef source, RootlistItemRef target,
                                        RootlistDropPlacement placement, out PlaylistOp op, out RootlistMoveCheck reason)
        {
            op = default;
            if (!TryRange(entries, source, out int from, out int end) || !TryRange(entries, target, out int targetFrom, out int targetEnd))
            {
                reason = RootlistMoveCheck.Missing;
                return false;
            }
            if (from == targetFrom) { reason = RootlistMoveCheck.SameItem; return false; }
            int to = placement switch
            {
                RootlistDropPlacement.Before => targetFrom,
                RootlistDropPlacement.After => targetEnd,
                RootlistDropPlacement.Inside when target.IsFolder => Math.Max(targetFrom + 1, targetEnd - 1),
                _ => -1,
            };
            if (to < 0) { reason = RootlistMoveCheck.Invalid; return false; }
            if (to == from || to == end) { reason = RootlistMoveCheck.NoOp; return false; }
            if (to > from && to < end) { reason = RootlistMoveCheck.Cycle; return false; }
            op = new PlaylistOp(PlaylistOpKind.Move, FromIndex: from, Length: end - from, ToIndex: to);
            reason = RootlistMoveCheck.Ok;
            return true;
        }

        /// <summary>A whole ORDERED batch as ONE delta's ops (<c>RootlistOps.CheckMoves</c>'s writer twin): each move is
        /// built against the stream the previous ones left, a self-pair gathers, a per-move no-op is skipped, any Cycle /
        /// Missing / Invalid refuses the batch, and a batch that nets to the input stream is a NoOp.</summary>
        public static bool TryBuildMoves(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<RootlistMove> moves,
                                         out List<PlaylistOp> ops, out RootlistMoveCheck reason)
        {
            ops = new List<PlaylistOp>(moves.Count);
            if (moves.Count == 0) { reason = RootlistMoveCheck.NoOp; return false; }
            IReadOnlyList<RootlistEntry> current = entries;
            int gathered = 0;
            for (int i = 0; i < moves.Count; i++)
            {
                var move = moves[i];
                if (move.Source == move.Target) { gathered++; continue; }
                if (TryBuildMove(current, move.Source, move.Target, move.Placement, out var op, out var r))
                {
                    ops.Add(op);
                    current = ApplyLocally(current, [op]);
                    continue;
                }
                if (r is RootlistMoveCheck.NoOp or RootlistMoveCheck.SameItem) continue;
                ops.Clear();
                reason = r;
                return false;
            }
            if (gathered == moves.Count) { ops.Clear(); reason = RootlistMoveCheck.SameItem; return false; }
            if (SameStream(entries, current)) { ops.Clear(); reason = RootlistMoveCheck.NoOp; return false; }
            reason = RootlistMoveCheck.Ok;
            return true;
        }

        /// <summary>Create a folder: TWO index ADDs — the start marker at <paramref name="insertAt"/> and the end marker
        /// right after it, one create timestamp (golden a164). Created empty; items are moved in by the same delta's later
        /// ops.</summary>
        public static PlaylistOp[] CreateFolder(IReadOnlyList<RootlistEntry> entries, string groupId, string name, int insertAt,
                                                long nowMs)
        {
            int at = insertAt < 0 ? 0 : insertAt > entries.Count ? entries.Count : insertAt;
            return
            [
                new PlaylistOp(PlaylistOpKind.Add, FromIndex: at, Items: [new PlaylistMember(StartGroupUri(groupId, name), "", nowMs)]),
                new PlaylistOp(PlaylistOpKind.Add, FromIndex: at + 1, Items: [new PlaylistMember(EndGroupUri(groupId), "", nowMs)]),
            ];
        }

        /// <summary>Rename a folder: REM{from, len 1} naming NOTHING, then an ADD re-inserting the start marker at the same
        /// index with the new name and — load-bearing — the marker's ORIGINAL create timestamp (goldens b037, b128). The
        /// end marker is never touched, so the children stay put. Null when the folder is gone; <paramref name="nowMs"/> is
        /// the last-resort stamp for a marker that arrived without one.</summary>
        public static PlaylistOp[]? RenameFolder(IReadOnlyList<RootlistEntry> entries, string groupId, string name, long nowMs)
        {
            int start = FindFolderStart(entries, groupId);
            if (start < 0) return null;
            long stamp = entries[start].AddedAtMs > 0 ? entries[start].AddedAtMs : nowMs;
            return
            [
                new PlaylistOp(PlaylistOpKind.Remove, FromIndex: start, Length: 1),
                new PlaylistOp(PlaylistOpKind.Add, FromIndex: start, Items: [new PlaylistMember(StartGroupUri(groupId, name), "", stamp)]),
            ];
        }

        /// <summary>Delete a folder: the END marker first, then the START (removing the start first would shift the end by
        /// one). The children stay and move up a level. Reference-inferred (no capture). Null when the folder is gone or
        /// its markers are unbalanced — refuse rather than guess.</summary>
        public static PlaylistOp[]? DeleteFolder(IReadOnlyList<RootlistEntry> entries, string groupId)
        {
            if (!TryRange(entries, new RootlistItemRef(groupId, IsFolder: true), out int start, out int end)) return null;
            int endMarker = end - 1;
            if (endMarker <= start || entries[endMarker].Kind != 2) return null;
            return
            [
                new PlaylistOp(PlaylistOpKind.Remove, FromIndex: endMarker, Length: 1),
                new PlaylistOp(PlaylistOpKind.Remove, FromIndex: start, Length: 1),
            ];
        }

        /// <summary>Follow / create: ONE index ADD of a playlist row with its timestamp (golden a042; the rootlist builder
        /// adds <c>public=true</c>).</summary>
        public static PlaylistOp RootlistAdd(string playlistUri, int insertAt, long nowMs)
            => new(PlaylistOpKind.Add, FromIndex: Math.Max(0, insertAt), Items: [new PlaylistMember(playlistUri, "", nowMs)]);

        /// <summary>Unfollow / delete: an INDEX REM naming the bare row (golden a281). Null when it is not in the rootlist.</summary>
        public static PlaylistOp? RootlistRemove(IReadOnlyList<RootlistEntry> entries, string playlistUri)
        {
            int at = FindPlaylistIndex(entries, playlistUri);
            return at < 0 ? null : new PlaylistOp(PlaylistOpKind.Remove, FromIndex: at, Length: 1, Items: [new PlaylistMember(playlistUri)]);
        }

        /// <summary>Append rows to a playlist: ONE ADD add_last, every item with its client-minted id and the add
        /// timestamp (golden a046).</summary>
        public static PlaylistOp AppendTracks(IReadOnlyList<PlaylistMember> items)
            => new(PlaylistOpKind.Add, AddLast: true, Items: items);

        /// <summary>Apply positional rootlist ops to the stream LOCALLY — the rows the server will hold, because every
        /// rootlist reply is bookkeeping only. Ops apply in order, each against the state the previous left; a MOV's
        /// destination is against the pre-removal stream. Keyed ops are not rootlist shapes and throw
        /// <see cref="ArgumentOutOfRangeException"/>, as an out-of-range index does.</summary>
        public static List<RootlistEntry> ApplyLocally(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<PlaylistOp> ops)
        {
            var list = new List<RootlistEntry>(entries.Count + ops.Count);
            for (int i = 0; i < entries.Count; i++) list.Add(entries[i]);
            for (int o = 0; o < ops.Count; o++)
            {
                var op = ops[o];
                switch (op.Kind)
                {
                    case PlaylistOpKind.Add when op.Items is { Count: > 0 } items:
                        {
                            int at = op.AddLast ? list.Count : op.AddFirst ? 0 : op.FromIndex;
                            if (at < 0 || at > list.Count) throw new ArgumentOutOfRangeException(nameof(ops), "rootlist ADD index out of range");
                            for (int i = 0; i < items.Count; i++) list.Insert(at + i, EntryOf(items[i].Uri, items[i].AddedAtMs));
                            break;
                        }
                    case PlaylistOpKind.Remove when !op.ItemsAsKey:
                        if (op.FromIndex < 0 || op.Length <= 0 || op.FromIndex + op.Length > list.Count)
                            throw new ArgumentOutOfRangeException(nameof(ops), "rootlist REM range out of range");
                        list.RemoveRange(op.FromIndex, op.Length);
                        break;
                    case PlaylistOpKind.Move when !op.ItemsAsKey:
                        {
                            if (op.FromIndex < 0 || op.Length < 0 || op.FromIndex + op.Length > list.Count)
                                throw new ArgumentOutOfRangeException(nameof(ops), "rootlist MOV source out of range");
                            if (op.ToIndex < 0 || op.ToIndex > list.Count)
                                throw new ArgumentOutOfRangeException(nameof(ops), "rootlist MOV destination out of range");
                            var moved = list.GetRange(op.FromIndex, op.Length);
                            list.RemoveRange(op.FromIndex, op.Length);
                            int dest = op.ToIndex > op.FromIndex ? op.ToIndex - op.Length : op.ToIndex;
                            if (dest < 0) throw new ArgumentOutOfRangeException(nameof(ops), "rootlist MOV destination inside the moved range");
                            list.InsertRange(dest, moved);
                            break;
                        }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(ops), "only positional ADD/REM/MOV rootlist ops apply locally");
                }
            }
            Renumber(list);
            return list;
        }

        /// <summary>A playlist row or a balanced folder range (start through its nesting-aware end, exclusive).</summary>
        static bool TryRange(IReadOnlyList<RootlistEntry> entries, RootlistItemRef item, out int start, out int end)
        {
            start = -1; end = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                bool match = item.IsFolder
                    ? entry.Kind == 1 && string.Equals(GroupIdOf(entry.Uri), item.Key, StringComparison.Ordinal)
                    : entry.Kind == 0 && string.Equals(entry.Uri, item.Key, StringComparison.Ordinal);
                if (!match) continue;
                start = i;
                if (!item.IsFolder) { end = i + 1; return true; }
                int nesting = 0;
                for (int j = i; j < entries.Count; j++)
                {
                    if (entries[j].Kind == 1) nesting++;
                    else if (entries[j].Kind == 2 && --nesting == 0) { end = j + 1; return true; }
                }
                end = entries.Count;                     // a malformed missing end: the intact remaining subtree
                return true;
            }
            return false;
        }

        static bool SameStream(IReadOnlyList<RootlistEntry> a, IReadOnlyList<RootlistEntry> b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Kind != b[i].Kind || !string.Equals(a[i].Uri, b[i].Uri, StringComparison.Ordinal)) return false;
            return true;
        }

        // ── 8. the live rootlist edge ↔ the stream (UI thread) ────────────────────────────────────────────────────────

        /// <summary>The account's rootlist edge as the marker stream a write indexes into. A playlist row is its uri; a
        /// start marker is the full <c>spotify:start-group:&lt;id&gt;:&lt;name&gt;</c> with its name and ORIGINAL create
        /// stamp (a rename re-sends it); an end marker is <c>spotify:end-group:&lt;id&gt;</c>. A wire POSITION gap — an item
        /// the decoder skipped — is kept as an <see cref="OtherKind"/> row, because the server still counts it. UI thread
        /// (it resolves uris); allocates, like every write.</summary>
        public static void RootlistEntries(User me, List<RootlistEntry> into)
        {
            into.Clear();
            if (!me.IsValid) return;
            var rows = me.Rootlist;
            var targets = me.RootlistSlots;
            var playlists = Entities.Current.Playlists;
            for (int i = 0; i < rows.Length; i++)
            {
                ref readonly var r = ref rows[i];
                while (into.Count < r.Position) into.Add(new RootlistEntry(into.Count, OtherKind, "", null, 0));
                long stamp = r.AddedAt > 0 ? r.AddedAt * 1000L : 0;
                string groupId = Entities.Strings.Resolve(r.FolderId);
                switch ((RootlistKind)r.Kind)
                {
                    case RootlistKind.FolderStart:
                        {
                            string name = Entities.Strings.Resolve(r.FolderName);
                            into.Add(new RootlistEntry(into.Count, 1, StartGroupUri(groupId, name), name, r.Depth, stamp));
                            break;
                        }
                    case RootlistKind.FolderEnd:
                        into.Add(new RootlistEntry(into.Count, 2, EndGroupUri(groupId), null, r.Depth, stamp));
                        break;
                    default:
                        {
                            int slot = targets[i];
                            string uri = slot > Table.None && slot < playlists.Count ? playlists.Id[slot].Text : "";
                            into.Add(new RootlistEntry(into.Count, uri.Length > 0 ? 0 : OtherKind, uri, null, r.Depth, stamp));
                            break;
                        }
                }
            }
        }

        /// <summary>The inverse: land a stream as the account's rootlist edge (the optimistic half of a rootlist write).
        /// Playlist uris resolve to rows (allocating an unseen one), folder names and ids are interned and handed to
        /// <see cref="User.ReplaceRootlist"/>, which owns them (G-062). <see cref="OtherKind"/> rows are not landed — they
        /// have no row — but the positions after them keep their gap. UI thread.</summary>
        public static void LandRootlist(User me, IReadOnlyList<RootlistEntry> entries)
        {
            if (!me.IsValid) return;
            int n = 0;
            for (int i = 0; i < entries.Count; i++) if (entries[i].Kind is 0 or 1 or 2) n++;
            var targets = new int[n];
            var payload = new RootlistEdge[n];
            var open = new Stack<string>();
            var playlists = Entities.Current.Playlists;
            int k = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                ushort position = (ushort)Math.Clamp(i, 0, ushort.MaxValue);
                byte depth = (byte)Math.Clamp(e.Depth, 0, byte.MaxValue);
                int added = e.AddedAtMs > 0 ? (int)Math.Min(e.AddedAtMs / 1000, int.MaxValue) : 0;
                switch (e.Kind)
                {
                    case 0:
                        targets[k] = playlists.Slot(e.Uri.AsSpan());
                        payload[k++] = new RootlistEdge(position, depth, (byte)RootlistKind.Item, default, added);
                        break;
                    case 1:
                        {
                            string id = GroupIdOf(e.Uri);
                            string name = e.GroupName ?? FolderNameOf(e.Uri);
                            open.Push(id);
                            targets[k] = Table.None;
                            payload[k++] = new RootlistEdge(position, depth, (byte)RootlistKind.FolderStart,
                                Entities.Strings.Intern(name), added, Entities.Strings.Intern(id));
                            break;
                        }
                    case 2:
                        {
                            string id = e.Uri.Length > 0 ? GroupIdOf(e.Uri) : "";
                            string popped = open.Count > 0 ? open.Pop() : "";
                            if (id.Length == 0) id = popped;
                            targets[k] = Table.None;
                            payload[k++] = new RootlistEdge(position, depth, (byte)RootlistKind.FolderEnd, default, added,
                                Entities.Strings.Intern(id));
                            break;
                        }
                }
            }
            me.ReplaceRootlist(targets, payload);
        }

        /// <summary>The account's server pin set, as wire uris with their add instants — what <see cref="LibraryPinSync"/>
        /// converges the sidebar onto. A row kind is its row's uri; Liked is <see cref="LikedPinUri"/>; a folder is
        /// <c>spotify:folder:&lt;id&gt;</c>. A pin of <see cref="PinKind.Unknown"/> (written before the kind bit) is skipped.
        /// UI thread.</summary>
        public static void PinWires(User me, List<PinWire> into)
        {
            into.Clear();
            if (!me.IsValid) return;
            var rows = me.PinEdges;
            var targets = me.PinSlots;
            for (int i = 0; i < rows.Length; i++)
            {
                var kind = (PinKind)rows[i].Flags;
                long at = rows[i].AddedAt > 0 ? rows[i].AddedAt * 1000L : 0;
                string uri = kind switch
                {
                    PinKind.Liked => LikedPinUri,
                    PinKind.Folder => targets[i] > 0 ? EntityUri.FolderPrefix + Entities.Strings.Resolve(new StringId(targets[i])) : "",
                    _ => RowUri(User.EntityKindOf(kind), targets[i]),
                };
                if (uri.Length > 0) into.Add(new PinWire(uri, at));
            }

            static string RowUri(EntityKind kind, int slot)
            {
                var table = kind == EntityKind.Unknown ? null : Entities.TableFor(kind);
                return table is null || slot <= Table.None || slot >= table.Count ? "" : table.Id[slot].Text;
            }
        }

        /// <summary>The default name of a new playlist: "My Playlist #N" with the smallest N no existing name already uses
        /// (case-insensitive; 0.2.9 <c>PlaylistDepositTargets.NextDefaultName</c>). Bounded by the list's length.</summary>
        public static string NextPlaylistName(IReadOnlyList<string> taken, string baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Playlist";
            int limit = taken.Count + 1;
            for (int n = 1; n <= limit; n++)
            {
                string candidate = baseName + " #" + n.ToString(CultureInfo.InvariantCulture);
                bool used = false;
                for (int i = 0; i < taken.Count && !used; i++)
                    used = string.Equals(taken[i], candidate, StringComparison.OrdinalIgnoreCase);
                if (!used) return candidate;
            }
            return baseName + " #" + limit.ToString(CultureInfo.InvariantCulture);
        }
    }
}

// ── 9. the library host's decisions ──────────────────────────────────────────────────────────────────────────────────

/// <summary>What the login-time sync remembers between two looks at the session.</summary>
public struct LibrarySyncMemo
{
    public uint SessionEpoch;
    public uint ScopeGeneration;
    public long AtMs;
    public bool Any;
}

public enum LibrarySyncVerdict : byte
{
    /// <summary>Nothing to do: offline, not a Spotify account scope, or this (session epoch, scope) already synced.</summary>
    None = 0,
    /// <summary>Ask for every library relation again.</summary>
    Sync,
    /// <summary>A reconnect of the SAME scope inside <see cref="LibrarySyncRules.ReconnectWindowMs"/> of the last sync: a
    /// flapping network must not storm the collection service (0.2.9's §6.2 rate limit).</summary>
    RateLimited,
}

/// <summary>WHEN THE LIBRARY SYNCS (G-042). Once per transition into <c>Online</c> — per session epoch — and once per
/// catalog scope: the welcome switches the scope to the account's, a market change switches it again, and a reconnect
/// (a new epoch) re-asks. PURE.</summary>
public static class LibrarySyncRules
{
    public const long ReconnectWindowMs = 30_000;

    public static LibrarySyncVerdict Decide(ref LibrarySyncMemo memo, bool online, bool accountScope, uint sessionEpoch,
                                            uint scopeGeneration, long nowMs)
    {
        if (!online || !accountScope) return LibrarySyncVerdict.None;
        if (memo.Any && memo.SessionEpoch == sessionEpoch && memo.ScopeGeneration == scopeGeneration) return LibrarySyncVerdict.None;
        if (memo.Any && memo.ScopeGeneration == scopeGeneration && nowMs - memo.AtMs < ReconnectWindowMs)
        {
            memo.SessionEpoch = sessionEpoch;             // this reconnect is accounted for; the next one is judged afresh
            return LibrarySyncVerdict.RateLimited;
        }
        memo = new LibrarySyncMemo { SessionEpoch = sessionEpoch, ScopeGeneration = scopeGeneration, AtMs = nowMs, Any = true };
        return LibrarySyncVerdict.Sync;
    }
}

/// <summary>The library relations a dealer push can name.</summary>
[Flags]
public enum LibraryPush : byte
{
    None = 0,
    Rootlist = 1 << 0,
    Liked = 1 << 1,
    SavedAlbums = 1 << 2,
    FollowedArtists = 1 << 3,
    SavedShows = 1 << 4,
    Pins = 1 << 5,
}

/// <summary>WHICH RELATION A DEALER PUSH TOUCHES (G-042 "dealer pushes → deltas"). <c>hm://collection/&lt;set&gt;/&lt;user&gt;</c>
/// names a collection set — <c>collection</c> is ONE set holding liked tracks AND saved albums, so it names both —
/// and <c>hm://playlist/…/rootlist</c> (both the v2 and the legacy spelling the dealer sends as a pair) names the
/// rootlist. A delta is applied by asking the relation again (the provider's revision-gated read makes that a diff), so
/// there is no op replayer to disagree with the server. PURE, allocation-free.</summary>
public static class LibraryPushRules
{
    public static LibraryPush Classify(ReadOnlySpan<byte> topic)
    {
        ReadOnlySpan<byte> collection = "hm://collection/"u8;
        if (topic.StartsWith(collection))
        {
            var rest = topic[collection.Length..];
            int slash = rest.IndexOf((byte)'/');
            var set = slash < 0 ? rest : rest[..slash];
            if (set.SequenceEqual("collection"u8)) return LibraryPush.Liked | LibraryPush.SavedAlbums;
            if (set.SequenceEqual("artist"u8)) return LibraryPush.FollowedArtists;
            if (set.SequenceEqual("show"u8)) return LibraryPush.SavedShows;
            if (set.SequenceEqual("ylpin"u8)) return LibraryPush.Pins;
            return LibraryPush.None;
        }
        if (topic.StartsWith("hm://playlist/"u8))
        {
            var tail = topic.EndsWith("/"u8) ? topic[..^1] : topic;
            if (tail.EndsWith("/rootlist"u8)) return LibraryPush.Rootlist;
        }
        return LibraryPush.None;
    }
}

/// <summary>THE OWNED PLAYLISTS' CAPABILITIES (G-048). A rootlist row lands THIN — no capability block — and an
/// unknown block reads not-owner / not-editable, so every deposit refused "can't edit" and Rename/Delete never appeared.
/// The owner of a playlist holds every capability, so a row whose owner IS the signed-in account can be stated owned at
/// <see cref="Authority.Thin"/>: a later full read (the v2 playlist read's own block) overwrites it, and a row nobody
/// knows the owner of is left unknown, never guessed. UI thread; stages, the caller commits.</summary>
public static class LibraryCaps
{
    public const PlaylistCaps Owner = PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata
                                    | PlaylistCaps.IsOwner | PlaylistCaps.CanAdministratePermissions;

    /// <summary>Stage the owner block for every rootlist playlist the account owns whose capabilities are unknown.
    /// Returns how many rows were staged.</summary>
    public static int StageOwned(Scope scope, Staging into)
    {
        int me = scope.MeSlot;
        if (me <= Table.None) return 0;
        var t = scope.Playlists;
        var targets = scope.Edges.Rootlist.Targets(me);
        int staged = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            int slot = targets[i];
            if (slot <= Table.None || slot >= t.Count) continue;
            if (t.Owner[slot] != me || !t.Knows(slot, (uint)PlaylistFields.Identity)
                || t.Knows(slot, (uint)PlaylistFields.Capabilities)) continue;
            ref var row = ref into.Playlists.RowFor(new StagedId(t.Id[slot]), Authority.Thin, (uint)PlaylistFields.Capabilities);
            row.Caps = (byte)Owner;
            staged++;
        }
        return staged;
    }
}

/// <summary>THE PRE-SAVE DROPS (G-089): which saved albums the release-drop scheduler holds a toast for, and what the
/// toast says. A drop is a SAVED row that is a prerelease (the flag or the <c>spotify:prerelease:</c> id); its instant is
/// the parsed release date, else the prerelease window's end; it is upcoming while that instant is ahead. The cover url
/// is the host's to resolve (a UI-layer rule), so it is left null here. UI thread (reads the tables).</summary>
public static class LibraryDrops
{
    /// <summary>Every saved prerelease of the account, as the scheduler's links. <paramref name="coverUrl"/> resolves a
    /// cover id to a url (the host passes the UI's resolver); null leaves the links cover-less.</summary>
    public static void Resolve(Scope scope, long nowUnixSeconds, List<Notify.DropLink> into, Func<StringId, string?>? coverUrl = null)
    {
        into.Clear();
        int me = scope.MeSlot;
        if (me <= Table.None) return;
        var saved = scope.Edges.SavedAlbums.Targets(me);
        for (int i = 0; i < saved.Length; i++)
        {
            var album = new Album(saved[i]);
            if (!album.IsValid || !album.IsPreRelease) continue;
            var link = LinkOf(album, nowUnixSeconds);
            into.Add(coverUrl is null ? link : link with { CoverUrl = coverUrl(album.ImageId) });
        }
    }

    /// <summary>One prerelease row as a drop link. PreRelease is the row's own uri when it IS the prerelease id, else its
    /// resolved kind-138 pairing; Play is the album itself only when the row is the album (a prerelease id names no
    /// playable album, and the toast falls back to the prerelease).</summary>
    public static Notify.DropLink LinkOf(Album album, long nowUnixSeconds)
    {
        bool rowIsPrerelease = album.Id.IsPrerelease;
        EntityUri preRelease = rowIsPrerelease ? album.Uri
            : !album.PreReleaseUriId.IsEmpty ? new EntityUri(album.PreReleaseId) : album.Uri;
        EntityUri play = rowIsPrerelease ? default : album.Uri;
        int instant = album.ReleaseAt > 0 ? album.ReleaseAt : album.PreReleaseEnd;
        DateTimeOffset? releaseAt = instant > 0 ? DateTimeOffset.FromUnixTimeSeconds(instant) : null;
        var artists = album.ArtistSlots;
        string artist = artists.Length > 0 && artists[0] > Table.None ? new Artist(artists[0]).Name : "";
        return new Notify.DropLink(preRelease, play, album.Title, artist, null, releaseAt, instant > nowUnixSeconds);
    }
}

/// <summary>One server pin: the ylpin item's uri and when it was added (unix ms, 0 = not stated).</summary>
public readonly record struct PinWire(string Uri, long AddedAtMs);

/// <summary>THE PIN BRIDGE (G-049): the two-way convergence between Spotify's ylpin set (the <c>Pins</c> edge, read as
/// <see cref="PinWire"/>s) and the sidebar's <see cref="SidebarPinStore"/> — the port of 0.2.9's
/// <c>SidebarPinSync</c>, with the decision the register recommended: the STORE stays the ordered, persisted authority
/// and the offline cache, and this bridge only converges membership.
/// <list type="bullet">
/// <item>remote → local (<see cref="ApplyServer"/>): server pins the sidebar can represent are appended oldest-first;
/// missing SYNCABLE local pins are removed only once the server set has converged AND the one-time migration latch is
/// set, and never while a write for that pin is still in flight. Never raises a write.</item>
/// <item>local → remote (the store's <see cref="SidebarPinStore.OnLocalPinChanged"/>): a syncable pin/unpin becomes one
/// collection write; an app route or a <c>wavee:</c> playlist stays local.</item>
/// <item>the upgrade (§1.7): the first converged walk PUSHES every syncable local pin the server lacks instead of
/// sweeping it — deleting a user's pins because the server never had them would be data loss — then latches
/// <see cref="Platform.Keys.PinsMigratedToServer"/>.</item>
/// </list>
/// A shape this client cannot represent (a track, an episode, a prerelease, <c>your-episodes</c>, an unknown scheme)
/// never becomes a local pin and never causes a write, so a foreign client's pin survives on the server untouched.
/// UI thread only — the store is.</summary>
public sealed class LibraryPinSync : IDisposable
{
    readonly SidebarPinStore _pins;
    readonly IAppSettings _settings;
    readonly Action<string, bool> _write;
    readonly Func<string, bool> _hasPending;
    readonly Func<string, string>? _routeTitle;
    readonly Action<SidebarPin, bool> _onLocal;
    bool _migrated;

    /// <param name="write">(wire uri, pinned) — one collection write.</param>
    /// <param name="hasPending">Is a write for this wire uri still in flight? A pending pin is never swept.</param>
    /// <param name="routeTitle">A route pin's display title (Liked Songs has no entity to name it).</param>
    public LibraryPinSync(SidebarPinStore pins, IAppSettings settings, Action<string, bool> write, Func<string, bool> hasPending,
                          Func<string, string>? routeTitle = null)
    {
        _pins = pins;
        _settings = settings;
        _write = write;
        _hasPending = hasPending;
        _routeTitle = routeTitle;
        _migrated = settings.Get(Platform.Keys.PinsMigratedToServer);
        _onLocal = OnLocalPinChanged;
        _pins.OnLocalPinChanged = _onLocal;
    }

    /// <summary>Has the one-time local → server migration run?</summary>
    public bool Migrated => _migrated;

    /// <summary>Converge the store onto the server set. <paramref name="converged"/>: the set has been walked whole at
    /// least once this scope (the edge is Complete) — an empty or partial mirror must never read as "unpin everything".</summary>
    public void ApplyServer(IReadOnlyList<PinWire> server, bool converged)
    {
        // Oldest first → appended in pin order. A stable sort by index keeps equal instants in wire order.
        var order = new int[server.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = server[a].AddedAtMs.CompareTo(server[b].AddedAtMs);
            return c != 0 ? c : a.CompareTo(b);
        });

        var mapped = new List<SidebarPin>(server.Count);
        for (int i = 0; i < order.Length; i++)
        {
            var wire = server[order[i]];
            if (EntityUri.IsPrerelease(wire.Uri.AsSpan())) continue;          // an album row, but never a pin (file doc)
            if (PinSyncRules.TryPinId(wire.Uri) is not { } id) continue;
            var kind = SidebarPinId.KindOf(id);
            // A route pin (Liked Songs) has no entity to fill its name, so it would render blank and disabled; every other
            // kind leaves "" for the projection's Touch.
            string name = kind == SidebarEntryKind.AppRoute ? _routeTitle?.Invoke(id) ?? "" : "";
            mapped.Add(new SidebarPin(id, kind, SidebarPinId.UriOf(id), name, wire.AddedAtMs));
        }

        bool removeMissing = converged && _migrated;
        _pins.ApplyRemote(mapped, IsSweepable, removeMissing);
        if (!_migrated && converged) Migrate(mapped);
    }

    bool IsSweepable(string pinId)
        => PinSyncRules.TryWireUri(pinId, "") is { } uri && !_hasPending(uri);

    void OnLocalPinChanged(SidebarPin pin, bool pinned)
    {
        if (PinSyncRules.TryWireUri(pin.Id, "") is not { } uri) return;   // not syncable — a silent local pin
        _write(uri, pinned);
    }

    void Migrate(IReadOnlyList<SidebarPin> server)
    {
        var onServer = new HashSet<string>(server.Count, StringComparer.Ordinal);
        for (int i = 0; i < server.Count; i++) onServer.Add(server[i].Id);
        for (int i = 0; i < _pins.Count; i++)
        {
            var pin = _pins[i];
            if (onServer.Contains(pin.Id)) continue;
            if (PinSyncRules.TryWireUri(pin.Id, "") is not { } uri) continue;
            _write(uri, true);
        }
        _migrated = true;
        _settings.Set(Platform.Keys.PinsMigratedToServer, true);
    }

    public void Dispose()
    {
        if (ReferenceEquals(_pins.OnLocalPinChanged, _onLocal)) _pins.OnLocalPinChanged = null;
    }
}
