using System.Text.Json.Serialization;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class NoticeEndpoint
{
    public const string Path = "/api/v1/notice";

    private sealed record Translation(int Id, string Language, string Title, string Body,
        [property: JsonPropertyName("versionNumber")] string VersionNumber = "");

    private sealed record Response(string News,
        [property: JsonPropertyName("startTime")] long StartTime = 0,
        [property: JsonPropertyName("endTime")] long EndTime = 0);

    private static readonly Response Payload = new(JsonResponse.Serialize(new Translation[][]
    {
        [
            new(1337, "EN", "Dollhouse",
                "Welcome to Dollhouse, an offline patch and server replacement for Fallen Doll. Enjoy playing offline on your own computer.\n\n" +
                "That is not dead which can eternal lie,\nAnd with strange aeons even death may die.\n— H. P. Lovecraft")
        ]
    }));

    public ServerRoute Route => new("GET", Path, (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return JsonResponse.WriteAsync(request.Context, StatusCodes.Status200OK, Payload);
    });
}
