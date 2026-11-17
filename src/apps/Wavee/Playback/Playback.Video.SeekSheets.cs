// ── Playback/Playback.Video.SeekSheets.cs ──────────────────────────────────────────────────────────────────────────
// the seek-preview sheets ON DISK: a sheet's signed url → one local file, fetched once, found again under a key that
// ignores the signature. NAMED PARTIAL of Playback/Playback.Video.cs.
//
// Role: SHELL (files, HTTP, a marshal to the UI thread)
// Spec: docs/plans/wavee/video-seek-preview-implementation.md §3
//
// WHY NOT THE IMAGE CACHE. The engine's image pipeline keys an image by its whole source string, in memory and on disk
// (`ImageCache.SourceKey`, `DiskImageCache.Resolve`). A sheet's url ends in a signature that changes with every manifest
// fetch (ten minutes), so the same sheet would miss, download and be stored again each time, and a signature that
// expired fails for good under that key. Here the KEY is the url without its query, the picture binds the local FILE
// (a stable source: the engine's file branch touches neither network nor disk cache), and a new signature for a sheet
// already on disk costs nothing.
//
// THREADS. `Source` and `Prefetch` are the UI thread's (they own the cells). The fetch runs on the pool, three at a
// time, and comes back through `ToUi` to set the cell. A local file template (the inspector's fixture) passes straight
// through and touches none of this.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using FluentGpu.Media;
using FluentGpu.Signals;

namespace Wavee;

public static partial class Playback
{
    public static partial class Video
    {
        /// <summary>The seek-preview sheets on disk.</summary>
        public static class SeekSheets
        {
            /// <summary>The cells a session keeps (a video is 7-40 coarse sheets; full means start over, never grow).</summary>
            const int CellCap = 512;

            /// <summary>How many sheets a tip asks for ahead of the pointer when it opens: a whole three-hour podcast's coarse set.</summary>
            public const int PrefetchCap = 48;

            /// <summary>A sheet that failed (a refused signature, no network) is not asked for again this soon.</summary>
            const long RetryMs = 30_000;

            /// <summary>The folder's budget and age: past either, the oldest sheets go (once per process, on the pool).</summary>
            const long BudgetBytes = 96L * 1024 * 1024;
            static readonly TimeSpan s_maxAge = TimeSpan.FromDays(14);

            static readonly Dictionary<string, Signal<string>> s_cells = new(StringComparer.Ordinal);   // UI thread
            static readonly ConcurrentDictionary<string, long> s_inFlight = new(StringComparer.Ordinal);
            static readonly ConcurrentDictionary<string, long> s_failedAt = new(StringComparer.Ordinal);
            static readonly SemaphoreSlim s_gate = new(3);
            static int s_trimmed;

            static DefaultImageFetcher? s_fetcher;

            /// <summary>The fetcher: the app's image client (logged through <c>Wire</c> as "images", path only), and NO disk cache of its
            /// own: the file this writes is the cache. Replaceable (the inspector, which runs with the wire refused, lends a plain client
            /// to the one mount that asks for real sheets).</summary>
            public static DefaultImageFetcher Fetcher
            {
                get => s_fetcher ??= new DefaultImageFetcher(
                    http: DefaultImageFetcher.CreateClient(static inner => global::Wavee.Wire.Handler("images", inner, storms: false)), diskCache: null);
                set => s_fetcher = value;
            }

            /// <summary>Where the sheets live: inside the image cache's folder, so the Storage tab counts them with the images.</summary>
            public static string Folder => Path.Combine(Settings.ImageCacheDirectory(), "seekpanels");

            public static bool IsRemote(string url)
                => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

            /// <summary>A sheet's identity: its url without the signature.</summary>
            public static string KeyOf(string url)
            {
                int query = url.IndexOf('?');
                return query < 0 ? url : url[..query];
            }

            /// <summary>The file a key is kept in.</summary>
            public static string PathOf(string key)
                => Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".jpg");

            /// <summary>What a sheet's picture binds as its source. A local file (the fixture's template) is itself; a url is a cell
            /// that holds nothing until the sheet is on disk and then its file's path, so the picture stays transparent (the theme's
            /// plate shows) and then decodes from a local file. UI thread.</summary>
            public static Prop<string> Source(string url)
            {
                if (!IsRemote(url)) return url;
                Signal<string> cell = Cell(url);
                return Prop.Of(() => cell.Value);
            }

            /// <summary>Ask for a variant's sheets ahead of the pointer: the one under it first, then outwards. UI thread.</summary>
            public static void Prefetch(SeekPanels sheets, SeekPanels.Variant variant, int around)
            {
                int panels = variant.Panels;
                if (panels <= 0 || !IsRemote(sheets.Url(variant, 0))) return;
                int asked = 0;
                for (int d = 0; d < panels && asked < PrefetchCap; d++)
                {
                    int after = around + d, before = around - d;
                    if (after < panels) { _ = Cell(sheets.Url(variant, after)); asked++; }
                    if (d > 0 && before >= 0) { _ = Cell(sheets.Url(variant, before)); asked++; }
                }
            }

            static Signal<string> Cell(string url)
            {
                string key = KeyOf(url);
                if (!s_cells.TryGetValue(key, out Signal<string>? cell))
                {
                    if (s_cells.Count >= CellCap) s_cells.Clear();
                    cell = new Signal<string>("");
                    s_cells[key] = cell;
                }
                if (cell.Peek().Length == 0) Ensure(url, key, cell);
                return cell;
            }

            /// <summary>Put the sheet on disk (when it is not) and publish its path. One flight per key; a failure waits
            /// <see cref="RetryMs"/> before the next try, which uses whatever url (signature) is current then.</summary>
            static void Ensure(string url, string key, Signal<string> cell)
            {
                long now = Environment.TickCount64;
                if (s_failedAt.TryGetValue(key, out long failed) && now - failed < RetryMs) return;
                if (!s_inFlight.TryAdd(key, now)) return;
                if (Interlocked.Exchange(ref s_trimmed, 1) == 0) _ = Task.Run(Trim);
                _ = Task.Run(async () =>
                {
                    string path = PathOf(key);
                    try
                    {
                        if (!File.Exists(path) && !await Download(url, path).ConfigureAwait(false))
                        {
                            s_failedAt[key] = Environment.TickCount64;
                            return;
                        }
                        s_failedAt.TryRemove(key, out _);
                        ToUi(() => cell.Value = path);
                    }
                    catch (Exception ex)
                    {
                        s_failedAt[key] = Environment.TickCount64;
                        Log.Warn("video", "seek sheet " + Path.GetFileName(key) + " failed", ex);
                    }
                    finally { s_inFlight.TryRemove(key, out _); }
                });
            }

            static async Task<bool> Download(string url, string path)
            {
                await s_gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (File.Exists(path)) return true;
                    long t0 = Environment.TickCount64;
                    DefaultImageFetcher fetcher = Fetcher;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    FetchResult fetched = await fetcher.FetchAsync(url, cts.Token).ConfigureAwait(false);
                    if (!fetched.Ok || fetched.Buffer is not { } buffer)
                    {
                        // The key's tail only: the url carries a signature and must never reach a log.
                        Log.Info("video", $"[video.seek] sheet.fail sheet={Tail(KeyOf(url))} failure={fetched.Failure} ms={Environment.TickCount64 - t0}");
                        if (fetched.Buffer is { } spare) fetcher.ReturnBuffer(spare);
                        return false;
                    }
                    try
                    {
                        Directory.CreateDirectory(Folder);
                        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        using (FileStream file = File.Create(temp)) file.Write(buffer, 0, fetched.Length);
                        File.Move(temp, path, overwrite: true);
                    }
                    finally { fetcher.ReturnBuffer(buffer); }
                    Log.Info("video", $"[video.seek] sheet.ok sheet={Tail(KeyOf(url))} bytes={fetched.Length} ms={Environment.TickCount64 - t0}");
                    return true;
                }
                finally { s_gate.Release(); }
            }

            /// <summary>"variant/panel.jpg": enough to tell sheets apart in a log, and none of the signature.</summary>
            static string Tail(string key)
            {
                int last = key.LastIndexOf('/');
                int before = last > 0 ? key.LastIndexOf('/', last - 1) : -1;
                return before >= 0 ? key[(before + 1)..] : key;
            }

            /// <summary>Keep the folder inside its age and budget: the oldest files go first. Best effort, once per process.</summary>
            static void Trim()
            {
                try
                {
                    var dir = new DirectoryInfo(Folder);
                    if (!dir.Exists) return;
                    FileInfo[] files = dir.GetFiles();
                    Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                    long total = 0;
                    foreach (FileInfo f in files) total += f.Length;
                    DateTime oldest = DateTime.UtcNow - s_maxAge;
                    foreach (FileInfo f in files)
                    {
                        bool stale = f.LastWriteTimeUtc < oldest || f.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase);
                        if (!stale && total <= BudgetBytes) break;
                        total -= f.Length;
                        try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) { Log.Warn("video", "seek sheet trim failed", ex); }
            }
        }
    }
}
