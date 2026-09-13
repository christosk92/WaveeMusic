using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Runtime;

/// <summary>PE machine-type detection + advisory Authenticode via WinVerifyTrust (Windows only).</summary>
static class PeAndSignature
{
    const ushort ImageFileMachineAmd64 = 0x8664;
    const ushort ImageFileMachineArm64 = 0xAA64;

    public static bool TryGetPeArchitecture(string path, out Architecture arch, out string? error)
    {
        arch = default;
        error = null;
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (fs.Read(dos) < 64) { error = "file too small"; return false; }
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') { error = "not a PE file"; return false; }
            int peOffset = BitConverter.ToInt32(dos.Slice(0x3C, 4));
            if (peOffset <= 0 || peOffset > fs.Length - 6) { error = "invalid PE offset"; return false; }
            fs.Position = peOffset;
            Span<byte> pe = stackalloc byte[6];
            if (fs.Read(pe) < 6) { error = "truncated PE header"; return false; }
            if (pe[0] != (byte)'P' || pe[1] != (byte)'E') { error = "invalid PE signature"; return false; }
            ushort machine = BitConverter.ToUInt16(pe.Slice(4, 2));
            arch = machine switch
            {
                ImageFileMachineAmd64 => Architecture.X64,
                ImageFileMachineArm64 => Architecture.Arm64,
                _ => default,
            };
            if (arch == default)
            {
                error = $"unsupported PE machine 0x{machine:X4}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Advisory Authenticode check. Any native fault degrades to <see cref="SignatureTrust.Unknown"/> — never
    /// a crash. WINTRUST_DATA's file member is a <c>WINTRUST_FILE_INFO*</c> union pointer, so it must be marshalled as an
    /// out-of-line allocation (embedding the struct by value mislays every field after the union and hands the API a
    /// malformed struct — the 0xC0000005 access violation this replaces).</summary>
    [SupportedOSPlatform("windows")]
    public static (SignatureTrust trust, string reason) TryVerifyAuthenticode(string path)
    {
        if (!OperatingSystem.IsWindows())
            return (SignatureTrust.UnsupportedPlatform, "unsupported platform");

        IntPtr pFile = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO_W
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO_W>(),
                pcwszFilePath = path,
            };
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO_W>());
            Marshal.StructureToPtr(fileInfo, pFile, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                pFile = pFile,
            };

            var actionId = WintrustActionGenericVerifyV2;
            int hr = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
            // Always release the provider state allocated during VERIFY, regardless of the verdict.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

            if (hr == 0) return (SignatureTrust.Trusted, "signature valid");
            if (hr == unchecked((int)0x800B0109)) return (SignatureTrust.Untrusted, "not signed or untrusted publisher");
            if (hr == unchecked((int)0x800B010A)) return (SignatureTrust.Untrusted, "signature expired");
            if (hr == unchecked((int)0x800B0100)) return (SignatureTrust.Untrusted, "not signed");
            return (SignatureTrust.Untrusted, $"WinVerifyTrust HRESULT 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            // Never let a native/marshalling fault crash the host — signature trust is advisory.
            return (SignatureTrust.Unknown, "verification error: " + ex.Message);
        }
        finally
        {
            if (pFile != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WINTRUST_FILE_INFO_W>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }

    /// <summary>Opens the native Windows certificate/signature viewer for a signed PE file (cryptui.dll).</summary>
    [SupportedOSPlatform("windows")]
    public static bool TryShowNativeSignatureDialog(string path, nint hwndParent = 0)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        IntPtr dup = IntPtr.Zero;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            dup = CertDuplicateCertificateContext(cert.Handle);
            if (dup == IntPtr.Zero) return false;

            var view = new CRYPTUI_VIEWCERTIFICATE_STRUCT
            {
                dwSize = (uint)Marshal.SizeOf<CRYPTUI_VIEWCERTIFICATE_STRUCT>(),
                hwndParent = hwndParent,
                dwFlags = CRYPTUI_DISABLE_ADDTOSTORE,
                szTitle = "Digital Signature",
                pCertContext = dup,
            };
            return CryptUIDlgViewCertificate(ref view, out _);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (dup != IntPtr.Zero) CertFreeCertificateContext(dup);
        }
    }

    [SupportedOSPlatform("windows")]
    public static DigitalSignatureInfo? TryReadDigitalSignatureInfo(string path, SignatureTrust trust, string reason)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
#pragma warning disable SYSLIB0057 // Extracts the Authenticode signer from a signed PE file, not a standalone cert file.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var subject = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var issuer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            return new DigitalSignatureInfo(
                path,
                string.IsNullOrWhiteSpace(subject) ? cert.Subject : subject,
                string.IsNullOrWhiteSpace(issuer) ? cert.Issuer : issuer,
                cert.Thumbprint ?? "",
                new DateTimeOffset(cert.NotBefore),
                new DateTimeOffset(cert.NotAfter),
                trust,
                reason);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    const uint WTD_UI_NONE = 2;
    const uint WTD_REVOKE_NONE = 0;
    const uint WTD_CHOICE_FILE = 1;
    const uint WTD_STATEACTION_VERIFY = 1;
    const uint WTD_STATEACTION_CLOSE = 2;
    const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    const uint CRYPTUI_DISABLE_ADDTOSTORE = 0x00000010;

    [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptUIDlgViewCertificate(ref CRYPTUI_VIEWCERTIFICATE_STRUCT pCryptUIViewCert, out bool pfPropertiesChanged);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern IntPtr CertDuplicateCertificateContext(IntPtr pCertContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CertFreeCertificateContext(IntPtr pCertContext);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CRYPTUI_VIEWCERTIFICATE_STRUCT
    {
        public uint dwSize;
        public IntPtr hwndParent;
        public uint dwFlags;
        public string? szTitle;
        public IntPtr pCertContext;
        public IntPtr rgszPurposes;
        public uint cPurposes;
        // Native union: CRYPT_PROVIDER_DATA* pCryptProviderData / HANDLE hWVTStateData.
        public IntPtr pCryptProviderData;
        public int fpCryptProviderDataTrustedUsage;
        public uint idxSigner;
        public uint idxCert;
        public int fCounterSigner;
        public uint idxCounterSigner;
        public uint cStores;
        public IntPtr rghStores;
        public uint cPropSheetPages;
        public IntPtr rgPropSheetPages;
        public uint nStartPage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO_W
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        // Union member: WINTRUST_FILE_INFO* / WINTRUST_CATALOG_INFO* / … — a POINTER, marshalled out-of-line.
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}
