#!/usr/bin/env bash
# Decode Spotify desktop gabo-receiver-service bodies against our proto schemas.
# Usage: ./decode_gabo.sh <base64-body>
# (or ./decode_gabo.sh <file-with-base64-body>)

set -euo pipefail

PROTOC="/c/Users/ChristosKarapasias/.nuget/packages/grpc.tools/2.80.0/tools/windows_x64/protoc.exe"
PROTO_ROOT="Wavee/Protocol/Protos"
ENVELOPE_PROTO="event_sender_envelope.proto"
EVENTS_PROTO="event_sender_events.proto"

if [[ -z "${1:-}" ]]; then
    echo "usage: $0 <base64-body|file>" >&2
    exit 1
fi

if [[ -f "$1" ]]; then
    BODY_B64=$(cat "$1" | tr -d ' \n')
else
    BODY_B64=$(echo -n "$1" | tr -d ' \n')
fi

# Base64 decode -> binary
TMPBIN=$(mktemp)
trap 'rm -f "$TMPBIN"' EXIT
echo -n "$BODY_B64" | base64 -d > "$TMPBIN"

echo "=== Body size: $(wc -c < "$TMPBIN") bytes ==="
echo

echo "=== As PublishEventsRequest (event_name only — the envelope) ==="
"$PROTOC" --decode=spotify.event_sender.PublishEventsRequest \
    -I "$PROTO_ROOT" \
    "$ENVELOPE_PROTO" "$EVENTS_PROTO" < "$TMPBIN" | head -80
