"""Render the Zune design boards (.dc.html) to PNG with headless Edge: no window, no network.
usage: python -I board_shots.py <boards dir with support.js> <out dir> [name-substring ...]
support.js is the design canvas runtime (artifact-type/dc-runtime.js of the Zune canvas), copied next to the boards."""
import json, os, re, subprocess, sys
EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
src, out = sys.argv[1], sys.argv[2]
want = sys.argv[3:]
os.makedirs(out, exist_ok=True)
prof = os.path.join(out, ".edge-profile")
for f in sorted(os.listdir(src)):
    if not f.endswith(".dc.html") or (want and not any(w in f for w in want)):
        continue
    html = open(os.path.join(src, f), encoding="utf-8").read()
    m = re.search(r'"\$preview":\{"width":(\d+),"height":(\d+)\}', html)
    w, h = (int(m.group(1)), int(m.group(2))) if m else (1440, 1000)
    png = os.path.join(out, f[:-8] + ".png")
    subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--user-data-dir=" + prof,
                    "--allow-file-access-from-files", "--virtual-time-budget=5000", f"--window-size={w},{h}",
                    "--screenshot=" + png, "file:///" + os.path.join(src, f).replace("\\", "/")],
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
    print(f"{f[:-8]} {w}x{h} {'ok' if os.path.exists(png) else 'FAILED'}")
