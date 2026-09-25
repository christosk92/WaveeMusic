"""ops/tools/capture/proto_dump.py — a minimal, generic protobuf wire decoder plus field-name maps for the
connect-state messages the story/diff views need (§5 of docs/plans/wavee/realtime-capture-implementation.md).

Stdlib-only, no `protoc`, no generated code: this reads the raw protobuf WIRE FORMAT (tag/varint/length-delimited)
directly and maps field numbers to names using the schemas below, hand-transcribed from the repo's own
`.proto` sources (`src/apps/Wavee/Spotify/Protos/connect.proto`, `player.proto`, `devices.proto`) — read, not
guessed. Only the fields the story/diff/anomaly views actually need are named (next_tracks, prev_tracks, track
uri/uid/provider, queue_revision, index, the handful of timestamps, device ids, message ids); everything else
decodes into an `_unknown` bucket by field number so nothing is silently dropped, it is just unnamed.

This intentionally supersedes the plan's own §5 sketch of shelling out to `protoc --python_out` at run time: the
task that commissioned this tool asked explicitly for a stdlib-only generic wire decoder instead, which is also
more robust (a `protoc` binary need not be on PATH, and a hand-rolled wire walk cannot desync from a generated
module built against a different protoc version).
"""

from __future__ import annotations

from typing import Any, Dict, Iterator, List, Tuple

# ── the generic wire walk ────────────────────────────────────────────────────────────────────────────────────────

WT_VARINT = 0
WT_FIXED64 = 1
WT_LEN = 2
WT_FIXED32 = 5


class ProtoDecodeError(ValueError):
    pass


def read_varint(buf: bytes, pos: int) -> Tuple[int, int]:
    result = 0
    shift = 0
    start = pos
    while True:
        if pos >= len(buf):
            raise ProtoDecodeError("truncated varint")
        b = buf[pos]
        result |= (b & 0x7F) << shift
        pos += 1
        if not (b & 0x80):
            break
        shift += 7
        if shift > 70:
            raise ProtoDecodeError("varint too long")
    if pos - start > 10:
        raise ProtoDecodeError("varint too long")
    return result, pos


def iter_wire_fields(buf: bytes) -> Iterator[Tuple[int, int, Any]]:
    """Yields (field_number, wire_type, raw_value) for one message's top-level fields. `raw_value` is an int for
    varint/fixed32/fixed64, or `bytes` for a length-delimited field (string/bytes/embedded message/packed repeated).
    Group wire types (3/4) are not used by proto3 and are not supported — a message containing one raises, which
    callers treat as "not decodable as protobuf" and fall back to a hex dump (§5's fallback ladder)."""
    pos = 0
    n = len(buf)
    while pos < n:
        tag, pos = read_varint(buf, pos)
        field_no = tag >> 3
        wire_type = tag & 0x7
        if wire_type == WT_VARINT:
            value, pos = read_varint(buf, pos)
        elif wire_type == WT_FIXED64:
            if pos + 8 > n:
                raise ProtoDecodeError("truncated fixed64")
            value = int.from_bytes(buf[pos:pos + 8], "little")
            pos += 8
        elif wire_type == WT_LEN:
            length, pos = read_varint(buf, pos)
            if pos + length > n:
                raise ProtoDecodeError("truncated length-delimited field")
            value = buf[pos:pos + length]
            pos += length
        elif wire_type == WT_FIXED32:
            if pos + 4 > n:
                raise ProtoDecodeError("truncated fixed32")
            value = int.from_bytes(buf[pos:pos + 4], "little")
            pos += 4
        else:
            raise ProtoDecodeError(f"unsupported wire type {wire_type} (group start/end — not proto3)")
        yield field_no, wire_type, value


def decode_unknown(buf: bytes) -> Dict[str, Any]:
    """No schema at all — every field surfaces under its number, values left as raw ints/bytes. This is the last
    rung before a hex dump; it still gives a reader field numbers/lengths to eyeball."""
    out: Dict[str, Any] = {}
    for field_no, wire_type, value in iter_wire_fields(buf):
        key = str(field_no)
        entry = len(value) if wire_type == WT_LEN else value
        out.setdefault(key, []).append(entry)
    return out


# ── field-name maps, transcribed from the .proto sources named above ───────────────────────────────────────────
# Each schema maps field_number -> (name, type). `type` is one of:
#   "string" | "bytes" | "bool" | "int" (any varint/fixed numeric width)
#   "message:<SCHEMA_NAME>"   — a single embedded message
#   "message:<SCHEMA_NAME>*"  — a repeated embedded message
#   "map:<SCHEMA_NAME>"       — a protobuf map<string, <SCHEMA_NAME>> (decoded as {key: value})
#   "map:string"              — a protobuf map<string, string>

PROVIDED_TRACK: Dict[int, Tuple[str, str]] = {
    1: ("uri", "string"),
    2: ("uid", "string"),
    6: ("provider", "string"),
    8: ("album_uri", "string"),
    10: ("artist_uri", "string"),
}

CONTEXT_INDEX: Dict[int, Tuple[str, str]] = {
    1: ("page", "int"),
    2: ("track", "int"),
}

PLAY_ORIGIN: Dict[int, Tuple[str, str]] = {
    1: ("feature_identifier", "string"),
    2: ("feature_version", "string"),
}

PLAYER_STATE: Dict[int, Tuple[str, str]] = {
    1: ("timestamp", "int"),
    2: ("context_uri", "string"),
    6: ("index", "message:CONTEXT_INDEX"),
    7: ("track", "message:PROVIDED_TRACK"),
    8: ("playback_id", "string"),
    10: ("position_as_of_timestamp", "int"),
    11: ("duration", "int"),
    12: ("is_playing", "bool"),
    13: ("is_paused", "bool"),
    14: ("is_buffering", "bool"),
    19: ("prev_tracks", "message:PROVIDED_TRACK*"),
    20: ("next_tracks", "message:PROVIDED_TRACK*"),
    23: ("session_id", "string"),
    24: ("queue_revision", "string"),
    25: ("position", "int"),
    26: ("entity_uri", "string"),
    35: ("session_command_id", "string"),
}

DEVICE_INFO: Dict[int, Tuple[str, str]] = {
    1: ("can_play", "bool"),
    2: ("volume", "int"),
    3: ("name", "string"),
    6: ("device_software_version", "string"),
    7: ("device_type", "int"),
    9: ("spirc_version", "string"),
    10: ("device_id", "string"),
    13: ("client_id", "string"),
    14: ("brand", "string"),
    15: ("model", "string"),
    17: ("product_id", "string"),
}

DEVICE: Dict[int, Tuple[str, str]] = {
    1: ("device_info", "message:DEVICE_INFO"),
    2: ("player_state", "message:PLAYER_STATE"),
}

CLUSTER: Dict[int, Tuple[str, str]] = {
    1: ("changed_timestamp_ms", "int"),
    2: ("active_device_id", "string"),
    3: ("player_state", "message:PLAYER_STATE"),
    4: ("device", "map:DEVICE_INFO"),
    6: ("transfer_data_timestamp", "int"),
    8: ("need_full_player_state", "bool"),
    9: ("server_timestamp_ms", "int"),
    10: ("needs_state_updates", "bool"),
    11: ("started_playing_at_timestamp", "int"),
}

CLUSTER_UPDATE: Dict[int, Tuple[str, str]] = {
    1: ("cluster", "message:CLUSTER"),
    2: ("update_reason", "int"),
    3: ("ack_id", "string"),
    4: ("devices_that_changed", "string*"),
}

PUT_STATE_REQUEST: Dict[int, Tuple[str, str]] = {
    1: ("callback_url", "string"),
    2: ("device", "message:DEVICE"),
    3: ("member_type", "int"),
    4: ("is_active", "bool"),
    5: ("put_state_reason", "int"),
    6: ("message_id", "int"),
    7: ("last_command_sent_by_device_id", "string"),
    8: ("last_command_message_id", "int"),
    9: ("started_playing_at", "int"),
    11: ("has_been_playing_for_ms", "int"),
    12: ("client_side_timestamp", "int"),
    13: ("only_write_player_state", "bool"),
}

_SCHEMAS: Dict[str, Dict[int, Tuple[str, str]]] = {
    "PROVIDED_TRACK": PROVIDED_TRACK,
    "CONTEXT_INDEX": CONTEXT_INDEX,
    "PLAY_ORIGIN": PLAY_ORIGIN,
    "PLAYER_STATE": PLAYER_STATE,
    "DEVICE_INFO": DEVICE_INFO,
    "DEVICE": DEVICE,
    "CLUSTER": CLUSTER,
    "CLUSTER_UPDATE": CLUSTER_UPDATE,
    "PUT_STATE_REQUEST": PUT_STATE_REQUEST,
}


def _decode_typed(wire_type: int, value: Any, typ: str) -> Any:
    if typ == "string":
        return value.decode("utf-8", errors="replace") if isinstance(value, (bytes, bytearray)) else str(value)
    if typ == "bytes":
        return value
    if typ == "bool":
        return bool(value)
    if typ == "int":
        return value
    if typ.startswith("message:"):
        inner = typ[len("message:"):]
        repeated = inner.endswith("*")
        schema_name = inner[:-1] if repeated else inner
        return decode_message(value, _SCHEMAS[schema_name])
    raise ProtoDecodeError(f"unhandled scalar type {typ!r}")


def decode_message(buf: bytes, schema: Dict[int, Tuple[str, str]]) -> Dict[str, Any]:
    """Decodes `buf` as an embedded message against `schema`. Repeated fields ("...*") collect into a list; a
    protobuf map ("map:...") collects into a dict keyed by the map entry's own string key (field 1) with the value
    (field 2) decoded per the named schema, or left as UTF-8 text for `map:string`."""
    out: Dict[str, Any] = {}
    for field_no, wire_type, value in iter_wire_fields(buf):
        spec = schema.get(field_no)
        if spec is None:
            out.setdefault("_unknown", []).append(field_no)
            continue
        name, typ = spec
        if typ.endswith("*") and not typ.startswith("message:"):
            # a packed/repeated scalar, e.g. "string*"
            base = typ[:-1]
            decoded = _decode_typed(wire_type, value, base)
            out.setdefault(name, []).append(decoded)
        elif typ.startswith("message:") and typ.endswith("*"):
            decoded = _decode_typed(wire_type, value, typ)
            out.setdefault(name, []).append(decoded)
        elif typ.startswith("map:"):
            value_kind = typ[len("map:"):]
            entry_schema = {1: ("key", "string"), 2: ("value", "string" if value_kind == "string" else f"message:{value_kind}")}
            entry = decode_message(value, entry_schema)
            out.setdefault(name, {})[entry.get("key")] = entry.get("value")
        else:
            out[name] = _decode_typed(wire_type, value, typ)
    return out


def decode_cluster(buf: bytes) -> Dict[str, Any]:
    return decode_message(buf, CLUSTER)


def decode_cluster_update(buf: bytes) -> Dict[str, Any]:
    return decode_message(buf, CLUSTER_UPDATE)


def decode_player_state(buf: bytes) -> Dict[str, Any]:
    return decode_message(buf, PLAYER_STATE)


def decode_put_state_request(buf: bytes) -> Dict[str, Any]:
    return decode_message(buf, PUT_STATE_REQUEST)


# ── best-effort dispatch used by decode.py's fallback ladder ────────────────────────────────────────────────────

def guess_and_decode(buf: bytes, hint: str = "") -> Tuple[str, Dict[str, Any]]:
    """Tries the schema `hint` suggests first (a topic/endpoint string containing "put-state"/"cluster"/"player"),
    then every other known schema, then the unnamed fallback. Returns (schema_name, decoded). Raises
    ProtoDecodeError only if nothing — not even the unnamed decoder — can walk the wire format at all (i.e. it is
    not protobuf-shaped junk), so the caller can fall through to a hex dump."""
    order = ["PUT_STATE_REQUEST", "CLUSTER", "CLUSTER_UPDATE", "PLAYER_STATE"]
    hint_l = hint.lower()
    if "put" in hint_l or "putstate" in hint_l:
        order = ["PUT_STATE_REQUEST", "CLUSTER", "CLUSTER_UPDATE", "PLAYER_STATE"]
    elif "cluster" in hint_l:
        order = ["CLUSTER", "CLUSTER_UPDATE", "PUT_STATE_REQUEST", "PLAYER_STATE"]
    elif "player" in hint_l:
        order = ["PLAYER_STATE", "CLUSTER", "PUT_STATE_REQUEST", "CLUSTER_UPDATE"]

    # Validate the wire walk once up front — if this raises, the buffer is not protobuf-shaped at all.
    list(iter_wire_fields(buf))

    for name in order:
        try:
            decoded = decode_message(buf, _SCHEMAS[name])
            if decoded:
                return name, decoded
        except ProtoDecodeError:
            continue
    return "unknown", decode_unknown(buf)
