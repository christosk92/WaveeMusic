# Pure parsers and selection policy. Inputs are diagnostic output, never production source.
function ConvertFrom-DumpHeapRows([string[]]$Lines) {
    foreach ($line in $Lines) {
        if ($line -match '^\s*(?<Address>[0-9a-fA-F]{8,16})\s+(?<MT>[0-9a-fA-F]{8,16})\s+(?<Size>[\d,]+)\s*$') {
            [pscustomobject]@{ Address = $Matches.Address.ToLowerInvariant(); MethodTable = $Matches.MT.ToLowerInvariant(); Bytes = [long]($Matches.Size -replace ',', '') }
        }
    }
}

function ConvertFrom-DumpHeapStatistics([string[]]$Lines) {
    foreach ($line in $Lines) {
        if ($line -match '^\s*(?<MT>[0-9a-fA-F]{8,16})\s+(?<Count>[\d,]+)\s+(?<Size>[\d,]+)\s+(?<Type>.+)$') {
            $type = $Matches.Type.Trim()
            [pscustomobject]@{ MethodTable = $Matches.MT; Count = [long]($Matches.Count -replace ',', ''); Bytes = [long]($Matches.Size -replace ',', ''); Type = $type; IsFree = $type -eq 'Free' }
        }
    }
}

function Select-HeapRootSamples([object[]]$Rows, [int]$DistinctSizes = 3, [int]$PerSize = 2) {
    # Exact sizes: rounding MB hid different allocation classes. Take two addresses of
    # each selected size and also the most populous size (often the repeated 2 MB buffers).
    $groups = @($Rows | Sort-Object Bytes -Descending | Group-Object Bytes)
    $selected = @($groups | Select-Object -First $DistinctSizes)
    $common = $groups | Sort-Object Count -Descending | Select-Object -First 1
    if ($common -and $common.Name -notin $selected.Name) { $selected += $common }
    foreach ($group in $selected) {
        $ordered = @($group.Group | Sort-Object Address -Unique)
        $count = [Math]::Min($PerSize, $ordered.Count)
        for ($i = 0; $i -lt $count; $i++) {
            $index = if ($count -eq 1) { 0 } else { [int][Math]::Floor($i * ($ordered.Count - 1) / ($count - 1)) }
            $ordered[$index]
        }
    }
}

function Test-DumpObject([string]$Output, [object]$Expected, [string]$Type) {
    $name = [regex]::Match($Output, '(?m)^\s*Name:\s*(.+?)\s*$')
    $mt = [regex]::Match($Output, '(?m)^\s*MethodTable:\s*([0-9a-fA-F]+)\s*$')
    $size = [regex]::Match($Output, '(?m)^\s*Size:\s*([\d,]+)\s*\(')
    return $name.Success -and $name.Groups[1].Value -eq $Type -and $mt.Success -and
        $mt.Groups[1].Value.TrimStart('0') -eq $Expected.MethodTable.TrimStart('0') -and
        $size.Success -and [long]($size.Groups[1].Value -replace ',', '') -eq $Expected.Bytes
}

function Test-GcRootTarget([string]$Output, [string]$Address) {
    # A command echo or a common prefix is not a verified root. Require an arrow to
    # this exact object and SOS's positive completion summary.
    $target = [regex]::Escape($Address.TrimStart('0'))
    return $Output -match "(?im)^\s*->\s*0*$target\s+" -and
        $Output -match '(?im)^\s*Found\s+[1-9][\d,]*\s+(?:unique\s+)?roots?\b'
}

function Get-ProfileWindowWeight([double]$Start, [double]$End, [object[]]$Windows) {
    if ($End -lt $Start) { throw 'Profile timestamps are not monotonic.' }
    if ($Windows.Count -eq 0) { return $End - $Start }
    $total = 0.0
    foreach ($window in $Windows) { $total += [Math]::Max(0, [Math]::Min($End, $window.EndMs) - [Math]::Max($Start, $window.StartMs)) }
    return $total
}

function Get-SpeedscopeThreadReport([object]$Document, [object[]]$Windows = @()) {
    # The exported trace contains long groups of opens/closes at identical timestamps.
    # Keep their O(1) stack updates in compiled code: no per-event PowerShell pipeline,
    # stack copy, regex match or scriptblock invocation. PowerShell's existing JSON
    # objects are consumed directly, without reparsing or a second document copy.
    if (-not ('WaveePerfSpeedscope' -as [type])) {
        Add-Type -ReferencedAssemblies @([System.Management.Automation.PSObject].Assembly.Location, 'System.dll', 'System.Core.dll') -TypeDefinition @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Management.Automation;
using System.Text.RegularExpressions;

public static class WaveePerfSpeedscope
{
    public sealed class Report
    {
        public string Name { get; set; }
        public bool IsUi { get; set; }
        public bool IsRender { get; set; }
        public double WorkMs { get; set; }
        public double WaitMs { get; set; }
        public double ObservedMs { get { return WorkMs + WaitMs; } }
        public long Intervals { get; set; }
        public string CountKind { get; set; }
        public Dictionary<string, double> Inclusive { get; private set; }
        public Dictionary<string, double> Exclusive { get; private set; }
        public Dictionary<string, double> Waits { get; private set; }
        public Dictionary<string, double> Categories { get; private set; }
        public double UnattributedWorkMs { get; set; }
        public double UnattributedWaitMs { get; set; }
        public Report()
        {
            Inclusive = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Exclusive = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Waits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Categories = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }
    sealed class Frame
    {
        public string Name;
        public bool Ui, Render, Wait, Synthetic;
        public string Category;
    }
    sealed class Window
    {
        public double Start, End;
    }
    static object Field(object value, string name)
    {
        IDictionary dictionary = value as IDictionary;
        if (dictionary != null) return dictionary[name];
        PSPropertyInfo property = PSObject.AsPSObject(value).Properties[name];
        return property == null ? null : property.Value;
    }
    static IEnumerable Sequence(object value)
    {
        IEnumerable result = value as IEnumerable;
        if (result == null || value is string) throw new ArgumentException("Speedscope array missing or invalid.");
        return result;
    }
    static double Number(object value)
    {
        if (value == null) throw new ArgumentException("Speedscope number missing.");
        double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (double.IsNaN(result) || double.IsInfinity(result)) throw new ArgumentException("Speedscope number must be finite.");
        return result;
    }
    static int FrameIndex(object value, int count)
    {
        double raw = Number(value);
        if (raw < 0 || raw >= count || raw != Math.Floor(raw)) throw new ArgumentException("Invalid speedscope frame index.");
        return (int)raw;
    }
    static void Add(Dictionary<string, double> target, string name, double weight)
    {
        double previous;
        target.TryGetValue(name, out previous);
        target[name] = previous + weight;
    }
    static void Identify(Report report, Frame frame)
    {
        report.IsUi |= frame.Ui;
        report.IsRender |= frame.Render;
    }
    static double Weight(double start, double end, List<Window> windows, ref int cursor)
    {
        if (end < start) throw new ArgumentException("Profile timestamps are not monotonic.");
        if (end == start) return 0;
        if (windows.Count == 0) return end - start;
        while (cursor < windows.Count && windows[cursor].End <= start) cursor++;
        double weight = 0;
        for (int i = cursor; i < windows.Count && windows[i].Start < end; i++)
            weight += Math.Max(0, Math.Min(end, windows[i].End) - Math.Max(start, windows[i].Start));
        return weight;
    }
    static void Account(Report report, double weight, string leaf, string category, bool waiting, Dictionary<string, int> active)
    {
        if (weight <= 0) return;
        report.Intervals++;
        // Categories partition the entire observed denominator, including named
        // waits. They are exporter labels, not independent OS scheduler evidence.
        Add(report.Categories, category ?? "UNMARKED", weight);
        if (waiting)
        {
            report.WaitMs += weight;
            if (leaf != null) Add(report.Waits, leaf, weight);
            else report.UnattributedWaitMs += weight;
            return;
        }
        report.WorkMs += weight;
        if (leaf != null) Add(report.Exclusive, leaf, weight);
        else report.UnattributedWorkMs += weight;
        foreach (KeyValuePair<string, int> frame in active) Add(report.Inclusive, frame.Key, weight);
    }
    static void PushName(Dictionary<string, int> active, string name)
    {
        int count;
        active.TryGetValue(name, out count);
        active[name] = count + 1;
    }
    static void PopName(Dictionary<string, int> active, string name)
    {
        int count = active[name];
        if (count == 1) active.Remove(name);
        else active[name] = count - 1;
    }
    public static List<Report> Analyze(object document, object[] requestedWindows)
    {
        var windows = new List<Window>();
        if (requestedWindows != null)
            foreach (object item in requestedWindows)
                windows.Add(new Window { Start = Number(Field(item, "StartMs")), End = Number(Field(item, "EndMs")) });
        windows.Sort((a, b) => a.Start.CompareTo(b.Start));
        double previousEnd = double.NegativeInfinity;
        foreach (Window window in windows)
        {
            if (window.End <= window.Start || window.Start < previousEnd)
                throw new ArgumentException("CPU windows must be positive, nonoverlapping trace-relative millisecond intervals.");
            previousEnd = window.End;
        }
        var uiPattern = new Regex(@"AppHost[.:]RunFrame\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var renderPattern = new Regex(@"RenderThread[.:]Loop\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var waitPattern = new Regex(@"(?:WaitForPacedWork|WaitFenceEventBounded|WaitForSingleObject|WaitForMultipleObjects|Monitor[.:]Wait|WaitHandle[.:]Wait|Thread[.:](?:Sleep|Join)|LowLevelLifoSemaphore[.:]Wait|NtWaitFor|GetMessage)[^(]*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var scaffoldPattern = new Regex(@"^(?:Threads?|\(Non-Activities\)|Thread\s*\([^)]*\)|Process(?:32|64)?(?:\s.*)?|\?!\?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var frames = new List<Frame>();
        foreach (object raw in Sequence(Field(Field(document, "shared"), "frames")))
        {
            string name = Convert.ToString(Field(raw, "name"), CultureInfo.InvariantCulture);
            string category = string.Equals(name, "CPU_TIME", StringComparison.OrdinalIgnoreCase) ? "CPU_TIME"
                : string.Equals(name, "UNMANAGED_CODE_TIME", StringComparison.OrdinalIgnoreCase) ? "UNMANAGED_CODE_TIME" : null;
            frames.Add(new Frame { Name = name, Ui = uiPattern.IsMatch(name), Render = renderPattern.IsMatch(name), Wait = waitPattern.IsMatch(name),
                Category = category, Synthetic = category != null || scaffoldPattern.IsMatch(name) });
        }
        var reports = new List<Report>();
        foreach (object profile in Sequence(Field(document, "profiles")))
        {
            string unit = Convert.ToString(Field(profile, "unit"), CultureInfo.InvariantCulture).ToLowerInvariant();
            double scale;
            switch (unit)
            {
                case "seconds": scale = 1000; break;
                case "milliseconds": scale = 1; break;
                case "microseconds": scale = 0.001; break;
                case "nanoseconds": scale = 0.000001; break;
                default: throw new ArgumentException("Unsupported speedscope time unit: " + unit);
            }
            var report = new Report { Name = Convert.ToString(Field(profile, "name"), CultureInfo.InvariantCulture) };
            var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            double at = Number(Field(profile, "startValue"));
            int windowCursor = 0;
            string type = Convert.ToString(Field(profile, "type"), CultureInfo.InvariantCulture).ToLowerInvariant();
            if (type == "evented")
            {
                var stack = new List<int>();
                var methods = new List<int>();
                var categories = new List<string>();
                int waitDepth = 0;
                foreach (object item in Sequence(Field(profile, "events")))
                {
                    double next = Number(Field(item, "at"));
                    if (next < at) throw new ArgumentException("Profile timestamps are not monotonic.");
                    if (next > at && stack.Count > 0)
                    {
                        double weight = Weight(at * scale, next * scale, windows, ref windowCursor);
                        if (weight > 0) Account(report, weight, methods.Count > 0 ? frames[methods[methods.Count - 1]].Name : null,
                            categories.Count > 0 ? categories[categories.Count - 1] : null, waitDepth > 0, active);
                    }
                    int index = FrameIndex(Field(item, "frame"), frames.Count);
                    Frame frame = frames[index];
                    string operation = Convert.ToString(Field(item, "type"), CultureInfo.InvariantCulture);
                    if (string.Equals(operation, "O", StringComparison.OrdinalIgnoreCase))
                    {
                        stack.Add(index);
                        if (!frame.Synthetic) { methods.Add(index); PushName(active, frame.Name); }
                        if (frame.Category != null) categories.Add(frame.Category);
                        if (frame.Wait) waitDepth++;
                        // Thread identity covers the entire trace, including zero-time
                        // stack groups and intervals outside requested windows.
                        Identify(report, frame);
                    }
                    else if (string.Equals(operation, "C", StringComparison.OrdinalIgnoreCase) && stack.Count > 0 && stack[stack.Count - 1] == index)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        if (!frame.Synthetic) { methods.RemoveAt(methods.Count - 1); PopName(active, frame.Name); }
                        if (frame.Category != null) categories.RemoveAt(categories.Count - 1);
                        if (frame.Wait) waitDepth--;
                    }
                    else throw new ArgumentException("Invalid speedscope open/close stack.");
                    at = next;
                }
                double end = Number(Field(profile, "endValue"));
                if (end < at) throw new ArgumentException("Profile timestamps are not monotonic.");
                if (end > at && stack.Count > 0)
                    Account(report, Weight(at * scale, end * scale, windows, ref windowCursor), methods.Count > 0 ? frames[methods[methods.Count - 1]].Name : null,
                        categories.Count > 0 ? categories[categories.Count - 1] : null, waitDepth > 0, active);
                if (stack.Count != 0) throw new ArgumentException("Unclosed speedscope stack.");
                report.CountKind = "exported stack intervals (original sample count unavailable)";
            }
            else if (type == "sampled")
            {
                IEnumerator weights = Sequence(Field(profile, "weights")).GetEnumerator();
                foreach (object sample in Sequence(Field(profile, "samples")))
                {
                    if (!weights.MoveNext()) throw new ArgumentException("Sampled profile requires explicit weights matching samples.");
                    double next = at + Number(weights.Current);
                    double weight = Weight(at * scale, next * scale, windows, ref windowCursor);
                    active.Clear();
                    string leaf = null;
                    string category = null;
                    bool waiting = false;
                    bool hasFrames = false;
                    foreach (object rawIndex in Sequence(sample))
                    {
                        Frame frame = frames[FrameIndex(rawIndex, frames.Count)];
                        hasFrames = true;
                        Identify(report, frame);
                        if (weight > 0)
                        {
                            if (!frame.Synthetic) { PushName(active, frame.Name); leaf = frame.Name; }
                            if (frame.Category != null) category = frame.Category;
                            waiting |= frame.Wait;
                        }
                    }
                    if (hasFrames) Account(report, weight, leaf, category, waiting, active);
                    at = next;
                }
                if (weights.MoveNext()) throw new ArgumentException("Sampled profile requires explicit weights matching samples.");
                report.CountKind = "weighted samples";
            }
            else throw new ArgumentException("Unsupported speedscope profile type: " + type);
            reports.Add(report);
        }
        return reports;
    }
}
'@
    }
    [WaveePerfSpeedscope]::Analyze($Document, $Windows)
}
