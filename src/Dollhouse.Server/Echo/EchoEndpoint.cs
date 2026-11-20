using System.Net.WebSockets;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Echo;

public static class EchoEndpoint
{
    public const int MaximumBodySize = RequestDispatcher.MaximumBodySize;

    public static async Task HandleAsync(ServerRequest request, IRequestJournal journal, IRequestCapture capture)
    {
        var context = request.Context;
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var message = request.Message;
            var metadata = new RequestMetadata(message.Time, message.Method, message.Target, message.Headers, message.BodyLength,
                RequestHandling.Echo);
            await EchoWebSocketAsync(context, socket, metadata, journal, capture, request.CaptureId);
            return;
        }
        await JsonResponse.WriteAsync(context, 200, request.Message);
    }

    private static async Task EchoWebSocketAsync(HttpContext context, WebSocket socket,
        RequestMetadata request, IRequestJournal journal, IRequestCapture capture, string? requestId)
    {
        var buffer = new byte[16 * 1024];
        var messageSize = 0;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        socket.CloseStatusDescription, timeout.Token);
                    return;
                }
                await capture.RecordWebSocketAsync(requestId, result.MessageType, result.EndOfMessage,
                    buffer.AsMemory(0, result.Count), timeout.Token);
                messageSize += result.Count;
                if (messageSize > MaximumBodySize)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message exceeds 8 MiB.", timeout.Token);
                    return;
                }
                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, timeout.Token);
                journal.Write(request with { Time = DateTimeOffset.UtcNow, Method = "WS", Headers = [], BodyLength = result.Count });
                if (result.EndOfMessage) messageSize = 0;
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException)
        {
            socket.Abort();
        }
    }

}
