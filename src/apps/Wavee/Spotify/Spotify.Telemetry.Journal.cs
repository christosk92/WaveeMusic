using System.Collections.Concurrent;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Telemetry
    {
        // Only this worker touches journal files on writes. Producers replace the latest immutable snapshot per path.
        static readonly ConcurrentDictionary<string, byte[]> JournalWrites = new(StringComparer.Ordinal);
        static readonly AutoResetEvent JournalWake = new(false);
        static readonly ManualResetEventSlim JournalIdle = new(true);
        static int s_journalStarted;
        static readonly Lock JournalStateGate = new();

        static void QueueJournal(string path, byte[] bytes)
        {
            if (Interlocked.CompareExchange(ref s_journalStarted, 1, 0) == 0)
                new Thread(JournalLoop) { IsBackground = true, Name = "wavee-telemetry-journal" }.Start();
            lock (JournalStateGate)
            {
                JournalIdle.Reset();
                JournalWrites[path] = bytes;
                JournalWake.Set();
            }
        }

        static byte[]? ReadJournal(string path)
            => JournalWrites.TryGetValue(path, out var latest) ? latest
                : File.Exists(path) ? File.ReadAllBytes(path) : null;

        static void JournalLoop()
        {
            while (true)
            {
                JournalWake.WaitOne();
                foreach (string path in JournalWrites.Keys)
                {
                    if (!JournalWrites.TryGetValue(path, out byte[]? bytes)) continue;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            stream.Write(bytes);
                            stream.Flush(flushToDisk: true);
                        }
                        File.Move(path + ".tmp", path, overwrite: true);
                        ((ICollection<KeyValuePair<string, byte[]>>)JournalWrites).Remove(new(path, bytes));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("spotify", "telemetry journal write failed; latest snapshot retained in memory", ex);
                    }
                }
                lock (JournalStateGate)
                    if (JournalWrites.IsEmpty) JournalIdle.Set();
            }
        }
    }
}
