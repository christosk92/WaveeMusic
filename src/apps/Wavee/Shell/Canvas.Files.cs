// ── Shell/Canvas.Files.cs — the canvas videos on disk and the offline index (a named partial of Shell/Canvas.cs) ─────────────────────────
//
// THE VIDEOS. `<LocalFolder>\cache\canvas\<sha256(url)>.mp4`, fetched once with the app's logged HTTP stack (`Wire.Handler("canvas")`) and
// played from disk, so a loop never re-reads the network and a repeat play costs nothing. Least recently used first out past
// <see cref="BudgetBytes"/>; a file in use is skipped. The folder is under LocalFolder, so a factory reset clears it, and Settings ▸
// Storage counts it with the rest of the app's data.
//
// THE INDEX. `index.tsv` beside them: one line per track the app has seen a canvas for (the record as text, with its fetch stamp and TTLs),
// so a canvas shows with no network: fresh, it answers without asking; offline, up to its offline TTL (30 days on the wire). Read once off
// the UI thread at mount; written off the UI thread on each change. Rows past their offline TTL are dropped at load.
//
// THREADS. Every table here is UI-thread only; the disk and the network run on the pool and post back.

using System.Security.Cryptography;
using System.Text;
using FluentGpu.Signals;

namespace Wavee;

public static partial class CanvasVideo
{
    internal static class Files
    {
        /// <summary>What the canvas videos may hold on disk (Spotify's canvases run 1-4 MB each).</summary>
        public const long BudgetBytes = 256L << 20;

        /// <summary>Index rows kept (the most recent).</summary>
        public const int MaxIndexRows = 2000;

        /// <summary>Bumps when a download lands (UI thread).</summary>
        public static readonly Signal<int> Epoch = new(0);

        /// <summary>Bumps when the disk index has been read (UI thread).</summary>
        public static readonly Signal<int> IndexEpoch = new(0);

        static Action<Action>? s_post;
        static readonly Dictionary<string, string> s_ready = new(StringComparer.Ordinal);
        static readonly HashSet<string> s_inflight = new(StringComparer.Ordinal), s_failed = new(StringComparer.Ordinal);
        static Dictionary<string, CanvasText>? s_index;
        static HttpClient? s_http;
        static readonly object s_saveGate = new();

        public static string Folder => Path.Combine(Platform.LocalFolder, "cache", "canvas");
        static string IndexPath => Path.Combine(Folder, "index.tsv");

        /// <summary>The observer's mount: the UI poster, then the index read. Idempotent.</summary>
        public static void Install(Action<Action> post)
        {
            if (s_post is not null) return;
            s_post = post;
            _ = Task.Run(LoadIndex);
        }

        /// <summary>Run on the UI thread.</summary>
        public static void Post(Action a)
        {
            if (s_post is { } p) p(a);
            else a();
        }

        /// <summary>The track's canvas from the disk index, or null. UI thread.</summary>
        public static CanvasText? Indexed(string trackUri)
            => s_index is { } index && index.TryGetValue(trackUri, out var c) ? c : null;

        /// <summary>Keep the track's canvas in the index (value-gated: the same answer writes nothing). UI thread.</summary>
        public static void Remember(string trackUri, in CanvasEdge c)
        {
            if (s_index is not { } index || Platform.Args.Fake) return;
            if (index.TryGetValue(trackUri, out var held) && held.FetchedAt == c.FetchedAt && held.Url == Entities.Strings.Resolve(c.Url)) return;
            index[trackUri] = CanvasText.Of(c);
            SaveIndex(index);
        }

        /// <summary>The local file for <paramref name="url"/>, or "" while it is not on disk yet (a fetch is started; <see cref="Epoch"/>
        /// bumps when it lands). A rooted local path (the fake catalog) is its own file. UI thread.</summary>
        public static string Ensure(string url, bool priority)
        {
            if (url.Length == 0) return "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return Path.IsPathRooted(url) ? url : "";
            if (s_ready.TryGetValue(url, out var ready)) return ready;
            if (s_failed.Contains(url) || !s_inflight.Add(url)) return "";
            _ = Task.Run(() => Fetch(url, priority));
            return "";
        }

        static async Task Fetch(string url, bool priority)
        {
            string? path = null;
            try
            {
                Directory.CreateDirectory(Folder);
                string target = Path.Combine(Folder, NameOf(url));
                if (File.Exists(target))
                {
                    File.SetLastWriteTimeUtc(target, DateTime.UtcNow);   // the LRU stamp
                    path = target;
                }
                else
                {
                    s_http ??= new HttpClient(Wire.Handler("canvas", new SocketsHttpHandler
                    {
                        AutomaticDecompression = System.Net.DecompressionMethods.All,
                        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    }, storms: false)) { Timeout = TimeSpan.FromSeconds(60) };
                    string part = target + ".part";
                    using (var response = await s_http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Log.Warn("canvas", "canvas.fetch status=" + (int)response.StatusCode + " host=" + new Uri(url).Host);
                        }
                        else
                        {
                            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                                await response.Content.CopyToAsync(file).ConfigureAwait(false);
                            File.Move(part, target, overwrite: true);
                            path = target;
                            if (Log.IsEnabled(WaveeLogLevel.Info))
                                Log.Info("canvas", "canvas.fetched bytes=" + new FileInfo(target).Length + " priority=" + priority + " host=" + new Uri(url).Host);
                            Trim(keep: target);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("canvas", "canvas.fetch failed " + ex.GetType().Name + ": " + ex.Message);
            }
            string? landed = path;
            Post(() =>
            {
                s_inflight.Remove(url);
                if (landed is not null) s_ready[url] = landed;
                else s_failed.Add(url);   // not again this session: the poster stands in
                Epoch.Value = Epoch.Peek() + 1;
            });
        }

        /// <summary>Least recently used out until the folder is under budget. Pool thread.</summary>
        static void Trim(string keep)
        {
            try
            {
                var files = new DirectoryInfo(Folder).GetFiles("*.mp4");
                long total = 0;
                foreach (var f in files) total += f.Length;
                if (total <= BudgetBytes) return;
                Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (var f in files)
                {
                    if (total <= BudgetBytes) break;
                    if (string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { long n = f.Length; f.Delete(); total -= n; }
                    catch (IOException) { }                  // open in the player: next time
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) { Log.Warn("canvas", "canvas.trim failed " + ex.GetType().Name); }
        }

        static string NameOf(string url)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(url), hash);
            return Convert.ToHexStringLower(hash) + ".mp4";
        }

        // ── the index ──────────────────────────────────────────────────────────────────────────────────────────────────

        static void LoadIndex()
        {
            var index = new Dictionary<string, CanvasText>(StringComparer.Ordinal);
            try
            {
                if (!Platform.Args.Fake && File.Exists(IndexPath))
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    foreach (string line in File.ReadLines(IndexPath))
                        if (Parse(line) is { } row && row.Text.OfflineFresh(now)) index[row.Uri] = row.Text;
                }
            }
            catch (Exception ex) { Log.Warn("canvas", "canvas.index read failed " + ex.GetType().Name); }
            Post(() =>
            {
                s_index = index;
                IndexEpoch.Value = IndexEpoch.Peek() + 1;
            });
        }

        static void SaveIndex(Dictionary<string, CanvasText> index)
        {
            var rows = new List<KeyValuePair<string, CanvasText>>(index);
            if (rows.Count > MaxIndexRows)
            {
                rows.Sort(static (a, b) => b.Value.FetchedAt.CompareTo(a.Value.FetchedAt));
                rows.RemoveRange(MaxIndexRows, rows.Count - MaxIndexRows);
            }
            _ = Task.Run(() =>
            {
                // One writer at a time, each with the snapshot it was handed (the last one queued is the newest).
                lock (s_saveGate)
                try
                {
                    Directory.CreateDirectory(Folder);
                    var sb = new StringBuilder(rows.Count * 256);
                    foreach (var (uri, c) in rows) Line(sb, uri, c);
                    string temp = IndexPath + ".tmp";
                    File.WriteAllText(temp, sb.ToString(), Encoding.UTF8);
                    File.Move(temp, IndexPath, overwrite: true);
                }
                catch (Exception ex) { Log.Warn("canvas", "canvas.index write failed " + ex.GetType().Name); }
            });
        }

        const char Tab = '\t';

        static void Line(StringBuilder sb, string uri, CanvasText c)
        {
            sb.Append(Clean(uri)).Append(Tab).Append(Clean(c.Url)).Append(Tab).Append(Clean(c.Poster)).Append(Tab)
              .Append(Clean(c.CanvasUri)).Append(Tab).Append(Clean(c.Artist)).Append(Tab).Append(Clean(c.ArtistUri)).Append(Tab)
              .Append(Clean(c.UploadedBy)).Append(Tab).Append(Clean(c.Etag)).Append(Tab)
              .Append((int)c.Kind).Append(Tab).Append(c.PosterW).Append(Tab).Append(c.PosterH).Append(Tab)
              .Append(c.FetchedAt).Append(Tab).Append(c.CacheTtl).Append(Tab).Append(c.OfflineTtl).Append('\n');

            static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }

        static (string Uri, CanvasText Text)? Parse(string line)
        {
            var f = line.Split(Tab);
            if (f.Length != 14 || f[0].Length == 0 || f[1].Length == 0) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!int.TryParse(f[8], inv, out int kind) || !int.TryParse(f[9], inv, out int w) || !int.TryParse(f[10], inv, out int h)
                || !long.TryParse(f[11], inv, out long at) || !int.TryParse(f[12], inv, out int ttl) || !int.TryParse(f[13], inv, out int offline))
                return null;
            return (f[0], new CanvasText(f[1], f[2], f[3], f[4], f[5], f[6], f[7], (CanvasKind)kind, w, h, at, ttl, offline));
        }
    }
}
