"""Shared driver for the page-mount perf scripts: a `Wavee.exe --inspect` session (tools/inspect.py) plus a trace-row printer.
WAVEE_EXE picks the build (measure a Release one; see docs/guide/agent-inspect.md)."""
import importlib.util, json, os, sys, time
spec = importlib.util.spec_from_file_location("ic", os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "inspect.py"))
ic = importlib.util.module_from_spec(spec); spec.loader.exec_module(ic)
EXE = os.environ.get("WAVEE_EXE") or ic.find_exe(None)
class Sess:
    def __init__(self):
        self.srv = ic.Server(EXE, extra=os.environ.get("INSPECT_EXTRA","").split())
    def cmd(self, line):
        r = self.srv.send(line if isinstance(line,str) else json.dumps(line)); return json.loads(r) if r else None
    def go(self, to, **targs):
        a={"cmd":"goto","args":{}}
        if ':' in to: a['args']['uri']=to
        else: a['args']['route']=to
        if targs: a['args'].update(dict(settle=False, **targs))
        r=self.cmd(a)
        if not targs: self.cmd("settle max=200")
        return r
    def close(self): self.srv.close()
def rows(r, thr=4):
    t=r.get('trace') or r
    for row in t['rows']:
        m=row['ms']; n=row['n']
        if m['cpu']>thr:
            print("f%-3d cpu %5.1f flush %5.1f rflush %4.1f vreal %4.1f layout %4.1f solve %4.1f record %4.1f anim %4.1f | " %(row['f'],m['cpu'],m['flush'],m.get('reactiveFlush',0),m.get('virtualRealize',0),m['layout'],m.get('solve',0),m['record'],m['anim']),
                  " ".join("%s=%s"%(k,v) for k,v in n.items() if v))
            if row.get('census'): print("    census:", json.dumps(row['census'])[:2500])
