using System.Net.WebSockets;
using Dollhouse.Server.Echo;

namespace Dollhouse.Server.Debugging;

public sealed record RequestCaptureResult(string RequestId, string Directory, string? CurlCommand, string? ExportError);

public interface IRequestCapture
{
    Task<RequestCaptureResult?> RecordRequestAsync(EchoRequest request, ReadOnlyMemory<byte> body, CancellationToken token);
    Task RecordWebSocketAsync(string? requestId, WebSocketMessageType type, bool endOfMessage,
        ReadOnlyMemory<byte> payload, CancellationToken token);
}

public sealed class NoRequestCapture : IRequestCapture
{
    public Task<RequestCaptureResult?> RecordRequestAsync(EchoRequest request, ReadOnlyMemory<byte> body, CancellationToken token) =>
        Task.FromResult<RequestCaptureResult?>(null);

    public Task RecordWebSocketAsync(string? requestId, WebSocketMessageType type, bool endOfMessage,
        ReadOnlyMemory<byte> payload, CancellationToken token) => Task.CompletedTask;
}
