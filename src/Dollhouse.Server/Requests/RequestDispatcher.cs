using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Dollhouse.Server.Echo;

namespace Dollhouse.Server.Requests;

public static class RequestDispatcher
{
    public const int MaximumBodySize = 8 * 1024 * 1024;

    public static async Task HandleAsync(HttpContext context)
    {
        try
        {
            // Kestrel normalizes CL+TE by moving CL to X-Content-Length.
            // Preserve the diagnostic server's stricter rejection policy.
            if (context.Request.Headers.ContainsKey("Transfer-Encoding")
                && (context.Request.Headers.ContainsKey("Content-Length")
                    || context.Request.Headers.ContainsKey("X-Content-Length")))
            {
                context.Response.Headers.Connection = "close";
                await ErrorAsync(context, 400, "Ambiguous request framing.");
                return;
            }
            if (context.Request.Headers.Upgrade.ToString().Equals("websocket", StringComparison.OrdinalIgnoreCase)
                && !context.WebSockets.IsWebSocketRequest)
            {
                await ErrorAsync(context, 400, "Invalid WebSocket upgrade.");
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (body.Length + count > MaximumBodySize)
                {
                    await ErrorAsync(context, 413, "Request body exceeds 8 MiB.");
                    return;
                }
                body.Write(buffer, 0, count);
            }
            var bytes = body.GetBuffer().AsMemory(0, checked((int)body.Length));
            var headers = context.Request.Headers.SelectMany(pair => pair.Value.Select(value => new[] { pair.Key, value ?? "" })).ToArray();
            var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            if (string.IsNullOrEmpty(target)) target = context.Request.Path + context.Request.QueryString;
            var metadata = new RequestMetadata(DateTimeOffset.UtcNow, context.Request.Method, target, headers, bytes.Length);
            var echo = new EchoRequest(metadata.Time, metadata.Method, target, context.Request.Protocol, headers, bytes);
            var request = new ServerRequest(context, echo, bytes);
            await context.RequestServices.GetRequiredService<ServerPipeline>().HandleAsync(request, timeout.Token);
        }
        catch (BadHttpRequestException error)
        {
            if (!context.Response.HasStarted)
                await ErrorAsync(context, error.StatusCode, "Invalid request or body limit exceeded.");
        }
        catch (OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
                await ErrorAsync(context, 408, "Request timed out.");
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static Task ErrorAsync(HttpContext context, int status, string message) =>
        JsonResponse.WriteAsync(context, status, new { Error = message });
}
