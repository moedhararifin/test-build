using System.Text.Json;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class UserLoginEndpoint(LocalAccountStore accounts, TimeProvider? clock = null)
{
    public const string Path = "/api/v1/user/login";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public ServerRoute Route => new("POST", Path, HandleAsync);

    private Task ErrorAsync(HttpContext context, int status, string reason, string message) =>
        JsonResponse.WriteAsync(context, status, new { Error = message, Reason = reason, Now = clock.GetUtcNow().ToUnixTimeSeconds() });

    private async Task HandleAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Body.Length > 16 * 1024)
        {
            await ErrorAsync(request.Context, 413, "bad_request", "Login request is too large.");
            return;
        }
        string? name;
        string? password;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("password", out var passwordValue) || passwordValue.ValueKind != JsonValueKind.String)
                throw new JsonException();
            name = nameValue.GetString();
            password = passwordValue.GetString();
        }
        catch (JsonException)
        {
            await ErrorAsync(request.Context, 400, "bad_request", "Expected name and password strings.");
            return;
        }
        if (!LocalAccountStore.ValidName(name))
        {
            await ErrorAsync(request.Context, 400, "name", "Enter a local username of 1 to 64 characters.");
            return;
        }
        if (string.IsNullOrEmpty(password) || password.Length > 1024)
        {
            await ErrorAsync(request.Context, 400, "password", "Enter a password of 1 to 1024 characters.");
            return;
        }
        var login = accounts.Login(name!, password);
        if (login is null)
        {
            await ErrorAsync(request.Context, 401, "wrong", "wrong name or password");
            return;
        }
        await JsonResponse.WriteAsync(request.Context, 200, login);
    }
}
