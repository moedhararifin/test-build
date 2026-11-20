using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dollhouse.Core.Diagnostics;

/// <summary>Direct loopback WinHTTP probe. Its certificate check is after the synthetic response.</summary>
[SupportedOSPlatform("windows")]
public static class WinHttpProbe
{
    public static object Run(WorkspacePaths paths, int port = 7900)
    {
        var handles = new List<nint>();
        try
        {
            var session = Check(Native.WinHttpOpen("DollhouseLocalProbe/2.0", 1, null, null, 0));
            handles.Add(session);
            Check(Native.WinHttpSetTimeouts(session, 5000, 5000, 5000, 5000));
            var connection = Check(Native.WinHttpConnect(session, "127.0.0.1", checked((ushort)port), 0));
            handles.Add(connection);
            const string target = "/__echo_probe?source=winhttp";
            var request = Check(Native.WinHttpOpenRequest(connection, "GET", target, null, null, 0, 0x00800000));
            handles.Add(request);
            uint flags = 0x3300;
            Check(Native.WinHttpSetOption(request, 31, ref flags, sizeof(uint)));
            Check(Native.WinHttpSendRequest(request, null, 0, 0, 0, 0, 0));
            Check(Native.WinHttpReceiveResponse(request, 0));
            uint size = (uint)IntPtr.Size;
            Check(Native.WinHttpQueryOption(request, 78, out var certificatePointer, ref size));
            string fingerprint;
            try
            {
                var certificate = Marshal.PtrToStructure<CertContext>(certificatePointer);
                var bytes = new byte[checked((int)certificate.Length)];
                Marshal.Copy(certificate.Encoded, bytes, 0, bytes.Length);
                fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes));
            }
            finally { Native.CertFreeCertificateContext(certificatePointer); }
            if (fingerprint != Security.LocalCertificate.Fingerprint(paths.Certificate))
                throw new InvalidOperationException("WinHTTP certificate pin mismatch.");
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                Check(Native.WinHttpReadData(request, buffer, (uint)buffer.Length, out var count));
                if (count == 0) break;
                body.Write(buffer, 0, checked((int)count));
                if (body.Length > 8 * 1024 * 1024) throw new IOException("Probe response exceeds 8 MiB.");
            }
            using var payload = JsonDocument.Parse(body.ToArray());
            if (payload.RootElement.GetProperty("method").GetString() != "GET"
                || payload.RootElement.GetProperty("target").GetString() != target)
                throw new InvalidOperationException("Unexpected echo response.");
            return new
            {
                Transport = "Windows WinHTTP",
                DirectLoopback = true,
                CertificateSha256 = fingerprint,
                CertificatePinMatches = true,
                PinCheckTiming = "after response",
                SystemTrustModified = false,
                EchoVerified = true,
                GameExecuted = false
            };
        }
        finally
        {
            for (var i = handles.Count - 1; i >= 0; i--) Native.WinHttpCloseHandle(handles[i]);
        }
    }

    private static void Check(bool result)
    {
        if (!result) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static nint Check(nint result)
    {
        if (result == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertContext
    {
        public uint Encoding;
        public nint Encoded;
        public uint Length;
        public nint Info;
        public nint Store;
    }

    private static class Native
    {
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint WinHttpOpen(string agent, uint access, string? proxy, string? bypass, uint flags);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint WinHttpConnect(nint session, string host, ushort port, uint reserved);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint WinHttpOpenRequest(nint connection, string method, string target,
            string? version, string? referer, nint acceptTypes, uint flags);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpSetOption(nint request, uint option, ref uint value, uint length);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpSetTimeouts(nint session, int resolve, int connect, int send, int receive);
        [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpSendRequest(nint request, string? headers, uint headerLength,
            nint optional, uint optionalLength, uint totalLength, nuint context);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpReceiveResponse(nint request, nint reserved);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpQueryOption(nint request, uint option, out nint value, ref uint length);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpReadData(nint request, byte[] buffer, uint requested, out uint read);
        [DllImport("winhttp.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WinHttpCloseHandle(nint handle);
        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CertFreeCertificateContext(nint certificate);
    }
}
