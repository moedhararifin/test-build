using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dollhouse.Core.Patching;
using Dollhouse.Core.Startup;
using Dollhouse.DeviceCrypto;
using Dollhouse.Server.Licensing;
using Dollhouse.Core.Security;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Math;

namespace Dollhouse.Tests;

internal static unsafe class DeviceCryptoTests
{
    private sealed class Api(nint library)
    {
        public readonly delegate* unmanaged<nint*, char*, uint, int> OpenProvider = library == 0
            ? &DeviceCryptoApi.OpenProvider : (delegate* unmanaged<nint*, char*, uint, int>)NativeLibrary.GetExport(library, "NCryptOpenStorageProvider");
        public readonly delegate* unmanaged<nint, nint*, char*, uint, uint, int> OpenKey = library == 0
            ? &DeviceCryptoApi.OpenKey : (delegate* unmanaged<nint, nint*, char*, uint, uint, int>)NativeLibrary.GetExport(library, "NCryptOpenKey");
        public readonly delegate* unmanaged<nint, nint*, char*, char*, uint, uint, int> CreateKey = library == 0
            ? &DeviceCryptoApi.CreateKey : (delegate* unmanaged<nint, nint*, char*, char*, uint, uint, int>)NativeLibrary.GetExport(library, "NCryptCreatePersistedKey");
        public readonly delegate* unmanaged<nint, char*, byte*, uint, uint, int> SetProperty = library == 0
            ? &DeviceCryptoApi.SetProperty : (delegate* unmanaged<nint, char*, byte*, uint, uint, int>)NativeLibrary.GetExport(library, "NCryptSetProperty");
        public readonly delegate* unmanaged<nint, uint, int> FinalizeKey = library == 0
            ? &DeviceCryptoApi.FinalizeKey : (delegate* unmanaged<nint, uint, int>)NativeLibrary.GetExport(library, "NCryptFinalizeKey");
        public readonly delegate* unmanaged<nint, nint, char*, nint, byte*, uint, uint*, uint, int> ExportKey = library == 0
            ? &DeviceCryptoApi.ExportKey : (delegate* unmanaged<nint, nint, char*, nint, byte*, uint, uint*, uint, int>)NativeLibrary.GetExport(library, "NCryptExportKey");
        public readonly delegate* unmanaged<nint, nint, byte*, uint, byte*, uint, uint*, uint, int> SignHash = library == 0
            ? &DeviceCryptoApi.SignHash : (delegate* unmanaged<nint, nint, byte*, uint, byte*, uint, uint*, uint, int>)NativeLibrary.GetExport(library, "NCryptSignHash");
        public readonly delegate* unmanaged<nint, int> FreeObject = library == 0
            ? &DeviceCryptoApi.FreeObject : (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "NCryptFreeObject");
    }

    public static void Verify()
    {
        VerifyApi(new Api(0));
        VerifyExtraction();
        VerifyDefaultPreparation();
        if (Environment.GetEnvironmentVariable("DOLLHOUSE_TEST_DEVICE_CRYPTO") is { } library)
        {
            library = Path.GetFullPath(library);
            // Native AOT libraries cannot be unloaded; retain this handle until process exit.
            var api = new Api(NativeLibrary.Load(library));
            VerifyApi(api, library);
            Console.WriteLine("PASS native device-key DLL: all eight real exports, persistence and child-process reopen (own library only).");
        }
    }

    private static nint Provider(Api api)
    {
        nint provider;
        fixed (char* name = "Microsoft Software Key Storage Provider")
            Check.Equal(0, api.OpenProvider(&provider, name, 0));
        return provider;
    }

    private static nint Create(Api api, nint provider)
    {
        nint key;
        fixed (char* name = "FallenDollRehost-Device")
        fixed (char* algorithm = "ECDSA_P256")
            Check.Equal(0, api.CreateKey(provider, &key, algorithm, name, 0, 0));
        return key;
    }

    private static nint Open(Api api, nint provider)
    {
        nint key;
        fixed (char* name = "FallenDollRehost-Device")
            Check.Equal(0, api.OpenKey(provider, &key, name, 0, 0));
        return key;
    }

    private static byte[] Public(Api api, nint key)
    {
        uint size;
        var blob = new byte[72];
        fixed (char* type = "ECCPUBLICBLOB")
        fixed (byte* output = blob)
        {
            Check.Equal(0, api.ExportKey(key, 0, type, 0, null, 0, &size, 0));
            Check.Equal(72u, size);
            Check.True(api.ExportKey(key, 0, type, 0, output, 71, &size, 0) != 0, "Short export buffer accepted.");
            Check.Equal(0, api.ExportKey(key, 0, type, 0, output, 72, &size, 0));
            Check.True(api.ExportKey(key, 0, type, 0, output, 72, null, 0) != 0, "Null result-size accepted.");
        }
        Check.Equal(0x31534345u, BitConverter.ToUInt32(blob));
        Check.Equal(32u, BitConverter.ToUInt32(blob, 4));
        return blob;
    }

    private static void VerifySignature(Api api, nint key, byte[] blob)
    {
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = blob[8..40], Y = blob[40..72] }
        });
        var message = "{\"account\":\"synthetic\",\"action\":\"activate\",\"client_nonce\":\"fixture\",\"device\":\"fixture\",\"nonce\":\"fixture\",\"product\":\"fallendoll\"}"u8.ToArray();
        var hash = SHA256.HashData(message);
        var signature = new byte[64];
        uint length;
        fixed (byte* digest = hash)
        fixed (byte* output = signature)
        {
            Check.Equal(0, api.SignHash(key, 0, digest, 32, null, 0, &length, 0));
            Check.Equal(64u, length);
            Check.True(api.SignHash(key, 0, digest, 32, output, 63, &length, 0) != 0, "Short signature buffer accepted.");
            Check.True(api.SignHash(key, 0, digest, 31, output, 64, &length, 0) != 0, "Wrong digest size accepted.");
            Check.Equal(0, api.SignHash(key, 0, digest, 32, output, 64, &length, 0));
        }
        Check.True(verifier.VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "Device signature failed independent verification.");
        var server = LicenseDeviceKey.Import(LocalLicenseAuthority.Base64Url(verifier.ExportSubjectPublicKeyInfo()));
        var der = new DerSequence(new DerInteger(new BigInteger(1, signature[..32])),
            new DerInteger(new BigInteger(1, signature[32..]))).GetDerEncoded();
        Check.True(server.Verify(message, der), "Native raw-to-DER proof failed the server verifier.");
        der[^1] ^= 1;
        Check.True(!server.Verify(message, der), "Tampered proof accepted.");
        foreach (var invalid in new byte[][]
        {
            [0x30, 0],
            [0x30, 6, 2, 1, 0, 2, 1, 1], // zero r
            [0x30, 6, 2, 1, 1, 2, 1, 0], // zero s
            [0x30, 6, 2, 1, 0xff, 2, 1, 1], // negative r
            [0x30, 6, 4, 1, 1, 2, 1, 1], // non-integer r
            [0x30, 7, 2, 2, 0, 1, 2, 1, 1] // noncanonical integer
        }) Check.True(!server.Verify(message, invalid), "Malformed proof accepted.");
    }

    private static void VerifyApi(Api api, string? nativeLibrary = null)
    {
        using var workspace = new TemporaryWorkspace();
        var old = Environment.GetEnvironmentVariable(DeviceCryptoApi.KeyPathVariable);
        var path = Path.Combine(workspace.Paths.Local, "device", "key.json");
        Environment.SetEnvironmentVariable(DeviceCryptoApi.KeyPathVariable, path);
        try
        {
            nint rejected = 123;
            fixed (char* tpm = "Microsoft Platform Crypto Provider")
                Check.True(api.OpenProvider(&rejected, tpm, 0) != 0 && rejected == 0, "TPM backing falsely advertised.");
            Check.True(api.OpenProvider(null, null, 0) != 0, "Null provider output accepted.");
            var provider = Provider(api);
            var key = Create(api, provider);
            var competing = Create(api, provider);
            Check.True(!File.Exists(path), "Unfinalized key was persisted.");
            uint policy = 0;
            fixed (char* property = "Export Policy")
                Check.Equal(0, api.SetProperty(key, property, (byte*)&policy, 4, 0x80000000));
            Check.Equal(0, api.FinalizeKey(key, 0));
            var original = File.ReadAllBytes(path);
            var blob = Public(api, key);
            if (nativeLibrary is not null)
            {
                using var publicKey = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = blob[8..40], Y = blob[40..72] }
                });
                var activeKey = key;
                LicenseLeaseTests.VerifyDeviceProofAsync(LocalLicenseAuthority.Base64Url(publicKey.ExportSubjectPublicKeyInfo()),
                    message => SignProof(api, activeKey, message)).GetAwaiter().GetResult();
            }
            Check.Equal(0, api.FinalizeKey(competing, 0));
            Check.True(Public(api, competing).SequenceEqual(blob), "Concurrent finalization rotated identity.");
            VerifySignature(api, key, blob);
            Check.Equal(0, api.FreeObject(provider)); // Keys retain their provider/store ownership.
            VerifySignature(api, key, blob);
            Check.Equal(0, api.FreeObject(key));
            Check.Equal(0, api.FreeObject(competing));
            Check.True(api.FreeObject(key) != 0, "Double free accepted.");
            provider = Provider(api);
            key = Open(api, provider);
            Check.True(Public(api, key).SequenceEqual(blob), "Reopen changed identity.");
            VerifySignature(api, key, blob);
            Check.Equal(0, api.FreeObject(key));
            Check.Equal(0, api.FreeObject(provider));
            Check.True(original.SequenceEqual(File.ReadAllBytes(path)), "Reopen or signing rewrote identity.");
            var copiedPath = Path.Combine(workspace.Paths.Root, "copied-game", ".dollhouse", "device", "key.json");
            Directory.CreateDirectory(Path.GetDirectoryName(copiedPath)!);
            File.Copy(path, copiedPath);
            Environment.SetEnvironmentVariable(DeviceCryptoApi.KeyPathVariable, copiedPath);
            try
            {
                var copiedProvider = Provider(api);
                var copiedKey = Open(api, copiedProvider);
                Check.True(Public(api, copiedKey).SequenceEqual(blob), "Copying the adapter key changed its public identity.");
                VerifySignature(api, copiedKey, blob);
                api.FreeObject(copiedKey);
                api.FreeObject(copiedProvider);
                Check.True(original.SequenceEqual(File.ReadAllBytes(copiedPath)), "Opening the copied key rewrote it.");
            }
            finally { Environment.SetEnvironmentVariable(DeviceCryptoApi.KeyPathVariable, path); }
            Task.WaitAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var owner = Provider(api);
                var opened = Open(api, owner);
                Check.True(Public(api, opened).SequenceEqual(blob), "Concurrent reopen changed identity.");
                api.FreeObject(opened);
                api.FreeObject(owner);
            })).ToArray());
            if (nativeLibrary is not null) VerifyRestart(nativeLibrary, blob);

            File.WriteAllText(path, "{corrupt}");
            provider = Provider(api);
            fixed (char* name = "FallenDollRehost-Device")
            fixed (char* algorithm = "ECDSA_P256")
            {
                Check.True(api.OpenKey(provider, &key, name, 0, 0) != 0 && key == 0, "Corrupt identity reopened.");
                Check.True(api.CreateKey(provider, &key, algorithm, name, 0, 0) != 0 && key == 0, "Corrupt identity replaced.");
            }
            Check.Equal("{corrupt}", File.ReadAllText(path));
            api.FreeObject(provider);

            File.Delete(path);
            provider = Provider(api);
            key = Create(api, provider);
            Directory.CreateDirectory(path); // Force commit failure, then retry the same handle.
            Check.True(api.FinalizeKey(key, 0) != 0, "Failed key write returned success.");
            Directory.Delete(path);
            Check.Equal(0, api.FinalizeKey(key, 0));
            VerifySignature(api, key, Public(api, key));
            api.FreeObject(key);
            api.FreeObject(provider);
        }
        finally { Environment.SetEnvironmentVariable(DeviceCryptoApi.KeyPathVariable, old); }
    }

    private static void VerifyRestart(string library, byte[] expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--device-crypto-reopen");
        start.ArgumentList.Add(library);
        start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(expected)));
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(30_000)) { child.Kill(); throw new TimeoutException("Device-key reopen test timed out."); }
        Task.WaitAll(stdout, stderr);
        Check.Equal(0, child.ExitCode);
    }

    private static byte[] SignProof(Api api, nint key, byte[] message)
    {
        var digest = SHA256.HashData(message);
        var signature = new byte[64];
        uint length;
        fixed (byte* hash = digest)
        fixed (byte* output = signature)
            Check.Equal(0, api.SignHash(key, 0, hash, 32, output, 64, &length, 0));
        return new DerSequence(new DerInteger(new BigInteger(1, signature[..32])),
            new DerInteger(new BigInteger(1, signature[32..]))).GetDerEncoded();
    }

    public static void Reopen(string library, string fingerprint)
    {
        var api = new Api(NativeLibrary.Load(library));
        var provider = Provider(api);
        var key = Open(api, provider);
        var blob = Public(api, key);
        Check.Equal(fingerprint, Convert.ToHexString(SHA256.HashData(blob)));
        VerifySignature(api, key, blob);
        Check.Equal(0, api.FreeObject(key));
        Check.Equal(0, api.FreeObject(provider));
    }

    private static void VerifyExtraction()
    {
        if (!RuntimeCompatibility.IsBundled) return;
        using var workspace = new TemporaryWorkspace();
        var installation = new GameInstallation(Path.Combine(workspace.Paths.Root, "Paralogue", "Binaries", "Win64", "unused.exe"), workspace.Paths.Root);
        var previous = RuntimeCompatibility.PreviousLibraryPath(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
        File.WriteAllText(previous, "old program-managed runtime");
        Check.True(RuntimeCompatibility.Prepare(installation), "Native resource was not extracted.");
        var file = RuntimeCompatibility.LibraryPath(installation);
        Check.Equal(Path.Combine(installation.Paths.Local, "runtime", "dhruntime.dll"), file);
        Check.True(!File.Exists(previous), "The previous program-managed runtime survived relocation.");
        var expected = File.ReadAllBytes(file);
        Check.True(expected.Length > 1024 && expected[0] == 'M' && expected[1] == 'Z', "Native library is not a PE image.");
        Check.True(!RuntimeCompatibility.Prepare(installation), "Identical library was rewritten.");
        File.WriteAllText(file, "damaged program-managed resource");
        Check.True(RuntimeCompatibility.Prepare(installation), "Library was not repaired.");
        Check.True(expected.SequenceEqual(File.ReadAllBytes(file)), "Native resource repair changed bytes.");
        File.WriteAllText(previous, "old runtime retained on extraction failure");
        using (var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Check.Throws<IOException>(() => RuntimeCompatibility.Prepare(installation));
        Check.Equal("old runtime retained on extraction failure", File.ReadAllText(previous));
        Check.True(RuntimeCompatibility.Prepare(installation), "Relocation retry did not install the runtime.");
        Check.True(!File.Exists(previous), "Successful relocation retry retained the previous runtime.");
        var start = GameLauncher.CreateStartInfo(installation);
        Check.Equal(DeviceKeyPaths.KeyPath(installation), start.Environment[DeviceKeyPaths.KeyPathVariable]);
        Check.True(!File.Exists(DeviceKeyPaths.KeyPath(installation)), "DLL extraction generated an unrelated key.");
        var targets = GameCleanup.Plan(installation, Path.Combine(workspace.Paths.Root, "localapp"));
        Check.True(targets.Any(target => target.Path == Path.GetDirectoryName(file) && target.Directory), "Clean omits native runtime directory.");
        Check.True(targets.Any(target => target.Path == Path.GetDirectoryName(DeviceKeyPaths.KeyPath(installation)) && target.Directory), "Clean omits device identity.");
    }

    private static void VerifyDefaultPreparation()
    {
        if (GameBinaryFixture.Original is not { } fixture
            || !RuntimeCompatibility.IsBundled) return;
        using var workspace = new TemporaryWorkspace();
        var installation = new GameInstallation(Path.Combine(workspace.Paths.Root, "game.exe"), workspace.Paths.Root);
        File.Copy(fixture, installation.Executable);
        File.Copy(fixture, GameBinary.BackupPath(installation.Executable));
        // Model an older three-allocation install with its existing issuer and TLS identity.
        var certificate = Dollhouse.Core.Security.LocalCertificate.Setup(installation.Paths);
        var authority = LocalLicenseAuthority.Setup(installation.Paths);
        var issuer = File.ReadAllBytes(LocalLicenseAuthority.StatePath(installation.Paths));
        var account = Path.Combine(installation.Paths.Local, "auth", "accounts.json");
        Directory.CreateDirectory(Path.GetDirectoryName(account)!);
        File.WriteAllText(account, "existing account fixture: preserve");

        var prepared = GamePreparation.Prepare(installation);
        Check.True(File.Exists(RuntimeCompatibility.LibraryPath(installation)), "Normal startup omitted the runtime library.");
        Check.True(!File.Exists(Path.Combine(installation.Paths.Local, "runtime.bin")), "Startup must not generate runtime configuration.");
        Check.Equal(authority.RootPublicKey, RuntimeTrust.Read(installation.Paths).LicenseRoot);
        Check.Equal(certificate.CertificateSha256, prepared.CertificateSha256);
        Check.True(issuer.SequenceEqual(File.ReadAllBytes(LocalLicenseAuthority.StatePath(installation.Paths))), "Upgrade changed issuer keys.");
        Check.Equal("existing account fixture: preserve", File.ReadAllText(account));
        Check.Equal(prepared.CertificateSha256, GamePreparation.Prepare(installation).CertificateSha256);
        Check.True(!File.Exists(GameBinary.BackupPath(installation.Executable)), "Preparation retained a redundant binary backup.");
    }
}
