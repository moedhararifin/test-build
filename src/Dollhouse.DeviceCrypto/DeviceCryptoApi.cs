using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dollhouse.DeviceCrypto;

/// <summary>The game's eight NCrypt imports, with opaque process-local handles.</summary>
public static unsafe class DeviceCryptoApi
{
    public const string KeyPathVariable = "DOLLHOUSE_DEVICE_KEY_FILE";
    private const string KeyName = "FallenDollRehost-Device";
    private const string SoftwareProvider = "Microsoft Software Key Storage Provider";
    private const string Algorithm = "ECDSA_P256";
    private const string PublicBlobType = "ECCPUBLICBLOB";
    private const uint SilentFlag = 0x40;
    private const uint PersistFlag = 0x80000000;
    private const int InvalidHandle = unchecked((int)0x80090026);
    private const int InvalidParameter = unchecked((int)0x80090027);
    private const int NotSupported = unchecked((int)0x80090029);
    private const int BadKeyset = unchecked((int)0x80090016);
    private const int Exists = unchecked((int)0x8009000f);
    private const int BufferTooSmall = unchecked((int)0x80090028);
    private const int Failure = unchecked((int)0x80090020);
    private static readonly object Gate = new();
    private static readonly Dictionary<nint, object> Handles = [];
    private static long nextHandle;

    private static nint Add(object value)
    {
        var handle = checked((nint)(++nextHandle));
        Handles.Add(handle, value);
        return handle;
    }

    // Never let a managed exception escape a native ABI boundary.
    private static int Guard(Func<int> action)
    {
        lock (Gate)
        {
            try { return action(); }
            catch (Exception error)
            {
                try { Console.Error.WriteLine($"Dollhouse device key: operation failed ({error.GetType().Name}). Identity preserved."); }
                catch { /* Console availability must not affect native error handling. */ }
                return Failure;
            }
        }
    }

    private static string? Text(char* value)
    {
        if (value == null) return null;
        for (var length = 0; length < 128; length++)
            if (value[length] == '\0') return new string(value, 0, length);
        return null;
    }

    [UnmanagedCallersOnly(EntryPoint = "NCryptOpenStorageProvider")]
    public static int OpenProvider(nint* output, char* name, uint flags) => Guard(() =>
    {
        if (output == null) return InvalidParameter;
        *output = 0;
        if (flags != 0) return NotSupported;
        var provider = Text(name);
        if (name != null && provider is null) return InvalidParameter;
        if (provider is not (null or SoftwareProvider)) return NotSupported;
        var path = KeyPath();
        if (path is null || !Path.IsPathFullyQualified(path)) return BadKeyset;
        *output = Add(new DeviceKeyStore(path));
        return 0;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptOpenKey")]
    public static int OpenKey(nint provider, nint* output, char* name, uint spec, uint flags) => Guard(() =>
    {
        if (output == null) return InvalidParameter;
        *output = 0;
        if (!Handles.TryGetValue(provider, out var value) || value is not DeviceKeyStore store) return InvalidHandle;
        if (Text(name) != KeyName || spec != 0 || (flags & ~SilentFlag) != 0) return NotSupported;
        var material = store.Read();
        if (material is null) return BadKeyset;
        *output = Add(new DeviceSigningKey(store, material, finalized: true));
        return 0;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptCreatePersistedKey")]
    public static int CreateKey(nint provider, nint* output, char* algorithm, char* name, uint spec, uint flags) => Guard(() =>
    {
        if (output == null) return InvalidParameter;
        *output = 0;
        if (!Handles.TryGetValue(provider, out var value) || value is not DeviceKeyStore store) return InvalidHandle;
        if (Text(algorithm) != Algorithm || Text(name) != KeyName || spec != 0 || flags != 0) return NotSupported;
        if (store.Read() is not null) return Exists;
        *output = Add(new DeviceSigningKey(store, DeviceKeyStore.Create(), finalized: false));
        return 0;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptSetProperty")]
    public static int SetProperty(nint handle, char* name, byte* input, uint length, uint flags) => Guard(() =>
    {
        if (!Handles.TryGetValue(handle, out var value) || value is not DeviceSigningKey key) return InvalidHandle;
        if (key.IsFinalized || Text(name) != "Export Policy" || flags != PersistFlag) return NotSupported;
        if (input == null || length != 4) return InvalidParameter;
        return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(input, 4)) == 0 ? 0 : NotSupported;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptFinalizeKey")]
    public static int FinalizeKey(nint handle, uint flags) => Guard(() =>
    {
        if (!Handles.TryGetValue(handle, out var value) || value is not DeviceSigningKey key) return InvalidHandle;
        if (flags != 0 || key.IsFinalized) return NotSupported;
        key.FinalizeKey();
        return 0;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptExportKey")]
    public static int ExportKey(nint handle, nint exportKey, char* type, nint parameters,
        byte* output, uint length, uint* written, uint flags) => Guard(() =>
    {
        if (written == null) return InvalidParameter;
        *written = 0;
        if (!Handles.TryGetValue(handle, out var value) || value is not DeviceSigningKey { IsFinalized: true } key) return InvalidHandle;
        if (Text(type) != PublicBlobType || exportKey != 0 || parameters != 0 || flags != 0) return NotSupported;
        *written = DeviceSigningKey.PublicBlobSize;
        if (output == null) return 0;
        if (length < DeviceSigningKey.PublicBlobSize) return BufferTooSmall;
        key.ExportPublicBlob().CopyTo(new Span<byte>(output, DeviceSigningKey.PublicBlobSize));
        return 0;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptSignHash")]
    public static int SignHash(nint handle, nint padding, byte* hash, uint hashLength,
        byte* output, uint length, uint* written, uint flags) => Guard(() =>
    {
        if (written == null) return InvalidParameter;
        *written = 0;
        if (!Handles.TryGetValue(handle, out var value) || value is not DeviceSigningKey { IsFinalized: true } key) return InvalidHandle;
        if (padding != 0 || (flags & ~SilentFlag) != 0) return NotSupported;
        if (hash == null || hashLength != DeviceSigningKey.DigestSize) return InvalidParameter;
        *written = DeviceSigningKey.SignatureSize;
        if (output == null) return 0;
        if (length < DeviceSigningKey.SignatureSize) return BufferTooSmall;
        key.SignHash(new ReadOnlySpan<byte>(hash, DeviceSigningKey.DigestSize))
            .CopyTo(new Span<byte>(output, DeviceSigningKey.SignatureSize));
        return 0;
    });

    [UnmanagedCallersOnly(EntryPoint = "NCryptFreeObject")]
    public static int FreeObject(nint handle) => Guard(() => Handles.Remove(handle) ? 0 : InvalidHandle);

    private static string? KeyPath()
    {
        if (Environment.GetEnvironmentVariable(KeyPathVariable) is { } configured) return configured;
        // Manual launches use the same identity as launches through Dollhouse.
        if (Environment.ProcessPath is not { } executable) return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        return directory.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase)
            && directory.Parent?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) == true
            && directory.Parent.Parent?.Name.Equals("Paralogue", StringComparison.OrdinalIgnoreCase) == true
            ? Path.Combine(directory.Parent.Parent.Parent!.FullName, ".dollhouse", "device", "key.json")
            : null;
    }
}
