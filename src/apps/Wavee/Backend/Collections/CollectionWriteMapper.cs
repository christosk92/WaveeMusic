using Google.Protobuf;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Backend.Collections;

// ── §2.4 — the body builder for POST /collection/v2/write ─────────────────────────────────────────────────────────────
// A single-item write carrying the desired end-state: saved → is_removed=false, unsaved → true, against the WIRE set (via
// CollectionSets — "collection"|"artist"|"show"|"listenlater"), plus a client_update_id for echo suppression (§7.1).
// added_at is int32 UNIX SECONDS — the collection trap (playlist timestamps are int64 ms).
public static class CollectionWriteMapper
{
    public static byte[] BuildWrite(string username, string setId, string uri, bool saved,
                                    long nowUnixSeconds, string clientUpdateId)
        => new Col.WriteRequest
        {
            Username = username,
            Set = CollectionSets.WireSet(setId),
            Items = { new Col.CollectionItem { Uri = uri, AddedAt = (int)nowUnixSeconds, IsRemoved = !saved } },
            ClientUpdateId = clientUpdateId,
        }.ToByteArray();
}
