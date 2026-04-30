using System.Buffers.Binary;

namespace Wavee.Core.Video;

public sealed record Mp4InitSegmentProtectionData(
    string? DefaultKeyId,
    string? DefaultPlayReadyKeyId,
    int? DefaultPerSampleIvSize,
    bool? IsProtected,
    byte[]? PlayReadyPsshBytes,
    byte[]? PlayReadyProBytes,
    int TencBoxCount,
    int PsshBoxCount)
{
    public bool HasAnySignal =>
        !string.IsNullOrWhiteSpace(DefaultKeyId)
        || PlayReadyPsshBytes is { Length: > 0 }
        || PlayReadyProBytes is { Length: > 0 };
}

public static class Mp4InitSegmentProtectionParser
{
    private static readonly byte[] PlayReadySystemId =
    [
        0x9A, 0x04, 0xF0, 0x79, 0x98, 0x40, 0x42, 0x86,
        0xAB, 0x92, 0xE6, 0x5B, 0xE0, 0x88, 0x5F, 0x95,
    ];

    public static Mp4InitSegmentProtectionData Parse(ReadOnlySpan<byte> initSegment)
    {
        string? defaultKeyId = null;
        string? defaultPlayReadyKeyId = null;
        int? defaultPerSampleIvSize = null;
        bool? isProtected = null;
        byte[]? playReadyPssh = null;
        byte[]? playReadyPro = null;
        var tencBoxCount = 0;
        var psshBoxCount = 0;

        for (var typeOffset = 4; typeOffset <= initSegment.Length - 4; typeOffset++)
        {
            if (IsType(initSegment, typeOffset, 't', 'e', 'n', 'c')
                && TryReadBoxBounds(initSegment, typeOffset - 4, out var payloadStart, out var boxEnd))
            {
                if (TryParseTenc(initSegment, payloadStart, boxEnd, out var tenc))
                {
                    tencBoxCount++;
                    defaultKeyId ??= FormatCencKeyId(tenc.DefaultKid);
                    defaultPlayReadyKeyId ??= FormatPlayReadyKeyId(tenc.DefaultKid);
                    defaultPerSampleIvSize ??= tenc.DefaultPerSampleIvSize;
                    isProtected ??= tenc.IsProtected;
                }
            }
            else if (IsType(initSegment, typeOffset, 'p', 's', 's', 'h')
                     && TryReadBoxBounds(initSegment, typeOffset - 4, out payloadStart, out boxEnd))
            {
                psshBoxCount++;
                if (IsPlayReadyPssh(initSegment, payloadStart, boxEnd))
                {
                    var psshLength = boxEnd - (typeOffset - 4);
                    playReadyPssh = initSegment.Slice(typeOffset - 4, psshLength).ToArray();
                    playReadyPro = ExtractProFromPssh(playReadyPssh);
                }
            }
        }

        return new Mp4InitSegmentProtectionData(
            defaultKeyId,
            defaultPlayReadyKeyId,
            defaultPerSampleIvSize,
            isProtected,
            playReadyPssh,
            playReadyPro,
            tencBoxCount,
            psshBoxCount);
    }

    private static bool TryReadBoxBounds(
        ReadOnlySpan<byte> data,
        int boxStart,
        out int payloadStart,
        out int boxEnd)
    {
        payloadStart = 0;
        boxEnd = 0;

        if (boxStart < 0 || boxStart + 8 > data.Length)
            return false;

        var size32 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(boxStart, 4));
        ulong boxSize = size32;
        var headerSize = 8;

        if (size32 == 1)
        {
            if (boxStart + 16 > data.Length)
                return false;

            boxSize = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(boxStart + 8, 8));
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            boxSize = (ulong)(data.Length - boxStart);
        }

        if (boxSize < (ulong)headerSize || boxSize > int.MaxValue)
            return false;

        var localBoxEnd = boxStart + (int)boxSize;
        if (localBoxEnd > data.Length)
            return false;

        payloadStart = boxStart + headerSize;
        boxEnd = localBoxEnd;
        return true;
    }

    private static bool TryParseTenc(
        ReadOnlySpan<byte> data,
        int payloadStart,
        int boxEnd,
        out TencBox tenc)
    {
        tenc = default;
        if (payloadStart + 4 > boxEnd)
            return false;

        var version = data[payloadStart];
        var fullBoxPayload = payloadStart + 4;

        Span<int> skips = stackalloc int[3];
        var skipCount = 0;
        if (version == 1)
        {
            skips[skipCount++] = 2;
            skips[skipCount++] = 1;
        }
        else
        {
            skips[skipCount++] = 1;
            skips[skipCount++] = 2;
            skips[skipCount++] = 0;
        }

        for (var i = 0; i < skipCount; i++)
        {
            var pos = fullBoxPayload + skips[i];
            if (pos + 18 > boxEnd)
                continue;

            var isProtected = data[pos];
            var ivSize = data[pos + 1];
            if (isProtected > 1 || (ivSize != 0 && ivSize != 8 && ivSize != 16))
                continue;

            var kid = data.Slice(pos + 2, 16).ToArray();
            tenc = new TencBox(isProtected != 0, ivSize, kid);
            return true;
        }

        return false;
    }

    private static bool IsPlayReadyPssh(ReadOnlySpan<byte> data, int payloadStart, int boxEnd)
    {
        if (payloadStart + 24 > boxEnd)
            return false;

        return data.Slice(payloadStart + 4, 16).SequenceEqual(PlayReadySystemId);
    }

    private static byte[]? ExtractProFromPssh(byte[] pssh)
    {
        if (pssh.Length < 32)
            return null;

        try
        {
            if (!IsType(pssh, 4, 'p', 's', 's', 'h'))
                return null;

            var version = pssh[8];
            var offset = 28;
            if (version > 0)
            {
                if (pssh.Length < offset + 4)
                    return null;

                var kidCount = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
                offset += 4;
                if (kidCount < 0 || pssh.Length < offset + kidCount * 16 + 4)
                    return null;

                offset += kidCount * 16;
            }

            var proLength = BinaryPrimitives.ReadInt32BigEndian(pssh.AsSpan(offset, 4));
            offset += 4;
            if (proLength <= 0 || offset + proLength > pssh.Length)
                return null;

            var pro = new byte[proLength];
            pssh.AsSpan(offset, proLength).CopyTo(pro);
            return LooksLikePlayReadyObject(pro) ? pro : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikePlayReadyObject(byte[] pro)
    {
        if (pro.Length < 6)
            return false;

        var objectLength = BinaryPrimitives.ReadInt32LittleEndian(pro.AsSpan(0, 4));
        return objectLength == pro.Length;
    }

    private static string? FormatCencKeyId(byte[] kid)
    {
        if (kid.Length != 16)
            return null;

        return string.Create(36, kid, static (span, bytes) =>
        {
            const string hex = "0123456789abcdef";
            var output = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i is 4 or 6 or 8 or 10)
                    span[output++] = '-';

                span[output++] = hex[bytes[i] >> 4];
                span[output++] = hex[bytes[i] & 0x0F];
            }
        });
    }

    private static string? FormatPlayReadyKeyId(byte[] cencKid)
    {
        if (cencKid.Length != 16)
            return null;

        var playReadyKid = new byte[16];
        playReadyKid[0] = cencKid[3];
        playReadyKid[1] = cencKid[2];
        playReadyKid[2] = cencKid[1];
        playReadyKid[3] = cencKid[0];
        playReadyKid[4] = cencKid[5];
        playReadyKid[5] = cencKid[4];
        playReadyKid[6] = cencKid[7];
        playReadyKid[7] = cencKid[6];
        cencKid.AsSpan(8, 8).CopyTo(playReadyKid.AsSpan(8));
        return Convert.ToBase64String(playReadyKid);
    }

    private static bool IsType(ReadOnlySpan<byte> data, int offset, char a, char b, char c, char d)
        => offset >= 0
           && offset + 4 <= data.Length
           && data[offset] == (byte)a
           && data[offset + 1] == (byte)b
           && data[offset + 2] == (byte)c
           && data[offset + 3] == (byte)d;

    private readonly record struct TencBox(
        bool IsProtected,
        int DefaultPerSampleIvSize,
        byte[] DefaultKid);
}
