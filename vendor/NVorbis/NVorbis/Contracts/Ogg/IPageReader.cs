using System;

namespace NVorbis.Contracts.Ogg
{
    interface IPageReader : IDisposable
    {
        void Lock();
        bool Release();

        long ContainerBits { get; }
        long WasteBits { get; }

        /// <summary>
        /// Length of the underlying stream in bytes, used as the upper anchor for
        /// byte-position bisection in forward seeks. Returns 0 if the stream
        /// doesn't report a length (non-seekable / network without Content-Length).
        /// </summary>
        long StreamLength { get; }

        /// <summary>
        /// Diagnostic helper: reads <paramref name="count"/> raw bytes from the
        /// underlying stream at <paramref name="offset"/> WITHOUT scanning for OggS
        /// or mutating any internal scan state. Restores the stream position
        /// before returning. Returns the number of bytes actually read. Used by
        /// StreamPageReader's bisection to log what the probe target actually
        /// contains when behavior diverges from expectations.
        /// </summary>
        int PeekRawAt(long offset, byte[] buffer, int bufferOffset, int count);

        bool ReadNextPage();

        bool ReadPageAt(long offset);

        void SeekForNextPage(long offset);
    }
}
