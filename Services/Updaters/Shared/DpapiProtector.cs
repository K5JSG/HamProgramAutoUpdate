using System.Runtime.InteropServices;
using System.Text;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Minimal Win32 DPAPI (CryptProtectData/CryptUnprotectData) wrapper, hand-rolled
/// the same way as the rest of this project's native interop (see
/// HiddenDesktopAutomation, InstallerWindowSuppressor) rather than taking the
/// System.Security.Cryptography.ProtectedData NuGet package. Encrypts a string
/// so it can only be decrypted by this same Windows user account on this same
/// machine - used to keep an optional GitHub PAT out of plaintext on disk.
/// </summary>
public static class DpapiProtector
{
    private const string Prefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>True if <paramref name="value"/> is already in the protected
    /// form this class produces, so a caller like PotaUpdaterConfig.Load()
    /// knows not to try to protect it again.</summary>
    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts for the current Windows user on this machine only,
    /// returned as "dpapi:&lt;base64&gt;" so it round-trips through
    /// IsProtected/Unprotect and is self-evidently not plaintext in the file.</summary>
    public static string Protect(string plaintext) =>
        Prefix + Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plaintext), protect: true));

    /// <summary>Reverses Protect(). Throws if <paramref name="value"/> is not
    /// in the "dpapi:" form - check IsProtected first.</summary>
    public static string Unprotect(string value)
    {
        if (!IsProtected(value))
            throw new ArgumentException("Value is not DPAPI-protected.", nameof(value));

        return Encoding.UTF8.GetString(Run(Convert.FromBase64String(value[Prefix.Length..]), protect: false));
    }

    private static byte[] Run(byte[] input, bool protect)
    {
        var inputHandle = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, inputHandle, input.Length);
            var inBlob = new DATA_BLOB { cbData = input.Length, pbData = inputHandle };

            bool ok;
            DATA_BLOB outBlob;
            if (protect)
                ok = CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outBlob);
            else
                ok = CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out outBlob);

            if (!ok)
            {
                var api = protect ? "CryptProtectData" : "CryptUnprotectData";
                throw new InvalidOperationException($"{api} failed (error {Marshal.GetLastWin32Error()}).");
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inputHandle);
        }
    }
}
