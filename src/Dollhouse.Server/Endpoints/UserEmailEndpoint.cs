using System.Text.Json;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class UserEmailEndpoint(LocalAccountStore accounts, TimeProvider? clock = null)
{
    public const string Path = "/api/v1/user/email";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public ServerRoute GetRoute => new("GET", Path, GetAsync);
    public ServerRoute PostRoute => new("POST", Path, PostAsync);

    private Task ErrorAsync(HttpContext context, int status, string reason, string message) =>
        JsonResponse.WriteAsync(context, status, new { Error = message, Reason = reason, Now = clock.GetUtcNow().ToUnixTimeSeconds() });

    private static Task StateAsync(HttpContext context, string email) =>
        JsonResponse.WriteAsync(context, StatusCodes.Status200OK, new
        {
            Status = email.Length == 0 ? 0 : 2, Email = email, Expired = false
        });

    private Task GetAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        var email = login is null ? null : accounts.GetEmail(login.Session);
        return email is null
            ? ErrorAsync(request.Context, 401, "no_login", "Local login required.")
            : StateAsync(request.Context, email);
    }

    private async Task PostAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var login = LocalSession.Resolve(request.Context.Request.Headers, accounts);
        if (login is null)
        {
            await ErrorAsync(request.Context, 401, "no_login", "Local login required.");
            return;
        }
        if (request.Body.Length > 4096)
        {
            await ErrorAsync(request.Context, 413, "bad_request", "Email request is too large.");
            return;
        }
        string email;
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("email", out var value) || value.ValueKind != JsonValueKind.String
                || root.EnumerateObject().Count(field => field.Name == "email") != 1
                || (root.TryGetProperty("lang", out var language) && language.ValueKind != JsonValueKind.String))
                throw new JsonException();
            email = value.GetString()!.Trim();
        }
        catch (JsonException)
        {
            await ErrorAsync(request.Context, 400, "bad_request", "Expected an email string and optional lang string.");
            return;
        }
        if (!LocalAccountStore.ValidEmail(email))
        {
            await ErrorAsync(request.Context, 400, "email", "Enter a valid email address.");
            return;
        }
        // This is an account-local association. There is no SMTP, verification
        // link, pending timeout, ownership claim, or remote password recovery.
        if (!accounts.SetEmail(login.Session, email))
        {
            await ErrorAsync(request.Context, 401, "no_login", "Local login required.");
            return;
        }
        await StateAsync(request.Context, email);
    }
}
