using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Dollhouse.Core.Startup;

[SupportedOSPlatform("windows")]
internal static class WindowsDeviceIdentity
{
    private const int Silent = 0x40;
    private const int NotFound = unchecked((int)0x80090011);
    private const int BadKeyset = unchecked((int)0x80090016);
    private const int BadProvider = unchecked((int)0x80090013);
    private const int NotSupported = unchecked((int)0x80090029);
    private const int DeviceNotReady = unchecked((int)0x80090030);
    private const string KeyName = "FallenDollRehost-Device";

    public static void Delete(Action<string>? progress)
    {
        foreach (var providerName in new[] { "Microsoft Platform Crypto Provider", "Microsoft Software Key Storage Provider" })
            DeleteFromProvider(providerName, KeyName, progress);
    }

    internal static void DeleteFromProvider(string providerName, string keyName, Action<string>? progress = null)
    {
        var status = NCryptOpenStorageProvider(out var provider, providerName, 0);
        if (status != 0)
        {
            if (status is NotFound or BadProvider or NotSupported or DeviceNotReady)
            {
                progress?.Invoke($"Device-key provider unavailable: {providerName} (0x{status:X8}).");
                return;
            }
            throw Failure("open device-key provider", status);
        }
        try
        {
            status = NCryptOpenKey(provider, out var key, keyName, 0, Silent);
            if (status is NotFound or BadKeyset)
            {
                progress?.Invoke($"Device key already absent: {providerName}/{keyName}");
                return;
            }
            if (status != 0) throw Failure("open device key", status);
            // Only the current user's exact named key; never enumerate/delete a key store.
            // The Platform provider rejects SILENT on deletion (NTE_BAD_FLAGS),
            // although it accepts it on open. Use the standard zero-flags delete.
            status = NCryptDeleteKey(key, 0);
            if (status != 0)
            {
                NCryptFreeObject(key);
                throw Failure("delete device key", status);
            }
            // Successful NCryptDeleteKey also frees its handle.
            progress?.Invoke($"Deleted device key: {providerName}/{keyName}");
        }
        finally { NCryptFreeObject(provider); }
    }

    private static IOException Failure(string operation, int status) => new(
        $"Could not {operation} (0x{status:X8}): {new Win32Exception(status).Message.TrimEnd('.')}. Earlier reported deletions have already completed.");

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NCryptOpenStorageProvider(out IntPtr provider, string providerName, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NCryptOpenKey(IntPtr provider, out IntPtr key, string keyName, int legacyKeySpec, int flags);
    [DllImport("ncrypt.dll")]
    private static extern int NCryptDeleteKey(IntPtr key, int flags);
    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(IntPtr handle);
}
