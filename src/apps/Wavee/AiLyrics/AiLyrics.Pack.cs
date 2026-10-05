// ── AiLyrics/AiLyrics.Pack.cs ────────────────────────────────────────────────────────────────────────────────────────
// PackManifest, PackFile, SetupError, SetupException, DownloadProgress, Pack
//
// Role: HOST (runs on a background task; reports progress through a callback, never touches UI state itself)
//
// The one-time download (plan §5). Everything is pinned by the embedded manifest (AiLyrics.PackManifest.cs): sizes and
// SHA-256 of the two runtime wheels (PyPI) and every model file (models.cproducts.dev, 64 MiB parts).
//
//   resumable   a file downloads into .partial\<sha256>.part; a pause, a crash or a dropped connection resumes it with
//               HTTP Range (the hash of the bytes already on disk is recomputed first)
//   verified    each 64 MiB part's SHA-256 is checked as its byte range completes (a bad part is cut off again, so a
//               retry resumes from the last good one); the whole file's SHA-256 must match before it moves into place
//   stall-proof a read that brings no byte for ReadIdle fails as a network error (a half-open connection), and resumes
//   atomic      installed.json records what is verified; a file is "installed" only when listed there with its hash
//   roomy       free space for the download and the compiled NPU caches is checked before the first byte

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;

namespace Wavee;

public static partial class AiLyrics
{
    public sealed class SetupException(SetupError kind, string detail) : Exception(detail)
    {
        public SetupError Kind { get; } = kind;
    }

    public enum DownloadPhase { Checking, Downloading, Verifying, Installing }

    /// <summary>Overall progress across every file being fetched. <see cref="Label"/> names the part in plain words.</summary>
    public readonly record struct DownloadProgress(DownloadPhase Phase, long Done, long Total, double BytesPerSecond, string Label);

    public sealed record PackPart(string Path, long Bytes, string Sha256);

    /// <summary>One downloadable unit: a model file (one or more parts on the model host) or a runtime wheel (one URL,
    /// unpacked after verification).</summary>
    public sealed record PackFile(string Name, long Bytes, string Sha256, IReadOnlyList<PackPart> Parts, string? Url = null,
        IReadOnlyList<string>? Extract = null, string? ExtractDir = null, string Label = "")
    {
        public bool IsWheel => Url is not null;
    }

    public sealed class PackManifest
    {
        public int Version { get; init; }
        public string Base { get; init; } = "";
        public IReadOnlyList<PackFile> Runtime { get; init; } = [];
        public IReadOnlyList<PackFile> Common { get; init; } = [];
        public IReadOnlyDictionary<string, IReadOnlyList<PackFile>> Languages { get; init; } = new Dictionary<string, IReadOnlyList<PackFile>>();

        static PackManifest? s_embedded;
        public static PackManifest Embedded => s_embedded ??= Parse(PackManifestJson);

        public static PackManifest Parse(string json)
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            static IReadOnlyList<PackFile> Files(JsonElement arr)
            {
                var list = new List<PackFile>();
                foreach (var f in arr.EnumerateArray())
                {
                    var parts = new List<PackPart>();
                    foreach (var p in f.GetProperty("parts").EnumerateArray())
                        parts.Add(new PackPart(p.GetProperty("path").GetString()!, p.GetProperty("bytes").GetInt64(), p.GetProperty("sha256").GetString()!));
                    list.Add(new PackFile(f.GetProperty("name").GetString()!, f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!, parts));
                }
                return list;
            }
            var runtime = new List<PackFile>();
            foreach (var w in r.GetProperty("runtime").EnumerateArray())
            {
                List<string>? extract = null;
                if (w.TryGetProperty("extract", out var ex)) { extract = []; foreach (var e in ex.EnumerateArray()) extract.Add(e.GetString()!); }
                string? dir = w.TryGetProperty("extractDir", out var ed) ? ed.GetString() : null;
                runtime.Add(new PackFile(w.GetProperty("id").GetString()!, w.GetProperty("bytes").GetInt64(), w.GetProperty("sha256").GetString()!,
                    [], w.GetProperty("url").GetString(), extract, dir, w.GetProperty("label").GetString() ?? ""));
            }
            var langs = new Dictionary<string, IReadOnlyList<PackFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in r.GetProperty("languages").EnumerateObject()) langs[l.Name] = Files(l.Value.GetProperty("files"));
            return new PackManifest
            {
                Version = r.GetProperty("version").GetInt32(), Base = r.GetProperty("base").GetString()!,
                Runtime = runtime, Common = Files(r.GetProperty("common")), Languages = langs,
            };
        }

        /// <summary>What a set of languages needs, runtime first.</summary>
        public IReadOnlyList<PackFile> FilesFor(IEnumerable<string> languages)
        {
            var all = new List<PackFile>(Runtime);
            all.AddRange(Common);
            foreach (string l in languages) if (Languages.TryGetValue(l, out var fs)) all.AddRange(fs);
            return all;
        }
    }

    /// <summary>The per-part SHA-256 check of one model file: its bytes are fed in file order, and each manifest part is
    /// verified the moment its byte range completes. Bytes past the last part are not checked here (the whole-file hash
    /// covers them).</summary>
    public sealed class PartHashes : IDisposable
    {
        readonly IReadOnlyList<PackPart> _parts;
        readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int _part;
        long _pos, _partStart, _partEnd;

        public PartHashes(IReadOnlyList<PackPart> parts)
        {
            _parts = parts;
            _partEnd = parts.Count > 0 ? parts[0].Bytes : long.MaxValue;
        }

        /// <summary>The parts verified so far.</summary>
        public int Verified => _part;

        /// <summary>Feeds the next bytes of the file. Returns -1 while every completed part matched, otherwise the file
        /// offset where the first mismatching part starts (where the file must be cut back to); feed nothing after
        /// that.</summary>
        public long Append(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0 && _part < _parts.Count)
            {
                int take = (int)Math.Min(data.Length, _partEnd - _pos);
                _hash.AppendData(data[..take]);
                _pos += take;
                data = data[take..];
                if (_pos < _partEnd) continue;
                string sha = Convert.ToHexStringLower(_hash.GetHashAndReset());
                if (!string.Equals(sha, _parts[_part].Sha256, StringComparison.OrdinalIgnoreCase)) return _partStart;
                _part++;
                _partStart = _pos;
                _partEnd = _part < _parts.Count ? _pos + _parts[_part].Bytes : long.MaxValue;
            }
            return -1;
        }

        public void Dispose() => _hash.Dispose();
    }

    public static class Pack
    {
        const int Buffer = 1 << 20;

        /// <summary>A read that brings no byte for this long fails as <see cref="SetupError.Network"/>: a half-open
        /// connection otherwise stalls the download forever. Retry resumes from the bytes on disk.</summary>
        public static readonly TimeSpan ReadIdle = TimeSpan.FromSeconds(60);
        static HttpClient? s_http;

        static HttpClient Http
        {
            get
            {
                if (Volatile.Read(ref s_http) is { } live) return live;
                var c = new HttpClient(Wire.Handler("ai-lyrics", new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(15),
                    AutomaticDecompression = DecompressionMethods.None,
                })) { Timeout = Timeout.InfiniteTimeSpan };                 // a 64 MiB part on a slow link takes minutes
                c.DefaultRequestHeaders.UserAgent.TryParseAdd("Wavee");
                return Interlocked.CompareExchange(ref s_http, c, null) ?? c;
            }
        }

        static string InstalledPath => Path.Combine(Root, "installed.json");

        /// <summary>file name (or runtime id) -> SHA-256 of what is verified and in place.</summary>
        public static Dictionary<string, string> Installed()
        {
            try
            {
                if (!File.Exists(InstalledPath)) return new(StringComparer.OrdinalIgnoreCase);
                using var d = JsonDocument.Parse(File.ReadAllText(InstalledPath));
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in d.RootElement.EnumerateObject()) map[p.Name] = p.Value.GetString() ?? "";
                return map;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(StringComparer.OrdinalIgnoreCase); }
        }

        static void SaveInstalled(Dictionary<string, string> map)
        {
            Directory.CreateDirectory(Root);
            string tmp = InstalledPath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (k, v) in map) w.WriteString(k, v);
                w.WriteEndObject();
            }
            File.Move(tmp, InstalledPath, overwrite: true);
        }

        /// <summary>Files of <paramref name="needed"/> that are not verified and in place.</summary>
        public static List<PackFile> Missing(IReadOnlyList<PackFile> needed)
        {
            var installed = Installed();
            var missing = new List<PackFile>();
            foreach (var f in needed)
            {
                bool ok = installed.TryGetValue(f.Name, out var sha) && sha == f.Sha256;
                if (ok && !f.IsWheel) ok = File.Exists(Path.Combine(ModelsDir, f.Name)) && new FileInfo(Path.Combine(ModelsDir, f.Name)).Length == f.Bytes;
                if (ok && f.IsWheel) ok = File.Exists(Path.Combine(RuntimeDir, "onnxruntime.dll")) || f.Name != "onnxruntime";
                if (!ok) missing.Add(f);
            }
            return missing;
        }

        /// <summary>Bytes on disk once installed: the files plus the compiled NPU caches (about the size of each model).</summary>
        public static long DiskNeed(IReadOnlyList<PackFile> files)
        {
            long n = 0;
            foreach (var f in files) n += f.IsWheel ? f.Bytes * 3 : f.Bytes * 2;    // wheels unpack ~2.5x; models + their cache
            return n;
        }

        public static long DownloadBytes(IReadOnlyList<PackFile> files) { long n = 0; foreach (var f in files) n += f.Bytes; return n; }

        /// <summary>Downloads, verifies and installs <paramref name="files"/>. Throws <see cref="SetupException"/>.
        /// <paramref name="alreadyDone"/> is the bytes of the set that an earlier run already installed, so a resumed
        /// download continues the bar from where it was instead of starting again from zero.</summary>
        public static async Task InstallAsync(IReadOnlyList<PackFile> files, string modelBase, Action<DownloadProgress> progress, CancellationToken ct,
            long alreadyDone = 0)
        {
            if (files.Count == 0) return;
            // the user downloads again: a removal deferred by a loaded runtime DLL no longer applies
            try { File.Delete(Path.Combine(Root, ".remove")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            progress(new DownloadProgress(DownloadPhase.Checking, alreadyDone, alreadyDone + DownloadBytes(files), 0, ""));
            if (!NetworkInterface.GetIsNetworkAvailable()) throw new SetupException(SetupError.Offline, "no network");
            Directory.CreateDirectory(PartialDir); Directory.CreateDirectory(ModelsDir); Directory.CreateDirectory(RuntimeDir);
            long need = DiskNeed(files);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Root))!);
            if (drive.AvailableFreeSpace < need + need / 10)
                throw new SetupException(SetupError.DiskFull, Rules.FormatBytes(need + need / 10) + "|" + drive.Name);

            long total = alreadyDone + DownloadBytes(files), doneBefore = alreadyDone;
            var meter = new Rules.SpeedMeter();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                string label = f.IsWheel ? f.Label : f.Name;
                await FetchAsync(f, modelBase, (fileDone) =>
                {
                    long done = doneBefore + fileDone;
                    if (meter.Sample(done, Environment.TickCount64))
                        progress(new DownloadProgress(DownloadPhase.Downloading, done, total, meter.BytesPerSecond, label));
                }, ct).ConfigureAwait(false);
                progress(new DownloadProgress(DownloadPhase.Installing, doneBefore + f.Bytes, total, meter.BytesPerSecond, label));
                InstallOne(f);
                var installed = Installed();
                installed[f.Name] = f.Sha256;
                SaveInstalled(installed);
                doneBefore += f.Bytes;
            }
        }

        static async Task FetchAsync(PackFile f, string modelBase, Action<long> onBytes, CancellationToken ct)
        {
            string partial = Path.Combine(PartialDir, f.Sha256 + ".part");
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (have > f.Bytes) { File.Delete(partial); have = 0; }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var parts = f.IsWheel ? null : new PartHashes(f.Parts);
            var buf = new byte[Buffer];
            long cut = -1;                                                    // where a bad part starts: cut back to it
            if (have > 0)
            {
                // the bytes already on disk are re-hashed, and the parts they complete re-verified
                using var existing = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer);
                int n;
                while ((n = await existing.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buf, 0, n);
                    if (parts is not null && (cut = parts.Append(buf.AsSpan(0, n))) >= 0) break;
                }
            }
            if (cut >= 0) CutBack(partial, cut, f);
            onBytes(have);

            var segments = f.IsWheel
                ? new List<(string Url, long Start, long Bytes)> { (f.Url!, 0, f.Bytes) }
                : SegmentsOf(f, modelBase);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await using (var fs = new FileStream(partial, FileMode.Append, FileAccess.Write, FileShare.Read, Buffer, useAsync: true))
            {
                foreach (var (url, start, bytes) in segments)
                {
                    long end = start + bytes;
                    if (have >= end) continue;
                    long skip = have - start;
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    if (skip > 0) req.Headers.Range = new RangeHeaderValue(skip, null);
                    HttpResponseMessage resp;
                    idle.CancelAfter(ReadIdle);
                    try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false); }
                    catch (HttpRequestException ex) { throw new SetupException(SetupError.Network, ex.Message); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Stalled(url); }
                    using (resp)
                    {
                        if (resp.StatusCode == HttpStatusCode.NotFound) throw new SetupException(SetupError.NotFound, url);
                        if (!resp.IsSuccessStatusCode) throw new SetupException(SetupError.Network, "HTTP " + (int)resp.StatusCode + " " + url);
                        long toDrop = skip > 0 && resp.StatusCode != HttpStatusCode.PartialContent ? skip : 0;   // the server ignored the range
                        try
                        {
                            await using var body = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                            while (true)
                            {
                                idle.CancelAfter(ReadIdle);                         // re-armed per read: an idle deadline, not a total one
                                int n = await body.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                                if (n <= 0) break;
                                int off = 0;
                                if (toDrop > 0) { int d = (int)Math.Min(toDrop, n); toDrop -= d; off = d; if (off == n) continue; }
                                int len = (int)Math.Min(n - off, end - have);
                                if (len <= 0) break;
                                await fs.WriteAsync(buf.AsMemory(off, len), ct).ConfigureAwait(false);
                                hash.AppendData(buf, off, len);
                                have += len;
                                onBytes(have);
                                if (parts is not null && (cut = parts.Append(buf.AsSpan(off, len))) >= 0) break;
                            }
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Stalled(url); }
                        catch (IOException ex) when (!ct.IsCancellationRequested) { throw new SetupException(SetupError.Network, ex.Message); }
                        catch (HttpRequestException ex) { throw new SetupException(SetupError.Network, ex.Message); }
                    }
                    if (cut >= 0) break;
                    if (have < end) throw new SetupException(SetupError.Network, "the download ended early: " + url);
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            if (cut >= 0) CutBack(partial, cut, f);
            string sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new SetupException(SetupError.HashMismatch, f.Name);
            }
        }

        static SetupException Stalled(string url)
            => new(SetupError.Network, "no data for " + (int)ReadIdle.TotalSeconds + " s: " + url);

        /// <summary>A part failed its hash: drop it and everything after it, so a retry downloads it again and keeps
        /// every good part before it.</summary>
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        static void CutBack(string partial, long at, PackFile f)
        {
            using (var t = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None)) t.SetLength(at);
            throw new SetupException(SetupError.HashMismatch, f.Name + " @" + at);
        }

        static List<(string, long, long)> SegmentsOf(PackFile f, string modelBase)
        {
            var list = new List<(string, long, long)>();
            long start = 0;
            foreach (var p in f.Parts) { list.Add((modelBase + p.Path, start, p.Bytes)); start += p.Bytes; }
            return list;
        }

        static void InstallOne(PackFile f)
        {
            string partial = Path.Combine(PartialDir, f.Sha256 + ".part");
            if (!f.IsWheel)
            {
                string dst = Path.Combine(ModelsDir, f.Name);
                LoadedModels.DeleteCompiled(dst);                           // a new file needs a new NPU compile
                File.Move(partial, dst, overwrite: true);
                return;
            }
            using (var zip = System.IO.Compression.ZipFile.OpenRead(partial))
            {
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue;
                    bool take = f.Extract is not null ? f.Extract.Contains(e.FullName)
                        : f.ExtractDir is not null && e.FullName.StartsWith(f.ExtractDir, StringComparison.Ordinal)
                          && !e.FullName.EndsWith(".py", StringComparison.Ordinal) && !e.FullName.Contains("__pycache__", StringComparison.Ordinal);
                    if (!take) continue;
                    string dst = Path.Combine(RuntimeDir, Path.GetFileName(e.FullName));
                    string tmp = dst + ".tmp";
                    e.ExtractToFile(tmp, overwrite: true);
                    try { File.Move(tmp, dst, overwrite: true); }
                    catch (IOException) when (SameContent(tmp, dst))
                    {
                        // a download again after a deferred remove: this runtime DLL is still loaded in this process,
                        // and it is byte-for-byte the file just unpacked, so the loaded copy stays
                        File.Delete(tmp);
                    }
                }
            }
            File.Delete(partial);
        }

        /// <summary>Same length and SHA-256. A file this process has loaded (a runtime DLL) is still readable; anything
        /// that cannot be read counts as different.</summary>
        public static bool SameContent(string a, string b)
        {
            try
            {
                if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length) return false;
                return HashOf(a).AsSpan().SequenceEqual(HashOf(b));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

            static byte[] HashOf(string path)
            {
                using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Buffer);
                return SHA256.HashData(s);
            }
        }

        /// <summary>Deletes one language's model files, their compiled NPU caches and their installed.json entries. Runs
        /// on the worker once that language's sessions are unloaded. A file that cannot be deleted is logged and left;
        /// it is no longer listed as installed, so a later download replaces it.</summary>
        public static void RemoveLanguage(string language)
        {
            if (!PackManifest.Embedded.Languages.TryGetValue(language, out var files)) return;
            var installed = Installed();
            foreach (var f in files)
            {
                string path = Path.Combine(ModelsDir, f.Name);
                try
                {
                    if (Directory.Exists(ModelsDir)) LoadedModels.DeleteCompiled(path);
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", $"ai.remove.language file={f.Name} failed", ex); }
                installed.Remove(f.Name);
            }
            try { SaveInstalled(installed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "ai.remove.language installed.json not saved", ex); }
            Log.Info("ai-lyrics", $"ai.remove.language language={language} files={files.Count}");
        }

        /// <summary>Deletes every downloaded file and result. A runtime DLL already loaded in this process cannot be
        /// deleted: the folder is then marked and cleared on the next start (<see cref="FinishPendingRemoval"/>).</summary>
        public static bool Remove()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(InstalledPath); } catch (IOException) { }
                try { Directory.CreateDirectory(Root); File.WriteAllText(Path.Combine(Root, ".remove"), "1"); } catch (IOException) { }
                return false;
            }
        }

        /// <summary>Completes a removal that a loaded runtime blocked last session. Call before anything loads it.</summary>
        public static void FinishPendingRemoval()
        {
            try
            {
                if (File.Exists(Path.Combine(Root, ".remove"))) Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ai-lyrics", "pending removal failed", ex); }
        }

        /// <summary>Bytes the downloaded files and caches use now.</summary>
        public static long DiskUsed()
        {
            try
            {
                if (!Directory.Exists(Root)) return 0;
                long n = 0;
                foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)) n += f.Length;
                return n;
            }
            catch (IOException) { return 0; }
        }
    }
}
