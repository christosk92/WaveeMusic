"""Release before/after numbers for the page-mount hitch: median CPU of the mount frame over N fresh mounts and the
median record ms of the kept-alive fade frames. Usage: python -I tools/perf/mount_meas.py zune 10"""
import sys, os, statistics as st; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
os.environ.pop("FGPU_PROF", None)
from mountdrv import *
theme=sys.argv[1]; n=int(sys.argv[2])
s=Sess(); mounts=[]; fades=[]; ka=[]
try:
    s.cmd("mount w=1440 h=900 theme=%s route=home"%theme); s.cmd("settle max=120")
    pls=["spotify:playlist:pl%d"%i for i in range(5)]
    for k in range(2):
        for p in pls: s.go(p); s.go("home")
    for k in range(n):
        r=s.go(pls[k%5], trace=40); rows_=(r.get('trace') or r)['rows']
        m=max(rows_, key=lambda x:x['ms']['cpu']); mounts.append((m['ms']['cpu'],m['ms']['flush'],m['ms']['layout'],m['ms']['record'],m['n'].get('gc0',0)))
        r=s.go("home", trace=40); rows_=(r.get('trace') or r)['rows']
        ka += [x['ms']['record'] for x in rows_[1:20] if x['n'].get('nodesVisited',0)>1000]
finally: s.close()
def med(i, nogc=False): v=[m[i] for m in mounts if not (nogc and m[4])]; return st.median(v) if v else float('nan')
print(theme,"mount frames:",n," gc in",sum(1 for m in mounts if m[4]))
print(" median cpu %.1f (no-GC frames %.1f) flush %.1f layout %.1f record %.1f"%(med(0),med(0,True),med(1),med(2),med(3)))
print(" all cpu:", " ".join("%.1f%s"%(m[0],"*" if m[4] else "") for m in mounts))
print(" kept-alive fade record/frame median %.2f (n=%d)"%(st.median(ka) if ka else 0,len(ka)))
