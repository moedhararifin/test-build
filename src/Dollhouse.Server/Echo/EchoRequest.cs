namespace Dollhouse.Server.Echo;

public enum RequestHandling { Unknown, Api, Echo }

public sealed record EchoRequest(DateTimeOffset Time, string Method, string Target, string HttpVersion,
    string[][] Headers, int BodyLength, string BodyBase64, string BodyText)
{
    private readonly ReadOnlyMemory<byte> body;
    private string? encoded = BodyBase64, decoded = BodyText;
    public string BodyBase64 { get => encoded ??= Convert.ToBase64String(body.Span); init => encoded = value; }
    public string BodyText { get => decoded ??= System.Text.Encoding.UTF8.GetString(body.Span); init => decoded = value; }

    // Logging/API handlers need metadata and raw bytes, not two extra text copies.
    // Echo serialization and opt-in debug capture materialize them on demand.
    internal EchoRequest(DateTimeOffset time, string method, string target, string httpVersion,
        string[][] headers, ReadOnlyMemory<byte> body)
        : this(time, method, target, httpVersion, headers, body.Length, "", "")
    { this.body = body; encoded = null; decoded = null; }
}

public sealed record RequestMetadata(DateTimeOffset Time, string Method, string Target,
    string[][] Headers, int BodyLength, RequestHandling Handling = RequestHandling.Unknown);
