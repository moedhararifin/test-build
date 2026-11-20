using System.Text.Json;
using Dollhouse.Server.Accounts;
using Dollhouse.Server.Modules;
using Dollhouse.Server.Requests;

namespace Dollhouse.Server.Endpoints;

public sealed class ShopEndpoint(LocalAccountStore accounts, TimeProvider? clock = null)
{
    public const string PricesPath = "/api/v1/shop/prices";
    public const string OpenPath = "/api/v1/shop/open";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public ServerRoute PricesRoute => new("POST", PricesPath, PricesAsync);
    public ServerRoute OpenRoute => new("POST", OpenPath, OpenAsync);

    private async Task PricesAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!await ValidateBodyAsync(request)) return;
        // Both maps must be objects, even when there are no local offers.
        await JsonResponse.WriteAsync(request.Context, 200, new
        {
            Prices = new Dictionary<string, decimal>(), Base = new Dictionary<string, decimal>(), Campaign = (object?)null
        });
    }

    private async Task OpenAsync(ServerRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LocalSession.Resolve(request.Context.Request.Headers, accounts) is null)
        {
            await ErrorAsync(request.Context, 401, "no_login", "Local login required.");
            return;
        }
        if (!await ValidateBodyAsync(request)) return;
        // This is an existing-order lookup, not a request to create an order.
        // No cashier URL, wallet, QR code, or payment state exists locally.
        await JsonResponse.WriteAsync(request.Context, 200, new { Order = (object?)null, Now = Now });
    }

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    private Task ErrorAsync(HttpContext context, int status, string reason, string message) =>
        JsonResponse.WriteAsync(context, status, new { Error = message, Reason = reason, Now });

    private async Task<bool> ValidateBodyAsync(ServerRequest request)
    {
        if (request.Body.Length > 4096)
        {
            await ErrorAsync(request.Context, 413, "bad_request", "Shop request is too large.");
            return false;
        }
        try
        {
            using var json = JsonDocument.Parse(request.Body);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            return true;
        }
        catch (JsonException)
        {
            await ErrorAsync(request.Context, 400, "bad_request", "Expected a JSON object.");
            return false;
        }
    }
}
