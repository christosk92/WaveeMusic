# Working-set attribution for a live process: which resident pages belong to images (per module), mapped files,
# and private memory (per allocation base), split private/shared. QueryWorkingSetEx over VirtualQueryEx regions; the
# process is never suspended or written to. Windows PowerShell 5.1 (C# 5 in Add-Type). Output: markdown to stdout.
param([Parameter(Mandatory = $true)][int]$ProcessId, [int]$Top = 30)
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class WsAttrib {
    [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress; public IntPtr AllocationBase; public uint AllocationProtect; public ushort PartitionId; public ushort Pad; public IntPtr RegionSize; public uint State; public uint Protect; public uint Type; }
    [StructLayout(LayoutKind.Sequential)] public struct WSX { public IntPtr VirtualAddress; public ulong VirtualAttributes; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, IntPtr len);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool QueryWorkingSetEx(IntPtr h, [In, Out] WSX[] info, int cb);
    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint GetMappedFileNameW(IntPtr h, IntPtr addr, StringBuilder name, uint size);
    public class Region { public ulong Base; public ulong AllocBase; public ulong Size; public uint State; public uint Type; public uint Protect; public long ResidentPrivate; public long ResidentShared; public string Module; }
    public static List<Region> Scan(int pid) {
        IntPtr h = OpenProcess(0x0400 | 0x0010, false, pid);
        if (h == IntPtr.Zero) throw new Exception("OpenProcess failed: " + Marshal.GetLastWin32Error());
        var list = new List<Region>();
        try {
            ulong addr = 0; MBI mbi; int sz = Marshal.SizeOf(typeof(MBI));
            var buf = new WSX[4096]; var names = new Dictionary<ulong, string>();
            while (addr < 0x7FFFFFFFFFFFUL) {
                if (VirtualQueryEx(h, (IntPtr)(long)addr, out mbi, (IntPtr)sz) == IntPtr.Zero) break;
                ulong size = (ulong)(long)mbi.RegionSize; ulong b = (ulong)(long)mbi.BaseAddress;
                if (mbi.State == 0x1000) {
                    var r = new Region { Base = b, AllocBase = (ulong)(long)mbi.AllocationBase, Size = size, State = mbi.State, Type = mbi.Type, Protect = mbi.Protect };
                    if (mbi.Type == 0x1000000 || mbi.Type == 0x40000) {
                        string n; if (!names.TryGetValue(r.AllocBase, out n)) { var sb = new StringBuilder(1024); n = GetMappedFileNameW(h, (IntPtr)(long)r.AllocBase, sb, 1024) > 0 ? sb.ToString() : "?"; names[r.AllocBase] = n; }
                        r.Module = n;
                    }
                    ulong pages = size / 4096; ulong done = 0;
                    while (done < pages) {
                        int n = (int)Math.Min((ulong)buf.Length, pages - done);
                        var batch = n == buf.Length ? buf : new WSX[n];
                        for (int i = 0; i < n; i++) { batch[i].VirtualAddress = (IntPtr)(long)(b + (done + (ulong)i) * 4096); batch[i].VirtualAttributes = 0; }
                        QueryWorkingSetEx(h, batch, n * Marshal.SizeOf(typeof(WSX)));
                        for (int i = 0; i < n; i++) { ulong a = batch[i].VirtualAttributes; if ((a & 1) == 0) continue; bool shared = ((a >> 15) & 1) == 1; if (shared) r.ResidentShared += 4096; else r.ResidentPrivate += 4096; }
                        done += (ulong)n;
                    }
                    list.Add(r);
                }
                addr = b + size;
            }
        } finally { CloseHandle(h); }
        return list;
    }
}
'@
$regions = [WsAttrib]::Scan($ProcessId)
$p = Get-Process -Id $ProcessId
function MB([double]$b) { return [math]::Round($b / 1MB, 1) }
function SumOf($group, [string]$prop) { return ($group | Measure-Object $prop -Sum).Sum }
"# Working set attribution pid=$ProcessId ($($p.ProcessName)) ws=$(MB $p.WorkingSet64) MB private=$(MB $p.PrivateMemorySize64) MB"
""
$typeName = { switch ($_.Type) { 0x1000000 { 'Image' } 0x40000 { 'Mapped' } 0x20000 { 'Private' } default { 'Other' } } }
"| type | committed MB | resident private MB | resident shared MB |"; "|---|---:|---:|---:|"
foreach ($g in ($regions | Group-Object $typeName | Sort-Object { -(SumOf $_.Group 'ResidentPrivate') })) {
    "| $($g.Name) | $(MB (SumOf $g.Group 'Size')) | $(MB (SumOf $g.Group 'ResidentPrivate')) | $(MB (SumOf $g.Group 'ResidentShared')) |"
}
""
"## Images by module (resident)"; "| module | resident private MB | resident shared MB | committed MB |"; "|---|---:|---:|---:|"
foreach ($g in ($regions | Where-Object { $_.Type -eq 0x1000000 } | Group-Object Module | Sort-Object { -((SumOf $_.Group 'ResidentPrivate') + (SumOf $_.Group 'ResidentShared')) } | Select-Object -First 15)) {
    "| $([IO.Path]::GetFileName($g.Name)) | $(MB (SumOf $g.Group 'ResidentPrivate')) | $(MB (SumOf $g.Group 'ResidentShared')) | $(MB (SumOf $g.Group 'Size')) |"
}
""
"## Mapped files (resident)"; "| file | resident MB | committed MB |"; "|---|---:|---:|"
foreach ($g in ($regions | Where-Object { $_.Type -eq 0x40000 } | Group-Object Module | Sort-Object { -((SumOf $_.Group 'ResidentPrivate') + (SumOf $_.Group 'ResidentShared')) } | Select-Object -First 10)) {
    "| $($g.Name) | $(MB ((SumOf $g.Group 'ResidentPrivate') + (SumOf $g.Group 'ResidentShared'))) | $(MB (SumOf $g.Group 'Size')) |"
}
""
"## Private allocations by allocation base (resident private, top $Top)"
"| alloc base | committed regions | committed MB | resident MB | protect |"; "|---|---:|---:|---:|---|"
$prot = @{ 0x01 = 'NOACCESS'; 0x02 = 'R'; 0x04 = 'RW'; 0x08 = 'WC'; 0x10 = 'X'; 0x20 = 'RX'; 0x40 = 'RWX'; 0x104 = 'RW+GUARD' }
foreach ($g in ($regions | Where-Object { $_.Type -eq 0x20000 } | Group-Object AllocBase | Sort-Object { -(SumOf $_.Group 'ResidentPrivate') } | Select-Object -First $Top)) {
    $protects = ($g.Group | Group-Object Protect | ForEach-Object { $k = [int]$_.Name; if ($prot.ContainsKey($k)) { $prot[$k] } else { ('0x{0:x}' -f $k) } } | Sort-Object -Unique) -join ','
    "| 0x$([Convert]::ToString([long]$g.Name, 16)) | $($g.Count) | $(MB (SumOf $g.Group 'Size')) | $(MB (SumOf $g.Group 'ResidentPrivate')) | $protects |"
}
""
"## Private allocations by committed-region size class (resident private)"
"| size class | regions | resident MB |"; "|---|---:|---:|"
$sizeClass = { $s = $_.Size; if ($s -ge 64MB) { 'e >=64MB' } elseif ($s -ge 16MB) { 'd 16-64MB' } elseif ($s -ge 4MB) { 'c 4-16MB' } elseif ($s -ge 1MB) { 'b 1-4MB' } else { 'a <1MB' } }
foreach ($g in ($regions | Where-Object { $_.Type -eq 0x20000 } | Group-Object $sizeClass | Sort-Object Name)) {
    "| $($g.Name.Substring(2)) | $($g.Count) | $(MB (SumOf $g.Group 'ResidentPrivate')) |"
}
