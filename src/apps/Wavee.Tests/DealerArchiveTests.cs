using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Wavee;
using Wavee.Backend.Realtime;
using Xunit;

namespace Wavee.Tests;

public class DealerArchiveTests
{
    [Fact]
    public void IsHandled_MatchesSubscriberPrefixesOnly()
    {
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Ping, null, null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Pong, null, null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message, "hm://pusher/v1/connections/abc", null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message, "hm://connect-state/v1/cluster", null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message, "hm://connect-state/v1/connect/volume", null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message, "hm://playlist/v2/playlist/xyz", null));
        // P1: the permission arm is live (DealerRouter.OnPermission), so its frames are HANDLED, not "seen but dropped".
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message,
            "hm://playlist-permission/v1/playlist/xyz/permission/state", null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message, "hm://collection/tracks/user", null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Message, "hm://presence2/user/123", null));
        Assert.True(DealerArchive.IsHandled(DealerFrameType.Request, null, "hm://connect-state/v1/player/command"));

        Assert.False(DealerArchive.IsHandled(DealerFrameType.Unknown, "hm://playlist/v2/playlist/xyz", null));
        Assert.False(DealerArchive.IsHandled(DealerFrameType.Message, "hm://artist/v1/foo", null));
        Assert.False(DealerArchive.IsHandled(DealerFrameType.Message, null, null));
        Assert.False(DealerArchive.IsHandled(DealerFrameType.Request, null, "hm://collection/v1/"));
        Assert.False(DealerArchive.IsHandled(DealerFrameType.Message, "hm://connect-state/v1/elsewhere", null));
    }

    [Fact]
    public void TenThousandFrames_MatchIndexAndBin_WithoutBlockingRecord()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            archive.Configure(dir, enabled: true);
            var frames = new byte[10_000][];
            long expectedBin = 0;
            for (int i = 0; i < frames.Length; i++)
            {
                string json = i % 3 == 0
                    ? "{\"type\":\"message\",\"uri\":\"hm://playlist/v2/playlist/" + i + "\"}"
                    : "{\"type\":\"message\",\"uri\":\"hm://artist/v1/" + i + "\"}";
                frames[i] = Encoding.UTF8.GetBytes(json);
                expectedBin += frames[i].Length;
                archive.RecordInbound(frames[i], DealerFrameType.Message,
                    i % 3 == 0 ? "hm://playlist/v2/playlist/" + i : "hm://artist/v1/" + i, null, null);
            }
            Assert.Equal(0, archive.DroppedForTests);
            archive.FlushForTests();

            string idxPath = Single(dir, "dealer-*.idx.ndjson");
            string binPath = Single(dir, "dealer-*.bin");
            var lines = ReadLines(idxPath);
            Assert.Equal(10_000, lines.Length);

            int handled = 0, unhandled = 0;
            using var bin = OpenRead(binPath);
            var buf = new byte[4096];
            for (int i = 0; i < lines.Length; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                Assert.Equal("message", root.GetProperty("typ").GetString());
                int n = root.GetProperty("n").GetInt32();
                long off = root.GetProperty("off").GetInt64();
                bool ok = root.GetProperty("handled").GetBoolean();
                if (ok) handled++; else unhandled++;
                Assert.Equal(frames[i].Length, n);
                Assert.Equal(expectedPrefix(frames, i), off);
                bin.Position = off;
                Assert.Equal(n, bin.Read(buf, 0, n));
                Assert.True(frames[i].AsSpan().SequenceEqual(buf.AsSpan(0, n)));
            }
            Assert.Equal(3334, handled);   // i % 3 == 0 → 0,3,...,9999 = 3334
            Assert.Equal(6666, unhandled);
            Assert.Equal(expectedBin, new FileInfo(binPath).Length);
        }
        finally { TryDelete(dir); }

        static long expectedPrefix(byte[][] frames, int i)
        {
            long off = 0;
            for (int j = 0; j < i; j++) off += frames[j].Length;
            return off;
        }
    }

    [Fact]
    public void ByteBudget_DropsOverflow_AndRecordsDroppedLine()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            archive.MaxPendingBytes = 80;
            archive.Configure(dir, enabled: true);
            byte[] frame = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"uri\":\"hm://artist/v1/x\"}");
            Assert.True(frame.Length > 20);
            for (int i = 0; i < 20; i++)
                archive.RecordInbound(frame, DealerFrameType.Message, "hm://artist/v1/x", null, null);

            Assert.True(archive.DroppedForTests > 0);
            archive.FlushForTests();

            var lines = ReadLines(Single(dir, "dealer-*.idx.ndjson"));
            Assert.Contains(lines, l => l.Contains("\"typ\":\"dropped\"", StringComparison.Ordinal));
            int messages = 0, droppedN = 0;
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                string? typ = doc.RootElement.GetProperty("typ").GetString();
                if (typ == "message") messages++;
                if (typ == "dropped") droppedN += doc.RootElement.GetProperty("n").GetInt32();
            }
            Assert.Equal(archive.DroppedForTests, droppedN);
            Assert.True(messages > 0);
            Assert.Equal(20, messages + droppedN);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void PingPong_AreKeepaliveSummaries_NotPerFrameRecords()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            archive.Configure(dir, enabled: true);
            byte[] ping = "{\"type\":\"ping\"}"u8.ToArray();
            byte[] pong = "{\"type\":\"pong\"}"u8.ToArray();
            archive.RecordInbound(ping, DealerFrameType.Ping, null, null, null);
            archive.RecordInbound(pong, DealerFrameType.Pong, null, null, null);
            archive.RecordKeepalive(DealerFrameType.Ping);
            archive.FlushForTests();

            var lines = ReadLines(Single(dir, "dealer-*.idx.ndjson"));
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("keepalive", doc.RootElement.GetProperty("typ").GetString());
            Assert.Equal(2, doc.RootElement.GetProperty("ping").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("pong").GetInt32());
            var bins = Directory.GetFiles(dir, "dealer-*.bin");
            Assert.True(bins.Length == 0 || new FileInfo(bins[0]).Length == 0);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void DayRoll_GzipsYesterday_AndOpensToday()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            var clock = new DateTime(2026, 8, 14, 12, 0, 0);
            archive.Clock = () => clock;
            archive.Configure(dir, enabled: true);
            byte[] frame = "{\"type\":\"message\",\"uri\":\"hm://artist/v1/a\"}"u8.ToArray();
            archive.RecordInbound(frame, DealerFrameType.Message, "hm://artist/v1/a", null, null);
            archive.FlushForTests();
            Assert.True(File.Exists(Path.Combine(dir, "dealer-20260814.idx.ndjson")));
            Assert.True(File.Exists(Path.Combine(dir, "dealer-20260814.bin")));

            clock = new DateTime(2026, 8, 15, 1, 0, 0);
            archive.RecordInbound(frame, DealerFrameType.Message, "hm://artist/v1/b", null, null);
            archive.FlushForTests();

            Assert.False(File.Exists(Path.Combine(dir, "dealer-20260814.idx.ndjson")));
            Assert.False(File.Exists(Path.Combine(dir, "dealer-20260814.bin")));
            Assert.True(File.Exists(Path.Combine(dir, "dealer-20260814.idx.ndjson.gz")));
            Assert.True(File.Exists(Path.Combine(dir, "dealer-20260814.bin.gz")));
            Assert.True(File.Exists(Path.Combine(dir, "dealer-20260815.idx.ndjson")));
            Assert.True(File.Exists(Path.Combine(dir, "dealer-20260815.bin")));
            AssertRoundtripGzip(Path.Combine(dir, "dealer-20260814.idx.ndjson.gz"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void SizeRoll_GzipsSegmentImmediately()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            archive.Clock = () => new DateTime(2026, 8, 14, 12, 0, 0);
            archive.MaxLiveFileBytes = 40;
            archive.Configure(dir, enabled: true);
            byte[] frame = Encoding.UTF8.GetBytes("{\"type\":\"message\",\"uri\":\"hm://artist/v1/big-payload-xxxx\"}");
            Assert.True(frame.Length > 20);
            archive.RecordInbound(frame, DealerFrameType.Message, "hm://artist/v1/big-payload-xxxx", null, null);
            archive.RecordInbound(frame, DealerFrameType.Message, "hm://artist/v1/big-payload-xxxx", null, null);
            archive.FlushForTests();

            Assert.NotEmpty(Directory.GetFiles(dir, "dealer-20260814-*.bin.gz"));
            Assert.NotEmpty(Directory.GetFiles(dir, "dealer-20260814-*.idx.ndjson.gz"));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Prune_DeletesOldestGzipFirst_ByAgeAndDirectoryCap()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            archive.Clock = () => new DateTime(2026, 8, 14, 12, 0, 0);
            archive.RetainDays = 90;
            archive.MaxDirectoryBytes = 100;
            archive.Configure(dir, enabled: true);

            string tooOld = Path.Combine(dir, "dealer-20260101.idx.ndjson.gz");
            string oldestCap = Path.Combine(dir, "dealer-20260801.bin.gz");
            string newest = Path.Combine(dir, "dealer-20260813.bin.gz");
            File.WriteAllBytes(tooOld, new byte[80]);
            File.WriteAllBytes(oldestCap, new byte[80]);
            File.WriteAllBytes(newest, new byte[80]);
            File.SetLastWriteTimeUtc(tooOld, new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(oldestCap, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newest, new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc));

            archive.Configure(dir, enabled: true);

            Assert.False(File.Exists(tooOld));
            Assert.False(File.Exists(oldestCap));
            Assert.True(File.Exists(newest));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void RecordInbound_DoesNotThrow_WhenWriterCannotOpenFiles()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            string blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "x");
            archive.Configure(Path.Combine(blocker, "sub"), enabled: true);
            byte[] frame = "{\"type\":\"message\",\"uri\":\"hm://artist/v1/x\"}"u8.ToArray();
            archive.RecordInbound(frame, DealerFrameType.Message, "hm://artist/v1/x", null, null);
            archive.FlushForTests();
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void Disabled_WritesNothing()
    {
        string dir = NewTempDir();
        using var archive = NewArchive();
        try
        {
            archive.Configure(dir, enabled: false);
            archive.RecordInbound("{\"type\":\"message\",\"uri\":\"hm://artist/v1/x\"}"u8, DealerFrameType.Message,
                "hm://artist/v1/x", null, null);
            archive.FlushForTests();
            Assert.Empty(Directory.GetFiles(dir, "dealer-*"));
        }
        finally { TryDelete(dir); }
    }

    static DealerArchive NewArchive() => new() { SyncDrainForTests = true };

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-dealer-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    static string Single(string dir, string glob)
    {
        var files = Directory.GetFiles(dir, glob);
        Assert.True(files.Length == 1, "expected 1 " + glob + " in " + dir + ", got " + files.Length);
        return files[0];
    }

    static string[] ReadLines(string path)
    {
        using var fs = OpenRead(path);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } line)
            if (line.Length > 0) lines.Add(line);
        return lines.ToArray();
    }

    static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    static void AssertRoundtripGzip(string gzPath)
    {
        using var input = File.OpenRead(gzPath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var sr = new StreamReader(gzip);
        string text = sr.ReadToEnd();
        Assert.Contains("\"typ\":\"message\"", text, StringComparison.Ordinal);
    }
}
