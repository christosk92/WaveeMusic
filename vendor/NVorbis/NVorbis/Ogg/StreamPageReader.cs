using NVorbis.Contracts.Ogg;
using System;
using System.Collections.Generic;

namespace NVorbis.Ogg
{
    class StreamPageReader : IStreamPageReader
    {
        private const int SeekCheckpointPageStride = 64;
        private const double ForwardJumpMinRatio = 1.75;
        private const long ForwardJumpMinByteAdvance = 128 * 1024;
        private const int ForwardJumpMaxAttempts = 3;

        internal static Func<IStreamPageReader, int, Contracts.IPacketProvider> CreatePacketProvider { get; set; } = (pr, ss) => new PacketProvider(pr, ss);

        private readonly IPageData _reader;
        private readonly List<long> _pageOffsets = new List<long>();
        private readonly List<long> _pageGranulePositions = new List<long>();
        private readonly List<int> _seekCheckpointPageIndices = new List<int>();
        private readonly List<long> _seekCheckpointGranulePositions = new List<long>();
        private readonly List<long> _seekCheckpointOffsets = new List<long>();

        private int _lastSeqNbr;
        private int? _firstDataPageIndex;
        private long _maxGranulePos;

        private int _lastPageIndex = -1;
        private long _lastPageGranulePos;
        private bool _lastPageIsResync;
        private bool _lastPageIsContinuation;
        private bool _lastPageIsContinued;
        private int _lastPagePacketCount;
        private int _lastPageOverhead;

        // When true, AddPage suppresses all state mutation. The byte-position
        // bisection in FindPageForwardByteBisection uses ProbeForwardPage to read
        // mid-file page headers without polluting _pageOffsets / _pageGranulePositions
        // (which FindPageBisection assumes are append-only and roughly monotonic).
        // The probe caller reads _reader.PageOffset / _reader.GranulePosition
        // directly after the underlying ReadNextPage returns.
        private bool _probingMode;

        // bytes-per-sample hint derived from the Vorbis ID header
        // (NominalBitrate / 8 / SampleRate). Set once by StreamDecoder via
        // PacketProvider.SetByteRateHint after header parsing. Used by the
        // forward bisection's first probe to seed near the target byte
        // instead of midpoint — saves ~1 cold CDN fetch per seek.
        internal double BytesPerSampleHint;

        private Memory<byte>[] _cachedPagePackets;

        public Contracts.IPacketProvider PacketProvider { get; private set; }

        public StreamPageReader(IPageData pageReader, int streamSerial)
        {
            _reader = pageReader;

            // The packet provider has a reference to us, and we have a reference to it.
            // The page reader has a reference to us.
            // The container reader has a _weak_ reference to the packet provider.
            // The user has a reference to the packet provider.
            // So long as the user doesn't drop their reference and the page reader doesn't drop us,
            //  the packet provider will stay alive.
            // This is important since the container reader only holds a week reference to it.
            PacketProvider = CreatePacketProvider(this, streamSerial);
        }

        public void AddPage()
        {
            // Probing mode (FindPageForwardByteBisection): the underlying ReadNextPage
            // already populated _reader.PageOffset / GranulePosition. Suppress all
            // bookkeeping so we don't pollute _pageOffsets with mid-file probes
            // (which would break FindPageBisection's monotonicity assumption) or
            // trigger the regression check when probing pages with smaller granules
            // than the current _maxGranulePos.
            if (_probingMode) return;

            // verify we haven't read all pages
            if (!HasAllPages)
            {
                // verify the new page's flags

                // if the page's granule position is 0 or less it doesn't have any sample
                if (_reader.GranulePosition != -1)
                {
                    if (_firstDataPageIndex == null && _reader.GranulePosition > 0)
                    {
                        _firstDataPageIndex = _pageOffsets.Count;
                    }
                    // Defensive: never regress _maxGranulePos. The stored value can be
                    // ahead of the current page's granule because a prior probe / forward
                    // jump landed further into the file. That's expected — just skip the
                    // update for this page (don't throw and don't move backward).
                    if (_reader.GranulePosition > _maxGranulePos)
                    {
                        _maxGranulePos = _reader.GranulePosition;
                    }
                }
                // granule position == -1, so this page doesn't complete any packets
                // we don't really care if it's a continuation itself, only that it is continued and has a single packet
                else if (_firstDataPageIndex.HasValue && (!_reader.IsContinued || _reader.PacketCount != 1))
                {
                    throw new System.IO.InvalidDataException("Granule Position was -1 but page does not have exactly 1 continued packet.");
                }

                if ((_reader.PageFlags & PageFlags.EndOfStream) != 0)
                {
                    HasAllPages = true;
                }

                if (_reader.IsResync.Value || (_lastSeqNbr != 0 && _lastSeqNbr + 1 != _reader.SequenceNumber))
                {
                    // as a practical matter, if the sequence numbers are "wrong", our logical stream is now out of sync
                    // so whether the page header sync was lost or we just got an out of order page / sequence jump, we're counting it as a resync
                    _pageOffsets.Add(-_reader.PageOffset);
                }
                else
                {
                    _pageOffsets.Add(_reader.PageOffset);
                }

                // Cache granule position for fast seeking
                _pageGranulePositions.Add(_reader.GranulePosition);

                // Maintain a sparse checkpoint index so forward seek can jump using
                // verified (already-read) byte/granule anchors.
                var pageIndex = _pageOffsets.Count - 1;
                TryAddSeekCheckpoint(pageIndex, _reader.GranulePosition, _reader.PageOffset);

                _lastSeqNbr = _reader.SequenceNumber;
            }
        }

        public Memory<byte>[] GetPagePackets(int pageIndex)
        {
            if (_cachedPagePackets != null && _lastPageIndex == pageIndex)
            {
                return _cachedPagePackets;
            }

            var pageOffset = _pageOffsets[pageIndex];
            if (pageOffset < 0)
            {
                pageOffset = -pageOffset;
            }

            _reader.Lock();
            try
            {
                _reader.ReadPageAt(pageOffset);
                var packets = _reader.GetPackets();
                if (pageIndex == _lastPageIndex)
                {
                    _cachedPagePackets = packets;
                }
                return packets;
            }
            finally
            {
                _reader.Release();
            }
        }

        public int FindPage(long granulePos)
        {
            var traceEnabled = NVorbisDiagnostics.IsEnabled;
            var traceStart = traceEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            // if we're being asked for the first granule, just grab the very first data page
            int pageIndex = -1;
            string branch = "?";
            if (granulePos == 0)
            {
                branch = "first";
                pageIndex = FindFirstDataPage();
            }
            else
            {
                // start by looking at the last read page's position...
                var lastPageIndex = _pageOffsets.Count - 1;
                if (GetPageRaw(lastPageIndex, out var pageGP))
                {
                    // most likely, we can look at previous pages for the appropriate one...
                    if (granulePos < pageGP)
                    {
                        branch = "bisection";
                        if (traceEnabled)
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.FindPage bisection target={granulePos} low_pageGP=? high_pageIdx={lastPageIndex} high_pageGP={pageGP}");
                        pageIndex = FindPageBisection(granulePos, FindFirstDataPage(), lastPageIndex, pageGP);
                    }
                    // forward seek: try byte-position bisection first (libvorbisfile-style ~5–10
                    // hops vs sequential's O(N) page walk). Falls through to FindPageForward when
                    // the file end isn't discoverable or bisection can't make progress.
                    else if (granulePos > pageGP)
                    {
                        var bisectResult = FindPageForwardByteBisection(lastPageIndex, pageGP, granulePos);
                        if (bisectResult >= 0)
                        {
                            branch = "forward-bisect";
                            pageIndex = bisectResult;
                        }
                        else
                        {
                            branch = "forward";
                            if (traceEnabled)
                                NVorbisDiagnostics.Log($"[seek-trace] SPR.FindPage forward (bisect unavailable) from pageIdx={lastPageIndex} pageGP={pageGP} target={granulePos}");
                            pageIndex = FindPageForward(lastPageIndex, pageGP, granulePos);
                        }
                    }
                    // but of course, it's possible (though highly unlikely) that the last read page ended on the granule we're looking for.
                    else
                    {
                        branch = "exact-last";
                        pageIndex = lastPageIndex + 1;
                    }
                }
            }
            if (pageIndex == -1)
            {
                throw new ArgumentOutOfRangeException(nameof(granulePos));
            }
            if (traceEnabled)
            {
                var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - traceStart) * 1000d
                         / System.Diagnostics.Stopwatch.Frequency;
                NVorbisDiagnostics.Log($"[seek-trace] SPR.FindPage END branch={branch} pageIdx={pageIndex} elapsed={ms:F1}ms");
            }
            return pageIndex;
        }

        private int FindFirstDataPage()
        {
            while (!_firstDataPageIndex.HasValue)
            {
                if (!GetPageRaw(_pageOffsets.Count, out _))
                {
                    return -1;
                }
            }
            return _firstDataPageIndex.Value;
        }

        private int FindPageForward(int pageIndex, long pageGranulePos, long granulePos)
        {
            var traceEnabled = NVorbisDiagnostics.IsEnabled;
            var traceStart = traceEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            var jumpAttempts = 0;
            var sequentialReads = 0;
            var startPageIndex = pageIndex;

            // Always scan forward sequentially from the last known page.
            // Estimation jumps can skip intermediate pages and create resync gaps,
            // which breaks packet-level granule continuity checks during seek.
            while (pageGranulePos <= granulePos)
            {
                if (jumpAttempts < ForwardJumpMaxAttempts &&
                    TryProbeForwardJump(pageIndex, pageGranulePos, granulePos, out var jumpedPageIndex, out var jumpedGranulePos))
                {
                    // Only keep the jump if it moved us forward and did not overshoot target.
                    // Overshoots near a sparse-resync boundary can reintroduce granule mismatch risk.
                    if (jumpedPageIndex > pageIndex && jumpedGranulePos > pageGranulePos && jumpedGranulePos <= granulePos)
                    {
                        if (traceEnabled)
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.forward jump_kept attempt={jumpAttempts + 1} from_pageIdx={pageIndex} -> {jumpedPageIndex} granule {pageGranulePos} -> {jumpedGranulePos}");
                        pageIndex = jumpedPageIndex;
                        pageGranulePos = jumpedGranulePos;
                        jumpAttempts++;
                        continue;
                    }
                    else if (traceEnabled)
                    {
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.forward jump_rejected attempt={jumpAttempts + 1} jumpedPageIdx={jumpedPageIndex} jumpedGP={jumpedGranulePos} (overshoot or no progress)");
                    }
                }

                if (++pageIndex == _pageOffsets.Count)
                {
                    if (!GetNextPageGranulePos(out pageGranulePos))
                    {
                        // if we couldn't get a page because we're EOS, allow finding the last granulePos
                        if (MaxGranulePosition < granulePos)
                        {
                            pageIndex = -1;
                        }
                        break;
                    }
                    sequentialReads++;
                }
                else
                {
                    long readStart = 0;
                    if (traceEnabled) readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!GetPageRaw(pageIndex, out pageGranulePos))
                    {
                        pageIndex = -1;
                        break;
                    }
                    sequentialReads++;
                    if (traceEnabled && (sequentialReads <= 3 || sequentialReads % 25 == 0))
                    {
                        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - readStart) * 1000d
                                 / System.Diagnostics.Stopwatch.Frequency;
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.forward seq_read#{sequentialReads} pageIdx={pageIndex} pageGP={pageGranulePos} elapsed={ms:F1}ms");
                    }
                }
            }
            if (traceEnabled)
            {
                var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - traceStart) * 1000d
                              / System.Diagnostics.Stopwatch.Frequency;
                NVorbisDiagnostics.Log($"[seek-trace] SPR.forward END from_pageIdx={startPageIndex} -> {pageIndex} jumps={jumpAttempts} seq_reads={sequentialReads} total={totalMs:F1}ms");
            }
            return pageIndex;
        }

        // libvorbisfile-style byte-position bisection for forward seeks. Replaces the
        // O(N) sequential page walk in FindPageForward with O(log N) interpolated probes.
        // Returns the resolved page index, or -1 if bisection can't be used (no stream
        // length, EOS during probe, or convergence failure → caller falls back to
        // FindPageForward and pays the original sequential cost). Probe reads suppress
        // all StreamPageReader bookkeeping (see _probingMode) so they don't violate
        // FindPageBisection's monotonicity assumption on _pageGranulePositions.
        private int FindPageForwardByteBisection(int currentPageIndex, long currentGranulePos, long targetGranulePos)
        {
            const int MaxHops = 16;
            const long MinBisectGap = 32 * 1024; // 32 KB — below this, finish sequentially
            const long HeadBoundaryGuardBytes = 16 * 1024;

            var traceEnabled = NVorbisDiagnostics.IsEnabled;
            var traceStart = traceEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            // Need a known file end. If StreamLength is 0 (non-seekable / unknown), bail.
            var streamLen = _reader.StreamLength;
            if (streamLen <= 0) return -1;

            var lowOffset = _pageOffsets[currentPageIndex];
            if (lowOffset < 0) lowOffset = -lowOffset;
            var originalLowOffset = lowOffset;
            var lowGP = currentGranulePos;
            var highOffset = streamLen;
            long highGP = -1; // unknown upper-anchor granule until first overshoot

            // Edge-case guard (a): pre-CDN window. LazyProgressiveDownloader.Length
            // returns just the head data size (~398 KB) until CDN init completes.
            // Bisection inside the head can't help — the target byte is past the
            // visible end. Bail so the caller's sequential walk handles the
            // CDN-init wait normally.
            if (highOffset - lowOffset < MinBisectGap) return -1;

            int resolvedPageIndex = -1;
            var hops = 0;
            var lastProbeOffset = -1L;
            var lastGap = highOffset - lowOffset;
            var stagnantHops = 0;

            while (highOffset - lowOffset > MinBisectGap && hops < MaxHops)
            {
                hops++;
                long probe;
                if (highGP < 0 && BytesPerSampleHint > 0 && stagnantHops < 1)
                {
                    // No upper-granule anchor yet — project forward from the current
                    // low anchor using Vorbis ID-header byte rate (Spotify Vorbis is
                    // ~CBR). Subsequent probes refine the anchor (lowOffset/lowGP get
                    // updated each undershoot) so the rate-based projection stays
                    // accurate even if the bitrate hint is slightly off. Lands near
                    // target byte in 1–2 hops vs midpoint's 4+ hops to a far-off
                    // overshoot first.
                    var bytesAhead = (long)((targetGranulePos - lowGP) * BytesPerSampleHint);
                    probe = lowOffset + Math.Max(MinBisectGap, bytesAhead);
                    if (traceEnabled && hops == 1)
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward seeded probe={probe} bytesPerSample={BytesPerSampleHint:F4}");
                }
                else if (stagnantHops >= 1 || highGP < 0)
                {
                    // Fallback midpoint when no hint and no upper anchor, or the
                    // interpolation has stopped shrinking the gap.
                    probe = lowOffset + (highOffset - lowOffset) / 2;
                }
                else
                {
                    var span = highGP - lowGP;
                    var frac = span > 0 ? (targetGranulePos - lowGP) / (double)span : 0.5;
                    if (frac < 0.05) frac = 0.05;
                    if (frac > 0.95) frac = 0.95;
                    probe = lowOffset + (long)((highOffset - lowOffset) * frac);
                }
                if (probe <= lowOffset) probe = lowOffset + 1;
                // Edge-case guard (b): keep the probe a head-boundary buffer away from
                // streamLen. Without this, a probe right at the head-data edge would
                // make ReadNextPage scan across into CDN territory and synchronously
                // trigger CDN init from inside the bisection probe (blocking the
                // playback loop for the full handshake).
                var clampHigh = highOffset - 1;
                if (highOffset == streamLen && clampHigh > HeadBoundaryGuardBytes)
                    clampHigh = streamLen - HeadBoundaryGuardBytes;
                if (probe >= clampHigh) probe = clampHigh;

                if (probe == lastProbeOffset)
                {
                    probe = lowOffset + (highOffset - lowOffset) / 2;
                    if (probe == lastProbeOffset) break;
                }
                lastProbeOffset = probe;

                if (!ProbeForwardPage(probe, out var probePageOffset, out var probeGP))
                {
                    if (traceEnabled)
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward hop={hops} probe={probe} probe-failed (eos or no-page)");
                    return -1;
                }

                if (traceEnabled)
                    NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward hop={hops} probe={probe} pageOffset={probePageOffset} pageGP={probeGP} target={targetGranulePos}");

                if (probeGP < targetGranulePos)
                {
                    // Only accept if it actually moves low FORWARD AND stays inside
                    // the upper bracket. Mysterious "outside-bracket" probes (Phase 4
                    // Issue B) would otherwise expand the search range and trigger
                    // 12+ hops of oscillation.
                    if (probePageOffset > lowOffset && probePageOffset < highOffset)
                    {
                        lowOffset = probePageOffset;
                        lowGP = probeGP;
                    }
                    else
                    {
                        if (traceEnabled)
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward hop={hops} REJECT (outside-bracket low) probePageOffset={probePageOffset} bracket=[{lowOffset}-{highOffset}]");
                        stagnantHops++;
                    }
                }
                else if (probeGP > targetGranulePos)
                {
                    if (probePageOffset < highOffset && probePageOffset > lowOffset)
                    {
                        highOffset = probePageOffset;
                        highGP = probeGP;
                    }
                    else
                    {
                        if (traceEnabled)
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward hop={hops} REJECT (outside-bracket high) probePageOffset={probePageOffset} bracket=[{lowOffset}-{highOffset}]");
                        stagnantHops++;
                    }
                }
                else
                {
                    // Exact granule hit — but the page isn't in _pageOffsets yet (probe
                    // suppressed the add). Materialize it via the regular path and use
                    // the resulting index.
                    resolvedPageIndex = MaterializePageAt(probePageOffset);
                    if (resolvedPageIndex >= 0) resolvedPageIndex += 1; // FindPage convention
                    break;
                }

                var newGap = highOffset - lowOffset;
                if (newGap >= lastGap) stagnantHops++; else stagnantHops = 0;
                lastGap = newGap;

                // Bail to materialize+sequential after consistent no-progress. The
                // tail walks from the converged low anchor through cached bytes
                // (~1–3 page reads, ~30 ms).
                if (stagnantHops >= 4)
                {
                    if (traceEnabled)
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward bail (stagnant={stagnantHops})");
                    break;
                }
            }

            if (resolvedPageIndex < 0)
            {
                if (lowOffset == originalLowOffset)
                {
                    // Bisection made no progress (e.g., target was within MinBisectGap).
                    // Don't re-materialize the original page (that would duplicate it
                    // in _pageOffsets); just sequential-walk from the original index.
                    resolvedPageIndex = FindPageForward(currentPageIndex, currentGranulePos, targetGranulePos);
                }
                else
                {
                    // Converged to a new low-anchor byte position. Materialize that page
                    // (so _pageOffsets has a real index for it) then walk forward
                    // sequentially — typically 1–3 page reads against cached bytes.
                    var anchorIdx = MaterializePageAt(lowOffset);
                    if (anchorIdx < 0)
                    {
                        // Couldn't add the low anchor — fall back to the original
                        // sequential walk from the original starting page.
                        return -1;
                    }
                    resolvedPageIndex = FindPageForward(anchorIdx, lowGP, targetGranulePos);
                }
            }

            if (traceEnabled)
            {
                var totalMs = (System.Diagnostics.Stopwatch.GetTimestamp() - traceStart) * 1000d
                              / System.Diagnostics.Stopwatch.Frequency;
                NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect-forward END pageIdx={resolvedPageIndex} hops={hops} stagnant={stagnantHops} total={totalMs:F1}ms");
            }
            return resolvedPageIndex;
        }

        // Reads the next page header from probeByteOffset without mutating _pageOffsets,
        // _pageGranulePositions, _maxGranulePos, or _seekCheckpoints. Returns the actual
        // discovered page's byte offset and granule position via out params.
        private bool ProbeForwardPage(long probeByteOffset, out long pageOffset, out long granulePos)
        {
            pageOffset = -1;
            granulePos = -1;

            _reader.Lock();
            try
            {
                // Diagnostic: peek a wide window at the probe target and scan for OggS
                // sync (4F 67 67 53). Then compare against where the actual ReadNextPage
                // ends up landing. If OggS appears at offset+N within the peek but
                // ReadNextPage finds OggS much further along, the scan logic in
                // PageReaderBase.ReadNextPage is skipping known sync bytes (probable
                // root cause of the "scan-skip" bug).
                if (NVorbisDiagnostics.IsEnabled)
                {
                    var peek = new byte[512];
                    var n = _reader.PeekRawAt(probeByteOffset, peek, 0, peek.Length);
                    if (n > 0)
                    {
                        int oggsPos = -1;
                        for (int i = 0; i <= n - 4; i++)
                        {
                            if (peek[i] == 0x4F && peek[i + 1] == 0x67 && peek[i + 2] == 0x67 && peek[i + 3] == 0x53)
                            {
                                oggsPos = i;
                                break;
                            }
                        }
                        if (oggsPos >= 0)
                        {
                            // Found OggS in the raw peek — log byte position relative to probe.
                            // The actual ReadNextPage should find a page at probeByteOffset + oggsPos.
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.probe-peek offset={probeByteOffset} OggS@+{oggsPos} (within {n}B)");
                        }
                        else
                        {
                            var hex = System.Convert.ToHexString(peek, 0, Math.Min(32, n));
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.probe-peek offset={probeByteOffset} no-OggS in {n}B (first32B={hex})");
                        }
                    }
                }

                _reader.SeekForNextPage(probeByteOffset);
                _probingMode = true;
                try
                {
                    if (!_reader.ReadNextPage()) return false;
                }
                finally
                {
                    _probingMode = false;
                }

                pageOffset = _reader.PageOffset;
                granulePos = _reader.GranulePosition;
            }
            finally
            {
                _reader.Release();
            }
            return granulePos >= 0;
        }

        // Adds the page at exactPageOffset to _pageOffsets via the normal AddPage path
        // and returns its index. Used by the bisection's tail to materialize the
        // converged-low-anchor page so FindPageForward / FindPacket can use it.
        private int MaterializePageAt(long exactPageOffset)
        {
            _reader.SeekForNextPage(exactPageOffset);
            var oldCount = _pageOffsets.Count;
            if (!GetNextPageGranulePos(out _)) return -1;
            if (_pageOffsets.Count <= oldCount) return -1;
            return _pageOffsets.Count - 1;
        }

        private void TryAddSeekCheckpoint(int pageIndex, long granulePos, long pageOffset)
        {
            if (granulePos < 0 || pageOffset < 0)
            {
                return;
            }

            if (_seekCheckpointPageIndices.Count == 0)
            {
                _seekCheckpointPageIndices.Add(pageIndex);
                _seekCheckpointGranulePositions.Add(granulePos);
                _seekCheckpointOffsets.Add(pageOffset);
                return;
            }

            var lastCheckpointPageIndex = _seekCheckpointPageIndices[_seekCheckpointPageIndices.Count - 1];
            if (pageIndex - lastCheckpointPageIndex < SeekCheckpointPageStride)
            {
                return;
            }

            _seekCheckpointPageIndices.Add(pageIndex);
            _seekCheckpointGranulePositions.Add(granulePos);
            _seekCheckpointOffsets.Add(pageOffset);
        }

        private bool TryProbeForwardJump(int pageIndex, long pageGranulePos, long targetGranulePos, out int jumpedPageIndex, out long jumpedGranulePos)
        {
            jumpedPageIndex = -1;
            jumpedGranulePos = 0;

            if (pageGranulePos <= 0 || targetGranulePos <= pageGranulePos)
            {
                return false;
            }

            var ratio = targetGranulePos / (double)pageGranulePos;
            if (ratio < ForwardJumpMinRatio)
            {
                return false;
            }

            // Find the latest checkpoint at or before our current page index.
            var checkpointListIndex = _seekCheckpointPageIndices.Count - 1;
            while (checkpointListIndex >= 0 && _seekCheckpointPageIndices[checkpointListIndex] > pageIndex)
            {
                checkpointListIndex--;
            }

            if (checkpointListIndex < 0)
            {
                return false;
            }

            var checkpointGranulePos = _seekCheckpointGranulePositions[checkpointListIndex];
            var checkpointOffset = _seekCheckpointOffsets[checkpointListIndex];
            var currentOffset = _pageOffsets[pageIndex];
            if (currentOffset < 0)
            {
                currentOffset = -currentOffset;
            }

            var granuleDelta = pageGranulePos - checkpointGranulePos;
            var offsetDelta = currentOffset - checkpointOffset;
            if (granuleDelta <= 0 || offsetDelta <= 0)
            {
                return false;
            }

            var targetDelta = targetGranulePos - pageGranulePos;
            var estimatedAdvance = (long)Math.Ceiling(targetDelta * (offsetDelta / (double)granuleDelta));

            // Stay intentionally behind the target to avoid overshooting into a sparse gap.
            estimatedAdvance = (estimatedAdvance * 3) / 4;

            if (estimatedAdvance < ForwardJumpMinByteAdvance)
            {
                return false;
            }

            var jumpOffset = currentOffset + estimatedAdvance;
            _reader.SeekForNextPage(jumpOffset);

            var oldPageCount = _pageOffsets.Count;
            if (!GetNextPageGranulePos(out var probeGranulePos))
            {
                return false;
            }

            // If no new page was added, this probe is unusable.
            if (_pageOffsets.Count <= oldPageCount)
            {
                return false;
            }

            jumpedPageIndex = oldPageCount;
            jumpedGranulePos = probeGranulePos;
            return true;
        }

        private bool GetNextPageGranulePos(out long granulePos)
        {
            var pageCount = _pageOffsets.Count;
            while (pageCount == _pageOffsets.Count && !HasAllPages)
            {
                _reader.Lock();
                try
                {
                    if (!_reader.ReadNextPage())
                    {
                        HasAllPages = true;
                        continue;
                    }

                    if (pageCount < _pageOffsets.Count)
                    {
                        granulePos = _reader.GranulePosition;
                        return true;
                    }
                }
                finally
                {
                    _reader.Release();
                }
            }
            granulePos = 0;
            return false;
        }

        private int FindPageBisection(long granulePos, int low, int high, long highGranulePos)
        {
            var traceEnabled = NVorbisDiagnostics.IsEnabled;
            var hop = 0;
            var ioHops = 0;
            var cachedHops = 0;
            // we can treat low as always being before the first sample; later work will correct that if needed
            var lowGranulePos = 0L;
            int dist;
            while ((dist = high - low) > 0)
            {
                hop++;
                // try to find the right page by assumming they are all about the same size
                var index = low + (int)(dist * ((granulePos - lowGranulePos) / (double)(highGranulePos - lowGranulePos)));

                // Use cached granule position if available (O(1) lookup instead of reading from stream)
                long idxGranulePos;
                bool cached;
                long readStart = 0;
                if (index < _pageGranulePositions.Count)
                {
                    cached = true;
                    cachedHops++;
                    idxGranulePos = _pageGranulePositions[index];
                }
                else
                {
                    cached = false;
                    ioHops++;
                    if (traceEnabled) readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (!GetPageRaw(index, out idxGranulePos))
                    {
                        if (traceEnabled)
                            NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect hop={hop} pageIdx={index} GetPageRaw returned false");
                        return -1;
                    }
                    if (traceEnabled)
                    {
                        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - readStart) * 1000d
                                 / System.Diagnostics.Stopwatch.Frequency;
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect hop={hop} pageIdx={index} cached=false read_elapsed={ms:F1}ms");
                    }
                }
                if (traceEnabled && cached)
                {
                    NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect hop={hop} pageIdx={index} cached=true");
                }

                // figure out where to go from here
                if (idxGranulePos > granulePos)
                {
                    // we read a page after our target (could be the right one, but we don't know yet)
                    high = index;
                    highGranulePos = idxGranulePos;
                }
                else if (idxGranulePos < granulePos)
                {
                    // we read a page before our target
                    low = index + 1;
                    lowGranulePos = idxGranulePos + 1;
                }
                else
                {
                    // direct hit
                    if (traceEnabled)
                        NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect END hops={hop} ioHops={ioHops} cachedHops={cachedHops} (direct hit)");
                    return index + 1;
                }
            }
            if (traceEnabled)
                NVorbisDiagnostics.Log($"[seek-trace] SPR.bisect END hops={hop} ioHops={ioHops} cachedHops={cachedHops}");
            return low;
        }

        private bool GetPageRaw(int pageIndex, out long pageGranulePos)
        {
            var offset = _pageOffsets[pageIndex];
            if (offset < 0)
            {
                offset = -offset;
            }

            _reader.Lock();
            try
            {
                if (_reader.ReadPageAt(offset))
                {
                    pageGranulePos = _reader.GranulePosition;
                    return true;
                }
                pageGranulePos = 0;
                return false;
            }
            finally
            {
                _reader.Release();
            }
        }

        public bool GetPage(int pageIndex, out long granulePos, out bool isResync, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
        {
            if (_lastPageIndex == pageIndex)
            {
                granulePos = _lastPageGranulePos;
                isResync = _lastPageIsResync;
                isContinuation = _lastPageIsContinuation;
                isContinued = _lastPageIsContinued;
                packetCount = _lastPagePacketCount;
                pageOverhead = _lastPageOverhead;
                return true;
            }

            _reader.Lock();
            try
            {
                while (pageIndex >= _pageOffsets.Count && !HasAllPages)
                {
                    if (_reader.ReadNextPage())
                    {
                        // if we found our page, return it from here so we don't have to do further processing
                        if (pageIndex < _pageOffsets.Count)
                        {
                            isResync = _reader.IsResync.Value;
                            ReadPageData(pageIndex, out granulePos, out isContinuation, out isContinued, out packetCount, out pageOverhead);
                            return true;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                _reader.Release();
            }

            if (pageIndex < _pageOffsets.Count)
            {
                var offset = _pageOffsets[pageIndex];
                if (offset < 0)
                {
                    isResync = true;
                    offset = -offset;
                }
                else
                {
                    isResync = false;
                }

                _reader.Lock();
                try
                {
                    if (_reader.ReadPageAt(offset))
                    {
                        _lastPageIsResync = isResync;
                        ReadPageData(pageIndex, out granulePos, out isContinuation, out isContinued, out packetCount, out pageOverhead);
                        return true;
                    }
                }
                finally
                {
                    _reader.Release();
                }
            }

            granulePos = 0;
            isResync = false;
            isContinuation = false;
            isContinued = false;
            packetCount = 0;
            pageOverhead = 0;
            return false;
        }

        private void ReadPageData(int pageIndex, out long granulePos, out bool isContinuation, out bool isContinued, out int packetCount, out int pageOverhead)
        {
            _cachedPagePackets = null;
            _lastPageGranulePos = granulePos = _reader.GranulePosition;
            _lastPageIsContinuation = isContinuation = (_reader.PageFlags & PageFlags.ContinuesPacket) != 0;
            _lastPageIsContinued = isContinued = _reader.IsContinued;
            _lastPagePacketCount = packetCount = _reader.PacketCount;
            _lastPageOverhead = pageOverhead = _reader.PageOverhead;
            _lastPageIndex = pageIndex;
        }

        public void SetEndOfStream()
        {
            HasAllPages = true;
        }

        public int PageCount => _pageOffsets.Count;

        public bool HasAllPages { get; private set; }

        public long? MaxGranulePosition => HasAllPages ? (long?)_maxGranulePos : null;

        public int FirstDataPageIndex => FindFirstDataPage();
    }
}
