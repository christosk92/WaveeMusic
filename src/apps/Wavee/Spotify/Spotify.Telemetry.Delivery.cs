using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Win32;
using Ev = Wavee.Protocol.EventSender;
using Evt = Wavee.Protocol.EventSender.Events;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Telemetry
    {
        readonly record struct GaboWork(Ev.EventEnvelope? Event, string Account = "", TaskCompletionSource? Completion = null);
        static readonly Lock GaboJournalGate = new();
        static readonly Dictionary<string, List<Ev.EventEnvelope>> GaboJournals = new(StringComparer.Ordinal);
        static string s_pendingAccount = "";
        static readonly Dictionary<string, long> EventSequences = new(StringComparer.Ordinal);
        static readonly Lock SequenceGate = new();
        static byte[] s_sequenceId = [];
        static long s_monotonicClockId;
        static string s_endpointId = "", s_endpointName = "", s_routeType = "";

        static string TelemetryIdentityPath => Path.Combine(Platform.LocalFolder, "telemetry", "identity");

        static void LoadTelemetryIdentity()
        {
            lock (SequenceGate)
            {
                try
                {
                    if (ReadJournal(TelemetryIdentityPath) is { } identity)
                    {
                        using var r = new BinaryReader(new MemoryStream(identity));
                        if (r.ReadInt32() != 1) throw new InvalidDataException("Unknown telemetry identity.");
                        s_sequenceId = r.ReadBytes(20);
                        if (s_sequenceId.Length != 20) throw new InvalidDataException("Invalid telemetry identity.");
                        s_monotonicClockId = checked(r.ReadInt64() + 1);
                        int n = ReadCount(r, 4096);
                        for (int i = 0; i < n; i++) EventSequences[r.ReadString()] = r.ReadInt64() + GaboSequenceResumeMargin;
                    }
                    else
                    {
                        s_sequenceId = RandomNumberGenerator.GetBytes(20);
                        s_monotonicClockId = 1;
                    }
                    SaveTelemetryIdentity();
                }
                catch (Exception ex)
                {
                    s_sequenceId = RandomNumberGenerator.GetBytes(20);
                    s_monotonicClockId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Log.Warn("spotify", "telemetry identity unavailable; using fresh sequence identity", ex);
                }
            }
        }

        static void SaveTelemetryIdentity()
        {
            lock (SequenceGate)
            {
                try
                {
                    string path = TelemetryIdentityPath;
                    using var stream = new MemoryStream();
                    using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                    {
                        w.Write(1); w.Write(s_sequenceId); w.Write(s_monotonicClockId); w.Write(EventSequences.Count);
                        foreach (var (name, sequence) in EventSequences) { w.Write(name); w.Write(sequence + GaboSequenceResumeMargin); }
                    }
                    QueueJournal(path, stream.ToArray());
                }
                catch (Exception ex) { Log.Warn("spotify", "telemetry sequence identity not persisted", ex); }
            }
        }

        static long NextEventSequence(string eventName)
        {
            lock (SequenceGate)
            {
                long next = EventSequences.GetValueOrDefault(eventName) + 1;
                EventSequences[eventName] = next;
                if (next % GaboSequenceResumeMargin == 1) SaveTelemetryIdentity();
                return next;
            }
        }

        public static int[] RetryGaboIndices(Ev.PublishEventsResponse response, int sentCount, out int rejected)
        {
            rejected = 0;
            var retry = new SortedSet<int>();
            foreach (var error in response.Error)
            {
                if (error.Index < 0 || error.Index >= sentCount)
                    throw new InvalidDataException("Gabo error index outside request.");
                if (error.Transient) retry.Add(error.Index);
                else rejected++;
            }
            return retry.ToArray();
        }

        static bool SameEvent(Ev.EventEnvelope left, Ev.EventEnvelope right)
            => left.EventName == right.EventName && left.SequenceNumber == right.SequenceNumber && left.SequenceId.Equals(right.SequenceId);

        static void RestoreGabo(string account)
        {
            if (account.Length == 0) return;
            lock (GaboJournalGate)
            {
                if (!GaboJournals.TryGetValue(account, out var pending))
                {
                    pending = new List<Ev.EventEnvelope>();
                    GaboJournals[account] = pending;
                    try
                    {
                        string path = JournalPath(account, "gabo");
                        if (ReadJournal(path) is { } bytes)
                            pending.AddRange(Ev.PublishEventsRequest.Parser.ParseFrom(bytes).Event.Take(GaboBacklogCap));
                    }
                    catch (Exception ex) { Log.Warn("spotify", "gabo journal could not be restored", ex); }
                }
                foreach (var item in pending)
                    if (!GaboQueue.TryAdd(new GaboWork(item, account))) break;
            }
        }

        static void JournalGabo(string account, Ev.EventEnvelope envelope)
        {
            if (account.Length == 0) return;
            lock (GaboJournalGate)
            {
                if (!GaboJournals.TryGetValue(account, out var pending))
                    GaboJournals[account] = pending = new List<Ev.EventEnvelope>();
                pending.Add(envelope);
                int drop = Overflow(pending.Count);
                if (drop > 0) { pending.RemoveRange(0, drop); Interlocked.Add(ref s_gaboDropped, drop); }
                SaveGaboJournal(account, pending);
            }
        }

        static void AcknowledgeGabo(string account, IReadOnlyList<Ev.EventEnvelope> sent, IReadOnlyList<int> retry)
        {
            lock (GaboJournalGate)
            {
                if (!GaboJournals.TryGetValue(account, out var pending)) return;
                for (int i = 0; i < sent.Count; i++)
                {
                    if (retry.Contains(i)) continue;
                    var envelope = sent[i];
                    pending.RemoveAll(x => SameEvent(x, envelope));
                }
                SaveGaboJournal(account, pending);
            }
        }

        static void SaveGaboJournal(string account, List<Ev.EventEnvelope> pending)
        {
            try
            {
                string path = JournalPath(account, "gabo");
                var request = new Ev.PublishEventsRequest();
                request.Event.AddRange(pending);
                QueueJournal(path, request.ToByteArray());
            }
            catch (Exception ex) { Log.Warn("spotify", "gabo journal could not be persisted", ex); }
        }

        static string DeviceModel()
        {
            try
            {
                using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                return bios?.GetValue("SystemProductName") as string ?? Environment.MachineName;
            }
            catch { return Environment.MachineName; }
        }

        public static void AudioRoute(string endpointId, string endpointName, string routeType)
        {
            if (s_endpointId == endpointId && s_endpointName == endpointName && s_routeType == routeType) return;
            s_endpointId = endpointId; s_endpointName = endpointName; s_routeType = routeType;
            Enqueue("WasapiAudioDriverInfo", new Evt.WasapiAudioDriverInfo
            { DriverName = "WASAPI", OutputDeviceName = endpointName }.ToByteArray());
        }

        public static void RateChanged(ref Registration r, long positionMs, double rate)
        {
            if (!double.IsFinite(rate) || rate <= 0 || r.PlaybackSpeed == rate) return;
            if (r.Open && r.SegmentActive)
                CloseSegment(ref r, (int)Math.Clamp(positionMs, 0, int.MaxValue), false, false, "speed-change");
            r.PlaybackSpeed = rate;
        }

        public const int ShutdownBudgetMs = 2000;

        static void ShutdownBounded()
        {
            var gabo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!GaboQueue.TryAdd(new GaboWork(null, Completion: gabo))) gabo.TrySetResult();
            if (!Api.Run(() => { try { FlushResumePoints(); } finally { progress.TrySetResult(); } }))
                progress.TrySetResult();
            long started = Stopwatch.GetTimestamp();
            bool sent = Task.WhenAll(gabo.Task, progress.Task).Wait(ShutdownBudgetMs);
            int remaining = Math.Max(0, ShutdownBudgetMs - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            bool persisted = JournalIdle.Wait(remaining);
            if (!sent || !persisted)
                Log.Warn("spotify", "telemetry shutdown deadline reached; sent=" + sent + " persisted=" + persisted);
        }
    }
}
