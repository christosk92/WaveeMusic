// ── Playback/Playback.Os.JumpListWorker.cs ────────────────────────────────────────────────────────────────────────
// The jump list's COM transaction, moved off the UI thread (#115).
//
// WHY A THREAD OF ITS OWN. One `EngineJumpList.SetCategory` is a `BeginList` (it opens the per-AUMID
// `.customDestinations-ms` file and QIs every item the user removed), a `CoCreateInstance(CLSID_ShellLink)` plus an
// `IPropertyStore` round trip per row, and a `CommitList` that rewrites the file: 10-90 ms measured, on EVERY play/pause
// edge and three or four times per launch. None of it needs the WINDOW thread — a jump list is keyed by AUMID, not by
// HWND, and the engine type creates and releases its COM objects inside each call. It needs AN STA:
// `CLSID_DestinationList` and `CLSID_EnumerableObjectCollection` register `ThreadingModel=Apartment`, so an MTA caller
// would get them in a COM-made host STA and pay a proxy per call. Hence one dedicated, lazily started, below-normal STA
// thread that owns every jump-list write for the life of the process. SMTC (`GetForWindow(hwnd)`) and the taskbar
// button (`ITaskbarList3`, HWND-bound) stay on the window thread; they are not this file's business.
//
// WHAT STAYS ON THE UI THREAD. Building the write: the phase read, the localized strings, the play log and the nav
// history (UI-owned stores). That part is a fraction of a millisecond; only the immutable `JumpListWrite` crosses over.
//
// NO QUEUE GROWTH. The mailbox holds at most ONE pending write per AUMID target and the newest one wins, so a burst of
// play/pause edges costs one transaction for the state the user ended on, not one per edge. A write whose content equals
// the last one COMMITTED for its target is skipped outright (a pause that is undone before the worker gets to it costs
// nothing). Different targets keep their arrival order — the toast layer's AUMID can land after the first publish went
// out under the process default, and both lists are written exactly as they were before this file existed.

using FluentGpu.WindowsApi.Shell;

using EngineJumpList = FluentGpu.WindowsApi.Shell.JumpList;

namespace Wavee;

public static partial class Playback
{
    public static partial class Os
    {
        /// <summary>One whole jump-list write for one AUMID target: either a full publish (the tasks plus the "Jump back
        /// in" category, one Begin/Commit transaction) or a clear. Immutable and compared by CONTENT, which is what lets
        /// the worker skip a write that would commit exactly what the shell already has.</summary>
        public sealed class JumpListWrite : IEquatable<JumpListWrite>
        {
            JumpListWrite(string? aumid, bool clear, string category, JumpListItem[] items, JumpTask[] tasks)
            {
                Aumid = aumid;
                IsClear = clear;
                Category = category;
                Items = items;
                Tasks = tasks;
            }

            /// <summary>The target list's AUMID; null is the process default (the shell's own association).</summary>
            public string? Aumid { get; }

            /// <summary>Delete the custom list instead of publishing one (sign-out).</summary>
            public bool IsClear { get; }

            public string Category { get; }

            public JumpListItem[] Items { get; }

            public JumpTask[] Tasks { get; }

            /// <summary>The mailbox key: one pending write per target, ordinal (an AUMID is an identity, not text).</summary>
            public string Target => Aumid ?? "";

            public static JumpListWrite Publish(string category, JumpListItem[] items, JumpTask[] tasks, string? aumid)
                => new(aumid, false, category, items, tasks);

            public static JumpListWrite Clear(string? aumid) => new(aumid, true, "", [], []);

            public bool Equals(JumpListWrite? other)
                => other is not null
                   && IsClear == other.IsClear
                   && string.Equals(Aumid, other.Aumid, StringComparison.Ordinal)
                   && string.Equals(Category, other.Category, StringComparison.Ordinal)
                   && Items.AsSpan().SequenceEqual(other.Items)
                   && Tasks.AsSpan().SequenceEqual(other.Tasks);

            public override bool Equals(object? obj) => Equals(obj as JumpListWrite);

            public override int GetHashCode() => HashCode.Combine(Aumid, IsClear, Category, Items.Length, Tasks.Length);
        }

        /// <summary>The worker's pending set, as a pure data structure (no lock, no thread — the worker holds the lock),
        /// so the coalescing and ordering rules are a unit test. At most one pending write per target, newest wins and
        /// keeps its target's place in line; a write equal to the target's last COMMITTED one is dropped at take time; a
        /// failed write is not remembered, so the next identical write retries it.</summary>
        public sealed class JumpListMailbox
        {
            /// <summary>Distinct AUMID targets that may wait at once. Two exist in practice (the process default, then
            /// the toast layer's); past the cap the OLDEST waiting target is dropped, so nothing here can grow.</summary>
            public const int MaxTargets = 4;

            readonly List<JumpListWrite> _pending = new(MaxTargets);
            readonly Dictionary<string, JumpListWrite> _committed = new(StringComparer.Ordinal);

            /// <summary>Writes waiting (never more than <see cref="MaxTargets"/>).</summary>
            public int Count => _pending.Count;

            /// <summary>Writes replaced by a newer one for the same target before they ran.</summary>
            public int Coalesced { get; private set; }

            /// <summary>Writes dropped because the target already shows exactly that content.</summary>
            public int Skipped { get; private set; }

            public void Offer(JumpListWrite write)
            {
                ArgumentNullException.ThrowIfNull(write);
                for (int i = 0; i < _pending.Count; i++)
                {
                    if (!string.Equals(_pending[i].Target, write.Target, StringComparison.Ordinal)) continue;
                    _pending[i] = write;
                    Coalesced++;
                    return;
                }
                if (_pending.Count == MaxTargets)
                {
                    _pending.RemoveAt(0);
                    Coalesced++;
                }
                _pending.Add(write);
            }

            /// <summary>The next write worth running, in target arrival order, or false when nothing is.</summary>
            public bool TryTake(out JumpListWrite write)
            {
                while (_pending.Count > 0)
                {
                    write = _pending[0];
                    _pending.RemoveAt(0);
                    if (_committed.TryGetValue(write.Target, out JumpListWrite? last) && last.Equals(write))
                    {
                        Skipped++;
                        continue;
                    }
                    return true;
                }
                write = null!;
                return false;
            }

            /// <summary>Report how a taken write ended. Only a success becomes the target's committed content; a
            /// failure forgets it, so the shell's real state is never assumed.</summary>
            public void Done(JumpListWrite write, bool committed)
            {
                if (committed) _committed[write.Target] = write;
                else _committed.Remove(write.Target);
            }
        }

        /// <summary>The dedicated STA thread that runs every jump-list write (see the file header). Started on the first
        /// <see cref="Submit"/>, never before — a launch that publishes nothing spins nothing up. Thread-safe; the apply
        /// callback is injectable so the threading is a unit test without COM.</summary>
        public sealed class JumpListWorker
        {
            /// <summary>How long <see cref="Shutdown"/> lets the last pending write finish. One transaction is
            /// 10-90 ms; this is the ceiling the exit path pays when Explorer is slow, never a wait on a hung shell.</summary>
            public const int ShutdownJoinMs = 1_000;

            readonly object _gate = new();
            readonly JumpListMailbox _mailbox = new();
            readonly Func<JumpListWrite, bool> _apply;
            readonly string _name;
            Thread? _thread;
            bool _stopping;

            public JumpListWorker(Func<JumpListWrite, bool> apply, string name = "Wavee.ShellSTA")
            {
                _apply = apply ?? throw new ArgumentNullException(nameof(apply));
                _name = name;
            }

            /// <summary>The mailbox's counters, for tests and diagnostics.</summary>
            public (int Pending, int Coalesced, int Skipped) Stats
            {
                get { lock (_gate) return (_mailbox.Count, _mailbox.Coalesced, _mailbox.Skipped); }
            }

            /// <summary>Hand a write to the STA thread and return at once. After <see cref="Shutdown"/> it is dropped.</summary>
            public void Submit(JumpListWrite write)
            {
                ArgumentNullException.ThrowIfNull(write);
                lock (_gate)
                {
                    if (_stopping) return;
                    _mailbox.Offer(write);
                    if (_thread is null && !TryStart()) return;
                    Monitor.Pulse(_gate);
                }
            }

            /// <summary>Stop taking writes, let what is pending finish, and wait at most <paramref name="joinMs"/> for the
            /// thread. The jump list outlives the process by design, so its LAST state is worth the bounded wait.
            /// Idempotent. Returns false when the thread was still busy at the deadline (it is a background thread
            /// and dies with the process).</summary>
            public bool Shutdown(int joinMs = ShutdownJoinMs)
            {
                Thread? t;
                lock (_gate)
                {
                    _stopping = true;
                    Monitor.PulseAll(_gate);
                    t = _thread;
                }
                if (t is null || t == Thread.CurrentThread) return true;
                return t.Join(joinMs);
            }

            bool TryStart()
            {
                try
                {
                    var t = new Thread(Pump) { IsBackground = true, Name = _name, Priority = ThreadPriority.BelowNormal };
                    if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);   // BEFORE Start
                    t.Start();
                    _thread = t;
                    return true;
                }
                catch (Exception ex)
                {
                    // Fail-soft: no thread means no jump list this session, never a crash on the play/pause path.
                    _stopping = true;
                    Log.Warn("playback", "jump list worker could not start; the jump list is not updated this run", ex);
                    return false;
                }
            }

            void Pump()
            {
                while (true)
                {
                    JumpListWrite write;
                    lock (_gate)
                    {
                        while (!_mailbox.TryTake(out write))
                        {
                            if (_stopping) return;
                            Monitor.Wait(_gate);
                        }
                    }
                    bool ok;
                    try { ok = _apply(write); }
                    catch (Exception ex)
                    {
                        Log.Warn("playback", "jump list publish failed", ex);
                        ok = false;
                    }
                    lock (_gate) _mailbox.Done(write, ok);
                }
            }
        }

        /// <summary>The process's one jump-list worker, over the engine's COM transaction.</summary>
        static readonly JumpListWorker s_jumpListWorker = new(ApplyJumpListWrite);

        /// <summary>Runs ON the STA worker. The engine type's <c>EnsureSta</c> finds the apartment already set
        /// (<c>S_FALSE</c>) and creates every COM object on this thread, so no call is marshalled. Fail-soft: a shell
        /// that refuses is logged and the write is not remembered as committed.</summary>
        static bool ApplyJumpListWrite(JumpListWrite w)
        {
            try
            {
                if (w.IsClear) EngineJumpList.Clear(w.Aumid);
                else EngineJumpList.SetCategory(w.Category, w.Items, w.Tasks, w.Aumid);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("playback", w.IsClear ? "jump list clear failed" : "jump list publish failed", ex);
                return false;
            }
        }
    }
}
