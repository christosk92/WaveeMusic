using System;
using System.IO;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// Session discovery/parsing over the rolling wavee.log file set — both on-disk line shapes: the legacy
// "yyyy-MM-dd HH:mm:ss.fffZ seq= tid= ..." prefix and the current "seq= tid= t=<unixms> ..." form.
public class WaveeLogSessionsTests
{
    static string WriteLog(string dir, string name, params string[] lines)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ListPastSessions_SplitsOnStartupLines_AndDropsTheCurrentProcess()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string live = WriteLog(dir, "wavee.log",
                // session 1 — legacy ISO-prefixed lines
                "2026-07-05 18:24:27.445Z seq=1 tid=2 I [app] startup - Wavee starting pid=25560 args=1 log=x framework=.NET",
                "2026-07-05 18:24:28.000Z seq=2 tid=2 I [connect] dealer connected",
                "2026-07-05 18:24:29.000Z seq=3 tid=4 W [lyrics] no synced lyrics",
                // session 2 — current t= lines, pid matches the "running" process below
                "seq=1 tid=2 t=1751742525980 I [app] startup - Wavee starting pid=4242 args=1 log=x framework=.NET",
                "seq=2 tid=2 t=1751742526100 I [audio] stack.state - Local audio stack active");

            var sessions = WaveeLogSessions.ListPastSessions(live, currentPid: 4242);

            // The trailing pid-4242 session is "this run" and must be excluded; the legacy session survives.
            var s = Assert.Single(sessions);
            Assert.Equal(25560, s.Pid);
            Assert.Equal(3, s.EntryCount);
            Assert.True(s.StartUnixMs > 0);   // parsed from the ISO prefix

            var entries = WaveeLogSessions.LoadSession(s);
            Assert.Equal(3, entries.Length);
            Assert.Equal(WaveeLogLevel.Warning, entries[2].Level);
            Assert.Equal("lyrics", entries[2].Category);
            Assert.Contains("no synced lyrics", entries[2].Message);
            Assert.All(entries, e => Assert.True(e.UnixMs > 0));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ListPastSessions_SessionSpansARolledFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The rolled file holds the session start; the live file continues it (a mid-session 10 MB roll),
            // then a NEW process (the current one) starts.
            WriteLog(dir, "wavee-20260704-120000.log",
                "seq=1 tid=2 t=1751630000000 I [app] startup - Wavee starting pid=1111 args=1",
                "seq=2 tid=2 t=1751630001000 I [connect] hello");
            string live = WriteLog(dir, "wavee.log",
                "seq=3 tid=2 t=1751630002000 E [audio] playback failed",
                "seq=1 tid=2 t=1751742525980 I [app] startup - Wavee starting pid=9999 args=1");

            var sessions = WaveeLogSessions.ListPastSessions(live, currentPid: 9999);

            var s = Assert.Single(sessions);
            Assert.Equal(1111, s.Pid);
            Assert.Equal(3, s.EntryCount);   // 2 in the rolled file + 1 continued in the live file

            var entries = WaveeLogSessions.LoadSession(s);
            Assert.Equal(3, entries.Length);
            Assert.Equal(WaveeLogLevel.Error, entries[2].Level);
            Assert.Equal("audio", entries[2].Category);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ListPastSessions_NewestFirst_AndSeqResetIsABoundary()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Second session has NO startup line (e.g. it was filtered) — the seq reset alone must open it.
            string live = WriteLog(dir, "wavee.log",
                "seq=1 tid=2 t=1000 I [app] startup - Wavee starting pid=1 args=1",
                "seq=2 tid=2 t=2000 I [connect] a",
                "seq=1 tid=2 t=9000 I [connect] b",
                "seq=2 tid=2 t=9500 I [connect] c");

            var sessions = WaveeLogSessions.ListPastSessions(live, currentPid: -1);

            Assert.Equal(2, sessions.Count);
            Assert.Equal(9000, sessions[0].StartUnixMs);   // newest first
            Assert.Equal(1000, sessions[1].StartUnixMs);
            Assert.Equal(2, sessions[0].EntryCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void LoadSession_KeepsOnlyTheLastMaxEntries()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var lines = new string[12];
            lines[0] = "seq=1 tid=2 t=1000 I [app] startup - Wavee starting pid=7 args=1";
            for (int i = 1; i < 12; i++)
                lines[i] = $"seq={i + 1} tid=2 t={1000 + i} I [connect] msg{i}";
            string live = WriteLog(dir, "wavee.log", lines);

            var s = Assert.Single(WaveeLogSessions.ListPastSessions(live, currentPid: -1));
            var entries = WaveeLogSessions.LoadSession(s, maxEntries: 5);

            Assert.Equal(5, entries.Length);
            Assert.Equal("msg11", entries[^1].Message);
            Assert.Equal("msg7", entries[0].Message);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ListPastSessions_KeysOnSidChange_AndExcludesCurrentBySid()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // session A (sid aaaa1111) then a trailing session whose sid IS this run → excluded by sid, not pid.
            string live = WriteLog(dir, "wavee.log",
                "seq=1 tid=2 t=1000 sid=aaaa1111 pid=11 I [app] startup - Wavee starting",
                "seq=2 tid=2 t=2000 sid=aaaa1111 pid=11 I [connect] hi",
                $"seq=1 tid=2 t=3000 sid={WaveeLog.SessionId} pid=22 I [app] startup - Wavee starting");

            var s = Assert.Single(WaveeLogSessions.ListPastSessions(live, currentPid: -1));
            Assert.Equal("aaaa1111", s.SessionId);
            Assert.Equal(11, s.Pid);
            Assert.Equal(2, s.EntryCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ListPastSessions_MixedLegacyAndSidLines()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string live = WriteLog(dir, "wavee.log",
                "seq=1 tid=2 t=1000 I [app] startup - Wavee starting pid=1 args=1",   // legacy (no sid)
                "seq=2 tid=2 t=2000 I [connect] a",
                "seq=1 tid=2 t=5000 sid=bbbb2222 pid=2 I [app] startup - Wavee starting",
                "seq=2 tid=2 t=6000 sid=bbbb2222 pid=2 I [connect] b");

            var sessions = WaveeLogSessions.ListPastSessions(live, currentPid: -1);

            Assert.Equal(2, sessions.Count);
            Assert.Equal("bbbb2222", sessions[0].SessionId);   // newest first
            Assert.Equal(2, sessions[0].Pid);
            Assert.Equal("", sessions[1].SessionId);           // legacy carries no sid
            Assert.Equal(1, sessions[1].Pid);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ListPastSessions_WhileWriterHoldsLiveFileOpen()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string live = WriteLog(dir, "wavee.log",
                "seq=1 tid=2 t=1000 sid=aaaa1111 pid=11 I [app] startup - Wavee starting",
                "seq=2 tid=2 t=2000 sid=aaaa1111 pid=11 I [connect] a",
                "seq=1 tid=2 t=9000 sid=bbbb2222 pid=22 I [app] startup - Wavee starting",
                "seq=2 tid=2 t=9500 sid=bbbb2222 pid=22 I [connect] b");

            // WaveeLog's persistent sink keeps the live file open for append with FileShare.ReadWrite | Delete. Enumerating
            // it with File.ReadLines (default FileShare.Read) would throw a sharing violation and discard the whole list;
            // the session walk must still find every past session while that writer handle is held.
            using var writer = new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

            var sessions = WaveeLogSessions.ListPastSessions(live, currentPid: -1);

            Assert.Equal(2, sessions.Count);
            Assert.Equal("bbbb2222", sessions[0].SessionId);   // newest first
            Assert.Equal(22, sessions[0].Pid);
            Assert.Equal("aaaa1111", sessions[1].SessionId);
            Assert.Equal(11, sessions[1].Pid);

            // The session that spans the still-open live file loads its entries too (the read loop, not just the listing).
            var entries = WaveeLogSessions.LoadSession(sessions[0]);
            Assert.Equal(2, entries.Length);
            Assert.Equal("b", entries[^1].Message);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ExportSessionToFile_LineCountMatchesEntryCount()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string live = WriteLog(dir, "wavee.log",
                "seq=1 tid=2 t=1000 I [app] startup - Wavee starting pid=7 args=1",
                "seq=2 tid=2 t=2000 I [connect] hello",
                "seq=3 tid=2 t=3000 W [ui] warn");

            var s = Assert.Single(WaveeLogSessions.ListPastSessions(live, currentPid: -1));
            string outPath = Path.Combine(dir, "export.txt");
            WaveeLogSessions.ExportSessionToFile(s, outPath);

            var exported = File.ReadAllLines(outPath);
            Assert.Equal(s.EntryCount, exported.Length);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
