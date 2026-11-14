// ── Screens/Diagnostics.Inspect.Runtime.cs ──────────────────────────────────────────────────────────────────────────────
// `state kind=runtime runtime=<case> issue=<kind>`: the Playback settings' runtime card in each of its shapes, without a runtime
//
// Role: SHELL (UI thread; writes Setup.Runtime.Status)
//
// THE PATH. The headless server has no local playback runtime, so `Setup.Runtime.Status` stays `NotApplicable` and the Playback page only ever shows
// its "not set up" bar. This publishes the ONE status model the card reads (`Setup.Runtime.Status`, whole, as the host does) with a made-up value
// for each shape the card draws: `ready` (the facts and the doors), `setup` (the informational bar with Set up) and `problem` (the error bar with
// the diagnostics doors and Retry setup, `issue=` its typed reason). Nothing is installed, downloaded or loaded; the next `mount` (or `reset`) puts
// the boot's status back.

namespace Wavee;

public static partial class Diagnostics
{
    public sealed partial class InspectSession
    {
        /// <summary>The cases `state kind=runtime runtime=` stages, in the order `help` lists them.</summary>
        internal static readonly string[] RuntimeCases = ["problem", "setup", "ready", "none"];

        /// <summary>The issues `issue=` takes (with `runtime=problem`).</summary>
        internal static readonly string[] RuntimeIssues = ["wrongarch", "nopack", "unsupported"];

        /// <summary>The status before the first `state kind=runtime` of this mount; null while none stands.</summary>
        Setup.RuntimeFacts? _runtimeBefore;

        /// <summary>`state kind=runtime runtime=<case> issue=<kind>`: publish a made-up runtime status (<see cref="RuntimeStatus"/>).</summary>
        string RuntimeState(int id, string name, Inspect.Args a)
        {
            string c = a.Has("runtime") ? a.Str("runtime").ToLowerInvariant() : "problem";
            string i = a.Has("issue") ? a.Str("issue").ToLowerInvariant() : "wrongarch";
            if (Array.IndexOf(RuntimeCases, c) < 0) return Inspect.Reply.Fail(id, name, "state kind=runtime wants runtime=" + string.Join("|", RuntimeCases) + ", not '" + c + "'");
            if (RuntimeIssueOf(i) is not { } issue) return Inspect.Reply.Fail(id, name, "state kind=runtime wants issue=" + string.Join("|", RuntimeIssues) + ", not '" + i + "'");
            _runtimeBefore ??= Setup.Runtime.Status.Peek();
            Setup.Runtime.Status.Value = c == "none" ? _runtimeBefore : RuntimeStatus(c, issue);
            if (c == "none") _runtimeBefore = null;
            if (!a.Has("trace") && a.Bool("settle", true)) RunFrames(30);
            return Settled(id, name, a, w =>
            {
                w.WriteString("kind", "runtime");
                w.WriteString("runtime", c);
                var st = Setup.Runtime.Status.Peek();
                w.WriteBoolean("ready", st.IsReady);
                w.WriteString("issue", st.Issue.ToString());
            });
        }

        /// <summary>A mount puts the boot's runtime status back after a staged one.</summary>
        void RestoreRuntime()
        {
            if (_runtimeBefore is not { } before) return;
            _runtimeBefore = null;
            Setup.Runtime.Status.Value = before;
        }

        static Setup.RuntimeIssue? RuntimeIssueOf(string i) => i switch
        {
            "wrongarch" => Setup.RuntimeIssue.WrongArch,
            "nopack" => Setup.RuntimeIssue.NoPack,
            "unsupported" => Setup.RuntimeIssue.Unsupported,
            _ => null,
        };

        /// <summary>The status for one case: a problem carries its issue; Ready carries a version, an architecture and a location (no signature
        /// file, so the card's View signature door stays away).</summary>
        static Setup.RuntimeFacts RuntimeStatus(string c, Setup.RuntimeIssue issue) => c switch
        {
            "ready" => new Setup.RuntimeFacts(true, Setup.RuntimeIssue.None, PackId: "fixture", Version: "1.0.0", Arch: "arm64",
                Location: @"C:\Users\fixture\AppData\Local\Wavee\runtime", Trust: Setup.RuntimeTrust.Trusted),
            "setup" => new Setup.RuntimeFacts(false, Setup.RuntimeIssue.Missing),
            _ => new Setup.RuntimeFacts(false, issue),
        };
    }
}
