using System.Security.Cryptography;
using System.Runtime.Versioning;
using Dollhouse.Core.Startup;

namespace Dollhouse.Tests;

internal static class DeviceIdentityCleanupTests
{
    public static void Verify()
    {
        if (OperatingSystem.IsWindows()) VerifyWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindows()
    {
        VerifyProvider(CngProvider.MicrosoftSoftwareKeyStorageProvider);
        var platform = new CngProvider("Microsoft Platform Crypto Provider");
        try { VerifyProvider(platform); }
        catch (CryptographicException error) when (error.HResult is unchecked((int)0x80090013)
            or unchecked((int)0x80090029) or unchecked((int)0x80090030) or unchecked((int)0x80090011))
        {
            Console.WriteLine($"SKIP device-key test provider unavailable: {platform.Provider} (0x{error.HResult:X8}).");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyProvider(CngProvider provider)
    {
        var name = "Dollhouse.Tests.Cleanup." + Guid.NewGuid().ToString("N");
        var other = name + ".preserve";
        try
        {
            using (CngKey.Create(CngAlgorithm.ECDsaP256, name, new() { Provider = provider })) { }
            using (CngKey.Create(CngAlgorithm.ECDsaP256, other, new() { Provider = provider })) { }
            var messages = new List<string>();
            WindowsDeviceIdentity.DeleteFromProvider(provider.Provider, name, messages.Add);
            Check.True(!CngKey.Exists(name, provider), "Disposable device key survived cleanup.");
            Check.True(CngKey.Exists(other, provider), "Cleanup deleted an unrelated key.");
            WindowsDeviceIdentity.DeleteFromProvider(provider.Provider, name, messages.Add);
            Check.True(messages.Any(message => message.StartsWith("Device key already absent:", StringComparison.Ordinal)),
                "Repeated key cleanup must succeed for a missing key.");
        }
        finally
        {
            // These GUID-named keys belong exclusively to this test, never the game.
            foreach (var ownedName in new[] { name, other })
                if (CngKey.Exists(ownedName, provider))
                {
                    using var key = CngKey.Open(ownedName, provider);
                    key.Delete();
                }
        }
    }
}
