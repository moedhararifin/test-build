using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Dollhouse.Server.Requests;

namespace Dollhouse.Tests;

internal static class JsonResponseTests
{
    public static async Task VerifyAsync()
    {
        var payload = new
        {
            EncodedMaterial = Convert.ToBase64String(Enumerable.Range(0, 256).Select(value => (byte)value).ToArray()),
            Message = "中文 café + / = & < > '\" \\ \n\t",
            Now = 1_800_000_000L,
            Enabled = true,
            Missing = (string?)null
        };
        byte[]? normalBytes = null;
        foreach (var method in new[] { "GET", "HEAD" })
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Response.Body = new MemoryStream();
            await JsonResponse.WriteAsync(context, 200, payload);
            Check.Equal("application/json; charset=utf-8", context.Response.ContentType);
            Check.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            if (method == "HEAD")
            {
                Check.Equal(0L, context.Response.Body.Length);
                Check.Equal((long)normalBytes!.Length, context.Response.ContentLength);
                continue;
            }
            normalBytes = ((MemoryStream)context.Response.Body).ToArray();
            Check.Equal((long)normalBytes.Length, context.Response.ContentLength);
            var wire = Encoding.UTF8.GetString(normalBytes);
            Check.Equal(payload.EncodedMaterial, NativeJsonStringReader.Read(wire, "encoded_material"));
            Check.Equal(payload.Message, NativeJsonStringReader.Read(wire, "message"));
            using var json = JsonDocument.Parse(normalBytes);
            Check.Equal(payload.Message, json.RootElement.GetProperty("message").GetString());
            Check.Equal(payload.Now, json.RootElement.GetProperty("now").GetInt64());
            Check.True(json.RootElement.GetProperty("enabled").GetBoolean(), "Boolean response type changed.");
            Check.Equal(JsonValueKind.Null, json.RootElement.GetProperty("missing").ValueKind);
        }
    }
}
