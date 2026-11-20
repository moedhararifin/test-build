using System.Text.Json;
using System.Text.Encodings.Web;
using Dollhouse.Core;

namespace Dollhouse.Server.Requests;

public static class JsonResponse
{
    // All protocol JSON uses this policy. The game's native string reader does not
    // decode Unicode escapes, so base64 '+' must remain literal. This output is
    // application/json, never HTML or inline JavaScript. Log/file JSON is separate.
    private static readonly JsonSerializerOptions Options = new(JsonOutput.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static async Task WriteAsync<T>(HttpContext context, int status, T payload)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = data.Length;
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(data, context.RequestAborted);
    }
}
