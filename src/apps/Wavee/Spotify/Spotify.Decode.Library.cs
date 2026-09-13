// ── Spotify/Spotify.Decode.Library.cs ───────────────────────────────────────────────────────────────────────────────
// the rootlist marker stream and the gander notification feed (gap batch B1, G-045, G-046, G-047)
//
// Role: CORE
// Owner: E
// Wave: gap batch B1
// Budget: 440 lines
// Spec: gap register G-045/G-046/G-047, decision D10 — a named partial of Spotify.Decode.cs
//
// THE ROOTLIST (G-046, G-047). `/playlist/v2/user/<u>/rootlist` answers a playlist4 `SelectedListContent` whose items
// are playlists AND folder markers, in one flat stream:
//
//     spotify:start-group:<group id>:<name>       the folder opens: its name is `+`-for-space, then percent-escaped
//     spotify:playlist:<id>                       …its members…
//     spotify:end-group:<group id>                the folder closes
//
// The 0.3 decoder had none of it: no rootlist decode at all, and the pathfinder `libraryV3` fold staged every row as an
// Item — a folder became a USER row named "New Folder" and its id was a position the sidebar invented. This file stages
// the stream verbatim through `Staging.RootlistRows` (Edges.cs §7): markers with their depth, the folder NAME on the
// start marker and the bare GROUP ID on both (D10: `RootlistEdge.FolderId`, the `folder:<hex>` ids the sidebar mints
// from it), the revision a write needs as its base.
//
// GANDER (G-045, "gander first"). `/gander/v2/GetNotifications` is the one feed here that is NOT staged: a notification
// is not an entity (Platform/Notify.cs — "there is no Entities/Notification.* pair"), so its decode produces the
// `Notification` values `Notify.Rebuild` publishes. That makes it a string-producing fold and the one exception to this
// file's no-allocation rule; it runs once per feed refresh, never per frame.

using System.Text.Json;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Decode
    {
        /// <summary>A folder nests at most this deep before the decoder stops tracking it (the desktop client stops at
        /// far less); a deeper stream still lands, flattened at this depth.</summary>
        public const int MaxFolderDepth = 32;

        // ── the rootlist ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A rootlist <c>SelectedListContent</c> → the account's marker stream (<see cref="StagedRootlist"/>).
        /// Positions are the WIRE indexes (<c>contents.pos</c> + the item's index), markers included, because a rootlist
        /// write addresses the stream by exactly that index. A marker's depth is the folder's own (the start is stamped
        /// before the depth rises, the end after it falls — 0.2.9 <c>RootlistTreeBuilder</c>); an item that is not a
        /// playlist is skipped but still counts a position.</summary>
        public static void Rootlist(ReadOnlySpan<byte> selectedListContent, ReadOnlySpan<byte> meUri, Staging s)
        {
            var parent = Identity(s, meUri);
            if (parent.IsEmpty) return;
            int index = s.Rootlists.Count;
            ref var list = ref s.Rootlists.Add();
            list.Parent = parent;
            list.Start = s.RootlistRows.Count;

            var r = new ProtoReader(selectedListContent);
            TextRef revision = default;
            int depth = 0;
            while (r.Next())
            {
                if (r.Field == 1 && r.Wire == 2) { revision = Revision(s, r.Bytes()); continue; }
                if (r.Field != 5 || r.Wire != 2) { r.Skip(); continue; }
                var contents = r.Message();                         // ListItems { pos = 1, truncated = 2, items = 3 }
                int position = 0;
                while (contents.Next())
                {
                    if (contents.Field == 1 && contents.Wire == 0) { position = contents.Int32(); continue; }
                    if (contents.Field != 3 || contents.Wire != 2) { contents.Skip(); continue; }
                    RootlistItem(contents.Message(), s, position++, ref depth);
                }
            }

            // Re-read by INDEX: the arena and the row list grew underneath the reference taken above.
            ref var closed = ref s.Rootlists[index];
            closed.Revision = revision;
            closed.Length = s.RootlistRows.Count - closed.Start;
        }

        static void RootlistItem(ProtoReader item, Staging s, int position, ref int depth)
        {
            ReadOnlySpan<byte> uri = default;
            long timestamp = 0;
            while (item.Next())
            {
                if (item.Field == 1 && item.Wire == 2) uri = item.Bytes();
                else if (item.Field == 2 && item.Wire == 2) timestamp = item.Message().Varint(2);   // ItemAttributes.timestamp
                else item.Skip();
            }
            if (uri.IsEmpty) return;

            if (StartsWith(uri, StartGroup))
            {
                var rest = uri[StartGroup.Length..];
                int colon = rest.IndexOf((byte)':');
                var id = colon < 0 ? rest : rest[..colon];
                if (id.IsEmpty) return;
                ref var row = ref s.RootlistRows.Add();
                row.Kind = RootlistKind.FolderStart;
                row.Position = WirePosition(position);
                row.Depth = (byte)Math.Min(depth, MaxFolderDepth);
                row.FolderId = s.AddText(id);
                row.Name = colon < 0 ? default : FolderName(s, rest[(colon + 1)..]);
                row.AddedAt = Instant(timestamp);
                if (depth < MaxFolderDepth) depth++;
                return;
            }
            if (StartsWith(uri, EndGroup))
            {
                var rest = uri[EndGroup.Length..];
                int colon = rest.IndexOf((byte)':');
                depth = Math.Max(0, depth - 1);
                ref var row = ref s.RootlistRows.Add();
                row.Kind = RootlistKind.FolderEnd;
                row.Position = WirePosition(position);
                row.Depth = (byte)Math.Min(depth, MaxFolderDepth);
                row.FolderId = s.AddText(colon < 0 ? rest : rest[..colon]);
                return;
            }
            if (EntityUri.KindOf(uri) != EntityKind.Playlist) return;
            ref var entry = ref s.RootlistRows.Add();
            entry.Kind = RootlistKind.Item;
            entry.Position = WirePosition(position);
            entry.Depth = (byte)Math.Min(depth, MaxFolderDepth);
            entry.Target = Identity(s, uri);
            entry.AddedAt = Instant(timestamp);
        }

        const string StartGroup = "spotify:start-group:";
        const string EndGroup = "spotify:end-group:";

        static ushort WirePosition(int wire) => (ushort)Math.Clamp(wire, 0, ushort.MaxValue);

        /// <summary>An item attribute timestamp → the edge's seconds. The playlist4 wire states milliseconds for a
        /// recents play and has been read as seconds for a membership; a value past 10^11 cannot be seconds (that is the
        /// year 5138), so it is milliseconds and is divided — the one reading that is right for both.</summary>
        internal static int Instant(long wire)
            => wire <= 0 ? 0 : AtMost(wire > 100_000_000_000L ? wire / 1000 : wire);

        /// <summary>A folder name off the marker uri: <c>+</c> is a space FIRST, then the percent escapes are undone — so
        /// <c>A%2BB+C</c> is <c>A+B C</c> and <c>caf%C3%A9+mix</c> is <c>café mix</c> (0.2.9 <c>GroupNameOf</c>, whose
        /// write side is <c>EscapeDataString(name).Replace("%20", "+")</c>). A malformed escape is kept verbatim.</summary>
        public static TextRef FolderName(Staging s, ReadOnlySpan<byte> escaped)
        {
            if (escaped.IsEmpty) return default;
            Span<byte> name = stackalloc byte[512];
            int n = 0;
            for (int i = 0; i < escaped.Length && n < name.Length; i++)
            {
                byte b = escaped[i];
                if (b == (byte)'+') { name[n++] = (byte)' '; continue; }
                if (b == (byte)'%' && i + 2 < escaped.Length && HexValue(escaped[i + 1]) is int hi and >= 0
                    && HexValue(escaped[i + 2]) is int lo and >= 0)
                {
                    name[n++] = (byte)((hi << 4) | lo);
                    i += 2;
                    continue;
                }
                name[n++] = b;
            }
            return s.AddText(name[..n]);

            static int HexValue(byte c) => c is >= (byte)'0' and <= (byte)'9' ? c - '0'
                                         : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10
                                         : c is >= (byte)'A' and <= (byte)'F' ? c - 'A' + 10 : -1;
        }

        /// <summary>A revision's 4-byte big-endian counter and its hash → <c>{counter},{hex}</c>, the spelling the
        /// playlist routes take back (<c>Spotify.Api.FormatRevision</c>). Empty for a revision too short to be one.</summary>
        public static TextRef Revision(Staging s, ReadOnlySpan<byte> revision)
        {
            if (revision.Length < 5 || revision.Length > 64) return default;
            Span<byte> text = stackalloc byte[16 + 1 + 128];
            uint counter = (uint)((revision[0] << 24) | (revision[1] << 16) | (revision[2] << 8) | revision[3]);
            if (!System.Buffers.Text.Utf8Formatter.TryFormat(counter, text, out int n)) return default;
            text[n++] = (byte)',';
            for (int i = 4; i < revision.Length; i++)
            {
                text[n++] = Nibble(revision[i] >> 4);
                text[n++] = Nibble(revision[i] & 0xF);
            }
            return s.AddText(text[..n]);
        }

        // ── libraryV3: the same stream, built from the pathfinder library page ───────────────────────────────────────

        /// <summary>`libraryV3` → the rootlist MARKER STREAM over the account's own row (Edges.cs §7): the playlists and
        /// pseudo playlists the sidebar lists, in the server's order, each with its depth and its added-at instant — and
        /// every FOLDER as a <see cref="RootlistKind.FolderStart"/> / <see cref="RootlistKind.FolderEnd"/> pair (G-046).
        ///
        /// <para>This answer is a flat list with a depth per row, not a marker stream, so the stream is BUILT here: a
        /// <c>…:folder:&lt;hex&gt;</c> row opens a folder (its name, its group id — D10, G-047); a later row at or above
        /// that folder's depth closes it first; the end of the list closes whatever is still open. A folder row was
        /// staged as a USER named after the folder before this, and its id was a position.</para></summary>
        public static void LibraryV3(ref Utf8JsonReader r, ReadOnlySpan<byte> meUri, Staging s)
        {
            if (meUri.IsEmpty) { r.Skip(); return; }
            var me = Identity(s, meUri);
            int index = s.Rootlists.Count;
            ref var list = ref s.Rootlists.Add();
            list.Parent = me;
            list.Start = s.RootlistRows.Count;

            Span<int> openDepth = stackalloc int[MaxFolderDepth];
            Span<TextRef> openId = stackalloc TextRef[MaxFolderDepth];
            int open = 0, position = 0;

            for (int d = Fields(ref r); Next(ref r, d);)
            {
                if (r.ValueTextEquals("items"u8) && EnterArray(ref r))
                {
                    for (int items = r.CurrentDepth; Element(ref r, items);)
                        Row(ref r, s, ref position, openDepth, openId, ref open);
                }
                else SkipValue(ref r);
            }
            while (open > 0) { open--; FolderEnd(s, position++, openDepth[open], openId[open]); }

            ref var closed = ref s.Rootlists[index];
            closed.Length = s.RootlistRows.Count - closed.Start;

            static void Row(ref Utf8JsonReader r, Staging s, ref int position, scoped Span<int> openDepth, scoped Span<TextRef> openId,
                            ref int open)
            {
                var node = default(Node);
                int credit = s.CreditMark, mark = s.Edges.PendingMark, rowDepth = 0;
                TextRef addedAt = default;

                for (int depth = r.CurrentDepth; Next(ref r, depth);)
                {
                    if (r.ValueTextEquals("depth"u8)) { r.Read(); rowDepth = Math.Clamp((int)Num(ref r), 0, MaxFolderDepth - 1); }
                    else if (r.ValueTextEquals("addedAt"u8)) addedAt = OneText(ref r, s, "isoString"u8);
                    else NodeProperty(ref r, s, ref node, credit);
                }
                s.TakeCredit(credit);
                if (s.Edges.Pending(mark) > 0) CloseArtists(s, in node, mark);
                if (node.Uri.IsEmpty) return;

                // A row at or above an open folder's depth is outside it: close the folders it left.
                while (open > 0 && openDepth[open - 1] >= rowDepth) { open--; FolderEnd(s, position++, openDepth[open], openId[open]); }

                int at = 0;
                if (!addedAt.IsEmpty) IsoDate(s.Utf8(addedAt), out _, out at, out _);

                // A folder's uri is never a catalog gid, so it is always the text form of the staged identity.
                var folder = node.Uri.Packed.IsEmpty ? FolderIdOf(s.Utf8(node.Uri.Text)) : default;
                if (!folder.IsEmpty)
                {
                    ref var start = ref s.RootlistRows.Add();
                    start.Kind = RootlistKind.FolderStart;
                    start.Position = WirePosition(position++);
                    start.Depth = (byte)rowDepth;
                    start.FolderId = s.AddText(folder);
                    start.Name = node.Name;
                    start.AddedAt = at;
                    if (open < openDepth.Length) { openDepth[open] = rowDepth; openId[open] = start.FolderId; open++; }
                    return;
                }

                var uri = Stage(s, in node, Authority.Thin);
                if (uri.IsEmpty) return;
                ref var item = ref s.RootlistRows.Add();
                item.Kind = RootlistKind.Item;
                item.Position = WirePosition(position++);
                item.Depth = (byte)rowDepth;
                item.Target = uri;
                item.AddedAt = at;
            }

            static void FolderEnd(Staging s, int position, int depth, TextRef id)
            {
                ref var end = ref s.RootlistRows.Add();
                end.Kind = RootlistKind.FolderEnd;
                end.Position = WirePosition(position);
                end.Depth = (byte)depth;
                end.FolderId = id;
            }

            // `spotify:user:<u>:folder:<hex>` → the hex, a slice of the uri. Empty for anything else.
            static ReadOnlySpan<byte> FolderIdOf(ReadOnlySpan<byte> uri)
            {
                const string marker = ":folder:";
                if (!StartsWith(uri, "spotify:")) return default;
                int at = uri.IndexOf(":folder:"u8);
                return at < 0 || at + marker.Length >= uri.Length ? default : uri[(at + marker.Length)..];
            }
        }

        // ── gander ───────────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A <c>/gander/v2/GetNotifications</c> answer → the social rows the notification centre publishes,
        /// appended to <paramref name="into"/>; returns how many (0.2.9 <c>SpotifyNotificationsService.Parse</c>).
        ///
        /// <para>Per item: <c>id</c> and <c>title</c> are REQUIRED (the title is a finished, server-localized sentence and
        /// the only text); <c>createdTimestamp</c> is ISO-8601 (a number or numeric string is epoch ms); <c>isNew</c>
        /// is unread only when it is the JSON <c>true</c>; <c>action.type</c> <c>NAVIGATE</c> is an in-app route and
        /// anything else a web view; the act is the first non-blank <c>multiUserImage.userImages[].userDisplayName</c>; the
        /// wire type is <c>type</c> → <c>notificationType</c> → <c>category</c>. <c>latestCursor</c> and
        /// <c>storageId</c> are not read (0.2.9 discarded both).</para></summary>
        public static int Notifications(ReadOnlySpan<byte> json, List<Notification> into)
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return 0;
            int added = 0;
            for (int root = r.CurrentDepth; Next(ref r, root);)
            {
                if (!r.ValueTextEquals("notifications"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                for (int list = r.CurrentDepth; Element(ref r, list);)
                {
                    string? id = null, title = null, actionUri = null, image = null, act = null;
                    string? type = null, notificationType = null, category = null;
                    long timestamp = 0;
                    bool unread = false, webview = true;
                    for (int item = r.CurrentDepth; Next(ref r, item);)
                    {
                        if (r.ValueTextEquals("id"u8)) { r.Read(); id = Str(ref r); }
                        else if (r.ValueTextEquals("title"u8)) { r.Read(); title = Str(ref r); }
                        else if (r.ValueTextEquals("createdTimestamp"u8)) { r.Read(); timestamp = TimestampMs(ref r); }
                        else if (r.ValueTextEquals("isNew"u8)) { r.Read(); unread = r.TokenType == JsonTokenType.True; }
                        else if (r.ValueTextEquals("action"u8))
                        {
                            bool navigate = false;
                            for (int a = Fields(ref r); Next(ref r, a);)
                            {
                                if (r.ValueTextEquals("uri"u8)) { r.Read(); actionUri = Str(ref r); }
                                else if (r.ValueTextEquals("type"u8)) { r.Read(); navigate = Says(ref r, "NAVIGATE"); }
                                else SkipValue(ref r);
                            }
                            webview = !navigate;
                        }
                        else if (r.ValueTextEquals("entityImage"u8))
                        {
                            for (int e = Fields(ref r); Next(ref r, e);)
                            {
                                if (r.ValueTextEquals("imageUrl"u8)) { r.Read(); image = Str(ref r); }
                                else SkipValue(ref r);
                            }
                        }
                        else if (r.ValueTextEquals("multiUserImage"u8))
                        {
                            for (int m = Fields(ref r); Next(ref r, m);)
                            {
                                if (!r.ValueTextEquals("userImages"u8) || !EnterArray(ref r)) { SkipValue(ref r); continue; }
                                for (int users = r.CurrentDepth; Element(ref r, users);)
                                    for (int u = r.CurrentDepth; Next(ref r, u);)
                                    {
                                        if (!r.ValueTextEquals("userDisplayName"u8)) { SkipValue(ref r); continue; }
                                        r.Read();
                                        if (act is null && Str(ref r) is { } name && !string.IsNullOrWhiteSpace(name)) act = name;
                                    }
                            }
                        }
                        else if (r.ValueTextEquals("type"u8)) { r.Read(); type = Str(ref r); }
                        else if (r.ValueTextEquals("notificationType"u8)) { r.Read(); notificationType = Str(ref r); }
                        else if (r.ValueTextEquals("category"u8)) { r.Read(); category = Str(ref r); }
                        else SkipValue(ref r);
                    }
                    if (id is null || title is null) continue;
                    into.Add(NotifyRows.ForSocial(id, timestamp, unread, title, actionUri,
                        webview ? SocialActionType.NavigateWebview : SocialActionType.Navigate, image, act,
                        type ?? notificationType ?? category));
                    added++;
                }
            }
            return added;

            // Only a non-empty JSON string is a value (0.2.9 `Str`).
            static string? Str(ref Utf8JsonReader r) => r.TokenType == JsonTokenType.String && r.GetString() is { Length: > 0 } v ? v : null;
        }

        /// <summary>A gander timestamp → unix ms: ISO-8601 (<c>2026-07-09T16:02:24.011Z</c>, an offset honoured), or a
        /// number / numeric string already in ms. 0 when it is none of those.</summary>
        static long TimestampMs(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.Number) return r.TryGetInt64(out long ms) ? ms : 0;
            if (r.TokenType != JsonTokenType.String || r.HasValueSequence) return 0;
            var text = r.ValueSpan;
            long numeric = Number(text);
            if (numeric >= 0 && text.Length > 10) return numeric;
            return IsoInstantMs(text);
        }

        /// <summary>ISO-8601 <c>yyyy-MM-ddTHH:mm:ss[.fff][Z|±HH:mm]</c> → unix ms, or 0. Pure; no DateTimeOffset.</summary>
        public static long IsoInstantMs(ReadOnlySpan<byte> iso)
        {
            if (iso.Length < 19 || iso[4] != '-' || iso[7] != '-' || (iso[10] != 'T' && iso[10] != ' ')
                || iso[13] != ':' || iso[16] != ':') return 0;
            long y = Number(iso[..4]), mo = Number(iso.Slice(5, 2)), d = Number(iso.Slice(8, 2));
            long h = Number(iso.Slice(11, 2)), mi = Number(iso.Slice(14, 2)), se = Number(iso.Slice(17, 2));
            if (y < 1 || mo is < 1 or > 12 || d is < 1 or > 31 || h is < 0 or > 23 || mi is < 0 or > 59 || se is < 0 or > 60) return 0;
            int i = 19;
            long millis = 0;
            if (i < iso.Length && iso[i] == '.')
            {
                int digits = 0;
                for (i++; i < iso.Length && iso[i] is >= (byte)'0' and <= (byte)'9'; i++, digits++)
                    if (digits < 3) millis = millis * 10 + (iso[i] - '0');
                for (; digits < 3; digits++) millis *= 10;
            }
            long offsetSeconds = 0;
            if (iso.Length >= i + 6 && (iso[i] == '+' || iso[i] == '-'))
            {
                long oh = Number(iso.Slice(i + 1, 2)), om = Number(iso.Slice(i + 4, 2));
                if (oh >= 0 && om >= 0) offsetSeconds = (iso[i] == '-' ? -1 : 1) * (oh * 3600 + om * 60);
            }
            long midnight = Seconds((int)y, (int)mo, (int)d);
            return (midnight + h * 3600 + mi * 60 + se - offsetSeconds) * 1000 + millis;
        }
    }
}
