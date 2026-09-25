// ── Wavee.Tests/CrashScrubTests.cs — Platform/Crash.Scrub.cs (WP-C, structural scrubber) ───────────────────────────────
//
// docs/plans/wavee/crash-diagnostics-implementation.md §B.4, §I: every scrub rule pinned as a fact over a realistic
// 40-line tail fixture (built here, not read from a real log) plus a small report.txt fixture for the module/argv
// rewrites. Pure: no disk, no process, no engine — Crash.Scrubber never touches anything but the strings it is given.

using Wavee;
using Xunit;
using static Wavee.Crash;
using static Wavee.Feedback;

namespace Wavee.Tests;

public class CrashScrubTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static Summary MakeSummary(string exceptionMessage = "boom", string lastRoute = "home") => new(
        ReportId: "r-1", InstallId: "inst-1", Kind: Kind.Managed, StampUtc: "2026-09-24T14:30:12.118Z",
        Version: "0.3.0", Quad: "0.3.0.41", Commit: "7e209e37", Channel: "stable", Arch: "arm64", OsBuild: "26100",
        Gpu: "NVIDIA GeForce RTX", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
        SessionId: "sess-1", UptimeMs: 12_345, BeforeFirstFrame: false, LastRoute: lastRoute,
        ExceptionType: "System.InvalidOperationException", ExceptionMessage: exceptionMessage,
        Rvas: [0x1000], ModuleBase: 0x140000000, ModuleSize: 0x2000000,
        DebugId: "ABCDEF-1", ExitCode: 0, HasDump: false, DumpBytes: 0);

    const string ReportFixture =
        "version=0.3.0\n" +
        "commit=7e209e37\n" +
        "module=C:\\Program Files\\WindowsApps\\12345Company.Wavee_0.3.0.0_x64__abc123\\Wavee.exe base=0x140000000 size=0x2000000\n" +
        "args=C:\\wavee\\waveemusic\\bin\\Release\\Wavee.exe --fake --profile TestProfile --crash-probe throw --relaunch-after 1234 --recovery\n" +
        "\nException\n---------\nSystem.InvalidOperationException: boom\n";

    /// <summary>~40 lines covering every B.4 rule plus enough ordinary traffic (frame/session/library events) that
    /// the "nothing unexpected changes" half of the contract is exercised too, not just the rules under test.</summary>
    static List<string> TailFixture()
    {
        var lines = new List<string>
        {
            // 1: startup — account/credential force-dropped even though their values here are PURELY NUMERIC (i.e.
            // would otherwise pass the generic numeric-value allowance): proves the startup-specific rule, not just
            // the generic allowlist.
            "seq=1 tid=1 t=1758712345000 sid=aaaa1111 pid=5000 I [app] startup - Wavee starting account=12345 credential=99999 log=C:\\Users\\alice\\AppData\\Local\\Wavee\\logs\\wavee.log",
            // 2: auth category — whole line dropped.
            "seq=2 tid=1 t=1758712345001 sid=aaaa1111 pid=5000 I [auth] token.refresh - refreshed token=abc.def.ghi",
            // 3: wire category — whole line dropped.
            "seq=3 tid=2 t=1758712345002 sid=aaaa1111 pid=5000 I [wire] request.sent - POST /v1/foo",
            // 4: Authorization marker outside auth/wire — still dropped, by text not category.
            "seq=4 tid=2 t=1758712345003 sid=aaaa1111 pid=5000 I [http] request.headers - Authorization: Bearer abcdef123456",
            // 5: Cookie marker.
            "seq=5 tid=2 t=1758712345004 sid=aaaa1111 pid=5000 I [http] response.headers - Set-Cookie: sid=xyz; Path=/",
            // 6: client-token marker.
            "seq=6 tid=2 t=1758712345005 sid=aaaa1111 pid=5000 I [http] auth.exchange - client-token=abc123",
            // 7: nav.route — the track id in arg= must survive (B.4 item 6).
            "seq=7 tid=1 t=1758712345006 sid=aaaa1111 pid=5000 I [ui] nav.route - route shown route=artist arg=37i9dQZF1DXcBWIGoYBM5M navId=12",
            // 8: frame.slow — every …Ms field, plus plain numeric values, survive.
            "seq=8 tid=1 t=1758712345007 sid=aaaa1111 pid=5000 I [ui] frame.slow - slow frame route=artist budgetMs=16 sinceNavMs=240 scroll=1 frameMs=42 census=3",
            // 9: Debug — below Info, the WHOLE line is dropped regardless of what it contains.
            "seq=9 tid=1 t=1758712345008 sid=aaaa1111 pid=5000 D [engine] gpu.debug - adapter selected name=NVIDIA",
            // 10-11: absolute paths, two different drives, in free prose (not a recognised field) → <path>.
            "seq=10 tid=3 t=1758712345009 sid=aaaa1111 pid=5000 I [library] file.drop - dropped file C:\\Users\\bob\\Music\\song.flac",
            "seq=11 tid=3 t=1758712345010 sid=aaaa1111 pid=5000 I [library] file.drop - dropped file D:\\Music\\x.mp3",
            // 12: an install-tree path — exempt, stays literal. (No embedded space: the general path rule, unlike
            // the module= line's own rewrite, stops at whitespace — see Absolute_paths_become_path_except_the_install_tree.)
            "seq=12 tid=1 t=1758712345011 sid=aaaa1111 pid=5000 I [app] module.loaded - loaded C:\\Windows\\System32\\WindowsApps\\Wavee\\Wavee.exe",
            // 13: a non-allowlisted, non-numeric field (one value quoted, carrying a space) → both dropped.
            "seq=13 tid=4 t=1758712345012 sid=aaaa1111 pid=5000 I [spotify] track.loaded - loaded name=\"Bohemian Rhapsody\" artist=Queen",
            // 14: a bare IP in prose — only the final regex backstop catches this.
            "seq=14 tid=2 t=1758712345013 sid=aaaa1111 pid=5000 I [network] probe.result - reachable at 203.0.113.5 directly",
            // 15: a bare email in prose — same backstop.
            "seq=15 tid=2 t=1758712345014 sid=aaaa1111 pid=5000 I [network] support.contact - contact us at support@example.com for help",
        };
        // 16-40: ordinary Info traffic across a few categories/events — nothing here should ever change beyond the
        // allowlist's own rules (every field below is either allowlisted or numeric).
        for (int i = 16; i <= 40; i++)
        {
            lines.Add($"seq={i} tid=1 t=175871234{i:D4} sid=aaaa1111 pid=5000 I [ui] session.frames - frames census fps=60 frameMs={i} state=steady code=0");
        }
        return lines;
    }

    static ScrubbedBundle ScrubFixture(string exceptionMessage = "boom", string lastRoute = "home") =>
        Scrubber.Scrub(MakeSummary(exceptionMessage, lastRoute), ReportFixture, TailFixture(), RedactionRules.None);

    // ── report.txt: the module line and the argv whitelist (B.4 items 3-4) ─────────────────────────────────────────────

    [Fact]
    public void Module_line_is_rewritten_to_the_install_tree_keeping_base_and_size()
    {
        var b = ScrubFixture();
        Assert.Contains("module=<install>\\Wavee.exe base=0x140000000 size=0x2000000", b.ReportTxt);
        // The real MSIX path has a space in "Program Files" — proving the module rewrite does not stop there.
        Assert.DoesNotContain("Program Files", b.ReportTxt);
        Assert.DoesNotContain("12345Company.Wavee_0.3.0.0_x64__abc123", b.ReportTxt);
    }

    [Fact]
    public void Argv_is_whitelisted_to_the_four_named_shapes()
    {
        var b = ScrubFixture();
        Assert.Contains("args=--fake --profile --crash-probe throw", b.ReportTxt);
        Assert.DoesNotContain("TestProfile", b.ReportTxt);
        Assert.DoesNotContain("--relaunch-after", b.ReportTxt);
        Assert.DoesNotContain("1234", b.ReportTxt);
        Assert.DoesNotContain("--recovery", b.ReportTxt);
    }

    [Theory]
    [InlineData("--fake", "--fake")]
    [InlineData("--relaunched-after-update", "--relaunched-after-update")]
    [InlineData("--profile MyProfile", "--profile")]
    [InlineData("--crash-probe hang", "--crash-probe hang")]
    [InlineData("--crash-probe", "--crash-probe")]
    [InlineData("--recovery", "")]
    [InlineData("--relaunch-after 1234", "")]
    [InlineData("--crash-handler 1234 C:\\logs -", "")]
    [InlineData("C:\\wavee\\Wavee.exe --fake --recovery", "--fake")]
    public void FilterArgvLine_keeps_exactly_the_whitelisted_shapes(string raw, string expected)
        => Assert.Equal(expected, Scrubber.FilterArgvLine(raw));

    // ── the log tail: level, credential lines, the allowlist ────────────────────────────────────────────────────────────

    [Fact]
    public void Startup_line_force_drops_account_credential_and_sid_even_when_numeric()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain("account=12345", b.TailTxt);
        Assert.DoesNotContain("credential=99999", b.TailTxt);
        Assert.Contains("account=<dropped>", b.TailTxt);
        Assert.Contains("credential=<dropped>", b.TailTxt);
        Assert.DoesNotContain(@"C:\Users\alice", b.TailTxt);
    }

    [Fact]
    public void Auth_and_wire_categories_are_dropped_whole()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain("token.refresh", b.TailTxt);
        Assert.DoesNotContain("abc.def.ghi", b.TailTxt);
        Assert.DoesNotContain("request.sent", b.TailTxt);
        Assert.DoesNotContain("/v1/foo", b.TailTxt);
        Assert.Contains("[line dropped: credential]", b.TailTxt);
    }

    [Fact]
    public void Credential_marker_text_drops_the_line_regardless_of_category()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain("Authorization", b.TailTxt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", b.TailTxt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdef123456", b.TailTxt);
        Assert.DoesNotContain("Set-Cookie", b.TailTxt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client-token", b.TailTxt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", b.TailTxt);
    }

    [Fact]
    public void Nav_route_keeps_the_track_id_and_navId()
    {
        var b = ScrubFixture();
        Assert.Contains("arg=37i9dQZF1DXcBWIGoYBM5M", b.TailTxt);
        Assert.Contains("route=artist", b.TailTxt);
        Assert.Contains("navId=12", b.TailTxt);
    }

    [Fact]
    public void Frame_slow_keeps_every_Ms_field_and_plain_numerics()
    {
        var b = ScrubFixture();
        Assert.Contains("budgetMs=16", b.TailTxt);
        Assert.Contains("sinceNavMs=240", b.TailTxt);
        Assert.Contains("frameMs=42", b.TailTxt);
        Assert.Contains("scroll=1", b.TailTxt);
        Assert.Contains("census=3", b.TailTxt);
    }

    [Fact]
    public void Debug_lines_are_dropped_entirely_not_merely_scrubbed()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain("gpu.debug", b.TailTxt);
        Assert.DoesNotContain("adapter selected", b.TailTxt);
        Assert.DoesNotContain("NVIDIA", b.TailTxt);
    }

    [Fact]
    public void Absolute_paths_become_path_except_the_install_tree()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain(@"C:\Users\bob", b.TailTxt);
        Assert.DoesNotContain(@"D:\Music", b.TailTxt);
        Assert.Contains("<path>", b.TailTxt);
        Assert.Contains(@"C:\Windows\System32\WindowsApps\Wavee\Wavee.exe", b.TailTxt);
    }

    [Fact]
    public void A_non_allowlisted_field_is_dropped_quoted_value_included()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain("Bohemian Rhapsody", b.TailTxt);
        Assert.DoesNotContain("Queen", b.TailTxt);
        Assert.Contains("name=<dropped>", b.TailTxt);
        Assert.Contains("artist=<dropped>", b.TailTxt);
    }

    [Fact]
    public void The_regex_backstop_still_catches_a_bare_ip_and_email_in_prose()
    {
        var b = ScrubFixture();
        Assert.DoesNotContain("203.0.113.5", b.TailTxt);
        Assert.DoesNotContain("support@example.com", b.TailTxt);
        Assert.Contains("<ip>", b.TailTxt);
        Assert.Contains("<email>", b.TailTxt);
    }

    [Fact]
    public void Ordinary_traffic_keeps_its_allowlisted_and_numeric_fields()
    {
        var b = ScrubFixture();
        Assert.Contains("fps=60", b.TailTxt);
        Assert.Contains("state=steady", b.TailTxt);
        Assert.Contains("code=0", b.TailTxt);
    }

    // ── Summary.ExceptionMessage / LastRoute (B.4 step 5) ───────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_exception_message_and_last_route_are_redacted_too()
    {
        var b = Scrubber.Scrub(
            MakeSummary("Failed for bob@example.com in C:\\Users\\bob\\AppData\\Wavee\\cache", "profile:C:\\Users\\bob\\Desktop"),
            ReportFixture, TailFixture(), RedactionRules.None);
        Assert.DoesNotContain("bob@example.com", b.Summary.ExceptionMessage);
        Assert.Contains("<email>", b.Summary.ExceptionMessage);
        Assert.DoesNotContain(@"Users\bob\AppData", b.Summary.ExceptionMessage);
        Assert.Contains("<user>", b.Summary.ExceptionMessage);
        Assert.DoesNotContain(@"Users\bob\Desktop", b.Summary.LastRoute);
        Assert.Contains("<user>", b.Summary.LastRoute);
    }

    // ── idempotence ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scrub_is_idempotent()
    {
        var first = ScrubFixture();
        var second = Scrubber.Scrub(first.Summary, first.ReportTxt, first.TailTxt.Split('\n'), RedactionRules.None);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.ReportTxt, second.ReportTxt);
        Assert.Equal(first.TailTxt, second.TailTxt);
    }

    // ── Preview ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_carries_the_header_the_report_then_a_labelled_tail()
    {
        var b = new ScrubbedBundle(MakeSummary(), "the report body", "line one\nline two\nline three\n");
        string preview = Scrubber.Preview(b);
        Assert.Contains("Wavee 0.3.0 (0.3.0.41)", preview);
        Assert.Contains("report r-1", preview);
        Assert.Contains("install inst-1", preview);
        Assert.Contains("the report body", preview);
        Assert.Contains("--- log-tail.txt (3 lines, personal details removed) ---", preview);
        Assert.Contains("line one", preview);
        Assert.Contains("line three", preview);
    }

    [Fact]
    public void Preview_truncates_with_an_ellipsis_at_maxChars()
    {
        var b = new ScrubbedBundle(MakeSummary(), new string('x', 1000), new string('y', 1000) + "\n");
        string preview = Scrubber.Preview(b, maxChars: 200);
        Assert.Equal(200, preview.Length);
        Assert.EndsWith("\u2026", preview);
    }
}
