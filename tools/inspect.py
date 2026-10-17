"""inspect.py - a tiny client for `Wavee.exe --inspect` (docs/guide/agent-inspect.md).

Starts the server (stdin/stdout, or a named pipe with --pipe), waits for its `ready` line, sends each command as one line
and prints the one reply line it gets back. Standard library only; runs under `python -I`.

    python -I tools/inspect.py -c "mount w=400 h=860 theme=zune" -c "tiers" -c "dump root=zune-band keysOnly=true"
    python -I tools/inspect.py -f audit.jsonl --pretty
    python -I tools/inspect.py                      # interactive: type commands, Ctrl+Z / Ctrl+D to end

Commands are the server's own grammar: a JSON object {"cmd","args","id"} or a text line `verb key=value ...`.
"""

import argparse
import glob
import json
import os
import subprocess
import sys
import time


def find_exe(explicit):
    if explicit:
        return explicit
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    pattern = os.path.join(root, "src", "apps", "Wavee", "bin", "*", "*", "Wavee.exe")
    found = sorted(glob.glob(pattern), key=os.path.getmtime, reverse=True)
    if not found:
        sys.exit("inspect.py: no Wavee.exe under src/apps/Wavee/bin (build it, or pass --exe)")
    return found[0]


class Server:
    def __init__(self, exe, pipe=None, profile=None, text=None):
        argv = [exe, "--inspect"]
        if pipe:
            argv += ["--pipe", pipe]
        if profile:
            argv += ["--profile", profile]
        if text:
            argv += ["--text", text]
        self.proc = subprocess.Popen(argv, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     bufsize=0)
        self.out = self.proc.stdout
        self.inp = self.proc.stdin
        self.pipe = None
        if pipe:
            line = self._read_line()   # {"kind":"listening","pipe":"\\\\.\\pipe\\name"}
            path = json.loads(line)["pipe"]
            for _ in range(100):
                try:
                    self.pipe = open(path, "r+b", buffering=0)
                    break
                except OSError:
                    time.sleep(0.05)
            if self.pipe is None:
                sys.exit("inspect.py: could not open " + path)
            self.out = self.pipe
            self.inp = self.pipe
        self.ready = json.loads(self._read_line())
        if self.ready.get("kind") != "ready":
            sys.exit("inspect.py: the server did not come up: " + json.dumps(self.ready))

    def _read_line(self):
        buf = bytearray()
        while True:
            ch = self.out.read(1)
            if not ch:
                err = self.proc.stderr.read().decode("utf-8", "replace") if self.proc.poll() is not None else ""
                sys.exit("inspect.py: the server closed the stream. " + err)
            if ch == b"\n":
                return buf.decode("utf-8").rstrip("\r")
            buf += ch

    def send(self, line):
        line = line.strip()
        if not line or line.startswith("#"):
            return None
        self.inp.write((line + "\n").encode("utf-8"))
        self.inp.flush()
        return self._read_line()

    def close(self):
        try:
            self.send('{"cmd":"quit"}')
        except SystemExit:
            pass
        try:
            self.proc.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.proc.kill()


def show(reply, pretty):
    if reply is None:
        return
    if pretty:
        print(json.dumps(json.loads(reply), indent=2, ensure_ascii=False))
    else:
        print(reply)
    sys.stdout.flush()


def main():
    ap = argparse.ArgumentParser(description="Drive Wavee.exe --inspect.")
    ap.add_argument("--exe", help="path to Wavee.exe (default: the newest build under src/apps/Wavee/bin)")
    ap.add_argument("--pipe", help="talk over \\\\.\\pipe\\<name> instead of stdin/stdout")
    ap.add_argument("--profile", help="scratch profile folder (default: a temp folder the server deletes)")
    ap.add_argument("--text", choices=["dwrite", "headless"], help="text metrics (default dwrite)")
    ap.add_argument("-c", "--command", action="append", default=[], help="a command line; repeatable")
    ap.add_argument("-f", "--file", help="a file of command lines (JSON lines or text lines)")
    ap.add_argument("--pretty", action="store_true", help="indent the replies")
    ap.add_argument("--fail-fast", action="store_true", help="stop at the first ok:false reply (exit 2)")
    args = ap.parse_args()

    server = Server(find_exe(args.exe), args.pipe, args.profile, args.text)
    show(json.dumps(server.ready), args.pretty)
    code = 0
    try:
        lines = list(args.command)
        if args.file:
            with open(args.file, encoding="utf-8") as fh:
                lines += fh.read().splitlines()
        source = lines if (lines or args.file) else sys.stdin
        for line in source:
            reply = server.send(line)
            show(reply, args.pretty)
            if reply and args.fail_fast and not json.loads(reply).get("ok", False):
                code = 2
                break
    finally:
        server.close()
    sys.exit(code)


if __name__ == "__main__":
    main()
