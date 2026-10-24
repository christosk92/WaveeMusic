"""Page-accurate map of a running process's memory, read from outside (read-only: VirtualQueryEx + QueryWorkingSetEx).

Works on any build, NativeAOT included (dotnet-gcdump cannot read an AOT process). Every committed page is counted once and
assigned to one bucket:

  gc        the managed heap: private committed pages inside the GC's reservation (the largest private reservation, >= 1 GB)
  stack     thread stacks (a private reservation holding a PAGE_GUARD page)
  heap:N    a Win32 heap (CreateToolhelp32Snapshot heap list; segment bases)
  image:X   a loaded module X (copy-on-write and relocated pages are private)
  mapped:X  a file mapping X, or mapped:<anon> (pagefile-backed sections: D3D/DXGI CPU-visible GPU memory on UMA lands here)
  private   any other private committed allocation (driver allocations, native buffers, VirtualAlloc'd pools)

Per bucket: committed bytes, resident bytes, resident private bytes (Valid && !Shared) and the commit charge (private
committed + written copy-on-write pages). Totals check against the OS counters (PrivateUsage, WorkingSetPrivate).

Usage: python -I tools/perf/memmap.py <pid> [--top N] [--json out.json] [--allocs N]
  --allocs N   also list the N largest private allocations (base, reserved, committed, resident, protection, and the
               share of their resident pages that are all zero: memory committed and touched but holding nothing)
"""
import ctypes as C, ctypes.wintypes as W, json, sys
from collections import defaultdict

k32 = C.WinDLL("kernel32", use_last_error=True)
psapi = C.WinDLL("psapi", use_last_error=True)

MEM_COMMIT, MEM_RESERVE, MEM_FREE = 0x1000, 0x2000, 0x10000
MEM_PRIVATE, MEM_MAPPED, MEM_IMAGE = 0x20000, 0x40000, 0x1000000
PAGE_GUARD, PAGE_WRITECOPY, PAGE_EXECUTE_WRITECOPY = 0x100, 0x08, 0x80
PROCESS_QUERY_INFORMATION, PROCESS_VM_READ = 0x0400, 0x0010
TH32CS_SNAPHEAPLIST = 0x1
PAGE = 4096


class MBI(C.Structure):
    _fields_ = [("BaseAddress", C.c_void_p), ("AllocationBase", C.c_void_p), ("AllocationProtect", W.DWORD),
                ("PartitionId", W.WORD), ("RegionSize", C.c_size_t), ("State", W.DWORD), ("Protect", W.DWORD), ("Type", W.DWORD)]


class WSEX(C.Structure):
    _fields_ = [("VirtualAddress", C.c_void_p), ("Attributes", C.c_size_t)]


class PMC(C.Structure):
    _fields_ = [("cb", W.DWORD), ("PageFaultCount", W.DWORD), ("PeakWorkingSetSize", C.c_size_t), ("WorkingSetSize", C.c_size_t),
                ("QuotaPeakPagedPoolUsage", C.c_size_t), ("QuotaPagedPoolUsage", C.c_size_t), ("QuotaPeakNonPagedPoolUsage", C.c_size_t),
                ("QuotaNonPagedPoolUsage", C.c_size_t), ("PagefileUsage", C.c_size_t), ("PeakPagefileUsage", C.c_size_t),
                ("PrivateUsage", C.c_size_t), ("PrivateWorkingSetSize", C.c_size_t), ("SharedCommitUsage", C.c_size_t)]


class HEAPLIST32(C.Structure):
    _fields_ = [("dwSize", C.c_size_t), ("th32ProcessID", W.DWORD), ("th32HeapID", C.c_size_t), ("dwFlags", W.DWORD)]


k32.OpenProcess.restype = W.HANDLE
k32.VirtualQueryEx.argtypes = [W.HANDLE, C.c_void_p, C.POINTER(MBI), C.c_size_t]
k32.VirtualQueryEx.restype = C.c_size_t
k32.CreateToolhelp32Snapshot.restype = W.HANDLE
k32.ReadProcessMemory.argtypes = [W.HANDLE, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]
psapi.QueryWorkingSetEx.argtypes = [W.HANDLE, C.c_void_p, W.DWORD]
psapi.GetMappedFileNameW.argtypes = [W.HANDLE, C.c_void_p, W.LPWSTR, W.DWORD]
psapi.GetProcessMemoryInfo.argtypes = [W.HANDLE, C.POINTER(PMC), W.DWORD]


def regions(h):
    addr, m = 0, MBI()
    while k32.VirtualQueryEx(h, C.c_void_p(addr), C.byref(m), C.sizeof(m)):
        yield (m.BaseAddress or 0, m.AllocationBase or 0, m.RegionSize, m.State, m.Protect, m.Type, m.AllocationProtect)
        addr = (m.BaseAddress or 0) + m.RegionSize
        if addr >= 1 << 47: break


def mapped_name(h, addr):
    buf = C.create_unicode_buffer(1024)
    n = psapi.GetMappedFileNameW(h, C.c_void_p(addr), buf, 1024)
    if not n: return None
    s = buf.value
    return s.rsplit("\\", 1)[-1]


def heap_ids(pid):
    snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPHEAPLIST, pid)
    out = []
    if snap in (None, W.HANDLE(-1).value): return out
    hl = HEAPLIST32(); hl.dwSize = C.sizeof(hl)
    ok = k32.Heap32ListFirst(snap, C.byref(hl))
    while ok:
        out.append(hl.th32HeapID)
        ok = k32.Heap32ListNext(snap, C.byref(hl))
    k32.CloseHandle(snap)
    return out


def ws_pages(h, committed):
    """(valid, shared) per committed page, in one QueryWorkingSetEx call per chunk."""
    pages = [b + i * PAGE for (b, size) in committed for i in range(size // PAGE)]
    res = {}
    CH = 1 << 16
    for s in range(0, len(pages), CH):
        chunk = pages[s:s + CH]
        arr = (WSEX * len(chunk))()
        for i, p in enumerate(chunk): arr[i].VirtualAddress = p
        if not psapi.QueryWorkingSetEx(h, arr, C.sizeof(arr)):
            raise OSError(C.get_last_error(), "QueryWorkingSetEx")
        for i in range(len(chunk)):
            a = arr[i].Attributes
            res[chunk[i]] = (a & 1, (a >> 15) & 1)   # Valid, Shared
    return res


def main():
    args = sys.argv[1:]
    if not args: print(__doc__); return 2
    pid = int(args[0])
    top = int(args[args.index("--top") + 1]) if "--top" in args else 40
    nallocs = int(args[args.index("--allocs") + 1]) if "--allocs" in args else 0
    jpath = args[args.index("--json") + 1] if "--json" in args else None
    h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, pid)
    if not h: print("OpenProcess failed", C.get_last_error()); return 1

    regs = list(regions(h))
    # allocation groups
    alloc = defaultdict(lambda: {"reserved": 0, "committed": 0, "type": 0, "guard": False, "protect": set(), "regs": []})
    for (b, ab, size, state, prot, typ, aprot) in regs:
        if state == MEM_FREE: continue
        a = alloc[ab]
        a["reserved"] += size; a["type"] = typ; a["regs"].append((b, size, state, prot))
        if state == MEM_COMMIT:
            a["committed"] += size; a["protect"].add(prot & 0xFF)
            if prot & PAGE_GUARD: a["guard"] = True
    heaps = set(heap_ids(pid))
    gc_base = None
    priv_res = [(ab, a["reserved"]) for ab, a in alloc.items() if a["type"] == MEM_PRIVATE]
    big = [x for x in priv_res if x[1] >= 1 << 30]
    gc_bases = {ab for ab, _ in big}

    def bucket(ab, a):
        t = a["type"]
        if t == MEM_IMAGE: return "image:" + (mapped_name(h, ab) or "?")
        if t == MEM_MAPPED: return "mapped:" + (mapped_name(h, ab) or "<anon>")
        if ab in gc_bases: return "gc"
        if ab in heaps: return "heap:%x" % ab
        if a["guard"] and a["reserved"] <= 64 << 20: return "stack"
        return "private"

    committed = [(b, size) for a in alloc.values() for (b, size, st, _) in a["regs"] if st == MEM_COMMIT]
    ws = ws_pages(h, committed)

    rows = defaultdict(lambda: {"committed": 0, "resident": 0, "residentPrivate": 0, "charge": 0, "allocs": 0})
    allocs_out = []
    for ab, a in alloc.items():
        name = bucket(ab, a)
        r = rows[name]; r["allocs"] += 1
        ares = apres = acharge = 0
        for (b, size, st, prot) in a["regs"]:
            if st != MEM_COMMIT: continue
            r["committed"] += size
            for i in range(size // PAGE):
                v, sh = ws.get(b + i * PAGE, (0, 0))
                if v: ares += PAGE
                if v and not sh: apres += PAGE
            if a["type"] == MEM_PRIVATE: acharge += size
            elif (prot & 0xFF) in (PAGE_WRITECOPY, PAGE_EXECUTE_WRITECOPY): pass   # unwritten CoW: not charged yet
            elif a["type"] == MEM_IMAGE and (prot & 0xFF) in (0x04, 0x40):     # RW/RWX image page: written CoW, charged
                acharge += size
        r["resident"] += ares; r["residentPrivate"] += apres; r["charge"] += acharge
        if a["type"] == MEM_PRIVATE:
            allocs_out.append({"base": "%x" % ab, "bucket": name, "reserved": a["reserved"], "committed": a["committed"],
                               "residentPrivate": apres, "protect": sorted("%x" % p for p in a["protect"]),
                               "regs": [(b, size) for (b, size, st, _) in a["regs"] if st == MEM_COMMIT]})

    pmc = PMC(); pmc.cb = C.sizeof(pmc)
    psapi.GetProcessMemoryInfo(h, C.byref(pmc), pmc.cb)
    MB = 1048576.0
    # fold images and named mappings into totals by family for the summary
    fam = defaultdict(lambda: [0, 0, 0, 0])
    for n, r in rows.items():
        f = n.split(":", 1)[0] if n.startswith(("heap:",)) else n
        if n.startswith("image:"): f = "image (all)"
        if n.startswith("mapped:") and n != "mapped:<anon>": f = "mapped files (all)"
        fam[f][0] += r["committed"]; fam[f][1] += r["resident"]; fam[f][2] += r["residentPrivate"]; fam[f][3] += r["charge"]
    print("pid=%d  PrivateUsage(commit charge)=%.1f MB  WorkingSet=%.1f MB  PrivateWorkingSet=%.1f MB" % (
        pid, pmc.PrivateUsage / MB, pmc.WorkingSetSize / MB, pmc.PrivateWorkingSetSize / MB))
    print("\n%-28s %10s %10s %12s %10s" % ("family", "committed", "resident", "resPrivate", "charge"))
    tot = [0, 0, 0, 0]
    for f, v in sorted(fam.items(), key=lambda kv: -kv[1][2]):
        print("%-28s %10.1f %10.1f %12.1f %10.1f" % (f, v[0] / MB, v[1] / MB, v[2] / MB, v[3] / MB))
        for i in range(4): tot[i] += v[i]
    print("%-28s %10.1f %10.1f %12.1f %10.1f" % ("TOTAL", tot[0] / MB, tot[1] / MB, tot[2] / MB, tot[3] / MB))
    print("\n%-48s %10s %10s %12s %10s %6s" % ("bucket", "committed", "resident", "resPrivate", "charge", "allocs"))
    for n, r in sorted(rows.items(), key=lambda kv: -kv[1]["residentPrivate"])[:top]:
        print("%-48s %10.2f %10.2f %12.2f %10.2f %6d" % (n[:48], r["committed"] / MB, r["resident"] / MB, r["residentPrivate"] / MB, r["charge"] / MB, r["allocs"]))
    if nallocs:
        print("\nlargest private allocations by resident private bytes (zero = resident pages holding only zero bytes):")
        zero_page = bytes(PAGE)
        for a in sorted(allocs_out, key=lambda x: -x["residentPrivate"])[:nallocs]:
            zero = 0
            if a["bucket"] != "gc":
                for (b, size) in a["regs"]:
                    buf = C.create_string_buffer(size); got = C.c_size_t(0)
                    if not k32.ReadProcessMemory(h, C.c_void_p(b), buf, size, C.byref(got)): continue
                    raw = buf.raw
                    for i in range(size // PAGE):
                        v, sh = ws.get(b + i * PAGE, (0, 0))
                        if v and raw[i * PAGE:(i + 1) * PAGE] == zero_page: zero += PAGE
            a["zeroResident"] = zero
            print("  %14s %-10s reserved=%9.2f committed=%8.2f resPriv=%8.2f zero=%7.2f prot=%s" % (
                a["base"], a["bucket"][:10], a["reserved"] / MB, a["committed"] / MB, a["residentPrivate"] / MB, zero / MB, ",".join(a["protect"])))
    for a in allocs_out: a.pop("regs", None)
    if jpath:
        json.dump({"pid": pid, "privateUsage": pmc.PrivateUsage, "workingSet": pmc.WorkingSetSize,
                   "privateWorkingSet": pmc.PrivateWorkingSetSize, "buckets": rows, "allocs": allocs_out}, open(jpath, "w"), indent=1)
    k32.CloseHandle(h)
    return 0


if __name__ == "__main__":
    sys.exit(main())
