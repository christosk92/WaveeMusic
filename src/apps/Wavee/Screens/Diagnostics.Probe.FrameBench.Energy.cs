// ── Screens/Diagnostics.Probe.FrameBench.Energy.cs ──────────────────────────────────────────────────────────────────
// the frame bench's ENERGY columns: the platform's EMI meters, read over each measured window
//
// Role: SHELL
// Owner: S
//
// Windows exposes the Energy Metering Interface channels as the "Energy Meter" performance-counter set, readable without
// elevation (the raw EMI device needs it): per instance (on the owner's Snapdragon X: gpu, cpu_cluster_0..2, sys, psu_usb)
// "Energy" is cumulative picowatt-hours and "Time" the reading's time in ms. The bench reads them at the measured window's
// start and end, so every scenario reports the energy its window spent (mJ) and the mean power (mW) per channel — the
// arbiter when GPU milliseconds move with the clock the governor picks (audit PERF r3 COMP: a slower clock that does the
// same work may cost less energy, not more). Where the counter set is missing the values are NaN.

using System.Runtime.InteropServices;

namespace Wavee;

public static partial class Diagnostics
{
    public static partial class Probe
    {
        static unsafe partial class EnergyMeter
        {
            [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
            private static partial int PdhOpenQuery(string? source, nint user, out nint query);
            [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
            private static partial int PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
            [LibraryImport("pdh.dll")]
            private static partial int PdhCollectQueryData(nint query);
            [LibraryImport("pdh.dll")]
            private static partial int PdhGetRawCounterValue(nint counter, out uint type, byte* raw);

            /// <summary>The channels read (EMI instance names) and the summary key each reports under.</summary>
            static readonly (string Instance, string Key)[] Channels =
                [("gpu", "Gpu"), ("cpu_cluster_0", "Cpu0"), ("cpu_cluster_1", "Cpu1"), ("cpu_cluster_2", "Cpu2"), ("sys", "Sys")];
            static nint s_query;
            static nint[] s_energy = [], s_time = [];
            static bool s_tried, s_ok;

            static bool Init()
            {
                if (s_tried) return s_ok;
                s_tried = true;
                if (PdhOpenQuery(null, 0, out s_query) != 0) return false;
                s_energy = new nint[Channels.Length]; s_time = new nint[Channels.Length];
                for (int i = 0; i < Channels.Length; i++)
                {
                    if (PdhAddEnglishCounter(s_query, @"\Energy Meter(" + Channels[i].Instance + @")\Energy", 0, out s_energy[i]) != 0) return false;
                    if (PdhAddEnglishCounter(s_query, @"\Energy Meter(" + Channels[i].Instance + @")\Time", 0, out s_time[i]) != 0) return false;
                }
                s_ok = PdhCollectQueryData(s_query) == 0;
                return s_ok;
            }

            /// <summary>One reading: per channel, cumulative energy (pWh) and its time (ms); null when the meters are unavailable.</summary>
            public static (long PWh, long Ms)[]? Read()
            {
                if (!Init() || PdhCollectQueryData(s_query) != 0) return null;
                var r = new (long, long)[Channels.Length];
                byte* raw = stackalloc byte[64];   // PDH_RAW_COUNTER: CStatus, TimeStamp, FirstValue (at 16), SecondValue, MultiCount
                for (int i = 0; i < Channels.Length; i++)
                {
                    if (PdhGetRawCounterValue(s_energy[i], out _, raw) != 0) return null;
                    long e = *(long*)(raw + 16);
                    if (PdhGetRawCounterValue(s_time[i], out _, raw) != 0) return null;
                    r[i] = (e, *(long*)(raw + 16));
                }
                return r;
            }

            /// <summary>The window's energy (mJ) and mean power (mW) per channel, plus the CPU clusters summed, as summary metrics.</summary>
            public static void AddMetrics(List<KeyValuePair<string, double>> extra, (long PWh, long Ms)[]? a, (long PWh, long Ms)[]? b)
            {
                double cpuMj = 0, cpuMs = 0;
                for (int i = 0; i < Channels.Length; i++)
                {
                    double mj = double.NaN, mw = double.NaN;
                    if (a is not null && b is not null)
                    {
                        mj = (b[i].PWh - a[i].PWh) * 1e-12 * 3600.0 * 1000.0;   // pWh -> mJ
                        double ms = b[i].Ms - a[i].Ms;
                        mw = ms > 0 ? mj / ms * 1000.0 : double.NaN;
                        if (Channels[i].Key.StartsWith("Cpu", StringComparison.Ordinal)) { cpuMj += mj; cpuMs = ms; }
                    }
                    extra.Add(new("energy" + Channels[i].Key + "Mj", mj));
                    extra.Add(new("power" + Channels[i].Key + "Mw", mw));
                }
                extra.Add(new("energyCpuMj", a is null || b is null ? double.NaN : cpuMj));
                extra.Add(new("powerCpuMw", a is null || b is null || cpuMs <= 0 ? double.NaN : cpuMj / cpuMs * 1000.0));
            }
        }
    }
}
