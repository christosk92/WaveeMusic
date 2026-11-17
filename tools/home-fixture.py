"""Turn the owner's own Home answer into a redacted Home fixture (home=live2, ...), and prove it leaks nothing.

The raw answers come from a VERIFY Wavee on a profile COPY: `wavee://diag?cmd=homecap&tag=<tag>` writes every facet's raw answer
(home.<facet>.json) and its baseline lookup (lookup.<facet>.json) to <profile>/logs/evidence/home-<stamp>-<tag>/. Those bodies are the
account's own and never leave the machine as they are. This tool:

    python -I tools/home-fixture.py redact <evidence home dir> --name live2 [--display-name "<the account's display name>"]
        writes src/apps/Wavee/assets/spotify/fixtures/home.<facet>.live2.json (All: home.all.live2.json) and lookup.<facet>.live2.json,
        redacted: the account's user ids -> spotify:user:wavee-listener, its display names (and every title that carries them,
        "Made For <name>") -> Listener, its personal mix ids (37i9dQZF1E..., the Daily Mixes and Your Mixes) -> stable stand-ins of the
        same shape, user avatars and the response's extensions dropped, and a `_fixture` header saying what was done;
    python -I tools/home-fixture.py leakcheck <evidence home dir> --name live2 [--display-name "..."]
        exits 1 when a written fixture still holds any of the account's ids, display names, personal mix ids, an e-mail address or
        a credential word; 0 when it is clean. Run it before `git add`: a fixture that fails it never enters the public repo.

Then add the name to Entities.HomeFixtures (Entities/Entities.Fake.Home.cs) and to mount's `home` choices
(Screens/Diagnostics.Inspect.Protocol.cs), and mount it: `mount ... home=live2`, `home do=explain`.
The whole procedure, with its safety rules, is the fgpu skill's verify/debug-home.md "Real-data repro".
"""

import argparse
import hashlib
import json
import os
import re
import sys

FACETS = ["all", "music-chip", "music-following-chip", "podcasts-chip", "podcasts-following-chip", "audiobooks-chip"]
HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "..", "src", "apps", "Wavee", "assets", "spotify", "fixtures")
USER_RE = re.compile(r"spotify:user:([^:\"\s]+)")
MIX_RE = re.compile(r"37i9dQZF1E[0-9A-Za-z]{12}")
EMAIL_RE = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
CREDENTIAL_WORDS = ["Bearer ", "access_token", "client_token", "refresh_token", "authorization"]
PUBLIC_USERS = {"spotify", "@", "wavee-listener"}
B62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"


def name_re(name):
    """A display name as a whole word (no letter or digit on either side)."""
    return re.compile(r"(?<![0-9A-Za-z])" + re.escape(name) + r"(?![0-9A-Za-z])")


def stand_in(mix_id):
    """A stable stand-in of the same shape for a personal mix id (the prefix kept: the app reads it as a personal mix)."""
    h = int.from_bytes(hashlib.sha256(mix_id.encode()).digest()[:12], "big")
    tail = ""
    for _ in range(12):
        h, r = divmod(h, 62)
        tail += B62[r]
    return "37i9dQZF1E" + tail


def raw_files(src):
    for facet in FACETS:
        for kind in ("home", "lookup"):
            path = os.path.join(src, "%s.%s.json" % (kind, facet))
            if os.path.exists(path):
                yield kind, facet, path


def collect(src, display_names):
    """The account's identifiers in the raw answers: user ids, display names, personal mix ids."""
    users, names, mixes = set(), set(n for n in display_names if n), set()
    for _, _, path in raw_files(src):
        text = open(path, encoding="utf-8").read()
        users.update(u for u in USER_RE.findall(text) if u not in PUBLIC_USERS)
        mixes.update(MIX_RE.findall(text))

        def walk(node):
            if isinstance(node, dict):
                if node.get("__typename") == "User" and any(u in json.dumps(node) for u in users):
                    for k in ("name", "displayName", "username"):
                        if isinstance(node.get(k), str) and node[k] and node[k].lower() != "spotify":
                            names.add(node[k])
                for v in node.values():
                    walk(v)
            elif isinstance(node, list):
                for v in node:
                    walk(v)

        walk(json.loads(text))
    return users, names, mixes


def redact_text(s, users, names, mixes):
    for u in users:
        s = s.replace("spotify:user:" + u, "spotify:user:wavee-listener").replace(u, "wavee-listener")
    # Whole words only: a short first name ("Al") must not eat the middle of other words ("Album").
    for n in sorted(names, key=len, reverse=True):
        s = name_re(n).sub("Listener", s)
    for m in mixes:
        s = s.replace(m, stand_in(m))
    # Any e-mail address (a show's contact line in its description): never the account's, but none enters a fixture.
    return EMAIL_RE.sub("contact@example.com", s)


def redact_node(node, users, names, mixes):
    if isinstance(node, dict):
        out = {}
        for k, v in node.items():
            if k in ("extensions", "avatar") or (k == "images" and node.get("__typename") == "User"):
                continue
            out[k] = redact_node(v, users, names, mixes)
        return out
    if isinstance(node, list):
        return [redact_node(v, users, names, mixes) for v in node]
    if isinstance(node, str):
        return redact_text(node, users, names, mixes)
    return node


def out_name(kind, facet, name):
    return "%s.%s.%s.json" % (kind, facet, name)


def redact(args):
    users, names, mixes = collect(args.src, args.display_name)
    if not users:
        sys.exit("home-fixture: no account user id found in %s (is it a homecap folder?)" % args.src)
    written = []
    for kind, facet, path in raw_files(args.src):
        doc = json.load(open(path, encoding="utf-8"))
        body = redact_node(doc, users, names, mixes)
        if isinstance(body, dict):
            body.pop("extensions", None)
            body = {"_fixture": {
                "source": "Wavee live answer (cmd=homecap), facet %s, redacted by tools/home-fixture.py" % ("\"\"" if facet == "all" else facet),
                "redacted": "account user ids, display names, personal mix ids and e-mail addresses replaced; user avatars and response extensions dropped",
            }, **{k: v for k, v in body.items() if k != "_fixture"}}
        dest = os.path.join(args.out, out_name(kind, facet, args.name))
        with open(dest, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(body, fh, ensure_ascii=False, separators=(",", ":"))
        written.append(dest)
    print("redacted %d file%s: %d user id%s, %d display name%s, %d personal mix id%s replaced" % (
        len(written), "" if len(written) == 1 else "s", len(users), "" if len(users) == 1 else "s",
        len(names), "" if len(names) == 1 else "s", len(mixes), "" if len(mixes) == 1 else "s"))
    for w in written:
        print("  " + os.path.normpath(w))
    return leakcheck(args)


def leakcheck(args):
    users, names, mixes = collect(args.src, args.display_name)
    leaks = []
    files = [os.path.join(args.out, out_name(k, f, args.name)) for k, f, _ in raw_files(args.src)]
    files = [f for f in files if os.path.exists(f)]
    if not files:
        print("leakcheck: no %s fixture written yet in %s" % (args.name, os.path.normpath(args.out)))
        return 2
    for path in files:
        text = open(path, encoding="utf-8").read()
        for u in users:
            if u in text:
                leaks.append((path, "the account's user id"))
        for n in names:
            if name_re(n).search(text):
                leaks.append((path, "a display name (%d chars)" % len(n)))
        for m in mixes:
            if m in text:
                leaks.append((path, "a personal mix id"))
        for u in set(USER_RE.findall(text)) - PUBLIC_USERS:
            leaks.append((path, "a user uri that is not a stand-in"))
        for e in set(EMAIL_RE.findall(text)) - {"contact@example.com"}:
            leaks.append((path, "an e-mail address"))
        for w in CREDENTIAL_WORDS:
            if w.lower() in text.lower():
                leaks.append((path, "the credential word '%s'" % w.strip()))
    # never print a leaked value: only what kind of thing leaked, and where
    for path, what in leaks:
        print("LEAK %s: %s" % (os.path.basename(path), what))
    print("leakcheck %s: %d file%s, %d leak%s -> %s" % (args.name, len(files), "" if len(files) == 1 else "s", len(leaks),
                                                       "" if len(leaks) == 1 else "s", "FAIL (do not commit)" if leaks else "clean"))
    return 1 if leaks else 0


def main():
    ap = argparse.ArgumentParser(description="Redact a homecap evidence folder into a Home fixture and leak-check it.")
    ap.add_argument("verb", choices=["redact", "leakcheck"])
    ap.add_argument("src", help="the evidence folder cmd=homecap wrote (<profile>/logs/evidence/home-<stamp>-<tag>)")
    ap.add_argument("--name", required=True, help="the fixture name (live2): files home.<facet>.<name>.json")
    ap.add_argument("--display-name", action="append", default=[], help="the account's display name(s), when the answer does not carry them")
    ap.add_argument("--out", default=FIXTURES, help="where the fixture files go (default: the app's assets/spotify/fixtures)")
    args = ap.parse_args()
    if not re.fullmatch(r"[a-z][a-z0-9]{1,15}", args.name) or args.name in ("capture", "mock"):
        ap.error("--name is a short lowercase word that is not capture or mock (live2)")
    sys.exit(redact(args) if args.verb == "redact" else leakcheck(args))


if __name__ == "__main__":
    main()
