using System.Text;

namespace Wavee.Backend.Audio;

/// <summary>Strips SHOUTcast/Icecast interleaved metadata out of a live body.
///
/// <para>The wire shape: after every <c>icy-metaint</c> AUDIO bytes the server injects one length byte
/// (<c>len × 16</c> bytes of metadata follow; <c>0</c> = "nothing changed"), then the audio resumes. A metadata block is
/// a <c>key='value';</c> list of which only <c>StreamTitle</c> matters. The demuxer is a byte state machine precisely
/// because the socket hands us ARBITRARY chunk boundaries — a block, its length byte, and even the audio run before it
/// routinely straddle three reads.</para>
///
/// <para>A <c>metaInt</c> of 0 (the server ignored <c>Icy-MetaData: 1</c>) makes this a pass-through.</para></summary>
internal sealed class IcyDemuxer
{
    // The largest a block can be: a single length byte of 255 × 16.
    const int MaxMetaBytes = 255 * 16;

    readonly int _metaInt;
    readonly byte[] _meta = new byte[MaxMetaBytes];

    int _untilMeta;         // audio bytes still owed before the next length byte
    bool _needLengthByte;   // the next byte is the block-length byte
    int _metaRemaining;     // metadata bytes still owed for the current block
    int _metaFilled;

    public IcyDemuxer(int metaInt)
    {
        _metaInt = Math.Max(0, metaInt);
        _untilMeta = _metaInt;
    }

    /// <summary>Fires only when the parsed title actually CHANGES (servers re-send the same block every interval).</summary>
    public event Action<string>? StreamTitleChanged;

    /// <summary>The last parsed <c>StreamTitle</c>, or null before the first block.</summary>
    public string? CurrentTitle { get; private set; }

    /// <summary>Metadata blocks seen (including empty ones) — diagnostics.</summary>
    public long BlocksSeen { get; private set; }

    /// <summary>Route one producer chunk: audio to <paramref name="audio"/>, metadata to the title parser.</summary>
    public void Push(ReadOnlySpan<byte> data, IByteSink audio)
    {
        if (_metaInt <= 0)
        {
            audio.Write(data);
            return;
        }

        while (!data.IsEmpty)
        {
            if (_metaRemaining > 0)
            {
                int n = Math.Min(_metaRemaining, data.Length);
                data[..n].CopyTo(_meta.AsSpan(_metaFilled));
                _metaFilled += n;
                _metaRemaining -= n;
                data = data[n..];
                if (_metaRemaining == 0)
                {
                    ParseBlock(_meta.AsSpan(0, _metaFilled));
                    _metaFilled = 0;
                    _untilMeta = _metaInt;
                }
                continue;
            }

            if (_needLengthByte)
            {
                int len = data[0] * 16;
                data = data[1..];
                _needLengthByte = false;
                BlocksSeen++;
                if (len == 0) _untilMeta = _metaInt;   // "unchanged" — straight back to audio
                else { _metaRemaining = len; _metaFilled = 0; }
                continue;
            }

            int take = Math.Min(_untilMeta, data.Length);
            if (take > 0)
            {
                audio.Write(data[..take]);
                data = data[take..];
                _untilMeta -= take;
            }
            if (_untilMeta == 0) _needLengthByte = true;
        }
    }

    void ParseBlock(ReadOnlySpan<byte> block)
    {
        var text = Decode(block);
        var title = IcyMetadata.ExtractStreamTitle(text);
        if (title is null) return;
        title = title.Trim();
        if (title.Length == 0 || string.Equals(title, CurrentTitle, StringComparison.Ordinal)) return;
        CurrentTitle = title;
        StreamTitleChanged?.Invoke(title);
    }

    /// <summary>Strict UTF-8 first (the modern convention), Latin-1 as the fallback — a mis-decoded Latin-1 title is
    /// still readable, whereas UTF-8 replacement characters are not.</summary>
    static string Decode(ReadOnlySpan<byte> block)
    {
        // Servers pad the 16-byte-aligned block with NULs; they are not part of the payload.
        int end = block.Length;
        while (end > 0 && block[end - 1] == 0) end--;
        var payload = block[..end];
        if (payload.IsEmpty) return "";
        try { return new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(payload); }
    }
}

/// <summary>The pure ICY metadata text helpers — no I/O, so the host and the LiveConnect relay share one parser.</summary>
public static class IcyMetadata
{
    /// <summary>Pull the <c>StreamTitle</c> value out of a raw metadata block, or null when there is none.
    ///
    /// <para>Terminator detection is deliberately not "the first <c>';</c>": titles legitimately contain apostrophes
    /// (<c>Don't Stop</c>) and blocks legitimately carry more fields (<c>StreamUrl</c>). A candidate <c>';</c> only ends
    /// the value when what follows is the end of the block, padding, or another <c>key='</c> pair.</para></summary>
    public static string? ExtractStreamTitle(string block)
    {
        if (string.IsNullOrEmpty(block)) return null;
        const string key = "StreamTitle='";
        int start = block.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;

        int scan = start;
        while (true)
        {
            int term = block.IndexOf("';", scan, StringComparison.Ordinal);
            if (term < 0)
            {
                // Truncated block: take the rest, shedding a dangling quote/padding.
                return block[start..].TrimEnd('\0').TrimEnd('\'');
            }
            if (IsFieldBoundary(block, term + 2)) return block[start..term];
            scan = term + 1;
        }
    }

    static bool IsFieldBoundary(string block, int index)
    {
        int i = index;
        while (i < block.Length && (block[i] == '\0' || block[i] == ' ')) i++;
        if (i >= block.Length) return true;
        if (!char.IsAsciiLetter(block[i])) return false;
        int j = i;
        while (j < block.Length && (char.IsAsciiLetterOrDigit(block[j]) || block[j] == '_')) j++;
        return j + 1 < block.Length && block[j] == '=' && block[j + 1] == '\'';
    }

    /// <summary>Split the ICY convention <c>Artist - Title</c> on its FIRST <c>" - "</c>. With no separator (or an empty
    /// half) the whole string is the title and the artist is unknown — the relay then falls back to the station name.</summary>
    public static (string Title, string? Artist) SplitStreamTitle(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return ("", null);
        int i = s.IndexOf(" - ", StringComparison.Ordinal);
        if (i <= 0) return (s, null);
        var artist = s[..i].Trim();
        var title = s[(i + 3)..].Trim();
        if (artist.Length == 0 || title.Length == 0) return (s, null);
        return (title, artist);
    }
}
