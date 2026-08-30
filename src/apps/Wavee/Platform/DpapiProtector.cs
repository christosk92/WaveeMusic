using System.Runtime.Versioning;
using System.Security.Cryptography;
using Wavee.Backend.Persistence;

namespace Wavee;

// OPTIONAL Windows at-rest protector — one swap behind the portable ICredentialProtector seam. DPAPI CurrentUser scope:
// the blob is decryptable only by this Windows user on this machine. On macOS/Linux the app uses NoOpProtector (or a
// Keychain/libsecret swap); the persistence layer itself is portable. The "dpapi" scheme tag means a Windows-protected
// blob is cleanly rejected (→ re-auth) if the profile is ever opened on another platform.
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ICredentialProtector
{
    public string Scheme => "dpapi";
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
}
