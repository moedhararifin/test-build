using System.Net.WebSockets;
using System.Text;
using Dollhouse.Core;
using Dollhouse.Core.Patching;
using Dollhouse.Server.Echo;

namespace Dollhouse.Server.Debugging;

/// <summary>Explicitly enabled private capture sink. It never sends or replays traffic.</summary>
public sealed class DebugRequestCapture : IRequestCapture
{
    private long requestSequence;
    private long chunkSequence;
    private readonly Uri origin;
    public string SessionDirectory { get; }

    public DebugRequestCapture(WorkspacePaths paths, Uri? replayOrigin = null)
    {
        origin = replayOrigin ?? CurlReplay.OriginalServer;
        SessionDirectory = Path.Combine(paths.Local, "debug", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(SessionDirectory);
    }

    public async Task<RequestCaptureResult?> RecordRequestAsync(EchoRequest request, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        var id = Interlocked.Increment(ref requestSequence).ToString("D6");
        var directory = Path.Combine(SessionDirectory, id);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "body.bin"), body.ToArray(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "request.json"), JsonOutput.Serialize(request), Encoding.UTF8, token);
        string? command = null;
        string? exportError = null;
        try
        {
            await File.WriteAllLinesAsync(Path.Combine(directory, "headers.txt"), CurlReplay.Headers(request), new UTF8Encoding(false), token);
            command = CurlReplay.CreateCommand(request, body.Span, directory, origin);
        }
        catch (ArgumentException error)
        {
            exportError = error.Message;
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "capture.json"), JsonOutput.Serialize(new
        {
            RequestId = id,
            OriginalServer = origin.GetLeftPart(UriPartial.Authority),
            OriginalCertificateSha256 = GameBuild.OldPin,
            Note = "Development replay uses --insecure: curl does not verify the remote certificate. The original fingerprint is reference metadata only.",
            CurlCommand = command,
            CurlExportError = exportError,
            ReplayAutomatic = false,
            BodyFormat = "Exact decoded HTTP entity bytes; not raw TLS/wire framing."
        }), token);
        // Capture owns files/data; the journal owns the single complete console event.
        return new RequestCaptureResult(id, directory, command, exportError);
    }

    public async Task RecordWebSocketAsync(string? requestId, WebSocketMessageType type, bool endOfMessage,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (requestId is null) return;
        var directory = Path.Combine(SessionDirectory, requestId, "websocket");
        Directory.CreateDirectory(directory);
        var id = Interlocked.Increment(ref chunkSequence).ToString("D6");
        await File.WriteAllBytesAsync(Path.Combine(directory, id + ".bin"), payload.ToArray(), token);
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonOutput.Serialize(new
        {
            Time = DateTimeOffset.UtcNow,
            MessageType = type.ToString(),
            EndOfMessage = endOfMessage,
            PayloadLength = payload.Length,
            PayloadBase64 = Convert.ToBase64String(payload.Span),
            PayloadText = type == WebSocketMessageType.Text ? Encoding.UTF8.GetString(payload.Span) : null,
            Note = "Incoming application data chunk. Control frames, masking and TCP/TLS framing are handled by .NET."
        }), token);
    }
}
