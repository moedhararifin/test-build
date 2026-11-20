using Dollhouse.Server.Echo;
using Dollhouse.Server.Modules;

namespace Dollhouse.Server.Requests;

/// <summary>One bounded body read shared by logging, handlers, and the echo fallback.</summary>
public sealed class ServerRequest(HttpContext context, EchoRequest message, ReadOnlyMemory<byte> body)
{
    public HttpContext Context { get; } = context;
    public EchoRequest Message { get; } = message;
    public ReadOnlyMemory<byte> Body { get; } = body;
    public string? CaptureId { get; internal set; }
    internal ServerRoute? SelectedRoute { get; set; }
}
