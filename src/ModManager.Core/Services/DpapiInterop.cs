using System.Runtime.InteropServices;

namespace ModManager.Core.Services;

/// <summary>
/// Windows DPAPI bindings (<c>CryptProtectData</c> / <c>CryptUnprotectData</c>).
/// </summary>
/// <remarks>
/// <para>
/// WHY A P/INVOKE RATHER THAN A PACKAGE: <c>System.Security.Cryptography.ProtectedData</c> is not part of
/// the .NET 8 base class library — it ships as a separate NuGet package. This engine deliberately takes no
/// packages (see <c>ModManager.Core.csproj</c>), so that it builds and its tests run with no feed. That
/// package is a thin wrapper over exactly the two <c>crypt32.dll</c> calls below, so binding them directly
/// keeps the rule intact without giving up per-user encryption. It is the same reasoning that made
/// <c>FileSystemOps</c> P/Invoke <c>CreateHardLinkW</c> instead of taking a package.
/// </para>
/// <para>
/// WHAT THE PROTECTION IS: DPAPI ties the ciphertext to the current Windows user account, so a
/// <c>secrets.dat</c> copied to another machine or read by another account cannot yield the API key. It is
/// explicitly <b>not</b> a defence against code already running as that same user, and nothing here
/// substitutes for the user treating their own key as a credential.
/// </para>
/// </remarks>
internal static class DpapiInterop
{
    /// <summary>Never show a UI prompt: the app may be running without an interactive desktop.</summary>
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>Win32 <c>DATA_BLOB</c>: a length and a pointer to the bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        /// <summary>Buffer length in bytes.</summary>
        public int Size;

        /// <summary>Pointer to the buffer.</summary>
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>Encrypts bytes for the current Windows user.</summary>
    /// <param name="plaintext">Bytes to protect.</param>
    /// <param name="entropy">Additional entropy; the same value is required to decrypt.</param>
    /// <returns>The ciphertext, or <c>null</c> when DPAPI refused.</returns>
    internal static byte[]? Protect(byte[] plaintext, byte[] entropy) =>
        Transform(plaintext, entropy, protect: true);

    /// <summary>Decrypts bytes that <see cref="Protect"/> produced for the current Windows user.</summary>
    /// <param name="ciphertext">Bytes to unprotect.</param>
    /// <param name="entropy">The entropy value used when protecting.</param>
    /// <returns>The plaintext, or <c>null</c> when DPAPI refused (for example another user's ciphertext).</returns>
    internal static byte[]? Unprotect(byte[] ciphertext, byte[] entropy) =>
        Transform(ciphertext, entropy, protect: false);

    /// <summary>Runs one DPAPI direction over pinned buffers and copies the result out.</summary>
    /// <param name="input">Bytes to transform.</param>
    /// <param name="entropy">Optional entropy, always supplied here.</param>
    /// <param name="protect">True to protect, false to unprotect.</param>
    /// <returns>The transformed bytes, or <c>null</c> when the call failed.</returns>
    private static byte[]? Transform(byte[] input, byte[] entropy, bool protect)
    {
        // DPAPI rejects an empty input buffer outright. An empty secret is not meaningful, and returning
        // an empty array keeps callers on their normal path instead of failing a save.
        if (input.Length == 0)
        {
            return [];
        }

        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };

            DataBlob output;
            bool succeeded;
            if (protect)
            {
                succeeded = CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            }
            else
            {
                succeeded = CryptUnprotectData(
                    ref inputBlob,
                    IntPtr.Zero,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out output);
            }

            if (!succeeded)
            {
                return null;
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                // The output buffer is allocated by DPAPI and owned by us; not freeing it would leak
                // plaintext into the process heap.
                _ = LocalFree(output.Data);
            }
        }
        finally
        {
            inputHandle.Free();
            entropyHandle.Free();
        }
    }
}
