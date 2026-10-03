using System.Runtime.InteropServices;
using System.Text;

namespace HamProgramAutoUpdate.Services.Updaters.Shared;

/// <summary>
/// Minimal Win32 DPAPI (CryptProtectData/CryptUnprotectData) wrapper, hand-rolled
/// the same way as the rest of this project's native interop (see
/// HiddenDesktopAutomation, InstallerWindowSuppressor) rather than taking the
/// System.Security.Cryptography.ProtectedData NuGet package. Keeps an
/// optional GitHub PAT out of plaintext on disk.
///
/// New values are encrypted for this MACHINE ("dpapi-m:"), not one Windows
/// user, because the nightly "Program Update Scripts" task runs as SYSTEM -
/// which can't decrypt a value tied to the user who saved it. That's why the
/// files holding these tokens are also locked to Administrators and SYSTEM
/// (see SecureFile): machine scope alone would let any account on the PC
/// decrypt them. Older user-scoped values ("dpapi:") still decrypt for the
/// user who saved them; callers re-protect them via NeedsUpgrade.
/// </summary>
public static class DpapiProtector
{
    private const string UserPrefix = "dpapi:";
    private const string MachinePrefix = "dpapi-m:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

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
        value is not null &&
        (value.StartsWith(MachinePrefix, StringComparison.Ordinal) ||
         value.StartsWith(UserPrefix, StringComparison.Ordinal));

    /// <summary>True for an older user-scoped value that should be
    /// re-protected with Protect() (machine scope) once it has been
    /// decrypted - which only works as the user who saved it.</summary>
    public static bool NeedsUpgrade(string? value) =>
        value is not null && value.StartsWith(UserPrefix, StringComparison.Ordinal);

    /// <summary>Encrypts for this machine, returned as "dpapi-m:&lt;base64&gt;"
    /// so it round-trips through IsProtected/Unprotect and is self-evidently
    /// not plaintext in the file.</summary>
    public static string Protect(string plaintext) =>
        MachinePrefix + Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plaintext), protect: true));

    /// <summary>Reverses Protect() (or an older user-scoped value). Throws if
    /// <paramref name="value"/> is not protected - check IsProtected first.</summary>
    public static string Unprotect(string value)
    {
        var prefix = value.StartsWith(MachinePrefix, StringComparison.Ordinal) ? MachinePrefix
            : value.StartsWith(UserPrefix, StringComparison.Ordinal) ? UserPrefix
            : throw new ArgumentException("Value is not DPAPI-protected.", nameof(value));

        return Encoding.UTF8.GetString(Run(Convert.FromBase64String(value[prefix.Length..]), protect: false));
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
                ok = CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_LOCAL_MACHINE | CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            else
                ok = CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

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
