using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Dollhouse.Core;
using Dollhouse.Server.Debugging;
using Dollhouse.Server.Echo;
using Dollhouse.Server.Endpoints;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class UpdateEndpointTests
{
    public static async Task VerifyAsync()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var journal = new Journal();
        var pipeline = new ServerPipeline(journal, new NoRequestCapture(),
            [new NoticeEndpoint().Route, new UserHintEndpoint(clock).Route]);
        using var services = new ServiceCollection().AddSingleton(pipeline).BuildServiceProvider();

        using var notice = await SendAsync(services, "GET", NoticeEndpoint.Path);
        var outer = notice.RootElement;
        Check.Equal(JsonValueKind.String, outer.GetProperty("news").ValueKind);
        Check.Equal(0L, outer.GetProperty("startTime").GetInt64());
        Check.Equal(0L, outer.GetProperty("endTime").GetInt64());
        Check.True(!outer.TryGetProperty("start_time", out _), "Notice casing changed to snake_case.");
        using var news = JsonDocument.Parse(outer.GetProperty("news").GetString()!);
        Check.Equal(1, news.RootElement.GetArrayLength());
        var translations = news.RootElement[0].EnumerateArray().ToArray();
        Check.Equal(1, translations.Length);
        string[] expectedLanguages = ["EN"];
        Check.True(expectedLanguages.SequenceEqual(translations.Select(entry => entry.GetProperty("language").GetString())), "Notice languages differ from the observed contract.");
        foreach (var entry in translations)
        {
            Check.True(entry.GetProperty("id").GetInt32() > 0, "Notice ID must be stable and positive.");
            Check.Equal("Dollhouse", entry.GetProperty("title").GetString());
            Check.True(entry.GetProperty("body").GetString()!.Contains("Dollhouse"), "Missing emulator announcement.");
            Check.Equal("", entry.GetProperty("versionNumber").GetString());
            Check.True(!entry.TryGetProperty("version_number", out _), "Nested notice casing changed.");
        }
        Check.Equal("Welcome to Dollhouse, an offline patch and server replacement for Fallen Doll. Enjoy playing offline on your own computer.\n\n" +
            "That is not dead which can eternal lie,\nAnd with strange aeons even death may die.\n— H. P. Lovecraft",
            translations[0].GetProperty("body").GetString());

        using var hint = await SendAsync(services, "GET", UserHintEndpoint.Path);
        Check.Equal(1337, hint.RootElement.GetProperty("users").GetInt32());
        Check.Equal(clock.UtcNow.ToUnixTimeSeconds(), hint.RootElement.GetProperty("now").GetInt64());
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        using var refreshed = await SendAsync(services, "GET", UserHintEndpoint.Path);
        Check.Equal(1337, refreshed.RootElement.GetProperty("users").GetInt32());
        Check.Equal(clock.UtcNow.ToUnixTimeSeconds(), refreshed.RootElement.GetProperty("now").GetInt64());
        using var otherMethod = await SendAsync(services, "POST", NoticeEndpoint.Path);
        Check.Equal("POST", otherMethod.RootElement.GetProperty("method").GetString());
        Check.Equal(4, journal.Count);

        // Raw user samples are optional and ignored; compare only public shape/casing.
        using var noticeSample = ReadSampleIfPresent("api-v1-notice.txt");
        if (noticeSample is not null)
        {
            SameFields(noticeSample.RootElement, outer);
            using var sampleNews = JsonDocument.Parse(noticeSample.RootElement.GetProperty("news").GetString()!);
            SameFields(sampleNews.RootElement[0][0], translations[0]);
        }
        using var hintSample = ReadSampleIfPresent("api-v1-user-hint.txt");
        if (hintSample is not null) SameFields(hintSample.RootElement, hint.RootElement);
    }

    private static void SameFields(JsonElement expected, JsonElement actual) => Check.True(
        expected.EnumerateObject().Select(field => (field.Name, field.Value.ValueKind)).OrderBy(field => field.Name)
            .SequenceEqual(actual.EnumerateObject().Select(field => (field.Name, field.Value.ValueKind)).OrderBy(field => field.Name)),
        "Response schema differs from the supplied sample.");

    private static JsonDocument? ReadSampleIfPresent(string name)
    {
        var path = Path.Combine(WorkspacePaths.Discover().Root, "docs", "samples", name);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var status = text.IndexOf("HTTP/1.1 200 OK", StringComparison.Ordinal);
        var body = text.IndexOf("\n\n", status, StringComparison.Ordinal);
        return JsonDocument.Parse(text[(body + 2)..]);
    }

    private static async Task<JsonDocument> SendAsync(IServiceProvider services, string method, string path)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString("?sample=1");
        context.Request.Protocol = "HTTP/1.1";
        context.Request.Body = new MemoryStream();
        context.Response.Body = new MemoryStream();
        await RequestDispatcher.HandleAsync(context);
        Check.Equal(200, context.Response.StatusCode);
        return JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class Journal : IRequestJournal
    {
        public int Count { get; private set; }
        public void Write(RequestMetadata metadata) => Count++;
    }
}
