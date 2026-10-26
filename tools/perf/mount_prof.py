"""Per-phase exclusive profile of fresh page mounts (home -> playlist plN, keep-alive evicted) and the kept-alive return.
Sets FGPU_PROF (engine Hosting/MountProf.cs) and prints each traced transition's slow rows plus the MountProf lines.
Usage: python -I tools/perf/mount_prof.py [zune|classic] [reps]   (THR=ms row threshold, default 6)"""
import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
PF=os.path.abspath("prof.txt"); os.environ["FGPU_PROF"]=PF
from mountdrv import *
theme=sys.argv[1] if len(sys.argv)>1 else "zune"; reps=int(sys.argv[2]) if len(sys.argv)>2 else 3
s=Sess()
def grab():
    t=open(PF).read() if os.path.exists(PF) else ""; open(PF,"w").close(); return t
try:
    s.cmd("mount w=1440 h=900 theme=%s route=home"%theme); s.cmd("settle max=120")
    pls=["spotify:playlist:pl%d"%i for i in range(5)]
    for k in range(2):
        for p in pls: s.go(p); s.go("home")
    for k in range(reps):
        p=pls[k%5]; grab()
        r=s.go(p, trace=60); print("=== home ->",p); rows(r,float(os.environ.get("THR","6"))); print(grab())
        r=s.go("home", trace=60); print("=== ->home (kept alive)"); rows(r,float(os.environ.get("THR","6"))); print(grab())
finally: s.close()
