using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class WaveeLogTests
{
    [Fact]
    public void Snapshot_RetainsNewestEntries_InOrder()
    {
        var log = new WaveeLog(ringCapacity: 3) { MinLevel = WaveeLogLevel.Trace, FileMinLevel = WaveeLogLevel.Critical };

        log.Info("app", "one");
        log.Info("app", "two");
        log.Info("app", "three");
        log.Info("app", "four");

        Assert.Equal(["two", "three", "four"], log.Snapshot().Select(e => e.Message).ToArray());
    }

    [Fact]
    public void MinLevel_FiltersRingAndFile()
    {
        var log = new WaveeLog { MinLevel = WaveeLogLevel.Warning };

        log.Info("app", "ignored");
        log.Warn("app", "kept");

        var snap = log.Snapshot();
        Assert.Single(snap);
        Assert.Equal("kept", snap[0].Message);
    }

    [Fact]
    public void StructuredEntry_FormatsFieldsOperationAndSecrets()
    {
        var log = new WaveeLog { MinLevel = WaveeLogLevel.Trace };

        log.Event(WaveeLogLevel.Info, "audio", "key.result", "resolved",
            operationId: "play-1",
            elapsedMs: 42,
            fields:
            [
                WaveeLogField.Of("file", "abc123"),
                WaveeLogField.Secret("token"),
                WaveeLogField.Of("reason", "NeverProvisioned"),
            ]);

        string formatted = log.Snapshot()[0].Format();
        Assert.Contains("key.result", formatted);
        Assert.Contains("op=play-1", formatted);
        Assert.Contains("elapsed=42ms", formatted);
        Assert.Contains("token=***", formatted);
        Assert.DoesNotContain("spotify-token", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileSink_WritesInfoPlusStructuredLines()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "wavee.log");
            var log = NewFileLog();
            log.Configure(path, minLevel: WaveeLogLevel.Trace, fileMinLevel: WaveeLogLevel.Info);

            log.Debug("app", "debug ignored by file");
            log.Event(WaveeLogLevel.Info, "connect", "session.start", "started",
                fields: [WaveeLogField.Of("interactive", true)]);
            log.FlushForTests();

            string text = ReadLogFile(path);
            Assert.StartsWith("seq=", text);
            Assert.Contains("I [connect] session.start", text);
            Assert.Contains("interactive=true", text);
            Assert.DoesNotContain("Z seq=", text);
            Assert.DoesNotContain("debug ignored by file", text);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void FileSink_RollsWhenCurrentFileIsTooLarge()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "wavee.log");
            File.WriteAllText(path, new string('x', 128));
            var log = NewFileLog();
            log.Configure(path, minLevel: WaveeLogLevel.Info, fileMinLevel: WaveeLogLevel.Info, maxFileBytes: 32, retainedFiles: 2);

            log.Info("app", "after roll");
            log.FlushForTests();

            Assert.Contains(Directory.GetFiles(dir, "wavee-*.log"), File.Exists);
            Assert.Contains("after roll", ReadLogFile(path));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void FileSink_StampsSidAndPid_StableAcrossLines()
    {
        string dir = NewTempDir();
        try
        {
            string path = Path.Combine(dir, "wavee.log");
            var log = NewFileLog();
            log.Configure(path, minLevel: WaveeLogLevel.Info, fileMinLevel: WaveeLogLevel.Info);

            log.Info("app", "one");
            log.Info("connect", "two");
            log.FlushForTests();

            var lines = ReadLogLines(path);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                Assert.Contains(" sid=" + WaveeLog.SessionId + " ", line);
                Assert.Contains(" pid=", line);
            }
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Echo_ReceivesFormattedLine_ParityWithEntryFormat()
    {
        string? echoed = null;
        var log = new WaveeLog();
        log.Configure(echo: s => echoed = s, minLevel: WaveeLogLevel.Trace);

        log.Info("app", "hello world");

        Assert.Equal(log.Snapshot()[0].Format(), echoed);
    }

    [Fact]
    public void RingCapacity_Reconfigure_PreservesNewest()
    {
        var log = new WaveeLog(ringCapacity: 4) { MinLevel = WaveeLogLevel.Trace };
        log.Info("app", "one");
        log.Info("app", "two");
        log.Info("app", "three");
        log.Info("app", "four");

        log.Configure(minLevel: WaveeLogLevel.Trace, ringCapacity: 2);

        Assert.Equal(["three", "four"], log.Snapshot().Select(e => e.Message).ToArray());
    }

    [Fact]
    public void EnvPrecedence_EnvBeatsExplicitConfigureArg()
    {
        var prior = Environment.GetEnvironmentVariable("WAVEE_LOG_LEVEL");
        try
        {
            Environment.SetEnvironmentVariable("WAVEE_LOG_LEVEL", "Error");
            var log = new WaveeLog();
            log.Configure(minLevel: WaveeLogLevel.Debug);   // arg says Debug; env says Error → env wins
            Assert.Equal(WaveeLogLevel.Error, log.MinLevel);
        }
        finally { Environment.SetEnvironmentVariable("WAVEE_LOG_LEVEL", prior); }
    }

    [Fact]
    public void FileSink_Health_WarnsOnceThenRecovers()
    {
        string dir = NewTempDir();
        try
        {
            string blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "x");                          // a FILE where a directory is needed
            string badPath = Path.Combine(blocker, "sub", "wavee.log");

            var log = NewFileLog();
            log.Configure(badPath, minLevel: WaveeLogLevel.Info, fileMinLevel: WaveeLogLevel.Info);
            log.Info("app", "one");
            log.FlushForTests();

            var warns = log.Snapshot().Where(e => e.Category == "log" && e.Level == WaveeLogLevel.Warning).ToArray();
            Assert.Single(warns);
            Assert.Contains("file sink write failed", warns[0].Message);

            log.Info("app", "two");
            log.FlushForTests();
            // Still only ONE warning (first-failure-only), no recovery yet (path still bad).
            Assert.Single(log.Snapshot(), e => e.Category == "log" && e.Level == WaveeLogLevel.Warning);

            string good = Path.Combine(dir, "wavee.log");
            log.Configure(good, minLevel: WaveeLogLevel.Info, fileMinLevel: WaveeLogLevel.Info);
            log.Info("app", "three");
            log.FlushForTests();

            Assert.Contains(log.Snapshot(), e => e.Category == "log" && e.Level == WaveeLogLevel.Info
                && e.Message.Contains("file sink recovered"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Secret_ToString_RedactsValue()
    {
        var secret = new Secret("spotify-token");
        Assert.Equal("***", secret.ToString());
        Assert.Equal("spotify-token", secret.Reveal());
    }

    static string ReadLogFile(string path)
    {
        // The production sink keeps a persistent write handle (FileShare.ReadWrite) — ordinary File.ReadAllText
        // can fail on Windows when the drain thread still holds the stream.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    static string[] ReadLogLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    static WaveeLog NewFileLog() => new() { SyncFileDrainForTests = true };

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
