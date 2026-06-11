using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using PhotoWell.Common;

namespace PhotoWell.Services.Security;

/// <summary>
/// Verifies the Authenticode signature of a downloaded executable before it is run.
/// Uses WinVerifyTrust so the file hash is checked against the signature (extracting
/// the certificate alone would not detect tampering). Windows-only.
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// Returns true if the file has a valid, trusted Authenticode signature and the
    /// signer's subject name contains <paramref name="expectedSignerFragment"/>
    /// (case-insensitive), e.g. "Ollama".
    /// </summary>
    public static bool IsSignedBy(string filePath, string expectedSignerFragment)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (!HasValidSignature(filePath))
        {
            AppLog.Error($"[Authenticode] '{Path.GetFileName(filePath)}' has no valid signature.");
            return false;
        }

        try
        {
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            if (signer.Subject.Contains(expectedSignerFragment, StringComparison.OrdinalIgnoreCase))
                return true;

            AppLog.Error($"[Authenticode] '{Path.GetFileName(filePath)}' is signed by '{signer.Subject}', expected signer containing '{expectedSignerFragment}'.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error($"[Authenticode] Failed to read signer certificate: {ex.Message}");
            return false;
        }
    }

    private static bool HasValidSignature(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath
        };

        var data = new WINTRUST_DATA
        {
            cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice          = 2,  // WTD_UI_NONE
            fdwRevocationChecks = 0,  // WTD_REVOKE_NONE (offline-friendly; chain trust still enforced)
            dwUnionChoice       = 1,  // WTD_CHOICE_FILE
            dwStateAction       = 0   // WTD_STATEACTION_IGNORE
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            data.pFile = fileInfoPtr;

            var actionId = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref actionId, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint   cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint   cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint   dwUIChoice;
        public uint   fdwRevocationChecks;
        public uint   dwUnionChoice;
        public IntPtr pFile;
        public uint   dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint   dwProvFlags;
        public uint   dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
