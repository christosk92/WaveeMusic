// CencMediaSource.h — the LAST MILE for native PlayReady video: a custom fragmented-MP4 / CENC demuxer + a custom
// IMFMediaSource/IMFMediaStream that emits ENCRYPTED IMFSamples (carrying the CENC per-sample attributes) to the
// media engine's protected pipeline + CDM, exactly like Microsoft's MediaEngineEMEUWPSample `CdmMediaSource`.
//
// Why this exists: the built-in IMFMediaSourceExtension (MSE) rejects protected byte streams
// (MF_E_UNSUPPORTED_BYTESTREAM_TYPE / MF_E_DRM_UNSUPPORTED — see the README/design-doc findings), and a URL/byte-stream
// SetSource hard-wedges the PMP protected pipeline. Microsoft's sample instead demuxes fMP4/CENC IN-APP and hands the
// engine already-encrypted samples with the CENC metadata the CDM needs to decrypt. This is that source.
//
// This header is #included from Helper.cpp (FG_UWP build) AFTER all the shared helpers it leans on are defined:
//   LogLine, HttpGetBytes, CreateAndPrepareCdm, MediaEngineProtectionManager, EmeNeedKeyNotify, MediaEngineNotify,
//   CdmSessionCallbacks, HandleCdmKeyMessage, QueryCdmKeyStatus, WriteCoord, StopRequested, and the g_* CDM globals.
//
// Scope of the demuxer (H.264 video, single track): moov{trak/mdia(mdhd)/minf/stbl/stsd(encv|avc1 → avcC + sinf →
// schm(cenc/cbcs)/schi/tenc)}, pssh (PlayReady init data), and per media segment moof{traf/tfhd/trun/senc}+mdat.

#pragma once

#include <cstdint>
#include <cstring>
#include <vector>
#include <string>
#include <mutex>
#include <functional>
#include <algorithm>
#include <map>        // per-stream ITA cache (a single slot thrashes once there are two streams)
#include <set>        // announced / ended stream ids
#include <iterator>   // make_move_iterator — appending fetched samples without copying them

// ── MF_MT_PROTECTED is not in the 26100 SDK headers; its documented GUID (media type "content is protected"). ──
// {5FA1B54B-B61A-4d76-A99B-8FD7F0EA8F55}
static const GUID FG_MF_MT_PROTECTED = { 0x5FA1B54B, 0xB61A, 0x4d76, { 0xA9, 0x9B, 0x8F, 0xD7, 0xF0, 0xEA, 0x8F, 0x55 } };

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Big-endian box reader helpers.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
namespace cenc {

static inline uint32_t rd32(const uint8_t* p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3]; }
static inline uint16_t rd16(const uint8_t* p) { return (uint16_t)(((uint16_t)p[0] << 8) | p[1]); }
static inline uint64_t rd64(const uint8_t* p) { return ((uint64_t)rd32(p) << 32) | rd32(p + 4); }
static inline uint32_t fourcc(const char* s) { return ((uint32_t)(uint8_t)s[0] << 24) | ((uint32_t)(uint8_t)s[1] << 16) | ((uint32_t)(uint8_t)s[2] << 8) | (uint8_t)s[3]; }

struct Box { uint32_t type; const uint8_t* payload; size_t payloadLen; const uint8_t* boxStart; size_t boxLen; };

// Iterate the top-level boxes within [data,data+len). Full-box version/flags are NOT stripped (payload starts right
// after the 8-byte (or 16-byte for 64-bit size) header); callers strip version/flags themselves where needed.
static void ForEachBox(const uint8_t* data, size_t len, const std::function<void(const Box&)>& fn)
{
    size_t off = 0;
    while (off + 8 <= len)
    {
        uint64_t size = rd32(data + off);
        uint32_t type = rd32(data + off + 4);
        size_t hdr = 8;
        if (size == 1) { if (off + 16 > len) break; size = rd64(data + off + 8); hdr = 16; }
        else if (size == 0) { size = len - off; }
        if (size < hdr || off + size > len) break;
        Box b{ type, data + off + hdr, (size_t)(size - hdr), data + off, (size_t)size };
        fn(b);
        off += (size_t)size;
    }
}

// Find the first child box of a given type inside a parent payload; returns false if absent.
static bool FindBox(const uint8_t* data, size_t len, uint32_t type, Box& out)
{
    bool found = false; Box hit{};
    ForEachBox(data, len, [&](const Box& b) { if (!found && b.type == type) { found = true; hit = b; } });
    if (found) out = hit;
    return found;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Parsed init-segment info.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// Which kind of track an init segment described. Spotify addresses every representation as its own single-track file
// (video profile N, audio profile M), so one InitInfo == one track and there is no multi-track-per-file case to handle.
enum class TrackKind { Video, Audio };

struct InitInfo
{
    TrackKind kind = TrackKind::Video;
    uint32_t codec4cc = 0;             // original sample format (e.g. 'avc1'/'avc3'/'mp4a') from frma, else stsd entry type
    std::vector<uint8_t> avcC;         // raw AVCDecoderConfigurationRecord (from the 'avcC' box)
    std::vector<uint8_t> spspps;       // SPS+PPS as Annex-B (for MF_MT_MPEG_SEQUENCE_HEADER)
    uint32_t width = 0, height = 0;
    // ── audio (mp4a/enca) ───────────────────────────────────────────────────────────────────────────────────────────
    uint32_t channels = 0;             // AudioSampleEntry channelcount
    uint32_t sampleRate = 0;           // AudioSampleEntry samplerate (integer part of the 16.16 fixed-point field)
    uint32_t bitsPerSample = 16;       // AudioSampleEntry samplesize
    uint32_t avgBitrate = 0;           // esds DecoderConfigDescriptor avgBitrate (bits/s), 0 when absent
    std::vector<uint8_t> asc;          // AudioSpecificConfig (esds → DecoderSpecificInfo) — AAC's real configuration
    uint64_t timescale = 90000;        // media timescale (mdhd)
    int scheme = 0;                    // 0 = cenc (AES-CTR), 1 = cbcs (AES-CBC pattern)
    bool encrypted = false;
    uint8_t kid[16] = {};              // tenc default_KID
    uint8_t perSampleIvSize = 8;       // tenc default_Per_Sample_IV_Size (0 => constant IV)
    uint8_t cryptByteBlock = 0, skipByteBlock = 0; // cbcs pattern
    std::vector<uint8_t> constIv;      // tenc default constant IV (when perSampleIvSize == 0)
    std::vector<uint8_t> pssh;         // concatenated pssh box(es) — the "cenc" init data for GenerateRequest
    uint8_t nalLenSize = 4;            // AVC NAL length prefix size (from avcC)
};

// Build SPS/PPS Annex-B from an AVCDecoderConfigurationRecord.
static void ExtractSpsPps(const std::vector<uint8_t>& avcC, InitInfo& info)
{
    if (avcC.size() < 7) return;
    info.nalLenSize = (uint8_t)((avcC[4] & 0x03) + 1);
    size_t p = 5;
    auto emit = [&](const uint8_t* nal, size_t n) {
        static const uint8_t sc[4] = { 0, 0, 0, 1 };
        info.spspps.insert(info.spspps.end(), sc, sc + 4);
        info.spspps.insert(info.spspps.end(), nal, nal + n);
    };
    if (p >= avcC.size()) return;
    int numSps = avcC[p++] & 0x1F;
    for (int i = 0; i < numSps && p + 2 <= avcC.size(); i++)
    {
        uint16_t n = rd16(&avcC[p]); p += 2;
        if (p + n > avcC.size()) return;
        emit(&avcC[p], n); p += n;
    }
    if (p >= avcC.size()) return;
    int numPps = avcC[p++];
    for (int i = 0; i < numPps && p + 2 <= avcC.size(); i++)
    {
        uint16_t n = rd16(&avcC[p]); p += 2;
        if (p + n > avcC.size()) return;
        emit(&avcC[p], n); p += n;
    }
}

// Parse tenc (Track Encryption box) — the CENC defaults.
static void ParseTenc(const Box& tenc, InitInfo& info)
{
    const uint8_t* p = tenc.payload; size_t n = tenc.payloadLen;
    if (n < 8) return;
    uint8_t version = p[0];
    // p[1..3] flags. p[4] reserved. p[5]: (v0) reserved | (v>0) crypt<<4|skip. p[6] default_isProtected. p[7] iv size.
    if (version > 0) { info.cryptByteBlock = p[5] >> 4; info.skipByteBlock = p[5] & 0x0F; }
    info.encrypted = p[6] != 0;
    info.perSampleIvSize = p[7];
    if (n >= 8 + 16) memcpy(info.kid, p + 8, 16);
    size_t off = 8 + 16;
    if (info.perSampleIvSize == 0 && off < n)
    {
        uint8_t civLen = p[off++];
        if (off + civLen <= n) info.constIv.assign(p + off, p + off + civLen);
    }
}

// Parse a VisualSampleEntry (encv/avc1/avc3): width/height + walk child boxes for avcC + sinf.
static void ParseVisualSampleEntry(const Box& entry, InitInfo& info)
{
    const uint8_t* p = entry.payload; size_t n = entry.payloadLen;
    if (n < 78) return;
    info.width = rd16(p + 24);
    info.height = rd16(p + 26);
    // Child boxes begin at offset 78 of the VisualSampleEntry payload.
    const uint8_t* kids = p + 78; size_t klen = n - 78;
    ForEachBox(kids, klen, [&](const Box& b) {
        if (b.type == fourcc("avcC")) { info.avcC.assign(b.payload, b.payload + b.payloadLen); }
        else if (b.type == fourcc("sinf"))
        {
            Box frma, schm, schi, tenc;
            if (FindBox(b.payload, b.payloadLen, fourcc("frma"), frma) && frma.payloadLen >= 4)
                info.codec4cc = rd32(frma.payload);
            if (FindBox(b.payload, b.payloadLen, fourcc("schm"), schm) && schm.payloadLen >= 8)
            {
                uint32_t st = rd32(schm.payload + 4);   // scheme_type (after version/flags)
                info.scheme = (st == fourcc("cbcs") || st == fourcc("cbc1")) ? 1 : 0;
            }
            if (FindBox(b.payload, b.payloadLen, fourcc("schi"), schi))
                if (FindBox(schi.payload, schi.payloadLen, fourcc("tenc"), tenc)) ParseTenc(tenc, info);
        }
    });
}

// Walk an MPEG-4 ES_Descriptor (the 'esds' box payload) down to the DecoderSpecificInfo, which for AAC IS the
// AudioSpecificConfig — the two bytes that tell the decoder the real object type, sample rate and channel configuration.
// Descriptor lengths use the 7-bit continuation encoding (top bit = "another length byte follows"), and getting that
// wrong yields a silently empty config rather than an error, which is why the walk is explicit here.
static void ParseEsds(const Box& esds, InitInfo& info)
{
    const uint8_t* p = esds.payload; size_t n = esds.payloadLen;
    if (n < 5) return;
    size_t o = 4;   // version + flags

    auto readTag = [&](uint8_t& tag, size_t& len) -> bool {
        if (o >= n) return false;
        tag = p[o++];
        len = 0;
        for (int i = 0; i < 4 && o < n; i++)
        {
            uint8_t b = p[o++];
            len = (len << 7) | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return o + len <= n || o <= n;   // tolerate a slightly over-declared length
    };

    uint8_t tag = 0; size_t len = 0;
    if (!readTag(tag, len) || tag != 0x03 /*ES_DescrTag*/) return;
    if (o + 3 > n) return;
    uint8_t esFlags = p[o + 2];
    o += 3;                                        // ES_ID(2) + flags(1)
    if (esFlags & 0x80) o += 2;                    // streamDependenceFlag → dependsOn_ES_ID
    if (esFlags & 0x40) { if (o >= n) return; o += 1 + p[o]; }   // URL_Flag → length-prefixed URL
    if (esFlags & 0x20) o += 2;                    // OCRstreamFlag → OCR_ES_Id

    if (!readTag(tag, len) || tag != 0x04 /*DecoderConfigDescrTag*/) return;
    if (o + 13 > n) return;
    info.avgBitrate = rd32(p + o + 9);
    o += 13;                                       // objectType(1) + streamType/bufferSize(4) + max(4) + avg(4)

    if (!readTag(tag, len) || tag != 0x05 /*DecSpecificInfoTag*/) return;
    if (len == 0 || o + len > n) return;
    info.asc.assign(p + o, p + o + len);
}

// Parse an AudioSampleEntry (mp4a/enca). Its fixed header is 28 bytes — HALF the VisualSampleEntry's 78 — and child
// boxes (esds, sinf) begin there; using the visual offset silently reads past the entry and finds nothing.
static void ParseAudioSampleEntry(const Box& entry, InitInfo& info)
{
    const uint8_t* p = entry.payload; size_t n = entry.payloadLen;
    if (n < 28) return;
    // 0..5 reserved, 6..7 data_reference_index, 8..15 reserved,
    // 16..17 channelcount, 18..19 samplesize, 20..21 pre_defined, 22..23 reserved, 24..27 samplerate (16.16 fixed).
    info.channels = rd16(p + 16);
    info.bitsPerSample = rd16(p + 18);
    info.sampleRate = rd16(p + 24);   // integer part; fractional part is always 0 in practice
    const uint8_t* kids = p + 28; size_t klen = n - 28;
    ForEachBox(kids, klen, [&](const Box& b) {
        if (b.type == fourcc("esds")) ParseEsds(b, info);
        else if (b.type == fourcc("wave"))   // QuickTime nesting: esds one level deeper
            ForEachBox(b.payload, b.payloadLen, [&](const Box& w) { if (w.type == fourcc("esds")) ParseEsds(w, info); });
        else if (b.type == fourcc("sinf"))
        {
            Box frma, schm, schi, tenc;
            if (FindBox(b.payload, b.payloadLen, fourcc("frma"), frma) && frma.payloadLen >= 4)
                info.codec4cc = rd32(frma.payload);
            if (FindBox(b.payload, b.payloadLen, fourcc("schm"), schm) && schm.payloadLen >= 8)
            {
                uint32_t st = rd32(schm.payload + 4);
                info.scheme = (st == fourcc("cbcs") || st == fourcc("cbc1")) ? 1 : 0;
            }
            if (FindBox(b.payload, b.payloadLen, fourcc("schi"), schi))
                if (FindBox(schi.payload, schi.payloadLen, fourcc("tenc"), tenc)) ParseTenc(tenc, info);
        }
    });
}

static void ParseStsd(const Box& stsd, InitInfo& info)
{
    // stsd: version/flags(4) + entry_count(4) + entries.
    if (stsd.payloadLen < 8) return;
    const uint8_t* entries = stsd.payload + 8; size_t elen = stsd.payloadLen - 8;
    bool done = false;
    ForEachBox(entries, elen, [&](const Box& b) {
        if (done) return;
        if (b.type == fourcc("encv") || b.type == fourcc("avc1") || b.type == fourcc("avc3"))
        {
            info.kind = TrackKind::Video;
            if (info.codec4cc == 0 && b.type != fourcc("encv")) info.codec4cc = b.type;
            ParseVisualSampleEntry(b, info);
            done = true;
        }
        else if (b.type == fourcc("enca") || b.type == fourcc("mp4a"))
        {
            // The video's OWN soundtrack. 'enca' is the CENC-protected wrapper whose sinf/frma names the real format.
            info.kind = TrackKind::Audio;
            if (info.codec4cc == 0 && b.type != fourcc("enca")) info.codec4cc = b.type;
            ParseAudioSampleEntry(b, info);
            done = true;
        }
    });
}

// Parse the whole init segment (moov + any pssh).
static bool ParseInit(const std::vector<uint8_t>& data, InitInfo& info)
{
    Box moov;
    if (!FindBox(data.data(), data.size(), fourcc("moov"), moov)) return false;
    // Collect pssh boxes at moov level (PlayReady init data for GenerateRequest).
    ForEachBox(moov.payload, moov.payloadLen, [&](const Box& b) {
        if (b.type == fourcc("pssh")) info.pssh.insert(info.pssh.end(), b.boxStart, b.boxStart + b.boxLen);
    });
    // trak → mdia → (mdhd timescale) + minf → stbl → stsd.
    bool ok = false;
    ForEachBox(moov.payload, moov.payloadLen, [&](const Box& trak) {
        if (trak.type != fourcc("trak")) return;
        Box mdia;
        if (!FindBox(trak.payload, trak.payloadLen, fourcc("mdia"), mdia)) return;
        Box mdhd;
        if (FindBox(mdia.payload, mdia.payloadLen, fourcc("mdhd"), mdhd) && mdhd.payloadLen >= 20)
        {
            uint8_t v = mdhd.payload[0];
            info.timescale = v == 1 ? rd32(mdhd.payload + 4 + 16) : rd32(mdhd.payload + 4 + 8);
        }
        Box minf, stbl, stsd;
        if (FindBox(mdia.payload, mdia.payloadLen, fourcc("minf"), minf) &&
            FindBox(minf.payload, minf.payloadLen, fourcc("stbl"), stbl) &&
            FindBox(stbl.payload, stbl.payloadLen, fourcc("stsd"), stsd))
        {
            ParseStsd(stsd, info);
            // Validate against the kind actually found: a video track needs its avcC, an audio track needs its
            // AudioSpecificConfig + sample rate. Validating audio against the VIDEO rule is how an audio init segment
            // gets reported as "no usable sample entry" even when it parsed perfectly.
            ok = info.kind == TrackKind::Video
                     ? (info.width > 0 && !info.avcC.empty())
                     : (info.sampleRate > 0 && !info.asc.empty());
        }
    });
    if (ok && info.kind == TrackKind::Video) ExtractSpsPps(info.avcC, info);
    if (info.timescale == 0) info.timescale = info.kind == TrackKind::Audio ? 48000 : 90000;
    return ok;
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Parsed sample (one access unit) with its CENC metadata.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct Subsample { uint32_t clearBytes; uint32_t encBytes; };
struct Sample
{
    std::vector<uint8_t> data;                 // Annex-B H.264; converted byte-for-byte from MP4/AVCC before delivery
    std::vector<uint8_t> iv;                   // CENC IV, preserved at the tenc-declared size (normally 8 bytes)
    std::vector<Subsample> subsamples;         // clear/encrypted byte runs (empty => whole-sample encrypted)
    uint64_t timeTicks = 0;                    // presentation time in media timescale ticks
    uint64_t decodeTicks = 0;                  // decode time in media timescale ticks (DTS; differs for B frames)
    uint64_t durTicks = 0;
    bool keyframe = false;
    bool encrypted = false;
};

// Media Foundation's H.264 decoder consumes Annex-B access units (start-code-prefixed NALs), while ISO BMFF stores
// AVC samples as AVCC (length-prefixed NALs). For the common four-byte MP4 NAL length field the conversion is exactly
// size preserving: replace each big-endian length with 00 00 00 01. That is crucial for CENC because the IV and the
// clear/encrypted subsample mapping describe byte offsets in this same buffer; inserting/removing bytes would corrupt
// those ranges. CENC video leaves the NAL length fields clear, so they remain readable before decryption.
static bool ConvertAvccToAnnexBInPlace(Sample& sample, uint8_t nalLenSize)
{
    if (nalLenSize != 4) return false; // This test vector (and Spotify AVC) use four-byte NAL lengths.

    auto rangeIsClear = [&](size_t begin, size_t len) {
        if (!sample.encrypted) return true;
        size_t cursor = 0;
        for (auto const& ss : sample.subsamples)
        {
            if (begin >= cursor && begin + len <= cursor + ss.clearBytes) return true;
            cursor += (size_t)ss.clearBytes + ss.encBytes;
        }
        return false;
    };

    size_t off = 0;
    while (off + 4 <= sample.data.size())
    {
        if (!rangeIsClear(off, 4)) return false;
        uint32_t nalBytes = rd32(sample.data.data() + off);
        if (nalBytes == 0 || (uint64_t)off + 4 + nalBytes > sample.data.size()) return false;
        sample.data[off + 0] = 0;
        sample.data[off + 1] = 0;
        sample.data[off + 2] = 0;
        sample.data[off + 3] = 1;
        off += 4 + nalBytes;
    }
    return off == sample.data.size();
}

// Parse one media segment (moof + mdat) and append its samples. runningDecodeTicks tracks decode time across segments.
static int ParseSegment(const std::vector<uint8_t>& seg, const InitInfo& info, std::vector<Sample>& out, uint64_t& runningDecodeTicks)
{
    // Locate moof + mdat at the top level (record moof's absolute start for trun data_offset base).
    Box moof{}, mdat{}; bool haveMoof = false, haveMdat = false; size_t moofAbs = 0;
    ForEachBox(seg.data(), seg.size(), [&](const Box& b) {
        if (b.type == fourcc("moof") && !haveMoof) { moof = b; haveMoof = true; moofAbs = (size_t)(b.boxStart - seg.data()); }
        else if (b.type == fourcc("mdat") && !haveMdat) { mdat = b; haveMdat = true; }
    });
    if (!haveMoof || !haveMdat) return 0;

    Box traf;
    if (!FindBox(moof.payload, moof.payloadLen, fourcc("traf"), traf)) return 0;

    // tfhd — defaults + base offset flags.
    uint32_t defSampleDur = 0, defSampleSize = 0, defSampleFlags = 0;
    bool defaultBaseIsMoof = false; uint64_t baseDataOffset = 0; bool haveBaseDataOffset = false;
    Box tfhd;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("tfhd"), tfhd) && tfhd.payloadLen >= 8)
    {
        uint32_t flags = rd32(tfhd.payload) & 0x00FFFFFF;
        const uint8_t* p = tfhd.payload + 8; // skip version/flags(4) + track_ID(4)
        if (flags & 0x000001) { baseDataOffset = rd64(p); haveBaseDataOffset = true; p += 8; }
        if (flags & 0x000002) { p += 4; } // sample_description_index
        if (flags & 0x000008) { defSampleDur = rd32(p); p += 4; }
        if (flags & 0x000010) { defSampleSize = rd32(p); p += 4; }
        if (flags & 0x000020) { defSampleFlags = rd32(p); p += 4; }
        defaultBaseIsMoof = (flags & 0x020000) != 0;
    }

    // tfdt — base media decode time (optional).
    Box tfdt;
    if (FindBox(traf.payload, traf.payloadLen, fourcc("tfdt"), tfdt) && tfdt.payloadLen >= 8)
        runningDecodeTicks = tfdt.payload[0] == 1 ? rd64(tfdt.payload + 4) : rd32(tfdt.payload + 4);

    // trun — per-sample sizes/durations/flags/composition offsets.
    Box trun;
    if (!FindBox(traf.payload, traf.payloadLen, fourcc("trun"), trun) || trun.payloadLen < 8) return 0;
    uint32_t trFlags = rd32(trun.payload) & 0x00FFFFFF;
    uint32_t sampleCount = rd32(trun.payload + 4);
    const uint8_t* tp = trun.payload + 8;
    int32_t dataOffset = 0; bool haveDataOffset = false;
    if (trFlags & 0x000001) { dataOffset = (int32_t)rd32(tp); tp += 4; haveDataOffset = true; }
    uint32_t firstSampleFlags = 0; bool haveFirstFlags = false;
    if (trFlags & 0x000004) { firstSampleFlags = rd32(tp); tp += 4; haveFirstFlags = true; }

    // senc — per-sample IVs + subsample mapping (inline aux info).
    struct SencEntry { std::vector<uint8_t> iv; std::vector<Subsample> subs; };
    std::vector<SencEntry> senc;
    Box sencBox;
    bool haveSenc = FindBox(traf.payload, traf.payloadLen, fourcc("senc"), sencBox);
    if (haveSenc && sencBox.payloadLen >= 8)
    {
        uint32_t sflags = rd32(sencBox.payload) & 0x00FFFFFF;
        uint32_t count = rd32(sencBox.payload + 4);
        const uint8_t* sp = sencBox.payload + 8;
        const uint8_t* send = sencBox.payload + sencBox.payloadLen;
        uint8_t ivSize = info.perSampleIvSize ? info.perSampleIvSize : 0;
        for (uint32_t i = 0; i < count && sp <= send; i++)
        {
            SencEntry e;
            if (ivSize > 0) { if (sp + ivSize > send) break; e.iv.assign(sp, sp + ivSize); sp += ivSize; }
            if (sflags & 0x000002)
            {
                if (sp + 2 > send) break;
                uint16_t subCount = rd16(sp); sp += 2;
                for (uint16_t s = 0; s < subCount && sp + 6 <= send; s++)
                {
                    Subsample ss; ss.clearBytes = rd16(sp); ss.encBytes = rd32(sp + 2); sp += 6;
                    e.subs.push_back(ss);
                }
            }
            senc.push_back(std::move(e));
        }
    }

    // Sample data base: default-base-is-moof => moof start; else explicit base-data-offset; else 0 (segment-relative).
    size_t base = defaultBaseIsMoof ? moofAbs : (haveBaseDataOffset ? (size_t)baseDataOffset : moofAbs);
    size_t cursor = base + (haveDataOffset ? (size_t)(int64_t)dataOffset : 0);

    int produced = 0;
    for (uint32_t i = 0; i < sampleCount; i++)
    {
        uint32_t sz = defSampleSize, dur = defSampleDur, flags = defSampleFlags;
        // Per-sample fields in order: duration, size, flags, composition-offset.
        if (trFlags & 0x000100) { dur = rd32(tp); tp += 4; }
        if (trFlags & 0x000200) { sz = rd32(tp); tp += 4; }
        if (trFlags & 0x000400) { flags = rd32(tp); tp += 4; }
        int64_t cto = 0;
        if (trFlags & 0x000800) { cto = (int32_t)rd32(tp); tp += 4; }
        if (i == 0 && haveFirstFlags) flags = firstSampleFlags;

        if (cursor + sz > seg.size()) break;
        Sample s;
        s.data.assign(seg.data() + cursor, seg.data() + cursor + sz);
        s.durTicks = dur;
        s.decodeTicks = runningDecodeTicks;
        int64_t t = (int64_t)s.decodeTicks + cto;
        s.timeTicks = t < 0 ? 0 : (uint64_t)t;
        // Every AAC access unit is independently decodable, so an audio track is all sync samples regardless of what the
        // trun flags happen to say (some muxers set the non-sync bit on audio, which would leave a seek with no
        // reposition target and — worse — make the first delivered sample look like a non-clean point).
        s.keyframe = info.kind == TrackKind::Audio || (flags & 0x00010000) == 0;
        s.encrypted = info.encrypted;

        // IV: per-sample from senc, else constant IV (cbcs). Preserve the declared byte length exactly.
        // MFSampleExtension_Encryption_SampleID expects m_bIVSize bytes; padding an 8-byte CENC IV to 16 changes
        // the counter block interpreted by the PlayReady decryptor and leaves the decoder with ciphertext.
        std::vector<uint8_t> iv;
        const std::vector<uint8_t>* src = nullptr;
        if (i < senc.size() && !senc[i].iv.empty()) src = &senc[i].iv;
        else if (!info.constIv.empty()) src = &info.constIv;
        if (src) iv = *src;
        s.iv = std::move(iv);
        if (i < senc.size()) s.subsamples = senc[i].subs;

        // ── AVC-ONLY transforms. An AAC access unit is already exactly what the decoder wants: no length prefixes to
        // rewrite and no parameter sets to prepend (its configuration travels out-of-band in the media type's
        // AudioSpecificConfig). Running either transform over audio would corrupt the payload AND desynchronise the CENC
        // subsample mapping, so both are gated on the track kind rather than on "did it happen to parse".
        if (info.kind == TrackKind::Audio)
        {
            out.push_back(std::move(s));
            cursor += sz;
            runningDecodeTicks += dur;
            produced++;
            continue;
        }

        // The decoder accepts Annex-B, not the MP4/AVCC payload stored in mdat. Four-byte replacement preserves every
        // CENC byte offset. Refuse malformed/unsupported samples rather than delivering a packet the decoder can only
        // report later as the opaque MF_E_INVALIDREQUEST (0xC00D36B2).
        if (!ConvertAvccToAnnexBInPlace(s, info.nalLenSize))
        {
            LogLine("[cenc] AVCC->AnnexB failed sample=" + std::to_string(i) +
                    " nalLenSize=" + std::to_string(info.nalLenSize) +
                    " bytes=" + std::to_string(s.data.size()) +
                    " subs=" + std::to_string(s.subsamples.size()));
            break;
        }

        // Annex-B keyframes must carry their parameter sets IN-BAND: avc1 samples reference SPS/PPS only via the
        // container's avcC, and after the byte-stream conversion the decoder never sees them (the first NAL here is
        // typically an SEI) — MF_MT_MPEG_SEQUENCE_HEADER alone does not save the protected pipeline, which fails the
        // very first sample with MF_E_INVALIDREQUEST. Firefox's proven desktop MFCDM path prepends the Annex-B
        // SPS/PPS to every keyframe and widens the FIRST CLEAR subsample by the prepended length so the CENC byte
        // mapping still describes the same ciphertext (gecko AnnexB::ConvertAVCCSampleToAnnexB, aAddSPS).
        if (s.keyframe && !info.spspps.empty())
        {
            s.data.insert(s.data.begin(), info.spspps.begin(), info.spspps.end());
            if (s.encrypted)
            {
                if (s.subsamples.empty())
                {
                    Subsample ss;
                    ss.clearBytes = (uint32_t)info.spspps.size();
                    ss.encBytes = (uint32_t)(s.data.size() - info.spspps.size());
                    s.subsamples.push_back(ss);
                }
                else
                {
                    s.subsamples[0].clearBytes += (uint32_t)info.spspps.size();
                }
            }
        }

        out.push_back(std::move(s));
        cursor += sz;
        runningDecodeTicks += dur;
        produced++;
    }
    return produced;
}

} // namespace cenc

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Custom IMFMediaStream — serves the demuxed encrypted samples with CENC per-sample attributes.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct CencMediaSource;   // fwd

struct CencMediaStream : winrt::implements<CencMediaStream, IMFMediaStream>
{
    winrt::com_ptr<IMFMediaEventQueue> m_queue;
    winrt::com_ptr<IMFStreamDescriptor> m_sd;
    IMFMediaSource* m_source = nullptr;   // weak (the source owns this stream)
    std::vector<cenc::Sample> m_samples;
    cenc::InitInfo m_info;
    size_t m_next = 0;
    DWORD m_streamId = 1;                 // 1 = video, 2 = audio (matches the stream descriptors in BuildCencSource)
    const char* m_label = "video";        // log prefix only
    bool m_started = false, m_paused = false, m_eos = false, m_discontinuity = true, m_shutdown = false;
    // Incremental feeding: the fetcher appends segments while playback runs, so the first frame does not wait for the
    // whole track to download. Until m_complete is set, running out of samples is STARVATION (park the request and
    // satisfy it when more arrive), not end-of-stream — reporting EOS there would truncate the track to its prefix.
    bool m_complete = false;
    std::vector<winrt::com_ptr<::IUnknown>> m_pausedRequests;
    std::vector<winrt::com_ptr<::IUnknown>> m_starvedRequests;
    std::mutex m_mx;

    CencMediaStream() { winrt::check_hresult(MFCreateEventQueue(m_queue.put())); }

    // IMFMediaEventGenerator (delegate to the queue).
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* c, ::IUnknown* s) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->BeginGetEvent(c, s); }
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->EndGetEvent(r, e); }
    IFACEMETHODIMP GetEvent(DWORD f, IMFMediaEvent** e) noexcept override { winrt::com_ptr<IMFMediaEventQueue> q; { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; q = m_queue; } return q->GetEvent(f, e); }
    IFACEMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT s, const PROPVARIANT* v) noexcept override { std::lock_guard<std::mutex> gd(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->QueueEventParamVar(t, g, s, v); }

    // IMFMediaStream
    IFACEMETHODIMP GetMediaSource(IMFMediaSource** ppSource) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!ppSource) return E_POINTER;
        if (!m_source) return MF_E_NOT_INITIALIZED;
        m_source->AddRef(); *ppSource = m_source; return S_OK;
    }
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** ppSD) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!ppSD) return E_POINTER;
        m_sd.copy_to(ppSD); return S_OK;
    }
    IFACEMETHODIMP RequestSample(::IUnknown* pToken) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!m_started) return MF_E_MEDIA_SOURCE_WRONGSTATE;
        // A paused source must ACCEPT sample requests but deliver nothing until the next Start (IMFMediaSource::Pause
        // contract). Retain the token so resume can satisfy the exact request instead of depending on a re-request.
        if (m_paused)
        {
            winrt::com_ptr<::IUnknown> token;
            if (pToken) token.copy_from(pToken);
            m_pausedRequests.push_back(std::move(token));
            return S_OK;
        }
        return DeliverSampleLocked(pToken);
    }

    HRESULT DeliverSampleLocked(::IUnknown* pToken)
    {
        if (m_next >= m_samples.size())
        {
            if (!m_complete)
            {
                // Starved, not finished: park the request. AppendSamples releases it the moment the next segment lands.
                winrt::com_ptr<::IUnknown> token;
                if (pToken) token.copy_from(pToken);
                m_starvedRequests.push_back(std::move(token));
                if (m_starvedRequests.size() == 1)
                    LogLine(std::string("[cenc-src] ") + m_label + " starved at sample " + std::to_string(m_next) +
                            " — awaiting fetch (not EOS)");
                return S_OK;
            }
            if (!m_eos) { m_eos = true; m_queue->QueueEventParamVar(MEEndOfStream, GUID_NULL, S_OK, nullptr); NotifySourceEnded(); }
            return S_OK;
        }
        if (m_next == 0)
        {
            auto const& first = m_samples[0];
            uint64_t clearTotal = 0, encryptedTotal = 0;
            for (auto const& part : first.subsamples)
            {
                clearTotal += part.clearBytes;
                encryptedTotal += part.encBytes;
            }
            LogLine("[cenc-src] sample#0 bytes=" + std::to_string(first.data.size()) +
                    " kf=" + std::to_string(first.keyframe ? 1 : 0) +
                    " iv=" + std::to_string(first.iv.size()) + "B subs=" +
                    std::to_string(first.subsamples.size()) + " clear=" +
                    std::to_string(clearTotal) + " encrypted=" + std::to_string(encryptedTotal) +
                    (first.data.size() >= 8
                        ? " head=" + std::to_string(first.data[0]) + "," + std::to_string(first.data[1]) +
                          "," + std::to_string(first.data[2]) + "," + std::to_string(first.data[3]) +
                          "," + std::to_string(first.data[4]) + "," + std::to_string(first.data[5]) +
                          "," + std::to_string(first.data[6]) + "," + std::to_string(first.data[7])
                        : " head=<short>"));
        }
        winrt::com_ptr<IMFSample> sample;
        HRESULT hr = MakeSample(m_samples[m_next], sample.put());
        if (FAILED(hr)) return hr;
        if (pToken) sample->SetUnknown(MFSampleExtension_Token, pToken);
        if (m_discontinuity)
        {
            sample->SetUINT32(MFSampleExtension_Discontinuity, TRUE);
            m_discontinuity = false;
        }
        if (m_next == 0 || (m_next % 100) == 0) LogLine("[cenc-src] RequestSample #" + std::to_string(m_next) + " (encrypted sample delivered)");
        m_next++;
        return m_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.get());
    }

    // Complete one Start operation on the stream. An explicit position repositions to the nearest keyframe at or
    // before the requested presentation time; Media Foundation discards the decoded preroll before the exact target.
    // A resume passes VT_EMPTY and deliberately preserves m_next/decoder history.
    void Start(const PROPVARIANT* startPos, bool seeking, bool reposition)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (reposition)
        {
            LONGLONG requested100ns = startPos && startPos->vt == VT_I8
                ? std::max<LONGLONG>(0, startPos->hVal.QuadPart)
                : 0;
            uint64_t targetTicks = 0;
            if (m_info.timescale > 0)
            {
                uint64_t wholeSeconds = (uint64_t)requested100ns / 10000000ULL;
                uint64_t remainder100ns = (uint64_t)requested100ns % 10000000ULL;
                targetTicks = wholeSeconds * m_info.timescale +
                              (remainder100ns * m_info.timescale) / 10000000ULL;
            }

            size_t best = 0;
            uint64_t bestTime = 0;
            bool found = false;
            for (size_t i = 0; i < m_samples.size(); i++)
            {
                auto const& sample = m_samples[i];
                if (!sample.keyframe || sample.timeTicks > targetTicks) continue;
                if (!found || sample.timeTicks >= bestTime)
                {
                    best = i;
                    bestTime = sample.timeTicks;
                    found = true;
                }
            }
            m_next = found ? best : 0;
            m_discontinuity = true;
            LogLine("[cenc-src] seek target100ns=" + std::to_string((long long)requested100ns) +
                    " -> sample=" + std::to_string(m_next) +
                    " keyframe100ns=" + std::to_string(m_info.timescale > 0
                        ? (long long)((bestTime * 10000000ULL) / m_info.timescale) : 0));
        }
        m_started = true; m_paused = false; m_eos = false;
        m_queue->QueueEventParamVar(seeking ? MEStreamSeeked : MEStreamStarted, GUID_NULL, S_OK, startPos);
        // Requests accepted during Pause are released only AFTER MEStreamStarted, which is the exact boundary after
        // which the stream may resume data delivery. Deliver under the same stream lock to preserve request order.
        for (auto const& token : m_pausedRequests) DeliverSampleLocked(token.get());
        m_pausedRequests.clear();
        ReleaseStarvedLocked();   // a request parked while stopped/paused becomes deliverable again here
    }
    void Pause()
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (!m_started || m_paused) return;
        m_paused = true;
        m_queue->QueueEventParamVar(MEStreamPaused, GUID_NULL, S_OK, nullptr);
    }
    void Stop()
    {
        std::lock_guard<std::mutex> g(m_mx);
        m_started = false; m_paused = false;
        m_next = 0; m_eos = false; m_discontinuity = true;
        m_pausedRequests.clear();
        m_starvedRequests.clear();
        m_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }

    /// How much already-delivered history stays resident behind the playhead. Without a bound the vector grows for the
    /// whole track AND for every seek (the memory half of the feeder wedge). What is retained is also exactly what a
    /// short backward scrub can be served from without a refetch.
    static constexpr size_t kRetainBehind = 300;

    /// Insert a freshly demuxed run so the buffer stays MONOTONIC in presentation time.
    ///
    /// The feeder rewinds its cursor on a seek and on a representation switch, so an incoming run can overlap what is
    /// already buffered. Appending it at the tail (what this used to do) left the vector non-monotonic and duplicated:
    /// Ahead()/AheadDurationMs() then over-reported and the feeder parked in its backpressure sleep. Instead, drop
    /// everything at or after the incoming start time first — but NEVER anything already handed to Media Foundation,
    /// which owns those samples. Returns the index the run landed at.
    size_t SpliceLocked(std::vector<cenc::Sample>&& incoming)
    {
        const uint64_t startTicks = incoming.front().timeTicks;
        size_t cut = m_samples.size();
        for (size_t i = m_next; i < m_samples.size(); i++)
            if (m_samples[i].timeTicks >= startTicks) { cut = i; break; }
        if (cut < m_samples.size())
            m_samples.erase(m_samples.begin() + (ptrdiff_t)cut, m_samples.end());
        m_samples.insert(m_samples.end(), std::make_move_iterator(incoming.begin()),
                         std::make_move_iterator(incoming.end()));
        return cut;
    }

    /// Drop history beyond the retention window, keeping m_next pointing at the same sample.
    void TrimBehindLocked()
    {
        if (m_next <= kRetainBehind) return;
        const size_t drop = m_next - kRetainBehind;
        m_samples.erase(m_samples.begin(), m_samples.begin() + (ptrdiff_t)drop);
        m_next -= drop;
    }

    /// Append freshly demuxed samples (the background fetcher) and release any request that was parked on starvation.
    /// Called from the fetch thread; the stream lock serialises it against RequestSample.
    void AppendSamples(std::vector<cenc::Sample>&& more)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown || more.empty()) return;
        SpliceLocked(std::move(more));
        TrimBehindLocked();
        ReleaseStarvedLocked();
    }

    /// Splice the target representation's segment into the timeline at ITS OWN presentation time, then announce the new
    /// H.264 geometry. The feeder calls this at a segment boundary at-or-after the playhead, so everything between the
    /// playhead and the splice point stays in the OLD representation and the timeline remains contiguous. (Erasing from
    /// m_next unconditionally — what this used to do — deleted every buffered sample under the playhead and refilled
    /// from the feeder's stale cursor, which is what punched a multi-second hole in the video while audio kept running:
    /// the frozen frame.) Already delivered samples remain owned by Media Foundation.
    void SwitchVideoRepresentation(const cenc::InitInfo& nextInfo, std::vector<cenc::Sample>&& replacement)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown || nextInfo.kind != cenc::TrackKind::Video || replacement.empty()) return;
        const uint64_t spliceTicks = replacement.front().timeTicks;
        const size_t at = SpliceLocked(std::move(replacement));
        m_info = nextInfo;
        // Only the sample delivered NEXT may be flagged discontinuous. When the splice lands ahead of the playhead the
        // next sample is still old-representation continuous video; flagging it would make the decoder drop frames all
        // the way to the new keyframe.
        if (at <= m_next) m_discontinuity = true;
        m_complete = false;

        // The protected wrapper remains the same stream type; update its display/config attributes and publish the
        // standard format-change event. The first replacement access unit also carries the new SPS/PPS, which is the
        // decoder-authoritative configuration for H.264 dynamic resolution changes.
        winrt::com_ptr<IMFMediaTypeHandler> handler;
        winrt::com_ptr<IMFMediaType> current;
        if (m_sd && SUCCEEDED(m_sd->GetMediaTypeHandler(handler.put())) && handler &&
            SUCCEEDED(handler->GetCurrentMediaType(current.put())) && current)
        {
            MFSetAttributeSize(current.get(), MF_MT_FRAME_SIZE, nextInfo.width, nextInfo.height);
            if (!nextInfo.spspps.empty())
                current->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, nextInfo.spspps.data(), (UINT32)nextInfo.spspps.size());
            m_queue->QueueEventParamUnk(MEStreamFormatChanged, GUID_NULL, S_OK, current.get());
        }
        const int64_t spliceMs = m_info.timescale ? (int64_t)((spliceTicks * 1000ULL) / m_info.timescale) : 0;
        const int64_t playheadMs = (m_info.timescale && m_next < m_samples.size())
            ? (int64_t)((m_samples[m_next].timeTicks * 1000ULL) / m_info.timescale) : 0;
        LogLine("[cenc-src] video switched to " + std::to_string(nextInfo.width) + "x" +
                std::to_string(nextInfo.height) + " spliced at sample " + std::to_string(at) +
                " (t=" + std::to_string((long long)spliceMs) + "ms) with the playhead at sample " +
                std::to_string(m_next) + " (t=" + std::to_string((long long)playheadMs) + "ms), buffer=" +
                std::to_string(m_samples.size()) + " sample(s)");
        ReleaseStarvedLocked();
    }

    /// No more samples are coming (the fetcher finished, or gave up). After this, running dry is a real end of stream —
    /// including for requests parked while starved, which must not hang forever if the fetch failed.
    void MarkComplete()
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_complete) return;
        m_complete = true;
        LogLine(std::string("[cenc-src] ") + m_label + " feed complete: " + std::to_string(m_samples.size()) + " sample(s)");
        ReleaseStarvedLocked();
    }

    /// How many demuxed samples are buffered ahead of the playhead — the fetcher's backpressure signal, so it stays a
    /// bounded distance in front of playback instead of pulling the whole track into memory.
    size_t Ahead()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_samples.size() > m_next ? m_samples.size() - m_next : 0;
    }
    int64_t AheadDurationMs()
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_info.timescale == 0 || m_next >= m_samples.size()) return 0;
        uint64_t start = m_samples[m_next].timeTicks;
        auto const& last = m_samples.back();
        uint64_t end = last.timeTicks + last.durTicks;
        return end > start ? (int64_t)(((end - start) * 1000ULL) / m_info.timescale) : 0;
    }
    int64_t NextSampleTimeMs()
    {
        std::lock_guard<std::mutex> g(m_mx);
        return m_info.timescale > 0 && m_next < m_samples.size()
            ? (int64_t)((m_samples[m_next].timeTicks * 1000ULL) / m_info.timescale)
            : 0;
    }

    /// Can a seek to `targetMs` be served from what is ALREADY buffered? This is what turns a backward scrub into an
    /// instant reposition instead of three serial TLS-handshaking GETs. Video needs a KEYFRAME at or before the target
    /// (that is what Start() repositions to); audio only needs coverage. Both need CONTIGUOUS coverage past the target
    /// — a buffer with a hole in it (left by an earlier forward seek) must not report the far side as seekable.
    bool CanSeekTo(int64_t targetMs, bool requireKeyframe)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown || m_info.timescale == 0 || m_samples.empty()) return false;
        if (targetMs < 0) targetMs = 0;
        const uint64_t target = ((uint64_t)targetMs * m_info.timescale) / 1000ULL;
        size_t anchor = m_samples.size();
        for (size_t i = 0; i < m_samples.size(); i++)
        {
            if (m_samples[i].timeTicks > target) break;
            if (!requireKeyframe || m_samples[i].keyframe) anchor = i;
        }
        if (anchor == m_samples.size()) return false;
        uint64_t reach = m_samples[anchor].timeTicks;
        for (size_t i = anchor; i < m_samples.size(); i++)
        {
            if (m_samples[i].timeTicks > reach) break;   // gap in the buffer — coverage ends here
            const uint64_t end = m_samples[i].timeTicks + m_samples[i].durTicks;
            if (end > reach) reach = end;
        }
        return reach > target;
    }

    /// The buffered presentation range, for the always-on log.
    void BufferedRangeMs(int64_t& startMs, int64_t& endMs)
    {
        std::lock_guard<std::mutex> g(m_mx);
        startMs = endMs = 0;
        if (m_info.timescale == 0 || m_samples.empty()) return;
        startMs = (int64_t)((m_samples.front().timeTicks * 1000ULL) / m_info.timescale);
        auto const& last = m_samples.back();
        endMs = (int64_t)(((last.timeTicks + last.durTicks) * 1000ULL) / m_info.timescale);
    }

    bool IsShutdown() { std::lock_guard<std::mutex> g(m_mx); return m_shutdown; }

    void ReleaseStarvedLocked()
    {
        if (m_starvedRequests.empty() || !m_started || m_paused) return;
        auto parked = std::move(m_starvedRequests);
        m_starvedRequests.clear();
        for (auto const& token : parked) DeliverSampleLocked(token.get());
    }
    void Shutdown() { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return; m_shutdown = true; if (m_queue) m_queue->Shutdown(); }

    void NotifySourceEnded();   // defined after CencMediaSource

    // Build one encrypted IMFSample with its CENC attributes.
    HRESULT MakeSample(const cenc::Sample& s, IMFSample** ppSample)
    {
        winrt::com_ptr<IMFSample> sample;
        winrt::com_ptr<IMFMediaBuffer> buf;
        HRESULT hr = MFCreateSample(sample.put());
        if (FAILED(hr)) return hr;
        hr = MFCreateMemoryBuffer((DWORD)s.data.size(), buf.put());
        if (FAILED(hr)) return hr;
        BYTE* dst = nullptr; DWORD maxLen = 0;
        hr = buf->Lock(&dst, &maxLen, nullptr);
        if (FAILED(hr)) return hr;
        memcpy(dst, s.data.data(), s.data.size());
        buf->Unlock();
        buf->SetCurrentLength((DWORD)s.data.size());
        sample->AddBuffer(buf.get());

        auto toMf = [&](uint64_t ticks) -> LONGLONG { return (LONGLONG)((ticks * 10000000ULL) / m_info.timescale); };
        sample->SetSampleTime(toMf(s.timeTicks));
        sample->SetSampleDuration(toMf(s.durTicks));
        sample->SetUINT64(MFSampleExtension_DecodeTimestamp, (UINT64)toMf(s.decodeTicks));
        if (s.keyframe) sample->SetUINT32(MFSampleExtension_CleanPoint, 1);

        if (s.encrypted)
        {
            sample->SetUINT32(MFSampleExtension_Encryption_ProtectionScheme,
                              m_info.scheme == 1 ? MF_SAMPLE_ENCRYPTION_PROTECTION_SCHEME_AES_CBC
                                                 : MF_SAMPLE_ENCRYPTION_PROTECTION_SCHEME_AES_CTR);
            // ISO BMFF tenc stores default_KID as a 16-byte big-endian UUID. MFSampleExtension_Content_KeyID is a
            // Windows GUID, whose Data1/Data2/Data3 fields have little-endian in-memory representation. A raw memcpy
            // asks the CDM for a different key even though the proactively licensed key reports USABLE.
            GUID kid{};
            kid.Data1 = cenc::rd32(m_info.kid);
            kid.Data2 = cenc::rd16(m_info.kid + 4);
            kid.Data3 = cenc::rd16(m_info.kid + 6);
            memcpy(kid.Data4, m_info.kid + 8, 8);
            sample->SetGUID(MFSampleExtension_Content_KeyID, kid);
            sample->SetBlob(MFSampleExtension_Encryption_SampleID, s.iv.data(), (UINT32)s.iv.size());
            if (!s.subsamples.empty())
            {
                // The modern protected/CDM path consumes SubSample_Mapping (the attribute Chromium uses). Do not also
                // publish legacy SubSampleMappingSplit: the PlayReady transform treats two maps as an ambiguous,
                // non-empty duplicate property and fails the sample with MF_E_PROPERTY_NOT_EMPTY.
                std::vector<uint32_t> map; map.reserve(s.subsamples.size() * 2);
                for (auto const& ss : s.subsamples) { map.push_back(ss.clearBytes); map.push_back(ss.encBytes); }
                sample->SetBlob(MFSampleExtension_Encryption_SubSample_Mapping,
                                (const UINT8*)map.data(), (UINT32)(map.size() * sizeof(uint32_t)));
            }
            if (m_info.scheme == 1)
            {
                sample->SetUINT32(MFSampleExtension_Encryption_CryptByteBlock, m_info.cryptByteBlock);
                sample->SetUINT32(MFSampleExtension_Encryption_SkipByteBlock, m_info.skipByteBlock);
            }
        }
        *ppSample = sample.detach();
        return S_OK;
    }
};

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Custom IMFMediaSource — one video stream of demuxed encrypted samples.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
struct CencMediaSource : winrt::implements<CencMediaSource, IMFMediaSource, IMFTrustedInput>
{
    winrt::com_ptr<IMFMediaEventQueue> m_queue;
    winrt::com_ptr<IMFPresentationDescriptor> m_pd;
    // Every stream this source can serve: video (id 1) and, for a music video, its own soundtrack (id 2). A vector
    // rather than a single member because the topology loader selects and starts them independently.
    std::vector<winrt::com_ptr<CencMediaStream>> m_streams;
    winrt::com_ptr<IMFTrustedInput> m_trustedInput;
    // One STABLE Input Trust Authority per stream id. Re-creating an authority for a stream the loader already asked
    // about resets PlayReady's policy/decrypter state and is rejected as DRM_E_LOGICERR — and with two streams the
    // loader alternates between them, so a single-slot cache would thrash on every other query. The cached value is the
    // exact IUnknown proxy the CDM handed back: re-querying another interface into this output can produce a
    // proxy/vtable mismatch across the PMP boundary (mirrors Firefox's MFCDMProxy cache).
    std::map<DWORD, winrt::com_ptr<::IUnknown>> m_itaByStream;
    std::set<DWORD> m_announcedStreams;   // MENewStream is sent once per stream; later Starts send MEUpdatedStream
    std::set<DWORD> m_endedStreams;       // MEEndOfPresentation waits for ALL streams (see NotifyStreamEnded)
    bool m_started = false, m_paused = false, m_shutdown = false;
    std::mutex m_mx;

    CencMediaSource() { winrt::check_hresult(MFCreateEventQueue(m_queue.put())); }

    // IMFMediaEventGenerator
    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* c, ::IUnknown* s) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->BeginGetEvent(c, s); }
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* r, IMFMediaEvent** e) noexcept override { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->EndGetEvent(r, e); }
    IFACEMETHODIMP GetEvent(DWORD f, IMFMediaEvent** e) noexcept override { winrt::com_ptr<IMFMediaEventQueue> q; { std::lock_guard<std::mutex> g(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; q = m_queue; } return q->GetEvent(f, e); }
    IFACEMETHODIMP QueueEvent(MediaEventType t, REFGUID g, HRESULT s, const PROPVARIANT* v) noexcept override { std::lock_guard<std::mutex> gd(m_mx); if (m_shutdown) return MF_E_SHUTDOWN; return m_queue->QueueEventParamVar(t, g, s, v); }

    // IMFMediaSource
    IFACEMETHODIMP GetCharacteristics(DWORD* pdw) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!pdw) return E_POINTER;
        *pdw = MFMEDIASOURCE_CAN_PAUSE | MFMEDIASOURCE_CAN_SEEK;
        return S_OK;
    }
    IFACEMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** ppPD) noexcept override
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return MF_E_SHUTDOWN;
        if (!ppPD) return E_POINTER;
        if (!m_pd) return MF_E_NOT_INITIALIZED;
        return m_pd->Clone(ppPD);
    }
    IFACEMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPos) noexcept override
    {
        PROPVARIANT startVar; PropVariantInit(&startVar);
        std::vector<winrt::com_ptr<CencMediaStream>> starting;
        bool wasActive = false, seeking = false, explicitPosition = false;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
            if (!pd || !startPos || (startPos->vt != VT_I8 && startPos->vt != VT_EMPTY)) return E_INVALIDARG;
            if (FAILED(PropVariantCopy(&startVar, startPos))) return E_OUTOFMEMORY;

            wasActive = m_started;
            explicitPosition = startVar.vt == VT_I8;
            seeking = wasActive && explicitPosition;

            // Honour the SELECTION the topology loader made: walk every descriptor in the presentation descriptor it
            // handed us and start exactly the streams marked selected. Assuming index 0 is the only stream is what makes
            // a second (audio) stream impossible — it would never be announced, so nothing would ever request from it.
            DWORD count = 0;
            if (FAILED(pd->GetStreamDescriptorCount(&count)) || count == 0)
            {
                PropVariantClear(&startVar);
                return MF_E_INVALIDREQUEST;
            }
            std::string sel;
            for (DWORD i = 0; i < count; i++)
            {
                BOOL selected = FALSE; winrt::com_ptr<IMFStreamDescriptor> sd;
                if (FAILED(pd->GetStreamDescriptorByIndex(i, &selected, sd.put())) || !sd) continue;
                DWORD id = 0;
                if (FAILED(sd->GetStreamIdentifier(&id))) continue;
                auto stream = FindStreamLocked(id);
                if (!stream) continue;
                sel += (sel.empty() ? "" : ",") + std::string(stream->m_label) + (selected ? ":on" : ":off");
                if (!selected) continue;

                bool isNew = m_announcedStreams.insert(id).second;
                m_queue->QueueEventParamUnk(isNew ? MENewStream : MEUpdatedStream, GUID_NULL, S_OK,
                                            (::IUnknown*)(IMFMediaStream*)stream.get());
                starting.push_back(stream);
            }
            if (starting.empty())
            {
                PropVariantClear(&startVar);
                return MF_E_INVALIDREQUEST;
            }

            LogLine("[cenc-src] Start previous=" + std::string(m_paused ? "paused" : (wasActive ? "started" : "stopped")) +
                    " position=" + (explicitPosition
                        ? std::to_string((long long)startVar.hVal.QuadPart) : std::string("current")) +
                    " event=" + (seeking ? "seeked" : "started") + " streams=[" + sel + "]");

            m_started = true;
            m_paused = false;
            if (explicitPosition || !wasActive) m_endedStreams.clear();   // a reposition un-ends the presentation
            // The source event precedes the corresponding stream events (the documented custom-source sequence).
            m_queue->QueueEventParamVar(seeking ? MESourceSeeked : MESourceStarted, GUID_NULL, S_OK, &startVar);
        }

        // A non-empty position also repositions a previously stopped source, but that operation is still a Start
        // (MESourceStarted/MEStreamStarted), not a seek. VT_EMPTY is pause-resume and preserves the sample cursor.
        for (auto const& stream : starting)
            stream->Start(&startVar, seeking, explicitPosition || !wasActive);
        PropVariantClear(&startVar);
        return S_OK;
    }

    winrt::com_ptr<CencMediaStream> FindStreamLocked(DWORD id)
    {
        for (auto const& s : m_streams) if (s && s->m_streamId == id) return s;
        return nullptr;
    }
    IFACEMETHODIMP Stop() noexcept override
    {
        std::vector<winrt::com_ptr<CencMediaStream>> streams;
        HRESULT hr;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            m_started = false; m_paused = false;
            m_endedStreams.clear();
            streams = m_streams;
            hr = m_queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
        }
        for (auto const& s : streams) if (s) s->Stop();
        return hr;
    }
    IFACEMETHODIMP Pause() noexcept override
    {
        std::vector<winrt::com_ptr<CencMediaStream>> streams;
        HRESULT hr;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            if (!m_started || m_paused) return MF_E_INVALID_STATE_TRANSITION;
            m_paused = true;
            streams = m_streams;
            LogLine("[cenc-src] Pause -> MESourcePaused + MEStreamPaused");
            hr = m_queue->QueueEventParamVar(MESourcePaused, GUID_NULL, S_OK, nullptr);
        }
        for (auto const& s : streams) if (s) s->Pause();
        return hr;
    }
    IFACEMETHODIMP Shutdown() noexcept override
    {
        // Shut the streams down OUTSIDE this lock. A stream that is mid-delivery holds its own lock and then reaches for
        // the source lock (end-of-stream notification); taking them in the opposite order here — source first, then
        // stream — is a textbook lock inversion, and with two streams delivering concurrently it is reachable.
        // Setting m_shutdown first makes the notification a no-op, so nothing is lost by releasing the lock.
        std::vector<winrt::com_ptr<CencMediaStream>> streams;
        {
            std::lock_guard<std::mutex> g(m_mx);
            if (m_shutdown) return MF_E_SHUTDOWN;
            m_shutdown = true;
            streams = m_streams;
        }
        for (auto const& s : streams) if (s) s->Shutdown();
        std::lock_guard<std::mutex> g(m_mx);
        if (m_queue) m_queue->Shutdown();
        return S_OK;
    }

    // IMFTrustedInput. A normal desktop protected topology asks the source for the CDM's Input Trust Authority, which
    // supplies the PMP decrypter and output policy for this stream.
    IFACEMETHODIMP GetInputTrustAuthority(DWORD streamId, REFIID riid, IUnknown** value) noexcept override
    {
        if (!value) return E_POINTER;
        *value = nullptr;
        if (!m_trustedInput) return MF_E_NOT_INITIALIZED;
        std::lock_guard<std::mutex> g(m_mx);

        // The topology loader asks repeatedly while resolving each protected branch, and with two streams it ALTERNATES
        // between them — so the cache must be keyed by stream id. A single slot would evict the video's authority when
        // the audio branch resolves (and vice versa), and the re-created authority is rejected as DRM_E_LOGICERR.
        HRESULT hr;
        bool cached = false;
        auto it = m_itaByStream.find(streamId);
        if (it != m_itaByStream.end() && it->second)
        {
            // Hand back the EXACT proxy pointer the CDM returned the first time. GetInputTrustAuthority's output is
            // IUnknown** even though riid normally requests IMFInputTrustAuthority; re-querying another interface into
            // this output can produce a proxy/vtable mismatch across the PMP boundary. Mirrors Firefox's MFCDMProxy.
            *value = it->second.get();
            (*value)->AddRef();
            hr = S_OK;
            cached = true;
        }
        else
        {
            winrt::com_ptr<::IUnknown> unknown;
            hr = m_trustedInput->GetInputTrustAuthority(streamId, riid, unknown.put());
            if (SUCCEEDED(hr) && unknown)
            {
                m_itaByStream[streamId] = unknown;      // keep the proxy identity itself, not a re-queried interface
                *value = unknown.detach();
                hr = S_OK;
            }
        }
        std::stringstream ss; ss << "[cenc-src] GetInputTrustAuthority stream=" << streamId
                                 << (cached ? " (cached)" : " (fresh)")
                                 << " hr=0x" << std::hex << (uint32_t)hr;
        LogLine(ss.str());
        return hr;
    }

    /// One stream drained. The PRESENTATION only ends when EVERY selected stream has: video and audio never hold the
    /// same number of samples, so a first-stream-wins MEEndOfPresentation truncates playback at whichever track runs out
    /// first — the shorter one, always, and usually well before the end of the song.
    void NotifyStreamEnded(DWORD streamId)
    {
        std::lock_guard<std::mutex> g(m_mx);
        if (m_shutdown) return;
        m_endedStreams.insert(streamId);
        // The denominator is the streams the topology actually SELECTED, not every stream we offered. If MF declined the
        // audio branch, that stream is never started and can never end — waiting for it would hold the presentation open
        // forever and the track would never advance.
        size_t total = m_announcedStreams.empty() ? m_streams.size() : m_announcedStreams.size();
        if (m_endedStreams.size() < total)
        {
            LogLine("[cenc-src] stream " + std::to_string(streamId) + " ended (" +
                    std::to_string(m_endedStreams.size()) + "/" + std::to_string(total) +
                    ") — holding MEEndOfPresentation");
            return;
        }
        LogLine("[cenc-src] all " + std::to_string(total) + " stream(s) ended -> MEEndOfPresentation");
        m_queue->QueueEventParamVar(MEEndOfPresentation, GUID_NULL, S_OK, nullptr);
    }
};

inline void CencMediaStream::NotifySourceEnded()
{
    if (m_source) static_cast<CencMediaSource*>(m_source)->NotifyStreamEnded(m_streamId);
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Factory: build the media type / stream descriptor / presentation descriptor and wire the demuxed samples.
// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
/// The video's own soundtrack, handed to <see cref="BuildCencSource"/> alongside the video. Absent (null) ⇒ video only.
struct CencAudioFeed
{
    cenc::InitInfo info;
    std::vector<cenc::Sample> samples;
};

/// Build the AAC media type for the protected pipeline. Two things here are easy to get silently wrong:
///  • MF wants the AAC configuration as MF_MT_USER_DATA in the HEAACWAVEINFO layout — the 12 bytes that FOLLOW the
///    WAVEFORMATEX part (payload type, profile-level, struct type, two reserved fields) and THEN the raw
///    AudioSpecificConfig. Handing it the bare ASC produces a type MF accepts and then fails to decode.
///  • payload type 0 means "raw AAC access units" (what fMP4 stores). Anything else describes ADTS/ADIF framing that
///    our samples do not have.
static winrt::com_ptr<IMFMediaType> BuildAacMediaType(const cenc::InitInfo& a)
{
    winrt::com_ptr<IMFMediaType> mt;
    winrt::check_hresult(MFCreateMediaType(mt.put()));
    mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    mt->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    mt->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, a.sampleRate ? a.sampleRate : 44100);
    mt->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, a.channels ? a.channels : 2);
    mt->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
    mt->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
    mt->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
    if (a.avgBitrate) mt->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, a.avgBitrate / 8);
    // AAC-LC unless the AudioSpecificConfig says otherwise; 0x29 is the AAC-LC profile-level indication MF expects.
    const uint16_t profileLevel = 0x29;
    mt->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0);
    mt->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, profileLevel);

    std::vector<uint8_t> userData(12, 0);
    userData[0] = 0; userData[1] = 0;                                   // wPayloadType = 0 (raw AAC)
    userData[2] = (uint8_t)(profileLevel & 0xFF); userData[3] = (uint8_t)(profileLevel >> 8);
    userData[4] = 0; userData[5] = 0;                                   // wStructType = 0 (AudioSpecificConfig follows)
    userData.insert(userData.end(), a.asc.begin(), a.asc.end());
    mt->SetBlob(MF_MT_USER_DATA, userData.data(), (UINT32)userData.size());
    return mt;
}

/// <param name="totalDuration100ns">The WHOLE track's duration when only a prefix has been demuxed so far. Required for a
/// streaming build: deriving the presentation duration from the samples in hand would declare the track to be as long as
/// the initial burst (~8s), and the media engine then treats every later position as past the end — the seek bar pins,
/// the clock overshoots ("0:36 / 0:08"), and Play() after a Pause does nothing because the presentation already ended.</param>
static winrt::com_ptr<CencMediaSource> BuildCencSource(const cenc::InitInfo& info, std::vector<cenc::Sample>&& samples,
                                                       CencAudioFeed* audio = nullptr, bool streaming = false,
                                                       uint64_t totalDuration100ns = 0)
{
    auto hx = [](HRESULT h) { std::stringstream ss; ss << "0x" << std::hex << (uint32_t)h; return ss.str(); };

    winrt::com_ptr<IMFMediaType> mt;
    winrt::check_hresult(MFCreateMediaType(mt.put()));
    mt->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    mt->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    MFSetAttributeSize(mt.get(), MF_MT_FRAME_SIZE, info.width, info.height);
    mt->SetUINT32(MF_MT_INTERLACE_MODE, 2 /*MFVideoInterlace_Progressive*/);
    if (!info.spspps.empty()) mt->SetBlob(MF_MT_MPEG_SEQUENCE_HEADER, info.spspps.data(), (UINT32)info.spspps.size());
    mt->SetUINT32(MF_MT_ORIGINAL_4CC, info.codec4cc ? info.codec4cc : cenc::fourcc("avc1"));
    // Protected-stream advertisement, the Firefox desktop-MFCDM way (gecko MFMediaEngineVideoStream::CreateMediaType +
    // MFMediaEngineStream::GenerateStreamDescriptor): WRAP the fully-populated clear H.264 type into a
    // MFMediaType_Protected envelope with MFWrapMediaType and set MF_SD_PROTECTED=1 on the stream descriptor. That is
    // what tells the media engine's modern EME pipeline "insert the CDM's decryptor before the decoder for this
    // stream"; without it the engine wires our encrypted samples STRAIGHT into the H.264 decoder, which rejects the
    // first ciphertext-bearing sample as MF_E_INVALIDREQUEST (decode error 3). NOTE this is NOT the earlier failed
    // experiment: stamping the raw MF_MT_PROTECTED *attribute* on an UNWRAPPED clear type (FG_CENC_MARK_SD_PROTECTED=1
    // diagnostics) selects the legacy ITA/OTA topology whose trust verification fails 0xC00D715B. The wrapped type is
    // unwrapped by the pipeline (MFUnwrapMediaType) after the decryptor, so the decoder still sees the real H.264 type.
    // Set FG_CENC_NO_PROTECTED_WRAP=1 to A/B the old clear-typed wiring.
    bool markProtected = info.encrypted && GetEnvironmentVariableW(L"FG_CENC_MARK_SD_PROTECTED", nullptr, 0) != 0;
    if (markProtected) mt->SetUINT32(FG_MF_MT_PROTECTED, TRUE);

    winrt::com_ptr<IMFMediaType> streamType = mt;
    bool wrapProtected = info.encrypted && !markProtected &&
                         GetEnvironmentVariableW(L"FG_CENC_NO_PROTECTED_WRAP", nullptr, 0) == 0;
    if (wrapProtected)
    {
        winrt::com_ptr<IMFMediaType> wrapped;
        HRESULT hrWrap = MFWrapMediaType(mt.get(), MFMediaType_Protected, MFVideoFormat_H264, wrapped.put());
        LogLine("[cenc-src] MFWrapMediaType(Protected) hr=" + hx(hrWrap));
        if (SUCCEEDED(hrWrap)) streamType = wrapped;
    }

    winrt::com_ptr<IMFStreamDescriptor> sd;
    IMFMediaType* mts[1] = { streamType.get() };
    winrt::check_hresult(MFCreateStreamDescriptor(1 /*streamId*/, 1, mts, sd.put()));
    if (markProtected || wrapProtected) sd->SetUINT32(MF_SD_PROTECTED, 1);
    {
        winrt::com_ptr<IMFMediaTypeHandler> mth;
        winrt::check_hresult(sd->GetMediaTypeHandler(mth.put()));
        winrt::check_hresult(mth->SetCurrentMediaType(streamType.get()));
    }

    // ── the audio stream (the video's own soundtrack), same protected envelope as the video ─────────────────────────
    winrt::com_ptr<IMFStreamDescriptor> audioSd;
    if (audio)
    {
        auto audioMt = BuildAacMediaType(audio->info);
        winrt::com_ptr<IMFMediaType> audioStreamType = audioMt;
        bool wrapAudio = audio->info.encrypted && !markProtected &&
                         GetEnvironmentVariableW(L"FG_CENC_NO_PROTECTED_WRAP", nullptr, 0) == 0;
        if (audio->info.encrypted && markProtected) audioMt->SetUINT32(FG_MF_MT_PROTECTED, TRUE);
        if (wrapAudio)
        {
            winrt::com_ptr<IMFMediaType> wrapped;
            HRESULT hrWrap = MFWrapMediaType(audioMt.get(), MFMediaType_Protected, MFAudioFormat_AAC, wrapped.put());
            LogLine("[cenc-src] MFWrapMediaType(Protected, AAC) hr=" + hx(hrWrap));
            if (SUCCEEDED(hrWrap)) audioStreamType = wrapped;
        }
        IMFMediaType* amts[1] = { audioStreamType.get() };
        winrt::check_hresult(MFCreateStreamDescriptor(2 /*streamId*/, 1, amts, audioSd.put()));
        if (audio->info.encrypted && (markProtected || wrapAudio)) audioSd->SetUINT32(MF_SD_PROTECTED, 1);
        winrt::com_ptr<IMFMediaTypeHandler> amth;
        winrt::check_hresult(audioSd->GetMediaTypeHandler(amth.put()));
        winrt::check_hresult(amth->SetCurrentMediaType(audioStreamType.get()));
    }

    winrt::com_ptr<IMFPresentationDescriptor> pd;
    IMFStreamDescriptor* sds[2] = { sd.get(), audioSd.get() };
    winrt::check_hresult(MFCreatePresentationDescriptor(audioSd ? 2 : 1, sds, pd.put()));
    pd->SelectStream(0);
    if (audioSd) pd->SelectStream(1);

    // Presentation duration = the longer of the two tracks. Reporting only the video's would cut the last audio samples
    // off (they routinely extend past the final frame), and reporting a partial prefix would make the seek bar lie while
    // the rest of the track is still being fetched — so a STREAMING build reports the caller-supplied total instead.
    auto endOf = [](const std::vector<cenc::Sample>& v, uint64_t timescale) -> uint64_t {
        uint64_t endTicks = 0;
        for (auto const& s : v) { uint64_t e = s.timeTicks + s.durTicks; if (e > endTicks) endTicks = e; }
        return timescale ? (endTicks * 10000000ULL) / timescale : 0;
    };
    uint64_t dur100ns = endOf(samples, info.timescale);
    if (audio) dur100ns = std::max<uint64_t>(dur100ns, endOf(audio->samples, audio->info.timescale));
    if (totalDuration100ns > dur100ns) dur100ns = totalDuration100ns;   // streaming: the WHOLE track, not the prefix
    if (dur100ns) pd->SetUINT64(MF_PD_DURATION, (UINT64)dur100ns);

    auto source = winrt::make_self<CencMediaSource>();
    auto stream = winrt::make_self<CencMediaStream>();
    stream->m_sd = sd;
    stream->m_source = (IMFMediaSource*)source.get();   // weak — source holds the strong ref
    stream->m_info = info;
    stream->m_samples = std::move(samples);
    stream->m_streamId = 1;
    stream->m_label = "video";
    stream->m_complete = !streaming;
    source->m_pd = pd;
    source->m_streams.push_back(stream);

    if (audio)
    {
        auto astream = winrt::make_self<CencMediaStream>();
        astream->m_sd = audioSd;
        astream->m_source = (IMFMediaSource*)source.get();
        astream->m_info = audio->info;
        astream->m_samples = std::move(audio->samples);
        astream->m_streamId = 2;
        astream->m_label = "audio";
        astream->m_complete = !streaming;
        source->m_streams.push_back(astream);
    }

    LogLine("[cenc-src] built source: " + std::to_string(info.width) + "x" + std::to_string(info.height) +
            " scheme=" + std::string(info.scheme == 1 ? "cbcs" : "cenc") +
            " ivSize=" + std::to_string((int)info.perSampleIvSize) +
            " spspps=" + std::to_string(info.spspps.size()) + "B samples=" + std::to_string(source->m_streams[0]->m_samples.size()) +
            (audio ? (" + AUDIO " + std::to_string(source->m_streams[1]->m_info.channels) + "ch/" +
                      std::to_string(source->m_streams[1]->m_info.sampleRate) + "Hz asc=" +
                      std::to_string(source->m_streams[1]->m_info.asc.size()) + "B samples=" +
                      std::to_string(source->m_streams[1]->m_samples.size()))
                    : std::string(" (no audio track)")) +
            (streaming ? "  [streaming: more segments arriving]" : ""));
    return source;
}

// Base64-encode raw bytes (crypt32).
static std::string CencBase64(const uint8_t* data, size_t n)
{
    DWORD cch = 0;
    CryptBinaryToStringA(data, (DWORD)n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, nullptr, &cch);
    std::string out(cch, '\0');
    if (cch) CryptBinaryToStringA(data, (DWORD)n, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, out.data(), &cch);
    while (!out.empty() && out.back() == '\0') out.pop_back();
    return out;
}

// Build a PlayReady 'pssh' box (system id 9A04F079-9840-4286-AB92-E65BE0885F95) wrapping a WRMHEADER for the given KID
// + license URL. Fallback init data for GenerateRequest when the DASH init segment carries no pssh box. The KID in the
// WRMHEADER VALUE is the GUID-ordered (mixed-endian) form of the big-endian tenc KID.
static std::vector<uint8_t> BuildPlayReadyPssh(const uint8_t kidBE[16], const std::wstring& laUrl)
{
    // tenc KID is big-endian; PlayReady KID VALUE uses GUID byte order (first 3 fields little-endian).
    uint8_t g[16];
    g[0] = kidBE[3]; g[1] = kidBE[2]; g[2] = kidBE[1]; g[3] = kidBE[0];
    g[4] = kidBE[5]; g[5] = kidBE[4];
    g[6] = kidBE[7]; g[7] = kidBE[6];
    memcpy(g + 8, kidBE + 8, 8);
    std::string kidB64 = CencBase64(g, 16);

    std::wstring xml = L"<WRMHEADER xmlns=\"http://schemas.microsoft.com/DRM/2007/03/PlayReadyHeader\" version=\"4.3.0.0\">"
                       L"<DATA><PROTECTINFO><KIDS><KID ALGID=\"AESCTR\" VALUE=\"" +
                       std::wstring(kidB64.begin(), kidB64.end()) + L"\"></KID></KIDS></PROTECTINFO>";
    if (!laUrl.empty()) xml += L"<LA_URL>" + laUrl + L"</LA_URL>";
    xml += L"</DATA></WRMHEADER>";

    // WRMHEADER stored UTF-16LE.
    const uint8_t* xmlBytes = (const uint8_t*)xml.data();
    uint32_t xmlLen = (uint32_t)(xml.size() * sizeof(wchar_t));

    // PlayReady Object: [u32 size][u16 count=1][u16 type=1][u16 length][WRMHEADER].
    std::vector<uint8_t> pro;
    auto put16le = [&](uint16_t v) { pro.push_back((uint8_t)(v & 0xFF)); pro.push_back((uint8_t)(v >> 8)); };
    auto put32le = [&](uint32_t v) { for (int i = 0; i < 4; i++) pro.push_back((uint8_t)((v >> (8 * i)) & 0xFF)); };
    uint32_t proSize = 4 + 2 + 2 + 2 + xmlLen;
    put32le(proSize); put16le(1); put16le(1); put16le((uint16_t)xmlLen);
    pro.insert(pro.end(), xmlBytes, xmlBytes + xmlLen);

    // pssh box (version 0): [u32 size]['pssh'][u32 version+flags=0][SystemID 16][u32 dataSize][PRO].
    static const uint8_t prSystemId[16] = { 0x9A,0x04,0xF0,0x79,0x98,0x40,0x42,0x86,0xAB,0x92,0xE6,0x5B,0xE0,0x88,0x5F,0x95 };
    std::vector<uint8_t> box;
    uint32_t boxSize = 8 + 4 + 16 + 4 + (uint32_t)pro.size();
    auto putBE32 = [&](uint32_t v) { box.push_back((uint8_t)(v >> 24)); box.push_back((uint8_t)(v >> 16)); box.push_back((uint8_t)(v >> 8)); box.push_back((uint8_t)v); };
    putBE32(boxSize);
    box.push_back('p'); box.push_back('s'); box.push_back('s'); box.push_back('h');
    putBE32(0);   // version+flags
    box.insert(box.end(), prSystemId, prSystemId + 16);
    putBE32((uint32_t)pro.size());
    box.insert(box.end(), pro.begin(), pro.end());
    return box;
}
