using System.Security.Cryptography;
using System.Text;

namespace Wavee;

public static partial class Spotify
{
    public static partial class Api
    {
        public static Result TranscriptDocument(string url, string? etag, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return new(400, []);
            bool spotify = uri.Host.EndsWith(".spotify.com", StringComparison.OrdinalIgnoreCase);
            bool cdn = uri.Host.EndsWith(".spotifycdn.com", StringComparison.OrdinalIgnoreCase);
            if (!spotify && !cdn) return new(400, []);
            HeaderSet headers = spotify ? CommonJson | HeaderSet.PathfinderDesktop : HeaderSet.AcceptJson;
            return SendAuthed(Verb.Get, url, headers, RequestKind.Custom, [], "", ct, ifNoneMatch: etag);
        }
    }

    public static partial class Podcasts
    {
        public static string TranscriptRequestUrl(string url, bool readAlong)
        {
            if (!readAlong) return url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
            var builder = new UriBuilder(uri);
            var parameters = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.Split('=')[0] is not ("format" or "maxSentenceLength" or "excludeCC"));
            builder.Query = string.Join("&", parameters.Append("format=json").Append("maxSentenceLength=500").Append("excludeCC=true"));
            return builder.Uri.AbsoluteUri;
        }

        public static Task<Transcript> TranscriptAsync(TranscriptKey key, string url, string account, CancellationToken ct)
        {
            string folder = Path.Combine(Platform.LocalFolder, "cache", "podcast-transcripts");
            string identity = account + "\n" + key.Episode + "\n" + key.Language + "\n" + key.ReadAlong;
            string path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".bin");
            return Api.RunAsync(() =>
            {
                using var accountRequest = Api.ForAccount(account);
                ct.ThrowIfCancellationRequested();
                byte[] cached = []; string? etag = null; long expires = 0; int cachedMaxAge = 0;
                try
                {
                    if (File.Exists(path))
                    {
                        using var reader = new BinaryReader(File.OpenRead(path));
                        if (reader.ReadInt32() == 2)
                        {
                            expires = reader.ReadInt64(); etag = reader.ReadString(); cachedMaxAge = reader.ReadInt32();
                            int length = reader.ReadInt32();
                            if (length is > 0 and <= 8_388_608)
                            {
                                cached = reader.ReadBytes(length);
                                if (cached.Length != length) { cached = []; etag = null; }
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { cached = []; }
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (cached.Length > 0 && expires > now)
                {
                    try
                    {
                        var existing = DecodeTranscript(cached);
                        if (existing.Ok) return existing;
                        cached = []; etag = null;
                    }
                    catch (System.Text.Json.JsonException) { cached = []; etag = null; }
                }
                string target = TranscriptRequestUrl(url, key.ReadAlong);
                var result = Api.TranscriptDocument(target, cached.Length > 0 ? etag : null, ct);
                byte[] bytes = result.NotModified ? cached : result.Body;
                if ((!result.Ok && !result.NotModified) || bytes.Length == 0) return new Transcript(key.Language, [], result.Status);
                Transcript transcript;
                try { transcript = DecodeTranscript(bytes); }
                catch (System.Text.Json.JsonException) { return new Transcript(key.Language, [], 502); }
                if (!transcript.Ok) return transcript;
                try
                {
                    if (result.CacheNoStore)
                    {
                        if (File.Exists(path)) File.Delete(path);
                        return transcript;
                    }
                    int maxAge = result.NotModified && !result.CacheHasMaxAge ? cachedMaxAge : result.CacheMaxAgeSeconds;
                    Directory.CreateDirectory(folder);
                    string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var writer = new BinaryWriter(File.Create(temp)))
                        {
                            writer.Write(2); writer.Write(now + maxAge);
                            writer.Write(result.ETag ?? etag ?? ""); writer.Write(maxAge); writer.Write(bytes.Length); writer.Write(bytes);
                        }
                        File.Move(temp, path, overwrite: true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("podcast", "transcript cache write failed"); }
                return transcript;
            });
        }
    }
}
