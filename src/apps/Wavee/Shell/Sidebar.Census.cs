// ── Shell/Sidebar.Census.cs — why did the sidebar re-plan? (always-on, drained into the scroll rollup) ─────────────
//
// WHY THIS EXISTS. The first `scroll.trace` session (2026-09-16, 20:18) showed the sidebar rail re-rendering about
// every fifth frame of a playlist scroll — `RailHost×1 a=305K` in the render census, 24 times a second, the single
// largest scroll-time allocator left (38 MB over a 4.6 s burst, gen0 seven times, gen1 twice). The rail re-renders only
// when `PaneView.PublishStage` bumps `_railVersion`, which happens only when a re-plan ran, which happens only when
// `PlanDep` moved. The census says WHICH of the ten planner inputs moved, so the log names the culprit instead of a
// session guessing at `InputVersion` vs `Entries.Version` vs the play log.
//
// Pure counters; no engine, no clock. `PaneView` notes builds and publishes, `NavigationFrameWatch` drains one line per
// scroll burst into `scroll.frames` (and resets at the burst's start), so the count is the burst's own.

using System;
using System.Text;

namespace Wavee;

public static partial class Shell
{
    /// <summary>The planner inputs `PaneView.PlanDep` folds, as bits — one per input, so a moved key names its cause.</summary>
    [Flags]
    public enum SidebarReplanCause : ushort
    {
        None = 0,
        Layout = 1 << 0,
        Entries = 1 << 1,
        Pins = 1 << 2,
        Folder = 1 << 3,
        Revision = 1 << 4,
        Mode = 1 << 5,
        Edit = 1 << 6,
        Binder = 1 << 7,
        Input = 1 << 8,
        Search = 1 << 9,
    }

    public static class SidebarReplanCensus
    {
        static int s_builds, s_publishes, s_railBumps, s_wholesale;
        static SidebarReplanCause s_causes;

        /// <summary>A stage was built (the plan memo recomputed) because these inputs moved since the previous build.</summary>
        public static void NoteBuild(SidebarReplanCause causes) { s_builds++; s_causes |= causes; }

        /// <summary>A stage was published with notification; <paramref name="railChanged"/> is the rail re-render edge.</summary>
        public static void NotePublish(bool railChanged, bool wholesale)
        {
            s_publishes++;
            if (railChanged) s_railBumps++;
            if (wholesale) s_wholesale++;
        }

        public static void Reset() { s_builds = s_publishes = s_railBumps = s_wholesale = 0; s_causes = SidebarReplanCause.None; }

        /// <summary>The counters since the last drain as log fields, then reset. Empty when nothing re-planned, so a quiet
        /// burst adds nothing to its line.</summary>
        public static string Drain()
        {
            string s = Describe(s_builds, s_publishes, s_railBumps, s_wholesale, s_causes);
            Reset();
            return s;
        }

        public static string Describe(int builds, int publishes, int railBumps, int wholesale, SidebarReplanCause causes)
        {
            if (builds == 0 && publishes == 0) return "";
            var sb = new StringBuilder(96);
            sb.Append(" sidebarReplans=").Append(builds).Append(" sidebarPublishes=").Append(publishes)
              .Append(" railBumps=").Append(railBumps).Append(" wholesale=").Append(wholesale).Append(" causes=");
            if (causes == SidebarReplanCause.None) { sb.Append("none"); return sb.ToString(); }
            bool first = true;
            for (int bit = 0; bit < 16; bit++)
            {
                var c = (SidebarReplanCause)(1 << bit);
                if ((causes & c) == 0) continue;
                if (!first) sb.Append('|');
                sb.Append(c.ToString());
                first = false;
            }
            return sb.ToString();
        }
    }
}
