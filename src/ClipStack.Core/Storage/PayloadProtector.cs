using System.Security.Cryptography;

namespace ClipStack.Core.Storage;

/// <summary>
/// Encrypts payload bytes at rest with Windows DPAPI under the current user account.
/// </summary>
/// <remarks>
/// The key is derived from the user's Windows credentials and never stored by ClipStack,
/// so another account on the same machine cannot read the history even with file access.
/// This protects clips the source application never marked as sensitive — the exclusion
/// formats in <see cref="Utilities.ClipboardExclusionFormats"/> only help when the source
/// app cooperates.
///
/// What it does not protect against: anything running as the same user, which can call
/// Unprotect exactly as ClipStack does. This raises the cost of offline disk access, not
/// of local malware.
///
/// Every protected file carries a short magic header so reads can tell an encrypted
/// payload from a plaintext one written by an older build. That makes the upgrade a
/// no-op — existing history stays readable and is re-encrypted only when re-captured.
/// </remarks>
public static class PayloadProtector
{
    /// <summary>"CSP1" — ClipStack Protected, version 1.</summary>
    private static readonly byte[] Magic = [0x43, 0x53, 0x50, 0x31];

    /// <summary>Ties the ciphertext to ClipStack so it cannot be swapped with another app's blob.</summary>
    private static readonly byte[] Entropy = "ClipStack.PayloadProtector.v1"u8.ToArray();

    public static bool IsProtected(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Magic.Length)
            return false;

        for (var i = 0; i < Magic.Length; i++)
        {
            if (bytes[i] != Magic[i])
                return false;
        }

        return true;
    }

    /// <summary>Encrypts and prefixes the magic header. Returns the input unchanged on any failure.</summary>
    public static byte[] Protect(byte[] plaintext)
    {
        if (plaintext.Length == 0)
            return plaintext;

        var cipher = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        var result = new byte[Magic.Length + cipher.Length];
        Magic.CopyTo(result, 0);
        cipher.CopyTo(result, Magic.Length);
        return result;
    }

    /// <summary>
    /// Decrypts a protected payload. Bytes without the header are returned as-is, which is
    /// what keeps plaintext history from before this change readable.
    /// </summary>
    public static byte[] Unprotect(byte[] stored)
    {
        if (!IsProtected(stored))
            return stored;

        var cipher = new byte[stored.Length - Magic.Length];
        Array.Copy(stored, Magic.Length, cipher, 0, cipher.Length);
        return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
    }
}
