"""Build the podcast / audiobook page fixtures from local Fiddler captures, redacted, and prove they leak nothing.

Offline only: this tool reads capture files on disk and writes JSON. It sends no request of any kind.

    python -I tools/podcast-fixture.py build \
        --abook     C:/WAVEE/scratch/abook/raw \
        --abook-web C:/WAVEE/scratch/abook/raw2 \
        --mgr       %USERPROFILE%/Documents/Fiddler2/Captures/themanagerpath.saz \
        --vc1       C:/WAVEE/wavee-captures/fresh-client-2026-09-19/vc1 \
        --vc2       C:/WAVEE/wavee-captures/fresh-client-2026-09-19/vc2
        writes src/apps/Wavee/assets/spotify/fixtures/podcast-pages/<name>.json for the six names below, then runs leakcheck;
    python -I tools/podcast-fixture.py leakcheck [the same capture options]
        exits 1 when a written fixture holds an account id, username, display name, device name, device or connection id, a
        uuid that is not the zero stand-in, an e-mail address, a cookie, a token or a credential word (also inside every
        base64 protobuf body, decoded); 0 when clean. With the capture options it also learns the account's own ids from the
        captures and looks for each of them. Run it before `git add`: a fixture that fails it never enters the public repo.

    Both verbs take --display-name / --device-name (repeatable) for names the captures do not reveal in a parseable place.

Capture kinds: --abook / --abook-web are Fiddler "raw" folders (raw/NNN_c.txt request, raw/NNN_s.txt response: HTTP headers
then body; bodies may be chunked and zstd/gzip/deflate/br encoded), --mgr is a .saz zip of the same layout, --vc1 / --vc2 are
dump_all.py folders (index.tsv + bodies/NNNN_resp.* already decompressed). zstd: Python 3.14 compression.zstd, else the
`zstandard` module, else Node's zlib.zstdDecompressSync (one node process for all bodies); br: Node.

Captures (what each one is):
    abook     audiobookssss.saz extracted (official desktop 1.3.8.620, 2026-11-17), map C:/WAVEE/scratch/abook/audiobook-api-map.md
    abook-web bbbb.saz extracted (open.spotify.com web player, anonymous page token), map §10
    mgr       themanagerpath.saz (official desktop 1.2.96.518, 2026-09-19): The Manager's Path page opened fresh
    vc1, vc2  verycomplex.saz / verycomplex2.saz dumps (official desktop 1.2.96.518, 2026-09-19), findings-podcast-wire.md

Exact source sessions per fixture (extended-metadata answers are filtered to the show and its episodes; an answer that already
only concerns them is kept byte for byte):

    show-episodic   spotify:show:4ZNGEoKCuO3X8a7dHLl13h "Hey Tablo" (SHOW_V4 75 = 2 EPISODIC)
                    items   vc1#692  GET /playlist/v2/show/{id} (newest first)
                    xm      vc1#80 (collection: SHOW_V4, SHOW_ACCESS, 127, IDENTITY, 183; filtered), vc1#519 (your_library: 179;
                            filtered), vc1#693 (show_cosmos show batch), vc1#694 (show_cosmos episode traits), vc1#695
                            (track_metadata_loader EPISODE_V4 x35), vc1#75 (played_state EPISODE_V4; filtered), vc1#660
                            (track_metadata_loader; filtered), vc1#701 (resumption 249), vc1#749 (list_metadata_prefetcher 178,
                            212; filtered)
    show-serial     spotify:show:1YEwCKoRz8fEDqheXB6UJ1 "ColdFusion". NO podcast with SHOW_V4 75 = 1 (SEQUENTIAL) exists in any
                    local capture (every sequential show is an audiobook), so this is the best second EPISODIC show; its
                    protobuf is not altered. "Serial order" in the state block = oldest first.
                    items   vc1#623  GET /playlist/v2/show/{id}
                    xm      vc1#80 (filtered), vc1#519 (filtered), vc1#624 (show batch), vc1#625 (EPISODE_V4 x45), vc1#626
                            (episode traits), vc1#75 (filtered), vc1#634 (resumption 249), vc1#636 (178, 212; filtered)
    book-titled, book-inprogress, book-finished
                    spotify:show:4nWWkORsA650CoeCeAIi2K "The Subtle Art of Not Giving a F*ck" (real chapter titles)
                    items   abook#682  GET /playlist/v2/show/{id} (book order, the sample last)
                    xm      abook#684 (show_cosmos show batch, 21 kinds, all fresh), abook#685 (SHOW_V4 + SHOW_ACCESS),
                            abook#686 (track_metadata_loader EPISODE_V4 x13), abook#687 (show_cosmos chapter traits),
                            abook#690 (resumption 249), abook#693 (playlist_tracks_loader 22, 212), abook#731, #732, #733
                            (resumption CHAPTER_SPECIFICS 198)
                    pathfinder (book-titled only) similarAudiobooks abook-web#295 (a public "more like this" list; it answers
                            the web book spotify:show:71CqWlRIA46iXurhdSSNNA, the only similarAudiobooks answer captured)
    book-tracks     spotify:show:1BARGvPTODoBcEjiiXK70g "The Manager's Path" (chapters "Track 1" .. "Track 18")
                    items   mgr#399  GET /playlist/v2/show/{id} (the abook capture only had a 304 for it)
                    xm      vc1#80 (SHOW_V4; filtered: both audiobook captures answered SHOW_V4 304), mgr#398 (show_cosmos
                            show batch: specifics, genres, rating, html description, price ...), mgr#401 (EPISODE_V4 x19),
                            mgr#402 (show_cosmos chapter traits), mgr#407 (resumption 249), mgr#413 (178, 212),
                            abook#564 (list_metadata_prefetcher ENTITY_CAPPING 133; filtered), abook#600 (CHAPTER_SPECIFICS).
                            The abook capture's own chapter batches (#549, #551) were mostly 304, so mgr's fresh ones are used.
    capping (all four book fixtures) abook#72  POST capping-api PutConsumption (startup answer; #587/#597/#628 are the same
                    allowance re-read)

Redaction: the bodies only, never headers. Account user ids -> wavee-listener, display names -> Listener, device names ->
wavee-device, device / connection ids -> zeros of the same length, inside protobuf too (re-encoded with correct lengths);
the capping entitlement's per-subscription uuid (field 1.4.7) -> 00000000-0000-0000-0000-000000000000; a podcast rating's
own-rating fields (anything but 1 and 3 in PODCAST_RATING) dropped; pathfinder `extensions` dropped; e-mail addresses ->
contact@example.com. Catalogue ids (shows, episodes, authors, images) are public and stay.

`state` is SYNTHETIC: never the account's resume points. It is computed from the catalogue data (durations, publish dates,
book order) as documented in each fixture's `_fixture.state`.
"""

import argparse
import base64
import datetime
import gzip
import json
import os
import re
import subprocess
import sys
import zipfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "src", "apps", "Wavee", "assets", "spotify", "fixtures", "podcast-pages")
NAMES = ["show-episodic", "show-serial", "book-titled", "book-tracks", "book-inprogress", "book-finished"]
ZERO_UUID = "00000000-0000-0000-0000-000000000000"

# ---------------------------------------------------------------------------------------------------------------- specs
TABLO, COLDFUSION = "spotify:show:4ZNGEoKCuO3X8a7dHLl13h", "spotify:show:1YEwCKoRz8fEDqheXB6UJ1"
SUBTLE, MANAGER = "spotify:show:4nWWkORsA650CoeCeAIi2K", "spotify:show:1BARGvPTODoBcEjiiXK70g"
SUBTLE_XM = ["abook#684", "abook#685", "abook#686", "abook#687", "abook#690", "abook#693", "abook#731", "abook#732", "abook#733"]
SPECS = {
    "show-episodic": dict(show=TABLO, items="vc1#692", xm=["vc1#80", "vc1#519", "vc1#693", "vc1#694", "vc1#695", "vc1#75",
                                                           "vc1#660", "vc1#701", "vc1#749"]),
    "show-serial": dict(show=COLDFUSION, items="vc1#623", xm=["vc1#80", "vc1#519", "vc1#624", "vc1#625", "vc1#626", "vc1#75",
                                                              "vc1#634", "vc1#636"]),
    "book-titled": dict(show=SUBTLE, items="abook#682", xm=SUBTLE_XM, capping="abook#72",
                        pathfinder={"similarAudiobooks": "abook-web#295"}),
    "book-tracks": dict(show=MANAGER, items="mgr#399", xm=["vc1#80", "mgr#398", "mgr#401", "mgr#402", "mgr#407", "mgr#413",
                                                           "abook#564", "abook#600"], capping="abook#72"),
    "book-inprogress": dict(show=SUBTLE, items="abook#682", xm=SUBTLE_XM, capping="abook#72"),
    "book-finished": dict(show=SUBTLE, items="abook#682", xm=SUBTLE_XM, capping="abook#72"),
}

# --------------------------------------------------------------------------------------------------------- decompression
try:
    from compression import zstd as _zstd  # Python 3.14

    def _zstd_native(b):
        return _zstd.decompress(b)
except ImportError:
    try:
        import zstandard as _zstandard

        def _zstd_native(b):
            return _zstandard.ZstdDecompressor().decompressobj().decompress(b)
    except ImportError:
        _zstd_native = None


def _node(kind, b):
    fn = {"zstd": "zstdDecompressSync", "br": "brotliDecompressSync"}[kind]
    r = subprocess.run(["node", "-e", "process.stdout.write(require('zlib').%s(require('fs').readFileSync(0)))" % fn],
                       input=b, capture_output=True)
    if r.returncode:
        raise RuntimeError("node %s failed: %s" % (fn, r.stderr.decode(errors="replace")[:200]))
    return r.stdout


def dechunk(b):
    out, i = b"", 0
    while i < len(b):
        j = b.find(b"\r\n", i)
        if j < 0:
            break
        n = int(b[i:j].split(b";")[0], 16)
        if n == 0:
            break
        out += b[j + 2:j + 2 + n]
        i = j + 2 + n + 2
    return out


def hget(hdrs, k):
    for a, v in hdrs:
        if a.lower() == k.lower():
            return v
    return None


def decode_body(hdrs, b):
    if (hget(hdrs, "transfer-encoding") or "").lower() == "chunked":
        b = dechunk(b)
    ce = (hget(hdrs, "content-encoding") or "").lower()
    if not b or ce in ("", "identity"):
        return b
    if ce == "gzip":
        return gzip.decompress(b)
    if ce == "deflate":
        return zlib.decompress(b)
    if ce == "zstd":
        return _zstd_native(b) if _zstd_native else _node("zstd", b)
    if ce == "br":
        return _node("br", b)
    raise ValueError("unknown content-encoding " + ce)


# -------------------------------------------------------------------------------------------------------------- captures
def _split(raw):
    h, _, b = raw.partition(b"\r\n\r\n")
    lines = h.decode("latin1").split("\r\n")
    hdrs = []
    for line in lines[1:]:
        k, _, v = line.partition(":")
        hdrs.append((k.strip(), v.strip()))
    return lines[0], hdrs, b


class Fiddler:
    """An extracted .saz (folder holding raw/) or the .saz zip itself."""

    def __init__(self, path):
        self.path = path
        if os.path.isdir(path):
            root = path if os.path.isdir(os.path.join(path, "raw")) else os.path.dirname(path)
            self._names = os.listdir(os.path.join(root, "raw"))
            self._read = lambda n: open(os.path.join(root, "raw", n), "rb").read()
        else:
            z = zipfile.ZipFile(path)
            self._names = [n[4:] for n in z.namelist() if n.startswith("raw/")]
            self._read = lambda n: z.read("raw/" + n)
        self._ids = {}
        for n in self._names:
            m = re.fullmatch(r"(\d+)_c\.txt", n)
            if m:
                self._ids[int(m.group(1))] = m.group(1)

    def ids(self):
        return sorted(self._ids)

    def request(self, i):
        rl, rh, rb = _split(self._read(self._ids[i] + "_c.txt"))
        return rl, rh, rb

    def response(self, i):
        name = self._ids[i] + "_s.txt"
        if name not in self._names:
            return "", [], b""
        sl, sh, sb = _split(self._read(name))
        return sl, sh, decode_body(sh, sb)


class VcDump:
    """A dump_all.py folder: index.tsv + bodies/NNNN_resp.* (decompressed)."""

    def __init__(self, path):
        self.path = path
        self.rows = {}
        for line in open(os.path.join(path, "index.tsv"), encoding="utf-8").read().splitlines()[1:]:
            c = line.split("\t")
            self.rows[int(c[0])] = c

    def ids(self):
        return sorted(self.rows)

    def request(self, i):
        c = self.rows[i]
        return "%s https://%s%s" % (c[2], c[3], c[4]), [], b""

    def response(self, i):
        c = self.rows[i]
        f = c[13] if len(c) > 13 else ""
        body = open(os.path.join(self.path, "bodies", f), "rb").read() if f else b""
        return "HTTP %s" % c[5], [], body


def open_captures(args):
    caps = {}
    for key, attr, cls in (("abook", "abook", Fiddler), ("abook-web", "abook_web", Fiddler), ("mgr", "mgr", Fiddler),
                           ("vc1", "vc1", VcDump), ("vc2", "vc2", VcDump)):
        p = getattr(args, attr, None)
        if p:
            caps[key] = cls(p)
    return caps


def body_of(caps, ref):
    cap, _, i = ref.partition("#")
    if cap not in caps:
        sys.exit("podcast-fixture: %s needs the --%s capture" % (ref, cap))
    sl, _, b = caps[cap].response(int(i))
    if not b:
        sys.exit("podcast-fixture: %s has no response body (%s)" % (ref, sl))
    return b


# -------------------------------------------------------------------------------------------------------------- protobuf
def varint(b, i):
    r = s = 0
    while True:
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        s += 7
        if not c & 0x80:
            return r, i


def parse(b):
    """[(field, wiretype, value)]: value is an int (0), or the raw bytes (1, 2, 5). Raises on anything malformed."""
    i, out = 0, []
    while i < len(b):
        key, i = varint(b, i)
        fn, wt = key >> 3, key & 7
        if fn == 0:
            raise ValueError("field 0")
        if wt == 0:
            v, i = varint(b, i)
        elif wt == 1:
            v, i = b[i:i + 8], i + 8
        elif wt == 5:
            v, i = b[i:i + 4], i + 4
        elif wt == 2:
            n, i = varint(b, i)
            v, i = b[i:i + n], i + n
        else:
            raise ValueError("wiretype %d" % wt)
        if i > len(b):
            raise ValueError("overrun")
        out.append((fn, wt, v))
    return out


def enc_varint(n):
    out = bytearray()
    while True:
        c = n & 0x7F
        n >>= 7
        out.append(c | (0x80 if n else 0))
        if not n:
            return bytes(out)


def encode(fields):
    out = bytearray()
    for fn, wt, v in fields:
        out += enc_varint(fn << 3 | wt)
        if wt == 0:
            out += enc_varint(v)
        elif wt == 2:
            out += enc_varint(len(v)) + v
        else:
            out += v
    return bytes(out)


def zigzag(n):
    return (n >> 1) ^ -(n & 1)


def fields(b):
    d = {}
    for fn, wt, v in parse(b):
        d.setdefault(fn, v)
    return d


def _is_text(v):
    try:
        s = v.decode("utf-8")
    except UnicodeDecodeError:
        return False
    return all(ch.isprintable() or ch in "\r\n\t" for ch in s)


def pb_replace(b, old, new):
    """Replace a byte string inside a protobuf message, re-encoding every enclosing length: text fields get the replacement,
    nested messages are walked, a value inside a fixed-width field or across field boundaries is an error (never left in)."""
    if old not in b:
        return b
    out = []
    for fn, wt, v in parse(b):
        if wt == 2 and old in v:
            if _is_text(v):
                v = v.replace(old, new)
            else:
                try:
                    parse(v)
                    is_msg = True
                except Exception:
                    is_msg = False
                v = pb_replace(v, old, new) if is_msg else v.replace(old, new)
        elif wt in (1, 5) and old in v:
            raise ValueError("a secret inside a fixed-width field")
        out.append((fn, wt, v))
    res = encode(out)
    if old in res:
        raise ValueError("a secret spans protobuf field boundaries")
    return res


# ------------------------------------------------------------------------------------------------------ extended metadata
def em_entities(b):
    """(kind, uri, status, type_url, value) for every entity of a BatchedExtensionResponse."""
    for fn, wt, v in parse(b):
        if fn != 2 or wt != 2:
            continue
        kind, ents = None, []
        for f2, w2, v2 in parse(v):
            if f2 == 2 and w2 == 0:
                kind = v2
            elif f2 == 3 and w2 == 2:
                ents.append(v2)
        for e in ents:
            st = uri = tu = val = None
            for f3, w3, v3 in parse(e):
                if f3 == 1 and w3 == 2:
                    st = fields(v3).get(1)
                elif f3 == 2:
                    uri = v3.decode()
                elif f3 == 3 and w3 == 2:
                    d = fields(v3)
                    tu, val = d.get(1, b"").decode(), d.get(2)
            yield kind, uri, st, tu, val


def _entity_uri(e):
    for f, w, v in parse(e):
        if f == 2:
            return v.decode()
    return None


def _scrub_rating(e, notes):
    """PODCAST_RATING (37): keep the public average (1) and the can-rate flag (3); drop the account's own rating."""
    out, changed = [], False
    for f3, w3, v3 in parse(e):
        if f3 == 3 and w3 == 2:
            anyf = []
            for a, wa, va in parse(v3):
                if a == 2 and va:
                    kept = [x for x in parse(va) if x[0] in (1, 3)]
                    if len(kept) != len(parse(va)):
                        changed = True
                        va = encode(kept)
                anyf.append((a, wa, va))
            v3 = encode(anyf)
        out.append((f3, w3, v3))
    if changed:
        notes.add("an own rating dropped from PODCAST_RATING")
        return encode(out)
    return e


def em_filter(b, keep, notes):
    """Keep only the entities whose uri is in `keep`. Returns (body, kept, dropped); an untouched answer is returned verbatim."""
    out, kept, dropped, changed = [], 0, 0, False
    for fn, wt, v in parse(b):
        if fn == 2 and wt == 2:
            arr, n_ent, kind = [], 0, None
            for f2, w2, v2 in parse(v):
                if f2 == 2 and w2 == 0:
                    kind = v2
                if f2 == 3 and w2 == 2:
                    if _entity_uri(v2) not in keep:
                        dropped += 1
                        changed = True
                        continue
                    if kind == 37:
                        v3 = _scrub_rating(v2, notes)
                        changed |= v3 is not v2
                        v2 = v3
                    n_ent += 1
                arr.append((f2, w2, v2))
            kept += n_ent
            if n_ent == 0:
                changed = True
                continue
            v = encode(arr)
        out.append((fn, wt, v))
    return (encode(out) if changed else b), kept, dropped


# --------------------------------------------------------------------------------------------------------- identities
USER_URI_RE = re.compile(r"spotify:user:([^:\"\s/&?,<>\\]+)")
# the account's OWN user id: the paths only the signed-in account requests (its rootlist, its collection, its profile)
OWN_USER_PATH_RE = re.compile(r"/(?:playlist/v2/user|collection/v2/[^/]+/user|user-profile-view/v3/profile|users)/"
                              r"(?!spotify\b)([^/?#\s]+)/(?:rootlist|collection|profile|playlists|followers|following)?")
DEVICE_PATH_RE = re.compile(r"/(?:devices|cache|from|to)/([0-9A-Za-z_-]{16,})")
EMAIL_RE = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")
UUID_RE = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
PUBLIC_USERS = {"spotify", "wavee-listener", "me", "@"}
# substrings that never belong in a fixture (case-insensitive), plus key-shaped credential words: a catalogue description may
# say "wifi passwords", it never says `password=` or `"password":`
CREDENTIAL_WORDS = ["Bearer ", "access_token", "accessToken", "client_token", "clientToken", "refresh_token", "auth_data",
                    "stored_credential", "sp_dc", "sp_key", "__Host-sp", "Set-Cookie", "id_token", "login5", "client_secret"]
CREDENTIAL_RE = re.compile(r"(?i)\b(?:password|passwd|authorization|cookie|token|secret|csrf|sp_t|credentials?)\b[\"']?\s*[:=]")
JWT_RE = re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")
LONG_RE = re.compile(r"[A-Za-z0-9_\-]{120,}")


def _walk_json(node, fn):
    if isinstance(node, dict):
        fn(node)
        for v in node.values():
            _walk_json(v, fn)
    elif isinstance(node, list):
        for v in node:
            _walk_json(v, fn)


def collect_identities(caps, display_names, device_names):
    """The account's own identifiers, learned from every session of every capture: its user id from the paths only it
    requests (rootlist, collection, own profile) and the `username` of profile answers about it; device / connection ids from
    paths, headers and JSON device fields; its display name from JSON objects naming that user id; device names from JSON
    device objects. Third-party users (playlist owners) are not collected: their uris are replaced by pattern anyway."""
    users, devices = set(), set()
    names, dnames = set(n for n in display_names if n), set(n for n in device_names if n)
    jsons = []
    for cap in caps.values():
        for i in cap.ids():
            try:
                rl, rh, rb = cap.request(i)
            except Exception:
                continue
            path = rl.split(" ")[1] if " " in rl else rl
            if "/rootlist" in path or "/collection/" in path:
                for u in OWN_USER_PATH_RE.findall(path):
                    u = re.sub(r"%3A", ":", u, flags=re.I).split(":")[-1]
                    if u not in PUBLIC_USERS and "{" not in u and "<" not in u:
                        users.add(u)
            devices.update(DEVICE_PATH_RE.findall(path))
            for k, v in rh:
                if k.lower() in ("x-spotify-connection-id", "spotify-connection-id", "x-client-id-device"):
                    devices.add(v)
            ct = (hget(rh, "content-type") or "") if rh else ""
            bodies = []
            if rb and ("json" in ct or rb[:1] in (b"{", b"[")):
                try:
                    bodies.append(decode_body(rh, rb))
                except Exception:
                    pass
            if "connect-state" in rl or "/me" in rl or "profile" in rl or "pathfinder" in rl:
                try:
                    _, _, sb = cap.response(i)
                    bodies.append(sb)
                except Exception:
                    pass
            for b in bodies:
                try:
                    jsons.append(json.loads(b.decode("utf-8")))
                except Exception:
                    pass

    def pick(d):
        for k in ("device_id", "deviceId", "installation_id", "installationId", "connection_id", "connectionId", "cache_id"):
            v = d.get(k)
            if isinstance(v, str) and len(v) >= 16:
                devices.add(v)
        if any(k in d for k in ("device_id", "deviceId", "device_type", "deviceType")):
            for k in ("name", "device_name", "deviceName"):
                v = d.get(k)
                if isinstance(v, str) and v:
                    dnames.add(v)

    def pick_names(d):
        own = [v for k, v in d.items() if k in ("uri", "username", "id", "userId", "user_id") and isinstance(v, str)
               and v.split(":")[-1] in users]
        if own:
            for k in ("display_name", "displayName", "name"):
                v = d.get(k)
                if isinstance(v, str) and v and v.lower() != "spotify" and v.split(":")[-1] not in users:
                    names.add(v)

    for j in jsons:
        _walk_json(j, pick)
        _walk_json(j, pick_names)
    devices = set(d for d in devices if len(d) >= 16)
    return users, names, devices, dnames


def name_re(name):
    return re.compile(r"(?<![0-9A-Za-z])" + re.escape(name) + r"(?![0-9A-Za-z])")


class Redactor:
    def __init__(self, users, names, devices, dnames):
        self.users = sorted(users, key=len, reverse=True)
        self.names = sorted(names, key=len, reverse=True)
        self.devices = sorted(devices, key=len, reverse=True)
        self.dnames = sorted(dnames, key=len, reverse=True)
        self.notes = set()

    def text(self, s):
        o = s
        for u in self.users:
            s = s.replace(u, "wavee-listener")
        for d in self.devices:
            s = s.replace(d, "0" * len(d))
        for n in self.dnames:
            s = s.replace(n, "wavee-device")
        for n in self.names:
            s = name_re(n).sub("Listener", s)
        s = USER_URI_RE.sub(lambda m: m.group(0) if m.group(1) in PUBLIC_USERS else "spotify:user:wavee-listener", s)
        s = EMAIL_RE.sub("contact@example.com", s)
        s = UUID_RE.sub(ZERO_UUID, s)
        if s != o:
            self.notes.add("ids/names in JSON text replaced")
        return s

    def json(self, node):
        if isinstance(node, dict):
            return {k: self.json(v) for k, v in node.items() if k != "extensions"}
        if isinstance(node, list):
            return [self.json(v) for v in node]
        if isinstance(node, str):
            return self.text(node)
        return node

    def pb(self, b):
        for u in self.users:
            b = pb_replace(b, u.encode(), b"wavee-listener")
        for d in self.devices:
            b = pb_replace(b, d.encode(), b"0" * len(d))
        for n in self.dnames:
            b = pb_replace(b, n.encode(), b"wavee-device")
        for e in sorted(set(EMAIL_RE.findall(b.decode("latin1"))) - {"contact@example.com"}):
            self.notes.add("e-mail addresses (catalogue contact lines) -> contact@example.com")
            b = pb_replace(b, e.encode("latin1"), b"contact@example.com")
        for n in self.names:
            for m in set(name_re(n).findall(b.decode("latin1"))):
                # whole-word hits only (a short first name must not eat catalogue words); reported, then replaced
                self.notes.add("a display-name word replaced inside protobuf")
                b = pb_replace(b, m.encode("latin1"), b"Listener")
        return b


# --------------------------------------------------------------------------------------------------------- the catalogue
def episode_info(xm_bodies):
    info = {}
    for b in xm_bodies:
        for kind, uri, st, tu, val in em_entities(b):
            if kind != 12 or not val:
                continue
            d = fields(val)
            pub = None
            if 66 in d:  # publish_time: a Date whose fields are zig-zag sint32 too (raw year 4052 = 2026)
                dt = {k: zigzag(v) for k, v in fields(d[66]).items()}
                try:
                    pub = int(datetime.datetime(dt.get(1, 1970), dt.get(2, 1), dt.get(3, 1), dt.get(4, 0), dt.get(5, 0),
                                                tzinfo=datetime.timezone.utc).timestamp() * 1000)
                except Exception:
                    pub = None
            info[uri] = dict(name=d.get(2, b"").decode("utf-8", "replace"), duration=zigzag(d.get(7, 0)),
                             number=zigzag(d[65]) if 65 in d else None, published=pub, trailer=d.get(87, 0) == 1)
    return info


def show_title(xm_bodies, show):
    for b in xm_bodies:
        for kind, uri, st, tu, val in em_entities(b):
            if kind == 11 and uri == show and val:
                return fields(val).get(2, b"").decode()
    return "?"


def list_items(body):
    return [u.decode() for u in dict.fromkeys(re.findall(rb"spotify:episode:[0-9A-Za-z]{22}", body))]


def strip_grpc_frame(b):
    if len(b) >= 5 and b[0] in (0, 1) and int.from_bytes(b[1:5], "big") == len(b) - 5:
        return b[5:]
    return b


def redact_capping(b):
    """PutConsumption: every feature block's 1.4.7 (the per-subscription uuid) -> the zero uuid."""
    out = []
    for fn, wt, v in parse(b):
        if fn == 1 and wt == 2:
            blk = []
            for f2, w2, v2 in parse(v):
                if f2 == 4 and w2 == 2:
                    v2 = encode([(a, w, ZERO_UUID.encode() if a == 7 and w == 2 else x) for a, w, x in parse(v2)])
                blk.append((f2, w2, v2))
            v = encode(blk)
        out.append((fn, wt, v))
    return encode(out)


def make_state(name, items, info):
    pct = lambda u, p: int(info[u]["duration"] * p)
    if name == "show-episodic":
        by_new = sorted(items, key=lambda u: info[u]["published"] or 0, reverse=True)
        t3, t4 = info[by_new[2]]["published"], info[by_new[3]]["published"]
        last = (t3 + t4) // 2
        assert sum(1 for u in items if (info[u]["published"] or 0) > last) == 3
        resume = {by_new[3]: pct(by_new[3], 0.40), by_new[4]: pct(by_new[4], 0.75)}
        finished = by_new[5:9]
        what = ("lastVisitUnixMs sits between the 3rd and 4th newest publish dates (exactly 3 episodes are newer = 'new since "
                "your last visit'); the 4th and 5th newest are resumed at 40 % and 75 % of duration_ms; the 6th-9th newest "
                "are finished; the show is saved")
        return dict(resume=resume, finished=finished, saved=True, lastVisitUnixMs=last), what
    if name == "show-serial":
        by_old = sorted(reversed(items), key=lambda u: info[u]["published"] or 0)  # ties: list order, reversed
        resume = {by_old[3]: pct(by_old[3], 0.30)}
        what = ("serial order = oldest first (publish date): episodes 1-3 finished, episode 4 resumed at 30 % of duration_ms; "
                "saved; no last visit")
        return dict(resume=resume, finished=by_old[:3], saved=True, lastVisitUnixMs=0), what
    real = [u for u in items if not info[u]["trailer"]]
    if name in ("book-titled", "book-tracks"):
        what = "nothing started or finished; saved %s" % ("false" if name == "book-titled" else "true")
        return dict(resume={}, finished=[], saved=name == "book-tracks", lastVisitUnixMs=0), what
    if name == "book-inprogress":
        what = ("the first 3 chapters in book order (the sample/trailer excluded) finished, the 4th resumed at 50 % of its "
                "duration; saved")
        return dict(resume={real[3]: pct(real[3], 0.50)}, finished=real[:3], saved=True, lastVisitUnixMs=0), what
    if name == "book-finished":
        what = "every item of the chapter list finished (the sample included); saved"
        return dict(resume={}, finished=list(items), saved=True, lastVisitUnixMs=0), what
    raise KeyError(name)


# ------------------------------------------------------------------------------------------------------------- build
def build(args):
    caps = open_captures(args)
    missing = [k for k in ("abook", "abook-web", "mgr", "vc1", "vc2") if k not in caps]
    if missing:
        sys.exit("podcast-fixture build: needs every capture; missing --%s" % ", --".join(missing))
    users, names, devices, dnames = collect_identities(caps, args.display_name, args.device_name)
    os.makedirs(args.out, exist_ok=True)
    for name in NAMES:
        spec = SPECS[name]
        red = Redactor(users, names, devices, dnames)
        items = list_items(body_of(caps, spec["items"]))
        keep = set(items) | {spec["show"]}
        xm, kept_total = [], 0
        for ref in spec["xm"]:
            b, kept, dropped = em_filter(body_of(caps, ref), keep, red.notes)
            if kept:
                b = red.pb(b)
                em_list = list(em_entities(b))  # must still decode
                assert em_list
                xm.append(b)
                kept_total += kept
        info = episode_info(xm)
        lost = [u for u in items if u not in info]
        if lost:
            sys.exit("podcast-fixture: %s: %d item(s) without an EPISODE_V4 body, e.g. %s" % (name, len(lost), lost[0]))
        if name != "show-episodic" and name != "show-serial":
            # a book's list order is its book order; double-check it against Episode.65
            nums = [info[u]["number"] for u in items if info[u]["number"] is not None and not info[u]["trailer"]]
            if nums != sorted(nums):
                print("  note: %s list order differs from Episode.65 order" % name)
        state, what = make_state(name, items, info)
        doc = {"_fixture": {
            "kind": name,
            "source": "items %s; xm %s%s%s (sessions: see tools/podcast-fixture.py docstring)" % (
                spec["items"], ", ".join(spec["xm"]),
                "; capping %s" % spec["capping"] if "capping" in spec else "",
                "".join("; pathfinder %s %s" % kv for kv in spec.get("pathfinder", {}).items())),
            "redaction": "",
            "state": "synthetic: " + what,
        }, "show": spec["show"], "items": items, "xm": [base64.b64encode(b).decode() for b in xm]}
        if "pathfinder" in spec:
            pf = {}
            for op, ref in spec["pathfinder"].items():
                pf[op] = red.json(json.loads(body_of(caps, ref).decode("utf-8")))
            doc["pathfinder"] = pf
        if "capping" in spec:
            cb = redact_capping(strip_grpc_frame(body_of(caps, spec["capping"])))
            doc["capping"] = base64.b64encode(red.pb(cb)).decode()
            red.notes.add("capping 1.4.7 subscription uuid -> zero uuid")
        doc["state"] = state
        doc["_fixture"]["redaction"] = ("bodies only (no headers, cookies or tokens); extended-metadata answers filtered to this "
                                        "show and its episodes; account ids, display/device names, device ids scanned for "
                                        "and replaced (wavee-listener / Listener / zeros), pathfinder extensions dropped"
                                        + ("; " + "; ".join(sorted(red.notes)) if red.notes else ""))
        dest = os.path.join(args.out, name + ".json")
        with open(dest, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(doc, fh, ensure_ascii=False, separators=(",", ":"))
        kinds = sorted(set(k for b in xm for k, u, st, tu, v in em_entities(b) if v))
        ex = [info[u]["name"] for u in items[:3]]
        print("%-16s %s %r: %d items, %d xm bodies (%d entities, kinds with bodies %s)%s%s; e.g. %s" % (
            name, spec["show"], show_title(xm, spec["show"]), len(items), len(xm), kept_total, kinds,
            ", pathfinder " + "+".join(doc["pathfinder"]) if "pathfinder" in doc else "",
            ", capping" if "capping" in doc else "", ex))
    return leakcheck(args, caps=caps, ids=(users, names, devices, dnames))


# --------------------------------------------------------------------------------------------------------- leakcheck
def _scan_text(s, ids, where, leaks):
    users, names, devices, dnames = ids
    for u in users:
        if u in s:
            leaks.append((where, "the account's user id"))
    for n in names:
        if name_re(n).search(s):
            leaks.append((where, "a display name (%d chars)" % len(n)))
    for d in devices:
        if d in s:
            leaks.append((where, "a device/connection id"))
    for n in dnames:
        if n.lower() in s.lower():
            leaks.append((where, "a device name"))
    for u in set(USER_URI_RE.findall(s)) - PUBLIC_USERS:
        leaks.append((where, "a user uri that is not the stand-in"))
    for e in set(EMAIL_RE.findall(s)) - {"contact@example.com"}:
        leaks.append((where, "an e-mail address"))
    for u in set(UUID_RE.findall(s)) - {ZERO_UUID}:
        leaks.append((where, "a uuid"))
    low = s.lower()
    for w in CREDENTIAL_WORDS:
        if w.lower() in low:
            leaks.append((where, "the credential word '%s'" % w.strip()))
    for m in set(CREDENTIAL_RE.findall(s)):
        leaks.append((where, "a credential key '%s'" % m.strip()))
    if JWT_RE.search(s):
        leaks.append((where, "a JWT"))
    if LONG_RE.search(s):
        leaks.append((where, "a long opaque token-like run"))


def leakcheck(args, caps=None, ids=None):
    if ids is None:
        caps = open_captures(args)
        ids = collect_identities(caps, args.display_name, args.device_name) if caps else (
            set(), set(a for a in args.display_name if a), set(), set(a for a in args.device_name if a))
    leaks, files = [], [os.path.join(args.out, n + ".json") for n in NAMES]
    files = [f for f in files if os.path.exists(f)]
    if not files:
        print("leakcheck: no podcast-page fixture written yet in %s" % os.path.normpath(args.out))
        return 2
    for path in files:
        base = os.path.basename(path)
        doc = json.load(open(path, encoding="utf-8"))
        blobs = list(doc.get("xm", []))
        if doc.get("capping"):
            blobs.append(doc["capping"])
        plain = dict(doc)
        plain["xm"], plain["capping"] = [], ""
        _scan_text(json.dumps(plain, ensure_ascii=False), ids, base, leaks)
        for k, b64 in enumerate(blobs):
            raw = base64.b64decode(b64)
            _scan_text(raw.decode("latin1"), ids, "%s blob %d" % (base, k), leaks)
            try:
                parse(raw)
            except Exception:
                leaks.append(("%s blob %d" % (base, k), "a body that does not decode as protobuf"))
        if not ids[0] and caps:
            leaks.append((base, "no account user id learned from the captures (wrong capture paths?)"))
    for where, what in leaks:  # never print a leaked value: only what kind of thing leaked, and where
        print("LEAK %s: %s" % (where, what))
    print("leakcheck podcast-pages: %d file%s, %d leak%s, %s -> %s" % (
        len(files), "" if len(files) == 1 else "s", len(leaks), "" if len(leaks) == 1 else "s",
        "%d account ids, %d names, %d device ids, %d device names learned" % tuple(len(x) for x in ids) if caps else
        "generic patterns only (no capture given)", "FAIL (do not commit)" if leaks else "clean"))
    return 1 if leaks else 0


def main():
    ap = argparse.ArgumentParser(description="Build the redacted podcast/audiobook page fixtures from local captures; leak-check them.")
    ap.add_argument("verb", choices=["build", "leakcheck"])
    ap.add_argument("--abook", help="audiobookssss.saz extracted (folder holding raw/)")
    ap.add_argument("--abook-web", dest="abook_web", help="bbbb.saz extracted (folder holding raw/)")
    ap.add_argument("--mgr", help="themanagerpath.saz")
    ap.add_argument("--vc1", help="fresh-client-2026-09-19/vc1 dump folder")
    ap.add_argument("--vc2", help="fresh-client-2026-09-19/vc2 dump folder")
    ap.add_argument("--display-name", action="append", default=[], help="the account's display name(s), when the captures hide them")
    ap.add_argument("--device-name", action="append", default=[], help="the account's device name(s)")
    ap.add_argument("--out", default=OUT, help="where the fixtures go (default: assets/spotify/fixtures/podcast-pages)")
    args = ap.parse_args()
    sys.exit(build(args) if args.verb == "build" else leakcheck(args))


if __name__ == "__main__":
    main()
