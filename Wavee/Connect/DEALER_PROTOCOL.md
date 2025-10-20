# Spotify Dealer Protocol Guide

## Overview

The **Dealer** is Spotify's real-time messaging system that uses WebSockets to push updates and handle remote commands between Spotify's backend and clients. It's a critical component of Spotify Connect, enabling features like:

- Remote playback control across devices
- Real-time playlist/collection synchronization
- Device state updates and clustering
- Volume control
- User attribute changes
- Logout notifications

Unlike the Mercury protocol (which uses request/response over the main TCP connection), Dealer provides **bidirectional, asynchronous messaging** over a persistent WebSocket connection.

---

## Connection Establishment

### WebSocket Endpoint

```
wss://{dealer-host}/?access_token={token}
```

**Components:**
- `{dealer-host}`: Dealer endpoint obtained from Access Point resolver (e.g., `gae2-dealer.spotify.com`)
- `{token}`: OAuth access token obtained during authentication

**Connection Process:**
1. Resolve dealer endpoint from AP resolver
2. Obtain valid OAuth access token
3. Establish WebSocket connection with token in query string
4. Begin heartbeat mechanism upon successful connection

### Connection Lifecycle

**States:**
- **Connected**: WebSocket open, heartbeat active
- **Disconnected**: WebSocket closed, attempting reconnection
- **Failed**: Connection error, scheduled retry

**Reconnection Strategy:**
- Wait 10 seconds after disconnection
- Retry indefinitely until successful
- Refresh access token if expired

---

## Message Types

The Dealer protocol uses **JSON-formatted messages** with a `type` field indicating the message category.

### 1. PING (Heartbeat Request)

**Direction:** Client → Server

**Purpose:** Keep connection alive and detect disconnections

**Format:**
```json
{
  "type": "ping"
}
```

**Timing:**
- Send every **30 seconds**
- Expect PONG response within **3 seconds**
- Reconnect if no PONG received

### 2. PONG (Heartbeat Response)

**Direction:** Server → Client

**Purpose:** Acknowledge heartbeat

**Format:**
```json
{
  "type": "pong"
}
```

### 3. MESSAGE (Asynchronous Notification)

**Direction:** Server → Client (one-way)

**Purpose:** Push notifications for state changes, updates, events

**Format:**
```json
{
  "type": "message",
  "uri": "hm://connect-state/v1/connect/volume",
  "headers": {
    "Content-Type": "application/x-protobuf",
    "Transfer-Encoding": "gzip"
  },
  "payloads": [
    "base64EncodedData1",
    "base64EncodedData2"
  ]
}
```

**Fields:**
- `uri`: Message identifier/topic (see URI Patterns below)
- `headers`: Metadata about payload encoding
- `payloads`: Array of base64-encoded payload chunks

**No response required** - fire-and-forget pattern

### 4. REQUEST (Synchronous Command)

**Direction:** Server → Client (expects reply)

**Purpose:** Remote commands requiring acknowledgment

**Format:**
```json
{
  "type": "request",
  "key": "unique-request-key-12345",
  "message_ident": "hm://connect-state/v1/devices/command",
  "headers": {
    "Content-Type": "application/json",
    "Transfer-Encoding": "gzip"
  },
  "payload": {
    "compressed": "base64GzippedData",
    "message_id": 42,
    "sent_by_device_id": "sender-device-id-abc123",
    "command": {
      "endpoint": "transfer",
      "data": { ... }
    }
  }
}
```

**Fields:**
- `key`: Unique identifier for this request (used in reply)
- `message_ident`: Command type/category
- `payload.message_id`: Message sequence number
- `payload.sent_by_device_id`: Originating device
- `payload.command`: Actual command object

**Reply Required:**
```json
{
  "type": "reply",
  "key": "unique-request-key-12345",
  "payload": {
    "success": true
  }
}
```

**Reply Results:**
- `SUCCESS` - Command executed successfully
- `DEVICE_NOT_FOUND` - Target device unavailable
- `CONTEXT_PLAYER_ERROR` - Playback context error
- `DEVICE_DISAPPEARED` - Device disconnected
- `UPSTREAM_ERROR` - Internal server error
- `DEVICE_DOES_NOT_SUPPORT_COMMAND` - Unsupported operation
- `RATE_LIMITED` - Too many requests
- `UNKNOWN_SEND_COMMAND_RESULT` - Unknown error

---

## Payload Encoding

Dealer supports multiple payload encodings based on `Content-Type` and `Transfer-Encoding` headers.

### Content-Type Values

#### 1. `application/x-protobuf`
Binary protobuf message (most common)

**Processing:**
```
1. Concatenate all base64-decoded payload chunks
2. If Transfer-Encoding == "gzip": decompress with GZIP
3. Parse as protobuf message
```

#### 2. `application/json`
JSON object (single payload only)

**Processing:**
```
1. Decode single base64 payload
2. Parse as JSON object
```

#### 3. `text/plain`
Plain text string (single payload only)

**Processing:**
```
1. Decode single base64 payload
2. Interpret as UTF-8 string
```

### Transfer-Encoding

**`gzip`**: Payload is GZIP-compressed
- Decompress after base64 decoding
- Applies to both protobuf and other formats

**None/absent**: Payload is uncompressed
- Use directly after base64 decoding

### Processing Algorithm

```
payloads = message["payloads"]

if Content-Type == "application/json":
    data = base64_decode(payloads[0])

elif Content-Type == "text/plain":
    data = base64_decode(payloads[0]).decode("utf-8")

else:  # binary/protobuf
    # Concatenate all payload chunks
    stream = concatenate([base64_decode(p) for p in payloads])

    if Transfer-Encoding == "gzip":
        stream = gzip_decompress(stream)

    data = stream

return data
```

---

## URI Patterns

Dealer uses two URI schemes to identify message topics and commands.

### `hm://` Scheme (Hermes Mercury Protocol)

Internal Spotify protocol URIs for backend services.

#### Connect State (Spotify Connect)
- `hm://connect-state/v1/devices/{device_id}` - Device state updates
- `hm://connect-state/v1/connect/volume` - Volume change commands
- `hm://connect-state/v1/connect/logout` - Remote logout
- `hm://connect-state/v1/cluster` - Device cluster updates

#### Pusher (Connection Management)
- `hm://pusher/v1/connections/{connection_id}` - Connection ID assignment

#### Playlists & Collections
- `hm://playlist/{playlist_id}` - Playlist modification events
- `hm://collection/collection/{username}/json` - Collection updates

#### Metadata
- `hm://metadata/4/track/{track_id}` - Track metadata
- `hm://metadata/4/album/{album_id}` - Album metadata
- `hm://metadata/4/artist/{artist_id}` - Artist metadata
- `hm://metadata/4/episode/{episode_id}` - Podcast episode metadata
- `hm://metadata/4/show/{show_id}` - Podcast show metadata

#### Other Services
- `hm://event-service/v1/events` - Analytics/telemetry events
- `hm://radio-apollo/v3/stations/{context}` - Radio station data
- `hm://searchview/km/v4/search/` - Search queries
- `hm://autoplay-enabled/query?uri={context}` - Autoplay settings

### `spotify:` Scheme (Spotify URIs)

Public Spotify identifiers.

- `spotify:user:attributes:update` - User attribute changes
- `spotify:user:{username}:collection` - User's collection/library
- `spotify:track:{id}` - Track URI
- `spotify:album:{id}` - Album URI
- `spotify:playlist:{id}` - Playlist URI

---

## Message Listener Registration

Clients register listeners for specific URI patterns to receive relevant messages.

### Pattern Matching

**Prefix matching:** URIs are matched by prefix, not exact equality.

**Examples:**
```csharp
// Matches all connect-state messages
AddMessageListener("hm://connect-state/v1/")

// Matches only volume commands
AddMessageListener("hm://connect-state/v1/connect/volume")

// Matches all playlists
AddMessageListener("hm://playlist/")

// Matches specific user's collection
AddMessageListener($"hm://collection/collection/{username}/json")
```

### Typical Registrations

**For Spotify Connect device:**
```csharp
// Connection management
AddMessageListener("hm://pusher/v1/connections/");

// Volume control
AddMessageListener("hm://connect-state/v1/connect/volume");

// Device clustering
AddMessageListener("hm://connect-state/v1/cluster");

// Remote logout
AddMessageListener("hm://connect-state/v1/connect/logout");

// User attributes
AddMessageListener("spotify:user:attributes:update");

// Playlist changes (if needed)
AddMessageListener("hm://playlist/");

// Collection updates
AddMessageListener($"hm://collection/collection/{username}/json");

// Remote commands (REQUEST type)
AddRequestListener("hm://connect-state/v1/");
```

---

## Common Message Examples

### 1. Connection ID Assignment

**Type:** MESSAGE
**URI:** `hm://pusher/v1/connections/{connection_id}`

**Purpose:** Server assigns a connection ID for this session

**Headers:**
```json
{
  "Spotify-Connection-Id": "connection-abc-123-def"
}
```

**Payload:** Empty or minimal

**Action:** Store connection ID for PUT state requests

---

### 2. Volume Command

**Type:** MESSAGE
**URI:** `hm://connect-state/v1/connect/volume`

**Protobuf:** `Connect.SetVolumeCommand`

```protobuf
message SetVolumeCommand {
  int32 volume = 1;  // 0-65535 (0x0000-0xFFFF)
  CommandOptions command_options = 2;
}
```

**Action:** Update device volume to specified level

---

### 3. Cluster Update

**Type:** MESSAGE
**URI:** `hm://connect-state/v1/cluster`

**Protobuf:** `Connect.ClusterUpdate`

```protobuf
message ClusterUpdate {
  Cluster cluster = 1;
  UpdateReason update_reason = 2;
  int64 timestamp = 3;
}
```

**Purpose:** Notify of changes in device cluster (active devices, transfer state)

**Action:** Update internal cluster state, handle device transfers

---

### 4. Remote Logout

**Type:** MESSAGE
**URI:** `hm://connect-state/v1/connect/logout`

**Payload:** Empty

**Action:** Close session immediately, clear credentials

---

### 5. Playlist Modification

**Type:** MESSAGE
**URI:** `hm://playlist/{playlist_id}`

**Protobuf:** `Playlist4ApiProto.PlaylistModificationInfo`

```protobuf
message PlaylistModificationInfo {
  bytes uri = 1;
  int64 new_revision = 2;
  int64 parent_revision = 3;
  repeated Op ops = 4;  // Add/Remove/Move operations
  PlaylistAttributes attributes = 5;
}
```

**Action:** Update local playlist cache with modifications

---

### 6. User Attributes Update

**Type:** MESSAGE (via Mercury) or Dealer MESSAGE
**URI:** `spotify:user:attributes:update`

**Protobuf:** `ExplicitContentPubsub.UserAttributesUpdate`

```protobuf
message UserAttributesUpdate {
  map<string, string> pairs = 1;
}
```

**Examples:**
- `"filter-explicit-content" -> "1"`
- `"country" -> "US"`
- `"product" -> "premium"`

**Action:** Update user preferences/attributes in session

---

### 7. Remote Playback Command (REQUEST)

**Type:** REQUEST
**Message Ident:** `hm://connect-state/v1/devices/command`

**Compressed Payload Structure:**
```json
{
  "message_id": 42,
  "sent_by_device_id": "source-device-id",
  "command": {
    "endpoint": "transfer",
    "data": {
      "options": {
        "restore_paused": "restore"
      }
    }
  }
}
```

**Common Commands:**
- `transfer` - Transfer playback to this device
- `play` - Start playback
- `pause` - Pause playback
- `resume` - Resume playback
- `skip_next` - Next track
- `skip_prev` - Previous track
- `seek` - Seek to position
- `set_shuffling_context` - Toggle shuffle
- `set_repeating_context` - Toggle repeat

**Action:** Execute command, send reply with result

---

## Threading Model

### Asynchronous Processing

All message/request handling should be **asynchronous** to prevent blocking the WebSocket receiver.

**Pattern:**
```
WebSocket receives message
   ↓
Parse JSON, extract type
   ↓
Queue to async worker/task
   ↓
Return immediately (don't block WebSocket)
   ↓
Worker processes message
   ↓
Invoke registered listeners
   ↓
(For REQUEST) Send reply
```

### Concurrency Considerations

- **Single-threaded listener dispatch** prevents race conditions
- **Listener callbacks** should be quick or delegate to background tasks
- **Reply sending** must not block listener execution
- **Connection management** (reconnect, heartbeat) runs on separate scheduler

---

## Error Handling

### WebSocket Failures

**Connection Lost:**
1. Log error/exception
2. Mark connection as invalid
3. Cancel heartbeat timer
4. Schedule reconnection after 10 seconds
5. Retry with fresh token if needed

**Heartbeat Timeout:**
1. Detect missing PONG within 3 seconds
2. Log warning
3. Close WebSocket
4. Trigger reconnection flow

### Message Processing Errors

**Malformed JSON:**
- Log error with raw message
- Ignore message, continue processing

**Decompression Failure:**
- Log warning with URI and headers
- Skip message

**Unknown Message Type:**
- Throw exception or log error
- Do not crash connection

**No Registered Listener:**
- Log debug message (not error)
- Common for unused message types

---

## Implementation Checklist

For implementing a Dealer client in C#:

- [ ] WebSocket client with auto-reconnection
- [ ] JSON parsing (System.Text.Json or Newtonsoft.Json)
- [ ] Base64 decoding
- [ ] GZIP decompression (System.IO.Compression.GzipStream)
- [ ] Protobuf deserialization (Google.Protobuf)
- [ ] Heartbeat timer (30s interval, 3s timeout)
- [ ] Async message queue/worker
- [ ] Listener registration (prefix matching)
- [ ] Request/reply handling
- [ ] Connection ID storage
- [ ] Thread-safe listener management
- [ ] Graceful shutdown/cleanup

---

## Security Considerations

1. **Token in URL:** Access token is passed in WebSocket URL query string
   - Use WSS (TLS) to encrypt connection
   - Token exposure risk if logging URLs

2. **Token Refresh:** Tokens expire; refresh before reconnection if needed

3. **Connection Validation:** Verify dealer hostname from trusted AP resolver

4. **Message Validation:** Always validate message structure before processing
   - Check required fields exist
   - Validate protobuf parsing
   - Sanitize URIs before matching

---

## Differences from Mercury Protocol

| Feature | Dealer (WebSocket) | Mercury (TCP) |
|---------|-------------------|---------------|
| **Transport** | WebSocket over TLS | Custom protocol over TLS |
| **Direction** | Bidirectional push | Client request/response |
| **Use Case** | Real-time updates, commands | Metadata, playback data |
| **Connection** | Persistent WebSocket | Persistent TCP with Shannon cipher |
| **Message Format** | JSON with binary payloads | Binary packets |
| **Heartbeat** | PING/PONG every 30s | Different keep-alive mechanism |
| **Authentication** | Token in URL | During handshake |

**Both are required** for a full Spotify client:
- **Mercury:** Metadata, audio keys, channels, initial state
- **Dealer:** Real-time connect state, remote commands, live updates

---

## References

**Implementations:**
- librespot-java: `dealer/DealerClient.java`
- librespot (Rust): `dealer` module

**Protobuf Definitions:**
- `connect.proto` - Connect state messages
- `playlist4.proto` - Playlist operations
- `metadata.proto` - Metadata structures
- `explicit_content_pubsub.proto` - User attributes

**Related Components:**
- Access Point Resolver (provides dealer endpoint)
- Token Provider (OAuth tokens)
- Mercury Client (complementary protocol)
- Session Manager (connection lifecycle)

---

This protocol documentation provides everything needed to implement a C# Dealer client for the Wavee project. The actual implementation would go in `Wavee/Connect/DealerClient.cs` following patterns similar to the Mercury and Session implementations already in the project.
