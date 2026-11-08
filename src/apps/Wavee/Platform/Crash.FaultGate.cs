// ── Platform/Crash.FaultGate.cs ────────────────────────────────────────────────────────────────────────────────────
// The native fault hook's FRONT DOOR: a few dozen bytes of machine code registered as the vectored handler and the
// unhandled-exception filter, so the first instructions that run after a hardware fault are never managed code.
//
// WHY NOT AN [UnmanagedCallersOnly] HANDLER. A NativeAOT reverse P/Invoke entered on a thread that is already in managed
// (cooperative) mode is a fatal error: the runtime fail-fasts with 0xC0000602 "attempted to call a UnmanagedCallersOnly
// method from managed code". A hardware fault in the runtime or the GC (inside Wavee.exe), in a SuppressGCTransition
// call, or in any native code reached without a GC transition, arrives on exactly such a thread. The old C# VEH therefore
// turned those faults into 0xC0000602 with no frames and no dump, even the in-image ones it would have ignored anyway
// (proved by lane FAIL_FAST with a scratch AOT probe; `docs/guide/crash-diagnostics.md` §1's exit-code table).
//
// WHAT THE GATE DOES, in native code, on the faulting thread:
//   1. not one of the five fault codes the hook watches, or the address is inside Wavee.exe → "continue search" at once
//      (the runtime, a driver's own __try, or Windows' default handling decide, and the exit code stays the real one);
//   2. a foreign fault: take the once-per-process latch (lock cmpxchg / ldaxr-stlxr), store the code, address,
//      EXCEPTION_POINTERS and the OS thread id into a data page, SetEvent the reporter, and WaitForSingleObject until it
//      answers (bounded), then "continue search" (the VEH) or "execute handler" (the filter: the process ends with the
//      fault's own code, as before).
// The REPORTER is an ordinary managed thread created at install and parked in WaitForSingleObject (preemptive mode), so
// it can run managed code no matter what state the faulting thread is in. It walks the faulting thread's stack from the
// fault's own CONTEXT (RtlLookupFunctionEntry / RtlVirtualUnwind, no handler frames to skip) and hands everything to
// `Crash.NativeHook`. Allocation on the reporter cannot deadlock on the faulting thread's heap lock forever: the gate's
// wait is bounded (`WaitMs`).
//
// Self-contained on purpose (System only): the scratch AOT probe that proves the fix compiles this file as is.
// x64 and ARM64; any other architecture answers false from `TryInstall` and the caller keeps its managed handlers.

using System.Runtime.InteropServices;

namespace Wavee;

public static partial class Crash
{
    public static unsafe partial class FaultGate
    {
        /// <summary>What the gate saw. <see cref="Frames"/> is the faulting thread's stack walked from the fault's context,
        /// innermost first (the first entry is the faulting instruction itself).</summary>
        public readonly record struct Fault(uint Code, nint Address, nint Pointers, uint ThreadId, nint[] Frames);

        /// <summary>How long the faulting thread waits for the reporter (dump included) before the fault goes on its way.</summary>
        public const uint WaitMs = 30_000;

        public const int MaxFrames = 62;

        // Data page layout (shared with the machine code below; never reorder):
        const int OffImageLo = 0, OffImageHi = 8, OffLatch = 16, OffTid = 20, OffCode = 36, OffAddress = 40,
            OffPointers = 48, OffRequest = 56, OffDone = 64, OffSetEvent = 80, OffWait = 88, OffGetTid = 96,
            OffWaitMs = 104, OffCodes = 112;
        const int FilterThunkOffset = 0x200, PageBytes = 0x1000;

        static readonly uint[] s_codes = [0xC0000005, 0xC000001D, 0xC0000006, 0xC000008C, 0xC0000094];

        static byte* s_data;
        static nint s_veh, s_filter;
        static Action<Fault>? s_onFault;
        static int s_installed;

        /// <summary>The gate's two entry points, for registration: the vectored handler (answers "continue search") and
        /// the unhandled filter (answers "execute handler" for a foreign fault). 0 before <see cref="TryInstall"/>.</summary>
        public static nint VectoredHandler => s_veh;
        public static nint UnhandledFilter => s_filter;

        /// <summary>Builds the gate for faults OUTSIDE [<paramref name="imageBase"/>, +<paramref name="imageSize"/>), starts
        /// the reporter thread and registers both entry points (the VEH last in the chain, like the old hook). False on an
        /// architecture the gate has no code for, or when the executable page cannot be made.</summary>
        public static bool TryInstall(nint imageBase, nuint imageSize, Action<Fault> onFault)
        {
            if (Interlocked.Exchange(ref s_installed, 1) != 0) return s_veh != 0;
            byte[] veh, filter;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64: veh = X64(0, PageBytes); filter = X64(1, PageBytes - FilterThunkOffset); break;
                case Architecture.Arm64: veh = Arm64(0, PageBytes); filter = Arm64(1, PageBytes - FilterThunkOffset); break;
                default: return false;
            }
            byte* page = (byte*)VirtualAlloc(0, 2 * PageBytes, MemCommit | MemReserve, PageReadWrite);
            if (page == null) return false;
            byte* data = page + PageBytes;
            *(nint*)(data + OffImageLo) = imageBase;
            *(nint*)(data + OffImageHi) = unchecked(imageBase + (nint)imageSize);
            *(nint*)(data + OffRequest) = CreateEventW(0, 0, 0, 0);
            *(nint*)(data + OffDone) = CreateEventW(0, 0, 0, 0);
            nint kernel32 = NativeLibrary.Load("kernel32.dll");
            *(nint*)(data + OffSetEvent) = NativeLibrary.GetExport(kernel32, "SetEvent");
            *(nint*)(data + OffWait) = NativeLibrary.GetExport(kernel32, "WaitForSingleObject");
            *(nint*)(data + OffGetTid) = NativeLibrary.GetExport(kernel32, "GetCurrentThreadId");
            *(uint*)(data + OffWaitMs) = WaitMs;
            for (int i = 0; i < s_codes.Length; i++) *(uint*)(data + OffCodes + 4 * i) = s_codes[i];
            if (*(nint*)(data + OffRequest) == 0 || *(nint*)(data + OffDone) == 0) return false;

            new ReadOnlySpan<byte>(veh).CopyTo(new Span<byte>(page, FilterThunkOffset));
            new ReadOnlySpan<byte>(filter).CopyTo(new Span<byte>(page + FilterThunkOffset, PageBytes - FilterThunkOffset));
            if (!VirtualProtect((nint)page, PageBytes, PageExecuteRead, out _)) return false;
            FlushInstructionCache(GetCurrentProcess(), (nint)page, PageBytes);

            s_data = data;
            s_onFault = onFault;
            s_veh = (nint)page;
            s_filter = (nint)(page + FilterThunkOffset);
            new Thread(Reporter) { IsBackground = true, Name = "crash-fault-gate" }.Start();
            SetUnhandledExceptionFilter(s_filter);
            AddVectoredExceptionHandler(0 /* last: every other handler (the runtime's first) sees the fault before us */, s_veh);
            return true;
        }

        static void Reporter()
        {
            byte* d = s_data;
            while (true)
            {
                if (WaitForSingleObject(*(nint*)(d + OffRequest), 0xFFFFFFFF) != 0) return;
                try
                {
                    nint pointers = *(nint*)(d + OffPointers);
                    var fault = new Fault(*(uint*)(d + OffCode), *(nint*)(d + OffAddress), pointers, *(uint*)(d + OffTid),
                        WalkFromContext(pointers));
                    s_onFault?.Invoke(fault);
                }
                catch (Exception) { }
                finally { SetEvent(*(nint*)(d + OffDone)); }
            }
        }

        // ── the faulting thread's stack, from the fault context ───────────────────────────────────────────────────

        /// <summary>Unwinds a COPY of the fault's CONTEXT with the OS unwinder: the first frame is the faulting instruction,
        /// then each caller's return address. A frame without unwind data (a leaf) returns through [sp] on x64 (only when
        /// that stack page is committed and readable) or through LR on ARM64.</summary>
        static nint[] WalkFromContext(nint exceptionPointers)
        {
            if (exceptionPointers == 0) return [];
            nint contextRecord = ((nint*)exceptionPointers)[1];
            if (contextRecord == 0) return [];
            bool x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            int size = x64 ? 0x4D0 : 0x390;
            int offPc = x64 ? 0xF8 : 0x108, offSp = x64 ? 0x98 : 0x100, offLr = 0xF8;
            byte* ctx = (byte*)NativeMemory.AlignedAlloc(0x1000, 16);
            try
            {
                Buffer.MemoryCopy((void*)contextRecord, ctx, size, size);
                var frames = new List<nint>(MaxFrames);
                for (int n = 0; n < MaxFrames; n++)
                {
                    ulong pc = *(ulong*)(ctx + offPc), sp = *(ulong*)(ctx + offSp);
                    if (pc == 0) break;
                    frames.Add((nint)pc);
                    ulong imageBase;
                    void* entry = RtlLookupFunctionEntry(pc, &imageBase, null);
                    if (entry != null)
                    {
                        void* handlerData;
                        ulong establisher;
                        RtlVirtualUnwind(0 /* UNW_FLAG_NHANDLER */, imageBase, pc, entry, ctx, &handlerData, &establisher, null);
                    }
                    else if (x64)
                    {
                        if (!Readable(sp)) break;
                        *(ulong*)(ctx + offPc) = *(ulong*)sp;
                        *(ulong*)(ctx + offSp) = sp + 8;
                    }
                    else
                    {
                        ulong lr = *(ulong*)(ctx + offLr);
                        if (lr == pc) break;
                        *(ulong*)(ctx + offPc) = lr;
                    }
                    if (*(ulong*)(ctx + offPc) == pc && *(ulong*)(ctx + offSp) == sp) break;   // no progress
                }
                return frames.ToArray();
            }
            catch (Exception) { return []; }
            finally { NativeMemory.AlignedFree(ctx); }
        }

        static bool Readable(ulong address)
        {
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery((nint)address, &mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return false;
            const uint Readables = 0x02 | 0x04 | 0x20 | 0x40;   // READONLY, READWRITE, EXECUTE_READ, EXECUTE_READWRITE
            return mbi.State == MemCommit && (mbi.Protect & Readables) != 0 && (mbi.Protect & 0x100 /* GUARD */) == 0;
        }

        // ── the machine code ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>x64. <paramref name="result"/> is what a foreign fault answers (0 = continue search, 1 = execute handler);
        /// <paramref name="dataDistance"/> is the data page's offset from this thunk's first byte.</summary>
        static byte[] X64(int result, int dataDistance)
        {
            var a = new Asm();
            a.Bytes(0x53, 0x56, 0x48, 0x83, 0xEC, 0x28);              // push rbx; push rsi; sub rsp,28h
            a.Bytes(0x48, 0x8D, 0x1D); a.Rel32To(dataDistance);       // lea rbx,[rip+data]
            a.Bytes(0x48, 0x89, 0xCE);                                // mov rsi,rcx            (EXCEPTION_POINTERS*)
            a.Bytes(0x48, 0x8B, 0x01);                                // mov rax,[rcx]          (ExceptionRecord)
            a.Bytes(0x48, 0x85, 0xC0);                                // test rax,rax
            a.Jcc(0x84, "ret0");                                      // jz ret0
            a.Bytes(0x8B, 0x10);                                      // mov edx,[rax]          (ExceptionCode)
            foreach (uint code in s_codes)
            {
                a.Bytes(0x81, 0xFA); a.U32(code);                     // cmp edx,code
                a.Jcc(0x84, "codeok");                                // je codeok
            }
            a.Jmp("ret0");
            a.Label("codeok");
            a.Bytes(0x48, 0x8B, 0x48, 0x10);                          // mov rcx,[rax+10h]      (ExceptionAddress)
            a.Bytes(0x48, 0x3B, 0x0B);                                // cmp rcx,[rbx]          (image lo)
            a.Jcc(0x82, "foreign");                                   // jb foreign
            a.Bytes(0x48, 0x3B, 0x4B, 0x08);                          // cmp rcx,[rbx+8]        (image hi)
            a.Jcc(0x82, "ret0");                                      // jb ret0                (inside Wavee.exe)
            a.Label("foreign");
            a.Bytes(0x31, 0xC0);                                      // xor eax,eax
            a.Bytes(0x41, 0xB8, 0x01, 0x00, 0x00, 0x00);              // mov r8d,1
            a.Bytes(0xF0, 0x44, 0x0F, 0xB1, 0x43, OffLatch);          // lock cmpxchg [rbx+latch],r8d
            a.Jcc(0x85, "retR");                                      // jnz retR               (already reported)
            a.Bytes(0x48, 0x89, 0x4B, OffAddress);                    // mov [rbx+address],rcx
            a.Bytes(0x89, 0x53, OffCode);                             // mov [rbx+code],edx
            a.Bytes(0x48, 0x89, 0x73, OffPointers);                   // mov [rbx+pointers],rsi
            a.Bytes(0xFF, 0x53, OffGetTid);                           // call [rbx+GetCurrentThreadId]
            a.Bytes(0x89, 0x43, OffTid);                              // mov [rbx+tid],eax
            a.Bytes(0x48, 0x8B, 0x4B, OffRequest);                    // mov rcx,[rbx+request]
            a.Bytes(0xFF, 0x53, OffSetEvent);                         // call [rbx+SetEvent]
            a.Bytes(0x48, 0x8B, 0x4B, OffDone);                       // mov rcx,[rbx+done]
            a.Bytes(0x8B, 0x53, OffWaitMs);                           // mov edx,[rbx+waitMs]
            a.Bytes(0xFF, 0x53, OffWait);                             // call [rbx+WaitForSingleObject]
            a.Label("retR");
            a.Bytes(0xB8); a.U32((uint)result);                       // mov eax,result
            a.Jmp("done");
            a.Label("ret0");
            a.Bytes(0x31, 0xC0);                                      // xor eax,eax
            a.Label("done");
            a.Bytes(0x48, 0x83, 0xC4, 0x28, 0x5E, 0x5B, 0xC3);        // add rsp,28h; pop rsi; pop rbx; ret
            return a.Finish(x64: true);
        }

        /// <summary>ARM64, same contract as <see cref="X64"/>.</summary>
        static byte[] Arm64(int result, int dataDistance)
        {
            var a = new Asm();
            a.I(0xA9BE7BFD);                                          // stp x29,x30,[sp,#-32]!
            a.I(0x910003FD);                                          // mov x29,sp
            a.I(0xA90153F3);                                          // stp x19,x20,[sp,#16]
            a.Adr(19, dataDistance);                                  // adr x19,data
            a.I(0xAA0003F4);                                          // mov x20,x0             (EXCEPTION_POINTERS*)
            a.I(LdrX(8, 0, 0));                                       // ldr x8,[x0]            (ExceptionRecord)
            a.Cb(0xB4000000, 8, "ret0");                              // cbz x8,ret0
            a.I(LdrW(9, 8, 0));                                       // ldr w9,[x8]            (ExceptionCode)
            for (int i = 0; i < s_codes.Length; i++)
            {
                a.I(LdrW(10, 19, OffCodes + 4 * i));                  // ldr w10,[x19,#codes+4i]
                a.I(0x6B0A013F);                                      // cmp w9,w10
                a.BCond(0x0, "codeok");                               // b.eq codeok
            }
            a.B("ret0");
            a.Label("codeok");
            a.I(LdrX(11, 8, 16));                                     // ldr x11,[x8,#16]       (ExceptionAddress)
            a.I(LdrX(12, 19, OffImageLo));                            // ldr x12,[x19]
            a.I(0xEB0C017F);                                          // cmp x11,x12
            a.BCond(0x3, "foreign");                                  // b.lo foreign
            a.I(LdrX(12, 19, OffImageHi));                            // ldr x12,[x19,#8]
            a.I(0xEB0C017F);                                          // cmp x11,x12
            a.BCond(0x3, "ret0");                                     // b.lo ret0              (inside Wavee.exe)
            a.Label("foreign");
            a.I(0x91000000 | (OffLatch << 10) | (19 << 5) | 12);      // add x12,x19,#latch
            a.Label("retry");
            a.I(0x885FFC00 | (12 << 5) | 13);                         // ldaxr w13,[x12]
            a.Cb(0x35000000, 13, "taken");                            // cbnz w13,taken
            a.I(0x5280002E);                                          // mov w14,#1
            a.I(0x8800FC00 | (15 << 16) | (12 << 5) | 14);            // stlxr w15,w14,[x12]
            a.Cb(0x35000000, 15, "retry");                            // cbnz w15,retry
            a.I(StrX(11, 19, OffAddress));                            // str x11,[x19,#address]
            a.I(StrW(9, 19, OffCode));                                // str w9,[x19,#code]
            a.I(StrX(20, 19, OffPointers));                           // str x20,[x19,#pointers]
            a.I(LdrX(16, 19, OffGetTid)); a.I(0xD63F0200);            // ldr x16,[x19,#GetCurrentThreadId]; blr x16
            a.I(StrW(0, 19, OffTid));                                 // str w0,[x19,#tid]
            a.I(LdrX(0, 19, OffRequest));                             // ldr x0,[x19,#request]
            a.I(LdrX(16, 19, OffSetEvent)); a.I(0xD63F0200);          // ldr x16,[x19,#SetEvent]; blr x16
            a.I(LdrX(0, 19, OffDone));                                // ldr x0,[x19,#done]
            a.I(LdrW(1, 19, OffWaitMs));                              // ldr w1,[x19,#waitMs]
            a.I(LdrX(16, 19, OffWait)); a.I(0xD63F0200);              // ldr x16,[x19,#WaitForSingleObject]; blr x16
            a.B("retR");
            a.Label("taken");
            a.I(0xD503305F);                                          // clrex
            a.Label("retR");
            a.I(0x52800000 | ((uint)result << 5));                    // mov w0,#result
            a.B("done");
            a.Label("ret0");
            a.I(0x52800000);                                          // mov w0,#0
            a.Label("done");
            a.I(0xA94153F3);                                          // ldp x19,x20,[sp,#16]
            a.I(0xA8C27BFD);                                          // ldp x29,x30,[sp],#32
            a.I(0xD65F03C0);                                          // ret
            return a.Finish(x64: false);
        }

        static uint LdrX(int rt, int rn, int off) => 0xF9400000 | ((uint)(off / 8) << 10) | ((uint)rn << 5) | (uint)rt;
        static uint LdrW(int rt, int rn, int off) => 0xB9400000 | ((uint)(off / 4) << 10) | ((uint)rn << 5) | (uint)rt;
        static uint StrX(int rt, int rn, int off) => 0xF9000000 | ((uint)(off / 8) << 10) | ((uint)rn << 5) | (uint)rt;
        static uint StrW(int rt, int rn, int off) => 0xB9000000 | ((uint)(off / 4) << 10) | ((uint)rn << 5) | (uint)rt;

        /// <summary>A two-pass-free label assembler: fixups are patched in <see cref="Finish"/>.</summary>
        sealed class Asm
        {
            readonly List<byte> _code = new(256);
            readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
            readonly List<(int At, string Label, int Kind, uint Template)> _fixups = new();   // kind: 0 x64 rel32, 1 arm b26, 2 arm imm19

            public void Bytes(params byte[] b) => _code.AddRange(b);
            public void U32(uint v) { _code.Add((byte)v); _code.Add((byte)(v >> 8)); _code.Add((byte)(v >> 16)); _code.Add((byte)(v >> 24)); }
            public void I(uint instruction) => U32(instruction);
            public void Label(string name) => _labels[name] = _code.Count;

            /// <summary>x64: a rel32 to a fixed distance from THIS thunk's start (the data page).</summary>
            public void Rel32To(int distanceFromStart) => U32(unchecked((uint)(distanceFromStart - (_code.Count + 4))));
            public void Jcc(byte cc, string label) { Bytes(0x0F, cc); _fixups.Add((_code.Count, label, 0, 0)); U32(0); }
            public void Jmp(string label) { Bytes(0xE9); _fixups.Add((_code.Count, label, 0, 0)); U32(0); }

            public void Adr(int rd, int distanceFromStart)
            {
                int off = distanceFromStart - _code.Count;
                I(0x10000000 | ((uint)(off & 3) << 29) | ((uint)((off >> 2) & 0x7FFFF) << 5) | (uint)rd);
            }
            public void B(string label) { _fixups.Add((_code.Count, label, 1, 0x14000000)); U32(0); }
            public void BCond(uint cond, string label) { _fixups.Add((_code.Count, label, 2, 0x54000000 | cond)); U32(0); }
            public void Cb(uint opcode, int rt, string label) { _fixups.Add((_code.Count, label, 2, opcode | (uint)rt)); U32(0); }

            public byte[] Finish(bool x64)
            {
                byte[] code = _code.ToArray();
                foreach (var (at, label, kind, template) in _fixups)
                {
                    int target = _labels[label];
                    uint v = kind switch
                    {
                        0 => unchecked((uint)(target - (at + 4))),
                        1 => template | ((uint)((target - at) >> 2) & 0x3FFFFFF),
                        _ => template | (((uint)((target - at) >> 2) & 0x7FFFF) << 5),
                    };
                    code[at] = (byte)v; code[at + 1] = (byte)(v >> 8); code[at + 2] = (byte)(v >> 16); code[at + 3] = (byte)(v >> 24);
                }
                if (code.Length > FilterThunkOffset) throw new InvalidOperationException("fault gate thunk too large");
                _ = x64;
                return code;
            }
        }

        // ── Win32 ─────────────────────────────────────────────────────────────────────────────────────────────────

        const uint MemCommit = 0x1000, MemReserve = 0x2000, PageReadWrite = 0x04, PageExecuteRead = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORY_BASIC_INFORMATION
        {
            public nint BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public nint RegionSize;
            public uint State, Protect, Type;
        }

        [LibraryImport("kernel32.dll")] private static partial nint VirtualAlloc(nint address, nuint size, uint type, uint protect);
        [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool VirtualProtect(nint address, nuint size, uint protect, out uint old);
        [LibraryImport("kernel32.dll")] private static partial nuint VirtualQuery(nint address, MEMORY_BASIC_INFORMATION* info, nuint length);
        [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool FlushInstructionCache(nint process, nint address, nuint size);
        [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
        [LibraryImport("kernel32.dll")] private static partial nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);
        [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetEvent(nint handle);
        [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint handle, uint ms);
        [LibraryImport("kernel32.dll")] private static partial nint SetUnhandledExceptionFilter(nint filter);
        [LibraryImport("kernel32.dll")] private static partial nint AddVectoredExceptionHandler(uint first, nint handler);
        [LibraryImport("ntdll.dll")] private static partial void* RtlLookupFunctionEntry(ulong controlPc, ulong* imageBase, void* historyTable);
        [LibraryImport("ntdll.dll")] private static partial void* RtlVirtualUnwind(uint handlerType, ulong imageBase, ulong controlPc, void* functionEntry,
            void* context, void** handlerData, ulong* establisherFrame, void* contextPointers);
    }
}
