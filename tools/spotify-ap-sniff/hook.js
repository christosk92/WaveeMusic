// Spotify desktop AP-traffic sniffer.
//
// Hooks the AP packet-receive path (post-Shannon-decrypt) and dumps each
// frame so we can see what hm:// Mercury URIs the desktop client uses —
// especially looking for any play-history endpoint that might exist beyond
// the gabo HTTPS POST and the dead /event-service/v1/events.
//
// The AP frame layout (little-endian, after Shannon decrypt):
//   [1 byte] cmd / packet type — 0xb2 = MercuryReq, 0xb5 = MercuryEvent, ...
//   [2 bytes BE] payload length
//   [N bytes]  payload (Mercury packet for cmd 0xb2/0xb5)
//
// Mercury packet layout inside the payload:
//   [2 bytes BE] seq length (usually 8 → seq is uint64)
//   [seq_len bytes BE] seq
//   [1 byte] flags (0x01 = FINAL)
//   [2 bytes BE] part_count
//   For each part: [2 bytes BE length] + [length bytes]
//   First part = protobuf Header (uri + method + status_code)
//
// We pattern-match for the AP read function — it's the one that calls into
// shn_decrypt with the receive buffer. Symbol stripped, so we use signatures.
//
// Usage: frida -l hook.js -f "%APPDATA%\Spotify\Spotify.exe"

'use strict';

const PACKET_TYPE_NAMES = {
    0x04: 'PongAck',
    0x08: 'StreamChunkRes',
    0x09: 'StreamChunkErr',
    0x0a: 'ChannelError',
    0x0b: 'ChannelAbort',
    0x1b: 'CountryCode',
    0x4a: 'Pong',
    0x50: 'AesKey',
    0x51: 'AesKeyError',
    0x52: 'Image',
    0x69: 'StreamChunk',
    0x76: 'PreferredLocale',
    0x82: 'ProductInfo',
    0xb2: 'MercuryReq',
    0xb3: 'MercurySub',
    0xb4: 'MercuryUnsub',
    0xb5: 'MercuryEvent',
    0xab: 'AuthenticateSuccess',
    0xad: 'AuthenticateFailure',
};

function nameForCmd(cmd) {
    return PACKET_TYPE_NAMES[cmd] || ('cmd_0x' + cmd.toString(16).padStart(2, '0'));
}

function readU16BE(buf, off) {
    return (buf[off] << 8) | buf[off + 1];
}

function readU64BE(buf, off) {
    let lo = 0, hi = 0;
    for (let i = 0; i < 4; i++) hi = hi * 256 + buf[off + i];
    for (let i = 4; i < 8; i++) lo = lo * 256 + buf[off + i];
    return hi * 4294967296 + lo;
}

function utf8(buf, off, len) {
    let s = '';
    for (let i = 0; i < len; i++) {
        const b = buf[off + i];
        if (b >= 0x20 && b < 0x7f) s += String.fromCharCode(b);
        else s += '.';
    }
    return s;
}

// Extract the Mercury Header.uri (field 1, wire type 2) from raw protobuf bytes.
// Cheap bare-bones parser — stops on first match.
function findUri(buf, off, len) {
    let i = 0;
    while (i < len) {
        const tag = buf[off + i++];
        const fieldNum = tag >> 3;
        const wire = tag & 7;
        if (wire === 0) {
            // varint — skip
            while (i < len && (buf[off + i] & 0x80)) i++;
            i++;
        } else if (wire === 2) {
            let l = 0, shift = 0;
            while (i < len) {
                const b = buf[off + i++];
                l |= (b & 0x7f) << shift;
                if (!(b & 0x80)) break;
                shift += 7;
            }
            if (fieldNum === 1) return utf8(buf, off + i, l); // uri field
            i += l;
        } else {
            return null; // unsupported wire type
        }
    }
    return null;
}

// Extract Header.method (field 2) and status_code (field 5)
function parseHeader(buf, off, len) {
    let uri = null, method = null, status = null;
    let i = 0;
    while (i < len) {
        const tag = buf[off + i++];
        const fieldNum = tag >> 3;
        const wire = tag & 7;
        if (wire === 0) {
            let v = 0, shift = 0;
            while (i < len) {
                const b = buf[off + i++];
                v |= (b & 0x7f) << shift;
                if (!(b & 0x80)) break;
                shift += 7;
            }
            if (fieldNum === 5) status = v;
        } else if (wire === 2) {
            let l = 0, shift = 0;
            while (i < len) {
                const b = buf[off + i++];
                l |= (b & 0x7f) << shift;
                if (!(b & 0x80)) break;
                shift += 7;
            }
            if (fieldNum === 1) uri = utf8(buf, off + i, l);
            else if (fieldNum === 2) method = utf8(buf, off + i, l);
            i += l;
        } else {
            break;
        }
    }
    return { uri, method, status };
}

function dumpFrame(direction, cmd, payloadAddr, payloadLen) {
    const buf = new Uint8Array(ArrayBuffer.wrap(payloadAddr, payloadLen));
    const cmdName = nameForCmd(cmd);

    if (cmd === 0xb2 || cmd === 0xb3 || cmd === 0xb4 || cmd === 0xb5) {
        // Mercury packet: parse seq + parts
        let off = 0;
        if (off + 2 > payloadLen) return;
        const seqLen = readU16BE(buf, off); off += 2;
        if (off + seqLen > payloadLen) return;
        const seq = (seqLen === 8) ? readU64BE(buf, off) : 0;
        off += seqLen;
        if (off + 1 > payloadLen) return;
        const flags = buf[off]; off += 1;
        if (off + 2 > payloadLen) return;
        const partCount = readU16BE(buf, off); off += 2;

        // First part = Header
        if (partCount > 0 && off + 2 <= payloadLen) {
            const headerLen = readU16BE(buf, off); off += 2;
            if (off + headerLen <= payloadLen) {
                const h = parseHeader(buf, off, headerLen);
                console.log(`[${direction}] ${cmdName} seq=${seq} flags=${flags} parts=${partCount} ` +
                    `uri=${h.uri || '?'} method=${h.method || '?'} status=${h.status || '-'} (${payloadLen}B)`);
                return;
            }
        }
        console.log(`[${direction}] ${cmdName} seq=${seq} (${payloadLen}B, malformed)`);
    } else {
        // Non-Mercury packet — just log type + size
        console.log(`[${direction}] ${cmdName} (${payloadLen}B)`);
    }
}

// ---- The hook itself ----
//
// We need to find the function that processes a freshly-decrypted AP frame.
// In libap (Spotify's AP library), there's typically a `recv_packet` that
// returns (cmd, buf, len) after Shannon decryption. The signature on Windows
// x64 puts cmd in CL (or stack slot) and the buffer pointer in RDX.
//
// Easier hook target: shn_decrypt itself. After it returns, the buffer holds
// plaintext. The first byte of the AP frame is the cmd; bytes 1–2 are the
// big-endian payload length.
//
// We pattern-scan for shn_decrypt in the main module. Spotify links Shannon
// statically; the function has a recognizable preamble.
//
// If pattern-matching fails (Spotify rebuilt with different opts), the user
// will see a "Shannon hook not found" message and can fall back to
// `spotify-analyze` (proper Wireshark setup).

function hookSpotify() {
    const main = Process.enumerateModules()[0];
    console.log(`Main module: ${main.name} @ ${main.base} (${main.size} bytes)`);

    // Try to find shn_decrypt by signature. Common Shannon implementations
    // start with a state-machine init that's recognizable. This is a heuristic;
    // adjust the pattern if it stops matching after a Spotify update.
    //
    // Common Shannon decrypt prologue (x64 MSVC): mov [rcx+...], <state load>
    // We try a known-good pattern from libshn-spotify; fall back if not found.
    let matches;
    try {
        // Pattern from prior reverse-engineering work — may need updating.
        matches = Memory.scanSync(main.base, main.size,
            '48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 49 8B F8 48 8B EA 48 8B D9');
    } catch (e) {
        matches = [];
    }

    if (matches.length === 0) {
        console.log('Shannon hook not found via pattern. Two fallbacks:');
        console.log('  1. Use spotify-analyze (https://github.com/librespot-org/spotify-analyze) — proper Wireshark setup');
        console.log('  2. Find shn_decrypt manually in IDA/Ghidra and call Interceptor.attach(ptr("0x..."), ...)');
        return;
    }

    console.log(`Found ${matches.length} Shannon candidate(s); hooking the first.`);
    const target = matches[0].address;

    Interceptor.attach(target, {
        onEnter(args) {
            // shn_decrypt(state, buf, len) — buf in RDX, len in R8 (Win64 ABI)
            this.buf = args[1];
            this.len = args[2].toInt32();
        },
        onLeave() {
            if (!this.buf || this.len < 3) return;
            try {
                const cmd = this.buf.readU8();
                const payloadLen = this.buf.add(1).readU16(); // big-endian on wire, but
                // Spotify stores network-order — try both
                const payloadAddr = this.buf.add(3);
                dumpFrame('IN ', cmd, payloadAddr, this.len - 3);
            } catch (e) {
                // ignore decode failures
            }
        },
    });
}

setImmediate(hookSpotify);
